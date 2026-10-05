using System.IO;
using System.Text.Json;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Database.Writes;

public sealed class TrashWrites
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public TrashWrites(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task PersistEntryAsync(
        TrashEntryPersistence entry,
        CancellationToken cancellationToken = default)
    {
        Validate(entry);
        entry = entry with
        {
            CreatedAtUtc = DbTime.Parse(DbTime.Format(entry.CreatedAtUtc)),
            UpdatedAtUtc = DbTime.Parse(DbTime.Format(entry.UpdatedAtUtc)),
        };
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var existing = await ReadEntryAsync(transaction, entry.TrashEntryId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing != entry)
            {
                throw new CatalogInvariantException(
                    $"Trash entry {entry.TrashEntryId:D} already preserves different restore facts.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var insert = transaction.CreateCommand(
            """
            INSERT INTO trash_entries(
                trash_entry_id, entity_type, entity_id, state,
                recovery_relative_path, plan_json, created_at_ms, updated_at_ms)
            VALUES (
                $trashEntryId, $entityType, $entityId, $state,
                $recoveryRelativePath, $planJson, $createdAtMs, $updatedAtMs);
            """);
        insert.Parameters.AddWithValue("$trashEntryId", DbGuid.Format(entry.TrashEntryId));
        insert.Parameters.AddWithValue("$entityType", entry.EntityType);
        insert.Parameters.AddWithValue("$entityId", DbGuid.Format(entry.EntityId));
        insert.Parameters.AddWithValue("$state", entry.State);
        insert.Parameters.AddWithValue(
            "$recoveryRelativePath",
            (object?)entry.RecoveryRelativePath ?? DBNull.Value);
        insert.Parameters.AddWithValue("$planJson", entry.PlanJson);
        insert.Parameters.AddWithValue("$createdAtMs", DbTime.Format(entry.CreatedAtUtc));
        insert.Parameters.AddWithValue("$updatedAtMs", DbTime.Format(entry.UpdatedAtUtc));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TrashEntryPersistence?> ReadEntryAsync(
        CatalogTransaction transaction,
        Guid trashEntryId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT entity_type, entity_id, state, recovery_relative_path,
                   plan_json, created_at_ms, updated_at_ms
            FROM trash_entries
            WHERE trash_entry_id = $trashEntryId;
            """);
        command.Parameters.AddWithValue("$trashEntryId", DbGuid.Format(trashEntryId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TrashEntryPersistence(
            trashEntryId,
            reader.GetString(0),
            DbGuid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            DbTime.Parse(reader.GetInt64(5)),
            DbTime.Parse(reader.GetInt64(6)));
    }

    public async Task<long> UpdateEntryStateAsync(
        Guid trashEntryId,
        string state,
        string? recoveryRelativePath,
        DateTimeOffset? completedAtUtc,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(trashEntryId, nameof(trashEntryId));
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using var command = transaction.CreateCommand(
            """
            UPDATE trash_entries
            SET state = $state,
                recovery_relative_path = $recoveryPath,
                completed_at_ms = $completedAt,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE trash_entry_id = $id AND row_version = $expectedRowVersion;
            """);
        command.Parameters.AddWithValue("$id", DbGuid.Format(trashEntryId));
        command.Parameters.AddWithValue("$state", state.Trim());
        command.Parameters.AddWithValue("$recoveryPath", (object?)recoveryRelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", completedAtUtc.HasValue ? DbTime.Format(completedAtUtc.Value) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            await using var check = transaction.CreateCommand("SELECT row_version FROM trash_entries WHERE trash_entry_id = $id;");
            check.Parameters.AddWithValue("$id", DbGuid.Format(trashEntryId));
            var currentVer = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (currentVer is null or DBNull)
            {
                throw new CatalogInvariantException($"TrashEntry {trashEntryId:D} does not exist.");
            }
            throw new CatalogConcurrencyConflictException(
                $"TrashEntry {trashEntryId:D} concurrency conflict: expected row_version {expectedRowVersion}, found {currentVer}.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expectedRowVersion + 1;
    }

    private static void Validate(TrashEntryPersistence entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureNonEmpty(entry.TrashEntryId, nameof(entry.TrashEntryId));
        EnsureNonEmpty(entry.EntityId, nameof(entry.EntityId));
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.EntityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.State);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.PlanJson);
        if (entry.RecoveryRelativePath is { } relativePath
            && (Path.IsPathRooted(relativePath) || relativePath.Contains('\\')))
        {
            throw new ArgumentException(
                "Trash recovery locations must use vault-relative '/' database paths.",
                nameof(entry));
        }

        try
        {
            using var _ = JsonDocument.Parse(entry.PlanJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Trash restore plan JSON must be valid.", nameof(entry), exception);
        }
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }
}

public sealed record TrashEntryPersistence(
    Guid TrashEntryId,
    string EntityType,
    Guid EntityId,
    string State,
    string? RecoveryRelativePath,
    string PlanJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
