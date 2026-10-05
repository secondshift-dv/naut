using System.Globalization;
using Microsoft.Data.Sqlite;
using Neuterradise.App.Trash;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed class TrashReads
{
    private readonly CatalogConnectionFactory _connectionFactory;

    public TrashReads(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
    }

    public TrashReads(CatalogConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<TrashItemSummary?> GetTrashEntryAsync(
        Guid trashEntryId,
        CancellationToken cancellationToken = default)
    {
        if (trashEntryId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.trash_entry_id, t.entity_type, t.entity_id,
                   CASE
                       WHEN t.entity_type = 'PROFILE' THEN (SELECT display_name FROM profiles WHERE profile_id = t.entity_id)
                       WHEN t.entity_type = 'MEDIA' THEN (SELECT coalesce(current_managed_file_name, original_file_name) FROM media WHERE media_id = t.entity_id)
                       ELSE NULL
                   END AS display_name,
                   t.state, t.recovery_relative_path, t.plan_json,
                   t.created_at_ms, t.updated_at_ms, t.completed_at_ms, t.row_version
            FROM trash_entries t
            WHERE t.trash_entry_id = $id;
            """;
        command.Parameters.AddWithValue("$id", DbGuid.Format(trashEntryId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadTrashItem(reader);
    }

    public async Task<TrashItemSummary?> GetTrashEntryForEntityAsync(
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        if (entityId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.trash_entry_id, t.entity_type, t.entity_id,
                   CASE
                       WHEN t.entity_type = 'PROFILE' THEN (SELECT display_name FROM profiles WHERE profile_id = t.entity_id)
                       WHEN t.entity_type = 'MEDIA' THEN (SELECT coalesce(current_managed_file_name, original_file_name) FROM media WHERE media_id = t.entity_id)
                       ELSE NULL
                   END AS display_name,
                   t.state, t.recovery_relative_path, t.plan_json,
                   t.created_at_ms, t.updated_at_ms, t.completed_at_ms, t.row_version
            FROM trash_entries t
            WHERE t.entity_type = $type AND t.entity_id = $id
            ORDER BY t.created_at_ms DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$type", entityType.Trim());
        command.Parameters.AddWithValue("$id", DbGuid.Format(entityId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadTrashItem(reader);
    }

    public async Task<IReadOnlyList<TrashItemSummary>> GetActiveTrashEntriesAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT t.trash_entry_id, t.entity_type, t.entity_id,
                   CASE
                       WHEN t.entity_type = 'PROFILE' THEN (SELECT display_name FROM profiles WHERE profile_id = t.entity_id)
                       WHEN t.entity_type = 'MEDIA' THEN (SELECT coalesce(current_managed_file_name, original_file_name) FROM media WHERE media_id = t.entity_id)
                       ELSE NULL
                   END AS display_name,
                   t.state, t.recovery_relative_path, t.plan_json,
                   t.created_at_ms, t.updated_at_ms, t.completed_at_ms, t.row_version
            FROM trash_entries t
            WHERE t.state IN ('PENDING', 'IN_TRASH', 'EXECUTING', 'RESTORE_EXECUTING', 'RESTORE_FINALIZING')
            ORDER BY t.created_at_ms DESC, t.trash_entry_id DESC
            LIMIT {limit};
            """;

        var list = new List<TrashItemSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadTrashItem(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<TrashItemSummary>> GetExpiredProfileTrashEntriesAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.trash_entry_id, t.entity_type, t.entity_id,
                   CASE
                       WHEN t.entity_type = 'PROFILE' THEN (SELECT display_name FROM profiles WHERE profile_id = t.entity_id)
                       WHEN t.entity_type = 'MEDIA' THEN (SELECT coalesce(current_managed_file_name, original_file_name) FROM media WHERE media_id = t.entity_id)
                       ELSE NULL
                   END AS display_name,
                   t.state, t.recovery_relative_path, t.plan_json,
                   t.created_at_ms, t.updated_at_ms, t.completed_at_ms, t.row_version
            FROM trash_entries t
            WHERE t.state = 'IN_TRASH'
              AND t.entity_type = 'PROFILE'
              AND t.created_at_ms <= $cutoff
            ORDER BY t.created_at_ms, t.trash_entry_id;
            """;
        command.Parameters.AddWithValue("$cutoff", DbTime.Format(cutoffUtc));

        var items = new List<TrashItemSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadTrashItem(reader));
        }

        return items;
    }

    public async Task<ActiveTrashPage> GetActiveTrashPageAsync(
        int pageSize = 200,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 500);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var cursor = DecodeCursor(continuationToken);
        var cursorClause = cursor is null
            ? string.Empty
            : """
              AND (
                    t.created_at_ms < $cursorCreatedAt
                    OR (t.created_at_ms = $cursorCreatedAt AND t.trash_entry_id < $cursorId)
                  )
              """;

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT t.trash_entry_id, t.entity_type, t.entity_id,
                   CASE
                       WHEN t.entity_type = 'PROFILE' THEN (SELECT display_name FROM profiles WHERE profile_id = t.entity_id)
                       WHEN t.entity_type = 'MEDIA' THEN (SELECT coalesce(current_managed_file_name, original_file_name) FROM media WHERE media_id = t.entity_id)
                       ELSE NULL
                   END AS display_name,
                   t.state, t.recovery_relative_path, t.plan_json,
                   t.created_at_ms, t.updated_at_ms, t.completed_at_ms, t.row_version
            FROM trash_entries t
            WHERE t.state IN ('PENDING', 'IN_TRASH', 'EXECUTING', 'RESTORE_EXECUTING', 'RESTORE_FINALIZING')
            {cursorClause}
            ORDER BY t.created_at_ms DESC, t.trash_entry_id DESC
            LIMIT {pageSize + 1};
            """;
        if (cursor is { } decoded)
        {
            command.Parameters.AddWithValue("$cursorCreatedAt", decoded.CreatedAtMs);
            command.Parameters.AddWithValue("$cursorId", decoded.TrashEntryId);
        }

        var items = new List<TrashItemSummary>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadTrashItem(reader));
            }
        }

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        var nextToken = hasMore
            ? $"{DbTime.Format(items[^1].CreatedAtUtc)}:{DbGuid.Format(items[^1].TrashEntryId)}"
            : null;

        return new ActiveTrashPage(items, nextToken, hasMore);
    }

    /// <summary>
    /// Authoritative count of active Trash entries using the same active-state predicate as
    /// <see cref="GetActiveTrashEntriesAsync"/>. Used for confirmation/eligibility against durable
    /// authority rather than the bounded loaded UI page.
    /// </summary>
    public async Task<long> CountActiveTrashEntriesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM trash_entries t
            WHERE t.state IN ('PENDING', 'IN_TRASH', 'EXECUTING', 'RESTORE_EXECUTING', 'RESTORE_FINALIZING');
            """;

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null ? 0 : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<TrashHistoryPage> GetTrashHistoryPageAsync(
        int pageSize = 50,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 500);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        long totalCount;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(*) FROM trash_entries;";
            totalCount = Convert.ToInt64(
                await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        var cursor = DecodeCursor(continuationToken);
        var cursorClause = cursor is null
            ? ""
            : """
              WHERE (t.created_at_ms < $cursorCreatedAt)
                 OR (t.created_at_ms = $cursorCreatedAt AND t.trash_entry_id < $cursorId)
              """;

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT t.trash_entry_id, t.entity_type, t.entity_id,
                   CASE
                       WHEN t.entity_type = 'PROFILE' THEN (SELECT display_name FROM profiles WHERE profile_id = t.entity_id)
                       WHEN t.entity_type = 'MEDIA' THEN (SELECT coalesce(current_managed_file_name, original_file_name) FROM media WHERE media_id = t.entity_id)
                       ELSE NULL
                   END AS display_name,
                   t.state, t.recovery_relative_path, t.plan_json,
                   t.created_at_ms, t.updated_at_ms, t.completed_at_ms, t.row_version
            FROM trash_entries t
            {cursorClause}
            ORDER BY t.created_at_ms DESC, t.trash_entry_id DESC
            LIMIT {pageSize + 1};
            """;
        if (cursor is { } decoded)
        {
            command.Parameters.AddWithValue("$cursorCreatedAt", decoded.CreatedAtMs);
            command.Parameters.AddWithValue("$cursorId", decoded.TrashEntryId);
        }

        var items = new List<TrashItemSummary>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadTrashItem(reader));
            }
        }

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        var nextToken = hasMore
            ? $"{DbTime.Format(items[^1].CreatedAtUtc)}:{DbGuid.Format(items[^1].TrashEntryId)}"
            : null;

        return new TrashHistoryPage(items, nextToken, hasMore, totalCount);
    }

    private static (long CreatedAtMs, string TrashEntryId)? DecodeCursor(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var separatorIndex = token.IndexOf(':');
        if (separatorIndex <= 0
            || !long.TryParse(token[..separatorIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var createdAtMs))
        {
            return null;
        }

        var trashEntryId = token[(separatorIndex + 1)..];
        return string.IsNullOrWhiteSpace(trashEntryId) ? null : (createdAtMs, trashEntryId);
    }

    private static TrashItemSummary ReadTrashItem(SqliteDataReader reader)
    {
        var id = DbGuid.Parse(reader.GetString(0));
        var entityType = reader.GetString(1);
        var entityId = DbGuid.Parse(reader.GetString(2));
        var displayName = reader.IsDBNull(3) ? null : reader.GetString(3);
        var state = reader.GetString(4);
        var recoveryPath = reader.IsDBNull(5) ? null : reader.GetString(5);
        var planJson = reader.GetString(6);
        var createdAt = DbTime.Parse(reader.GetInt64(7));
        var updatedAt = DbTime.Parse(reader.GetInt64(8));
        var completedAt = reader.IsDBNull(9) ? (DateTimeOffset?)null : DbTime.Parse(reader.GetInt64(9));
        var rowVersion = reader.GetInt64(10);

        return new TrashItemSummary(
            id,
            entityType,
            entityId,
            displayName,
            state,
            recoveryPath,
            planJson,
            createdAt,
            updatedAt,
            completedAt,
            rowVersion);
    }
}
