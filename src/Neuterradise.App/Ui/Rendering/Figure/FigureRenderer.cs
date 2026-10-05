using System.Numerics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Neuterradise.App.Media.Model;

namespace Neuterradise.App.Ui.Figure;

public sealed record FigureCamera(
    float Yaw = 0.25f,
    float Pitch = 0.10f,
    float DistanceScale = 1f,
    float TargetOffsetX = 0f,
    float TargetOffsetY = 0f);

/// <summary>Shared D3D shaders and scene resources. The caller retains viewport, presentation, and device-loss policy.</summary>
public sealed class FigureRenderer : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private IntPtr _device, _context, _vertexShader, _pixelShader, _layout, _constants;
    private IntPtr _cullBack, _cullNone, _opaqueDepth, _blendDepth, _alphaBlend, _depthTexture, _depthView;
    private int _width, _height;
    private GpuScene? _scene;
    private bool _disposed;
    private GpuMesh? _pedestal, _accentRim, _contactShadow;
    private Vector3 _stageAccent = new(0.27f, 0.36f, 0.70f);

    public FigureRenderer(IntPtr device, IntPtr context)
    {
        if (device == IntPtr.Zero || context == IntPtr.Zero) throw new ArgumentException("A live shared D3D11 device and context are required.");
        _device = device;
        _context = context;
        Marshal.AddRef(_device);
        Marshal.AddRef(_context);
        try { CreateCommonResources(); }
        catch { Dispose(); throw; }
    }

    public int TriangleCount => _scene?.Meshes.Sum(mesh => mesh.IndexCount / 3) ?? 0;
    public int TextureCount => _scene?.Textures.Count ?? 0;
    public long UploadedBytes => _scene?.Bytes ?? 0;
    public long DrawCount { get; private set; }
    public int StageTriangleCount => ((_pedestal?.IndexCount ?? 0) + (_accentRim?.IndexCount ?? 0) + (_contactShadow?.IndexCount ?? 0)) / 3;

    public void ClearScene()
    {
        EnsureThread();
        _scene?.Dispose();
        _scene = null;
    }

    /// <summary>Updates an sRGB Theme accent without rebuilding the durable scene or shared stage geometry.</summary>
    public void SetStageAccent(Vector3 rgb)
    {
        EnsureThread();
        if (!float.IsFinite(rgb.X) || !float.IsFinite(rgb.Y) || !float.IsFinite(rgb.Z)) throw new ArgumentException("Stage color must be finite.", nameof(rgb));
        rgb = Vector3.Clamp(rgb, Vector3.Zero, Vector3.One);
        _stageAccent = new(SrgbToLinear(rgb.X), SrgbToLinear(rgb.Y), SrgbToLinear(rgb.Z));
    }

    private static float SrgbToLinear(float value) => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    public void Upload(FigureSceneData scene)
    {
        EnsureThread();
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.ByteLength > FigureSceneData.CpuBudget) throw new InvalidDataException("Figure scene exceeds the upload budget.");
        var candidate = new GpuScene(scene.Header, scene.ByteLength);
        try
        {
            foreach (var mesh in scene.Meshes)
            {
                var gpuMesh = new GpuMesh(mesh.Descriptor);
                gpuMesh.Center = MeshCenter(mesh.Vertices);
                candidate.Meshes.Add(gpuMesh);
                gpuMesh.Vertices = CreateBuffer(mesh.Vertices, 1, immutable: true);
                gpuMesh.Indices = CreateBuffer(mesh.Indices, 2, immutable: true);
            }
            foreach (var texture in scene.Textures)
            {
                var gpuTexture = new GpuTexture();
                candidate.Textures.Add(texture.Index, gpuTexture);
                CreateTexture(texture, gpuTexture);
            }
        }
        catch { candidate.Dispose(); throw; }
        var previous = _scene;
        _scene = candidate;
        previous?.Dispose();
    }

    /// <summary>
    /// Camera-distance policy shared with deterministic probes. It fits vertical and horizontal
    /// extents independently so tall Figures do not shrink merely because a Profile layout makes
    /// their viewport narrower, while genuinely wide models still receive horizontal safety.
    /// </summary>
    public static float CalculateFitDistance(Vector3 extent, float aspect, float distanceScale)
    {
        const float fov = MathF.PI * 35 / 180;
        aspect = Math.Max(0.1f, aspect);
        var radius = Math.Max(0.05f, extent.Length() * 0.5f);
        var verticalHalfExtent = Math.Max(0.01f, extent.Y * 0.5f);
        // Rotating around Y can expose both X and Z horizontally.
        var horizontalHalfExtent = Math.Max(
            0.01f,
            MathF.Sqrt(extent.X * extent.X + extent.Z * extent.Z) * 0.5f);
        var tanHalfVertical = MathF.Tan(fov * 0.5f);
        var tanHalfHorizontal = Math.Max(0.01f, tanHalfVertical * aspect);
        var fitDistance = MathF.Max(
            verticalHalfExtent / tanHalfVertical,
            horizontalHalfExtent / tanHalfHorizontal);
        return MathF.Max(radius * 1.05f, fitDistance * 1.10f)
            * Math.Clamp(distanceScale, 0.3f, 4f);
    }

    public void Render(IntPtr target, int width, int height, FigureCamera? camera = null, bool reduced = false)
    {
        EnsureThread();
        if (_scene is not { } scene || target == IntPtr.Zero || width <= 0 || height <= 0) return;
        camera ??= new();
        if (!float.IsFinite(camera.Yaw) || !float.IsFinite(camera.Pitch) || !float.IsFinite(camera.DistanceScale)
            || !float.IsFinite(camera.TargetOffsetX) || !float.IsFinite(camera.TargetOffsetY))
            throw new ArgumentException("Camera values must be finite.", nameof(camera));
        EnsureDepth(width, height);
        var header = scene.Header;
        var scale = header.NormalizationScale;
        const float pedestalHeight = 0.06f;
        var world = Matrix4x4.CreateTranslation(header.GroundOffset[0], header.GroundOffset[1], header.GroundOffset[2])
            * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(0, pedestalHeight, 0);
        var extent = new Vector3(header.Maximum[0] - header.Minimum[0], header.Maximum[1] - header.Minimum[1],
            header.Maximum[2] - header.Minimum[2]) * scale;
        var stageRadius = Math.Max(0.22f, Math.Max(extent.X, extent.Z) * 0.60f);
        extent.X = Math.Max(extent.X, stageRadius * 2);
        extent.Z = Math.Max(extent.Z, stageRadius * 2);
        extent.Y += pedestalHeight;
        var baseCenter = new Vector3(0, extent.Y * 0.5f, 0);
        var radius = Math.Max(0.05f, extent.Length() * 0.5f);
        var aspect = Math.Max(0.1f, (float)width / height);
        const float fov = MathF.PI * 35 / 180;
        var distance = CalculateFitDistance(extent, aspect, camera.DistanceScale);
        var pitch = Math.Clamp(camera.Pitch, -1.3f, 1.3f);
        var orbitDirection = Vector3.Normalize(new Vector3(
            MathF.Sin(camera.Yaw) * MathF.Cos(pitch),
            MathF.Sin(pitch),
            MathF.Cos(camera.Yaw) * MathF.Cos(pitch)));
        var forward = -orbitDirection;
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Normalize(Vector3.Cross(right, forward));
        var center = baseCenter + (right * camera.TargetOffsetX + up * camera.TargetOffsetY) * radius;
        var eye = center + orbitDirection * distance;
        var viewProjection = Matrix4x4.CreateLookAt(eye, center, up)
            * Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, Math.Max(0.001f, distance - radius * 1.6f), distance + radius * 3);
        var viewport = new FigureD3D.Viewport { Width = width, Height = height, MaximumDepth = 1 };
        FigureNative.Method<FigureD3D.SetTargets>(_context, 33)(_context, 1, [target], _depthView);
        try
        {
            FigureNative.Method<FigureD3D.ClearDepth>(_context, FigureD3D.ClearDepthStencilViewSlot)(_context, _depthView, 1, 1, 0);
            FigureNative.Method<FigureD3D.SetViewports>(_context, 44)(_context, 1, ref viewport);
            FigureNative.Method<FigureD3D.SetOne>(_context, 17)(_context, _layout);
            FigureNative.Method<FigureD3D.SetTopology>(_context, 24)(_context, 4);
            FigureNative.Method<FigureD3D.SetShader>(_context, 11)(_context, _vertexShader, IntPtr.Zero, 0);
            FigureNative.Method<FigureD3D.SetShader>(_context, 9)(_context, _pixelShader, IntPtr.Zero, 0);
            FigureNative.Method<FigureD3D.SetArray>(_context, 7)(_context, 0, 1, [_constants]);
            FigureNative.Method<FigureD3D.SetArray>(_context, 16)(_context, 0, 1, [_constants]);
            DrawStage(viewProjection, eye, stageRadius, reduced);
            var opaque = scene.Meshes.Where(mesh => scene.Header.Materials[mesh.Material].AlphaMode != "BLEND");
            var transparent = scene.Meshes.Where(mesh => scene.Header.Materials[mesh.Material].AlphaMode == "BLEND")
                .OrderByDescending(mesh => Vector3.DistanceSquared(eye, Vector3.Transform(mesh.Center, world)));
            foreach (var mesh in opaque.Concat(transparent))
            {
                var material = scene.Header.Materials[mesh.Material];
                var blend = material.AlphaMode == "BLEND";
                FigureNative.Method<FigureD3D.SetOne>(_context, 43)(_context, material.DoubleSided ? _cullNone : _cullBack);
                FigureNative.Method<FigureD3D.SetDepth>(_context, 36)(_context, blend ? _blendDepth : _opaqueDepth, 0);
                FigureNative.Method<FigureD3D.SetBlend>(_context, 35)(_context, blend ? _alphaBlend : IntPtr.Zero, [1, 1, 1, 1], uint.MaxValue);
                var textureIds = new[] { material.BaseColorTexture, material.MetallicRoughnessTexture, material.NormalTexture,
                    material.OcclusionTexture, material.EmissiveTexture };
                var views = new IntPtr[5];
                var samplers = new IntPtr[5];
                for (var i = 0; i < textureIds.Length; i++)
                    if (textureIds[i] is { } index && scene.Textures.TryGetValue(index, out var texture))
                    { views[i] = texture.View; samplers[i] = texture.Sampler; }
                FigureNative.Method<FigureD3D.SetArray>(_context, 8)(_context, 0, 5, views);
                FigureNative.Method<FigureD3D.SetArray>(_context, 10)(_context, 0, 5, samplers);
                var constants = new ShaderConstants
                {
                    World = world, NormalMatrix = NormalMatrix(world), ViewProjection = viewProjection,
                    Base = new(material.BaseColor[0], material.BaseColor[1], material.BaseColor[2], material.BaseColor[3]),
                    Emissive = new(material.Emissive[0], material.Emissive[1], material.Emissive[2], 0),
                    Properties = new(material.Metallic, material.Roughness, material.NormalScale, material.OcclusionStrength),
                    TextureFlags = new(views[0] == IntPtr.Zero ? 0 : 1, views[1] == IntPtr.Zero ? 0 : 1,
                        views[2] == IntPtr.Zero ? 0 : 1, views[3] == IntPtr.Zero ? 0 : 1),
                    Extra = new(views[4] == IntPtr.Zero ? 0 : 1, material.AlphaMode == "MASK" ? 1 : blend ? 2 : 0,
                        material.AlphaCutoff, material.DoubleSided ? 1 : 0),
                    Eye = new(eye, 0),
                    Policy = new(reduced ? 1 : 0, 0, 0, 0),
                };
                UpdateConstants(constants);
                FigureNative.Method<FigureD3D.SetVertexBuffers>(_context, 18)(_context, 0, 1, [mesh.Vertices], [48], [0]);
                FigureNative.Method<FigureD3D.SetIndexBuffer>(_context, 19)(_context, mesh.Indices, 42, 0);
                FigureNative.Method<FigureD3D.DrawIndexed>(_context, 12)(_context, (uint)mesh.IndexCount, 0, 0);
                DrawCount++;
            }
        }
        finally
        {
            // Swapchain resize must not retain a back-buffer reference through the shared context.
            FigureNative.Method<FigureD3D.SetTargets>(_context, 33)(_context, 0, [], IntPtr.Zero);
            FigureNative.Method<FigureD3D.SetArray>(_context, 8)(_context, 0, 5, new IntPtr[5]);
            FigureNative.Method<FigureD3D.SetVertexBuffers>(_context, 18)(_context, 0, 1, [IntPtr.Zero], [48], [0]);
            FigureNative.Method<FigureD3D.SetIndexBuffer>(_context, 19)(_context, IntPtr.Zero, 42, 0);
        }
    }

    /// <summary>Diagnostic readback from the actual render target; packed little-endian BGRA, 0xAARRGGBB.</summary>
    public uint ReadbackPixel(IntPtr target, int width, int height, int x, int y)
    {
        EnsureThread();
        if (target == IntPtr.Zero || x < 0 || y < 0 || x >= width || y >= height) throw new ArgumentOutOfRangeException(nameof(x));
        FigureNative.Method<FigureD3D.GetResource>(target, 7)(target, out var source);
        IntPtr staging = IntPtr.Zero;
        IntPtr sourceTexture = IntPtr.Zero;
        try
        {
            var iid = FigureNative.Texture2D;
            FigureNative.Check(Marshal.QueryInterface(source, in iid, out sourceTexture));
            FigureNative.Method<FigureD3D.GetTextureDescription>(sourceTexture, 10)(sourceTexture, out var sourceDescription);
            var desc = new FigureD3D.TextureDescription
            {
                Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = sourceDescription.Format,
                SampleCount = 1, Usage = 3, CpuAccessFlags = 0x20000,
            };
            FigureNative.Check(FigureNative.Method<FigureD3D.CreateTexture>(_device, 5)(_device, ref desc, null, out staging));
            var box = new FigureD3D.Box { Left = (uint)x, Top = (uint)y, Right = (uint)x + 1, Bottom = (uint)y + 1, Back = 1 };
            FigureNative.Method<FigureD3D.CopyRegion>(_context, 46)(_context, staging, 0, 0, 0, 0, source, 0, ref box);
            FigureNative.Check(FigureNative.Method<FigureD3D.Map>(_context, 14)(_context, staging, 0, 1, 0, out var mapped));
            try { return unchecked((uint)Marshal.ReadInt32(mapped.Data)); }
            finally { FigureNative.Method<FigureD3D.Unmap>(_context, 15)(_context, staging, 0); }
        }
        finally { FigureNative.Release(ref staging); FigureNative.Release(ref sourceTexture); FigureNative.Release(ref source); }
    }

    private void CreateCommonResources()
    {
        IntPtr vertexCode = IntPtr.Zero, pixelCode = IntPtr.Zero;
        try
        {
            vertexCode = Compile("VS", "vs_4_0");
            pixelCode = Compile("PS", "ps_4_0");
            var vertexData = FigureNative.Method<FigureD3D.BlobData>(vertexCode, 3)(vertexCode);
            var vertexLength = FigureNative.Method<FigureD3D.BlobSize>(vertexCode, 4)(vertexCode);
            FigureNative.Check(FigureNative.Method<FigureD3D.CreateShader>(_device, 12)(_device, vertexData, vertexLength, IntPtr.Zero, out _vertexShader));
            FigureNative.Check(FigureNative.Method<FigureD3D.CreateShader>(_device, 15)(_device,
                FigureNative.Method<FigureD3D.BlobData>(pixelCode, 3)(pixelCode),
                FigureNative.Method<FigureD3D.BlobSize>(pixelCode, 4)(pixelCode), IntPtr.Zero, out _pixelShader));
            var names = new[] { "POSITION", "NORMAL", "TEXCOORD", "TANGENT" }.Select(Marshal.StringToHGlobalAnsi).ToArray();
            try
            {
                var elements = new[]
                {
                    new FigureD3D.InputElement { Semantic = names[0], Format = 6, Offset = 0 },
                    new FigureD3D.InputElement { Semantic = names[1], Format = 6, Offset = 12 },
                    new FigureD3D.InputElement { Semantic = names[2], Format = 16, Offset = 24 },
                    new FigureD3D.InputElement { Semantic = names[3], Format = 2, Offset = 32 },
                };
                FigureNative.Check(FigureNative.Method<FigureD3D.CreateLayout>(_device, 11)(_device, elements, 4, vertexData, vertexLength, out _layout));
            }
            finally { foreach (var name in names) Marshal.FreeHGlobal(name); }
        }
        finally { FigureNative.Release(ref vertexCode); FigureNative.Release(ref pixelCode); }
        _constants = CreateBuffer(new byte[Marshal.SizeOf<ShaderConstants>()], 4, immutable: false);
        var raster = new FigureD3D.RasterizerDescription { FillMode = 3, CullMode = 3, FrontCounterClockwise = 1, DepthClipEnable = 1 };
        FigureNative.Check(FigureNative.Method<FigureD3D.CreateRasterizer>(_device, 22)(_device, ref raster, out _cullBack));
        raster.CullMode = 1;
        FigureNative.Check(FigureNative.Method<FigureD3D.CreateRasterizer>(_device, 22)(_device, ref raster, out _cullNone));
        var stencil = new FigureD3D.DepthOperation { Fail = 1, DepthFail = 1, Pass = 1, Function = 8 };
        var depth = new FigureD3D.DepthDescription { Enabled = 1, WriteMask = 1, Function = 2,
            ReadMask = 255, WriteStencilMask = 255, Front = stencil, Back = stencil };
        FigureNative.Check(FigureNative.Method<FigureD3D.CreateDepth>(_device, 21)(_device, ref depth, out _opaqueDepth));
        depth.WriteMask = 0;
        FigureNative.Check(FigureNative.Method<FigureD3D.CreateDepth>(_device, 21)(_device, ref depth, out _blendDepth));
        var blend = new FigureD3D.BlendDescription { Targets = new FigureD3D.BlendTarget[8] };
        blend.Targets[0] = new() { Enabled = 1, Source = 5, Destination = 6, Operation = 1,
            SourceAlpha = 2, DestinationAlpha = 6, AlphaOperation = 1, WriteMask = 15 };
        FigureNative.Check(FigureNative.Method<FigureD3D.CreateBlend>(_device, 20)(_device, ref blend, out _alphaBlend));
        _pedestal = CreateStageMesh(FigureStageGeometry.Pedestal());
        _accentRim = CreateStageMesh(FigureStageGeometry.AccentRim());
        _contactShadow = CreateStageMesh(FigureStageGeometry.ContactShadow());
    }

    private GpuMesh CreateStageMesh(FigureStageMesh geometry)
    {
        var mesh = new GpuMesh(new(geometry.Vertices.Length / 48, geometry.Indices.Length / 4, 0, 1, 0, 0));
        try
        {
            mesh.Vertices = CreateBuffer(geometry.Vertices, 1, immutable: true);
            mesh.Indices = CreateBuffer(geometry.Indices, 2, immutable: true);
            return mesh;
        }
        catch { mesh.Dispose(); throw; }
    }

    private void DrawStage(Matrix4x4 viewProjection, Vector3 eye, float radius, bool reduced)
    {
        FigureNative.Method<FigureD3D.SetArray>(_context, 8)(_context, 0, 5, new IntPtr[5]);
        FigureNative.Method<FigureD3D.SetArray>(_context, 10)(_context, 0, 5, new IntPtr[5]);
        FigureNative.Method<FigureD3D.SetOne>(_context, 43)(_context, _cullNone);
        if (!reduced) Draw(_contactShadow!, 3, new Vector3(0), 0, 1, radius * 1.85f, blend: true);
        Draw(_pedestal!, 1, new Vector3(0.30f, 0.32f, 0.36f), 0.05f, 0.62f, radius, blend: false);
        Draw(_accentRim!, 2, _stageAccent, 0.45f, 0.35f, radius, blend: false);

        void Draw(GpuMesh mesh, int stageKind, Vector3 color, float metallic, float roughness, float horizontalScale, bool blend)
        {
            var world = Matrix4x4.CreateScale(horizontalScale, 1, horizontalScale);
            FigureNative.Method<FigureD3D.SetDepth>(_context, 36)(_context, blend ? _blendDepth : _opaqueDepth, 0);
            FigureNative.Method<FigureD3D.SetBlend>(_context, 35)(_context, blend ? _alphaBlend : IntPtr.Zero, [1, 1, 1, 1], uint.MaxValue);
            UpdateConstants(new ShaderConstants
            {
                World = world, NormalMatrix = NormalMatrix(world), ViewProjection = viewProjection,
                Base = new(color, 1), Properties = new(metallic, roughness, 1, 1),
                Extra = new(0, blend ? 2 : 0, 0, 1), Eye = new(eye, stageKind),
                Policy = new(reduced ? 1 : 0, 0, 0, 0),
            });
            FigureNative.Method<FigureD3D.SetVertexBuffers>(_context, 18)(_context, 0, 1, [mesh.Vertices], [48], [0]);
            FigureNative.Method<FigureD3D.SetIndexBuffer>(_context, 19)(_context, mesh.Indices, 42, 0);
            FigureNative.Method<FigureD3D.DrawIndexed>(_context, 12)(_context, (uint)mesh.IndexCount, 0, 0);
            DrawCount++;
        }
    }

    private static Matrix4x4 NormalMatrix(Matrix4x4 world)
    {
        if (!Matrix4x4.Invert(world, out var inverse)) throw new InvalidDataException("Figure normalization has no valid inverse.");
        return Matrix4x4.Transpose(inverse);
    }

    private static Vector3 MeshCenter(byte[] vertices)
    {
        var minimum = new Vector3(float.MaxValue); var maximum = new Vector3(float.MinValue);
        for (var offset = 0; offset < vertices.Length; offset += 48)
        {
            var point = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(vertices.AsSpan(offset)),
                BinaryPrimitives.ReadSingleLittleEndian(vertices.AsSpan(offset + 4)),
                BinaryPrimitives.ReadSingleLittleEndian(vertices.AsSpan(offset + 8)));
            minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point);
        }
        return (minimum + maximum) * 0.5f;
    }

    private static IntPtr Compile(string entry, string target)
    {
        var source = Encoding.UTF8.GetBytes(ShaderSource);
        var result = FigureD3D.D3DCompile(source, (nuint)source.Length, "NautFigure", IntPtr.Zero, IntPtr.Zero,
            entry, target, 0x8000, 0, out var code, out var errors);
        try
        {
            if (result < 0)
            {
                var detail = errors == IntPtr.Zero ? "Shader compilation failed." : Marshal.PtrToStringAnsi(
                    FigureNative.Method<FigureD3D.BlobData>(errors, 3)(errors)) ?? "Shader compilation failed.";
                FigureNative.Release(ref code);
                throw new InvalidOperationException(detail);
            }
            return code;
        }
        finally { FigureNative.Release(ref errors); }
    }

    private IntPtr CreateBuffer(byte[] bytes, uint bind, bool immutable)
    {
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var desc = new FigureD3D.BufferDescription { ByteWidth = checked((uint)bytes.Length), Usage = immutable ? 1u : 0, BindFlags = bind };
            var data = new FigureD3D.Subresource { Data = pinned.AddrOfPinnedObject() };
            FigureNative.Check(FigureNative.Method<FigureD3D.CreateBuffer>(_device, 3)(_device, ref desc, ref data, out var buffer));
            return buffer;
        }
        finally { pinned.Free(); }
    }

    private void CreateTexture(FigureTextureData texture, GpuTexture gpu)
    {
        var pins = new List<GCHandle>();
        try
        {
            var data = new FigureD3D.Subresource[texture.Mips.Length];
            for (var mip = 0; mip < data.Length; mip++)
            {
                var pin = GCHandle.Alloc(texture.Mips[mip], GCHandleType.Pinned);
                pins.Add(pin);
                data[mip] = new() { Data = pin.AddrOfPinnedObject(), Pitch = (uint)Math.Max(1, texture.Descriptor.Width >> mip) * 4 };
            }
            var desc = new FigureD3D.TextureDescription
            {
                Width = (uint)texture.Descriptor.Width, Height = (uint)texture.Descriptor.Height,
                MipLevels = (uint)data.Length, ArraySize = 1, Format = texture.Descriptor.Srgb ? 29u : 28u,
                SampleCount = 1, Usage = 1, BindFlags = 8,
            };
            FigureNative.Check(FigureNative.Method<FigureD3D.CreateTexture>(_device, 5)(_device, ref desc, data, out gpu.Texture));
            FigureNative.Check(FigureNative.Method<FigureD3D.CreateView>(_device, 7)(_device, gpu.Texture, IntPtr.Zero, out gpu.View));
            var sampler = new FigureD3D.SamplerDescription
            {
                Filter = Filter(texture.Descriptor), AddressU = Address(texture.Descriptor.WrapS), AddressV = Address(texture.Descriptor.WrapT),
                AddressW = 3, MaxAnisotropy = 1, Comparison = 1, MinLod = 0,
                MaxLod = texture.Descriptor.MinFilter is 9728 or 9729 ? 0 : float.MaxValue,
            };
            FigureNative.Check(FigureNative.Method<FigureD3D.CreateSampler>(_device, 23)(_device, ref sampler, out gpu.Sampler));
        }
        finally { foreach (var pin in pins) pin.Free(); }
    }

    private static uint Address(int wrap) => wrap == 33071 ? 3u : wrap == 33648 ? 2u : 1u;
    private static uint Filter(ModelRenderTexture texture)
    {
        var minLinear = texture.MinFilter is 9729 or 9985 or 9987;
        var mipLinear = texture.MinFilter is 9986 or 9987;
        return (minLinear ? 0x10u : 0) | (texture.MagFilter == 9729 ? 0x4u : 0) | (mipLinear ? 1u : 0);
    }

    private void EnsureDepth(int width, int height)
    {
        if (_width == width && _height == height) return;
        FigureNative.Release(ref _depthView);
        FigureNative.Release(ref _depthTexture);
        var desc = new FigureD3D.TextureDescription { Width = (uint)width, Height = (uint)height,
            MipLevels = 1, ArraySize = 1, Format = 45, SampleCount = 1, BindFlags = 0x40 };
        FigureNative.Check(FigureNative.Method<FigureD3D.CreateTexture>(_device, 5)(_device, ref desc, null, out _depthTexture));
        FigureNative.Check(FigureNative.Method<FigureD3D.CreateView>(_device, 10)(_device, _depthTexture, IntPtr.Zero, out _depthView));
        _width = width;
        _height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderConstants
    {
        public Matrix4x4 World, NormalMatrix, ViewProjection;
        public Vector4 Base, Emissive, Properties, TextureFlags, Extra, Eye, Policy;
    }

    private void UpdateConstants(ShaderConstants constants)
    {
        var bytes = new byte[Marshal.SizeOf<ShaderConstants>()];
        MemoryMarshal.Write(bytes, in constants);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { FigureNative.Method<FigureD3D.Update>(_context, 48)(_context, _constants, 0, IntPtr.Zero, pin.AddrOfPinnedObject(), 0, 0); }
        finally { pin.Free(); }
    }

    private void EnsureThread()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Figure GPU resources require their owning UI thread.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Figure GPU resources require their owning UI thread.");
        _disposed = true;
        _scene?.Dispose();
        _scene = null;
        _contactShadow?.Dispose(); _accentRim?.Dispose(); _pedestal?.Dispose();
        FigureNative.Release(ref _depthView); FigureNative.Release(ref _depthTexture);
        FigureNative.Release(ref _alphaBlend); FigureNative.Release(ref _blendDepth); FigureNative.Release(ref _opaqueDepth);
        FigureNative.Release(ref _cullNone); FigureNative.Release(ref _cullBack); FigureNative.Release(ref _constants);
        FigureNative.Release(ref _layout); FigureNative.Release(ref _pixelShader); FigureNative.Release(ref _vertexShader);
        FigureNative.Release(ref _context); FigureNative.Release(ref _device);
    }

    private sealed class GpuMesh(ModelRenderMesh descriptor) : IDisposable
    {
        public IntPtr Vertices, Indices;
        public int IndexCount => descriptor.Indices;
        public int Material => descriptor.Material;
        public Vector3 Center { get; set; }
        public void Dispose() { FigureNative.Release(ref Vertices); FigureNative.Release(ref Indices); }
    }
    private sealed class GpuTexture : IDisposable
    {
        public IntPtr Texture, View, Sampler;
        public void Dispose() { FigureNative.Release(ref Sampler); FigureNative.Release(ref View); FigureNative.Release(ref Texture); }
    }
    private sealed class GpuScene(ModelRenderHeader header, long bytes) : IDisposable
    {
        public ModelRenderHeader Header => header;
        public long Bytes => bytes;
        public List<GpuMesh> Meshes { get; } = [];
        public Dictionary<int, GpuTexture> Textures { get; } = [];
        public void Dispose() { foreach (var mesh in Meshes) mesh.Dispose(); foreach (var texture in Textures.Values) texture.Dispose(); }
    }

    private const string ShaderSource = """
        cbuffer Scene : register(b0) {
            row_major float4x4 world; row_major float4x4 normalMatrix; row_major float4x4 viewProjection;
            float4 baseFactor; float4 emissiveFactor; float4 properties; float4 textureFlags; float4 extra; float4 eye; float4 policy;
        };
        Texture2D baseMap : register(t0); Texture2D mrMap : register(t1); Texture2D normalMap : register(t2);
        Texture2D occlusionMap : register(t3); Texture2D emissiveMap : register(t4);
        SamplerState baseSampler : register(s0); SamplerState mrSampler : register(s1); SamplerState normalSampler : register(s2);
        SamplerState occlusionSampler : register(s3); SamplerState emissiveSampler : register(s4);
        struct Input { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float4 tangent : TANGENT; };
        struct Interpolated { float4 position : SV_POSITION; float3 worldPosition : TEXCOORD0; float3 normal : TEXCOORD1;
            float2 uv : TEXCOORD2; float4 tangent : TEXCOORD3; };
        Interpolated VS(Input input) {
            Interpolated output; float4 worldPosition = mul(float4(input.position,1),world);
            output.position = mul(worldPosition,viewProjection); output.worldPosition = worldPosition.xyz;
            output.normal = normalize(mul(input.normal,(float3x3)normalMatrix)); output.uv = input.uv;
            output.tangent = float4(normalize(mul(input.tangent.xyz,(float3x3)world)),input.tangent.w); return output;
        }
        float3 Fresnel(float cosine, float3 f0) {
            return f0 + (1-f0)*pow(1-saturate(cosine),5);
        }
        float3 DirectLight(float3 baseColor, float metallic, float roughness, float3 n, float3 v, float3 l, float3 radiance) {
            float nv = max(dot(n,v),0.001); float nl = saturate(dot(n,l));
            float3 h = normalize(v+l); float nh = saturate(dot(n,h)); float vh = saturate(dot(v,h));
            float a = roughness*roughness; float a2 = a*a;
            float denominator = nh*nh*(a2-1)+1;
            float distribution = a2 / max(3.14159265*denominator*denominator,0.000001);
            float gv = 2*nv/(nv+sqrt(a2+(1-a2)*nv*nv));
            float gl = 2*nl/max(nl+sqrt(a2+(1-a2)*nl*nl),0.000001);
            float3 f = Fresnel(vh,lerp(float3(0.04,0.04,0.04),baseColor,metallic));
            float3 specular = distribution*gv*gl*f/max(4*nv*nl,0.00001);
            float3 diffuse = (1-f)*(1-metallic)*baseColor/3.14159265;
            return (diffuse+specular)*radiance*nl;
        }
        float3 StudioEnvironment(float3 direction, float roughness) {
            float3 environment = lerp(float3(0.055,0.05,0.065),float3(0.32,0.36,0.44),saturate(direction.y*0.5+0.5));
            float exponent = lerp(96,2.5,roughness*roughness);
            float normalization = lerp(1,0.24,roughness);
            environment += float3(2.8,2.55,2.3)*pow(saturate(dot(direction,normalize(float3(-0.45,0.78,0.8)))),exponent)*normalization;
            if (policy.x < 0.5) {
                environment += float3(0.85,1.02,1.25)*pow(saturate(dot(direction,normalize(float3(0.7,0.3,0.3)))),exponent*0.65)*normalization;
                environment += float3(1.25,1.35,1.65)*pow(saturate(dot(direction,normalize(float3(-0.4,0.55,-0.7)))),exponent)*normalization;
            }
            return environment;
        }
        float2 EnvironmentBRDF(float roughness, float nv) {
            float4 c0 = float4(-1,-0.0275,-0.572,0.022); float4 c1 = float4(1,0.0425,1.04,-0.04);
            float4 r = roughness*c0+c1;
            float a004 = min(r.x*r.x,exp2(-9.28*nv))*r.x+r.y;
            return float2(-1.04,1.04)*a004+r.zw;
        }
        float3 ToneMap(float3 color) {
            return saturate(color*(2.51*color+0.03)/(color*(2.43*color+0.59)+0.14));
        }
        float4 PS(Interpolated input, bool front : SV_IsFrontFace) : SV_TARGET {
            if (eye.w > 2.5) {
                float2 radial = input.uv*2-1;
                float softContact = exp(-dot(radial,radial)*5.8)*0.32;
                return float4(0,0,0,softContact);
            }
            float4 base = baseFactor;
            if (textureFlags.x > 0.5) base *= baseMap.Sample(baseSampler,input.uv);
            if (extra.y > 0.5 && extra.y < 1.5) clip(base.a-extra.z);
            float3 n = normalize(input.normal); if (!front && extra.w > 0.5) n = -n;
            if (textureFlags.z > 0.5) {
                float3 t = normalize(input.tangent.xyz - n*dot(input.tangent.xyz,n));
                float3 b = normalize(cross(n,t))*input.tangent.w;
                float3 sampled = normalMap.Sample(normalSampler,input.uv).xyz*2-1;
                sampled.xy *= properties.z; n = normalize(t*sampled.x+b*sampled.y+n*sampled.z);
            }
            float metallic = properties.x; float roughness = properties.y;
            if (textureFlags.y > 0.5) { float4 mr = mrMap.Sample(mrSampler,input.uv); metallic *= mr.b; roughness *= mr.g; }
            float ao = 1; if (textureFlags.w > 0.5) ao = lerp(1,occlusionMap.Sample(occlusionSampler,input.uv).r,properties.w);
            if (policy.x < 0.5 && eye.w > 0.5 && eye.w < 1.5 && n.y > 0.5) {
                float2 contact = input.uv*2-1;
                ao *= 1-exp(-dot(contact,contact)*8)*0.32;
            }
            float3 emissive = emissiveFactor.xyz; if (extra.x > 0.5) emissive *= emissiveMap.Sample(emissiveSampler,input.uv).rgb;
            metallic = saturate(metallic); roughness = clamp(roughness,0.045,1);
            float3 v = normalize(eye.xyz-input.worldPosition);
            float3 color = DirectLight(base.rgb,metallic,roughness,n,v,normalize(float3(-0.45,0.78,0.8)),float3(3.4,3.1,2.8));
            if (policy.x < 0.5) {
                color += DirectLight(base.rgb,metallic,roughness,n,v,normalize(float3(0.7,0.3,0.3)),float3(0.75,0.92,1.15));
                color += DirectLight(base.rgb,metallic,roughness,n,v,normalize(float3(-0.4,0.55,-0.7)),float3(1.05,1.15,1.35));
            }
            float nv = saturate(dot(n,v)); float3 f0 = lerp(float3(0.04,0.04,0.04),base.rgb,metallic);
            float3 ambientFresnel = f0+(max(float3(1-roughness,1-roughness,1-roughness),f0)-f0)*pow(1-nv,5);
            float3 diffuseIrradiance = lerp(float3(0.12,0.10,0.12),float3(0.38,0.42,0.50),saturate(n.y*0.5+0.5));
            float2 integratedBRDF = EnvironmentBRDF(roughness,nv);
            float3 specularIBL = StudioEnvironment(reflect(-v,n),roughness)*(f0*integratedBRDF.x+integratedBRDF.y);
            color += ((1-ambientFresnel)*(1-metallic)*base.rgb*diffuseIrradiance+specularIBL)*ao;
            color += emissive;
            color = ToneMap(color);
            return float4(color,extra.y > 1.5 ? base.a : 1);
        }
        """;
}
