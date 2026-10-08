using Neuterradise.App.Media;

namespace Neuterradise.App.Import.Preparation;

/// <summary>
/// The independent capability tracks one derivative readiness dimension for one asset.
/// READY and NOT_APPLICABLE satisfy readiness. FAILED is terminal for one execution attempt but
/// remains a verification blocker until the user retries recovery or makes another legal decision.
/// A required capability that is not NOT_APPLICABLE must reach READY before publication.
/// </summary>
public enum MediaCapabilityState
{
    Queued,
    Processing,
    Ready,
    NotApplicable,
    Failed,
}

/// <summary>
/// The independent capability dimensions that media preparation tracks per asset.
/// Applicability is deterministic per <see cref="MediaType"/> and never hides a failure behind NOT_APPLICABLE.
/// </summary>
public enum MediaCapability
{
    ManagedOriginal,
    Metadata,
    Thumbnail,
    Hover,
    ModelRender,
    FaceDetection,
    FaceEmbedding,
    SearchProjection,
    SimilarityRelated,
}

/// <summary>
/// Deterministic applicability: which capabilities are required or applicable for a given media type.
/// </summary>
public static class CapabilityApplicability
{
    /// <summary>
    /// Returns all capabilities (required + optional applicable) with their required flag.
    /// Used by capability seeding to create rows for all applicable capabilities.
    /// </summary>
    public static IReadOnlyList<(MediaCapability Capability, bool Required)> GetAll(MediaType mediaType) => mediaType switch
    {
        MediaType.Image =>
        [
            (MediaCapability.ManagedOriginal, true),
            (MediaCapability.Metadata, true),
            (MediaCapability.Thumbnail, true),
            (MediaCapability.FaceDetection, false),
            (MediaCapability.FaceEmbedding, false),
            (MediaCapability.SearchProjection, false),
            (MediaCapability.SimilarityRelated, false),
        ],
        MediaType.Video =>
        [
            (MediaCapability.ManagedOriginal, true),
            (MediaCapability.Metadata, true),
            (MediaCapability.Thumbnail, true),
            (MediaCapability.Hover, true),
            (MediaCapability.FaceDetection, false),
            (MediaCapability.FaceEmbedding, false),
            (MediaCapability.SearchProjection, false),
            (MediaCapability.SimilarityRelated, false),
        ],
        MediaType.Model =>
        [
            (MediaCapability.ManagedOriginal, true),
            (MediaCapability.Metadata, true),
            (MediaCapability.Thumbnail, true),
            (MediaCapability.ModelRender, true),
        ],
        _ =>
        [
            (MediaCapability.ManagedOriginal, true),
            (MediaCapability.Metadata, true),
        ],
    };

    /// <summary>
    /// Returns only the required capabilities for the given media type.
    /// Used by legacy seeding that only creates rows for required capabilities.
    /// </summary>
    public static IReadOnlyList<MediaCapability> GetRequired(MediaType mediaType) =>
        GetAll(mediaType).Where(x => x.Required).Select(x => x.Capability).ToList();

    // Candidate preparation cannot wait for derivatives that only exist after domain commit.
    public static IReadOnlyList<(MediaCapability Capability, bool Required)> GetForImportPhase(
        MediaType mediaType, bool domainCommitted) => domainCommitted
        ? GetAll(mediaType)
        : GetAll(mediaType).Where(x => x.Capability is MediaCapability.Metadata
            or MediaCapability.FaceDetection or MediaCapability.FaceEmbedding
            or MediaCapability.SimilarityRelated).ToArray();

    public static IReadOnlyList<MediaCapability> GetRequiredForImportPhase(
        MediaType mediaType, bool domainCommitted) => GetForImportPhase(mediaType, domainCommitted)
            .Where(x => x.Required).Select(x => x.Capability).ToArray();

    /// <summary>
    /// Whether FaceEmbedding is applicable. It is only applicable when FaceDetection produces
    /// at least one face candidate. This is evaluated dynamically after FaceDetection completes.
    /// </summary>
    public static bool IsFaceEmbeddingApplicable(MediaType mediaType) =>
        mediaType is MediaType.Image or MediaType.Video;
}
