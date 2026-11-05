using Microsoft.Data.Sqlite;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Trash;

/// <summary>
/// Exclusive media follows its deleted Profile. Shared media keeps an existing consumer as its
/// storage custodian; deterministic ordering does not assert or change face identity.
/// </summary>
public sealed class ProfileTrashDispositionPlanner(CatalogDb catalog)
{
    public async Task<IReadOnlyList<ProfileOwnedMediaDisposition>> BuildAsync(
        ProfileTrashPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SelectedDispositions.Count > 0) return plan.SelectedDispositions;
        var result = new List<ProfileOwnedMediaDisposition>(plan.OwnedActiveMedias.Count);
        await using var connection = await catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var media in plan.OwnedActiveMedias)
        {
            var consumer = await FindOtherConsumerAsync(connection, media.MediaId, plan.ProfileId, cancellationToken).ConfigureAwait(false);
            if (consumer is { } id)
                await catalog.ProfileWrites.AssignStorageTokenAsync(id,
                    new StorageTokenAllocator().CreateProfileCandidateProvider(id), cancellationToken).ConfigureAwait(false);
            result.Add(new ProfileOwnedMediaDisposition(media.MediaId,
                consumer is null ? ProfileOwnedMediaDispositionKind.TrashMedia : ProfileOwnedMediaDispositionKind.ChangeOwner, consumer));
        }
        return result;
    }

    internal static async Task<Guid?> FindOtherConsumerAsync(SqliteConnection connection, Guid mediaId,
        Guid sourceProfileId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH consumers AS (
                SELECT profile_id FROM profile_media WHERE media_id=$media
                UNION SELECT profile_id FROM profile_appearance WHERE figure_media_id=$media
                    OR cover_media_asset_id IN (SELECT media_asset_id FROM media_assets WHERE media_id=$media)
                    OR banner_media_asset_id IN (SELECT media_asset_id FROM media_assets WHERE media_id=$media)
                UNION SELECT i.profile_id FROM face_detections f JOIN identities i
                    ON i.identity_id=f.confirmed_identity_id OR i.identity_id=f.suggested_identity_id
                    WHERE f.media_id=$media AND i.is_active=1
                UNION SELECT i.profile_id FROM identity_samples s JOIN identities i ON i.identity_id=s.identity_id
                    JOIN face_detections f ON f.face_id=s.face_id WHERE f.media_id=$media AND i.is_active=1
                UNION SELECT profile_id_low FROM related_profile_evidence WHERE media_id=$media
                UNION SELECT profile_id_high FROM related_profile_evidence WHERE media_id=$media
            )
            SELECT p.profile_id FROM consumers c JOIN profiles p ON p.profile_id=c.profile_id
            WHERE p.profile_id<>$source AND p.trashed_at_ms IS NULL ORDER BY p.profile_id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$source", sourceProfileId.ToString("D"));
        command.Parameters.AddWithValue("$media", mediaId.ToString("D"));
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string id ? Guid.Parse(id) : null;
    }
}
