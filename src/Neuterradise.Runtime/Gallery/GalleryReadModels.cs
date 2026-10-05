namespace Neuterradise.App.Gallery;

using Neuterradise.App.Media;
using Neuterradise.App.Profiles;

public enum GallerySortOrder
{
    DisplayNameAsc,
    DisplayNameDesc,
    UpdatedAtDesc,
    UpdatedAtAsc,
    RatingDesc,
    AddedToLibraryDesc,
    RatingAsc,
    MediaCountDesc
}

public enum GalleryProfileKindFilter
{
    NormalOnly,
    UnknownOnly,
    All
}

public static class GalleryPageSizes
{
    public const int Small = 24;
    public const int Medium = 48;
    public const int Large = 96;

    public const int Default = Medium;

    public static readonly IReadOnlyList<int> Allowed = [Small, Medium, Large];

    public static int Normalize(int requested) =>
        Allowed.Contains(requested) ? requested : Default;
}

public sealed record GalleryQuery(
    string? SearchText = null,
    string? CategoryId = null,
    string? TagId = null,
    bool? IsFavorite = null,
    int? MinRating = null,
    bool? HasMedia = null,
    bool? HasRelatedEvidence = null,
    GallerySortOrder SortOrder = GallerySortOrder.DisplayNameAsc,
    int PageSize = GalleryPageSizes.Default,
    int PageIndex = 1,
    bool? HasImages = null,
    bool? HasVideos = null,
    bool? HasModels = null,
    int? MaxRating = null,
    Guid? RelatedToProfileId = null,
    bool? HasSharedMedia = null,
    GalleryProfileKindFilter KindFilter = GalleryProfileKindFilter.All);

public sealed record GalleryProfileSummary(
    Guid ProfileId,
    ProfileKind Kind,
    string DisplayName,
    string? StorageToken,
    string? CategoryId,
    string? CategoryName,
    IReadOnlyList<string> Tags,
    int? Rating,
    bool IsFavorite,
    Guid? CoverMediaId,
    Guid? BannerMediaId,
    long ActiveOwnedMediaCount,
    int RelatedProfileCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long RowVersion,
    string? ProfileCardVariantId = null,
    ProfileAppearanceOverrides? Appearance = null,
    string? CoverContentFingerprint = null,
    string? BannerContentFingerprint = null,
    MediaType? BannerMediaType = null,
    string? BannerAssetRelativePath = null,
    Guid? BannerMediaAssetId = null)
{

    public ProfileAppearanceOverrides EffectiveAppearance =>
        Appearance ?? ProfileAppearanceOverrides.Default;

    public long? CoverThumbnailTimestampMilliseconds => EffectiveAppearance.CoverThumbnailTimestampMilliseconds;

    public long? BannerThumbnailTimestampMilliseconds => null;

    public bool HasVideoBanner =>
        BannerMediaType == MediaType.Video
        && BannerMediaAssetId.HasValue
        && !string.IsNullOrWhiteSpace(BannerAssetRelativePath);
}

public sealed record GalleryPage(
    IReadOnlyList<GalleryProfileSummary> Items,
    int PageIndex,
    int PageSize,
    long TotalCount)
{
    public int TotalPages => TotalCount <= 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);

    public bool HasPrevious => PageIndex > 1;

    public bool HasNext => PageIndex < TotalPages;

    public long RangeStart => TotalCount == 0 ? 0 : ((long)(PageIndex - 1) * PageSize) + 1;

    public long RangeEnd => TotalCount == 0 ? 0 : Math.Min((long)PageIndex * PageSize, TotalCount);
}

public enum GallerySuggestionKind
{
    Profile,
    Category,
    Tag
}

public sealed record GallerySearchSuggestion(
    GallerySuggestionKind Kind,
    string Text,
    string? Id = null,
    Guid? ProfileId = null);

public sealed record GalleryFilterOption(string Id, string Name, long ProfileCount);

public sealed record GalleryFacets(
    IReadOnlyList<GalleryFilterOption> Categories,
    IReadOnlyList<GalleryFilterOption> Tags);
