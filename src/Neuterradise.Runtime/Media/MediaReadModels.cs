namespace Neuterradise.App.Media;

public sealed record MediaClues(
    string Who,
    string Where,
    string When,
    string How);

public sealed record ImageMediaInfo(
    string Format,
    int PixelWidth,
    int PixelHeight,
    string? Orientation = null,
    string? ColorSpace = null,
    int? BitDepth = null);

public sealed record VideoMediaInfo(
    string Container,
    TimeSpan Duration,
    int PixelWidth,
    int PixelHeight,
    string VideoCodec,
    double? FrameRate = null,
    string? AudioCodec = null);

public sealed record ModelMediaInfo(
    string Format,
    int? SceneCount = null,
    int? NodeCount = null,
    int? MeshCount = null,
    int? MaterialCount = null,
    int? AnimationCount = null);

public sealed record MediaTileReadModel(
    Guid MediaId,
    MediaType MediaType,
    MediaState State,
    string? StorageToken,
    string? CurrentManagedRelativePath,
    string? CurrentManagedFileName,
    int? Width,
    int? Height,
    int? DurationMs,
    long? ByteLength,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? AddedToLibraryAtUtc,
    Guid? OwnerProfileId,
    string? OwnerDisplayName,
    long RowVersion);

public enum MediaSortOrder
{
    AddedToLibraryDesc,
    AddedToLibraryAsc,
    CreatedAtDesc,
    CreatedAtAsc,
    ByteLengthDesc
}

public sealed record MediaQuery(
    MediaType? MediaType = null,
    MediaState? State = null,
    Guid? OwnerProfileId = null,
    MediaSortOrder SortOrder = MediaSortOrder.AddedToLibraryDesc,
    int PageSize = MediaGridProvider.DefaultPageSize,
    string? ContinuationToken = null);

public sealed record MediaPage(
    IReadOnlyList<MediaTileReadModel> Items,
    string? NextPageToken,
    bool HasMore,
    long TotalCount);

public sealed record MediaOwnerProfile(
    Guid ProfileId,
    string DisplayName,
    string? StorageToken);

public sealed record MediaPersonItem(
    Guid ProfileId,
    string DisplayName,
    string? StorageToken,
    bool IsConfirmed = true,
    double? Confidence = null,
    long? SampledTimestampMs = null,
    string AppearanceBasis = "Confirmed Appearance");

public sealed record MediaLinkedProfileItem(
    Guid ProfileId,
    string DisplayName,
    string? StorageToken);

public sealed record MediaRelationsReadModel(
    MediaOwnerProfile? OwnerProfile,
    IReadOnlyList<MediaPersonItem> PeopleInMedia,
    IReadOnlyList<MediaLinkedProfileItem> LinkedProfiles);

public sealed record MediaFileDetailRow(
    string Label,
    string Value);

public sealed record MediaFileDetailGroup(
    string GroupName,
    IReadOnlyList<MediaFileDetailRow> Rows);

public sealed record MediaDetailReadModel(
    Guid MediaId,
    MediaType MediaType,
    string FileName,
    string OriginalName,
    string CurrentLibraryLocation,
    string? TargetLibraryLocation,
    SystemServices.Database.ManagedPathState ReconciliationState,
    string FileFingerprint,
    long ByteLength,
    MediaState Status,
    DateTimeOffset AddedToLibraryAt,
    MediaClues Clues,
    MediaOwnerProfile? OwnerProfile = null,
    IReadOnlyList<MediaPersonItem>? PeopleInMedia = null,
    IReadOnlyList<MediaLinkedProfileItem>? LinkedProfiles = null,
    object? Info = null,
    IReadOnlyList<MediaFileDetailGroup>? FileDetails = null,
    string? AbsoluteFilePath = null,
    long RowVersion = 0,
    MediaDependencyStatus DependencyStatus = MediaDependencyStatus.SelfContained,
    string? BundleSha256 = null,
    bool IsFavorite = false,
    string? ImportSourcePath = null,
    string? ImportSourceName = null);

public enum MediaRelationFilter
{
    All,
    Owned,
    AppearsIn,
    Manual,
}

public enum MediaTypeFilter
{
    All,
    Images,
    Videos,
    Models,
}

public enum MediaGridSort
{
    NewestFirst,
    OldestFirst,
    NameAscending,
    CapturedNewestFirst,
    SizeLargestFirst,
}

public enum MediaRelationBadge
{
    Owned,
    AppearsIn,
    Manual,
}

public enum MediaGridState
{
    Loading,
    Ready,
    EmptyProfileMedia,
    FilteredNoResults,
    BackgroundUpdating,
    RecoverablePreviewFailure,
    RecoverableQueryError,
}

public sealed record MediaGridQuery(
    Guid ProfileId,
    MediaRelationFilter Relation = MediaRelationFilter.All,
    MediaTypeFilter Type = MediaTypeFilter.All,
    MediaGridSort Sort = MediaGridSort.NewestFirst,
    bool IsFavoriteOnly = false,
    string? Cursor = null,
    int PageSize = MediaGridProvider.DefaultPageSize);

public sealed record MediaGridItem(
    Guid MediaId,
    MediaType MediaType,
    MediaRelationBadge Relation,
    string? CurrentManagedFileName,
    int? PixelWidth,
    int? PixelHeight,
    int? DurationMs,
    bool HasAttention,
    bool IsFavorite = false,
    string? DisplayFileName = null,
    string? ThumbnailPath = null,
    string? HoverPath = null,
    DateTimeOffset? AddedToLibraryAtUtc = null);

public sealed record MediaGridPage(
    IReadOnlyList<MediaGridItem> Items,
    string? NextCursor,
    bool HasMore);
