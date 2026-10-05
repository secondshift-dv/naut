using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Storage;
using System.IO;
using Microsoft.Data.Sqlite;
using Neuterradise.App.Import;
using Neuterradise.App.Settings;
using Neuterradise.App.Maintenance;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed class HealthReads
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public HealthReads(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public HealthReads(CatalogConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<LibraryHealthSummary> GetHealthSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM profiles WHERE trashed_at_ms IS NULL) AS total_profiles,
                (SELECT COUNT(*) FROM profiles WHERE kind = 'NORMAL' AND trashed_at_ms IS NULL) AS normal_profiles,
                (SELECT COUNT(*) FROM profiles WHERE kind = 'UNKNOWN' AND trashed_at_ms IS NULL) AS unknown_profiles,
                (SELECT COUNT(*) FROM media) AS total_assets,
                (SELECT COUNT(*) FROM media WHERE state = 'ACTIVE') AS active_assets,
                (SELECT COUNT(*) FROM media WHERE state = 'CANDIDATE') AS candidate_assets,
                (SELECT COUNT(*) FROM media WHERE state = 'TRASHED') AS trashed_assets,
                (SELECT COUNT(*) FROM media WHERE state = 'RETIRED') AS retired_assets,
                (
                    (SELECT COUNT(*) FROM profiles WHERE path_state = 'PENDING') +
                    (SELECT COUNT(*) FROM media WHERE path_state = 'PENDING')
                ) AS pending_reconciliation,
                (
                    (SELECT COUNT(*) FROM profiles WHERE path_state = 'NEEDS_ATTENTION') +
                    (SELECT COUNT(*) FROM media WHERE path_state = 'NEEDS_ATTENTION')
                ) AS needs_attention_reconciliation,
                (SELECT COUNT(*)
                 FROM face_detections fd
                 JOIN media a ON a.media_id = fd.media_id
                 WHERE a.state = 'ACTIVE'
                   AND fd.decision_state IN ('UNKNOWN', 'SUGGESTED')) AS unresolved_faces,
                (SELECT COUNT(*) FROM jobs WHERE state IN ('PENDING', 'RUNNABLE', 'RUNNING')) AS active_jobs,
                (SELECT COUNT(*) FROM jobs WHERE state IN ('FAILED_RETRYABLE', 'FAILED_TERMINAL')) AS failed_jobs,
                (SELECT coalesce(SUM(byte_length), 0) FROM media WHERE state = 'ACTIVE') AS active_bytes;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new LibraryHealthSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, _timeProvider.GetUtcNow(), 0);
        }

        return new LibraryHealthSummary(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            _timeProvider.GetUtcNow(),
            reader.GetInt64(13));
    }

    /// <summary>
    /// Counts only unresolved detections that the Face Review queue can actually publish. The
    /// health summary intentionally retains its raw diagnostic count; Home attention must use this
    /// actionable projection so preparation-time detections do not appear as user work.
    /// </summary>
    public async Task<int> GetActionableFaceReviewCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM face_detections fd
            JOIN media a ON a.media_id = fd.media_id AND a.state = 'ACTIVE'
            WHERE fd.decision_state IN ('UNKNOWN', 'SUGGESTED')
              AND EXISTS (
                  SELECT 1
                  FROM profile_media pa
                  JOIN profiles p ON p.profile_id = pa.profile_id
                  WHERE pa.media_id = fd.media_id
                    AND pa.publication_import_unit_id IS NULL
                    AND (p.kind <> 'NORMAL' OR (p.visibility = 'PUBLISHED' AND p.trashed_at_ms IS NULL))
              );
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<StorageMetricsAuthorityCounts> GetStorageMetricsCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM trash_entries WHERE state IN ('PENDING', 'IN_TRASH', 'EXECUTING')) AS trash_entry_count,
                (SELECT coalesce(SUM(byte_length), 0) FROM media WHERE state = 'TRASHED') AS trashed_media_bytes,
                (SELECT COUNT(*) FROM import_items WHERE source_cleanup_state IN ('SOURCE_DELETE_PENDING', 'SOURCE_DELETE_FAILED', 'SOURCE_CHANGED')) AS pending_cleanup_count;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new StorageMetricsAuthorityCounts(0, 0, 0);
        }

        return new StorageMetricsAuthorityCounts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
    }

    public async Task<IReadOnlyList<HealthProfileItem>> GetHealthProfileItemsAsync(
        Guid? profileId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        if (profileId.HasValue)
        {
            command.CommandText = """
                SELECT profile_id, kind,
                       CASE WHEN kind = 'UNKNOWN' THEN 'Unknown ' || unknown_sequence ELSE display_name END,
                       profile_storage_token, current_managed_relative_path,
                       path_state, (trashed_at_ms IS NOT NULL) AS is_trashed, row_version,
                       target_managed_relative_path
                FROM profiles
                WHERE profile_id = $profileId;
                """;
            command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId.Value));
        }
        else
        {
            command.CommandText = """
                SELECT profile_id, kind,
                       CASE WHEN kind = 'UNKNOWN' THEN 'Unknown ' || unknown_sequence ELSE display_name END,
                       profile_storage_token, current_managed_relative_path,
                       path_state, (trashed_at_ms IS NOT NULL) AS is_trashed, row_version,
                       target_managed_relative_path
                FROM profiles;
                """;
        }

        var results = new List<HealthProfileItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new HealthProfileItem(
                ProfileId: DbGuid.Parse(reader.GetString(0)),
                Kind: DbEnum.ParseProfileKind(reader.GetString(1)),
                DisplayName: reader.GetString(2),
                StorageToken: reader.IsDBNull(3) ? null : reader.GetString(3),
                CurrentManagedRelativePath: reader.IsDBNull(4) ? null : reader.GetString(4),
                PathState: DbEnum.ParseManagedPathState(reader.GetString(5)),
                IsTrashed: reader.GetInt32(6) != 0,
                RowVersion: reader.GetInt64(7),
                TargetManagedRelativePath: reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return results;
    }

    public async Task<IReadOnlyList<HealthMediaItem>> GetHealthMediaItemsAsync(
        Guid? assetId = null,
        Guid? ownerProfileId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        var sql = """
            SELECT a.media_id, a.media_type,
                   coalesce(a.current_managed_file_name, a.original_file_name, '') AS file_name,
                   a.media_storage_token, a.sha256, coalesce(a.byte_length, 0),
                   a.current_managed_relative_path, a.state, (a.trashed_at_ms IS NOT NULL) AS is_trashed,
                   pa.profile_id AS owner_profile_id,
                   CASE WHEN p.kind = 'UNKNOWN' THEN 'Unknown ' || p.unknown_sequence ELSE p.display_name END AS owner_display_name,
                   p.profile_storage_token AS owner_storage_token,
                   a.current_managed_file_name
            FROM media a
            LEFT JOIN profile_media pa ON pa.media_id = a.media_id AND pa.relation_type = $ownerRelation
            LEFT JOIN profiles p ON p.profile_id = pa.profile_id
            WHERE 1=1
            """;

        if (assetId.HasValue)
        {
            sql += " AND a.media_id = $assetId";
            command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId.Value));
        }

        command.Parameters.AddWithValue("$ownerRelation", DbEnum.Format(ProfileMediaRelation.Owner));

        if (ownerProfileId.HasValue)
        {
            sql += " AND pa.profile_id = $ownerProfileId";
            command.Parameters.AddWithValue("$ownerProfileId", DbGuid.Format(ownerProfileId.Value));
        }

        command.CommandText = sql + ";";

        var results = new List<HealthMediaItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var rawFileName = reader.GetString(2);
            var extension = Path.GetExtension(rawFileName).TrimStart('.');

            results.Add(new HealthMediaItem(
                MediaId: DbGuid.Parse(reader.GetString(0)),
                MediaType: DbEnum.ParseMediaType(reader.GetString(1)),
                Extension: extension,
                StorageToken: reader.IsDBNull(3) ? null : reader.GetString(3),
                Sha256: reader.IsDBNull(4) ? null : reader.GetString(4),
                ByteLength: reader.GetInt64(5),
                CurrentManagedRelativePath: reader.IsDBNull(6) ? null : reader.GetString(6),
                State: DbEnum.ParseMediaState(reader.GetString(7)),
                IsTrashed: reader.GetInt32(8) != 0,
                OwnerProfileId: reader.IsDBNull(9) ? null : DbGuid.Parse(reader.GetString(9)),
                OwnerDisplayName: reader.IsDBNull(10) ? null : reader.GetString(10),
                OwnerStorageToken: reader.IsDBNull(11) ? null : reader.GetString(11),
                CurrentManagedFileName: reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return results;
    }

    public async Task<IReadOnlyList<HealthSourceCleanupItem>> GetSourceCleanupRepairItemsAsync(
        Guid? subjectId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.import_item_id,
                   i.candidate_media_id,
                   i.row_version,
                   i.source_cleanup_state,
                   i.source_path,
                   candidate.sha256,
                   candidate.byte_length,
                   managed.current_managed_relative_path,
                   managed.current_managed_file_name
            FROM import_items i
            JOIN import_units u ON u.import_unit_id = i.import_unit_id
            JOIN media candidate ON candidate.media_id = i.candidate_media_id
            JOIN media managed ON managed.media_id = coalesce(i.reused_media_id, i.candidate_media_id)
            WHERE i.source_cleanup_state IN ('SOURCE_DELETE_PENDING','SOURCE_DELETE_FAILED','SOURCE_CHANGED')
              AND u.library_commit_state IN (
                  'DOMAIN_AUTHORITY_COMMITTED','SOURCE_CLEANUP_PENDING',
                  'SOURCE_CLEANUP_COMPLETE','TERMINAL')
              AND ($subjectId IS NULL
                   OR i.candidate_media_id = $subjectId
                   OR i.import_item_id = $subjectId)
              AND candidate.sha256 IS NOT NULL
              AND candidate.byte_length IS NOT NULL
              AND managed.current_managed_relative_path IS NOT NULL
              AND managed.current_managed_file_name IS NOT NULL
            ORDER BY i.import_item_id;
            """;
        command.Parameters.AddWithValue(
            "$subjectId",
            subjectId is null ? DBNull.Value : DbGuid.Format(subjectId.Value));

        var results = new List<HealthSourceCleanupItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new HealthSourceCleanupItem(
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1)),
                reader.GetInt64(2),
                DbEnum.ParseSourceCleanupState(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return results;
    }

    public async Task<IReadOnlyList<HealthFinding>> GetDatabaseIntegrityFindingsAsync(
        CancellationToken cancellationToken = default)
    {
        var findings = new List<HealthFinding>();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var check in DatabaseIntegrityChecks)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = check.Sql;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var subject = reader.IsDBNull(0) ? null : reader.GetString(0);
                var detail = reader.FieldCount > 1 && !reader.IsDBNull(1) ? reader.GetString(1) : null;

                findings.Add(new HealthFinding(
                    check.Code,
                    check.Severity,
                    ProfileId: check.Entity == IntegrityEntity.Profile ? TryParse(subject) : null,
                    MediaId: check.Entity == IntegrityEntity.Media ? TryParse(subject) : null,
                    JobId: check.Entity == IntegrityEntity.Job ? TryParse(subject) : null,
                    OperationId: check.Entity == IntegrityEntity.Operation ? subject : null,
                    Summary: detail is null
                        ? $"{check.Summary} ({subject ?? "unidentified"})"
                        : $"{check.Summary} ({subject ?? "unidentified"}): {detail}",
                    RepairAvailable: IsConservativeRepairAvailable(check.Code)));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT aa.media_id, aa.relative_path, aa.contract_version, a.media_type,
                       coalesce(a.current_managed_file_name,a.original_file_name), a.dependency_status, a.media_storage_token, a.sha256
                FROM media_assets aa JOIN media a ON a.media_id = aa.media_id WHERE aa.role = 'MODEL_RENDER';
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var valid = !reader.IsDBNull(7) && reader.GetString(3) == "MODEL" && !reader.IsDBNull(4)
                    && ModelRenderEligibility.IsEligible(reader.GetString(4),reader.GetString(5));
                try { valid &= reader.GetString(1) == new ManagedPathPlanner().PlanMediaAsset(new MediaStorageToken(reader.GetString(6)),MediaAssetRole.ModelRender); }
                catch (ArgumentException) { valid = false; }
                if (!valid || reader.GetInt32(2) != 1)
                    findings.Add(new HealthFinding(reader.GetInt32(2) != 1 ? HealthFindingCode.ArtifactContractObsolete : HealthFindingCode.MediaAssetFingerprintMismatch,
                        HealthSeverity.Error,null,DbGuid.Parse(reader.GetString(0)),null,null,
                        $"ModelRender '{reader.GetString(1)}' source, path or contract authority is invalid.",valid));
            }
        }

        findings.AddRange(
            await FindTaxonomyNameCollisionsAsync(connection, cancellationToken).ConfigureAwait(false));

        return findings;
    }

    private static async Task<IReadOnlyList<HealthFinding>> FindTaxonomyNameCollisionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var findings = new List<HealthFinding>();

        foreach (var (table, label) in new[] { ("categories", "Category"), ("tags", "Tag") })
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT name FROM {table} ORDER BY created_at_ms;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var canonical = TaxonomyNamePolicy.TryNormalize(name);
                if (canonical is null)
                {
                    continue;
                }

                if (seen.TryGetValue(canonical, out var existing))
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.TaxonomyNameCollision,
                        HealthSeverity.Warning,
                        ProfileId: null,
                        MediaId: null,
                        JobId: null,
                        OperationId: null,
                        Summary: $"{label} '{name}' and '{existing}' are the same name under the current"
                            + " naming rules. Both are kept; merge them from Settings when you decide which to keep.",
                        RepairAvailable: false));
                    continue;
                }

                seen[canonical] = name;
            }
        }

        return findings;
    }

    private static bool IsConservativeRepairAvailable(string code) =>
        code is HealthFindingCode.SourceDeletePending or HealthFindingCode.SourceDeleteFailed;

    private static Guid? TryParse(string? value) =>
        value is not null && DomainId.TryParse(value, out var parsed) ? parsed : null;

    private enum IntegrityEntity
    {
        Profile,
        Media,
        Job,
        Operation,
    }

    private sealed record IntegrityCheck(
        string Code,
        HealthSeverity Severity,
        IntegrityEntity Entity,
        string Summary,
        string Sql);

    private static readonly IntegrityCheck[] DatabaseIntegrityChecks =
    [
        new(
            HealthFindingCode.StorageTokenMissing,
            HealthSeverity.Error,
            IntegrityEntity.Profile,
            "An active Profile with a managed folder has no ProfileStorageToken",
            """
            SELECT profile_id, current_managed_relative_path
            FROM profiles
            WHERE trashed_at_ms IS NULL
              AND current_managed_relative_path IS NOT NULL
              AND profile_storage_token IS NULL;
            """),
        new(
            HealthFindingCode.StorageTokenMissing,
            HealthSeverity.Error,
            IntegrityEntity.Media,
            "An ACTIVE Media with managed bytes has no MediaStorageToken",
            """
            SELECT media_id, current_managed_file_name
            FROM media
            WHERE state = 'ACTIVE'
              AND trashed_at_ms IS NULL
              AND current_managed_file_name IS NOT NULL
              AND media_storage_token IS NULL;
            """),
        new(
            HealthFindingCode.StorageTokenPathMismatch,
            HealthSeverity.Error,
            IntegrityEntity.Profile,
            "A Profile's managed folder no longer carries its persisted ProfileStorageToken",
            """
            -- Mutation evidence, not a naming convention check: the finding fires only
            -- when the managed folder already carries a bracketed Profile token marker
            -- and that marker is not this Profile's persisted token. A path with no
            -- marker at all is a different concern and is not evidence of mutation.
            SELECT profile_id, current_managed_relative_path
            FROM profiles
            WHERE trashed_at_ms IS NULL
              AND profile_storage_token IS NOT NULL
              AND current_managed_relative_path IS NOT NULL
              AND instr(current_managed_relative_path, '[P-') > 0
              AND instr(current_managed_relative_path, '[' || profile_storage_token || ']') = 0;
            """),
        new(
            HealthFindingCode.StorageTokenPathMismatch,
            HealthSeverity.Error,
            IntegrityEntity.Media,
            "An Media's managed file name no longer carries its persisted MediaStorageToken",
            """
            -- A managed file is named "{safe name} - {token}.{ext}",
            -- so the Media marker is the " - " trailer, not the bracketed form a Profile
            -- folder uses. The finding fires only when the name already carries an Media
            -- token marker and that marker is not this Media's own token.
            SELECT media_id, current_managed_file_name
            FROM media
            WHERE state = 'ACTIVE'
              AND trashed_at_ms IS NULL
              AND media_storage_token IS NOT NULL
              AND current_managed_file_name IS NOT NULL
              AND instr(current_managed_file_name, ' - A-') > 0
              AND instr(current_managed_file_name, ' - ' || media_storage_token || '.') = 0;
            """),
        new(
            HealthFindingCode.OwnerCountInvalid,
            HealthSeverity.Critical,
            IntegrityEntity.Media,
            "An ACTIVE Media does not have exactly one OWNER",
            """
            SELECT a.media_id, 'owners=' || COUNT(pa.profile_id)
            FROM media a
            LEFT JOIN profile_media pa
              ON pa.media_id = a.media_id AND pa.relation_type = 'OWNER'
            WHERE a.state = 'ACTIVE' AND a.trashed_at_ms IS NULL
            GROUP BY a.media_id
            HAVING COUNT(pa.profile_id) <> 1;
            """),
        new(
            HealthFindingCode.OwnerCountInvalid,
            HealthSeverity.Critical,
            IntegrityEntity.Media,
            "A CANDIDATE or RETIRED Media still holds an OWNER relation",
            """
            SELECT a.media_id, a.state
            FROM media a
            JOIN profile_media pa
              ON pa.media_id = a.media_id AND pa.relation_type = 'OWNER'
            WHERE a.state IN ('CANDIDATE', 'RETIRED');
            """),
        new(
            HealthFindingCode.ActiveFingerprintMissing,
            HealthSeverity.Critical,
            IntegrityEntity.Media,
            "An ACTIVE Media is missing a trusted fingerprint or a resolved current path",
            """
            SELECT media_id,
                   'sha256=' || CASE WHEN sha256 IS NULL THEN 'missing' ELSE 'present' END
                   || ' path_state=' || path_state
            FROM media
            WHERE state = 'ACTIVE'
              AND trashed_at_ms IS NULL
              AND (
                    sha256 IS NULL
                    OR byte_length IS NULL
                    OR current_managed_relative_path IS NULL
                    OR current_managed_file_name IS NULL
                  );
            """),
        new(
            HealthFindingCode.IdentityCardinalityInvalid,
            HealthSeverity.Critical,
            IntegrityEntity.Profile,
            "An active NORMAL Profile does not have exactly one active Identity",
            """
            SELECT p.profile_id, 'identities=' || COUNT(i.identity_id)
            FROM profiles p
            LEFT JOIN identities i ON i.profile_id = p.profile_id AND i.is_active = 1
            WHERE p.kind = 'NORMAL' AND p.trashed_at_ms IS NULL
            GROUP BY p.profile_id
            HAVING COUNT(i.identity_id) <> 1;
            """),
        new(
            HealthFindingCode.IdentityCardinalityInvalid,
            HealthSeverity.Critical,
            IntegrityEntity.Profile,
            "An active UNKNOWN Profile holds an active Identity",
            """
            SELECT p.profile_id, i.identity_id
            FROM profiles p
            JOIN identities i ON i.profile_id = p.profile_id AND i.is_active = 1
            WHERE p.kind = 'UNKNOWN' AND p.trashed_at_ms IS NULL;
            """),
        new(
            HealthFindingCode.UnknownSequenceInvalid,
            HealthSeverity.Error,
            IntegrityEntity.Profile,
            "A Profile's UnknownSequence does not match its Kind or is not a positive value",
            """
            SELECT profile_id,
                   'kind=' || kind || ' sequence='
                   || CASE WHEN unknown_sequence IS NULL THEN 'null'
                           ELSE CAST(unknown_sequence AS TEXT) END
            FROM profiles
            WHERE (kind = 'UNKNOWN' AND (unknown_sequence IS NULL OR unknown_sequence <= 0))
               OR (kind = 'NORMAL' AND unknown_sequence IS NOT NULL);
            """),
        new(
            HealthFindingCode.AppearanceReferenceInvalid,
            HealthSeverity.Error,
            IntegrityEntity.Profile,
            "A Profile's Cover or Banner points at media that cannot legally serve that role",
            """
            SELECT p.profile_id,
                   'cover=' || coalesce(p.cover_media_id, '-')
                   || ' bannerAsset=' || coalesce(pa.banner_media_asset_id, '-')
            FROM profiles p
            LEFT JOIN media cover ON cover.media_id = p.cover_media_id
            LEFT JOIN profile_appearance pa ON pa.profile_id = p.profile_id
            LEFT JOIN media_assets banner_asset ON banner_asset.media_asset_id = pa.banner_media_asset_id
            LEFT JOIN media banner ON banner.media_id = banner_asset.media_id
            WHERE p.trashed_at_ms IS NULL
              AND (
                    (p.cover_media_id IS NOT NULL
                     AND (cover.media_id IS NULL
                          OR cover.state <> 'ACTIVE'
                          OR cover.trashed_at_ms IS NOT NULL
                          OR cover.media_type NOT IN ('IMAGE', 'VIDEO')))
                    OR (pa.banner_media_asset_id IS NOT NULL
                        AND (banner_asset.media_asset_id IS NULL
                             OR banner_asset.role <> 'HOVER'
                             OR banner_asset.state <> 'READY'
                             OR banner.media_id IS NULL
                             OR banner.state <> 'ACTIVE'
                             OR banner.trashed_at_ms IS NOT NULL
                             OR banner.media_type <> 'VIDEO'))
                    OR (p.kind = 'UNKNOWN' AND p.cover_media_id IS NOT NULL)
                  );
            """),
        new(
            HealthFindingCode.ReconciliationInconsistent,
            HealthSeverity.Warning,
            IntegrityEntity.Profile,
            "A Profile's current/target placement state is internally inconsistent",
            """
            SELECT profile_id, 'path_state=' || path_state
            FROM profiles
            WHERE (path_state <> 'NONE' AND reconciliation_operation_id IS NULL)
               OR (path_state = 'NONE'
                   AND target_managed_relative_path IS NOT NULL
                   AND current_managed_relative_path IS NOT NULL
                   AND current_managed_relative_path <> target_managed_relative_path);
            """),
        new(
            HealthFindingCode.ReconciliationInconsistent,
            HealthSeverity.Warning,
            IntegrityEntity.Media,
            "An Media's current/target placement state is internally inconsistent",
            """
            SELECT media_id, 'path_state=' || path_state
            FROM media
            WHERE (path_state <> 'NONE' AND reconciliation_operation_id IS NULL)
               OR (path_state = 'NONE'
                   AND target_managed_file_name IS NOT NULL
                   AND current_managed_file_name IS NOT NULL
                   AND (current_managed_file_name <> target_managed_file_name
                        OR coalesce(current_managed_relative_path, '')
                           <> coalesce(target_managed_relative_path, '')));
            """),
        new(
            HealthFindingCode.SourceDeletePending,
            HealthSeverity.Warning,
            IntegrityEntity.Media,
            "An Import item is stuck waiting for source cleanup",
            """
            SELECT coalesce(ii.candidate_media_id, ii.import_item_id), ii.source_cleanup_state
            FROM import_items ii
            WHERE ii.source_cleanup_state = 'SOURCE_DELETE_PENDING';
            """),
        new(
            HealthFindingCode.SourceDeleteFailed,
            HealthSeverity.Warning,
            IntegrityEntity.Media,
            "An Import item's source cleanup failed and remains unresolved",
            """
            SELECT coalesce(ii.candidate_media_id, ii.import_item_id), ii.source_cleanup_state
            FROM import_items ii
            WHERE ii.source_cleanup_state = 'SOURCE_DELETE_FAILED';
            """),
        new(
            HealthFindingCode.SourceChanged,
            HealthSeverity.Warning,
            IntegrityEntity.Media,
            "A committed import item's source file changed before cleanup completed",
            """
            SELECT coalesce(ii.candidate_media_id, ii.import_item_id), ii.source_cleanup_state
            FROM import_items ii
            WHERE ii.source_cleanup_state = 'SOURCE_CHANGED';
            """),
        new(
            HealthFindingCode.FaceRecordOrphaned,
            HealthSeverity.Error,
            IntegrityEntity.Media,
            "A FaceDetection points at an Identity that is no longer active",
            """
            SELECT fd.media_id, 'face=' || fd.face_id
            FROM face_detections fd
            LEFT JOIN identities confirmed ON confirmed.identity_id = fd.confirmed_identity_id
            LEFT JOIN profiles confirmed_profile ON confirmed_profile.profile_id = confirmed.profile_id
            LEFT JOIN identities suggested ON suggested.identity_id = fd.suggested_identity_id
            WHERE (fd.confirmed_identity_id IS NOT NULL
                   AND (confirmed.identity_id IS NULL
                        OR (confirmed.is_active = 0
                            AND (confirmed_profile.profile_id IS NULL
                                 OR confirmed_profile.trashed_at_ms IS NULL))))
               OR (fd.suggested_identity_id IS NOT NULL AND suggested.identity_id IS NULL);
            """),
        new(
            HealthFindingCode.FaceRecordOrphaned,
            HealthSeverity.Error,
            IntegrityEntity.Media,
            "An IdentitySample references a face detection that is no longer confirmed to it",
            """
            SELECT fd.media_id, 'sample=' || s.identity_sample_id
            FROM identity_samples s
            JOIN face_detections fd ON fd.face_id = s.face_id
            WHERE fd.decision_state <> 'CONFIRMED'
               OR fd.confirmed_identity_id IS NULL
               OR fd.confirmed_identity_id <> s.identity_id;
            """),
        new(
            HealthFindingCode.EmbeddingProvenanceInvalid,
            HealthSeverity.Error,
            IntegrityEntity.Media,
            "A stored embedding has missing or inconsistent space provenance",
            """
            SELECT fd.media_id, 'face=' || fd.face_id
            FROM face_detections fd
            WHERE (fd.embedding IS NOT NULL
                   AND (fd.embedding_space_key IS NULL OR trim(fd.embedding_space_key) = ''))
               OR (fd.embedding IS NULL AND fd.embedding_space_key IS NOT NULL);
            """),
        new(
            HealthFindingCode.EmbeddingProvenanceInvalid,
            HealthSeverity.Error,
            IntegrityEntity.Media,
            "An IdentitySample embedding space disagrees with the detection it came from",
            """
            SELECT fd.media_id, 'sample=' || s.identity_sample_id
            FROM identity_samples s
            JOIN face_detections fd ON fd.face_id = s.face_id
            WHERE fd.embedding_space_key IS NOT NULL
              AND fd.embedding_space_key <> s.embedding_space_key;
            """),
        new(
            HealthFindingCode.ManualRelatedPairInvalid,
            HealthSeverity.Error,
            IntegrityEntity.Profile,
            "A manual Related pair names an endpoint that cannot legally be related",
            """
            SELECT e.profile_id_low, 'other=' || e.profile_id_high
            FROM related_profile_evidence e
            LEFT JOIN profiles low ON low.profile_id = e.profile_id_low
            LEFT JOIN profiles high ON high.profile_id = e.profile_id_high
            WHERE e.evidence_type = 'MANUAL'
              AND (low.profile_id IS NULL
                   OR high.profile_id IS NULL
                   OR low.kind <> 'NORMAL'
                   OR high.kind <> 'NORMAL'
                   OR e.profile_id_low = e.profile_id_high);
            """),
        new(
            HealthFindingCode.RelatedSummaryMismatch,
            HealthSeverity.Warning,
            IntegrityEntity.Profile,
            "A Related summary disagrees with the evidence it is derived from",
            """
            SELECT s.profile_id_low,
                   'other=' || s.profile_id_high
                   || ' stored=' || s.shared_media_count || '/' || s.confirmed_face_count
                   || ' actual=' || coalesce(e.shared_assets, 0) || '/' || coalesce(e.confirmed_faces, 0)
            FROM related_profile_summary s
            LEFT JOIN (
                SELECT ev.profile_id_low,
                       ev.profile_id_high,
                       COUNT(DISTINCT CASE WHEN ev.evidence_type = 'SHARED_ASSET' THEN ev.media_id END)
                           AS shared_assets,
                       COUNT(DISTINCT CASE WHEN ev.evidence_type = 'CONFIRMED_FACE' THEN ev.face_id END)
                           AS confirmed_faces
                FROM related_profile_evidence ev
                LEFT JOIN media a ON a.media_id = ev.media_id
                WHERE ev.media_id IS NULL OR (a.state = 'ACTIVE' AND a.trashed_at_ms IS NULL)
                GROUP BY ev.profile_id_low, ev.profile_id_high
            ) e ON e.profile_id_low = s.profile_id_low AND e.profile_id_high = s.profile_id_high
            WHERE s.shared_media_count <> coalesce(e.shared_assets, 0)
               OR s.confirmed_face_count <> coalesce(e.confirmed_faces, 0);
            """),
        new(
            HealthFindingCode.JobStuck,
            HealthSeverity.Warning,
            IntegrityEntity.Job,
            "A durable job is claimed but no process can be holding its lease",
            """
            SELECT job_id, 'state=' || state || ' attempt=' || attempt || '/' || max_attempts
            FROM jobs
            WHERE state = 'RUNNING'
               OR (state = 'FAILED_RETRYABLE' AND attempt >= max_attempts);
            """),
        new(
            HealthFindingCode.ReconciliationInconsistent,
            HealthSeverity.Warning,
            IntegrityEntity.Operation,
            "A storage operation is nonterminal but no entity still owes it work",
            """
            SELECT o.operation_id, 'kind=' || o.kind || ' state=' || o.state
            FROM storage_operations o
            WHERE o.state NOT IN ('COMPLETED', 'FAILED', 'CANCELLED')
              AND NOT (o.kind = 'IMPORT_COMMIT' AND o.state = 'TERMINAL')
              AND o.kind <> 'LIBRARY_REPAIR'
              AND NOT EXISTS (
                    SELECT 1 FROM profiles p WHERE p.reconciliation_operation_id = o.operation_id)
              AND NOT EXISTS (
                    SELECT 1 FROM media a WHERE a.reconciliation_operation_id = o.operation_id)
              AND NOT EXISTS (
                    SELECT 1 FROM import_units u WHERE u.commit_operation_id = o.operation_id);
            """),
        new(
            HealthFindingCode.ProfileManifestMalformed,
            HealthSeverity.Warning,
            IntegrityEntity.Profile,
            "A persisted appearance or layout setting is not valid JSON",
            """
            SELECT pa.profile_id, 'overrides_json'
            FROM profile_appearance pa
            JOIN profiles p ON p.profile_id = pa.profile_id
            WHERE p.trashed_at_ms IS NULL
              AND json_valid(pa.overrides_json) = 0;
            """),
    ];
}
