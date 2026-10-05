using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed record CategoryRecord(
    string CategoryId,
    string Name,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long RowVersion);

public sealed record TagRecord(
    string TagId,
    string Name,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long RowVersion);

public sealed record SettingRecord(
    string Key,
    string ValueJson,
    DateTimeOffset UpdatedAtUtc);

public sealed class SettingsReads
{
    private readonly CatalogConnectionFactory _connectionFactory;

    public SettingsReads(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
    }

    public SettingsReads(CatalogConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<string?> GetSettingValueAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key.Trim());

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : (string)result;
    }

    public async Task<SettingRecord?> GetSettingAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key, value_json, updated_at_ms
            FROM settings
            WHERE key = $key;
            """;
        command.Parameters.AddWithValue("$key", key.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SettingRecord(
            reader.GetString(0),
            reader.GetString(1),
            DbTime.Parse(reader.GetInt64(2)));
    }

    public async Task<IReadOnlyList<CategoryRecord>> GetAllCategoriesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT category_id, name, created_at_ms, updated_at_ms, row_version
            FROM categories
            ORDER BY name ASC;
            """;

        var list = new List<CategoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new CategoryRecord(
                reader.GetString(0),
                reader.GetString(1),
                DbTime.Parse(reader.GetInt64(2)),
                DbTime.Parse(reader.GetInt64(3)),
                reader.GetInt64(4)));
        }

        return list;
    }

    public async Task<CategoryRecord?> GetCategoryAsync(
        string categoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT category_id, name, created_at_ms, updated_at_ms, row_version
            FROM categories
            WHERE category_id = $id;
            """;
        command.Parameters.AddWithValue("$id", categoryId.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new CategoryRecord(
            reader.GetString(0),
            reader.GetString(1),
            DbTime.Parse(reader.GetInt64(2)),
            DbTime.Parse(reader.GetInt64(3)),
            reader.GetInt64(4));
    }

    public async Task<IReadOnlyList<TagRecord>> GetAllTagsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tag_id, name, created_at_ms, updated_at_ms, row_version
            FROM tags
            ORDER BY name ASC;
            """;

        var list = new List<TagRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new TagRecord(
                reader.GetString(0),
                reader.GetString(1),
                DbTime.Parse(reader.GetInt64(2)),
                DbTime.Parse(reader.GetInt64(3)),
                reader.GetInt64(4)));
        }

        return list;
    }

    public async Task<TagRecord?> GetTagAsync(
        string tagId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagId);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tag_id, name, created_at_ms, updated_at_ms, row_version
            FROM tags
            WHERE tag_id = $id;
            """;
        command.Parameters.AddWithValue("$id", tagId.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TagRecord(
            reader.GetString(0),
            reader.GetString(1),
            DbTime.Parse(reader.GetInt64(2)),
            DbTime.Parse(reader.GetInt64(3)),
            reader.GetInt64(4));
    }

    public async Task<int> GetCategoryUsageCountAsync(string categoryId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(categoryId))
        {
            return 0;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM profiles WHERE category_id = $id AND trashed_at_ms IS NULL;";
        command.Parameters.AddWithValue("$id", categoryId.Trim());

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<(Guid ProfileId, string DisplayName)>> GetProfilesUsingCategoryAsync(
        string categoryId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(categoryId))
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT profile_id, coalesce(display_name, 'Unknown Profile')
            FROM profiles
            WHERE category_id = $id AND trashed_at_ms IS NULL
            ORDER BY display_name ASC
            LIMIT {Math.Clamp(limit, 1, 100)};
            """;
        command.Parameters.AddWithValue("$id", categoryId.Trim());

        var list = new List<(Guid, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add((DbGuid.Parse(reader.GetString(0)), reader.GetString(1)));
        }
        return list;
    }

    public async Task<int> GetTagUsageCountAsync(string tagId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tagId))
        {
            return 0;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM profile_tags pt
            INNER JOIN profiles p ON pt.profile_id = p.profile_id
            WHERE pt.tag_id = $id AND p.trashed_at_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$id", tagId.Trim());

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<(Guid ProfileId, string DisplayName)>> GetProfilesUsingTagAsync(
        string tagId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tagId))
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.profile_id, coalesce(p.display_name, 'Unknown Profile')
            FROM profile_tags pt
            INNER JOIN profiles p ON pt.profile_id = p.profile_id
            WHERE pt.tag_id = $id AND p.trashed_at_ms IS NULL
            ORDER BY p.display_name ASC
            LIMIT {Math.Clamp(limit, 1, 100)};
            """;
        command.Parameters.AddWithValue("$id", tagId.Trim());

        var list = new List<(Guid, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add((DbGuid.Parse(reader.GetString(0)), reader.GetString(1)));
        }
        return list;
    }
}
