using Microsoft.Data.Sqlite;
using Neuterradise.App.RelatedProfiles;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed class RelatedReads
{
    private readonly CatalogConnectionFactory _connectionFactory;

    public RelatedReads(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
    }

    public RelatedReads(CatalogConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<IReadOnlyList<RelatedProfileSummaryReadModel>> GetRelatedSummariesForProfileAsync(
        Guid profileId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            return [];
        }

        limit = Math.Clamp(limit, 1, 500);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT rps.profile_id_low, rps.profile_id_high,
                   CASE WHEN rps.profile_id_low = $profileId THEN rps.profile_id_high ELSE rps.profile_id_low END AS related_id,
                   coalesce(p.display_name, 'Unknown') AS related_name,
                   rps.shared_media_count, rps.confirmed_face_count, rps.manual_relation,
                   rps.last_evidence_at_ms, rps.rank_score, rps.updated_at_ms
            FROM related_profile_summary rps
            JOIN profiles p ON p.profile_id = (CASE WHEN rps.profile_id_low = $profileId THEN rps.profile_id_high ELSE rps.profile_id_low END)
            WHERE (rps.profile_id_low = $profileId OR rps.profile_id_high = $profileId)
              AND rps.rank_score > 0
              AND p.trashed_at_ms IS NULL
              AND p.visibility = 'PUBLISHED'
              AND EXISTS (
                  SELECT 1 FROM profiles self
                  WHERE self.profile_id = $profileId
                    AND self.visibility = 'PUBLISHED'
                    AND self.trashed_at_ms IS NULL
              )
              -- Charter 09 acceptance criterion 17: an active Related result never carries
              -- an UNKNOWN endpoint. The write side already refuses to create such evidence,
              -- but this is the active projection and Charter 09 the product contract makes filtering
              -- here its job rather than something it inherits from whoever wrote the row.
              AND p.kind = 'NORMAL'
            ORDER BY rps.rank_score DESC, rps.updated_at_ms DESC
            LIMIT {limit};
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        var list = new List<RelatedProfileSummaryReadModel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var low = DbGuid.Parse(reader.GetString(0));
            var high = DbGuid.Parse(reader.GetString(1));
            var relatedId = DbGuid.Parse(reader.GetString(2));
            var relatedName = reader.GetString(3);
            var sharedMedias = reader.GetInt32(4);
            var confirmedFaces = reader.GetInt32(5);
            var manual = reader.GetInt32(6) == 1;
            var lastEvidence = reader.IsDBNull(7) ? (DateTimeOffset?)null : DbTime.Parse(reader.GetInt64(7));
            var rankScore = reader.GetInt32(8);
            var updatedAt = DbTime.Parse(reader.GetInt64(9));

            list.Add(new RelatedProfileSummaryReadModel(
                low,
                high,
                relatedId,
                relatedName,
                sharedMedias,
                confirmedFaces,
                manual,
                lastEvidence,
                rankScore,
                updatedAt));
        }

        return list;
    }

    public async Task<RelatedProfileSummaryReadModel?> GetRelatedSummaryPairAsync(
        Guid profileId1,
        Guid profileId2,
        CancellationToken cancellationToken = default)
    {
        if (profileId1 == Guid.Empty || profileId2 == Guid.Empty || profileId1 == profileId2)
        {
            return null;
        }

        var (low, high) = CanonicalizePair(profileId1, profileId2);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rps.profile_id_low, rps.profile_id_high,
                   coalesce(p.display_name, 'Unknown') AS high_name,
                   rps.shared_media_count, rps.confirmed_face_count, rps.manual_relation,
                   rps.last_evidence_at_ms, rps.rank_score, rps.updated_at_ms
            FROM related_profile_summary rps
            JOIN profiles p ON p.profile_id = rps.profile_id_high
            WHERE rps.profile_id_low = $low AND rps.profile_id_high = $high
              AND p.visibility = 'PUBLISHED'
              AND p.trashed_at_ms IS NULL
              AND EXISTS (
                  SELECT 1 FROM profiles low_profile
                  WHERE low_profile.profile_id = rps.profile_id_low
                    AND low_profile.visibility = 'PUBLISHED'
                    AND low_profile.trashed_at_ms IS NULL
              );
            """;
        command.Parameters.AddWithValue("$low", DbGuid.Format(low));
        command.Parameters.AddWithValue("$high", DbGuid.Format(high));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var pLow = DbGuid.Parse(reader.GetString(0));
        var pHigh = DbGuid.Parse(reader.GetString(1));
        var relatedName = reader.GetString(2);
        var sharedMedias = reader.GetInt32(3);
        var confirmedFaces = reader.GetInt32(4);
        var manual = reader.GetInt32(5) == 1;
        var lastEvidence = reader.IsDBNull(6) ? (DateTimeOffset?)null : DbTime.Parse(reader.GetInt64(6));
        var rankScore = reader.GetInt32(7);
        var updatedAt = DbTime.Parse(reader.GetInt64(8));

        return new RelatedProfileSummaryReadModel(
            pLow,
            pHigh,
            pHigh,
            relatedName,
            sharedMedias,
            confirmedFaces,
            manual,
            lastEvidence,
            rankScore,
            updatedAt);
    }

    public async Task<IReadOnlyList<RelatedProfileEvidenceEntry>> GetEvidenceForPairAsync(
        Guid profileId1,
        Guid profileId2,
        CancellationToken cancellationToken = default)
    {
        if (profileId1 == Guid.Empty || profileId2 == Guid.Empty || profileId1 == profileId2)
        {
            return [];
        }

        var (low, high) = CanonicalizePair(profileId1, profileId2);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT evidence_key, profile_id_low, profile_id_high, evidence_type,
                   media_id, face_id, created_at_ms
            FROM related_profile_evidence e
            WHERE e.profile_id_low = $low AND e.profile_id_high = $high
              AND EXISTS (
                  SELECT 1 FROM profiles low_profile
                  WHERE low_profile.profile_id = e.profile_id_low
                    AND low_profile.visibility = 'PUBLISHED'
                    AND low_profile.trashed_at_ms IS NULL
              )
              AND EXISTS (
                  SELECT 1 FROM profiles high_profile
                  WHERE high_profile.profile_id = e.profile_id_high
                    AND high_profile.visibility = 'PUBLISHED'
                    AND high_profile.trashed_at_ms IS NULL
              )
              AND (
                  e.media_id IS NULL
                  OR (
                      EXISTS (
                          SELECT 1 FROM profile_media low_rel
                          WHERE low_rel.profile_id = e.profile_id_low
                            AND low_rel.media_id = e.media_id
                            AND low_rel.publication_import_unit_id IS NULL
                      )
                      AND EXISTS (
                          SELECT 1 FROM profile_media high_rel
                          WHERE high_rel.profile_id = e.profile_id_high
                            AND high_rel.media_id = e.media_id
                            AND high_rel.publication_import_unit_id IS NULL
                      )
                  )
              )
            ORDER BY e.created_at_ms DESC;
            """;
        command.Parameters.AddWithValue("$low", DbGuid.Format(low));
        command.Parameters.AddWithValue("$high", DbGuid.Format(high));

        var list = new List<RelatedProfileEvidenceEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = reader.GetString(0);
            var pLow = DbGuid.Parse(reader.GetString(1));
            var pHigh = DbGuid.Parse(reader.GetString(2));
            var evidenceType = DbEnum.ParseRelatedProfileEvidence(reader.GetString(3));
            var assetId = reader.IsDBNull(4) ? (Guid?)null : DbGuid.Parse(reader.GetString(4));
            var faceId = reader.IsDBNull(5) ? (Guid?)null : DbGuid.Parse(reader.GetString(5));
            var createdAt = DbTime.Parse(reader.GetInt64(6));

            list.Add(new RelatedProfileEvidenceEntry(
                key,
                pLow,
                pHigh,
                evidenceType,
                assetId,
                faceId,
                createdAt));
        }

        return list;
    }

    private static (Guid Low, Guid High) CanonicalizePair(Guid a, Guid b)
    {
        var strA = DbGuid.Format(a);
        var strB = DbGuid.Format(b);
        return string.Compare(strA, strB, StringComparison.Ordinal) < 0
            ? (a, b)
            : (b, a);
    }

    public async Task<FeaturedConnectionReadModel?> GetFeaturedConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rps.profile_id_low, rps.profile_id_high,
                   pl.display_name AS low_name,
                   ph.display_name AS high_name,
                   rps.shared_media_count, rps.confirmed_face_count, rps.manual_relation,
                   rps.last_evidence_at_ms, rps.rank_score, rps.updated_at_ms,
                   pl.cover_media_id AS low_cover_id,
                   ph.cover_media_id AS high_cover_id
            FROM related_profile_summary rps
            JOIN profiles pl ON pl.profile_id = rps.profile_id_low
            JOIN profiles ph ON ph.profile_id = rps.profile_id_high
            WHERE rps.rank_score > 0
              AND pl.trashed_at_ms IS NULL AND pl.kind = 'NORMAL' AND pl.visibility = 'PUBLISHED'
              AND ph.trashed_at_ms IS NULL AND ph.kind = 'NORMAL' AND ph.visibility = 'PUBLISHED'
              AND (rps.shared_media_count > 0 OR rps.confirmed_face_count > 0 OR rps.manual_relation = 1)
            ORDER BY
              rps.rank_score DESC,
              coalesce(rps.last_evidence_at_ms, 0) DESC,
              rps.profile_id_low ASC,
              rps.profile_id_high ASC
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var lowId = DbGuid.Parse(reader.GetString(0));
        var highId = DbGuid.Parse(reader.GetString(1));
        var lowName = reader.IsDBNull(2) ? "Unknown" : reader.GetString(2);
        var highName = reader.IsDBNull(3) ? "Unknown" : reader.GetString(3);
        var sharedMedias = reader.GetInt32(4);
        var confirmedFaces = reader.GetInt32(5);
        var manual = reader.GetInt32(6) == 1;
        var lastEvidence = reader.IsDBNull(7) ? (DateTimeOffset?)null : DbTime.Parse(reader.GetInt64(7));
        var rankScore = reader.GetInt32(8);
        var updatedAt = DbTime.Parse(reader.GetInt64(9));
        var lowCoverId = reader.IsDBNull(10) ? (Guid?)null : DbGuid.Parse(reader.GetString(10));
        var highCoverId = reader.IsDBNull(11) ? (Guid?)null : DbGuid.Parse(reader.GetString(11));

        return new FeaturedConnectionReadModel(
            lowId,
            highId,
            lowName,
            highName,
            sharedMedias,
            confirmedFaces,
            manual,
            lastEvidence,
            rankScore,
            updatedAt,
            lowCoverId,
            highCoverId);
    }
}

public sealed record FeaturedConnectionReadModel(
    Guid ProfileIdLow,
    Guid ProfileIdHigh,
    string LowDisplayName,
    string HighDisplayName,
    int SharedMediaCount,
    int ConfirmedFaceCount,
    bool ManualRelation,
    DateTimeOffset? LastEvidenceAtUtc,
    int RankScore,
    DateTimeOffset UpdatedAtUtc,
    Guid? LowCoverMediaId,
    Guid? HighCoverMediaId);

