using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Neuterradise.App.Media.Model;

public sealed partial class GlbThumbnailPreviewAdapter
{
    public static async Task BuildModelRenderAsync(string canonicalPath, string sourceSha256,
        string outputPath, CancellationToken ct = default, string? inputSha256 = null)
    {
        try
        {
            var (data, document, binary) = await ReadSceneAsync(canonicalPath, ct).ConfigureAwait(false);
            using var ownedDocument = document;
            if (Convert.ToHexStringLower(SHA256.HashData(data)) != (inputSha256 ?? sourceSha256))
                throw new InvalidDataException("Canonical GLB source hash changed during preparation.");
            using var scene = new Scene(document.RootElement, binary);
            var (header, payloads) = scene.Prepare(sourceSha256, ct);
            await ModelRenderContainer.WriteAsync(outputPath, header, payloads, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
            or IndexOutOfRangeException or OverflowException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("GLB scene is malformed or exceeds the preparation budget.", ex);
        }
    }

    private sealed partial class Scene
    {
        private readonly List<ReadOnlyMemory<byte>> _payloads = [];
        private readonly List<ModelRenderChunk> _chunks = [];
        private readonly List<ModelRenderTexture> _preparedTextures = [];
        private readonly Dictionary<(int, bool, bool, int, int, int, int), int> _textureIds = [];
        private long _preparedBytes, _preparedTextureBytes, _preparedVertices, _preparedIndices;

        public (ModelRenderHeader, IReadOnlyList<ReadOnlyMemory<byte>>) Prepare(string hash, CancellationToken ct)
        {
            if (_root.TryGetProperty("extensionsRequired", out var required) && required.GetArrayLength() != 0)
                throw new NotSupportedException("GLB required extensions are not supported by the core renderer.");
            if (_root.TryGetProperty("skins", out var skins) && skins.GetArrayLength() != 0)
                throw new NotSupportedException("Skinned GLB scenes require a deformation authority.");
            _exporting = true;
            var sceneIndex = Int(_root, "scene");
            foreach (var node in _root.GetProperty("scenes")[sceneIndex].GetProperty("nodes").EnumerateArray())
                AddNode(node.GetInt32(), Matrix4x4.Identity, [], ct);
            if (_primitives.Count == 0 || _primitives.Count > 256)
                throw new InvalidDataException("GLB scene exceeds the primitive budget.");
            var materials = new List<ModelRenderMaterial>();
            var meshes = new List<ModelRenderMesh>();
            foreach (var primitive in _primitives)
            {
                ct.ThrowIfCancellationRequested();
                if (primitive.Source.TryGetProperty("targets", out _))
                    throw new NotSupportedException("Morph-target GLB geometry is not supported.");
                var material = Material(primitive.Source, ct);
                if (primitive.Coordinates is null && new[] {material.BaseColorTexture, material.MetallicRoughnessTexture, material.NormalTexture, material.OcclusionTexture, material.EmissiveTexture}.Any(t => t.HasValue))
                    throw new InvalidDataException("Textured GLB primitive has no TEXCOORD_0.");
                var materialId = materials.Count;
                materials.Add(material);
                var (vertices, indices) = Geometry(primitive, ct);
                AddMesh(vertices, indices, materialId, 0);
                if (indices.Length / 3 > 500)
                {
                    var target = Math.Min(350_000, indices.Length / 6);
                    var reduced = Simplify(vertices, indices, target, ct);
                    AddMesh(reduced.Vertices, reduced.Indices, materialId, 1);
                    var small = Simplify(vertices, indices, Math.Min(100_000, indices.Length / 12), ct);
                    AddMesh(small.Vertices, small.Indices, materialId, 2);
                }
            }
            var extent = _maximum - _minimum;
            var scale = 1 / Math.Max(0.000001f, Math.Max(extent.X, Math.Max(extent.Y, extent.Z)));
            var center = (_maximum + _minimum) * 0.5f;
            return (new ModelRenderHeader(1, 1, hash, [_minimum.X, _minimum.Y, _minimum.Z],
                [_maximum.X, _maximum.Y, _maximum.Z], [-center.X, -_minimum.Y, -center.Z], scale,
                meshes.ToArray(), materials.ToArray(), _preparedTextures.ToArray(), _chunks.ToArray()), _payloads);

            void AddMesh(Vertex[] vertices, int[] indices, int material, int lod)
            {
                _preparedVertices = checked(_preparedVertices + vertices.Length);
                _preparedIndices = checked(_preparedIndices + indices.Length);
                var geometryBytes = checked((long)vertices.Length * ModelRenderContainer.VertexStride + (long)indices.Length * 4);
                if (_preparedVertices > 8_000_000 || _preparedIndices > 24_000_000
                    || geometryBytes > ModelRenderContainer.MaxBytes - ModelRenderContainer.MaxHeaderBytes - _preparedBytes)
                    throw new InvalidDataException("GLB prepared geometry exceeds its allocation budget.");
                var bytes = new byte[checked(vertices.Length * ModelRenderContainer.VertexStride)];
                for (var i = 0; i < vertices.Length; i++)
                {
                    if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                    var v = vertices[i]; var span = bytes.AsSpan(i * 48, 48);
                    Write(span,0,v.P.X); Write(span,4,v.P.Y); Write(span,8,v.P.Z);
                    Write(span,12,v.N.X); Write(span,16,v.N.Y); Write(span,20,v.N.Z);
                    Write(span,24,v.U.X); Write(span,28,v.U.Y);
                    Write(span,32,v.T.X); Write(span,36,v.T.Y); Write(span,40,v.T.Z); Write(span,44,v.T.W);
                }
                var indexBytes = new byte[checked(indices.Length * 4)];
                for (var i = 0; i < indices.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(indexBytes.AsSpan(i * 4), indices[i]);
                meshes.Add(new(vertices.Length, indices.Length, Chunk("VERTICES", bytes), Chunk("INDICES", indexBytes), material, lod));
            }
        }

        private static void Write(Span<byte> bytes, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes[offset..],value);

        private int Chunk(string kind, byte[] bytes)
        {
            _preparedBytes = checked(_preparedBytes + bytes.Length);
            if (_preparedBytes > ModelRenderContainer.MaxBytes - ModelRenderContainer.MaxHeaderBytes)
                throw new InvalidDataException("GLB prepared payload exceeds the durable byte budget.");
            var id = _payloads.Count; _payloads.Add(bytes);
            _chunks.Add(new(kind, 0, bytes.Length, "")); return id;
        }

        private ModelRenderMaterial Material(JsonElement primitive, CancellationToken ct)
        {
            if (!primitive.TryGetProperty("material", out var id))
                return new([1, 1, 1, 1], 1, 1, [0, 0, 0]);
            var m = _root.GetProperty("materials")[id.GetInt32()];
            var pbr = m.TryGetProperty("pbrMetallicRoughness", out var p) ? p : default;
            var baseColor = pbr.ValueKind == JsonValueKind.Object ? Floats(pbr,"baseColorFactor",[1,1,1,1]) : [1f,1f,1f,1f];
            var emissive = Floats(m,"emissiveFactor",[0,0,0]);
            if (baseColor.Any(v => v is < 0 or > 1) || emissive.Any(v => v is < 0 or > 64))
                throw new InvalidDataException("GLB material color is outside its factor budget.");
            var metallic = Scalar(pbr,"metallicFactor",1); var roughness = Scalar(pbr,"roughnessFactor",1);
            var normalScale = m.TryGetProperty("normalTexture",out var n) ? Scalar(n,"scale",1) : 1;
            var strength = m.TryGetProperty("occlusionTexture",out var o) ? Scalar(o,"strength",1) : 1;
            var cutoff = Scalar(m,"alphaCutoff",0.5f);
            var alpha = m.TryGetProperty("alphaMode",out var alphaValue) ? alphaValue.GetString()! : "OPAQUE";
            if (alpha is not ("OPAQUE" or "MASK" or "BLEND")) throw new InvalidDataException("Invalid GLB alpha mode.");
            return new(baseColor, metallic, roughness, emissive,
                TextureReference(pbr, "baseColorTexture", true, false, ct), TextureReference(pbr, "metallicRoughnessTexture", false, false, ct),
                TextureReference(m, "normalTexture", false, true, ct), TextureReference(m, "occlusionTexture", false, false, ct),
                TextureReference(m, "emissiveTexture", true, false, ct), normalScale, strength, alpha, cutoff,
                m.TryGetProperty("doubleSided", out var two) && two.GetBoolean());
        }
        private static float Scalar(JsonElement e, string name, float fallback)
        {
            var value = e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name,out var v) ? v.GetSingle() : fallback;
            if (!float.IsFinite(value) || (name == "scale" ? Math.Abs(value) > 16 : value is < 0 or > 1))
                throw new InvalidDataException("GLB material factor is non-finite or outside its range.");
            return value;
        }
        private int? TextureReference(JsonElement material, string key, bool srgb, bool normal, CancellationToken ct)
        {
            if (material.ValueKind != JsonValueKind.Object || !material.TryGetProperty(key, out var info)) return null;
            if (Int(info, "texCoord") != 0) throw new NotSupportedException("Only TEXCOORD_0 material bindings are supported.");
            var texture = _root.GetProperty("textures")[Int(info, "index")];
            var image = texture.GetProperty("source").GetInt32();
            var sampler = texture.TryGetProperty("sampler",out var samplerId)
                ? _root.GetProperty("samplers")[samplerId.GetInt32()] : default;
            var wrapS = sampler.ValueKind == JsonValueKind.Object ? Int(sampler,"wrapS",10497) : 10497;
            var wrapT = sampler.ValueKind == JsonValueKind.Object ? Int(sampler,"wrapT",10497) : 10497;
            var minFilter = sampler.ValueKind == JsonValueKind.Object ? Int(sampler,"minFilter",9987) : 9987;
            var magFilter = sampler.ValueKind == JsonValueKind.Object ? Int(sampler,"magFilter",9729) : 9729;
            if (wrapS is not (33071 or 33648 or 10497) || wrapT is not (33071 or 33648 or 10497)
                || minFilter is not (9728 or 9729 or 9984 or 9985 or 9986 or 9987) || magFilter is not (9728 or 9729))
                throw new InvalidDataException("Invalid GLB sampler enumerant.");
            var cacheKey = (image, srgb, normal, wrapS, wrapT, minFilter, magFilter);
            if (_textureIds.TryGetValue(cacheKey, out var existing)) return existing;
            var bitmap = Texture(image);
            var width = bitmap.Width; var height = bitmap.Height;
            long mipBytes = 0;
            for (int w = width, h = height; ; w = Math.Max(1,w/2), h = Math.Max(1,h/2))
            {
                mipBytes += (long)w * h * 4;
                if (w == 1 && h == 1) break;
            }
            if (_preparedTextures.Count >= 128 || mipBytes > 512L*1024*1024 - _preparedTextureBytes
                || mipBytes > ModelRenderContainer.MaxBytes - ModelRenderContainer.MaxHeaderBytes - _preparedBytes)
                throw new InvalidDataException("GLB prepared texture allocation exceeds its budget.");
            _preparedTextureBytes += mipBytes;
            var pixels = new byte[checked(width * height * 4)];
            for (var y = 0; y < height; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                {
                    var color = bitmap.GetPixel(x,y); var at = (y * width + x) * 4;
                    pixels[at] = color.Red; pixels[at+1] = color.Green; pixels[at+2] = color.Blue; pixels[at+3] = color.Alpha;
                }
            }
            var mips = new List<int>();
            while (true)
            {
                mips.Add(Chunk("RGBA8", pixels));
                if (width == 1 && height == 1) break;
                var w = Math.Max(1, width / 2); var h = Math.Max(1, height / 2);
                var next = new byte[w * h * 4];
                for (var y = 0; y < h; y++)
                {
                    ct.ThrowIfCancellationRequested();
                    for (var x = 0; x < w; x++)
                    {
                        var sum = Vector4.Zero;
                        for (var dy = 0; dy < 2; dy++) for (var dx = 0; dx < 2; dx++)
                        {
                            var at = (Math.Min(height-1,y*2+dy)*width+Math.Min(width-1,x*2+dx))*4;
                            var value = new Vector4(pixels[at]/255f,pixels[at+1]/255f,pixels[at+2]/255f,pixels[at+3]/255f);
                            if (srgb) value = new(ToLinear(value.X),ToLinear(value.Y),ToLinear(value.Z),value.W);
                            sum += value;
                        }
                        sum *= 0.25f;
                        if (normal)
                        {
                            var direction = Unit(new(sum.X*2-1,sum.Y*2-1,sum.Z*2-1), Vector3.UnitZ);
                            sum = new(direction*0.5f+new Vector3(0.5f), sum.W);
                        }
                        if (srgb) sum = new(ToSrgb(sum.X),ToSrgb(sum.Y),ToSrgb(sum.Z),sum.W);
                        var target = (y*w+x)*4;
                        next[target] = Byte(sum.X); next[target+1] = Byte(sum.Y); next[target+2] = Byte(sum.Z); next[target+3] = Byte(sum.W);
                    }
                }
                pixels = next; width = w; height = h;
            }
            var result = _preparedTextures.Count;
            _preparedTextures.Add(new(bitmap.Width,bitmap.Height,srgb,mips.ToArray(),wrapS,wrapT,minFilter,magFilter)); _textureIds.Add(cacheKey,result); return result;
        }
        private static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Round(value*255),0,255);
        private static float ToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v+0.055f)/1.055f,2.4f);
        private static float ToSrgb(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f*MathF.Pow(v,1/2.4f)-0.055f;
        private static Vector3 Unit(Vector3 v, Vector3 fallback)
        {
            var squared = v.LengthSquared();
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z) || !float.IsFinite(squared))
                throw new InvalidDataException("GLB direction computation is non-finite.");
            return squared > 1e-20f ? v / MathF.Sqrt(squared) : fallback;
        }
        private readonly record struct Vertex(Vector3 P, Vector3 N, Vector2 U, Vector4 T);

        private (Vertex[], int[]) Geometry(Primitive p, CancellationToken ct)
        {
            var attributes = p.Source.GetProperty("attributes");
            var normal = attributes.TryGetProperty("NORMAL",out var n) ? Access(n.GetInt32(),"VEC3",5126) : null;
            var tangent = attributes.TryGetProperty("TANGENT",out var t) ? Access(t.GetInt32(),"VEC4",5126) : null;
            if ((normal is not null && normal.Count != p.Vertices.Length) || (tangent is not null && tangent.Count != p.Vertices.Length))
                throw new InvalidDataException("GLB direction attributes do not match positions.");
            var directionAccessors = new[] {normal,tangent};
            for (var i = 0; i < p.Vertices.Length; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                foreach (var accessor in directionAccessors)
                {
                    if (accessor is null) continue;
                    var squared = 0f;
                    for (var axis = 0; axis < 3; axis++)
                    {
                        var value = accessor.Float(i,axis);
                        if (!float.IsFinite(value) || Math.Abs(value) > 1.001f)
                            throw new InvalidDataException("GLB direction attribute is non-finite or outside its unit range.");
                        squared += value * value;
                    }
                    if (squared < 1e-20f) throw new InvalidDataException("GLB direction attribute is zero.");
                }
                if (tangent is not null && tangent.Float(i,3) is not (-1 or 1))
                    throw new InvalidDataException("GLB tangent handedness must be -1 or 1.");
            }
            if (!Matrix4x4.Invert(p.Transform,out var inverse)) throw new InvalidDataException("GLB node transform is singular.");
            var normalMatrix = Matrix4x4.Transpose(inverse);
            var mirrored = p.Transform.GetDeterminant() < 0;
            var vertices = new List<Vertex>(p.Vertices.Length);
            var tangentSums = new List<Vector3>(p.Vertices.Length);
            var bitangentSums = new List<Vector3>(p.Vertices.Length);
            var normalSums = new List<Vector3>(p.Vertices.Length);
            var map = new Dictionary<(int,bool,int),int>(p.Vertices.Length);
            var indices = new int[p.Count];
            Span<int> ids = stackalloc int[3];
            // Mirrored UV islands require separate frames even when a source shares position indices.
            for (var i = 0; i < p.Count; i += 3)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                var a = p.Index(i); var b = p.Index(i+(mirrored?2:1)); var c = p.Index(i+(mirrored?1:2));
                var e1 = p.Vertices[b]-p.Vertices[a]; var e2 = p.Vertices[c]-p.Vertices[a];
                var uv1 = p.Uv(b)-p.Uv(a); var uv2 = p.Uv(c)-p.Uv(a);
                var det = uv1.X*uv2.Y-uv1.Y*uv2.X;
                var s = Math.Abs(det)>1e-12f ? (e1*uv2.Y-e2*uv1.Y)/det : Vector3.Zero;
                var bit = Math.Abs(det)>1e-12f ? (e2*uv1.X-e1*uv2.X)/det : Vector3.Zero;
                var face = Vector3.Cross(e1,e2);
                ids[0] = a; ids[1] = b; ids[2] = c;
                for (var j = 0; j < 3; j++)
                {
                    var original = ids[j]; var key = (original,det<0,normal is null ? i : 0);
                    if (!map.TryGetValue(key,out var index))
                    {
                        index = vertices.Count;
                        if (index >= 4_000_000) throw new InvalidDataException("GLB tangent/flat-normal expansion exceeds the vertex budget.");
                        map.Add(key,index);
                        var direction = normal is null ? Vector3.Zero : Unit(Vector3.TransformNormal(new(normal.Float(original,0),normal.Float(original,1),normal.Float(original,2)),normalMatrix),Vector3.UnitY);
                        var frame = tangent is null ? Vector4.Zero : new Vector4(Unit(Vector3.TransformNormal(new(tangent.Float(original,0),tangent.Float(original,1),tangent.Float(original,2)),p.Transform),Vector3.UnitX),tangent.Float(original,3)*(mirrored?-1:1));
                        vertices.Add(new(p.Vertices[original],direction,p.Uv(original),frame));
                        tangentSums.Add(Vector3.Zero); bitangentSums.Add(Vector3.Zero); normalSums.Add(Vector3.Zero);
                    }
                    indices[i+j] = index;
                    var weight = CornerAngle(p.Vertices[ids[(j+1)%3]]-p.Vertices[original],p.Vertices[ids[(j+2)%3]]-p.Vertices[original]);
                    tangentSums[index] += Unit(s,Vector3.Zero)*weight;
                    bitangentSums[index] += Unit(bit,Vector3.Zero)*weight;
                    normalSums[index] += face;
                }
            }
            for (var i = 0; i < vertices.Count; i++)
            {
                var v = vertices[i]; var nn = normal is null ? Unit(normalSums[i],Vector3.UnitY) : v.N;
                var ss = tangent is null ? tangentSums[i] : new Vector3(v.T.X,v.T.Y,v.T.Z);
                ss = Unit(ss-nn*Vector3.Dot(nn,ss),Unit(Vector3.Cross(Math.Abs(nn.Y)<0.9f?Vector3.UnitY:Vector3.UnitX,nn),Vector3.UnitX));
                var sign = tangent is null ? (Vector3.Dot(Vector3.Cross(nn,ss),bitangentSums[i])<0?-1:1) : v.T.W;
                vertices[i] = v with {N=nn,T=new(ss,sign)};
            }
            return (vertices.ToArray(),indices);
        }
        private static float CornerAngle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Unit(a,Vector3.UnitX),Unit(b,Vector3.UnitX)),-1,1));

        private (Vertex[] Vertices,int[] Indices) Simplify(Vertex[] source, int[] indices, int target, CancellationToken ct)
        {
            var result = (Vertices: source, Indices: indices);
            var parent = Enumerable.Range(0, source.Length).ToArray();
            int Find(int value)
            {
                while (parent[value] != value) { parent[value] = parent[parent[value]]; value = parent[value]; }
                return value;
            }
            void Union(int left, int right)
            {
                left = Find(left); right = Find(right);
                if (left != right) parent[right] = left;
            }
            for (var i = 0; i < indices.Length; i += 3)
            {
                Union(indices[i], indices[i + 1]);
                Union(indices[i], indices[i + 2]);
            }

            var extent = _maximum - _minimum;
            var max = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
            for (var resolution = 256; resolution >= 8 && result.Indices.Length / 3 > target; resolution /= 2)
            {
                var groups = new Dictionary<(int,int,int,int,int,int,int,int,int,int,int,int,int,int),int>();
                var remap = new int[source.Length];
                var vertices = new List<Vertex>();
                var cell = max / resolution;
                for (var i = 0; i < source.Length; i++)
                {
                    if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                    var v = source[i];
                    var p = (v.P - _minimum) / Math.Max(cell, 1e-8f);
                    var key = (Find(i), (int)p.X, (int)p.Y, (int)p.Z,
                        (int)MathF.Round(v.U.X * 64), (int)MathF.Round(v.U.Y * 64),
                        (int)MathF.Round(v.N.X * 16), (int)MathF.Round(v.N.Y * 16), (int)MathF.Round(v.N.Z * 16),
                        (int)MathF.Round(v.T.X * 16), (int)MathF.Round(v.T.Y * 16), (int)MathF.Round(v.T.Z * 16),
                        (int)MathF.Round(v.T.W), 0);
                    if (!groups.TryGetValue(key, out var id)) { id = vertices.Count; groups.Add(key, id); vertices.Add(v); }
                    remap[i] = id;
                }

                var triangles = new List<int>(indices.Length);
                var seen = new HashSet<(int,int,int)>();
                var unsafeWinding = false;
                for (var i = 0; i < indices.Length; i += 3)
                {
                    if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                    var ia = indices[i]; var ib = indices[i + 1]; var ic = indices[i + 2];
                    var a = remap[ia]; var b = remap[ib]; var c = remap[ic];
                    if (a == b || b == c || c == a || !seen.Add((a, b, c))) continue;
                    var sourceNormal = Vector3.Cross(source[ib].P - source[ia].P, source[ic].P - source[ia].P);
                    var reducedNormal = Vector3.Cross(vertices[b].P - vertices[a].P, vertices[c].P - vertices[a].P);
                    if (reducedNormal.LengthSquared() < 1e-20f) continue;
                    if (sourceNormal.LengthSquared() >= 1e-20f && Vector3.Dot(sourceNormal, reducedNormal) <= 0)
                    {
                        unsafeWinding = true;
                        break;
                    }
                    triangles.Add(a); triangles.Add(b); triangles.Add(c);
                }
                if (unsafeWinding) break;
                if (triangles.Count >= 3) result = (vertices.ToArray(), triangles.ToArray());
            }
            return result;
        }
    }
}
