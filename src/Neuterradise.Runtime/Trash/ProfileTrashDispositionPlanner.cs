using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Trash;

/// <summary>
/// Builds the internal ownership-transition plan for Profile Trash. The user expresses the destructive
/// intent once; the application preserves media automatically. Exactly one other durable Profile
/// relationship is treated as an unambiguous owner candidate. No candidate, or conflicting candidates,
/// falls back to one operation-stable Unknown Profile rather than asking for per-media data entry.
/// </summary>
public sealed class ProfileTrashDispositionPlanner
{
    private readonly CatalogDb _catalog;
    private readonly StorageTokenAllocator _tokenAllocator = new();

    public ProfileTrashDispositionPlanner(CatalogDb catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<IReadOnlyList<ProfileOwnedMediaDisposition>> BuildAsync(
        ProfileTrashPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.OwnedActiveMedias.Count == 0)
        {
            return [];
        }

        var targetByMedia = new Dictionary<Guid, Guid?>();
        var targetProfiles = new HashSet<Guid>();
        var needsUnknown = false;

        await using (var connection = await _catalog.ConnectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var asset in plan.OwnedActiveMedias)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT DISTINCT pa.profile_id
                    FROM profile_media pa
                    JOIN profiles p ON p.profile_id = pa.profile_id
                    WHERE pa.media_id = $assetId
                      AND pa.profile_id <> $sourceProfileId
                      AND pa.relation_type IN ('APPEARS', 'MANUAL')
                      AND p.trashed_at_ms IS NULL
                    ORDER BY pa.profile_id
                    LIMIT 2;
                    """;
                command.Parameters.AddWithValue("$assetId", DbGuid.Format(asset.MediaId));
                command.Parameters.AddWithValue("$sourceProfileId", DbGuid.Format(plan.ProfileId));

                var candidates = new List<Guid>(2);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    candidates.Add(DbGuid.Parse(reader.GetString(0)));
                }

                if (candidates.Count == 1)
                {
                    targetByMedia[asset.MediaId] = candidates[0];
                    targetProfiles.Add(candidates[0]);
                }
                else
                {
                    // Zero candidates means ownership is unresolved. More than one is genuine identity
                    // ambiguity. Both are safe to preserve under Unknown; neither is guessed.
                    targetByMedia[asset.MediaId] = null;
                    needsUnknown = true;
                }
            }
        }

        foreach (var targetProfileId in targetProfiles)
        {
            await _catalog.ProfileWrites.AssignStorageTokenAsync(
                targetProfileId,
                _tokenAllocator.CreateProfileCandidateProvider(targetProfileId),
                cancellationToken).ConfigureAwait(false);
        }

        Guid? unknownProfileId = null;
        if (needsUnknown)
        {
            // The persisted Trash operation id is stable across retries, so using it as the Unknown
            // Profile id makes fallback creation idempotent and prevents retry-created orphan Profiles.
            unknownProfileId = plan.OperationId;
            await _catalog.ProfileWrites.CreateUnknownProfileAsync(unknownProfileId.Value, cancellationToken)
                .ConfigureAwait(false);
            await _catalog.ProfileWrites.AssignStorageTokenAsync(
                unknownProfileId.Value,
                _tokenAllocator.CreateProfileCandidateProvider(unknownProfileId.Value),
                cancellationToken).ConfigureAwait(false);
        }

        return plan.OwnedActiveMedias
            .Select(asset => new ProfileOwnedMediaDisposition(
                asset.MediaId,
                ProfileOwnedMediaDispositionKind.ChangeOwner,
                targetByMedia[asset.MediaId] ?? unknownProfileId
                    ?? throw new InvalidOperationException("Profile Trash ownership planning produced no destination.")))
            .ToArray();
    }
}
