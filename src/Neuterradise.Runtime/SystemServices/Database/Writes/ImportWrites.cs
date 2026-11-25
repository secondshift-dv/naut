using System.Text.Json;
using Neuterradise.App.Import;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.SystemServices.Database.Writes;

public sealed class ImportWrites
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public ImportWrites(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }

    public async Task<long> UpdateVerificationDraftAsync(
        Guid unitId,
        int step,
        string draftJson,
        int verificationVersion,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(unitId, nameof(unitId));
        ArgumentException.ThrowIfNullOrWhiteSpace(draftJson);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using var command = transaction.CreateCommand(
            """
            UPDATE import_units
            SET verification_step = $step,
                verification_draft_json = $draftJson,
                verification_version = $version,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_unit_id = $unitId AND row_version = $expectedRowVersion;
            """);
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        command.Parameters.AddWithValue("$step", step);
        command.Parameters.AddWithValue("$draftJson", draftJson.Trim());
        command.Parameters.AddWithValue("$version", verificationVersion);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            await using var check = transaction.CreateCommand("SELECT row_version FROM import_units WHERE import_unit_id = $unitId;");
            check.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            var currentVer = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (currentVer is null or DBNull)
            {
                throw new CatalogInvariantException($"ImportUnit {unitId:D} does not exist.");
            }
            throw new CatalogConcurrencyConflictException(
                $"ImportUnit {unitId:D} concurrency conflict: expected row_version {expectedRowVersion}, found {currentVer}.");
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [unitId],
            CatalogInvalidationDomain.Import,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expectedRowVersion + 1;
    }

    public async Task<long> SetDestinationAsync(
        Guid unitId,
        string destinationKind,
        Guid? destinationProfileId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(unitId, nameof(unitId));
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationKind);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using var command = transaction.CreateCommand(
            """
            UPDATE import_units
            SET destination_kind = $destinationKind,
                destination_profile_id = $destinationProfileId,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_unit_id = $unitId AND row_version = $expectedRowVersion;
            """);
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        command.Parameters.AddWithValue("$destinationKind", destinationKind.Trim());
        command.Parameters.AddWithValue("$destinationProfileId", destinationProfileId.HasValue ? DbGuid.Format(destinationProfileId.Value) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            await using var check = transaction.CreateCommand("SELECT row_version FROM import_units WHERE import_unit_id = $unitId;");
            check.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            var currentVer = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (currentVer is null or DBNull)
            {
                throw new CatalogInvariantException($"ImportUnit {unitId:D} does not exist.");
            }
            throw new CatalogConcurrencyConflictException(
                $"ImportUnit {unitId:D} concurrency conflict: expected row_version {expectedRowVersion}, found {currentVer}.");
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [unitId],
            CatalogInvalidationDomain.Import,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expectedRowVersion + 1;
    }

    public async Task UpdateItemDispositionAsync(
        Guid importItemId,
        ItemDisposition disposition,
        DuplicateDecision? duplicateDecision = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(importItemId, nameof(importItemId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using var command = transaction.CreateCommand(
            """
            UPDATE import_items
            SET disposition = $disposition,
                duplicate_decision = $duplicateDecision,
                updated_at_ms = $now
            WHERE import_item_id = $itemId;
            """);
        command.Parameters.AddWithValue("$itemId", DbGuid.Format(importItemId));
        command.Parameters.AddWithValue("$disposition", DbEnum.Format(disposition));
        command.Parameters.AddWithValue(
            "$duplicateDecision",
            (object?)DbEnum.FormatOrNull(duplicateDecision) ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new CatalogInvariantException($"ImportItem {importItemId:D} does not exist.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateCandidateFingerprintAsync(
        Guid candidateMediaId,
        string sha256,
        long byteLength,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(candidateMediaId, nameof(candidateMediaId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (byteLength < 0) throw new ArgumentOutOfRangeException(nameof(byteLength));

        var normalizedSha = sha256.Trim().ToLowerInvariant();

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using var command = transaction.CreateCommand(
            """
            UPDATE media
            SET sha256 = $sha256,
                byte_length = $byteLength,
                row_version = row_version + 1
            WHERE media_id = $assetId AND state = 'CANDIDATE';
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(candidateMediaId));
        command.Parameters.AddWithValue("$sha256", normalizedSha);
        command.Parameters.AddWithValue("$byteLength", byteLength);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new CatalogInvariantException($"Candidate asset {candidateMediaId:D} does not exist or is not in CANDIDATE state.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateItemSourceDetailsAsync(
        Guid importItemId,
        long byteLength,
        long lastWriteMs,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(importItemId, nameof(importItemId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using var command = transaction.CreateCommand(
            """
            UPDATE import_items
            SET source_byte_length = $byteLength,
                source_last_write_ms = $lastWriteMs,
                updated_at_ms = $now
            WHERE import_item_id = $itemId;
            """);
        command.Parameters.AddWithValue("$itemId", DbGuid.Format(importItemId));
        command.Parameters.AddWithValue("$byteLength", byteLength);
        command.Parameters.AddWithValue("$lastWriteMs", lastWriteMs);
        command.Parameters.AddWithValue("$now", now);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new CatalogInvariantException($"ImportItem {importItemId:D} does not exist.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static readonly string OwnedDuplicateAuthoritySql = $"""
        EXISTS (
            SELECT 1 FROM media a INDEXED BY ix_media_active_byte_length
            JOIN profile_media owner ON owner.media_id = a.media_id AND owner.profile_id = unit.destination_profile_id
            WHERE a.media_id <> c.media_id AND a.media_type = c.media_type AND {ImportReads.ExactMediaIdentitySql}
              AND (c.media_type <> 'MODEL' OR c.bundle_sha256 IS NOT NULL)
              AND owner.relation_type = 'OWNER' AND owner.publication_import_unit_id IS NULL
              AND {ImportReads.PublishedReuseAuthoritySql}
        )
        """;

    internal async Task<IReadOnlyDictionary<Guid, long>> ReadParkedOwnedDuplicatesAsync(
        Guid unitId, Guid operationId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var query = connection.CreateCommand();
        query.CommandText = $"""
            SELECT c.media_id, c.row_version
            FROM import_units unit JOIN import_items item ON item.import_unit_id = unit.import_unit_id
            JOIN media c ON c.media_id = item.candidate_media_id
            WHERE unit.import_unit_id = $unit AND unit.commit_operation_id = $op
              AND unit.destination_kind = 'EXISTING_NORMAL' AND unit.is_paused = 0
              AND item.disposition IN ('INCLUDED','REUSED') AND item.source_cleanup_state = 'SOURCE_PRESENT'
              AND c.state = 'CANDIDATE' AND c.path_state = 'PENDING' AND c.media_type IN ('IMAGE','VIDEO')
              AND c.current_managed_relative_path IS NULL AND c.current_managed_file_name IS NULL
              AND c.reconciliation_operation_id = $op AND c.original_source_path = item.source_path
              AND NOT EXISTS (SELECT 1 FROM jobs job WHERE job.owner_type = 'Media'
                              AND job.owner_id = c.media_id AND job.state = 'RUNNING')
              AND {OwnedDuplicateAuthoritySql};
            """;
        query.Parameters.AddWithValue("$unit", DbGuid.Format(unitId));
        query.Parameters.AddWithValue("$op", DbGuid.Format(operationId));
        var result = new Dictionary<Guid, long>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(DbGuid.Parse(reader.GetString(0)), reader.GetInt64(1));
        return result;
    }

    internal async Task<bool> SkipOwnedDuplicatesAsync(Guid unitId, CancellationToken cancellationToken,
        Guid? recoveryOperation = null, IReadOnlyDictionary<string, long>? verifiedStaged = null)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var unitKey = DbGuid.Format(unitId);
        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using var skip = transaction.CreateCommand(
            $"""
            UPDATE import_items AS item
            SET disposition = 'SKIPPED', duplicate_decision = 'SKIP', reused_media_id = NULL, updated_at_ms = $now
            WHERE item.import_unit_id = $unit
              AND item.disposition IN ('INCLUDED', 'REUSED')
              AND (item.duplicate_decision IS NULL OR item.duplicate_decision = 'REUSE')
              AND item.source_cleanup_state = 'SOURCE_PRESENT'
              AND EXISTS (
                  SELECT 1 FROM import_units unit
                  JOIN profiles profile ON profile.profile_id = unit.destination_profile_id
                  JOIN media c ON c.media_id = item.candidate_media_id
                  WHERE unit.import_unit_id = item.import_unit_id
                    AND unit.commit_operation_id IS $op AND unit.is_paused = 0
                    AND (unit.state = 'PREPARING' OR ($op IS NOT NULL AND unit.state = 'FAILED_RETRYABLE'))
                    AND unit.destination_kind = 'EXISTING_NORMAL'
                    AND profile.kind = 'NORMAL' AND profile.visibility = 'PUBLISHED' AND profile.trashed_at_ms IS NULL
                    AND {OwnedDuplicateAuthoritySql}
                    AND c.state = 'CANDIDATE' AND c.current_managed_relative_path IS NULL
                    AND c.current_managed_file_name IS NULL
                    AND ((c.path_state = 'NONE' AND c.target_managed_relative_path IS NULL)
                         OR ($op IS NOT NULL AND c.path_state = 'PENDING'
                             AND c.reconciliation_operation_id = $op AND c.original_source_path = item.source_path
                             AND EXISTS (SELECT 1 FROM json_each($verified) proof
                                         WHERE proof.key = c.media_id AND proof.value = c.row_version)))
                    AND ($op IS NULL OR EXISTS (
                        SELECT 1 FROM storage_operations operation
                        WHERE operation.operation_id = $op AND operation.kind = 'IMPORT_COMMIT'
                          AND operation.entity_type = 'IMPORT_UNIT' AND operation.entity_id = unit.import_unit_id
                          AND operation.state = unit.library_commit_state
                          AND operation.state IN ('DESTINATION_PREPARED','PLACEMENT_PLANNED')))
                    AND NOT EXISTS (
                        SELECT 1 FROM jobs job WHERE job.owner_type = 'Media' AND job.owner_id = c.media_id
                          AND (job.state = 'RUNNING'
                               OR ($op IS NULL AND (job.kind <> 'HashMedia'
                                   OR job.state <> 'SUCCEEDED'))))
              )
            RETURNING candidate_media_id;
            """);
        skip.Parameters.AddWithValue("$unit", unitKey);
        skip.Parameters.AddWithValue("$now", now);
        var operationKey = recoveryOperation.HasValue ? (object)DbGuid.Format(recoveryOperation.Value) : DBNull.Value;
        skip.Parameters.AddWithValue("$op", operationKey);
        skip.Parameters.AddWithValue("$verified", verifiedStaged is null ? "{}" : JsonSerializer.Serialize(verifiedStaged));
        var retired = new List<string>();
        await using (var reader = await skip.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                retired.Add(reader.GetString(0));
            }
        }

        if (retired.Count == 0) return false;

        var retiredJson = JsonSerializer.Serialize(retired);
        if (recoveryOperation.HasValue)
        {
            // A preserved legacy journal can still have queued preparation for a Candidate
            // that has just proven redundant. Cancel it in this transaction, preserving
            // existing successful/failed job evidence and never retiring a running owner.
            await using var cancel = transaction.CreateCommand(
                """
                UPDATE jobs SET state = 'CANCELLED', completed_at_ms = $now, not_before_ms = NULL,
                    error_code = 'CANCELLED', error_detail_safe = 'Exact owned duplicate skipped during import recovery.',
                    row_version = row_version + 1
                WHERE owner_type = 'Media' AND owner_id IN (SELECT value FROM json_each($ids))
                  AND state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE','WAITING_DEPENDENCY','WAITING_USER_INPUT');
                """);
            cancel.Parameters.AddWithValue("$ids", retiredJson);
            cancel.Parameters.AddWithValue("$now", now);
            await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var retire = transaction.CreateCommand(
            """
            UPDATE media SET state = 'RETIRED', retirement_reason = 'SKIPPED',
                path_state = 'NONE', target_managed_relative_path = NULL, target_managed_file_name = NULL,
                reconciliation_operation_id = NULL, row_version = row_version + 1
            WHERE media_id IN (SELECT value FROM json_each($ids)) AND state = 'CANDIDATE';
            """))
        {
            retire.Parameters.AddWithValue("$ids", retiredJson);
            if (await retire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != retired.Count)
                throw new CatalogConcurrencyConflictException("An owned duplicate changed before candidate retirement.");
        }

        await using var complete = transaction.CreateCommand(
            $"""
            UPDATE import_units AS unit
            SET state = 'COMPLETED', completed_at_ms = $now, updated_at_ms = $now, row_version = row_version + 1,
                library_commit_state = CASE WHEN $op IS NULL THEN library_commit_state ELSE 'TERMINAL' END
            WHERE unit.import_unit_id = $unit
              AND unit.state IN ('PREPARING', 'FAILED_RETRYABLE') AND unit.commit_operation_id IS $op
              AND NOT EXISTS (SELECT 1 FROM profile_media owner WHERE owner.publication_import_unit_id = $unit)
              AND NOT EXISTS (
                  SELECT 1 FROM import_items item
                  LEFT JOIN media c ON c.media_id = item.candidate_media_id
                  WHERE item.import_unit_id = $unit AND (
                      item.disposition <> 'SKIPPED' OR item.duplicate_decision IS NOT 'SKIP'
                      OR c.state IS NOT 'RETIRED' OR c.retirement_reason IS NOT 'SKIPPED'
                      OR NOT {OwnedDuplicateAuthoritySql}
                  )
              );
            """);
        complete.Parameters.AddWithValue("$unit", unitKey);
        complete.Parameters.AddWithValue("$now", now);
        complete.Parameters.AddWithValue("$op", operationKey);
        var completed = await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (completed && recoveryOperation.HasValue)
        {
            await using var operation = transaction.CreateCommand(
                """
                UPDATE storage_operations
                SET state = 'TERMINAL', updated_at_ms = $now, row_version = row_version + 1,
                    checkpoint_json = json_set(checkpoint_json, '$.checkpoint', $terminal, '$.retiredCandidateIds',
                        (SELECT json_group_array(candidate_media_id) FROM import_items WHERE import_unit_id = $unit))
                WHERE operation_id = $op AND kind = 'IMPORT_COMMIT' AND entity_id = $unit
                  AND state IN ('DESTINATION_PREPARED','PLACEMENT_PLANNED');
                """);
            operation.Parameters.AddWithValue("$unit", unitKey);
            operation.Parameters.AddWithValue("$op", operationKey);
            operation.Parameters.AddWithValue("$now", now);
            operation.Parameters.AddWithValue("$terminal", (int)ImportCommitCheckpoint.Terminal);
            if (await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new CatalogConcurrencyConflictException("Recovered duplicate commit journal changed before completion.");
        }

        transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [unitId], CatalogInvalidationDomain.Import, 0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return completed;
    }

    public async Task ApplyDuplicateDecisionAsync(
        Guid importItemId,
        ItemDisposition disposition,
        DuplicateDecision duplicateDecision,
        Guid? reusedMediaId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(importItemId, nameof(importItemId));

        if (duplicateDecision == DuplicateDecision.Reuse && reusedMediaId is null)
        {
            throw new CatalogInvariantException(
                "A REUSE decision must name the active asset the item reuses.");
        }

        if (duplicateDecision != DuplicateDecision.Reuse && reusedMediaId is not null)
        {
            throw new CatalogInvariantException(
                "Only a REUSE decision may record a reused asset.");
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using var command = transaction.CreateCommand(
            """
            UPDATE import_items
            SET disposition = $disposition,
                duplicate_decision = $duplicateDecision,
                reused_media_id = $reusedMediaId,
                updated_at_ms = $now
            WHERE import_item_id = $itemId;
            """);
        command.Parameters.AddWithValue("$itemId", DbGuid.Format(importItemId));
        command.Parameters.AddWithValue("$disposition", DbEnum.Format(disposition));
        command.Parameters.AddWithValue("$duplicateDecision", DbEnum.Format(duplicateDecision));
        command.Parameters.AddWithValue("$reusedMediaId", reusedMediaId.HasValue ? DbGuid.Format(reusedMediaId.Value) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", now);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new CatalogInvariantException($"ImportItem {importItemId:D} does not exist.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reopens post-commit preparation only after an explicit user retry decision. This is the
    /// narrow exception to the automatic terminal-state lattice: a legacy FAILED_TERMINAL unit
    /// carrying a durable Required preparation failure may return to PREPARING without rewriting
    /// failed job history. READY_FOR_VERIFICATION recovery uses the same authority.
    /// </summary>
    public async Task<bool> BeginRequiredPreparationRetryAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(unitId, nameof(unitId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        ImportUnitState state;
        ImportCommitCheckpoint checkpoint;
        await using (var read = transaction.CreateCommand(
            "SELECT state, library_commit_state FROM import_units WHERE import_unit_id = $unitId;"))
        {
            read.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            state = DbEnum.ParseImportUnitState(reader.GetString(0));
            checkpoint = DbEnum.ParseImportCommitCheckpointOrDefault(reader.GetString(1));
        }

        if (!checkpoint.HasReachedDomainCommit())
        {
            return false;
        }

        if (state == ImportUnitState.Preparing)
        {
            return true;
        }

        if (state is not (ImportUnitState.ReadyForVerification or ImportUnitState.FailedTerminal))
        {
            return false;
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using var update = transaction.CreateCommand(
            """
            UPDATE import_units
            SET state = 'PREPARING',
                completed_at_ms = NULL,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_unit_id = $unitId
              AND state = $expectedState;
            """);
        update.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        update.Parameters.AddWithValue("$expectedState", DbEnum.Format(state));
        update.Parameters.AddWithValue("$now", now);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return false;
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [unitId],
            CatalogInvalidationDomain.Import,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task UpdateUnitStateAsync(
        Guid unitId,
        ImportUnitState state,
        CancellationToken cancellationToken = default) =>
        UpdateUnitStateAsync(unitId, state, expectedState: null, expectedRowVersion: null, cancellationToken);

    public async Task UpdateUnitStateAsync(
        Guid unitId,
        ImportUnitState state,
        ImportUnitState? expectedState,
        long? expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(unitId, nameof(unitId));
        if (expectedRowVersion is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRowVersion));
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        ImportUnitState currentState;
        long currentRowVersion;
        await using (var current = transaction.CreateCommand(
            "SELECT state, row_version FROM import_units WHERE import_unit_id = $unitId;"))
        {
            current.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var reader = await current.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new CatalogInvariantException($"ImportUnit {unitId:D} does not exist.");
            }

            currentState = DbEnum.ParseImportUnitState(reader.GetString(0));
            currentRowVersion = reader.GetInt64(1);
        }

        if (expectedState is { } requiredState && currentState != requiredState)
        {
            throw new CatalogConcurrencyConflictException(
                $"ImportUnit {unitId:D} changed lifecycle state: expected {requiredState}, found {currentState}.");
        }

        if (expectedRowVersion is { } requiredVersion && currentRowVersion != requiredVersion)
        {
            throw new CatalogConcurrencyConflictException(
                $"ImportUnit {unitId:D} changed concurrently: expected row_version {requiredVersion}, found {currentRowVersion}.");
        }

        if (!currentState.CanTransitionTo(state))
        {
            throw new CatalogConcurrencyConflictException(
                $"Illegal ImportUnit lifecycle transition {currentState} -> {state} was rejected for {unitId:D}.");
        }

        if (currentState == state)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var isTerminal = state.IsTerminal();
        await using var command = transaction.CreateCommand(
            """
            UPDATE import_units
            SET state = $state,
                completed_at_ms = CASE
                    WHEN $isTerminal = 1 THEN COALESCE(completed_at_ms, $now)
                    ELSE NULL
                END,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_unit_id = $unitId
              AND state = $currentState
              AND row_version = $currentRowVersion;
            """);
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        command.Parameters.AddWithValue("$state", DbEnum.Format(state));
        command.Parameters.AddWithValue("$currentState", DbEnum.Format(currentState));
        command.Parameters.AddWithValue("$currentRowVersion", currentRowVersion);
        command.Parameters.AddWithValue("$isTerminal", isTerminal ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CatalogConcurrencyConflictException(
                $"ImportUnit {unitId:D} changed before lifecycle transition {currentState} -> {state} could commit.");
        }

        if (isTerminal)
        {
            await using (var reprioritize = transaction.CreateCommand(
                """
                UPDATE jobs
                SET priority = COALESCE(
                        (
                            SELECT MAX(interest.desired_priority)
                            FROM import_media_interests interest
                            JOIN import_units consumer ON consumer.import_unit_id = interest.import_unit_id
                            WHERE interest.media_id = jobs.owner_id
                              AND interest.import_unit_id <> $unitId
                              AND consumer.is_paused = 0
                              AND consumer.state NOT IN (
                                  'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                                  'CANCELLED','FAILED_TERMINAL'
                              )
                        ),
                        $backgroundPriority
                    ),
                    row_version = row_version + 1
                WHERE owner_type = 'Media'
                  AND owner_id IN (
                      SELECT media_id
                      FROM import_media_interests
                      WHERE import_unit_id = $unitId
                  )
                  AND state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE');
                """))
            {
                reprioritize.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
                reprioritize.Parameters.AddWithValue("$backgroundPriority", JobPriorityPolicy.DefaultPriority);
                await reprioritize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var release = transaction.CreateCommand(
                "DELETE FROM import_media_interests WHERE import_unit_id = $unitId;"))
            {
                release.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
                await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [unitId],
            CatalogInvalidationDomain.Import,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersistedSourceCleanupObligation?> ReadSourceCleanupObligationAsync(
        Guid importItemId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(importItemId, nameof(importItemId));

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.import_unit_id, i.source_path, i.disposition,
                   i.duplicate_decision, i.source_cleanup_state,
                   u.library_commit_state,
                   i.candidate_media_id, i.reused_media_id,
                   candidate.state, candidate.retirement_reason,
                   candidate.original_source_path, candidate.sha256, candidate.byte_length,
                   managed.media_id, managed.state, managed.sha256, managed.byte_length,
                   managed.current_managed_relative_path, managed.current_managed_file_name,
                   i.cleanup_policy,
                   managed.dependency_status,
                   managed.dependency_discovery_state,
                   i.source_identity_json
            FROM import_items i
            JOIN import_units u ON u.import_unit_id = i.import_unit_id
            LEFT JOIN media candidate ON candidate.media_id = i.candidate_media_id
            LEFT JOIN media managed
              ON managed.media_id = COALESCE(i.reused_media_id, i.candidate_media_id)
            WHERE i.import_item_id = $itemId;
            """;
        command.Parameters.AddWithValue("$itemId", DbGuid.Format(importItemId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PersistedSourceCleanupObligation(
            importItemId,
            DbGuid.Parse(reader.GetString(0)),
            reader.GetString(1),
            DbEnum.ParseItemDisposition(reader.GetString(2)),
            DbEnum.ParseDuplicateDecisionOrNull(reader.IsDBNull(3) ? null : reader.GetString(3)),
            DbEnum.ParseSourceCleanupState(reader.GetString(4)),
            DbEnum.ParseImportCommitCheckpointOrDefault(reader.GetString(5)),
            reader.IsDBNull(6) ? null : DbGuid.Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : DbGuid.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : DbEnum.ParseMediaState(reader.GetString(8)),
            DbEnum.ParseMediaRetirementReasonOrNull(reader.IsDBNull(9) ? null : reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetInt64(12),
            reader.IsDBNull(13) ? null : DbGuid.Parse(reader.GetString(13)),
            reader.IsDBNull(14) ? null : DbEnum.ParseMediaState(reader.GetString(14)),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetInt64(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? ImportCleanupPolicy.Copy : DbEnum.ParseImportCleanupPolicy(reader.GetString(19)),
            reader.IsDBNull(20) ? null : DbEnum.ParseMediaDependencyStatus(reader.GetString(20)),
            reader.IsDBNull(21) ? null : DbEnum.ParseDependencyDiscoveryState(reader.GetString(21)),
            reader.IsDBNull(22) ? null : reader.GetString(22));
    }

    public async Task MarkSourceCleanupConsumedAsync(
        PersistedSourceCleanupObligation obligation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(obligation);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var current = await ReadCleanupStateAsync(
                transaction,
                obligation.ImportItemId,
                obligation.SourcePath,
                cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new CatalogInvariantException(
                $"ImportItem {obligation.ImportItemId:D} cleanup authority changed.");
        }

        if (current == SourceCleanupState.SourceConsumed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (current is not (SourceCleanupState.SourceDeletePending or SourceCleanupState.SourceDeleteFailed))
        {
            throw new CatalogInvariantException(
                $"ImportItem {obligation.ImportItemId:D} is not eligible to finish source cleanup.");
        }

        await using var update = transaction.CreateCommand(
            """
            UPDATE import_items
            SET source_cleanup_state = $consumedState,
                source_cleanup_error = NULL,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_item_id = $itemId
              AND source_path = $sourcePath
              AND source_cleanup_state IN ($pendingState, $failedState);
            """);
        update.Parameters.AddWithValue("$itemId", DbGuid.Format(obligation.ImportItemId));
        update.Parameters.AddWithValue("$sourcePath", obligation.SourcePath);
        update.Parameters.AddWithValue("$consumedState", DbEnum.Format(SourceCleanupState.SourceConsumed));
        update.Parameters.AddWithValue("$pendingState", DbEnum.Format(SourceCleanupState.SourceDeletePending));
        update.Parameters.AddWithValue("$failedState", DbEnum.Format(SourceCleanupState.SourceDeleteFailed));
        update.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CatalogInvariantException(
                $"ImportItem {obligation.ImportItemId:D} cleanup completion lost its expected state.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkSourceCleanupFailedAsync(
        PersistedSourceCleanupObligation obligation,
        string safeErrorDetail,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(obligation);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeErrorDetail);
        var boundedDetail = safeErrorDetail.Length <= 512
            ? safeErrorDetail
            : safeErrorDetail[..512];

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using var update = transaction.CreateCommand(
            """
            UPDATE import_items
            SET source_cleanup_state = $failedState,
                source_cleanup_error = $error,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_item_id = $itemId
              AND source_path = $sourcePath
              AND source_cleanup_state IN ($pendingState, $failedState);
            """);
        update.Parameters.AddWithValue("$itemId", DbGuid.Format(obligation.ImportItemId));
        update.Parameters.AddWithValue("$sourcePath", obligation.SourcePath);
        update.Parameters.AddWithValue("$pendingState", DbEnum.Format(SourceCleanupState.SourceDeletePending));
        update.Parameters.AddWithValue("$failedState", DbEnum.Format(SourceCleanupState.SourceDeleteFailed));
        update.Parameters.AddWithValue("$error", boundedDetail);
        update.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CatalogInvariantException(
                $"ImportItem {obligation.ImportItemId:D} cleanup failure lost its expected state.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkSourceCleanupPreservedAsync(
        PersistedSourceCleanupObligation obligation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(obligation);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var current = await ReadCleanupStateAsync(
                transaction,
                obligation.ImportItemId,
                obligation.SourcePath,
                cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new CatalogInvariantException(
                $"ImportItem {obligation.ImportItemId:D} cleanup authority changed.");
        }

        if (current == SourceCleanupState.SourcePreserved)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var update = transaction.CreateCommand(
            """
            UPDATE import_items
            SET source_cleanup_state = $preservedState,
                source_cleanup_error = NULL,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_item_id = $itemId
              AND source_path = $sourcePath;
            """);
        update.Parameters.AddWithValue("$itemId", DbGuid.Format(obligation.ImportItemId));
        update.Parameters.AddWithValue("$sourcePath", obligation.SourcePath);
        update.Parameters.AddWithValue("$preservedState", DbEnum.Format(SourceCleanupState.SourcePreserved));
        update.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CatalogInvariantException(
                $"ImportItem {obligation.ImportItemId:D} cleanup preservation lost its expected state.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkSourceCleanupChangedAsync(
        PersistedSourceCleanupObligation obligation,
        string? safeErrorDetail,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(obligation);
        var boundedDetail = safeErrorDetail is null
            ? null
            : safeErrorDetail.Length <= 512 ? safeErrorDetail : safeErrorDetail[..512];

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var current = await ReadCleanupStateAsync(
                transaction,
                obligation.ImportItemId,
                obligation.SourcePath,
                cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new CatalogInvariantException(
                $"ImportItem {obligation.ImportItemId:D} cleanup authority changed.");
        }

        if (current == SourceCleanupState.SourceChanged)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var update = transaction.CreateCommand(
            """
            UPDATE import_items
            SET source_cleanup_state = $changedState,
                source_cleanup_error = $error,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_item_id = $itemId
              AND source_path = $sourcePath;
            """);
        update.Parameters.AddWithValue("$itemId", DbGuid.Format(obligation.ImportItemId));
        update.Parameters.AddWithValue("$sourcePath", obligation.SourcePath);
        update.Parameters.AddWithValue("$changedState", DbEnum.Format(SourceCleanupState.SourceChanged));
        update.Parameters.AddWithValue("$error", (object?)boundedDetail ?? DBNull.Value);
        update.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CatalogInvariantException(
                $"ImportItem {obligation.ImportItemId:D} cleanup change marking lost its expected state.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MediaComponentRecord>> ReadMediaComponentsAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT media_id, component_relative_path, normalized_component_path,
                   component_role, sha256, byte_length,
                   original_source_path, source_identity_json,
                   source_cleanup_state, source_cleanup_error, row_version
            FROM media_components
            WHERE media_id = $assetId
            ORDER BY component_role ASC, normalized_component_path ASC;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var list = new List<MediaComponentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new MediaComponentRecord(
                DbGuid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                DbEnum.ParseComponentRole(reader.GetString(3)),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                DbEnum.ParseSourceCleanupState(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt64(10)));
        }

        return list;
    }

    public async Task UpdateComponentCleanupStateAsync(
        Guid assetId,
        string componentRelativePath,
        SourceCleanupState newState,
        string? errorDetail = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));
        ArgumentException.ThrowIfNullOrWhiteSpace(componentRelativePath);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using var update = transaction.CreateCommand(
            """
            UPDATE media_components
            SET source_cleanup_state = $state,
                source_cleanup_error = $error,
                row_version = row_version + 1
            WHERE media_id = $id AND component_relative_path = $relPath;
            """);
        update.Parameters.AddWithValue("$state", DbEnum.Format(newState));
        update.Parameters.AddWithValue("$error", (object?)errorDetail ?? DBNull.Value);
        update.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
        update.Parameters.AddWithValue("$relPath", componentRelativePath);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> BeginOrReadCommitOperationAsync(
        Guid unitId,
        Guid operationId,
        string initialCheckpointJson,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(unitId, nameof(unitId));
        EnsureNonEmpty(operationId, nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(initialCheckpointJson);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        string? existingId;
        await using (var read = transaction.CreateCommand(
            "SELECT commit_operation_id FROM import_units WHERE import_unit_id = $unitId;"))
        {
            read.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            existingId = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        if (!string.IsNullOrEmpty(existingId))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DbGuid.Parse(existingId);
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());

        await using (var setUnit = transaction.CreateCommand(
            """
            UPDATE import_units
            SET commit_operation_id = $op,
                library_commit_state = 'DECISION_VALIDATED',
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_unit_id = $unitId AND commit_operation_id IS NULL;
            """))
        {
            setUnit.Parameters.AddWithValue("$op", DbGuid.Format(operationId));
            setUnit.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            setUnit.Parameters.AddWithValue("$now", now);
            if (await setUnit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new CatalogInvariantException(
                    $"ImportUnit {unitId:D} does not exist or already carries a commit operation.");
            }
        }

        await using (var insertOp = transaction.CreateCommand(
            """
            INSERT INTO storage_operations(
                operation_id, kind, entity_type, entity_id, state, checkpoint_json,
                created_at_ms, updated_at_ms)
            VALUES ($op, 'IMPORT_COMMIT', 'IMPORT_UNIT', $unitId, 'DECISION_VALIDATED', $json,
                $now, $now)
            ON CONFLICT(operation_id) DO NOTHING;
            """))
        {
            insertOp.Parameters.AddWithValue("$op", DbGuid.Format(operationId));
            insertOp.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            insertOp.Parameters.AddWithValue("$json", initialCheckpointJson);
            insertOp.Parameters.AddWithValue("$now", now);
            await insertOp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return operationId;
    }

    public async Task AdvanceCommitCheckpointAsync(
        Guid unitId,
        Guid operationId,
        ImportCommitCheckpoint checkpoint,
        string checkpointJson,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(unitId, nameof(unitId));
        EnsureNonEmpty(operationId, nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointJson);

        var checkpointValue = DbEnum.Format(checkpoint);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        var now = DbTime.Format(_timeProvider.GetUtcNow());

        await using (var updateOp = transaction.CreateCommand(
            """
            UPDATE storage_operations
            SET state = $cp, checkpoint_json = $json, updated_at_ms = $now,
                row_version = row_version + 1
            WHERE operation_id = $op;
            """))
        {
            updateOp.Parameters.AddWithValue("$cp", checkpointValue);
            updateOp.Parameters.AddWithValue("$json", checkpointJson);
            updateOp.Parameters.AddWithValue("$now", now);
            updateOp.Parameters.AddWithValue("$op", DbGuid.Format(operationId));
            if (await updateOp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new CatalogInvariantException(
                    $"Commit operation {operationId:D} does not exist.");
            }
        }

        await using (var updateUnit = transaction.CreateCommand(
            """
            UPDATE import_units
            SET library_commit_state = $cp, updated_at_ms = $now, row_version = row_version + 1
            WHERE import_unit_id = $unitId AND commit_operation_id = $op;
            """))
        {
            updateUnit.Parameters.AddWithValue("$cp", checkpointValue);
            updateUnit.Parameters.AddWithValue("$now", now);
            updateUnit.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            updateUnit.Parameters.AddWithValue("$op", DbGuid.Format(operationId));
            if (await updateUnit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new CatalogInvariantException(
                    $"ImportUnit {unitId:D} commit operation lost its expected identity.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersistedImportCommitOperation?> ReadCommitOperationAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(unitId, nameof(unitId));

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT u.commit_operation_id, u.library_commit_state, u.state, u.destination_kind,
                   u.destination_profile_id, u.row_version, so.checkpoint_json
            FROM import_units u
            LEFT JOIN storage_operations so ON so.operation_id = u.commit_operation_id
            WHERE u.import_unit_id = $unitId;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        return new PersistedImportCommitOperation(
            DbGuid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : DbGuid.Parse(reader.GetString(4)),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? "{}" : reader.GetString(6));
    }

    public async Task SettleFailedCommitOperationAsync(
        Guid unitId,
        string errorCode,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(unitId, nameof(unitId));
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var command = transaction.CreateCommand(
            """
            UPDATE storage_operations
            SET state = 'FAILED',
                completed_at_ms = COALESCE(completed_at_ms, $now),
                error_code = $errorCode,
                error_detail_safe = NULL,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE operation_id = (
                SELECT commit_operation_id FROM import_units WHERE import_unit_id = $unitId)
              AND state NOT IN ('COMPLETED','FAILED','CANCELLED');
            """);
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        command.Parameters.AddWithValue("$errorCode", errorCode.Trim());
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Advances an item along the forward-only pre-cleanup path. Terminal dispositions
    /// (consumed, failed, preserved, changed) are owned by the source cleanup executor, not by this
    /// coordinator step, so they are rejected here.
    /// </summary>
    public async Task AdvanceItemCleanupStateAsync(
        Guid importItemId,
        SourceCleanupState newState,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(importItemId, nameof(importItemId));

        SourceCleanupState[] forwardStates =
        [
            SourceCleanupState.SourcePresent,
            SourceCleanupState.DestinationVerified,
            SourceCleanupState.LibraryCommitted,
            SourceCleanupState.SourceDeletePending,
        ];

        var forward = Array.ConvertAll(forwardStates, DbEnum.Format);
        var target = DbEnum.Format(newState);
        var targetIndex = Array.IndexOf(forward, target);
        if (targetIndex < 0)
        {
            throw new ArgumentException($"'{target}' is not a coordinator-advanceable cleanup state.", nameof(newState));
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        string current;
        await using (var read = transaction.CreateCommand(
            "SELECT source_cleanup_state FROM import_items WHERE import_item_id = $id;"))
        {
            read.Parameters.AddWithValue("$id", DbGuid.Format(importItemId));
            current = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
                ?? throw new CatalogInvariantException($"ImportItem {importItemId:D} does not exist.");
        }

        if (string.Equals(current, target, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var currentIndex = Array.IndexOf(forward, current);
        var retryFromFailedDelete = current == DbEnum.Format(SourceCleanupState.SourceDeleteFailed)
            && newState == SourceCleanupState.SourceDeletePending;
        if ((currentIndex < 0 || targetIndex <= currentIndex) && !retryFromFailedDelete)
        {
            throw new CatalogInvariantException(
                $"ImportItem {importItemId:D} cleanup state '{current}' cannot advance to '{target}'.");
        }

        await using (var update = transaction.CreateCommand(
            """
            UPDATE import_items
            SET source_cleanup_state = $s, updated_at_ms = $now, row_version = row_version + 1
            WHERE import_item_id = $id;
            """))
        {
            update.Parameters.AddWithValue("$s", target);
            update.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
            update.Parameters.AddWithValue("$id", DbGuid.Format(importItemId));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates that a candidate and an active managed asset represent the same authoritative media
    /// identity before exact-reuse is allowed.
    /// </summary>
    public async Task<bool> ValidateReuseAuthorityAsync(
        Guid candidateMediaId, Guid reusedMediaId, CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(candidateMediaId, nameof(candidateMediaId));
        EnsureNonEmpty(reusedMediaId, nameof(reusedMediaId));
        if (candidateMediaId == reusedMediaId) return false;
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT EXISTS (SELECT 1 FROM media c JOIN media a ON a.media_id = $reusedId
                WHERE c.media_id = $candidateId
                  AND (c.state = 'CANDIDATE' OR (c.state = 'RETIRED' AND c.retirement_reason = 'DEDUP_REUSED'))
                  AND {ImportReads.ExactMediaIdentitySql} AND {ImportReads.PublishedReuseAuthoritySql});
            """;
        command.Parameters.AddWithValue("$candidateId", DbGuid.Format(candidateMediaId));
        command.Parameters.AddWithValue("$reusedId", DbGuid.Format(reusedMediaId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    // Compare-and-set automatic decisions before a commit journal exists. Explicit Verify
    // decisions and in-flight workers retain their authority.
    internal async Task<bool> TryChangeAutomaticReuseAsync(
        Guid itemId, Guid? previousReuse, Guid? nextReuse, CancellationToken cancellationToken)
    {
        if (previousReuse == nextReuse) return false;
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var command = transaction.CreateCommand($"""
            UPDATE import_items AS i
            SET disposition = CASE WHEN $next IS NULL THEN 'INCLUDED' ELSE 'REUSED' END,
                duplicate_decision = CASE WHEN $next IS NULL THEN NULL ELSE 'REUSE' END,
                reused_media_id = $next, updated_at_ms = $now
            WHERE i.import_item_id = $item
              AND (($previous IS NULL AND i.disposition = 'INCLUDED' AND i.duplicate_decision IS NULL)
                OR ($previous IS NOT NULL AND i.disposition = 'REUSED'
                    AND i.duplicate_decision = 'REUSE' AND i.reused_media_id = $previous))
              AND EXISTS (
                SELECT 1 FROM import_units u JOIN media c ON c.media_id = i.candidate_media_id
                WHERE u.import_unit_id = i.import_unit_id AND u.commit_operation_id IS NULL
                  AND u.is_paused = 0 AND u.state NOT IN ('CANCELLED','FAILED_TERMINAL','COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION')
                  AND c.state = 'CANDIDATE' AND c.path_state = 'NONE'
                  AND NOT EXISTS (SELECT 1 FROM json_each(u.verification_draft_json, '$.duplicateDecisions') d
                                  WHERE lower(json_extract(d.value, '$.importItemId')) = lower(i.import_item_id))
                  AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.owner_type = 'Media' AND j.owner_id = c.media_id
                                  AND (j.state = 'RUNNING' OR (j.kind IN ('HashMedia','TransferOriginal')
                                      AND j.state NOT IN ('SUCCEEDED','FAILED_TERMINAL','CANCELLED'))))
                  AND ($previous IS NULL OR NOT EXISTS (SELECT 1 FROM media a WHERE a.media_id = $previous
                            AND {ImportReads.ExactMediaIdentitySql} AND {ImportReads.PublishedReuseAuthoritySql}))
                  AND ($next IS NULL OR (EXISTS (SELECT 1 FROM media a WHERE a.media_id = $next
                            AND a.media_id <> c.media_id AND {ImportReads.ExactMediaIdentitySql}
                            AND {ImportReads.PublishedReuseAuthoritySql})
                        AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.owner_type = 'Media' AND j.owner_id = c.media_id
                            AND j.kind = 'HashMedia' AND j.state IN ('FAILED_TERMINAL','CANCELLED'))))
              );
            """);
        command.Parameters.AddWithValue("$item", DbGuid.Format(itemId));
        command.Parameters.AddWithValue("$previous", previousReuse.HasValue ? (object)DbGuid.Format(previousReuse.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$next", nextReuse.HasValue ? (object)DbGuid.Format(nextReuse.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<bool> RetireCandidateForReuseAsync(
        Guid candidateMediaId,
        Guid reusedMediaId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(candidateMediaId, nameof(candidateMediaId));
        EnsureNonEmpty(reusedMediaId, nameof(reusedMediaId));
        if (candidateMediaId == reusedMediaId)
        {
            return false;
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using var read = transaction.CreateCommand(
            """
            SELECT candidate.state, candidate.retirement_reason,
                   candidate.sha256, candidate.byte_length, candidate.bundle_sha256,
                   candidate.dependency_status, candidate.dependency_discovery_state,
                   reused.state, reused.sha256, reused.byte_length, reused.bundle_sha256,
                   reused.dependency_status, reused.dependency_discovery_state,
                   reused.current_managed_relative_path, reused.current_managed_file_name
            FROM media candidate
            JOIN media reused ON reused.media_id = $reusedId AND NOT EXISTS (SELECT 1 FROM canonical_original_health health WHERE health.media_id = reused.media_id)
            WHERE candidate.media_id = $candidateId;
            """);
        read.Parameters.AddWithValue("$candidateId", DbGuid.Format(candidateMediaId));
        read.Parameters.AddWithValue("$reusedId", DbGuid.Format(reusedMediaId));

        string candidateState;
        string? candidateReason;
        string? candidateSha;
        long? candidateLength;
        string? candidateBundle;
        string candidateDependency;
        string candidateDiscovery;
        string reusedState;
        string? reusedSha;
        long? reusedLength;
        string? reusedBundle;
        string reusedDependency;
        string reusedDiscovery;
        string? reusedPath;
        string? reusedName;

        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            candidateState = reader.GetString(0);
            candidateReason = reader.IsDBNull(1) ? null : reader.GetString(1);
            candidateSha = reader.IsDBNull(2) ? null : reader.GetString(2);
            candidateLength = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            candidateBundle = reader.IsDBNull(4) ? null : reader.GetString(4);
            candidateDependency = reader.GetString(5);
            candidateDiscovery = reader.GetString(6);
            reusedState = reader.GetString(7);
            reusedSha = reader.IsDBNull(8) ? null : reader.GetString(8);
            reusedLength = reader.IsDBNull(9) ? null : reader.GetInt64(9);
            reusedBundle = reader.IsDBNull(10) ? null : reader.GetString(10);
            reusedDependency = reader.GetString(11);
            reusedDiscovery = reader.GetString(12);
            reusedPath = reader.IsDBNull(13) ? null : reader.GetString(13);
            reusedName = reader.IsDBNull(14) ? null : reader.GetString(14);
        }

        if (!string.Equals(reusedState, "ACTIVE", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(reusedPath)
            || string.IsNullOrWhiteSpace(reusedName))
        {
            return false;
        }

        var packageAuthority = candidateBundle is not null || reusedBundle is not null;
        var identityMatches = packageAuthority
            ? candidateBundle is not null
              && reusedBundle is not null
              && string.Equals(candidateBundle, reusedBundle, StringComparison.Ordinal)
              && candidateDiscovery == "COMPLETE"
              && reusedDiscovery == "COMPLETE"
              && candidateDependency is "COMPLETE" or "SELF_CONTAINED"
              && reusedDependency is "COMPLETE" or "SELF_CONTAINED"
            : candidateSha is not null
              && reusedSha is not null
              && candidateLength.HasValue
              && reusedLength.HasValue
              && candidateLength.Value == reusedLength.Value
              && string.Equals(candidateSha, reusedSha, StringComparison.Ordinal);

        if (!identityMatches)
        {
            return false;
        }

        if (candidateState == "RETIRED")
        {
            var alreadyReused = string.Equals(candidateReason, "DEDUP_REUSED", StringComparison.Ordinal);
            if (alreadyReused)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return alreadyReused;
        }

        if (candidateState != "CANDIDATE")
        {
            return false;
        }

        await using (var update = transaction.CreateCommand(
            """
            UPDATE media
            SET state = 'RETIRED',
                retirement_reason = 'DEDUP_REUSED',
                row_version = row_version + 1
            WHERE media_id = $candidateId
              AND state = 'CANDIDATE';
            """))
        {
            update.Parameters.AddWithValue("$candidateId", DbGuid.Format(candidateMediaId));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return false;
            }
        }

        await using (var cancelJobs = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'CANCELLED',
                not_before_ms = NULL,
                completed_at_ms = $now,
                error_code = 'OWNER_RETIRED',
                error_detail_safe = 'The transient duplicate Candidate was retired after REUSE revalidation.',
                row_version = row_version + 1
            WHERE owner_type = 'Media'
              AND owner_id = $candidateId
              AND state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE');
            """))
        {
            cancelJobs.Parameters.AddWithValue("$candidateId", DbGuid.Format(candidateMediaId));
            cancelJobs.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
            await cancelJobs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> CommitReuseAssociationAndRetirementAsync(
        Guid candidateMediaId,
        Guid reusedMediaId,
        Guid destinationProfileId,
        Guid importUnitId,
        bool allowExistingOwnerOutsideDestination,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(candidateMediaId, nameof(candidateMediaId));
        EnsureNonEmpty(reusedMediaId, nameof(reusedMediaId));
        EnsureNonEmpty(destinationProfileId, nameof(destinationProfileId));
        EnsureNonEmpty(importUnitId, nameof(importUnitId));
        if (candidateMediaId == reusedMediaId)
        {
            return false;
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using var read = transaction.CreateCommand($"""
            SELECT c.state, c.retirement_reason FROM media c
            JOIN media a ON a.media_id = $reusedId
            WHERE c.media_id = $candidateId
              AND {ImportReads.ExactMediaIdentitySql} AND {ImportReads.PublishedReuseAuthoritySql};
            """);
        read.Parameters.AddWithValue("$candidateId", DbGuid.Format(candidateMediaId));
        read.Parameters.AddWithValue("$reusedId", DbGuid.Format(reusedMediaId));
        string candidateState;
        string? candidateReason;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return false;
            candidateState = reader.GetString(0);
            candidateReason = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        if (candidateState != "CANDIDATE" && !(candidateState == "RETIRED" && candidateReason == "DEDUP_REUSED"))
            return false;

        await using (var destination = transaction.CreateCommand(
            """
            SELECT EXISTS(
                SELECT 1
                FROM profiles
                WHERE profile_id = $profileId
                  AND trashed_at_ms IS NULL
            );
            """))
        {
            destination.Parameters.AddWithValue("$profileId", DbGuid.Format(destinationProfileId));
            var destinationValid = Convert.ToInt32(
                await destination.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
            if (!destinationValid)
            {
                return false;
            }
        }

        var hasDestinationRelation = false;
        await using (var relation = transaction.CreateCommand(
            """
            SELECT EXISTS(
                SELECT 1
                FROM profile_media
                WHERE profile_id = $profileId
                  AND media_id = $assetId
            );
            """))
        {
            relation.Parameters.AddWithValue("$profileId", DbGuid.Format(destinationProfileId));
            relation.Parameters.AddWithValue("$assetId", DbGuid.Format(reusedMediaId));
            hasDestinationRelation = Convert.ToInt32(
                await relation.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
        }

        var hasActiveOwnerOutsideDestination = false;
        if (allowExistingOwnerOutsideDestination)
        {
            await using var owner = transaction.CreateCommand(
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM profile_media pa
                    JOIN profiles p ON p.profile_id = pa.profile_id
                    WHERE pa.media_id = $assetId
                      AND pa.relation_type = 'OWNER'
                      AND pa.profile_id <> $profileId
                      AND p.trashed_at_ms IS NULL
                );
                """);
            owner.Parameters.AddWithValue("$assetId", DbGuid.Format(reusedMediaId));
            owner.Parameters.AddWithValue("$profileId", DbGuid.Format(destinationProfileId));
            hasActiveOwnerOutsideDestination = Convert.ToInt32(
                await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
        }

        if (!hasDestinationRelation && !hasActiveOwnerOutsideDestination)
        {
            var associationNow = _timeProvider.GetUtcNow();
            await using (var insertRelation = transaction.CreateCommand(
                """
                INSERT INTO profile_media(
                    profile_id, media_id, relation_type, provenance_key, created_at_ms)
                VALUES(
                    $profileId, $assetId, 'MANUAL', $provenanceKey, $now);
                """))
            {
                insertRelation.Parameters.AddWithValue("$profileId", DbGuid.Format(destinationProfileId));
                insertRelation.Parameters.AddWithValue("$assetId", DbGuid.Format(reusedMediaId));
                insertRelation.Parameters.AddWithValue("$provenanceKey", $"import:{importUnitId:D}");
                insertRelation.Parameters.AddWithValue("$now", DbTime.Format(associationNow));
                await insertRelation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var publication = transaction.CreateCommand(
                """
                SELECT publication_import_unit_id
                FROM profile_media
                WHERE profile_id = $profileId
                  AND media_id = $assetId
                  AND relation_type = 'MANUAL';
                """))
            {
                publication.Parameters.AddWithValue("$profileId", DbGuid.Format(destinationProfileId));
                publication.Parameters.AddWithValue("$assetId", DbGuid.Format(reusedMediaId));
                var attributed = await publication.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                if (!string.Equals(attributed, DbGuid.Format(importUnitId), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            await ActivityWrites.AppendInternalAsync(
                    transaction,
                    new ActivityEntryPersistence(
                        Guid.NewGuid(),
                        ActivityEventType.MediaAssociationAdded,
                        destinationProfileId,
                        reusedMediaId,
                        importUnitId,
                        OperationId: null,
                        PayloadJson: "{\"relationType\":\"MANUAL\"}",
                        associationNow),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (candidateState == "CANDIDATE")
        {
            await using var retire = transaction.CreateCommand(
                """
                UPDATE media
                SET state = 'RETIRED',
                    retirement_reason = 'DEDUP_REUSED',
                    row_version = row_version + 1
                WHERE media_id = $candidateId
                  AND state = 'CANDIDATE';
                """);
            retire.Parameters.AddWithValue("$candidateId", DbGuid.Format(candidateMediaId));
            if (await retire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return false;
            }
        }

        await using (var cancelJobs = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'CANCELLED',
                not_before_ms = NULL,
                completed_at_ms = $now,
                error_code = 'OWNER_RETIRED',
                error_detail_safe = 'The transient duplicate Candidate was retired after atomic REUSE commit.',
                row_version = row_version + 1
            WHERE owner_type = 'Media'
              AND owner_id = $candidateId
              AND state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE');
            """))
        {
            cancelJobs.Parameters.AddWithValue("$candidateId", DbGuid.Format(candidateMediaId));
            cancelJobs.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
            await cancelJobs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task RetireCandidateAsync(
        Guid candidateMediaId, MediaRetirementReason reason, CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(candidateMediaId, nameof(candidateMediaId));
        return RetireCandidatesAsync([candidateMediaId], reason, cancellationToken);
    }

    internal async Task RetireCandidatesAsync(
        IReadOnlyCollection<Guid> candidateMediaIds, MediaRetirementReason reason, CancellationToken cancellationToken)
    {
        if (candidateMediaIds.Count == 0) return;
        var ids = JsonSerializer.Serialize(candidateMediaIds.Select(DbGuid.Format).Distinct());
        var retirementReason = DbEnum.Format(reason);
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using (var validation = transaction.CreateCommand(
            """
            SELECT requested.value FROM json_each($ids) requested
            LEFT JOIN media candidate ON candidate.media_id = requested.value
            WHERE candidate.state IS NOT 'CANDIDATE'
              AND (candidate.state IS NOT 'RETIRED' OR candidate.retirement_reason IS NOT $reason)
            LIMIT 1;
            """))
        {
            validation.Parameters.AddWithValue("$ids", ids);
            validation.Parameters.AddWithValue("$reason", retirementReason);
            if (await validation.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string invalidId)
                throw new CatalogInvariantException($"Media {invalidId} cannot be retired for '{retirementReason}'.");
        }

        // Only newly retired Candidates lose queued work. Retried retirement preserves history.
        await using (var cancel = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'CANCELLED', not_before_ms = NULL, completed_at_ms = $now,
                error_code = 'OWNER_RETIRED', error_detail_safe = 'The media left the import before this work ran.',
                row_version = row_version + 1
            WHERE owner_type = 'Media' AND state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE')
              AND owner_id IN (
                  SELECT media_id FROM media WHERE state = 'CANDIDATE'
                    AND media_id IN (SELECT value FROM json_each($ids))
              );
            """))
        {
            cancel.Parameters.AddWithValue("$ids", ids);
            cancel.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
            await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var retire = transaction.CreateCommand(
            """
            UPDATE media SET state = 'RETIRED', retirement_reason = $reason, row_version = row_version + 1
            WHERE state = 'CANDIDATE' AND media_id IN (SELECT value FROM json_each($ids));
            """))
        {
            retire.Parameters.AddWithValue("$ids", ids);
            retire.Parameters.AddWithValue("$reason", retirementReason);
            await retire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SourceCleanupState?> ReadCleanupStateAsync(
        CatalogTransaction transaction,
        Guid importItemId,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT source_cleanup_state
            FROM import_items
            WHERE import_item_id = $itemId AND source_path = $sourcePath;
            """);
        command.Parameters.AddWithValue("$itemId", DbGuid.Format(importItemId));
        command.Parameters.AddWithValue("$sourcePath", sourcePath);
        var persisted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return persisted is null ? null : DbEnum.ParseSourceCleanupState(persisted);
    }

}

public sealed record ImportUnitRegistration(
    Guid ImportSessionId,
    Guid ImportUnitId,
    string SourceKind,
    string SourceDisplayName,
    ImportSessionState SessionState,
    ImportUnitState UnitState,
    string? SourcePathOrReference = null,
    Guid? ParentImportUnitId = null);

public sealed record CandidateAllocationRequest(
    Guid ImportItemId,
    Guid ImportUnitId,
    Guid CandidateMediaId,
    string SourcePath,
    string SourceFileName,
    MediaType MediaType,
    long? SourceByteLength = null,
    long? SourceLastWriteMilliseconds = null,
    ImportCleanupPolicy CleanupPolicy = ImportCleanupPolicy.Copy,
    string? SourceIdentityJson = null);

public sealed record PersistedImportCommitOperation(
    Guid OperationId,
    string Checkpoint,
    string UnitState,
    string? DestinationKind,
    Guid? DestinationProfileId,
    long UnitRowVersion,
    string CheckpointJson);

public sealed record PersistedSourceCleanupObligation(
    Guid ImportItemId,
    Guid ImportUnitId,
    string SourcePath,
    ItemDisposition Disposition,
    DuplicateDecision? DuplicateDecision,
    SourceCleanupState SourceCleanupState,
    ImportCommitCheckpoint LibraryCommitState,
    Guid? CandidateMediaId,
    Guid? ReusedMediaId,
    MediaState? CandidateState,
    MediaRetirementReason? CandidateRetirementReason,
    string? CandidateOriginalSourcePath,
    string? CandidateSha256,
    long? CandidateByteLength,
    Guid? ManagedMediaId,
    MediaState? ManagedMediaState,
    string? ExpectedSha256,
    long? ExpectedByteLength,
    string? CurrentManagedRelativePath,
    string? CurrentManagedFileName,
    ImportCleanupPolicy CleanupPolicy = ImportCleanupPolicy.Copy,
    MediaDependencyStatus? DependencyStatus = null,
    DependencyDiscoveryState? DependencyDiscoveryState = null,
    string? SourceIdentityJson = null);
