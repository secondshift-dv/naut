using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Media;

public enum MediaAssetResourceState { Ready, Missing, NeedsRepair }

public sealed record MediaAssetResource(Guid MediaAssetId, MediaAssetResourceState State, string? PhysicalPath);

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

    public async Task<MediaAssetResource> ResolveImportCandidateAsync(Guid importUnitId, Guid assetId,
        MediaAssetRole role, CancellationToken ct = default) =>
        await ResolveMediaAssetAsync(await _reads.GetImportCandidateAsync(importUnitId, assetId, role, ct)
            .ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task<MediaAssetResource> ResolveSelectedProfileCoverAsync(Guid profileId,
        CancellationToken ct = default)
    {
        var selection = await _reads.GetPublicSelectionAsync(profileId, ct).ConfigureAwait(false);
        return selection?.CoverMediaAssetId is { } id
            ? await ResolveSelectedMediaAssetAsync(profileId, id, ct).ConfigureAwait(false)
            : Missing(Guid.Empty);
    }

    private async Task<MediaAssetResource> ResolveSelectedMediaAssetAsync(Guid profileId, Guid mediaAssetId,
        CancellationToken ct)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ma.media_id, ma.role FROM media_assets ma
            JOIN media m ON m.media_id = ma.media_id
            JOIN profile_media pm ON pm.media_id = m.media_id
            JOIN profiles p ON p.profile_id = pm.profile_id
            WHERE ma.media_asset_id = $asset AND ma.state = 'READY'
              AND ma.role = 'THUMBNAIL'
              AND m.state = 'ACTIVE' AND pm.profile_id = $profile
              AND pm.publication_import_unit_id IS NULL
              AND p.visibility = 'PUBLISHED' AND p.trashed_at_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$asset", DbGuid.Format(mediaAssetId));
        command.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Missing(mediaAssetId);
        var mediaId = DbGuid.Parse(reader.GetString(0));
        var role = DbEnum.ParseMediaAssetRole(reader.GetString(1));
        await reader.DisposeAsync().ConfigureAwait(false);
        var mediaAsset = await _reads.GetPublicMediaAssetAsync(mediaId, role, ct).ConfigureAwait(false);
        return mediaAsset?.MediaAssetId == mediaAssetId
            ? await ResolveMediaAssetAsync(mediaAsset, ct).ConfigureAwait(false) : Missing(mediaAssetId);
    }

    private Task<MediaAssetResource> ResolveMediaAssetAsync(MediaAssetRecord? mediaAsset, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (mediaAsset is null) return Task.FromResult(Missing(Guid.Empty));
        try
        {
            var path = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, mediaAsset.RelativePath);
            var valid = File.Exists(path) && new FileInfo(path).Length == mediaAsset.ByteLength;
            return Task.FromResult(valid
                ? new MediaAssetResource(mediaAsset.MediaAssetId, MediaAssetResourceState.Ready, path)
                : Repair(mediaAsset.MediaAssetId));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Repair(mediaAsset.MediaAssetId));
        }
    }

    private static MediaAssetResource Missing(Guid id) => new(id, MediaAssetResourceState.Missing, null);
    private static MediaAssetResource Repair(Guid id) => new(id, MediaAssetResourceState.NeedsRepair, null);
}
