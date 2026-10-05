using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.MediaTools;

namespace Neuterradise.App.Media.Model;

public sealed class ModelInteractiveSession : IModelInteractiveSession
{
    private bool _isDisposed;

    public Guid MediaId { get; }
    public string FilePath { get; }
    public double CurrentYaw { get; private set; }
    public double CurrentPitch { get; private set; }
    public double CurrentZoom { get; private set; } = 1.0;
    public bool IsDisposed => _isDisposed;

    public ModelInteractiveSession(Guid assetId, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        MediaId = assetId;
        FilePath = filePath;
    }

    public void Rotate(double deltaYaw, double deltaPitch)
    {
        ThrowIfDisposed();
        CurrentYaw = (CurrentYaw + deltaYaw) % 360.0;
        CurrentPitch = Math.Clamp(CurrentPitch + deltaPitch, -89.0, 89.0);
    }

    public void Zoom(double factor)
    {
        ThrowIfDisposed();
        if (factor > 0.0)
        {
            CurrentZoom = Math.Clamp(CurrentZoom * factor, 0.1, 10.0);
        }
    }

    public void ResetCamera()
    {
        ThrowIfDisposed();
        CurrentYaw = 0.0;
        CurrentPitch = 0.0;
        CurrentZoom = 1.0;
    }

    public void Dispose()
    {
        _isDisposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}

public sealed class ModelPreviewAdapterRegistry
{
    private readonly List<IModelPreviewAdapter> _adapters = [];
    private readonly object _syncLock = new();

    /// <summary>
    /// The fallback adapter is stateless and holds no unmanaged resource, so one shared instance is
    /// safe. There is deliberately no shared default registry: the composition root owns the single
    /// registry instance so adapter lifetime follows the runtime session.
    /// </summary>
    public static FallbackModelPreviewAdapter FallbackAdapter { get; } = new();

    public void Register(IModelPreviewAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        lock (_syncLock)
        {
            _adapters.RemoveAll(a => a.AdapterId == adapter.AdapterId);
            _adapters.Add(adapter);
            _adapters.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }
    }

    public bool Unregister(string adapterId)
    {
        if (string.IsNullOrWhiteSpace(adapterId)) return false;
        lock (_syncLock)
        {
            return _adapters.RemoveAll(a => a.AdapterId == adapterId) > 0;
        }
    }

    public IReadOnlyList<IModelPreviewAdapter> GetRegisteredAdapters()
    {
        lock (_syncLock)
        {
            return _adapters.ToList();
        }
    }

    public IModelPreviewAdapter ResolveAdapter(ModelProbeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_syncLock)
        {
            foreach (var adapter in _adapters)
            {
                if (adapter.CanHandle(input))
                {
                    return adapter;
                }
            }
        }
        return FallbackAdapter;
    }

    public async Task<ModelMetadata> ProbeAsync(
        ModelProbeInput input,
        CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(input);
        return await adapter.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ModelPreviewDescriptor> GetOrGeneratePreviewAsync(
        ModelPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = new ModelProbeInput(request.FilePath, request.Extension);
        var adapter = ResolveAdapter(input);
        return await adapter.GetOrGeneratePreviewAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IModelInteractiveSession?> CreateInteractiveSessionAsync(
        ModelInteractiveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = new ModelProbeInput(request.FilePath);
        var adapter = ResolveAdapter(input);
        return await adapter.CreateInteractiveSessionAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports every registered preview adapter through the single runtime capability state model, so
    /// a settings or diagnostics surface describes adapters, tools and models the same way. An adapter
    /// whose external dependency is not deployed is reported as unavailable with the concrete reason
    /// instead of silently producing an empty preview.
    /// </summary>
    public IReadOnlyList<RuntimeCapabilityStatus> DescribeAdapters()
    {
        List<IModelPreviewAdapter> adapters;
        lock (_syncLock)
        {
            adapters = [.. _adapters];
        }

        var statuses = new List<RuntimeCapabilityStatus>(adapters.Count);
        foreach (var adapter in adapters)
        {
            statuses.Add(adapter.DescribeCapability());
        }

        return statuses;
    }

    public static ModelPreviewAdapterRegistry CreateDefaultRegistry(IProcessLauncher? launcher = null)
    {
        var registry = new ModelPreviewAdapterRegistry();
        registry.Register(new GlbThumbnailPreviewAdapter());
        registry.Register(new GltfModelPreviewAdapter());
        registry.Register(new ObjModelPreviewAdapter());
        registry.Register(new StlModelPreviewAdapter());
        registry.Register(new F3dModelPreviewAdapter(launcher: launcher));
        registry.Register(FallbackAdapter);
        return registry;
    }
}

public sealed class GltfModelPreviewAdapter : IModelPreviewAdapter
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunkType = 0x4E4F534A;

    public string AdapterId => "builtin-gltf";
    public string DisplayName => "glTF / GLB Model Adapter";
    public string Version => "1.0.0";
    public int Priority => 100;
    public ModelAdapterCapabilities Capabilities =>
        ModelAdapterCapabilities.Probe |
        ModelAdapterCapabilities.Turntable |
        ModelAdapterCapabilities.Interactive;

    public bool CanHandle(ModelProbeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ext = input.Extension ?? Path.GetExtension(input.FilePath);
        if (string.Equals(ext, ".glb", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".gltf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (File.Exists(input.FilePath))
        {
            try
            {
                using var fs = File.OpenRead(input.FilePath);
                var header = new byte[4];
                if (fs.Read(header, 0, 4) == 4)
                {
                    if (BinaryPrimitives.ReadUInt32LittleEndian(header) == GlbMagic)
                    {
                        return true;
                    }
                }
            }
            catch { }
        }

        return false;
    }

    public async Task<ModelMetadata> ProbeAsync(
        ModelProbeInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!File.Exists(input.FilePath))
        {
            return new ModelMetadata(
                Format: "GLTF",
                Status: ModelAdapterStatus.Degraded,
                AdapterId: AdapterId,
                AdapterVersion: Version,
                Diagnostics: [$"File does not exist: {input.FilePath}"]);
        }

        var fileLength = new FileInfo(input.FilePath).Length;
        var ext = input.Extension ?? Path.GetExtension(input.FilePath);
        var isGlb = string.Equals(ext, ".glb", StringComparison.OrdinalIgnoreCase);

        try
        {
            string? jsonContent = null;
            if (isGlb)
            {
                jsonContent = await ReadGlbJsonAsync(input.FilePath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                jsonContent = await File.ReadAllTextAsync(input.FilePath, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                return new ModelMetadata(
                    Format: isGlb ? "GLB" : "GLTF",
                    FileSizeBytes: fileLength,
                    Status: ModelAdapterStatus.Degraded,
                    AdapterId: AdapterId,
                    AdapterVersion: Version,
                    Diagnostics: ["Unable to read glTF JSON structure."]);
            }

            return ParseGltfJson(jsonContent, isGlb ? "GLB" : "GLTF", fileLength);
        }
        catch (Exception ex)
        {
            return new ModelMetadata(
                Format: isGlb ? "GLB" : "GLTF",
                FileSizeBytes: fileLength,
                Status: ModelAdapterStatus.Degraded,
                AdapterId: AdapterId,
                AdapterVersion: Version,
                Diagnostics: [$"Error parsing glTF model: {ex.Message}"]);
        }
    }

    public Task<ModelPreviewDescriptor> GetOrGeneratePreviewAsync(
        ModelPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ext = request.Extension ?? Path.GetExtension(request.FilePath);
        var format = string.Equals(ext, ".glb", StringComparison.OrdinalIgnoreCase) ? "GLB" : "GLTF";

        var descriptor = new ModelPreviewDescriptor(
            request.MediaId,
            StaticThumbnailPath: null,
            TurntableFramePaths: request.RequestTurntable ? [] : null,
            Format: format,
            HasTurntable: request.RequestTurntable,
            AdapterId: AdapterId,
            IsInteractive: true);

        return Task.FromResult(descriptor);
    }

    public Task<IModelInteractiveSession?> CreateInteractiveSessionAsync(
        ModelInteractiveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IModelInteractiveSession session = new ModelInteractiveSession(request.MediaId, request.FilePath);
        return Task.FromResult<IModelInteractiveSession?>(session);
    }

    private static async Task<string?> ReadGlbJsonAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var header = new byte[12];
        if (await stream.ReadAsync(header.AsMemory(0, 12), ct).ConfigureAwait(false) < 12)
        {
            return null;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        if (magic != GlbMagic) return null;

        var chunkHeader = new byte[8];
        if (await stream.ReadAsync(chunkHeader.AsMemory(0, 8), ct).ConfigureAwait(false) < 8)
        {
            return null;
        }

        var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(0, 4));
        var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));
        if (chunkType != JsonChunkType || chunkLength > 50 * 1024 * 1024)
        {
            return null;
        }

        var jsonBytes = new byte[chunkLength];
        var readTotal = 0;
        while (readTotal < chunkLength)
        {
            var read = await stream.ReadAsync(jsonBytes.AsMemory(readTotal, (int)chunkLength - readTotal), ct).ConfigureAwait(false);
            if (read == 0) break;
            readTotal += read;
        }

        return Encoding.UTF8.GetString(jsonBytes, 0, readTotal);
    }

    private ModelMetadata ParseGltfJson(string json, string format, long fileLength)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        int? meshCount = root.TryGetProperty("meshes", out var meshes) && meshes.ValueKind == JsonValueKind.Array
            ? meshes.GetArrayLength() : null;

        int? materialCount = root.TryGetProperty("materials", out var mats) && mats.ValueKind == JsonValueKind.Array
            ? mats.GetArrayLength() : null;

        int? textureCount = root.TryGetProperty("textures", out var texs) && texs.ValueKind == JsonValueKind.Array
            ? texs.GetArrayLength() : null;

        int? animationCount = root.TryGetProperty("animations", out var anims) && anims.ValueKind == JsonValueKind.Array
            ? anims.GetArrayLength() : null;

        int? cameraCount = root.TryGetProperty("cameras", out var cams) && cams.ValueKind == JsonValueKind.Array
            ? cams.GetArrayLength() : null;

        long? totalVertices = null;
        long? totalTriangles = null;

        if (root.TryGetProperty("accessors", out var accessors) && accessors.ValueKind == JsonValueKind.Array)
        {
            long vCount = 0;
            long iCount = 0;
            var foundV = false;
            var foundI = false;

            foreach (var acc in accessors.EnumerateArray())
            {
                if (acc.TryGetProperty("count", out var cProp) && cProp.TryGetInt64(out var cnt))
                {
                    var type = acc.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
                    if (string.Equals(type, "VEC3", StringComparison.OrdinalIgnoreCase))
                    {
                        vCount += cnt;
                        foundV = true;
                    }
                    else if (string.Equals(type, "SCALAR", StringComparison.OrdinalIgnoreCase))
                    {
                        iCount += cnt;
                        foundI = true;
                    }
                }
            }

            if (foundV) totalVertices = vCount;
            if (foundI && iCount >= 3) totalTriangles = iCount / 3;
        }

        string? generator = null;
        if (root.TryGetProperty("asset", out var asset) && asset.TryGetProperty("generator", out var genProp))
        {
            generator = genProp.GetString();
        }

        var rawProps = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(generator)) rawProps["generator"] = generator;
        if (meshCount.HasValue) rawProps["meshes"] = meshCount.Value.ToString();
        if (materialCount.HasValue) rawProps["materials"] = materialCount.Value.ToString();

        return new ModelMetadata(
            Format: format,
            FileSizeBytes: fileLength,
            MeshCount: meshCount,
            VertexCount: totalVertices,
            TriangleCount: totalTriangles,
            MaterialCount: materialCount,
            TextureCount: textureCount,
            AnimationCount: animationCount,
            CameraCount: cameraCount,
            AdapterId: AdapterId,
            AdapterVersion: Version,
            Status: ModelAdapterStatus.Supported,
            RawProperties: rawProps);
    }
}

public sealed class ObjModelPreviewAdapter : IModelPreviewAdapter
{
    public string AdapterId => "builtin-obj";
    public string DisplayName => "Wavefront OBJ Model Adapter";
    public string Version => "1.0.0";
    public int Priority => 90;
    public ModelAdapterCapabilities Capabilities =>
        ModelAdapterCapabilities.Probe |
        ModelAdapterCapabilities.Interactive;

    public bool CanHandle(ModelProbeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var ext = input.Extension ?? Path.GetExtension(input.FilePath);
        return string.Equals(ext, ".obj", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ModelMetadata> ProbeAsync(
        ModelProbeInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!File.Exists(input.FilePath))
        {
            return new ModelMetadata(
                Format: "OBJ",
                Status: ModelAdapterStatus.Degraded,
                AdapterId: AdapterId,
                AdapterVersion: Version,
                Diagnostics: [$"File does not exist: {input.FilePath}"]);
        }

        var fileLength = new FileInfo(input.FilePath).Length;
        long vertexCount = 0;
        long normalCount = 0;
        long texcoordCount = 0;
        long triangleCount = 0;
        var materialNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var reader = new StreamReader(input.FilePath, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

                var trimmed = line.Trim();
                if (trimmed.StartsWith("v ", StringComparison.Ordinal))
                {
                    vertexCount++;
                }
                else if (trimmed.StartsWith("vn ", StringComparison.Ordinal))
                {
                    normalCount++;
                }
                else if (trimmed.StartsWith("vt ", StringComparison.Ordinal))
                {
                    texcoordCount++;
                }
                else if (trimmed.StartsWith("f ", StringComparison.Ordinal))
                {

                    var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var vCountInFace = parts.Length - 1;
                    if (vCountInFace >= 3)
                    {
                        triangleCount += vCountInFace - 2;
                    }
                }
                else if (trimmed.StartsWith("usemtl ", StringComparison.Ordinal))
                {
                    var mtl = trimmed[7..].Trim();
                    if (!string.IsNullOrWhiteSpace(mtl)) materialNames.Add(mtl);
                }
                else if (trimmed.StartsWith("o ", StringComparison.Ordinal) || trimmed.StartsWith("g ", StringComparison.Ordinal))
                {
                    var obj = trimmed[2..].Trim();
                    if (!string.IsNullOrWhiteSpace(obj)) objectNames.Add(obj);
                }
            }

            int? meshCount = objectNames.Count > 0 ? objectNames.Count : (vertexCount > 0 ? 1 : null);
            int? materialCount = materialNames.Count > 0 ? materialNames.Count : null;

            var rawProps = new Dictionary<string, string>
            {
                ["vertices"] = vertexCount.ToString(),
                ["normals"] = normalCount.ToString(),
                ["texcoords"] = texcoordCount.ToString(),
                ["triangles"] = triangleCount.ToString()
            };

            return new ModelMetadata(
                Format: "OBJ",
                FileSizeBytes: fileLength,
                MeshCount: meshCount,
                VertexCount: vertexCount > 0 ? vertexCount : null,
                TriangleCount: triangleCount > 0 ? triangleCount : null,
                MaterialCount: materialCount,
                AdapterId: AdapterId,
                AdapterVersion: Version,
                Status: ModelAdapterStatus.Supported,
                RawProperties: rawProps);
        }
        catch (Exception ex)
        {
            return new ModelMetadata(
                Format: "OBJ",
                FileSizeBytes: fileLength,
                Status: ModelAdapterStatus.Degraded,
                AdapterId: AdapterId,
                AdapterVersion: Version,
                Diagnostics: [$"Error parsing OBJ file: {ex.Message}"]);
        }
    }

    public Task<ModelPreviewDescriptor> GetOrGeneratePreviewAsync(
        ModelPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = new ModelPreviewDescriptor(
            request.MediaId,
            Format: "OBJ",
            HasTurntable: false,
            AdapterId: AdapterId,
            IsInteractive: true);
        return Task.FromResult(descriptor);
    }

    public Task<IModelInteractiveSession?> CreateInteractiveSessionAsync(
        ModelInteractiveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IModelInteractiveSession session = new ModelInteractiveSession(request.MediaId, request.FilePath);
        return Task.FromResult<IModelInteractiveSession?>(session);
    }
}

public sealed class StlModelPreviewAdapter : IModelPreviewAdapter
{
    public string AdapterId => "builtin-stl";
    public string DisplayName => "STL Model Adapter";
    public string Version => "1.0.0";
    public int Priority => 80;
    public ModelAdapterCapabilities Capabilities =>
        ModelAdapterCapabilities.Probe |
        ModelAdapterCapabilities.Interactive;

    public bool CanHandle(ModelProbeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var ext = input.Extension ?? Path.GetExtension(input.FilePath);
        return string.Equals(ext, ".stl", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ModelMetadata> ProbeAsync(
        ModelProbeInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!File.Exists(input.FilePath))
        {
            return new ModelMetadata(
                Format: "STL",
                Status: ModelAdapterStatus.Degraded,
                AdapterId: AdapterId,
                AdapterVersion: Version,
                Diagnostics: [$"File does not exist: {input.FilePath}"]);
        }

        var fileLength = new FileInfo(input.FilePath).Length;

        try
        {

            if (fileLength >= 84)
            {
                await using var fs = File.OpenRead(input.FilePath);
                var headerAndCount = new byte[84];
                if (await fs.ReadAsync(headerAndCount.AsMemory(0, 84), cancellationToken).ConfigureAwait(false) == 84)
                {
                    var triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(headerAndCount.AsSpan(80, 4));

                    if (fileLength == 84L + (triangleCount * 50L))
                    {
                        return new ModelMetadata(
                            Format: "STL",
                            FileSizeBytes: fileLength,
                            MeshCount: 1,
                            VertexCount: triangleCount * 3L,
                            TriangleCount: triangleCount,
                            AdapterId: AdapterId,
                            AdapterVersion: Version,
                            Status: ModelAdapterStatus.Supported,
                            RawProperties: new Dictionary<string, string>
                            {
                                ["type"] = "binary",
                                ["triangles"] = triangleCount.ToString()
                            });
                    }
                }
            }

            using var reader = new StreamReader(input.FilePath, Encoding.ASCII);
            var firstLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (firstLine != null && firstLine.TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase))
            {
                long triangleCount = 0;
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
                {
                    if (line.TrimStart().StartsWith("facet normal", StringComparison.OrdinalIgnoreCase))
                    {
                        triangleCount++;
                    }
                }

                return new ModelMetadata(
                    Format: "STL",
                    FileSizeBytes: fileLength,
                    MeshCount: 1,
                    VertexCount: triangleCount * 3L,
                    TriangleCount: triangleCount,
                    AdapterId: AdapterId,
                    AdapterVersion: Version,
                    Status: ModelAdapterStatus.Supported,
                    RawProperties: new Dictionary<string, string>
                    {
                        ["type"] = "ascii",
                        ["triangles"] = triangleCount.ToString()
                    });
            }

            return new ModelMetadata(
                Format: "STL",
                FileSizeBytes: fileLength,
                Status: ModelAdapterStatus.Degraded,
                AdapterId: AdapterId,
                AdapterVersion: Version,
                Diagnostics: ["STL file is neither standard binary nor valid ASCII solid."]);
        }
        catch (Exception ex)
        {
            return new ModelMetadata(
                Format: "STL",
                FileSizeBytes: fileLength,
                Status: ModelAdapterStatus.Degraded,
                AdapterId: AdapterId,
                AdapterVersion: Version,
                Diagnostics: [$"Error parsing STL file: {ex.Message}"]);
        }
    }

    public Task<ModelPreviewDescriptor> GetOrGeneratePreviewAsync(
        ModelPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = new ModelPreviewDescriptor(
            request.MediaId,
            Format: "STL",
            HasTurntable: false,
            AdapterId: AdapterId,
            IsInteractive: true);
        return Task.FromResult(descriptor);
    }

    public Task<IModelInteractiveSession?> CreateInteractiveSessionAsync(
        ModelInteractiveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IModelInteractiveSession session = new ModelInteractiveSession(request.MediaId, request.FilePath);
        return Task.FromResult<IModelInteractiveSession?>(session);
    }
}

public sealed class F3dModelPreviewAdapter : IModelPreviewAdapter
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gltf", ".glb", ".obj", ".fbx", ".stl", ".step", ".stp", ".ply", ".3mf", ".dae", ".blend"
    };

    private readonly IProcessLauncher _launcher;
    private readonly string? _f3dExecutablePath;
    private readonly Func<string, bool>? _fileExistenceChecker;
    private string? _cachedVersion;

    public string AdapterId => "external-f3d";
    public string DisplayName => "F3D External Tool Adapter";
    public string Version => _cachedVersion ?? "Unknown";
    public int Priority => 50;
    public ModelAdapterCapabilities Capabilities =>
        ModelAdapterCapabilities.Probe |
        ModelAdapterCapabilities.Turntable;

    public F3dModelPreviewAdapter(
        IProcessLauncher? launcher = null,
        string? f3dExecutablePath = null,
        Func<string, bool>? fileExistenceChecker = null)
    {
        _launcher = launcher ?? new BoundedProcessLauncher();
        _f3dExecutablePath = f3dExecutablePath;
        _fileExistenceChecker = fileExistenceChecker;
    }

    private bool CheckFileExists(string path) =>
        _fileExistenceChecker != null ? _fileExistenceChecker(path) : File.Exists(path);

    public bool CanHandle(ModelProbeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var exe = ResolveF3dPath();
        if (string.IsNullOrWhiteSpace(exe)) return false;

        var ext = input.Extension ?? Path.GetExtension(input.FilePath);
        return SupportedExtensions.Contains(ext);
    }

    public async Task<ModelMetadata> ProbeAsync(
        ModelProbeInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ext = input.Extension ?? Path.GetExtension(input.FilePath);
        var format = ext.TrimStart('.').ToUpperInvariant();
        var fileLength = File.Exists(input.FilePath) ? new FileInfo(input.FilePath).Length : input.FileSizeBytes;

        var exe = ResolveF3dPath();
        if (string.IsNullOrWhiteSpace(exe))
        {
            return new ModelMetadata(
                Format: format,
                FileSizeBytes: fileLength,
                Status: ModelAdapterStatus.Degraded,
                AdapterId: AdapterId,
                AdapterVersion: "Unavailable",
                Diagnostics: [$"No approved F3D executable is configured for this deployment. F3D is an optional external tool that {ProductIdentity.DisplayName} does not ship, and it is not synonymous with MODEL."]);
        }

        if (_cachedVersion is null)
        {
            try
            {
                var result = await _launcher.RunAsync(new ProcessRunRequest(
                    ExecutablePath: exe,
                    Arguments: ["--version"],
                    Timeout: TimeSpan.FromSeconds(5)),
                    cancellationToken).ConfigureAwait(false);

                if (result.ExitCode == 0 && !result.TimedOut)
                {
                    var match = Regex.Match(result.StandardOutput, @"F3D\s+v?([\d\.]+)", RegexOptions.IgnoreCase);
                    _cachedVersion = match.Success ? match.Groups[1].Value : result.StandardOutput.Trim();
                }
            }
            catch
            {
                _cachedVersion = "Unknown";
            }
        }

        return new ModelMetadata(
            Format: format,
            FileSizeBytes: fileLength,
            AdapterId: AdapterId,
            AdapterVersion: Version,
            Status: ModelAdapterStatus.Supported,
            Diagnostics: [$"Probed via F3D ({Version})"]);
    }

    public Task<ModelPreviewDescriptor> GetOrGeneratePreviewAsync(
        ModelPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ext = request.Extension ?? Path.GetExtension(request.FilePath);
        var format = ext.TrimStart('.').ToUpperInvariant();

        var descriptor = new ModelPreviewDescriptor(
            request.MediaId,
            Format: format,
            HasTurntable: request.RequestTurntable,
            AdapterId: AdapterId,
            IsInteractive: false);

        return Task.FromResult(descriptor);
    }

    public Task<IModelInteractiveSession?> CreateInteractiveSessionAsync(
        ModelInteractiveRequest request,
        CancellationToken cancellationToken = default)
    {

        return Task.FromResult<IModelInteractiveSession?>(null);
    }

    /// <summary>
    /// This adapter can only work through a deployed F3D executable. When no approved executable is
    /// configured for this deployment it reports Unavailable, so the model surface explains the
    /// limitation instead of presenting an empty preview as a success.
    /// </summary>
    public RuntimeCapabilityStatus DescribeCapability()
    {
        var executablePath = ResolveF3dPath();
        if (executablePath is null)
        {
            return RuntimeCapabilityStatus.Unavailable(
                AdapterId,
                RuntimeCapabilityKind.PreviewAdapter,
                $"{DisplayName} needs an approved F3D executable, which this deployment does not provide.",
                "External model rendering is unavailable; built-in geometry preview is used instead.");
        }

        return RuntimeCapabilityStatus.Available(
            AdapterId,
            RuntimeCapabilityKind.PreviewAdapter,
            $"{DisplayName} {Version} resolved at {executablePath}.",
            executablePath);
    }

    private string? ResolveF3dPath()
    {
        if (string.IsNullOrWhiteSpace(_f3dExecutablePath))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(_f3dExecutablePath))
        {
            return null;
        }

        return CheckFileExists(_f3dExecutablePath) ? _f3dExecutablePath : null;
    }
}

public sealed class FallbackModelPreviewAdapter : IModelPreviewAdapter
{
    public string AdapterId => "generic-fallback";
    public string DisplayName => "Generic 3D Model Fallback Adapter";
    public string Version => "1.0.0";
    public int Priority => 0;
    public ModelAdapterCapabilities Capabilities => ModelAdapterCapabilities.Probe;

    public bool CanHandle(ModelProbeInput input) => true;

    public Task<ModelMetadata> ProbeAsync(
        ModelProbeInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ext = input.Extension ?? Path.GetExtension(input.FilePath);
        var format = !string.IsNullOrWhiteSpace(ext) ? ext.TrimStart('.').ToUpperInvariant() : "3D";
        var fileLength = File.Exists(input.FilePath) ? new FileInfo(input.FilePath).Length : input.FileSizeBytes;

        var metadata = new ModelMetadata(
            Format: format,
            FileSizeBytes: fileLength > 0 ? fileLength : null,
            AdapterId: AdapterId,
            AdapterVersion: Version,
            Status: ModelAdapterStatus.Unsupported,
            Diagnostics:
            [
                $"Unsupported 3D model format '{format}'. Using generic 3D model fallback. Full viewing is available via Open in Default App."
            ]);

        return Task.FromResult(metadata);
    }

    public Task<ModelPreviewDescriptor> GetOrGeneratePreviewAsync(
        ModelPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ext = request.Extension ?? Path.GetExtension(request.FilePath);
        var format = !string.IsNullOrWhiteSpace(ext) ? ext.TrimStart('.').ToUpperInvariant() : "3D";

        var descriptor = new ModelPreviewDescriptor(
            request.MediaId,
            StaticThumbnailPath: null,
            TurntableFramePaths: null,
            Format: format,
            HasTurntable: false,
            AdapterId: AdapterId,
            IsInteractive: false);

        return Task.FromResult(descriptor);
    }

    public Task<IModelInteractiveSession?> CreateInteractiveSessionAsync(
        ModelInteractiveRequest request,
        CancellationToken cancellationToken = default)
    {

        return Task.FromResult<IModelInteractiveSession?>(null);
    }
}
