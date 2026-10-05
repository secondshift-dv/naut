using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Profiles;

public sealed record ProfileHeaderReadModel(
    Guid ProfileId,
    ProfileKind Kind,
    string DisplayName,
    string? StorageToken,
    string? CategoryId,
    string? CategoryName,
    IReadOnlyList<string> Tags,
    int? Rating,
    bool IsFavorite,
    string? Overview,
    string? Notes,
    Guid? IdentityId,
    Guid? CoverMediaId,
    Guid? BannerMediaId,
    long? UnknownSequence,
    long ActiveOwnedMediaCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? TrashedAtUtc,
    long RowVersion);

public sealed record ProfileFolderReadModel(
    Guid ProfileId,
    ProfileKind Kind,
    string DisplayName,
    string? StorageToken,
    string? CurrentManagedRelativePath,
    string? TargetManagedRelativePath,
    ManagedPathState PathState,
    string? ReconciliationOperationId,
    long RowVersion);

public sealed record ProfileDetailReadModel(
    Guid ProfileId,
    ProfileKind Kind,
    string DisplayName,
    string? StorageToken,
    string? CategoryId,
    string? CategoryName,
    IReadOnlyList<string> Tags,
    int? Rating,
    bool IsFavorite,
    string? Overview,
    string? Notes,
    Guid? IdentityId,
    int IdentitySampleCount,
    Guid? CoverMediaId,
    Guid? BannerMediaId,
    long? UnknownSequence,
    long ActiveOwnedMediaCount,
    string? LayoutPresetId,
    string? AppearanceOverridesJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? TrashedAtUtc,
    long RowVersion,
    IReadOnlyList<ProfileTagAssignment>? TagAssignments = null,
    MediaType? BannerMediaType = null,
    Guid? CoverMediaAssetId = null,
    Guid? BannerMediaAssetId = null,
    string? CoverAssetRelativePath = null,
    string? BannerAssetRelativePath = null,
    Guid? FigureMediaId = null, string? FigureThumbnailRelativePath = null,
    string? FigureModelRenderRelativePath = null, MediaAssetState? FigureModelRenderState = null,
    string? FigureSourceSha256 = null)
{
    public IReadOnlyList<ProfileTagAssignment> AssignedTags => TagAssignments ?? [];
}

public sealed record ProfileTagAssignment(string TagId, string DisplayName);

public sealed record ProfileAppearanceReadModel(
    Guid ProfileId,
    long ProfileRowVersion,
    string? LayoutPresetId,
    string? AppearanceOverridesJson,
    Guid? CoverMediaId,
    Guid? BannerMediaId,
    MediaType? BannerMediaType,
    Guid? CoverMediaAssetId,
    string? CoverAssetRelativePath,
    Guid? BannerMediaAssetId,
    string? BannerAssetRelativePath,
    Guid? FigureMediaId = null, string? FigureThumbnailRelativePath = null,
    string? FigureModelRenderRelativePath = null, MediaAssetState? FigureModelRenderState = null,
    string? FigureSourceSha256 = null);

public sealed record ProfileMediaPage(
    IReadOnlyList<ProfileMediaItemReadModel> Items,
    string? NextPageToken,
    bool HasMore,
    int TotalCount = 0);

public sealed record ProfileMediaItemReadModel(
    Guid MediaId,
    MediaType MediaType,
    ProfileMediaRelation RelationType,
    string? ManagedRelativePath,
    string? ManagedFileName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? AddedToLibraryAtUtc,
    int? PixelWidth = null,
    int? PixelHeight = null,
    int? DurationMs = null,
    ManagedPathState PathState = ManagedPathState.None,
    string? ContentFingerprint = null,
    bool IsFavorite = false,
    string? OriginalFileName = null,
    string? ThumbnailRelativePath = null,
    string? HoverRelativePath = null);

public enum ProfileMediaFilter
{
    All,
    Owned,
    AppearsIn,
    Manual
}

public sealed record ProfileMediaRelationEntry(
    Guid ProfileId,
    Guid MediaId,
    ProfileMediaRelation RelationType,
    DateTimeOffset CreatedAtUtc,
    string? ProvenanceKey = null);

public sealed record UnknownProfileSummary(
    Guid ProfileId,
    long UnknownSequence,
    string DerivedLabel,
    string? StorageToken,
    long ActiveOwnedMediaCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? TrashedAtUtc,
    long RowVersion);

public sealed record ProfileFigureCandidate(Guid MediaId, string DisplayName, long ByteLength,
    string? ThumbnailRelativePath, string? ModelRenderRelativePath, MediaAssetState? ModelRenderState,
    int? ModelRenderContractVersion, string SourceSha256, string? EligibilityReason, bool IsSelectable);
public sealed record ProfileFigurePrewarmCandidate(
    Guid MediaId,
    string ModelRenderRelativePath,
    string SourceSha256);
