using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.SystemServices.Database.Reads;

/// <summary>
/// Full-dataset read authority for Profile picker surfaces whose search must not be bounded by a
/// presentation preload. The overlay may filter this materialized set locally; therefore this read
/// intentionally has no arbitrary LIMIT.
/// </summary>
public sealed class ProfilePickerReads
{
    private readonly CatalogDb _catalog;

    public ProfilePickerReads(CatalogDb catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<IReadOnlyList<ProfilePickerItem>> GetAllCandidatesAsync(
        Guid? excludeProfileId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var sql = """
            SELECT p.profile_id, p.display_name, p.unknown_sequence, c.name AS category_name
            FROM profiles p
            LEFT JOIN categories c ON p.category_id = c.category_id
            WHERE p.kind = 'NORMAL' AND p.visibility = 'PUBLISHED' AND p.trashed_at_ms IS NULL
            """;

        if (excludeProfileId is { } excluded && excluded != Guid.Empty)
        {
            sql += " AND p.profile_id != $excludeId";
            command.Parameters.AddWithValue("$excludeId", DbGuid.Format(excluded));
        }

        sql += " ORDER BY coalesce(p.display_name, printf('Unknown %lld', p.unknown_sequence)) COLLATE NOCASE ASC, p.profile_id ASC;";
        command.CommandText = sql;

        var candidates = new List<ProfilePickerItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = DbGuid.Parse(reader.GetString(0));
            var displayName = reader.IsDBNull(1)
                ? (reader.IsDBNull(2) ? "Unknown" : $"Unknown {reader.GetInt64(2)}")
                : reader.GetString(1);
            var categoryName = reader.IsDBNull(3) ? null : reader.GetString(3);
            candidates.Add(new ProfilePickerItem(id, displayName, categoryName));
        }

        return candidates;
    }
}
