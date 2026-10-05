using Microsoft.Data.Sqlite;
using Neuterradise.App.Faces;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;

namespace Neuterradise.App.RelatedProfiles;

public sealed class RelatedEvidenceProjector
{
    private readonly CatalogDb _catalog;
    private readonly RelatedWrites _writes;
    private readonly TimeProvider _timeProvider;

    public RelatedEvidenceProjector(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _writes = new RelatedWrites(catalog);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<int> ProjectSharedMediaEvidenceForMediaAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            return 0;
        }

        var linkedProfiles = new List<Guid>();

        await using (var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {

            await using var assetCheck = connection.CreateCommand();
            assetCheck.CommandText = "SELECT state, trashed_at_ms FROM media WHERE media_id = $aid;";
            assetCheck.Parameters.AddWithValue("$aid", DbGuid.Format(assetId));
            await using var assetReader = await assetCheck.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await assetReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            var state = DbEnum.ParseMediaState(assetReader.GetString(0));
            var isTrashed = !assetReader.IsDBNull(1);
            if (state != MediaState.Active || isTrashed)
            {
                return 0;
            }

            await using var profCommand = connection.CreateCommand();
            profCommand.CommandText = """
                SELECT DISTINCT p.profile_id
                FROM profile_media pa
                JOIN profiles p ON pa.profile_id = p.profile_id
                WHERE pa.media_id = $aid
                  AND pa.publication_import_unit_id IS NULL
                  AND p.kind = $normalKind
                  AND p.visibility = 'PUBLISHED'
                  AND p.trashed_at_ms IS NULL;
                """;
            profCommand.Parameters.AddWithValue("$aid", DbGuid.Format(assetId));
            profCommand.Parameters.AddWithValue("$normalKind", DbEnum.Format(ProfileKind.Normal));
            await using var profReader = await profCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await profReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                linkedProfiles.Add(DbGuid.Parse(profReader.GetString(0)));
            }
        }

        if (linkedProfiles.Count < 2)
        {
            return 0;
        }

        var count = 0;
        var now = _timeProvider.GetUtcNow();

        for (var i = 0; i < linkedProfiles.Count; i++)
        {
            for (var j = i + 1; j < linkedProfiles.Count; j++)
            {
                var (low, high) = Canonicalize(linkedProfiles[i], linkedProfiles[j]);
                var key = $"SharedMedia|{DbGuid.Format(low)}|{DbGuid.Format(high)}|{DbGuid.Format(assetId)}";

                var evidence = new RelatedEvidencePersistence(
                    key,
                    low,
                    high,
                    RelatedProfileEvidence.SharedMedia,
                    assetId,
                    FaceId: null,
                    now);

                await _writes.PersistEvidenceAsync(evidence, cancellationToken).ConfigureAwait(false);
                await _writes.RebuildSummaryAsync(low, high, now, cancellationToken).ConfigureAwait(false);
                count++;
            }
        }

        return count;
    }

    public async Task<int> RefreshRelatedProjectionsForMediaAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            return 0;
        }

        var pairs = new List<(Guid Low, Guid High)>();

        await using (var connection = await _catalog.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT e.profile_id_low, e.profile_id_high
                FROM related_profile_evidence e
                JOIN profiles low ON low.profile_id = e.profile_id_low
                JOIN profiles high ON high.profile_id = e.profile_id_high
                WHERE e.media_id = $aid
                  AND low.kind = $normalKind AND low.visibility = 'PUBLISHED' AND low.trashed_at_ms IS NULL
                  AND high.kind = $normalKind AND high.visibility = 'PUBLISHED' AND high.trashed_at_ms IS NULL
                ORDER BY e.profile_id_low, e.profile_id_high;
                """;
            command.Parameters.AddWithValue("$aid", DbGuid.Format(assetId));
            command.Parameters.AddWithValue("$normalKind", DbEnum.Format(ProfileKind.Normal));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                pairs.Add((DbGuid.Parse(reader.GetString(0)), DbGuid.Parse(reader.GetString(1))));
            }
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var (low, high) in pairs)
        {
            await _writes.RebuildSummaryAsync(low, high, now, cancellationToken).ConfigureAwait(false);
        }

        return pairs.Count;
    }

    public async Task<int> ProjectConfirmedFaceEvidenceForMediaAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            return 0;
        }

        Guid? ownerProfileId = null;
        var confirmedFaces = new List<(Guid FaceId, Guid ProfileId)>();

        await using (var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {

            await using var assetCheck = connection.CreateCommand();
            assetCheck.CommandText = "SELECT state, trashed_at_ms FROM media WHERE media_id = $aid;";
            assetCheck.Parameters.AddWithValue("$aid", DbGuid.Format(assetId));
            await using var assetReader = await assetCheck.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await assetReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            var state = DbEnum.ParseMediaState(assetReader.GetString(0));
            var isTrashed = !assetReader.IsDBNull(1);
            if (state != MediaState.Active || isTrashed)
            {
                return 0;
            }

            await using var ownerCommand = connection.CreateCommand();
            ownerCommand.CommandText = """
                SELECT p.profile_id
                FROM profile_media pa
                JOIN profiles p ON pa.profile_id = p.profile_id
                WHERE pa.media_id = $aid
                  AND pa.relation_type = $ownerRelation
                  AND pa.publication_import_unit_id IS NULL
                  AND p.kind = $normalKind
                  AND p.visibility = 'PUBLISHED'
                  AND p.trashed_at_ms IS NULL;
                """;
            ownerCommand.Parameters.AddWithValue("$aid", DbGuid.Format(assetId));
            ownerCommand.Parameters.AddWithValue("$ownerRelation", DbEnum.Format(ProfileMediaRelation.Owner));
            ownerCommand.Parameters.AddWithValue("$normalKind", DbEnum.Format(ProfileKind.Normal));
            var ownerObj = await ownerCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (ownerObj is string ownerStr)
            {
                ownerProfileId = DbGuid.Parse(ownerStr);
            }

            await using var faceCommand = connection.CreateCommand();
            faceCommand.CommandText = """
                SELECT fd.face_id, p.profile_id
                FROM face_detections fd
                JOIN identities i ON fd.confirmed_identity_id = i.identity_id
                JOIN profiles p ON i.profile_id = p.profile_id
                WHERE fd.media_id = $aid
                  AND fd.decision_state = $confirmedState
                  AND p.kind = $normalKind
                  AND p.visibility = 'PUBLISHED'
                  AND p.trashed_at_ms IS NULL;
                """;
            faceCommand.Parameters.AddWithValue("$aid", DbGuid.Format(assetId));
            faceCommand.Parameters.AddWithValue("$confirmedState", DbEnum.Format(FaceDecisionState.Confirmed));
            faceCommand.Parameters.AddWithValue("$normalKind", DbEnum.Format(ProfileKind.Normal));
            await using var faceReader = await faceCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await faceReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var faceId = DbGuid.Parse(faceReader.GetString(0));
                var profileId = DbGuid.Parse(faceReader.GetString(1));
                confirmedFaces.Add((faceId, profileId));
            }
        }

        if (confirmedFaces.Count == 0)
        {
            return 0;
        }

        var count = 0;
        var now = _timeProvider.GetUtcNow();

        if (ownerProfileId is not null)
        {
            foreach (var (faceId, faceProfileId) in confirmedFaces)
            {
                if (ownerProfileId.Value == faceProfileId)
                {
                    continue;
                }

                var (low, high) = Canonicalize(ownerProfileId.Value, faceProfileId);
                var key = $"ConfirmedFace|{DbGuid.Format(low)}|{DbGuid.Format(high)}|{DbGuid.Format(assetId)}|{DbGuid.Format(faceId)}";

                var evidence = new RelatedEvidencePersistence(
                    key,
                    low,
                    high,
                    RelatedProfileEvidence.ConfirmedFace,
                    assetId,
                    faceId,
                    now);

                await _writes.PersistEvidenceAsync(evidence, cancellationToken).ConfigureAwait(false);
                await _writes.RebuildSummaryAsync(low, high, now, cancellationToken).ConfigureAwait(false);
                count++;
            }
        }

        for (var i = 0; i < confirmedFaces.Count; i++)
        {
            for (var j = i + 1; j < confirmedFaces.Count; j++)
            {
                var f1 = confirmedFaces[i];
                var f2 = confirmedFaces[j];
                if (f1.ProfileId == f2.ProfileId)
                {
                    continue;
                }

                var (low, high) = Canonicalize(f1.ProfileId, f2.ProfileId);
                var key = $"ConfirmedFace|{DbGuid.Format(low)}|{DbGuid.Format(high)}|{DbGuid.Format(assetId)}|{DbGuid.Format(f1.FaceId)}";

                var evidence = new RelatedEvidencePersistence(
                    key,
                    low,
                    high,
                    RelatedProfileEvidence.ConfirmedFace,
                    assetId,
                    f1.FaceId,
                    now);

                await _writes.PersistEvidenceAsync(evidence, cancellationToken).ConfigureAwait(false);
                await _writes.RebuildSummaryAsync(low, high, now, cancellationToken).ConfigureAwait(false);
                count++;
            }
        }

        return count;
    }

    private static (Guid Low, Guid High) Canonicalize(Guid a, Guid b)
    {
        var strA = DbGuid.Format(a);
        var strB = DbGuid.Format(b);
        return string.CompareOrdinal(strA, strB) < 0 ? (a, b) : (b, a);
    }
}
