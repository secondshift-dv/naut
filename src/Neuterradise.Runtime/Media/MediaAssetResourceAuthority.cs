using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Media;

public enum MediaAssetResourceState { Ready, Missing, NeedsRepair }

public sealed record MediaAssetResource(Guid MediaAssetId, MediaAssetResourceState State, string? PhysicalPath);
public sealed record SceneBannerResource(Guid MediaId, string VideoPath, string? PosterPath);
public enum SceneBannerResolutionStatus { Resolved, Incomplete, TemporarilyUnavailable }
public sealed record SceneBannerResolution(IReadOnlyDictionary<int, SceneBannerResource> Resources, SceneBannerResolutionStatus Status)
{
    public bool RetryRequired => Status != SceneBannerResolutionStatus.Resolved;
    public bool HasSnapshot => Status != SceneBannerResolutionStatus.TemporarilyUnavailable;
}

/// <summary>Resolves catalog-owned MediaAsset identity without generating media or exposing Candidate output.</summary>
public sealed class MediaAssetResourceAuthority
{
    private readonly CatalogDb _catalog;
    private readonly MediaAssetReads _reads;

    public MediaAssetResourceAuthority(CatalogDb catalog)
    {
        _catalog = catalog;
        _reads = catalog.MediaAssetReads;
    }

    public async Task<MediaAssetResource> ResolveMediaThumbnailAsync(Guid assetId, CancellationToken ct = default) =>
        await ResolveMediaAssetAsync(await _reads.GetPublicMediaAssetAsync(assetId, MediaAssetRole.Thumbnail, ct)
            .ConfigureAwait(false), ct).ConfigureAwait(false);

    internal async Task<Dictionary<Guid, ProfileArtwork>> ResolveProfileArtworkAsync(
        IReadOnlyCollection<Guid> profileIds, CancellationToken ct, bool resolveHover)
    {
        var assets = await _reads.GetPublicProfileArtworkAsync(profileIds, ct).ConfigureAwait(false);
        var result = new Dictionary<Guid, ProfileArtwork>(assets.Count);
        foreach (var (profileId, artwork) in assets)
        {
            ct.ThrowIfCancellationRequested();
            var cover = ResolveArtworkAsset(artwork.Cover);
            var poster = ResolveArtworkAsset(artwork.BannerPoster);
            var hoverPath = resolveHover ? ResolveArtworkAsset(artwork.ReadyHover).PhysicalPath : null;
            result.Add(profileId, new ProfileArtwork(cover.PhysicalPath, poster.PhysicalPath, artwork.HoverId, hoverPath,
                cover.State == MediaAssetResourceState.NeedsRepair, poster.State == MediaAssetResourceState.NeedsRepair));
        }
        return result;
    }

    private MediaAssetResource ResolveArtworkAsset(ArtworkAsset? asset) => asset is null
        ? Missing(Guid.Empty)
        : ResolveFile(asset.Id, asset.RelativePath, asset.ByteLength);

    public async Task<MediaAssetResource> ResolveImportCandidateAsync(Guid importUnitId, Guid assetId,
        MediaAssetRole role, CancellationToken ct = default) =>
        await ResolveMediaAssetAsync(await _reads.GetImportCandidateAsync(importUnitId, assetId, role, ct)
            .ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task<MediaAssetResource> ResolveSelectedProfileCoverAsync(Guid profileId,
        CancellationToken ct = default)
    {
        var artwork = await _reads.GetPublicProfileArtworkAsync([profileId], ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return ResolveArtworkAsset(artwork.GetValueOrDefault(profileId)?.Cover);
    }

    public async Task<MediaAssetResource> ResolveProfileBannerAsync(Guid profileId, Guid hoverAssetId, CancellationToken ct = default)
    {
        var hover = await _reads.GetPublicSelectedMediaAsync(profileId, hoverAssetId, ct).ConfigureAwait(false);
        if (hover?.Role != MediaAssetRole.Hover) return Missing(hoverAssetId);
        var rendition = await new Neuterradise.App.Profiles.ProfileBannerOperations(_catalog)
            .ResolveRenditionAsync(profileId, hover.MediaId, ct: ct).ConfigureAwait(false);
        return await ResolveMediaAssetAsync(rendition, ct).ConfigureAwait(false);
    }

    // Resolve only on demand for a visible authored multi-banner scene. Use existing ordered, prepared
    // collection entries and the same rendition readiness/containment authority as the primary Banner.
    public async Task<SceneBannerResolution> ResolveSceneBannersAsync(
        Guid profileId, string? primaryPath, Guid? primaryMediaId, IReadOnlyDictionary<int, Guid?> choices,
        CancellationToken ct = default)
    {
        var results = new Dictionary<int, SceneBannerResource>();
        if (choices.Count == 0 || choices.Keys.Any(index => index <= 0 || index >= Presentation.PresentationCompiler.MaxBackdropLayers))
            return new(results, SceneBannerResolutionStatus.Resolved);
        var selection = await _reads.GetPublicSelectionAsync(profileId, ct).ConfigureAwait(false);
        if (selection is null) return new(results, SceneBannerResolutionStatus.Resolved);
        if (primaryMediaId is null && selection.BannerMediaAssetId is { } selected)
            primaryMediaId = (await _reads.GetPublicSelectedMediaAsync(profileId, selected, ct).ConfigureAwait(false))?.MediaId;
        var operations = new Neuterradise.App.Profiles.ProfileBannerOperations(_catalog);
        var entries = await operations.ReadEntriesAsync(profileId, ct).ConfigureAwait(false);
        var cache = new Dictionary<Guid, SceneBannerResource?>();
        var retryRequired = false;
        async Task<SceneBannerResource?> ResolveAsync(Guid id)
        {
            ct.ThrowIfCancellationRequested();
            if (cache.TryGetValue(id, out var cached)) return cached;
            if (!entries.Any(entry => entry.MediaId == id)) return cache[id] = null;
            var asset = await operations.ResolveRenditionAsync(profileId, id, ct: ct).ConfigureAwait(false);
            var resource = await ResolveMediaAssetAsync(asset, ct).ConfigureAwait(false);
            if (resource.State != MediaAssetResourceState.Ready || resource.PhysicalPath is not { } path)
            {
                // Membership exists, but readiness or the physical file may be temporarily unavailable.
                retryRequired = true;
                return cache[id] = null;
            }
            var poster = await ResolveMediaThumbnailAsync(id, ct).ConfigureAwait(false);
            retryRequired |= poster.State == MediaAssetResourceState.NeedsRepair;
            return cache[id] = new(id, path, poster.PhysicalPath);
        }

        var fallbackCount = choices.Where(pair => pair.Value is null).Select(pair => pair.Key).DefaultIfEmpty(0).Max();
        var ordered = new List<SceneBannerResource>(fallbackCount);
        if (fallbackCount > 0)
            foreach (var entry in entries)
            {
                if (entry.MediaId == primaryMediaId) continue;
                if (await ResolveAsync(entry.MediaId).ConfigureAwait(false) is not { } resource
                    || string.Equals(resource.VideoPath, primaryPath, StringComparison.OrdinalIgnoreCase)
                    || ordered.Any(previous => string.Equals(previous.VideoPath, resource.VideoPath, StringComparison.OrdinalIgnoreCase))) continue;
                ordered.Add(resource);
                if (ordered.Count == fallbackCount) break;
            }
        foreach (var (index, id) in choices)
        {
            var resource = id is { } mediaId ? await ResolveAsync(mediaId).ConfigureAwait(false)
                : index <= ordered.Count ? ordered[index - 1] : null;
            if (resource is not null) results[index] = resource;
        }
        return new(results, retryRequired ? SceneBannerResolutionStatus.Incomplete : SceneBannerResolutionStatus.Resolved);
    }

    private Task<MediaAssetResource> ResolveMediaAssetAsync(MediaAssetRecord? mediaAsset, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (mediaAsset is null) return Task.FromResult(Missing(Guid.Empty));
        return Task.FromResult(ResolveFile(mediaAsset.MediaAssetId, mediaAsset.RelativePath, mediaAsset.ByteLength));
    }

    private MediaAssetResource ResolveFile(Guid id, string relativePath, long byteLength)
    {
        try
        {
            var path = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, relativePath);
            var file = new FileInfo(path);
            return file.Exists && file.Length == byteLength
                ? new MediaAssetResource(id, MediaAssetResourceState.Ready, path)
                : Repair(id);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Repair(id);
        }
    }

    private static MediaAssetResource Missing(Guid id) => new(id, MediaAssetResourceState.Missing, null);
    private static MediaAssetResource Repair(Guid id) => new(id, MediaAssetResourceState.NeedsRepair, null);
}

internal sealed record ProfileArtwork(string? CoverPath, string? BannerPosterPath, Guid? HoverAssetId, string? HoverPath,
    bool CoverNeedsRepair, bool PosterNeedsRepair);
