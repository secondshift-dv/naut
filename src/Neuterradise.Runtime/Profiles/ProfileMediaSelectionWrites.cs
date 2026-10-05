using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Profiles;

/// <summary>Sets first selections for a new Profile from already prepared MediaAssets.</summary>
public sealed class ProfileMediaSelectionWrites
{
    private readonly CatalogDb _catalog;
    private readonly ProfileMediaCandidateReads _candidates;

    public ProfileMediaSelectionWrites(CatalogDb catalog)
    {
        _catalog = catalog;
        _candidates = new ProfileMediaCandidateReads(catalog);
    }

    public async Task<bool> SelectInitialAsync(Guid profileId, CancellationToken ct = default)
    {
        var ranked = await _candidates.ReadAsync(profileId, ct).ConfigureAwait(false);
        var cover = ranked.Covers.FirstOrDefault();
        var banner = ranked.BannerHovers
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.MediaAssetId)
            .FirstOrDefault();
        if (cover is null) return false;

        await using var lease = await _catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);
        await using var command = transaction.CreateCommand("""
            UPDATE profile_appearance
            SET cover_media_asset_id = $coverMedia,
                banner_media_asset_id = $bannerMedia,
                row_version = row_version + 1
            WHERE profile_id = $profile
              AND cover_media_asset_id IS NULL
              AND EXISTS (SELECT 1 FROM profiles
                          WHERE profile_id = $profile AND kind = 'NORMAL');
            """);
        command.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$coverMedia", DbGuid.Format(cover.MediaAssetId));
        command.Parameters.AddWithValue("$bannerMedia", banner is null
            ? DBNull.Value : DbGuid.Format(banner.MediaAssetId));
        var selected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        if (selected)
        {
            await using var source = transaction.CreateCommand("""
                UPDATE profiles
                SET cover_media_id = $coverMedia,
                    row_version = row_version + 1
                WHERE profile_id = $profile AND cover_media_id IS NULL;
                """);
            source.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
            source.Parameters.AddWithValue("$coverMedia", DbGuid.Format(cover.MediaId));
            await source.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty, [profileId], CatalogInvalidationDomain.Appearance, 0));
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return selected;
    }
}
