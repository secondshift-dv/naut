using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Media;

/// <summary>
/// Resolves thumbnail, Cover, and import-candidate MediaAssets that still require validated image-resource access.
/// Video HOVER playback consumes its READY MediaAsset path directly.
/// </summary>
public sealed class MediaResourceAuthority
{
    private readonly MediaAssetResourceAuthority _mediaAssets;

    public MediaResourceAuthority(CatalogDb catalog)
    {
        _mediaAssets = new MediaAssetResourceAuthority(catalog ?? throw new ArgumentNullException(nameof(catalog)));
    }

    public Task<MediaAssetResource> ResolveMediaThumbnailAsync(Guid assetId, CancellationToken ct = default) =>
        _mediaAssets.ResolveMediaThumbnailAsync(assetId, ct);

    public Task<MediaAssetResource> ResolveSelectedProfileCoverAsync(Guid profileId, CancellationToken ct = default) =>
        _mediaAssets.ResolveSelectedProfileCoverAsync(profileId, ct);

    public Task<MediaAssetResource> ResolveSelectedProfileBannerThumbnailAsync(
        Guid profileId, Guid bannerMediaAssetId, CancellationToken ct = default) =>
        _mediaAssets.ResolveSelectedProfileBannerThumbnailAsync(profileId, bannerMediaAssetId, ct);

    public Task<MediaAssetResource> ResolveImportCandidateMediaAssetAsync(Guid importUnitId, Guid assetId,
        MediaAssetRole role, CancellationToken ct = default) =>
        _mediaAssets.ResolveImportCandidateAsync(importUnitId, assetId, role, ct);
}