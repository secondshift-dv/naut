using Microsoft.Data.Sqlite;
using Neuterradise.App.Import;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Import.Preparation;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed class ImportReads
{
    private readonly CatalogConnectionFactory _connectionFactory;

    public ImportReads(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
    }

    public ImportReads(CatalogConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<ImportSessionSummary?> GetSessionSummaryAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.import_session_id, s.state,
                   (SELECT COUNT(*) FROM import_units WHERE import_session_id = s.import_session_id) AS unit_count,
                   (
                       SELECT COUNT(*)
                       FROM import_items ii
                       JOIN import_units iu ON ii.import_unit_id = iu.import_unit_id
                       WHERE iu.import_session_id = s.import_session_id
                   ) AS total_item_count,
                   s.created_at_ms, s.updated_at_ms
            FROM import_sessions s
            WHERE s.import_session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", DbGuid.Format(sessionId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ImportSessionSummary(
            sessionId,
            DbEnum.ParseImportSessionState(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetInt32(3),
            DbTime.Parse(reader.GetInt64(4)),
            DbTime.Parse(reader.GetInt64(5)));
    }

    public async Task<ImportUnitSummary?> GetUnitSummaryAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.import_unit_id, u.import_session_id, u.parent_import_unit_id,
                   u.source_kind, u.source_display_name, u.source_path_or_reference,
                   u.state,
                   (SELECT COUNT(*) FROM import_items WHERE import_unit_id = u.import_unit_id) AS total_items,
                   (SELECT COUNT(*) FROM import_items WHERE import_unit_id = u.import_unit_id AND disposition = 'INCLUDED') AS included_count,
                   (SELECT COUNT(*) FROM import_items WHERE import_unit_id = u.import_unit_id AND disposition = 'SKIPPED') AS skipped_count,
                   (SELECT COUNT(*) FROM import_items WHERE import_unit_id = u.import_unit_id AND duplicate_decision IS NULL AND EXISTS (SELECT 1 FROM media ca JOIN media aa ON aa.sha256 = ca.sha256 AND aa.byte_length = ca.byte_length AND aa.state = 'ACTIVE' AND aa.media_id <> ca.media_id WHERE ca.media_id = import_items.candidate_media_id)) AS dup_count,
                   (SELECT COUNT(*) FROM import_items WHERE import_unit_id = u.import_unit_id AND disposition = 'INVALID') AS needs_attn_count,
                   u.created_at_ms, u.updated_at_ms,
                   u.destination_profile_id, u.library_commit_state, u.verification_step,
                   u.is_paused,
                   (SELECT COUNT(*) FROM import_items WHERE import_unit_id = u.import_unit_id
                       AND source_cleanup_state IN ('SOURCE_DELETE_FAILED','SOURCE_CHANGED')) AS cleanup_failed_count,
                   (SELECT COUNT(*) FROM import_items WHERE import_unit_id = u.import_unit_id
                       AND source_cleanup_state IN ('LIBRARY_COMMITTED','SOURCE_DELETE_PENDING')) AS cleanup_pending_count,
                   (SELECT COUNT(*) FROM import_items WHERE import_unit_id = u.import_unit_id
                       AND source_cleanup_state = 'SOURCE_CONSUMED') AS cleanup_consumed_count,
                   (SELECT COUNT(*) FROM import_items WHERE import_unit_id = u.import_unit_id
                       AND source_cleanup_state = 'SOURCE_PRESERVED') AS cleanup_preserved_count,
                   u.preparation_started_at_ms
            FROM import_units u
            WHERE u.import_unit_id = $unitId;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var sessionId = DbGuid.Parse(reader.GetString(1));
        var parentUnitId = reader.IsDBNull(2) ? (Guid?)null : DbGuid.Parse(reader.GetString(2));
        var sourceKind = reader.GetString(3);
        var sourceDisplayName = reader.GetString(4);
        var sourceReference = reader.IsDBNull(5) ? null : reader.GetString(5);
        var state = DbEnum.ParseImportUnitState(reader.GetString(6));
        var totalItems = reader.GetInt32(7);
        var includedCount = reader.GetInt32(8);
        var skippedCount = reader.GetInt32(9);
        var dupCount = reader.GetInt32(10);
        var needsAttnCount = reader.GetInt32(11);
        var createdAt = DbTime.Parse(reader.GetInt64(12));
        var updatedAt = DbTime.Parse(reader.GetInt64(13));

        return new ImportUnitSummary(
            unitId,
            sessionId,
            parentUnitId,
            sourceKind,
            sourceDisplayName,
            sourceReference,
            state,
            totalItems,
            includedCount,
            skippedCount,
            dupCount,
            needsAttnCount,
            createdAt,
            updatedAt,
            DestinationProfileId: reader.IsDBNull(14) ? null : DbGuid.Parse(reader.GetString(14)),
            LibraryCommitState: DbEnum.ParseImportCommitCheckpointOrDefault(reader.GetString(15)),
            VerificationStep: reader.GetInt32(16),
            CleanupFailedCount: reader.GetInt32(18),
            IsPaused: reader.GetBoolean(17),
            CleanupConsumedCount: reader.GetInt32(20),
            CleanupPreservedCount: reader.GetInt32(21),
            CleanupPendingCount: reader.GetInt32(19),
            StageTwoStartedAtUtc: reader.IsDBNull(22) ? null : DbTime.Parse(reader.GetInt64(22)));
    }

    public async Task<VerificationReadModel?> GetVerificationReadModelAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.import_unit_id, u.import_session_id, u.parent_import_unit_id,
                   u.source_kind, u.source_display_name, u.source_path_or_reference,
                   u.state, u.destination_kind, u.destination_profile_id,
                   u.verification_step, u.verification_draft_json, u.verification_version,
                   u.commit_operation_id, u.library_commit_state,
                   (SELECT COUNT(*) FROM import_items WHERE import_unit_id = u.import_unit_id) AS total_items,
                   (SELECT COUNT(*) FROM import_items WHERE import_unit_id = u.import_unit_id AND disposition = 'INCLUDED') AS included_count,
                   (SELECT COUNT(*) FROM import_items WHERE import_unit_id = u.import_unit_id AND disposition = 'SKIPPED') AS skipped_count,
                   (SELECT COUNT(*) FROM import_items WHERE import_unit_id = u.import_unit_id AND duplicate_decision IS NULL AND EXISTS (SELECT 1 FROM media ca JOIN media aa ON aa.sha256 = ca.sha256 AND aa.byte_length = ca.byte_length AND aa.state = 'ACTIVE' AND aa.media_id <> ca.media_id WHERE ca.media_id = import_items.candidate_media_id)) AS dup_count,
                   (SELECT COUNT(*) FROM import_items WHERE import_unit_id = u.import_unit_id AND disposition = 'INVALID') AS needs_attn_count,
                   u.created_at_ms, u.updated_at_ms, u.completed_at_ms, u.row_version
            FROM import_units u
            WHERE u.import_unit_id = $unitId;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var sessionId = DbGuid.Parse(reader.GetString(1));
        var parentUnitId = reader.IsDBNull(2) ? (Guid?)null : DbGuid.Parse(reader.GetString(2));
        var sourceKind = reader.GetString(3);
        var sourceDisplayName = reader.GetString(4);
        var sourceReference = reader.IsDBNull(5) ? null : reader.GetString(5);
        var state = DbEnum.ParseImportUnitState(reader.GetString(6));
        var destinationKind = reader.IsDBNull(7) ? null : reader.GetString(7);
        var destinationProfileId = reader.IsDBNull(8) ? (Guid?)null : DbGuid.Parse(reader.GetString(8));
        var step = reader.GetInt32(9);
        var draftJson = reader.GetString(10);
        var draftVersion = reader.GetInt32(11);
        var commitOpId = reader.IsDBNull(12) ? null : reader.GetString(12);
        var commitState = DbEnum.ParseImportCommitCheckpointOrDefault(reader.GetString(13));
        var totalItems = reader.GetInt32(14);
        var includedCount = reader.GetInt32(15);
        var skippedCount = reader.GetInt32(16);
        var dupCount = reader.GetInt32(17);
        var needsAttnCount = reader.GetInt32(18);
        var createdAt = DbTime.Parse(reader.GetInt64(19));
        var updatedAt = DbTime.Parse(reader.GetInt64(20));
        var completedAt = reader.IsDBNull(21) ? (DateTimeOffset?)null : DbTime.Parse(reader.GetInt64(21));
        var rowVersion = reader.GetInt64(22);

        return new VerificationReadModel(
            unitId,
            sessionId,
            parentUnitId,
            sourceKind,
            sourceDisplayName,
            sourceReference,
            state,
            destinationKind,
            destinationProfileId,
            step,
            draftJson,
            draftVersion,
            commitOpId,
            commitState,
            totalItems,
            includedCount,
            skippedCount,
            dupCount,
            needsAttnCount,
            createdAt,
            updatedAt,
            completedAt,
            rowVersion);
    }

    /// <summary>
    /// Presentation-oriented item read with default paging. NOT for lifecycle-critical decisions.
    /// Use <see cref="GetUnitItemsUnboundedAsync"/> for admission/finalizer/lifecycle operations.
    /// </summary>
    public async Task<IReadOnlyList<ImportItemSummary>> GetUnitItemsAsync(
        Guid unitId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return [];
        }

        limit = Math.Clamp(limit, 1, 1000);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ii.import_item_id, ii.import_unit_id, ii.candidate_media_id,
                   ii.reused_media_id, ii.source_path, ii.source_file_name,
                   ii.source_byte_length, ii.source_last_write_ms,
                   ii.disposition, ii.duplicate_decision,
                   ii.source_cleanup_state, ii.source_cleanup_error,
                   a.media_type, ii.created_at_ms, ii.updated_at_ms,
                   ii.cleanup_policy
            FROM import_items ii
            LEFT JOIN media a ON ii.candidate_media_id = a.media_id
            WHERE ii.import_unit_id = $unitId
            ORDER BY ii.created_at_ms ASC, ii.import_item_id ASC
            LIMIT {limit};
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var items = new List<ImportItemSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var itemId = DbGuid.Parse(reader.GetString(0));
            var candidateMediaId = reader.IsDBNull(2) ? (Guid?)null : DbGuid.Parse(reader.GetString(2));
            var reusedMediaId = reader.IsDBNull(3) ? (Guid?)null : DbGuid.Parse(reader.GetString(3));
            var sourcePath = reader.GetString(4);
            var sourceFileName = reader.GetString(5);
            var byteLength = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6);
            var lastWrite = reader.IsDBNull(7) ? (DateTimeOffset?)null : DbTime.Parse(reader.GetInt64(7));
            var disposition = DbEnum.ParseItemDisposition(reader.GetString(8));
            var duplicateDecision = DbEnum.ParseDuplicateDecisionOrNull(
                reader.IsDBNull(9) ? null : reader.GetString(9));
            var cleanupState = DbEnum.ParseSourceCleanupState(reader.GetString(10));
            var cleanupError = reader.IsDBNull(11) ? null : reader.GetString(11);
            var mediaType = reader.IsDBNull(12) ? null : (Media.MediaType?)DbEnum.ParseMediaType(reader.GetString(12));
            var createdAt = DbTime.Parse(reader.GetInt64(13));
            var updatedAt = DbTime.Parse(reader.GetInt64(14));
            var cleanupPolicy = reader.IsDBNull(15) ? ImportCleanupPolicy.Copy : DbEnum.ParseImportCleanupPolicy(reader.GetString(15));

            items.Add(new ImportItemSummary(
                itemId,
                unitId,
                candidateMediaId,
                reusedMediaId,
                sourcePath,
                sourceFileName,
                byteLength,
                lastWrite,
                disposition,
                duplicateDecision,
                cleanupState,
                cleanupError,
                mediaType,
                createdAt,
                updatedAt,
                cleanupPolicy));
        }

        return items;
    }

    /// <summary>
    /// Lifecycle-critical item read: no paging, no limit. Use this for admission, finalizer,
    /// and lifecycle decisions where the complete Import Unit dataset must be considered.
    /// </summary>
    public async Task<IReadOnlyList<ImportItemSummary>> GetUnitItemsUnboundedAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ii.import_item_id, ii.import_unit_id, ii.candidate_media_id,
                   ii.reused_media_id, ii.source_path, ii.source_file_name,
                   ii.source_byte_length, ii.source_last_write_ms,
                   ii.disposition, ii.duplicate_decision,
                   ii.source_cleanup_state, ii.source_cleanup_error,
                   a.media_type, ii.created_at_ms, ii.updated_at_ms,
                   ii.cleanup_policy
            FROM import_items ii
            LEFT JOIN media a ON ii.candidate_media_id = a.media_id
            WHERE ii.import_unit_id = $unitId
            ORDER BY ii.created_at_ms ASC, ii.import_item_id ASC;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var items = new List<ImportItemSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var itemId = DbGuid.Parse(reader.GetString(0));
            var candidateMediaId = reader.IsDBNull(2) ? (Guid?)null : DbGuid.Parse(reader.GetString(2));
            var reusedMediaId = reader.IsDBNull(3) ? (Guid?)null : DbGuid.Parse(reader.GetString(3));
            var sourcePath = reader.GetString(4);
            var sourceFileName = reader.GetString(5);
            var byteLength = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6);
            var lastWrite = reader.IsDBNull(7) ? (DateTimeOffset?)null : DbTime.Parse(reader.GetInt64(7));
            var disposition = DbEnum.ParseItemDisposition(reader.GetString(8));
            var duplicateDecision = DbEnum.ParseDuplicateDecisionOrNull(
                reader.IsDBNull(9) ? null : reader.GetString(9));
            var cleanupState = DbEnum.ParseSourceCleanupState(reader.GetString(10));
            var cleanupError = reader.IsDBNull(11) ? null : reader.GetString(11);
            var mediaType = reader.IsDBNull(12) ? null : (Media.MediaType?)DbEnum.ParseMediaType(reader.GetString(12));
            var createdAt = DbTime.Parse(reader.GetInt64(13));
            var updatedAt = DbTime.Parse(reader.GetInt64(14));
            var cleanupPolicy = reader.IsDBNull(15) ? ImportCleanupPolicy.Copy : DbEnum.ParseImportCleanupPolicy(reader.GetString(15));

            items.Add(new ImportItemSummary(
                itemId,
                unitId,
                candidateMediaId,
                reusedMediaId,
                sourcePath,
                sourceFileName,
                byteLength,
                lastWrite,
                disposition,
                duplicateDecision,
                cleanupState,
                cleanupError,
                mediaType,
                createdAt,
                updatedAt,
                cleanupPolicy));
        }

        return items;
    }

    public async Task<IReadOnlyList<ImportHistoryEntry>> GetRecentHistoryAsync(
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT u.import_session_id, u.import_unit_id, u.source_display_name,
                   u.source_kind, u.state,
                   (SELECT COUNT(*) FROM import_items i WHERE i.import_unit_id = u.import_unit_id),
                   u.created_at_ms, u.completed_at_ms,
                   (SELECT COUNT(*) FROM import_items i
                     WHERE i.import_unit_id = u.import_unit_id AND i.disposition = 'INCLUDED'),
                   (SELECT COUNT(*) FROM import_items i
                     WHERE i.import_unit_id = u.import_unit_id AND i.disposition = 'SKIPPED'),
                   (SELECT COUNT(*) FROM import_items i
                     WHERE i.import_unit_id = u.import_unit_id AND i.reused_media_id IS NOT NULL),
                   (SELECT COUNT(*) FROM import_items i
                     WHERE i.import_unit_id = u.import_unit_id AND i.disposition = 'INVALID'),
                   u.destination_profile_id,
                   p.display_name,
                   p.kind,
                   p.unknown_sequence,
                   u.library_commit_state,
                   (SELECT COUNT(*) FROM import_items i
                     WHERE i.import_unit_id = u.import_unit_id
                       AND i.source_cleanup_state IN ('SOURCE_DELETE_FAILED', 'SOURCE_CHANGED')),
                   (SELECT COUNT(*) FROM import_items i
                     WHERE i.import_unit_id = u.import_unit_id
                       AND i.source_cleanup_state IN ('LIBRARY_COMMITTED','SOURCE_DELETE_PENDING')),
                   (SELECT COUNT(*) FROM import_items i
                     WHERE i.import_unit_id = u.import_unit_id
                       AND i.source_cleanup_state = 'SOURCE_CONSUMED'),
                   (SELECT COUNT(*) FROM jobs j
                     JOIN import_items i ON i.candidate_media_id = j.owner_id OR i.import_item_id = j.owner_id
                     WHERE i.import_unit_id = u.import_unit_id
                       AND j.lane <> 'FACE'
                       AND j.state IN ('FAILED_TERMINAL','FAILED_RETRYABLE')),
                   (SELECT COUNT(*) FROM jobs j
                     JOIN import_items i ON i.candidate_media_id = j.owner_id
                     WHERE i.import_unit_id = u.import_unit_id
                       AND j.lane = 'FACE'
                       AND j.state IN ('PENDING','RUNNABLE','RUNNING','PAUSED','FAILED_RETRYABLE'))
            FROM import_units u
            LEFT JOIN profiles p ON p.profile_id = u.destination_profile_id
            WHERE u.hidden_from_history = 0
            ORDER BY u.created_at_ms DESC, u.import_unit_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var list = new List<ImportHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var cleanupFailed = reader.GetInt32(17);
            var cleanupPending = reader.GetInt32(18);
            var cleanupConsumed = reader.GetInt32(19);

            list.Add(new ImportHistoryEntry(
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                DbEnum.ParseImportUnitState(reader.GetString(4)),
                reader.GetInt32(5),
                DbTime.Parse(reader.GetInt64(6)),
                reader.IsDBNull(7) ? null : DbTime.Parse(reader.GetInt64(7)),
                IncludedCount: reader.GetInt32(8),
                SkippedCount: reader.GetInt32(9),
                ReusedCount: reader.GetInt32(10),
                NeedsAttentionCount: reader.GetInt32(11),
                DestinationProfileId: reader.IsDBNull(12) ? null : DbGuid.Parse(reader.GetString(12)),
                DestinationProfileLabel: DescribeDestinationProfile(reader, 13, 14, 15),
                LibraryCommitState: DbEnum.ParseImportCommitCheckpointOrDefault(reader.GetString(16)),
                SourceCleanupStatus: DescribeCleanup(cleanupFailed, cleanupPending, cleanupConsumed),
                SourceCleanupFailedCount: cleanupFailed,
                SourceCleanupPendingCount: cleanupPending,
                FailedJobCount: reader.GetInt32(20),
                OptionalProfilingRunning: reader.GetInt32(21) > 0));
        }

        return list;
    }

    private static string? DescribeDestinationProfile(
        SqliteDataReader reader,
        int displayNameOrdinal,
        int kindOrdinal,
        int unknownSequenceOrdinal)
    {
        if (reader.IsDBNull(kindOrdinal))
        {
            return null;
        }

        if (!reader.IsDBNull(displayNameOrdinal))
        {
            return reader.GetString(displayNameOrdinal);
        }

        return reader.IsDBNull(unknownSequenceOrdinal)
            ? null
            : $"Unknown {reader.GetInt64(unknownSequenceOrdinal)}";
    }

    private static ImportUnitCleanupSummary DescribeCleanup(int failed, int pending, int consumed)
    {
        if (failed > 0)
        {
            return ImportUnitCleanupSummary.NeedsAttention;
        }

        if (pending > 0)
        {
            return ImportUnitCleanupSummary.Pending;
        }

        return consumed > 0 ? ImportUnitCleanupSummary.Complete : ImportUnitCleanupSummary.None;
    }

    public async Task<IReadOnlyList<ImportUnitSummary>> ListUnitsForSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
        {
            return Array.Empty<ImportUnitSummary>();
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT u.import_unit_id, u.import_session_id, u.parent_import_unit_id,
                   u.source_kind, u.source_display_name, u.source_path_or_reference,
                   u.state, u.is_paused, u.created_at_ms, u.updated_at_ms,
                   COUNT(i.import_item_id) AS total_count,
                   SUM(CASE WHEN i.disposition = 'INCLUDED' THEN 1 ELSE 0 END) AS included_count,
                   SUM(CASE WHEN i.disposition = 'SKIPPED' THEN 1 ELSE 0 END) AS skipped_count,
                   SUM(CASE WHEN i.duplicate_decision IS NULL AND EXISTS (SELECT 1 FROM media ca JOIN media aa ON aa.sha256 = ca.sha256 AND aa.byte_length = ca.byte_length AND aa.state = 'ACTIVE' AND aa.media_id <> ca.media_id WHERE ca.media_id = i.candidate_media_id) THEN 1 ELSE 0 END) AS exact_dup_count,
                   SUM(CASE WHEN i.disposition = 'INVALID' THEN 1 ELSE 0 END) AS needs_attention_count,
                   u.destination_profile_id,
                   u.library_commit_state,
                   u.verification_step,
                   SUM(CASE WHEN i.source_cleanup_state IN ('SOURCE_DELETE_FAILED','SOURCE_CHANGED') THEN 1 ELSE 0 END) AS cleanup_failed_count,
                   SUM(CASE WHEN i.source_cleanup_state IN ('LIBRARY_COMMITTED','SOURCE_DELETE_PENDING') THEN 1 ELSE 0 END) AS cleanup_pending_count,
                   SUM(CASE WHEN i.source_cleanup_state = 'SOURCE_CONSUMED' THEN 1 ELSE 0 END) AS cleanup_consumed_count,
                   SUM(CASE WHEN i.source_cleanup_state = 'SOURCE_PRESERVED' THEN 1 ELSE 0 END) AS cleanup_preserved_count,
                   u.preparation_started_at_ms
            FROM import_units u
            LEFT JOIN import_items i ON i.import_unit_id = u.import_unit_id
            WHERE u.import_session_id = $sessionId
            GROUP BY u.import_unit_id
            ORDER BY u.created_at_ms ASC, u.import_unit_id ASC;
            """;
        command.Parameters.AddWithValue("$sessionId", DbGuid.Format(sessionId));

        var list = new List<ImportUnitSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new ImportUnitSummary(
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : DbGuid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DbEnum.ParseImportUnitState(reader.GetString(6)),
                reader.GetInt32(10),
                reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                DbTime.Parse(reader.GetInt64(8)),
                DbTime.Parse(reader.GetInt64(9)),
                reader.IsDBNull(15) ? null : DbGuid.Parse(reader.GetString(15)),
                DbEnum.ParseImportCommitCheckpointOrDefault(
                    reader.IsDBNull(16) ? null : reader.GetString(16)),
                reader.IsDBNull(17) ? 1 : reader.GetInt32(17),
                reader.IsDBNull(18) ? 0 : reader.GetInt32(18),
                IsPaused: reader.GetBoolean(7),
                CleanupConsumedCount: reader.IsDBNull(20) ? 0 : reader.GetInt32(20),
                CleanupPreservedCount: reader.IsDBNull(21) ? 0 : reader.GetInt32(21),
                CleanupPendingCount: reader.IsDBNull(19) ? 0 : reader.GetInt32(19),
                StageTwoStartedAtUtc: reader.IsDBNull(22) ? null : DbTime.Parse(reader.GetInt64(22))));
        }

        return list;
    }

    /// <summary>
    /// Import-critical progress for many units in one query: how many admitted media are fully
    /// prepared (all deterministic capabilities terminal) and how many are placed in the library,
    /// plus the destination name and the durable draft. Uses the same per-media terminal predicate
    /// as readiness: one media enters the prepared count only when every deterministic capability
    /// row for its media type exists and no capability remains QUEUED, PROCESSING, or FAILED.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, ImportUnitProgress>> ListUnitProgressAsync(
        IReadOnlyCollection<Guid> unitIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitIds);
        var result = new Dictionary<Guid, ImportUnitProgress>();
        if (unitIds.Count == 0)
        {
            return result;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var names = new List<string>(unitIds.Count);
        var index = 0;
        foreach (var unitId in unitIds)
        {
            var name = "$u" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            names.Add(name);
            command.Parameters.AddWithValue(name, DbGuid.Format(unitId));
            index++;
        }

        command.Parameters.AddWithValue("$imageCapabilityCount",
            CapabilityApplicability.GetAll(MediaType.Image).Count);
        command.Parameters.AddWithValue("$videoCapabilityCount",
            CapabilityApplicability.GetAll(MediaType.Video).Count);
        command.Parameters.AddWithValue("$modelCapabilityCount",
            CapabilityApplicability.GetAll(MediaType.Model).Count);

        command.CommandText =
            $"""
            WITH eligible_assets AS (
                SELECT ii.import_unit_id,
                       CASE
                           WHEN ii.disposition = 'REUSED' AND ii.reused_media_id IS NOT NULL
                               THEN ii.reused_media_id
                           ELSE ii.candidate_media_id
                       END AS media_id
                FROM import_items ii
                WHERE ii.import_unit_id IN ({string.Join(",", names)})
                  AND ii.disposition IN ('INCLUDED', 'REUSED')
                  AND (
                      (ii.disposition = 'REUSED' AND ii.reused_media_id IS NOT NULL)
                      OR (ii.disposition = 'INCLUDED' AND ii.candidate_media_id IS NOT NULL)
                  )
            )
            SELECT u.import_unit_id,
                   u.verification_draft_json,
                   p.display_name,
                   p.unknown_sequence,
                   COUNT(i.import_item_id),
                   SUM(CASE WHEN i.disposition IN ('INCLUDED','REUSED') THEN 1 ELSE 0 END),
                   (SELECT COUNT(*) FROM (
                       SELECT ea2.media_id
                       FROM eligible_assets ea2
                       JOIN media a2 ON a2.media_id = ea2.media_id
                       LEFT JOIN media_capability_readiness cr2 ON cr2.media_id = ea2.media_id
                       WHERE ea2.import_unit_id = u.import_unit_id
                       GROUP BY ea2.media_id
                       HAVING COUNT(cr2.capability) >= (
                           CASE WHEN a2.media_type = 'VIDEO' THEN $videoCapabilityCount
                                WHEN a2.media_type = 'MODEL' THEN $modelCapabilityCount
                                ELSE $imageCapabilityCount END
                       )
                       AND SUM(CASE WHEN cr2.state IN ('QUEUED','PROCESSING','FAILED') THEN 1 ELSE 0 END) = 0
                   ) prepared_assets),
                   SUM(CASE WHEN i.disposition IN ('INCLUDED','REUSED')
                             AND i.source_cleanup_state <> 'SOURCE_PRESENT' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN i.disposition = 'INVALID' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN i.disposition = 'REUSED' THEN 1 ELSE 0 END)
            FROM import_units u
            LEFT JOIN import_items i ON i.import_unit_id = u.import_unit_id
            LEFT JOIN profiles p ON p.profile_id = u.destination_profile_id
            WHERE u.import_unit_id IN ({string.Join(",", names)})
            GROUP BY u.import_unit_id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            static int Int(SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? 0 : r.GetInt32(ordinal);

            var unitId = DbGuid.Parse(reader.GetString(0));
            var draftJson = reader.IsDBNull(1) ? null : reader.GetString(1);
            string? destinationName = reader.IsDBNull(2)
                ? (reader.IsDBNull(3) ? null : "Unassigned media")
                : reader.GetString(2);

            result[unitId] = new ImportUnitProgress(
                unitId,
                DraftJson: draftJson,
                DestinationDisplayName: destinationName,
                TotalItemCount: Int(reader, 4),
                AdmittedItemCount: Int(reader, 5),
                PreparedItemCount: Int(reader, 6),
                PlacedItemCount: Int(reader, 7),
                UnusableItemCount: Int(reader, 8),
                AlreadyInLibraryCount: Int(reader, 9));
        }

        return result;
    }


    /// <summary>
    /// Returns durable failure provenance only for Required media preparation capabilities. Optional face,
    /// search, similarity, and presentation enrichment failures are intentionally filtered out.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ImportPreparationFailure>>> ListRequiredPreparationFailuresAsync(
        IReadOnlyCollection<Guid> unitIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitIds);
        if (unitIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<ImportPreparationFailure>>();
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var names = new List<string>(unitIds.Count);
        var parameterIndex = 0;
        foreach (var unitId in unitIds)
        {
            var name = "$f" + parameterIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            names.Add(name);
            command.Parameters.AddWithValue(name, DbGuid.Format(unitId));
            parameterIndex++;
        }

        command.CommandText =
            $"""
            WITH eligible_items AS (
                SELECT ii.import_unit_id,
                       ii.import_item_id,
                       ii.source_file_name,
                       CASE
                           WHEN ii.disposition = 'REUSED' AND ii.reused_media_id IS NOT NULL
                               THEN ii.reused_media_id
                           ELSE ii.candidate_media_id
                       END AS media_id
                FROM import_items ii
                WHERE ii.import_unit_id IN ({string.Join(",", names)})
                  AND ii.disposition IN ('INCLUDED','REUSED')
                  AND (
                      (ii.disposition = 'REUSED' AND ii.reused_media_id IS NOT NULL)
                      OR (ii.disposition = 'INCLUDED' AND ii.candidate_media_id IS NOT NULL)
                  )
            )
            SELECT ei.import_unit_id,
                   ei.import_item_id,
                   ei.source_file_name,
                   ei.media_id,
                   a.media_type,
                   cr.capability,
                   cr.job_id,
                   j.kind,
                   j.state,
                   j.error_code,
                   j.error_detail_safe
            FROM eligible_items ei
            JOIN media a ON a.media_id = ei.media_id
            JOIN media_capability_readiness cr ON cr.media_id = ei.media_id
            LEFT JOIN jobs j ON j.job_id = cr.job_id
            WHERE cr.state = 'FAILED'
            ORDER BY ei.import_unit_id, ei.import_item_id, cr.capability;
            """;

        var grouped = new Dictionary<Guid, List<ImportPreparationFailure>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var mediaType = DbEnum.ParseMediaType(reader.GetString(4));
            var capability = DbEnum.ParseMediaCapability(reader.GetString(5));
            if (!CapabilityApplicability.GetRequired(mediaType).Contains(capability))
            {
                continue;
            }

            var unitId = DbGuid.Parse(reader.GetString(0));
            if (!grouped.TryGetValue(unitId, out var failures))
            {
                failures = [];
                grouped.Add(unitId, failures);
            }

            failures.Add(new ImportPreparationFailure(
                unitId,
                DbGuid.Parse(reader.GetString(1)),
                reader.GetString(2),
                DbGuid.Parse(reader.GetString(3)),
                mediaType,
                capability.ToString(),
                reader.IsDBNull(6) ? null : DbGuid.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return grouped.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<ImportPreparationFailure>)pair.Value);
    }

    public async Task<IReadOnlyList<Guid>> ListVisibleFailedTerminalUnitIdsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT import_unit_id
            FROM import_units
            WHERE hidden_from_history = 0
              AND state = 'FAILED_TERMINAL'
            ORDER BY created_at_ms ASC, import_unit_id ASC;
            """;

        var unitIds = new List<Guid>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            unitIds.Add(DbGuid.Parse(reader.GetString(0), DomainIdKind.ImportUnit));
        }

        return unitIds;
    }

    public async Task<IReadOnlyList<ImportUnitSummary>> ListRecentUnitsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT u.import_unit_id, u.import_session_id, u.parent_import_unit_id,
                   u.source_kind, u.source_display_name, u.source_path_or_reference,
                   u.state, u.is_paused, u.created_at_ms, u.updated_at_ms,
                   COUNT(i.import_item_id) AS total_count,
                   SUM(CASE WHEN i.disposition = 'INCLUDED' THEN 1 ELSE 0 END) AS included_count,
                   SUM(CASE WHEN i.disposition = 'SKIPPED' THEN 1 ELSE 0 END) AS skipped_count,
                   SUM(CASE WHEN i.duplicate_decision IS NULL AND EXISTS (SELECT 1 FROM media ca JOIN media aa ON aa.sha256 = ca.sha256 AND aa.byte_length = ca.byte_length AND aa.state = 'ACTIVE' AND aa.media_id <> ca.media_id WHERE ca.media_id = i.candidate_media_id) THEN 1 ELSE 0 END) AS exact_dup_count,
                   SUM(CASE WHEN i.disposition = 'INVALID' THEN 1 ELSE 0 END) AS needs_attention_count,
                   u.destination_profile_id,
                   u.library_commit_state,
                   u.verification_step,
                   SUM(CASE WHEN i.source_cleanup_state IN ('SOURCE_DELETE_FAILED','SOURCE_CHANGED') THEN 1 ELSE 0 END) AS cleanup_failed_count,
                   SUM(CASE WHEN i.source_cleanup_state IN ('LIBRARY_COMMITTED','SOURCE_DELETE_PENDING') THEN 1 ELSE 0 END) AS cleanup_pending_count,
                   SUM(CASE WHEN i.source_cleanup_state = 'SOURCE_CONSUMED' THEN 1 ELSE 0 END) AS cleanup_consumed_count,
                   SUM(CASE WHEN i.source_cleanup_state = 'SOURCE_PRESERVED' THEN 1 ELSE 0 END) AS cleanup_preserved_count,
                   u.preparation_started_at_ms
            FROM import_units u
            LEFT JOIN import_items i ON i.import_unit_id = u.import_unit_id
            WHERE u.hidden_from_history = 0
               OR u.state NOT IN ('COMMITTED','COMPLETED','CANCELLED')
            GROUP BY u.import_unit_id
            ORDER BY u.created_at_ms DESC, u.import_unit_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        var list = new List<ImportUnitSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new ImportUnitSummary(
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : DbGuid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DbEnum.ParseImportUnitState(reader.GetString(6)),
                reader.GetInt32(10),
                reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                DbTime.Parse(reader.GetInt64(8)),
                DbTime.Parse(reader.GetInt64(9)),
                reader.IsDBNull(15) ? null : DbGuid.Parse(reader.GetString(15)),
                DbEnum.ParseImportCommitCheckpointOrDefault(
                    reader.IsDBNull(16) ? null : reader.GetString(16)),
                reader.IsDBNull(17) ? 1 : reader.GetInt32(17),
                reader.IsDBNull(18) ? 0 : reader.GetInt32(18),
                IsPaused: reader.GetBoolean(7),
                CleanupConsumedCount: reader.IsDBNull(20) ? 0 : reader.GetInt32(20),
                CleanupPreservedCount: reader.IsDBNull(21) ? 0 : reader.GetInt32(21),
                CleanupPendingCount: reader.IsDBNull(19) ? 0 : reader.GetInt32(19),
                StageTwoStartedAtUtc: reader.IsDBNull(22) ? null : DbTime.Parse(reader.GetInt64(22))));
        }

        return list;
    }

    /// <summary>
    /// Exact-duplicate matches for one unit, computed the canonical way: SHA-256 plus
    /// byte length against other media. The match state decides how the surface reacts, and an item
    /// whose <c>duplicate_decision</c> is still NULL is a decision the user must make.
    /// </summary>
    public async Task<IReadOnlyList<ImportDuplicateMatch>> ListExactDuplicateMatchesAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return Array.Empty<ImportDuplicateMatch>();
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.import_item_id, a.media_id, a.state, i.duplicate_decision
            FROM import_items i
            JOIN media c ON c.media_id = i.candidate_media_id
            JOIN media a ON a.sha256 = c.sha256
                         AND a.byte_length = c.byte_length
                         AND a.media_id <> c.media_id
                         AND a.state IN ('ACTIVE','TRASHED')
            WHERE i.import_unit_id = $unitId
              AND c.sha256 IS NOT NULL
              AND c.byte_length IS NOT NULL
            ORDER BY i.created_at_ms ASC, i.import_item_id ASC;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var matches = new List<ImportDuplicateMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            matches.Add(new ImportDuplicateMatch(
                DbGuid.Parse(reader.GetString(0), DomainIdKind.ImportItem),
                DbGuid.Parse(reader.GetString(1), DomainIdKind.Media),
                DbEnum.ParseMediaState(reader.GetString(2)),
                DbEnum.ParseDuplicateDecisionOrNull(reader.IsDBNull(3) ? null : reader.GetString(3))));
        }

        return matches;
    }

    public async Task<ImportItemSummary?> GetItemSummaryAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        if (itemId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.import_item_id, i.import_unit_id, i.candidate_media_id, i.reused_media_id,
                   i.source_path, i.source_file_name, i.source_byte_length, i.source_last_write_ms,
                   i.disposition, i.duplicate_decision, i.source_cleanup_state, i.source_cleanup_error,
                   a.media_type, i.created_at_ms, i.updated_at_ms,
                   i.cleanup_policy
            FROM import_items i
            LEFT JOIN media a ON a.media_id = i.candidate_media_id
            WHERE i.import_item_id = $itemId;
            """;
        command.Parameters.AddWithValue("$itemId", DbGuid.Format(itemId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ImportItemSummary(
            DbGuid.Parse(reader.GetString(0)),
            DbGuid.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : DbGuid.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : DbGuid.Parse(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : DbTime.Parse(reader.GetInt64(7)),
            DbEnum.ParseItemDisposition(reader.GetString(8)),
            DbEnum.ParseDuplicateDecisionOrNull(reader.IsDBNull(9) ? null : reader.GetString(9)),
            DbEnum.ParseSourceCleanupState(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : DbEnum.ParseMediaType(reader.GetString(12)),
            DbTime.Parse(reader.GetInt64(13)),
            DbTime.Parse(reader.GetInt64(14)),
            reader.IsDBNull(15) ? ImportCleanupPolicy.Copy : DbEnum.ParseImportCleanupPolicy(reader.GetString(15)));
    }

    public async Task<ImportItemSummary?> GetItemByCandidateMediaIdAsync(
        Guid candidateMediaId,
        CancellationToken cancellationToken = default)
    {
        if (candidateMediaId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.import_item_id, i.import_unit_id, i.candidate_media_id, i.reused_media_id,
                   i.source_path, i.source_file_name, i.source_byte_length, i.source_last_write_ms,
                   i.disposition, i.duplicate_decision, i.source_cleanup_state, i.source_cleanup_error,
                   a.media_type, i.created_at_ms, i.updated_at_ms,
                   i.cleanup_policy
            FROM import_items i
            LEFT JOIN media a ON a.media_id = i.candidate_media_id
            WHERE i.candidate_media_id = $candidateMediaId;
            """;
        command.Parameters.AddWithValue("$candidateMediaId", DbGuid.Format(candidateMediaId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ImportItemSummary(
            DbGuid.Parse(reader.GetString(0)),
            DbGuid.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : DbGuid.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : DbGuid.Parse(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : DbTime.Parse(reader.GetInt64(7)),
            DbEnum.ParseItemDisposition(reader.GetString(8)),
            DbEnum.ParseDuplicateDecisionOrNull(reader.IsDBNull(9) ? null : reader.GetString(9)),
            DbEnum.ParseSourceCleanupState(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : DbEnum.ParseMediaType(reader.GetString(12)),
            DbTime.Parse(reader.GetInt64(13)),
            DbTime.Parse(reader.GetInt64(14)),
            reader.IsDBNull(15) ? ImportCleanupPolicy.Copy : DbEnum.ParseImportCleanupPolicy(reader.GetString(15)));
    }

    public async Task<ImportItemSummary?> GetItemBySourcePathAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.import_item_id, i.import_unit_id, i.candidate_media_id, i.reused_media_id,
                   i.source_path, i.source_file_name, i.source_byte_length, i.source_last_write_ms,
                   i.disposition, i.duplicate_decision, i.source_cleanup_state, i.source_cleanup_error,
                   a.media_type, i.created_at_ms, i.updated_at_ms,
                   i.cleanup_policy
            FROM import_items i
            LEFT JOIN media a ON a.media_id = i.candidate_media_id
            WHERE i.source_path = $sourcePath;
            """;
        command.Parameters.AddWithValue("$sourcePath", sourcePath.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ImportItemSummary(
            DbGuid.Parse(reader.GetString(0)),
            DbGuid.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : DbGuid.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : DbGuid.Parse(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : DbTime.Parse(reader.GetInt64(7)),
            DbEnum.ParseItemDisposition(reader.GetString(8)),
            DbEnum.ParseDuplicateDecisionOrNull(reader.IsDBNull(9) ? null : reader.GetString(9)),
            DbEnum.ParseSourceCleanupState(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : DbEnum.ParseMediaType(reader.GetString(12)),
            DbTime.Parse(reader.GetInt64(13)),
            DbTime.Parse(reader.GetInt64(14)),
            reader.IsDBNull(15) ? ImportCleanupPolicy.Copy : DbEnum.ParseImportCleanupPolicy(reader.GetString(15)));
    }
}

/// <summary>
/// One exact-duplicate match found for an import item: the authoritative asset it matches, that
/// asset's state, and the decision the item currently carries (NULL means still open).
/// </summary>
public sealed record ImportDuplicateMatch(
    Guid ImportItemId,
    Guid MatchedMediaId,
    MediaState MatchedMediaState,
    DuplicateDecision? Decision)
{
    /// <summary>True when the user still has to choose INCLUDE, REUSE or SKIP.</summary>
    public bool RequiresDecision =>
        Decision is null && MatchedMediaState == MediaState.Active;
}
