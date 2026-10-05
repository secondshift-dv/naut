using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neuterradise.App.SystemServices.MediaTools;

namespace Neuterradise.App.Media.Model;

[Flags]
public enum ModelAdapterCapabilities
{
    None = 0,
    Probe = 1 << 0,
    StaticThumbnail = 1 << 1,
    Turntable = 1 << 2,
    Interactive = 1 << 3
}

public enum ModelAdapterStatus
{
    Supported,
    Degraded,
    Unsupported
}

public sealed record ModelProbeInput(
    string FilePath,
    string? Extension = null,
    long FileSizeBytes = 0,
    Stream? Stream = null);

public sealed record ModelPreviewRequest(
    Guid MediaId,
    string FilePath,
    string? OutputDirectory = null,
    int TargetWidth = 512,
    int TargetHeight = 512,
    bool RequestTurntable = false,
    string? Extension = null);

public sealed record ModelInteractiveRequest(
    Guid MediaId,
    string FilePath);

public sealed record ModelMetadata(
    string? Format = null,
    long? FileSizeBytes = null,
    int? MeshCount = null,
    long? VertexCount = null,
    long? TriangleCount = null,
    int? MaterialCount = null,
    int? TextureCount = null,
    int? AnimationCount = null,
    int? CameraCount = null,
    int? LightCount = null,
    string? Bounds = null,
    string? Units = null,
    string? AdapterId = null,
    string? AdapterVersion = null,
    ModelAdapterStatus Status = ModelAdapterStatus.Supported,
    IReadOnlyList<string>? Diagnostics = null,
    IReadOnlyDictionary<string, string>? RawProperties = null)
{
    public static readonly ModelMetadata Empty = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ModelMetadata FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            return JsonSerializer.Deserialize<ModelMetadata>(json, JsonOptions) ?? Empty;
        }
        catch
        {
            return Empty;
        }
    }
}

public interface IModelInteractiveSession : IDisposable
{
    Guid MediaId { get; }
    string FilePath { get; }
    double CurrentYaw { get; }
    double CurrentPitch { get; }
    double CurrentZoom { get; }
    bool IsDisposed { get; }

    void Rotate(double deltaYaw, double deltaPitch);
    void Zoom(double factor);
    void ResetCamera();
}

public interface IModelPreviewAdapter
{
    string AdapterId { get; }
    string DisplayName { get; }
    string Version { get; }
    int Priority { get; }
    ModelAdapterCapabilities Capabilities { get; }

    bool CanHandle(ModelProbeInput input);
    Task<ModelMetadata> ProbeAsync(ModelProbeInput input, CancellationToken cancellationToken = default);
    Task<ModelPreviewDescriptor> GetOrGeneratePreviewAsync(ModelPreviewRequest request, CancellationToken cancellationToken = default);
    Task<IModelInteractiveSession?> CreateInteractiveSessionAsync(ModelInteractiveRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Truthful capability status for this adapter, expressed in the single runtime capability state
    /// model shared with deployed tools and models. Adapters implemented entirely in managed code are
    /// available by construction; adapters that depend on an external executable must override this
    /// and report Unavailable when that dependency is not deployed.
    /// </summary>
    RuntimeCapabilityStatus DescribeCapability() =>
        RuntimeCapabilityStatus.Available(
            AdapterId,
            RuntimeCapabilityKind.PreviewAdapter,
            $"{DisplayName} {Version} is built into this application.");
}
