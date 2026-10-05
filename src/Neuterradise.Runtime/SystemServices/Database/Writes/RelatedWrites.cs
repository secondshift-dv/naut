using Neuterradise.App.RelatedProfiles;

namespace Neuterradise.App.SystemServices.Database.Writes;

public sealed class RelatedWrites
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;

    public RelatedWrites(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
    }

    public async Task PersistEvidenceAsync(
        RelatedEvidencePersistence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.EvidenceKey);
        var pair = Canonicalize(evidence.FirstProfileId, evidence.SecondProfileId);
        ValidateProvenance(evidence);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await EnsureActiveNormalPairAsync(transaction, pair, cancellationToken).ConfigureAwait(false);
        await EnsureEvidenceSourcesAsync(transaction, evidence, cancellationToken).ConfigureAwait(false);

        var existing = await ReadEvidenceAsync(transaction, evidence.EvidenceKey, cancellationToken)
            .ConfigureAwait(false);
        var createdAt = DbTime.Format(evidence.CreatedAtUtc);
        if (existing is not null)
        {
            if (existing.ProfileIdLow != pair.Low
                || existing.ProfileIdHigh != pair.High
                || existing.EvidenceType != evidence.EvidenceType
                || existing.MediaId != evidence.MediaId
                || existing.FaceId != evidence.FaceId)
            {
                throw new CatalogInvariantException(
                    $"Related evidence key '{evidence.EvidenceKey}' already has different provenance.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var insert = transaction.CreateCommand(
            """
            INSERT INTO related_profile_evidence(
                evidence_key, profile_id_low, profile_id_high, evidence_type,
                media_id, face_id, created_at_ms)
            VALUES (
                $evidenceKey, $profileIdLow, $profileIdHigh, $evidenceType,
                $assetId, $faceId, $createdAtMs);
            """);
        insert.Parameters.AddWithValue("$evidenceKey", evidence.EvidenceKey);
        insert.Parameters.AddWithValue("$profileIdLow", DbGuid.Format(pair.Low));
        insert.Parameters.AddWithValue("$profileIdHigh", DbGuid.Format(pair.High));
        insert.Parameters.AddWithValue("$evidenceType", DbEnum.Format(evidence.EvidenceType));
        insert.Parameters.AddWithValue(
            "$assetId",
            evidence.MediaId is null ? DBNull.Value : DbGuid.Format(evidence.MediaId.Value));
        insert.Parameters.AddWithValue(
            "$faceId",
            evidence.FaceId is null ? DBNull.Value : DbGuid.Format(evidence.FaceId.Value));
        insert.Parameters.AddWithValue("$createdAtMs", createdAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelatedSummaryPersistence> RebuildSummaryAsync(
        Guid firstProfileId,
        Guid secondProfileId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var pair = Canonicalize(firstProfileId, secondProfileId);
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        await EnsureActiveNormalPairAsync(transaction, pair, cancellationToken).ConfigureAwait(false);
        var summary = await AggregateSummaryAsync(transaction, pair, cancellationToken)
            .ConfigureAwait(false);
        var rebuilt = summary with
        {
            UpdatedAtMilliseconds = DbTime.Format(updatedAtUtc),
        };

        if (rebuilt.LastEvidenceAtMilliseconds is null)
        {
            await using var delete = transaction.CreateCommand(
                """
                DELETE FROM related_profile_summary
                WHERE profile_id_low = $profileIdLow AND profile_id_high = $profileIdHigh;
                """);
            delete.Parameters.AddWithValue("$profileIdLow", DbGuid.Format(pair.Low));
            delete.Parameters.AddWithValue("$profileIdHigh", DbGuid.Format(pair.High));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var upsert = transaction.CreateCommand(
                """
                INSERT INTO related_profile_summary(
                    profile_id_low, profile_id_high, shared_media_count,
                    confirmed_face_count, manual_relation, last_evidence_at_ms,
                    rank_score, updated_at_ms)
                VALUES (
                    $profileIdLow, $profileIdHigh, $sharedMediaCount,
                    $confirmedFaceCount, $manualRelation, $lastEvidenceAtMs,
                    $rankScore, $updatedAtMs)
                ON CONFLICT(profile_id_low, profile_id_high) DO UPDATE SET
                    shared_media_count = excluded.shared_media_count,
                    confirmed_face_count = excluded.confirmed_face_count,
                    manual_relation = excluded.manual_relation,
                    last_evidence_at_ms = excluded.last_evidence_at_ms,
                    rank_score = excluded.rank_score,
                    updated_at_ms = excluded.updated_at_ms;
                """);
            upsert.Parameters.AddWithValue("$profileIdLow", DbGuid.Format(pair.Low));
            upsert.Parameters.AddWithValue("$profileIdHigh", DbGuid.Format(pair.High));
            upsert.Parameters.AddWithValue("$sharedMediaCount", rebuilt.SharedMediaCount);
            upsert.Parameters.AddWithValue("$confirmedFaceCount", rebuilt.ConfirmedFaceCount);
            upsert.Parameters.AddWithValue("$manualRelation", rebuilt.ManualRelation ? 1 : 0);
            upsert.Parameters.AddWithValue(
                "$lastEvidenceAtMs",
                rebuilt.LastEvidenceAtMilliseconds.Value);
            upsert.Parameters.AddWithValue("$rankScore", rebuilt.RankScore);
            upsert.Parameters.AddWithValue("$updatedAtMs", rebuilt.UpdatedAtMilliseconds);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [pair.Low, pair.High],
            CatalogInvalidationDomain.Related,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rebuilt;
    }

    public async Task<RelatedSummaryPersistence?> RemoveManualEvidenceAsync(
        Guid firstProfileId,
        Guid secondProfileId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var pair = Canonicalize(firstProfileId, secondProfileId);
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        await EnsureActiveNormalPairAsync(transaction, pair, cancellationToken).ConfigureAwait(false);

        await using (var deleteEvidence = transaction.CreateCommand(
            """
            DELETE FROM related_profile_evidence
            WHERE profile_id_low = $profileIdLow
              AND profile_id_high = $profileIdHigh
              AND evidence_type = 'MANUAL';
            """))
        {
            deleteEvidence.Parameters.AddWithValue("$profileIdLow", DbGuid.Format(pair.Low));
            deleteEvidence.Parameters.AddWithValue("$profileIdHigh", DbGuid.Format(pair.High));
            await deleteEvidence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var summary = await AggregateSummaryAsync(transaction, pair, cancellationToken)
            .ConfigureAwait(false);
        var rebuilt = summary with
        {
            UpdatedAtMilliseconds = DbTime.Format(updatedAtUtc),
        };

        if (rebuilt.LastEvidenceAtMilliseconds is null)
        {
            await using var delete = transaction.CreateCommand(
                """
                DELETE FROM related_profile_summary
                WHERE profile_id_low = $profileIdLow AND profile_id_high = $profileIdHigh;
                """);
            delete.Parameters.AddWithValue("$profileIdLow", DbGuid.Format(pair.Low));
            delete.Parameters.AddWithValue("$profileIdHigh", DbGuid.Format(pair.High));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var upsert = transaction.CreateCommand(
                """
                INSERT INTO related_profile_summary(
                    profile_id_low, profile_id_high, shared_media_count,
                    confirmed_face_count, manual_relation, last_evidence_at_ms,
                    rank_score, updated_at_ms)
                VALUES (
                    $profileIdLow, $profileIdHigh, $sharedMediaCount,
                    $confirmedFaceCount, $manualRelation, $lastEvidenceAtMs,
                    $rankScore, $updatedAtMs)
                ON CONFLICT(profile_id_low, profile_id_high) DO UPDATE SET
                    shared_media_count = excluded.shared_media_count,
                    confirmed_face_count = excluded.confirmed_face_count,
                    manual_relation = excluded.manual_relation,
                    last_evidence_at_ms = excluded.last_evidence_at_ms,
                    rank_score = excluded.rank_score,
                    updated_at_ms = excluded.updated_at_ms;
                """);
            upsert.Parameters.AddWithValue("$profileIdLow", DbGuid.Format(pair.Low));
            upsert.Parameters.AddWithValue("$profileIdHigh", DbGuid.Format(pair.High));
            upsert.Parameters.AddWithValue("$sharedMediaCount", rebuilt.SharedMediaCount);
            upsert.Parameters.AddWithValue("$confirmedFaceCount", rebuilt.ConfirmedFaceCount);
            upsert.Parameters.AddWithValue("$manualRelation", rebuilt.ManualRelation ? 1 : 0);
            upsert.Parameters.AddWithValue(
                "$lastEvidenceAtMs",
                rebuilt.LastEvidenceAtMilliseconds.Value);
            upsert.Parameters.AddWithValue("$rankScore", rebuilt.RankScore);
            upsert.Parameters.AddWithValue("$updatedAtMs", rebuilt.UpdatedAtMilliseconds);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [pair.Low, pair.High],
            CatalogInvalidationDomain.Related,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rebuilt.LastEvidenceAtMilliseconds is null ? null : rebuilt;
    }

    public async Task<int> RebuildAllSummariesAsync(
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        await using (var cleanOrphaned = transaction.CreateCommand(
            """
            DELETE FROM related_profile_summary
            WHERE profile_id_low NOT IN (
                      SELECT profile_id FROM profiles
                      WHERE kind = 'NORMAL' AND visibility = 'PUBLISHED' AND trashed_at_ms IS NULL)
               OR profile_id_high NOT IN (
                      SELECT profile_id FROM profiles
                      WHERE kind = 'NORMAL' AND visibility = 'PUBLISHED' AND trashed_at_ms IS NULL);
            """))
        {
            await cleanOrphaned.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var pairs = new List<ProfilePair>();
        await using (var selectPairs = transaction.CreateCommand(
            """
            SELECT DISTINCT rpe.profile_id_low, rpe.profile_id_high
            FROM related_profile_evidence rpe
            JOIN profiles pLow ON rpe.profile_id_low = pLow.profile_id
            JOIN profiles pHigh ON rpe.profile_id_high = pHigh.profile_id
            WHERE pLow.kind = 'NORMAL' AND pLow.visibility = 'PUBLISHED' AND pLow.trashed_at_ms IS NULL
              AND pHigh.kind = 'NORMAL' AND pHigh.visibility = 'PUBLISHED' AND pHigh.trashed_at_ms IS NULL;
            """))
        {
            await using var reader = await selectPairs.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                pairs.Add(new ProfilePair(
                    DbGuid.Parse(reader.GetString(0)),
                    DbGuid.Parse(reader.GetString(1))));
            }
        }

        var count = 0;
        foreach (var pair in pairs)
        {
            var summary = await AggregateSummaryAsync(transaction, pair, cancellationToken).ConfigureAwait(false);
            if (summary.LastEvidenceAtMilliseconds is not null)
            {
                var updatedMs = DbTime.Format(updatedAtUtc);
                await using var upsert = transaction.CreateCommand(
                    """
                    INSERT INTO related_profile_summary(
                        profile_id_low, profile_id_high, shared_media_count,
                        confirmed_face_count, manual_relation, last_evidence_at_ms,
                        rank_score, updated_at_ms)
                    VALUES (
                        $profileIdLow, $profileIdHigh, $sharedMediaCount,
                        $confirmedFaceCount, $manualRelation, $lastEvidenceAtMs,
                        $rankScore, $updatedAtMs)
                    ON CONFLICT(profile_id_low, profile_id_high) DO UPDATE SET
                        shared_media_count = excluded.shared_media_count,
                        confirmed_face_count = excluded.confirmed_face_count,
                        manual_relation = excluded.manual_relation,
                        last_evidence_at_ms = excluded.last_evidence_at_ms,
                        rank_score = excluded.rank_score,
                        updated_at_ms = excluded.updated_at_ms;
                    """);
                upsert.Parameters.AddWithValue("$profileIdLow", DbGuid.Format(pair.Low));
                upsert.Parameters.AddWithValue("$profileIdHigh", DbGuid.Format(pair.High));
                upsert.Parameters.AddWithValue("$sharedMediaCount", summary.SharedMediaCount);
                upsert.Parameters.AddWithValue("$confirmedFaceCount", summary.ConfirmedFaceCount);
                upsert.Parameters.AddWithValue("$manualRelation", summary.ManualRelation ? 1 : 0);
                upsert.Parameters.AddWithValue("$lastEvidenceAtMs", summary.LastEvidenceAtMilliseconds.Value);
                upsert.Parameters.AddWithValue("$rankScore", summary.RankScore);
                upsert.Parameters.AddWithValue("$updatedAtMs", updatedMs);
                await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                count++;
            }
        }

        transaction.QueueInvalidation(CatalogInvalidationDomain.Related);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    private static async Task<RelatedSummaryPersistence> AggregateSummaryAsync(
        CatalogTransaction transaction,
        ProfilePair pair,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT COUNT(DISTINCT CASE WHEN e.evidence_type = 'SHARED_ASSET' THEN e.media_id END),
                   COUNT(DISTINCT CASE WHEN e.evidence_type = 'CONFIRMED_FACE' THEN e.face_id END),
                   MAX(CASE WHEN e.evidence_type = 'MANUAL' THEN 1 ELSE 0 END),
                   MAX(e.created_at_ms)
            FROM related_profile_evidence e
            LEFT JOIN media a ON a.media_id = e.media_id
            WHERE e.profile_id_low = $profileIdLow AND e.profile_id_high = $profileIdHigh
              AND (
                    e.media_id IS NULL
                    OR (
                        a.state = 'ACTIVE' AND a.trashed_at_ms IS NULL
                        AND EXISTS (
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
                  );
            """);
        command.Parameters.AddWithValue("$profileIdLow", DbGuid.Format(pair.Low));
        command.Parameters.AddWithValue("$profileIdHigh", DbGuid.Format(pair.High));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var sharedMediaCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        var confirmedFaceCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        var manualRelation = !reader.IsDBNull(2) && reader.GetInt32(2) == 1;
        long? lastEvidenceAt = reader.IsDBNull(3) ? null : reader.GetInt64(3);
        var rankScore = sharedMediaCount + (confirmedFaceCount * 2) + (manualRelation ? 1 : 0);
        return new RelatedSummaryPersistence(
            pair.Low,
            pair.High,
            sharedMediaCount,
            confirmedFaceCount,
            manualRelation,
            lastEvidenceAt,
            rankScore,
            UpdatedAtMilliseconds: 0);
    }

    private static async Task EnsureActiveNormalPairAsync(
        CatalogTransaction transaction,
        ProfilePair pair,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT COUNT(*)
            FROM profiles
            WHERE profile_id IN ($profileIdLow, $profileIdHigh)
              AND kind = 'NORMAL'
              AND visibility = 'PUBLISHED'
              AND trashed_at_ms IS NULL;
            """);
        command.Parameters.AddWithValue("$profileIdLow", DbGuid.Format(pair.Low));
        command.Parameters.AddWithValue("$profileIdHigh", DbGuid.Format(pair.High));
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (count != 2)
        {
            throw new CatalogInvariantException(
                "Related evidence requires two distinct active NORMAL Profiles.");
        }
    }

    private static async Task EnsureEvidenceSourcesAsync(
        CatalogTransaction transaction,
        RelatedEvidencePersistence evidence,
        CancellationToken cancellationToken)
    {
        if (evidence.MediaId is Guid assetId)
        {
            await using var asset = transaction.CreateCommand(
                """
                SELECT COUNT(*)
                FROM media a
                WHERE a.media_id = $assetId
                  AND a.state = 'ACTIVE'
                  AND a.trashed_at_ms IS NULL
                  AND EXISTS (
                      SELECT 1 FROM profile_media first_rel
                      WHERE first_rel.profile_id = $firstProfileId
                        AND first_rel.media_id = a.media_id
                        AND first_rel.publication_import_unit_id IS NULL
                  )
                  AND EXISTS (
                      SELECT 1 FROM profile_media second_rel
                      WHERE second_rel.profile_id = $secondProfileId
                        AND second_rel.media_id = a.media_id
                        AND second_rel.publication_import_unit_id IS NULL
                  );
                """);
            asset.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
            asset.Parameters.AddWithValue("$firstProfileId", DbGuid.Format(evidence.FirstProfileId));
            asset.Parameters.AddWithValue("$secondProfileId", DbGuid.Format(evidence.SecondProfileId));
            var active = Convert.ToInt32(
                await asset.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
            if (!active)
            {
                throw new CatalogInvariantException(
                    "Related asset evidence requires an ACTIVE Media published to both Profiles.");
            }
        }

        if (evidence.FaceId is Guid faceId)
        {
            await using var face = transaction.CreateCommand(
                """
                SELECT COUNT(*)
                FROM face_detections
                WHERE face_id = $faceId
                  AND media_id = $assetId
                  AND decision_state = 'CONFIRMED';
                """);
            face.Parameters.AddWithValue("$faceId", DbGuid.Format(faceId));
            face.Parameters.AddWithValue("$assetId", DbGuid.Format(evidence.MediaId!.Value));
            var confirmed = Convert.ToInt32(
                await face.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
            if (!confirmed)
            {
                throw new CatalogInvariantException(
                    "CONFIRMED_FACE evidence requires matching confirmed FaceDetection provenance.");
            }
        }
    }

    private static async Task<EvidenceRow?> ReadEvidenceAsync(
        CatalogTransaction transaction,
        string evidenceKey,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT profile_id_low, profile_id_high, evidence_type,
                   media_id, face_id, created_at_ms
            FROM related_profile_evidence
            WHERE evidence_key = $evidenceKey;
            """);
        command.Parameters.AddWithValue("$evidenceKey", evidenceKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new EvidenceRow(
            DbGuid.Parse(reader.GetString(0)),
            DbGuid.Parse(reader.GetString(1)),
            DbEnum.ParseRelatedProfileEvidence(reader.GetString(2)),
            reader.IsDBNull(3) ? null : DbGuid.Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : DbGuid.Parse(reader.GetString(4)),
            reader.GetInt64(5));
    }

    private static ProfilePair Canonicalize(Guid firstProfileId, Guid secondProfileId)
    {
        EnsureNonEmpty(firstProfileId, nameof(firstProfileId));
        EnsureNonEmpty(secondProfileId, nameof(secondProfileId));
        if (firstProfileId == secondProfileId)
        {
            throw new ArgumentException("Related Profile evidence cannot target a self-pair.");
        }

        return string.CompareOrdinal(DbGuid.Format(firstProfileId), DbGuid.Format(secondProfileId)) < 0
            ? new ProfilePair(firstProfileId, secondProfileId)
            : new ProfilePair(secondProfileId, firstProfileId);
    }

    private static void ValidateProvenance(RelatedEvidencePersistence evidence)
    {
        var valid = evidence.EvidenceType switch
        {
            RelatedProfileEvidence.SharedMedia => evidence.MediaId is not null
                && evidence.FaceId is null,
            RelatedProfileEvidence.ConfirmedFace => evidence.MediaId is not null
                && evidence.FaceId is not null,
            RelatedProfileEvidence.Manual => evidence.MediaId is null && evidence.FaceId is null,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                "Related evidence provenance does not match its canonical namespace.",
                nameof(evidence));
        }
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }

    private readonly record struct ProfilePair(Guid Low, Guid High);

    private sealed record EvidenceRow(
        Guid ProfileIdLow,
        Guid ProfileIdHigh,
        RelatedProfileEvidence EvidenceType,
        Guid? MediaId,
        Guid? FaceId,
        long CreatedAtMilliseconds);
}

public sealed record RelatedEvidencePersistence(
    string EvidenceKey,
    Guid FirstProfileId,
    Guid SecondProfileId,
    RelatedProfileEvidence EvidenceType,
    Guid? MediaId,
    Guid? FaceId,
    DateTimeOffset CreatedAtUtc);

public sealed record RelatedSummaryPersistence(
    Guid ProfileIdLow,
    Guid ProfileIdHigh,
    int SharedMediaCount,
    int ConfirmedFaceCount,
    bool ManualRelation,
    long? LastEvidenceAtMilliseconds,
    int RankScore,
    long UpdatedAtMilliseconds);
