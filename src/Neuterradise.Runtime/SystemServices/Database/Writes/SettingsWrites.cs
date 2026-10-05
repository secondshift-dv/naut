using Microsoft.Data.Sqlite;
using Neuterradise.App.Settings;

namespace Neuterradise.App.SystemServices.Database.Writes;

public sealed class SettingsWrites
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public SettingsWrites(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task SetSettingAsync(
        string key,
        string valueJson,
        CancellationToken cancellationToken = default) =>
        await SetSettingAsync(key, valueJson, null, cancellationToken).ConfigureAwait(false);

    public async Task SetSettingAsync(
        string key,
        string valueJson,
        ActivityEntryPersistence? activityEntry,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(valueJson);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        await SetSettingInTransactionAsync(
            transaction,
            key,
            valueJson,
            activityEntry,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task SetSettingInTransactionAsync(
        CatalogTransaction transaction,
        string key,
        string valueJson,
        ActivityEntryPersistence? activityEntry,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(valueJson);

        var now = DbTime.Format(nowUtc);
        await using var upsertCmd = transaction.CreateCommand(
            """
            INSERT INTO settings(key, value_json, updated_at_ms)
            VALUES ($key, $value, $now)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_at_ms = excluded.updated_at_ms;
            """);
        upsertCmd.Parameters.AddWithValue("$key", key.Trim());
        upsertCmd.Parameters.AddWithValue("$value", valueJson);
        upsertCmd.Parameters.AddWithValue("$now", now);
        await upsertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (activityEntry is not null)
        {
            await ActivityWrites.AppendInternalAsync(transaction, activityEntry, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task CreateCategoryAsync(
        string categoryId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var normalized = TaxonomyNamePolicy.Normalize(name);
        var displayName = TaxonomyNamePolicy.NormalizeDisplayName(name);

        await EnsureNameIsFreeAsync(
            transaction, "categories", "category_id", normalized, excludeId: null, "Category", cancellationToken)
            .ConfigureAwait(false);

        await using var command = transaction.CreateCommand(
            """
            INSERT INTO categories(category_id, name, normalized_name, created_at_ms, updated_at_ms, row_version)
            VALUES ($id, $name, $norm, $now, $now, 0);
            """);
        command.Parameters.AddWithValue("$id", categoryId.Trim());
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$norm", normalized);
        command.Parameters.AddWithValue("$now", now);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [],
            CatalogInvalidationDomain.Category,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> UpdateCategoryAsync(
        string categoryId,
        string name,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var normalized = TaxonomyNamePolicy.Normalize(name);
        var displayName = TaxonomyNamePolicy.NormalizeDisplayName(name);

        await EnsureNameIsFreeAsync(
            transaction, "categories", "category_id", normalized, categoryId.Trim(), "Category", cancellationToken)
            .ConfigureAwait(false);

        var affectedProfileIds = new List<Guid>();
        await using (var readProfiles = transaction.CreateCommand(
            "SELECT profile_id FROM profiles WHERE category_id = $id;"))
        {
            readProfiles.Parameters.AddWithValue("$id", categoryId.Trim());
            await using var reader = await readProfiles.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                affectedProfileIds.Add(DbGuid.Parse(reader.GetString(0)));
            }
        }

        await using var command = transaction.CreateCommand(
            """
            UPDATE categories
            SET name = $name,
                normalized_name = $norm,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE category_id = $id AND row_version = $expectedRowVersion;
            """);
        command.Parameters.AddWithValue("$id", categoryId.Trim());
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$norm", normalized);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            await using var check = transaction.CreateCommand("SELECT row_version FROM categories WHERE category_id = $id;");
            check.Parameters.AddWithValue("$id", categoryId.Trim());
            var currentVer = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (currentVer is null or DBNull)
            {
                throw new CatalogInvariantException($"Category '{categoryId}' does not exist.");
            }
            throw new CatalogConcurrencyConflictException(
                $"Category '{categoryId}' concurrency conflict: expected row_version {expectedRowVersion}, found {currentVer}.");
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [],
            CatalogInvalidationDomain.Category,
            expectedRowVersion + 1));
        if (affectedProfileIds.Count > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                affectedProfileIds,
                CatalogInvalidationDomain.Profile,
                expectedRowVersion + 1));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expectedRowVersion + 1;
    }

    public async Task DeleteCategoryAsync(
        string categoryId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var id = categoryId.Trim();
        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var affectedProfiles = new List<Guid>();
        await using (var readAssignments = transaction.CreateCommand(
            "SELECT profile_id FROM profiles WHERE category_id = $id;"))
        {
            readAssignments.Parameters.AddWithValue("$id", id);
            await using var reader = await readAssignments.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                affectedProfiles.Add(DbGuid.Parse(reader.GetString(0)));
            }
        }

        await using (var clearAssignments = transaction.CreateCommand(
            """
            UPDATE profiles
            SET category_id = NULL,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE category_id = $id;
            """))
        {
            clearAssignments.Parameters.AddWithValue("$id", id);
            clearAssignments.Parameters.AddWithValue("$now", now);
            await clearAssignments.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = transaction.CreateCommand("DELETE FROM categories WHERE category_id = $id AND row_version = $expectedRowVersion;");
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await ThrowMissingOrConflictAsync(transaction, "categories", "category_id", id, expectedRowVersion, "Category", cancellationToken).ConfigureAwait(false);
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [],
            CatalogInvalidationDomain.Category,
            expectedRowVersion));
        if (affectedProfiles.Count > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                affectedProfiles,
                CatalogInvalidationDomain.Profile,
                expectedRowVersion));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateTagAsync(
        string tagId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var normalized = TaxonomyNamePolicy.Normalize(name);
        var displayName = TaxonomyNamePolicy.NormalizeDisplayName(name);

        await EnsureNameIsFreeAsync(
            transaction, "tags", "tag_id", normalized, excludeId: null, "Tag", cancellationToken)
            .ConfigureAwait(false);

        await using var command = transaction.CreateCommand(
            """
            INSERT INTO tags(tag_id, name, normalized_name, created_at_ms, updated_at_ms, row_version)
            VALUES ($id, $name, $norm, $now, $now, 0);
            """);
        command.Parameters.AddWithValue("$id", tagId.Trim());
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$norm", normalized);
        command.Parameters.AddWithValue("$now", now);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [],
            CatalogInvalidationDomain.Tag,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> UpdateTagAsync(
        string tagId,
        string name,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var normalized = TaxonomyNamePolicy.Normalize(name);
        var displayName = TaxonomyNamePolicy.NormalizeDisplayName(name);

        await EnsureNameIsFreeAsync(
            transaction, "tags", "tag_id", normalized, tagId.Trim(), "Tag", cancellationToken)
            .ConfigureAwait(false);

        var affectedProfileIds = new List<Guid>();
        await using (var readProfiles = transaction.CreateCommand(
            "SELECT profile_id FROM profile_tags WHERE tag_id = $id;"))
        {
            readProfiles.Parameters.AddWithValue("$id", tagId.Trim());
            await using var reader = await readProfiles.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                affectedProfileIds.Add(DbGuid.Parse(reader.GetString(0)));
            }
        }

        await using var command = transaction.CreateCommand(
            """
            UPDATE tags
            SET name = $name,
                normalized_name = $norm,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE tag_id = $id AND row_version = $expectedRowVersion;
            """);
        command.Parameters.AddWithValue("$id", tagId.Trim());
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$norm", normalized);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            await using var check = transaction.CreateCommand("SELECT row_version FROM tags WHERE tag_id = $id;");
            check.Parameters.AddWithValue("$id", tagId.Trim());
            var currentVer = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (currentVer is null or DBNull)
            {
                throw new CatalogInvariantException($"Tag '{tagId}' does not exist.");
            }
            throw new CatalogConcurrencyConflictException(
                $"Tag '{tagId}' concurrency conflict: expected row_version {expectedRowVersion}, found {currentVer}.");
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [],
            CatalogInvalidationDomain.Tag,
            expectedRowVersion + 1));
        if (affectedProfileIds.Count > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                affectedProfileIds,
                CatalogInvalidationDomain.Profile,
                expectedRowVersion + 1));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expectedRowVersion + 1;
    }

    public async Task DeleteTagAsync(
        string tagId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagId);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var affectedProfiles = new List<Guid>();
        await using (var readProfiles = transaction.CreateCommand(
            "SELECT profile_id FROM profile_tags WHERE tag_id = $id;"))
        {
            readProfiles.Parameters.AddWithValue("$id", tagId.Trim());
            await using var reader = await readProfiles.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                affectedProfiles.Add(DbGuid.Parse(reader.GetString(0)));
            }
        }

        await using var command = transaction.CreateCommand("DELETE FROM tags WHERE tag_id = $id AND row_version = $expectedRowVersion;");
        command.Parameters.AddWithValue("$id", tagId.Trim());
        command.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await ThrowMissingOrConflictAsync(transaction, "tags", "tag_id", tagId.Trim(), expectedRowVersion, "Tag", cancellationToken).ConfigureAwait(false);
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [],
            CatalogInvalidationDomain.Tag,
            expectedRowVersion));
        if (affectedProfiles.Count > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                affectedProfiles,
                CatalogInvalidationDomain.Profile,
                expectedRowVersion));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> MergeTagAsync(
        string sourceTagId,
        string targetTagId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTagId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTagId);

        var source = sourceTagId.Trim();
        var target = targetTagId.Trim();
        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            throw new CatalogInvariantException("A Tag cannot be merged into itself.");
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        await EnsureTagExistsAsync(transaction, source, cancellationToken).ConfigureAwait(false);
        await EnsureTagExistsAsync(transaction, target, cancellationToken).ConfigureAwait(false);

        var affectedProfiles = new List<Guid>();
        await using (var readProfiles = transaction.CreateCommand(
            "SELECT profile_id FROM profile_tags WHERE tag_id = $source;"))
        {
            readProfiles.Parameters.AddWithValue("$source", source);
            await using var reader = await readProfiles.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                affectedProfiles.Add(DbGuid.Parse(reader.GetString(0)));
            }
        }

        int reassigned;
        await using (var reassign = transaction.CreateCommand(
            """
            INSERT OR IGNORE INTO profile_tags(profile_id, tag_id, created_at_ms)
            SELECT source.profile_id, $target, source.created_at_ms
            FROM profile_tags source
            WHERE source.tag_id = $source;
            """))
        {
            reassign.Parameters.AddWithValue("$source", source);
            reassign.Parameters.AddWithValue("$target", target);
            reassigned = await reassign.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var removeSourceLinks = transaction.CreateCommand(
            "DELETE FROM profile_tags WHERE tag_id = $source;"))
        {
            removeSourceLinks.Parameters.AddWithValue("$source", source);
            await removeSourceLinks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var removeTag = transaction.CreateCommand("DELETE FROM tags WHERE tag_id = $source;"))
        {
            removeTag.Parameters.AddWithValue("$source", source);
            await removeTag.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [],
            CatalogInvalidationDomain.Tag,
            0));
        if (affectedProfiles.Count > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                affectedProfiles,
                CatalogInvalidationDomain.Profile,
                0));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reassigned;
    }

    public async Task<TaxonomyNormalizationResult> ReconcileTaxonomyNormalizationAsync(
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var categories = await ReconcileTableAsync(
            transaction, "categories", "category_id", cancellationToken).ConfigureAwait(false);
        var tags = await ReconcileTableAsync(
            transaction, "tags", "tag_id", cancellationToken).ConfigureAwait(false);

        if (categories.Updated > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty, [], CatalogInvalidationDomain.Category, 0));
        }

        if (tags.Updated > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty, [], CatalogInvalidationDomain.Tag, 0));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new TaxonomyNormalizationResult(
            categories.Updated + tags.Updated,
            [.. categories.Collisions, .. tags.Collisions]);
    }

    private static async Task<(int Updated, IReadOnlyList<TaxonomyNameCollision> Collisions)> ReconcileTableAsync(
        CatalogTransaction transaction,
        string table,
        string idColumn,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, string Name, string StoredKey)>();
        await using (var read = transaction.CreateCommand(
            $"SELECT {idColumn}, name, normalized_name FROM {table} ORDER BY created_at_ms, {idColumn};"))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            claimed[row.StoredKey] = row.Id;
        }

        var updated = 0;
        var collisions = new List<TaxonomyNameCollision>();

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var canonical = TaxonomyNamePolicy.TryNormalize(row.Name);
            var display = TaxonomyNamePolicy.TryNormalizeDisplayName(row.Name);
            if (canonical is null || display is null || string.Equals(canonical, row.StoredKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (claimed.TryGetValue(canonical, out var owner) && !string.Equals(owner, row.Id, StringComparison.Ordinal))
            {
                collisions.Add(new TaxonomyNameCollision(table, row.Id, owner, row.Name));
                continue;
            }

            await using (var update = transaction.CreateCommand(
                $"UPDATE {table} SET name = $name, normalized_name = $norm WHERE {idColumn} = $id;"))
            {
                update.Parameters.AddWithValue("$name", display);
                update.Parameters.AddWithValue("$norm", canonical);
                update.Parameters.AddWithValue("$id", row.Id);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            claimed.Remove(row.StoredKey);
            claimed[canonical] = row.Id;
            updated++;
        }

        return (updated, collisions);
    }

    private static async Task ThrowMissingOrConflictAsync(
        CatalogTransaction transaction,
        string table,
        string idColumn,
        string id,
        long expectedRowVersion,
        string aggregateLabel,
        CancellationToken cancellationToken)
    {
        _ = CatalogTransaction.NextRowVersion(expectedRowVersion);
        await using var check = transaction.CreateCommand(
            $"SELECT row_version FROM {table} WHERE {idColumn} = $id;");
        check.Parameters.AddWithValue("$id", id);
        var current = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (current is null or DBNull)
        {
            throw new CatalogInvariantException($"{aggregateLabel} '{id}' does not exist.");
        }

        throw new CatalogConcurrencyConflictException(
            $"{aggregateLabel} '{id}' changed concurrently: expected row_version {expectedRowVersion}, found {current}.");
    }

    private static async Task EnsureNameIsFreeAsync(
        CatalogTransaction transaction,
        string table,
        string idColumn,
        string normalizedName,
        string? excludeId,
        string entityLabel,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            $"SELECT name FROM {table} WHERE normalized_name = $norm AND ($exclude IS NULL OR {idColumn} <> $exclude);");
        command.Parameters.AddWithValue("$norm", normalizedName);
        command.Parameters.AddWithValue("$exclude", (object?)excludeId ?? DBNull.Value);

        var existing = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (existing is not null)
        {
            throw new TaxonomyNameConflictException(entityLabel, existing);
        }
    }

    private static async Task EnsureTagExistsAsync(
        CatalogTransaction transaction,
        string tagId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand("SELECT COUNT(*) FROM tags WHERE tag_id = $id;");
        command.Parameters.AddWithValue("$id", tagId);
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (count == 0)
        {
            throw new CatalogInvariantException($"Tag '{tagId}' does not exist.");
        }
    }
}

public sealed record TaxonomyNormalizationResult(
    int UpdatedCount,
    IReadOnlyList<TaxonomyNameCollision> Collisions)
{

    public bool HasCollisions => Collisions.Count > 0;
}

public sealed record TaxonomyNameCollision(
    string Table,
    string EntryId,
    string ConflictingEntryId,
    string DisplayName);

public sealed class TaxonomyNameConflictException : InvalidOperationException
{
    public TaxonomyNameConflictException(string entityLabel, string existingDisplayName)
        : base($"{entityLabel} '{existingDisplayName}' already exists.")
    {
        EntityLabel = entityLabel;
        ExistingDisplayName = existingDisplayName;
    }

    public string EntityLabel { get; }

    public string ExistingDisplayName { get; }
}
