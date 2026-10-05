using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.MediaTools;

/// <summary>
/// Well-known capability identifiers the application asks about by name. Feature surfaces use these
/// instead of repeating literal artifact ids.
/// </summary>
public static class RuntimeCapabilityIds
{
    public const string Ffprobe = "ffprobe";

    public const string Ffmpeg = "ffmpeg";

    public const string FaceDetectionModel = "yunet";

    public const string FaceRecognitionModel = "sface";
}

/// <summary>
/// The truthful capability picture for one runtime session. Missing tools, missing model bytes and
/// integrity failures are feature-scoped facts: they disable the features that need them and are
/// reported with a concrete reason, but they never block startup and never become a fake success.
/// </summary>
public sealed class RuntimeCapabilitySnapshot
{
    private readonly Dictionary<string, RuntimeCapabilityStatus> _byId;

    private RuntimeCapabilitySnapshot(Dictionary<string, RuntimeCapabilityStatus> byId)
    {
        _byId = byId;
        Statuses = [.. byId.Values];
    }

    public IReadOnlyList<RuntimeCapabilityStatus> Statuses { get; }

    /// <summary>
    /// Builds the snapshot from the deployment manifest, the tool resolver and the owned preview
    /// adapter registry. A deployment without a readable manifest still produces a complete snapshot,
    /// with every artifact-backed capability reported unavailable.
    /// </summary>
    public static RuntimeCapabilitySnapshot Create(
        InstallPaths install,
        ExternalToolResolver toolResolver,
        IReadOnlyList<RuntimeCapabilityStatus> previewAdapterStatuses)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(toolResolver);
        ArgumentNullException.ThrowIfNull(previewAdapterStatuses);

        var byId = new Dictionary<string, RuntimeCapabilityStatus>(StringComparer.Ordinal);

        foreach (var toolId in new[] { RuntimeCapabilityIds.Ffprobe, RuntimeCapabilityIds.Ffmpeg })
        {
            byId[toolId] = toolResolver.Resolve(toolId);
        }

        var catalog = DeploymentArtifactCatalog.TryLoad(install);
        foreach (var modelId in new[]
                 {
                     RuntimeCapabilityIds.FaceDetectionModel,
                     RuntimeCapabilityIds.FaceRecognitionModel,
                 })
        {
            byId[modelId] = catalog is null
                ? RuntimeCapabilityStatus.Unavailable(
                    modelId,
                    RuntimeCapabilityKind.Model,
                    $"This deployment has no readable artifact manifest, so the '{modelId}' model cannot be resolved.",
                    "Face detection and identity matching are unavailable.")
                : catalog.DescribeModel(modelId, install.RuntimeRoot);
        }

        foreach (var adapterStatus in previewAdapterStatuses)
        {
            byId[adapterStatus.CapabilityId] = adapterStatus;
        }

        return new RuntimeCapabilitySnapshot(byId);
    }

    /// <summary>
    /// The recorded status for a capability. An id that was never probed is reported as unavailable
    /// rather than silently treated as usable.
    /// </summary>
    public RuntimeCapabilityStatus Get(string capabilityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);

        return _byId.TryGetValue(capabilityId, out var status)
            ? status
            : RuntimeCapabilityStatus.Unavailable(
                capabilityId,
                RuntimeCapabilityKind.Tool,
                $"No runtime capability named '{capabilityId}' was probed for this session.");
    }

    public bool IsUsable(string capabilityId) => Get(capabilityId).IsUsable;

    /// <summary>
    /// Capabilities that are not usable, for a settings or health surface that explains what this
    /// deployment cannot do and why.
    /// </summary>
    public IReadOnlyList<RuntimeCapabilityStatus> Limitations =>
        [.. Statuses.Where(status => !status.IsUsable)];

    /// <summary>
    /// True when at least one capability failed its integrity check. Integrity failures are never
    /// silently downgraded to "missing".
    /// </summary>
    public bool HasIntegrityFailure =>
        Statuses.Any(status => status.State == RuntimeCapabilityState.IntegrityFailed);

    /// <summary>
    /// Face analysis needs both the detection and the recognition model.
    /// </summary>
    public bool IsFaceAnalysisAvailable =>
        IsUsable(RuntimeCapabilityIds.FaceDetectionModel)
        && IsUsable(RuntimeCapabilityIds.FaceRecognitionModel);

    /// <summary>
    /// Complete video import tooling needs ffprobe for technical metadata and ffmpeg for required
    /// preview derivatives. Reporting the combined capability as available requires both tools.
    /// </summary>
    public bool IsVideoToolingAvailable =>
        IsUsable(RuntimeCapabilityIds.Ffprobe)
        && IsUsable(RuntimeCapabilityIds.Ffmpeg);
}
