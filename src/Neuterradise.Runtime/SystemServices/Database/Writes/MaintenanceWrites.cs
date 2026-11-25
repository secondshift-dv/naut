using Neuterradise.App.Maintenance;

namespace Neuterradise.App.SystemServices.Database.Writes;

public sealed class MaintenanceWrites
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public MaintenanceWrites(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public MaintenanceWrites(
        CatalogConnectionFactory connectionFactory,
        CatalogWriteCoordinator writeCoordinator,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RepairPlan> BeginOrReadRepairOperationAsync(
        RepairPlan proposed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        var json = proposed.ToJson();
        var (entityType, entityId) = GetRepairSubject(proposed);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using (var read = transaction.CreateCommand(
            """
            SELECT checkpoint_json
            FROM storage_operations
            WHERE kind = 'LIBRARY_REPAIR'
              AND entity_type = $entityType
              AND entity_id = $entityId
              AND state IN ('PREPARED','EXECUTING','FAILED_RETRYABLE')
            ORDER BY created_at_ms, operation_id
            LIMIT 1;
            """))
        {
            read.Parameters.AddWithValue("$entityType", entityType);
            read.Parameters.AddWithValue("$entityId", entityId);
            var existingJson = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (existingJson is not null)
            {
                var existing = RepairPlan.FromJson(existingJson)
                    ?? throw new CatalogInvariantException("An active Vault Repair plan is unreadable.");
                if (!AreCompatibleActiveRepairs(existing, proposed))
                {
                    throw new CatalogInvariantException(
                        "The repair subject already has a different active Vault Repair operation.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using (var insert = transaction.CreateCommand(
            """
            INSERT INTO storage_operations(
                operation_id, kind, entity_type, entity_id, state, checkpoint_json,
                created_at_ms, updated_at_ms)
            VALUES (
                $operationId, 'LIBRARY_REPAIR', $entityType, $entityId, 'PREPARED',
                $json, $now, $now);
            """))
        {
            insert.Parameters.AddWithValue("$operationId", DbGuid.Format(proposed.OperationId));
            insert.Parameters.AddWithValue("$entityType", entityType);
            insert.Parameters.AddWithValue("$entityId", entityId);
            insert.Parameters.AddWithValue("$json", json);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return proposed;
    }

    internal async Task<IReadOnlyList<RepairPlan>> ReadPendingOriginalRepairsAsync(
        Guid? mediaId, CancellationToken ct)
    {
        var result = new List<RepairPlan>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT checkpoint_json FROM storage_operations o
            WHERE o.kind='LIBRARY_REPAIR' AND o.entity_type='MEDIA'
              AND o.state IN ('PREPARED','EXECUTING','FAILED_RETRYABLE')
              AND ($id IS NULL OR o.entity_id=$id)
              AND EXISTS(SELECT 1 FROM media m WHERE m.media_id=o.entity_id);
            """;
        read.Parameters.AddWithValue("$id", mediaId is Guid id ? (object)DbGuid.Format(id) : DBNull.Value);
        await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var plan = RepairPlan.FromJson(reader.GetString(0));
            if (plan?.Kind is RepairKind.ReconcileOriginalIntegrity or RepairKind.RestoreCanonicalOriginal or RepairKind.ReconcileOriginalPlacement)
                result.Add(plan);
        }
        return result;
    }

    public async Task<PersistedRepairOperation?> ReadRepairOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A repair OperationId cannot be empty.", nameof(operationId));
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT state, checkpoint_json, row_version, error_code, error_detail_safe
            FROM storage_operations
            WHERE operation_id = $operationId AND kind = 'LIBRARY_REPAIR';
            """;
        command.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var plan = RepairPlan.FromJson(reader.GetString(1));
        if (plan is null || plan.OperationId != operationId)
        {
            return null;
        }

        return new PersistedRepairOperation(
            plan,
            reader.GetString(0),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    public async Task SetRepairOperationStateAsync(
        Guid operationId,
        string state,
        string? errorCode = null,
        string? safeErrorDetail = null,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A repair OperationId cannot be empty.", nameof(operationId));
        }

        if (state is not ("EXECUTING" or "COMPLETED" or "FAILED_RETRYABLE" or "STALE"))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown repair checkpoint.");
        }

        var boundedDetail = safeErrorDetail is { Length: > 512 } ? safeErrorDetail[..512] : safeErrorDetail;
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        string currentState;
        long currentRowVersion;
        await using (var read = transaction.CreateCommand(
            "SELECT state, row_version FROM storage_operations WHERE operation_id = $operationId AND kind = 'LIBRARY_REPAIR';"))
        {
            read.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new CatalogInvariantException($"Repair operation {operationId:D} does not exist.");
            }

            currentState = reader.GetString(0);
            currentRowVersion = reader.GetInt64(1);
        }

        if (string.Equals(currentState, state, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsAllowedRepairTransition(currentState, state))
        {
            throw new CatalogConcurrencyConflictException(
                $"Repair operation {operationId:D} cannot transition from {currentState} to {state}.");
        }

        await using var update = transaction.CreateCommand(
            """
            UPDATE storage_operations
            SET state = $state,
                updated_at_ms = $now,
                completed_at_ms = CASE WHEN $terminal = 1 THEN $now ELSE NULL END,
                error_code = $errorCode,
                error_detail_safe = $errorDetail,
                row_version = row_version + 1
            WHERE operation_id = $operationId
              AND kind = 'LIBRARY_REPAIR'
              AND state = $currentState
              AND row_version = $currentRowVersion;
            """);
        update.Parameters.AddWithValue("$state", state);
        update.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        update.Parameters.AddWithValue("$terminal", state is "COMPLETED" or "STALE" ? 1 : 0);
        update.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
        update.Parameters.AddWithValue("$errorDetail", (object?)boundedDetail ?? DBNull.Value);
        update.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
        update.Parameters.AddWithValue("$currentState", currentState);
        update.Parameters.AddWithValue("$currentRowVersion", currentRowVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CatalogConcurrencyConflictException(
                $"Repair operation {operationId:D} changed while its {state} checkpoint was being persisted.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> CompleteOriginalIntegrityAsync(
        RepairPlan plan, HealthMediaItem media, IReadOnlyList<MediaComponentRecord> components, string? status, CancellationToken ct, Func<CatalogTransaction, CancellationToken, Task>? publish = null)
    {
        if (plan.Kind is not (RepairKind.ReconcileOriginalIntegrity or RepairKind.RestoreCanonicalOriginal or RepairKind.ReconcileOriginalPlacement) || plan.MediaId is not Guid mediaId || media.MediaId != mediaId || media.OwnerProfileId is not Guid owner)
            throw new ArgumentException("Original integrity requires a persisted repair plan.", nameof(plan));
        await using var lease = await _writeCoordinator.EnterAsync(ct).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);
        var id = DbGuid.Format(mediaId);
        var op = DbGuid.Format(plan.OperationId);
        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using (var check = transaction.CreateCommand("""
            UPDATE media AS a SET row_version = row_version + 1
            WHERE a.media_id = $id AND a.row_version = $version
              AND a.state = 'ACTIVE' AND a.trashed_at_ms IS NULL AND a.path_state = 'NONE'
              AND a.sha256 = $sha AND a.byte_length = $length AND a.bundle_sha256 IS $bundle
              AND a.current_managed_relative_path || '/' || a.current_managed_file_name = $path
              AND EXISTS (SELECT 1 FROM profile_media pm JOIN profiles p ON p.profile_id = pm.profile_id
                  WHERE pm.media_id = a.media_id AND pm.relation_type = 'OWNER' AND pm.profile_id = $owner
                    AND pm.publication_import_unit_id IS NULL AND p.visibility = 'PUBLISHED'
                    AND p.trashed_at_ms IS NULL AND p.path_state = 'NONE' AND p.current_managed_relative_path IS $ownerFolder)
              AND (SELECT COUNT(*) FROM profile_media pm WHERE pm.media_id = a.media_id AND pm.relation_type = 'OWNER') = 1
              AND NOT EXISTS (SELECT 1 FROM trash_entries t WHERE t.state IN ('PENDING','EXECUTING')
                    AND ((t.entity_type = 'MEDIA' AND t.entity_id = $id)
                      OR (t.entity_type = 'PROFILE' AND t.entity_id = $owner)))
              AND NOT EXISTS (SELECT 1 FROM import_cancel_media_reservations r WHERE r.media_id = $id)
              AND $authority = $owner || '|' || COALESCE(
                    (SELECT status FROM canonical_original_health h WHERE h.media_id = $id), 'UNRECORDED')
              AND EXISTS (SELECT 1 FROM storage_operations o WHERE o.operation_id = $op
                    AND o.kind = 'LIBRARY_REPAIR' AND o.entity_id = $id
                    AND o.state IN ('PREPARED','EXECUTING','FAILED_RETRYABLE') AND o.checkpoint_json = $plan);
            """))
        {
            check.Parameters.AddWithValue("$id", id);
            check.Parameters.AddWithValue("$version", plan.ExpectedRowVersion);
            check.Parameters.AddWithValue("$sha", plan.ExpectedPhysicalSha256!);
            check.Parameters.AddWithValue("$length", plan.ExpectedPhysicalByteLength!.Value);
            check.Parameters.AddWithValue("$path", plan.Kind == RepairKind.ReconcileOriginalPlacement ? plan.SourcePathOrRelative : plan.TargetPathOrRelative);
            check.Parameters.AddWithValue("$owner", DbGuid.Format(owner));
            check.Parameters.AddWithValue("$ownerFolder", (object?)media.OwnerManagedRelativePath ?? DBNull.Value);
            check.Parameters.AddWithValue("$bundle", (object?)media.BundleSha256 ?? DBNull.Value);
            check.Parameters.AddWithValue("$authority", plan.ExpectedAuthorityState);
            check.Parameters.AddWithValue("$op", op);
            check.Parameters.AddWithValue("$plan", plan.ToJson());
            if (await check.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return false;
        }
        if (media.MediaType == Neuterradise.App.Media.MediaType.Model)
        {
            // Component identity, unlike source cleanup metadata, must still match the inspected package.
            await using var read = transaction.CreateCommand("""
                SELECT component_relative_path,component_role,sha256,byte_length
                FROM media_components WHERE media_id=$id;
                """);
            read.Parameters.AddWithValue("$id", id);
            var expected = components.ToDictionary(c => c.ComponentRelativePath, StringComparer.Ordinal);
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!expected.Remove(reader.GetString(0), out var component)
                    || reader.GetString(1) != DbEnum.Format(component.ComponentRole)
                    || reader.GetString(2) != component.Sha256
                    || reader.GetInt64(3) != component.ByteLength) return false;
            }
            if (expected.Count != 0) return false;
        }
        if (publish is not null) await publish(transaction, ct).ConfigureAwait(false);
        if (plan.Kind == RepairKind.ReconcileOriginalPlacement)
        {
            var separator = plan.TargetPathOrRelative.LastIndexOf('/');
            if (separator <= 0) throw new CatalogInvariantException("Placement repair has an invalid target.");
            var directory = plan.TargetPathOrRelative[..separator];
            var name = plan.TargetPathOrRelative[(separator + 1)..];
            await using var target = transaction.CreateCommand("""
                UPDATE media SET target_managed_relative_path=$directory,target_managed_file_name=$name,
                    path_state='PENDING',reconciliation_operation_id=$op
                WHERE media_id=$id AND path_state='NONE'
                    AND NOT EXISTS (SELECT 1 FROM media other WHERE other.media_id<>$id AND (
                        (other.current_managed_relative_path=$directory COLLATE NOCASE
                            AND other.current_managed_file_name=$name COLLATE NOCASE)
                        OR (other.target_managed_relative_path=$directory COLLATE NOCASE
                            AND other.target_managed_file_name=$name COLLATE NOCASE)));
                """);
            target.Parameters.AddWithValue("$directory", directory);
            target.Parameters.AddWithValue("$name", name);
            target.Parameters.AddWithValue("$id", id);
            target.Parameters.AddWithValue("$op", op);
            if (await target.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1
                || await MediaWrites.AdvanceReconciledPlacementAsync(transaction, mediaId, plan.OperationId, ct).ConfigureAwait(false) != 1)
                return false;
            transaction.QueueInvalidation(CatalogInvalidationDomain.Media, mediaId);
        }
        await using (var record = transaction.CreateCommand(status is null
            ? "DELETE FROM canonical_original_health WHERE media_id = $id;"
            : """
              INSERT INTO canonical_original_health(media_id,status,operation_id,checked_at_ms)
              VALUES($id,$status,$op,$now)
              ON CONFLICT(media_id) DO UPDATE SET status=excluded.status,
                  operation_id=excluded.operation_id,checked_at_ms=excluded.checked_at_ms;
              """))
        {
            record.Parameters.AddWithValue("$id", id);
            if (status is not null)
            {
                record.Parameters.AddWithValue("$status", status);
                record.Parameters.AddWithValue("$op", op);
                record.Parameters.AddWithValue("$now", now);
            }
            await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var complete = transaction.CreateCommand("""
            UPDATE storage_operations SET state='COMPLETED',updated_at_ms=$now,completed_at_ms=$now,
                error_code=NULL,error_detail_safe=NULL,row_version=row_version+1
            WHERE operation_id=$op AND kind='LIBRARY_REPAIR';
            """))
        {
            complete.Parameters.AddWithValue("$op", op);
            complete.Parameters.AddWithValue("$now", now);
            if (await complete.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new CatalogConcurrencyConflictException("The original integrity receipt changed.");
        }
        transaction.QueueInvalidation(CatalogInvalidationDomain.Health, mediaId);
        transaction.QueueInvalidation(CatalogInvalidationDomain.Import, mediaId);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static (string EntityType, string EntityId) GetRepairSubject(RepairPlan plan)
    {
        if (plan.ImportItemId is Guid importItemId)
        {
            return ("IMPORT_ITEM", DbGuid.Format(importItemId));
        }

        if (plan.ProfileId is Guid profileId)
        {
            return ("PROFILE", DbGuid.Format(profileId));
        }

        if (plan.Kind is RepairKind.ReconcileOriginalIntegrity or RepairKind.RestoreCanonicalOriginal or RepairKind.ReconcileOriginalPlacement
            && plan.MediaId is Guid originalId)
            return ("MEDIA", DbGuid.Format(originalId));
        if (plan.MediaId is Guid assetId)
        {
            return ("ASSET", DbGuid.Format(assetId));
        }

        throw new ArgumentException("A repair plan requires a stable subject identifier.", nameof(plan));
    }

    private static bool AreCompatibleActiveRepairs(RepairPlan existing, RepairPlan proposed)
    {
        if (existing.Kind != proposed.Kind)
        {
            return false;
        }

        return string.Equals(existing.FindingCode, proposed.FindingCode, StringComparison.Ordinal)
            && (existing.Kind is not (RepairKind.ReconcileOriginalIntegrity or RepairKind.RestoreCanonicalOriginal or RepairKind.ReconcileOriginalPlacement)
                || (existing.ExpectedRowVersion == proposed.ExpectedRowVersion
                    && existing.ExpectedAuthorityState == proposed.ExpectedAuthorityState
                    && existing.SourcePathOrRelative == proposed.SourcePathOrRelative
                    && existing.TargetPathOrRelative == proposed.TargetPathOrRelative));
    }

    private static bool IsAllowedRepairTransition(string currentState, string targetState) =>
        (currentState, targetState) switch
        {
            ("PREPARED", "EXECUTING" or "STALE") => true,
            ("EXECUTING", "COMPLETED" or "FAILED_RETRYABLE" or "STALE") => true,
            ("FAILED_RETRYABLE", "EXECUTING" or "STALE") => true,
            _ => false,
        };

    public async Task<int> PruneOldActivityLogsAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var cutoffMs = DbTime.Format(cutoffUtc);
        await using var command = transaction.CreateCommand(
            "DELETE FROM activity_log WHERE occurred_at_ms < $cutoff;");
        command.Parameters.AddWithValue("$cutoff", cutoffMs);

        var count = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    public async Task<int> PruneCompletedTrashEntriesAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var cutoffMs = DbTime.Format(cutoffUtc);
        await using var command = transaction.CreateCommand(
            "DELETE FROM trash_entries WHERE state = 'COMPLETED' AND completed_at_ms IS NOT NULL AND completed_at_ms < $cutoff;");
        command.Parameters.AddWithValue("$cutoff", cutoffMs);

        var count = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }
}
