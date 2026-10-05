using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using SkiaSharp;

namespace Neuterradise.App.Media.Model;

/// <summary>Bounded static GLB scene rendering; output is temporary input to canonical MediaAsset publication.</summary>
public sealed partial class GlbThumbnailPreviewAdapter : IModelPreviewAdapter
{
    private const int MaxBytes = 256 * 1024 * 1024;
    private const int MaxVertices = 2_000_000;
    private const int MaxTriangles = 4_000_000;
    public string AdapterId => "builtin-glb-thumbnail";
    public string DisplayName => "GLB static thumbnail";
    public string Version => "1.0.0";
    public int Priority => 110;
    public ModelAdapterCapabilities Capabilities => ModelAdapterCapabilities.Probe | ModelAdapterCapabilities.StaticThumbnail;
    public bool CanHandle(ModelProbeInput input) => string.Equals(input.Extension ?? Path.GetExtension(input.FilePath), ".glb", StringComparison.OrdinalIgnoreCase);
    public async Task<ModelMetadata> ProbeAsync(ModelProbeInput input, CancellationToken cancellationToken = default) =>
        (await new GltfModelPreviewAdapter().ProbeAsync(input, cancellationToken).ConfigureAwait(false)) with { AdapterId = AdapterId, AdapterVersion = Version };
    public Task<IModelInteractiveSession?> CreateInteractiveSessionAsync(ModelInteractiveRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IModelInteractiveSession?>(null);

    public async Task<ModelPreviewDescriptor> GetOrGeneratePreviewAsync(ModelPreviewRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await RenderAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException("GLB scene data is malformed or exceeds the thumbnail budget.", exception);
        }
    }

    private async Task<ModelPreviewDescriptor> RenderAsync(ModelPreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        var (data, document, binary) = await ReadSceneAsync(request.FilePath, cancellationToken).ConfigureAwait(false);
        using var ownedDocument = document;
        using var scene = new Scene(document.RootElement, binary);
        var width = Math.Clamp(request.TargetWidth, 64, 1024);
        var height = Math.Clamp(request.TargetHeight, 64, 1024);
        using var bitmap = scene.Render(width, height, cancellationToken);
        Directory.CreateDirectory(request.OutputDirectory);
        var output = Path.Combine(request.OutputDirectory, $"{request.MediaId:N}-{Guid.NewGuid():N}.png");
        try
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            await using var file = File.Create(output);
            encoded.SaveTo(file);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            File.Delete(output);
            throw;
        }
        return new ModelPreviewDescriptor(request.MediaId, StaticThumbnailPath: output, Format: "GLB", AdapterId: AdapterId);
    }

    private static async Task<(byte[] Data, JsonDocument Document, ReadOnlyMemory<byte> Binary)> ReadSceneAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(filePath,FileMode.Open,FileAccess.Read,FileShare.Read,
            65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        var sourceLength = source.Length;
        if (sourceLength is < 20 or > MaxBytes) throw new InvalidDataException("GLB exceeds the input byte budget.");
        var data = new byte[checked((int)sourceLength)];
        try { await source.ReadExactlyAsync(data,cancellationToken).ConfigureAwait(false); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("GLB source changed or was truncated during preparation.",ex); }
        if (source.Length != sourceLength || await source.ReadAsync(new byte[1],cancellationToken).ConfigureAwait(false) != 0)
            throw new InvalidDataException("GLB source grew during preparation.");
        if (data.Length < 20 || data.Length > MaxBytes || UInt(data, 0) != 0x46546c67 || UInt(data, 4) != 2 || UInt(data, 8) != data.Length)
            throw new InvalidDataException("Invalid GLB 2.0 container.");
        ReadOnlyMemory<byte> json = default, binary = default;
        for (var offset = 12; offset < data.Length;)
        {
            if (data.Length - offset < 8) throw new InvalidDataException("Truncated GLB chunk.");
            var length = checked((int)UInt(data, offset));
            var kind = UInt(data, offset + 4); offset += 8;
            if (length < 0 || length > data.Length - offset || length % 4 != 0) throw new InvalidDataException("Invalid GLB chunk length.");
            if (kind == 0x4e4f534a)
            {
                if (!json.IsEmpty || offset != 20) throw new InvalidDataException("GLB JSON chunk must occur once and first.");
                json = data.AsMemory(offset, length);
            }
            if (kind == 0x004e4942)
            {
                if (!binary.IsEmpty || json.IsEmpty) throw new InvalidDataException("Invalid GLB binary chunk ordering.");
                binary = data.AsMemory(offset, length);
            }
            offset += length;
        }
        if (json.IsEmpty || json.Length > 16 * 1024 * 1024 || binary.IsEmpty) throw new InvalidDataException("GLB has no embedded scene geometry.");

        var document = JsonDocument.Parse(json);
        try
        {
            var buffers = document.RootElement.GetProperty("buffers");
            if (buffers.GetArrayLength() != 1 || buffers[0].TryGetProperty("uri", out _)
                || Int(buffers[0], "byteLength") <= 0 || Int(buffers[0], "byteLength") > binary.Length
                || binary.Length - Int(buffers[0], "byteLength") > 3)
                throw new NotSupportedException("GLB preparation requires one embedded buffer.");
            return (data, document, binary);
        }
        catch { document.Dispose(); throw; }
    }

    private static uint UInt(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    private static int Int(JsonElement value, string key, int fallback = 0) => value.TryGetProperty(key, out var property) ? property.GetInt32() : fallback;
    private static float[] Floats(JsonElement value, string key, float[] fallback)
    {
        if (!value.TryGetProperty(key, out var property)) return fallback;
        if (property.GetArrayLength() != fallback.Length) throw new InvalidDataException("GLB numeric vector shape is invalid.");
        var result = property.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        if (result.Any(v => !float.IsFinite(v) || Math.Abs(v) > 1_000_000)) throw new InvalidDataException("GLB numeric vector exceeds its range.");
        return result;
    }

    private sealed partial class Scene : IDisposable
    {
        private readonly JsonElement _root;
        private readonly ReadOnlyMemory<byte> _binary;
        private readonly List<Primitive> _primitives = [];
        private readonly Dictionary<int, SKBitmap> _textures = [];
        private Vector3 _minimum = new(float.PositiveInfinity), _maximum = new(float.NegativeInfinity);
        private int _vertices, _triangles, _instances;
        private bool _exporting;
        private long _texturePixels;
        private readonly Matrix4x4 _camera = Matrix4x4.CreateRotationY(-0.25f) * Matrix4x4.CreateRotationX(0.04f);
        public Scene(JsonElement root, ReadOnlyMemory<byte> binary)
        {
            _root = root; _binary = binary;
            foreach (var (name, limit) in new[] { ("scenes", 64), ("nodes", 4096), ("meshes", 1024), ("accessors", 8192),
                ("bufferViews", 8192), ("materials", 256), ("textures", 128), ("images", 128), ("samplers", 128), ("animations", 128) })
                if (root.TryGetProperty(name, out var array) && array.GetArrayLength() > limit)
                    throw new InvalidDataException($"GLB {name} exceeds the descriptor budget.");
        }

        public SKBitmap Render(int width, int height, CancellationToken ct)
        {
            var sceneIndex = Int(_root, "scene");
            if (!_root.TryGetProperty("scenes", out var scenes) || sceneIndex < 0 || sceneIndex >= scenes.GetArrayLength())
                throw new InvalidDataException("GLB has no renderable scene.");
            foreach (var node in scenes[sceneIndex].GetProperty("nodes").EnumerateArray())
                AddNode(node.GetInt32(), Matrix4x4.Identity, new HashSet<int>(), ct);
            if (_primitives.Count == 0) throw new InvalidDataException("GLB contains no supported triangle geometry.");
            var extent = _maximum - _minimum;
            var scale = 0.86f * Math.Min(width / Math.Max(0.00001f, extent.X), height / Math.Max(0.00001f, extent.Y));
            var center = (_maximum + _minimum) * 0.5f;
            var depths = new float[width * height]; Array.Fill(depths, float.NegativeInfinity);
            var pixels = new SKColor[width * height]; Array.Fill(pixels, new SKColor(232, 237, 242));
            var visible = 0;
            foreach (var primitive in _primitives)
            {
                for (var index = 0; index < primitive.Count; index += 3)
                {
                    if ((index & 4095) == 0) ct.ThrowIfCancellationRequested();
                    var ai = primitive.Index(index); var bi = primitive.Index(index + 1); var ci = primitive.Index(index + 2);
                    var a = Project(primitive.Vertices[ai]); var b = Project(primitive.Vertices[bi]); var c = Project(primitive.Vertices[ci]);
                    var area = Edge(a, b, c.X, c.Y);
                    if (Math.Abs(area) < 0.00001f) continue;
                    var left = Math.Clamp((int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))), 0, width - 1);
                    var right = Math.Clamp((int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))), 0, width - 1);
                    var top = Math.Clamp((int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))), 0, height - 1);
                    var bottom = Math.Clamp((int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))), 0, height - 1);
                    var normal = Vector3.Cross(primitive.Vertices[bi] - primitive.Vertices[ai], primitive.Vertices[ci] - primitive.Vertices[ai]);
                    var shade = normal.LengthSquared() > 0 ? 0.72f + 0.28f * Math.Abs(Vector3.Normalize(normal).Z) : 1;
                    var uvA = primitive.Uv(ai); var uvB = primitive.Uv(bi); var uvC = primitive.Uv(ci);
                    for (var y = top; y <= bottom; y++) for (var x = left; x <= right; x++)
                    {
                        var wa = Edge(b, c, x + 0.5f, y + 0.5f) / area;
                        var wb = Edge(c, a, x + 0.5f, y + 0.5f) / area;
                        var wc = 1 - wa - wb;
                        if (wa < 0 || wb < 0 || wc < 0) continue;
                        var z = wa * a.Z + wb * b.Z + wc * c.Z;
                        var pixel = y * width + x;
                        if (z <= depths[pixel]) continue;
                        var color = primitive.Color;
                        if (primitive.Texture is { } texture)
                        {
                            var uv = uvA * wa + uvB * wb + uvC * wc;
                            var sampled = texture.GetPixel(Math.Clamp((int)(Math.Clamp(uv.X,0,1) * texture.Width), 0, texture.Width - 1), Math.Clamp((int)(Math.Clamp(uv.Y,0,1) * texture.Height), 0, texture.Height - 1));
                            color = new SKColor((byte)(sampled.Red * color.Red / 255), (byte)(sampled.Green * color.Green / 255), (byte)(sampled.Blue * color.Blue / 255), sampled.Alpha);
                        }
                        if (color.Alpha < 128) continue;
                        pixels[pixel] = new SKColor((byte)(color.Red * shade), (byte)(color.Green * shade), (byte)(color.Blue * shade));
                        depths[pixel] = z; visible++;
                    }
                }
            }
            if (visible == 0) throw new InvalidDataException("GLB scene did not produce visible geometry.");
            var bitmap = new SKBitmap(width, height); bitmap.Pixels = pixels; return bitmap;
            Vector3 Project(Vector3 point) => new((point.X - center.X) * scale + width * 0.5f, height * 0.5f - (point.Y - center.Y) * scale, point.Z);
        }

        private static float Edge(Vector3 a, Vector3 b, float x, float y) => (x - a.X) * (b.Y - a.Y) - (y - a.Y) * (b.X - a.X);
        private void AddNode(int index, Matrix4x4 parent, HashSet<int> ancestors, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var nodes = _root.GetProperty("nodes");
            if (index < 0 || index >= nodes.GetArrayLength() || ancestors.Count >= 64 || !ancestors.Add(index) || ++_instances > 256)
                throw new InvalidDataException("GLB scene hierarchy exceeds the thumbnail budget or contains a cycle.");
            var node = nodes[index]; Matrix4x4 local;
            if (node.TryGetProperty("matrix", out var matrix))
            {
                if (matrix.GetArrayLength() != 16) throw new InvalidDataException("Invalid node matrix shape.");
                var m = matrix.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                if (m.Any(v => !float.IsFinite(v) || Math.Abs(v) > 1_000_000)) throw new InvalidDataException("Invalid node matrix range.");
                if (m.Length != 16) throw new InvalidDataException("Invalid node matrix.");
                local = new Matrix4x4(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]);
            }
            else
            {
                var t = Floats(node, "translation", [0,0,0]); var s = Floats(node, "scale", [1,1,1]); var r = Floats(node, "rotation", [0,0,0,1]);
                if (t.Length != 3 || s.Length != 3 || r.Length != 4) throw new InvalidDataException("Invalid node transform.");
                local = Matrix4x4.CreateScale(s[0],s[1],s[2]) * Matrix4x4.CreateFromQuaternion(new Quaternion(r[0],r[1],r[2],r[3])) * Matrix4x4.CreateTranslation(t[0],t[1],t[2]);
            }
            var world = local * parent;
            if (node.TryGetProperty("mesh", out var mesh))
                foreach (var primitive in _root.GetProperty("meshes")[mesh.GetInt32()].GetProperty("primitives").EnumerateArray())
                    AddPrimitive(primitive, _exporting ? world : world * _camera, ct);
            if (node.TryGetProperty("children", out var children)) foreach (var child in children.EnumerateArray()) AddNode(child.GetInt32(), world, ancestors, ct);
            ancestors.Remove(index);
        }

        private void AddPrimitive(JsonElement source, Matrix4x4 transform, CancellationToken ct)
        {
            if (_primitives.Count >= 256) throw new InvalidDataException("GLB primitive count exceeds its budget.");
            if (Int(source,"mode",4) != 4) throw new NotSupportedException("Static GLB thumbnails require triangle primitives.");
            var attributes = source.GetProperty("attributes");
            var positions = Access(attributes.GetProperty("POSITION").GetInt32(), "VEC3", 5126);
            _vertices = checked(_vertices + positions.Count);
            if (_vertices > MaxVertices) throw new InvalidDataException("GLB exceeds the thumbnail vertex budget.");
            var vertices = new Vector3[positions.Count];
            for (var index = 0; index < vertices.Length; index++)
            {
                if ((index & 4095) == 0) ct.ThrowIfCancellationRequested();
                var raw = new Vector3(positions.Float(index,0), positions.Float(index,1), positions.Float(index,2));
                if (!float.IsFinite(raw.X) || !float.IsFinite(raw.Y) || !float.IsFinite(raw.Z)
                    || Math.Abs(raw.X)>1_000_000 || Math.Abs(raw.Y)>1_000_000 || Math.Abs(raw.Z)>1_000_000)
                    throw new InvalidDataException("GLB source position exceeds its numeric budget.");
                var point = Vector3.Transform(raw, transform);
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)
                    || Math.Abs(point.X) > 1_000_000 || Math.Abs(point.Y) > 1_000_000 || Math.Abs(point.Z) > 1_000_000) throw new InvalidDataException("Non-finite GLB geometry.");
                vertices[index] = point; _minimum = Vector3.Min(_minimum, point); _maximum = Vector3.Max(_maximum, point);
            }
            Accessor? indices = source.TryGetProperty("indices", out var indicesId) ? Access(indicesId.GetInt32(), "SCALAR") : null;
            var count = indices?.Count ?? vertices.Length;
            if (count % 3 != 0 || (_triangles = checked(_triangles + count / 3)) > MaxTriangles) throw new InvalidDataException("Invalid or oversized GLB triangle set.");
            var uv = attributes.TryGetProperty("TEXCOORD_0", out var uvId) ? Access(uvId.GetInt32(), "VEC2", 5126) : null;
            if (uv is not null && uv.Count != vertices.Length) throw new InvalidDataException("GLB texture coordinates do not match the geometry.");
            if (uv is not null) for (var i = 0; i < uv.Count; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                for (var axis = 0; axis < 2; axis++)
                {
                    var value = uv.Float(i,axis);
                    if (!float.IsFinite(value) || Math.Abs(value) > 1_000_000)
                        throw new InvalidDataException("GLB UV is non-finite or outside its coordinate budget.");
                }
            }
            var color = new SKColor(170,185,202); SKBitmap? texture = null;
            if (source.TryGetProperty("material", out var materialId))
            {
                var material = _root.GetProperty("materials")[materialId.GetInt32()];
                if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
                {
                    var factor = Floats(pbr,"baseColorFactor",[1,1,1,1]);
                    if (factor.Length != 4 || factor.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Invalid GLB base color.");
                    color = new SKColor((byte)Math.Clamp(factor[0]*255,0,255),(byte)Math.Clamp(factor[1]*255,0,255),(byte)Math.Clamp(factor[2]*255,0,255),(byte)Math.Clamp(factor[3]*255,0,255));
                    if (uv is not null && pbr.TryGetProperty("baseColorTexture", out var textureInfo) && Int(textureInfo,"texCoord") == 0)
                        texture = Texture(_root.GetProperty("textures")[Int(textureInfo,"index")].GetProperty("source").GetInt32());
                }
            }
            var primitive = new Primitive(vertices, indices, uv, count, color, texture, source, transform);
            for (var i = 0; i < count; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                if (primitive.Index(i) >= vertices.Length) throw new InvalidDataException("GLB index is outside the vertex set.");
            }
            _primitives.Add(primitive);
        }

        private SKBitmap Texture(int index)
        {
            if (_textures.TryGetValue(index,out var cached)) return cached;
            var image = _root.GetProperty("images")[index];
            if (!image.TryGetProperty("bufferView",out var viewId)) throw new NotSupportedException("GLB thumbnail textures must be embedded.");
            var view = _root.GetProperty("bufferViews")[viewId.GetInt32()];
            var offset = Int(view,"byteOffset"); var length = Int(view,"byteLength");
            if (Int(view,"buffer") != 0 || offset < 0 || length <= 0 || length > _binary.Length - offset) throw new InvalidDataException("Invalid GLB image buffer.");
            var bytes = _binary.Slice(offset,length).ToArray();
            using var stream = new SKMemoryStream(bytes);
            using var codec = SKCodec.Create(stream);
            if (codec is null || codec.Info.Width is < 1 or > 4096 || codec.Info.Height is < 1 or > 4096 || _textures.Count >= 16) throw new InvalidDataException("GLB texture exceeds the thumbnail decode budget.");
            _texturePixels += (long)codec.Info.Width * codec.Info.Height;
            if (_texturePixels > 67_108_864) throw new InvalidDataException("GLB exceeds the total thumbnail texture budget.");
            var info = new SKImageInfo(codec.Info.Width,codec.Info.Height,SKColorType.Rgba8888,SKAlphaType.Unpremul);
            var bitmap = new SKBitmap(info);
            if (codec.GetPixels(info,bitmap.GetPixels()) != SKCodecResult.Success)
            {
                bitmap.Dispose();
                throw new InvalidDataException("GLB texture is malformed or truncated.");
            }
            _textures.Add(index,bitmap); return bitmap;
        }
        private Accessor Access(int index, string type, int? componentType = null)
        {
            var accessor = _root.GetProperty("accessors")[index];
            if (accessor.GetProperty("type").GetString() != type || accessor.TryGetProperty("sparse",out _) || !accessor.TryGetProperty("bufferView",out var viewId))
                throw new NotSupportedException("GLB thumbnail accessors must use dense embedded geometry.");
            var component = Int(accessor,"componentType");
            if (componentType.HasValue && component != componentType) throw new NotSupportedException("Unsupported GLB vertex component type.");
            if (!componentType.HasValue && component is not (5121 or 5123 or 5125)) throw new InvalidDataException("Invalid GLB index component type.");
            var size = component == 5121 ? 1 : component == 5123 ? 2 : 4;
            var components = type == "VEC4" ? 4 : type == "VEC3" ? 3 : type == "VEC2" ? 2 : 1;
            var view = _root.GetProperty("bufferViews")[viewId.GetInt32()];
            var offset = Int(view,"byteOffset"); var length = Int(view,"byteLength"); var relative = Int(accessor,"byteOffset");
            var count = Int(accessor,"count"); var stride = Int(view,"byteStride", size * components);
            if (Int(view,"buffer") != 0 || offset < 0 || relative < 0 || length <= 0 || length > _binary.Length - offset || count <= 0 || count > 12_000_000 || stride < size * components
                || (long)relative + (long)(count - 1) * stride + size * components > length)
                throw new InvalidDataException("GLB accessor is outside its buffer view.");
            return new Accessor(_binary.Slice(offset+relative),count,stride,component);
        }
        public void Dispose() { foreach(var texture in _textures.Values) texture.Dispose(); }
        private sealed record Accessor(ReadOnlyMemory<byte> Bytes,int Count,int Stride,int Component)
        {
            public float Float(int index,int component) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(Bytes.Span.Slice(index*Stride+component*4,4)));
            public int Index(int index) => Component switch { 5121 => Bytes.Span[index*Stride], 5123 => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.Span.Slice(index*Stride,2)), _ => checked((int)UInt(Bytes.Span,index*Stride)) };
        }
        private sealed record Primitive(Vector3[] Vertices,Accessor? Indices,Accessor? Coordinates,int Count,SKColor Color,SKBitmap? Texture,JsonElement Source,Matrix4x4 Transform)
        {
            public int Index(int index) => Indices?.Index(index) ?? index;
            public Vector2 Uv(int index) => Coordinates is null ? Vector2.Zero : new(Coordinates.Float(index,0),Coordinates.Float(index,1));
        }
    }
}
