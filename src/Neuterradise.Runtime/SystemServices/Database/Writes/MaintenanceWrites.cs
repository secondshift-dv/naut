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

        return string.Equals(existing.FindingCode, proposed.FindingCode, StringComparison.Ordinal);
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
