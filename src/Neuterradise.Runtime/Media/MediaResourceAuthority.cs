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

    internal Task<Dictionary<Guid, ProfileArtwork>> ResolveProfileArtworkAsync(
        IReadOnlyCollection<Guid> profileIds, CancellationToken ct, bool resolveHover = false) =>
        _mediaAssets.ResolveProfileArtworkAsync(profileIds, ct, resolveHover);

    public Task<MediaAssetResource> ResolveSelectedProfileCoverAsync(Guid profileId, CancellationToken ct = default) =>
        _mediaAssets.ResolveSelectedProfileCoverAsync(profileId, ct);

    public Task<MediaAssetResource> ResolveProfileBannerAsync(Guid profileId, Guid hoverAssetId, CancellationToken ct = default) =>
        _mediaAssets.ResolveProfileBannerAsync(profileId, hoverAssetId, ct);

    public async Task<SceneBannerResolution> ResolveSceneBannersAsync(
        Guid profileId, string? primaryPath, Guid? primaryMediaId, IReadOnlyDictionary<int, Guid?> choices, CancellationToken ct = default)
    {
        try
        {
            return await _mediaAssets.ResolveSceneBannersAsync(profileId, primaryPath, primaryMediaId, choices, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            || exception is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 or 10 or 14 })
        {
            ct.ThrowIfCancellationRequested();
            System.Diagnostics.Trace.TraceWarning("Secondary Banner resource temporarily unavailable: {0}", exception.GetType().Name);
            return new(new Dictionary<int, SceneBannerResource>(), SceneBannerResolutionStatus.TemporarilyUnavailable);
        }
    }

    public Task<MediaAssetResource> ResolveImportCandidateMediaAssetAsync(Guid importUnitId, Guid assetId,
        MediaAssetRole role, CancellationToken ct = default) =>
        _mediaAssets.ResolveImportCandidateAsync(importUnitId, assetId, role, ct);
}
