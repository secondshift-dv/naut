using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Neuterradise.App.SystemServices.Database;

/// <summary>
/// Owns the current schema and the single recognized, append-only v1-to-v2 transition.
/// Existing user rows are never reset or rewritten. The contract object makes older binaries
/// reject a newer Vault instead of misinterpreting its destructive lifecycle journals.
/// </summary>
public sealed class CatalogSchemaInitializer
{
    public const int SchemaVersion = 2;
    private const string ContractTable = "catalog_contract";
    private static readonly string[] TransitionObjects = [ContractTable,
        "trg_group_trash_profile_metadata", "trg_group_trash_relation_insert", "trg_group_trash_relation_update",
        "trg_group_trash_appearance_insert", "trg_group_trash_appearance_update"];

    private const string SchemaResourceSuffix = ".SystemServices.Database.CatalogSchema.sql";

    private static readonly Regex SchemaObjectDeclaration = new(
        @"\bCREATE\s+(?:UNIQUE\s+)?(?<kind>TABLE|INDEX|TRIGGER)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;

    public CatalogSchemaInitializer(
        CatalogConnectionFactory connectionFactory,
        CatalogWriteCoordinator writeCoordinator)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
    }

    public Task<CatalogInitializationResult> InitializeAsync(CancellationToken cancellationToken = default) =>
        InitializeCoreAsync(databaseOpened: null, cancellationToken);

    internal Task<CatalogInitializationResult> InitializeAsync(
        Action databaseOpened,
        CancellationToken cancellationToken = default) =>
        InitializeCoreAsync(
            databaseOpened ?? throw new ArgumentNullException(nameof(databaseOpened)),
            cancellationToken);

    private async Task<CatalogInitializationResult> InitializeCoreAsync(
        Action? databaseOpened,
        CancellationToken cancellationToken)
    {
        var schemaSql = LoadSchemaSql();

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await SqlitePragmas.InitializeAndValidateDatabaseAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        databaseOpened?.Invoke();

        var existingObjects = await ReadCatalogObjectsAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var created = existingObjects.Count == 0;

        if (created)
        {
            await CreateSchemaAsync(connection, schemaSql, cancellationToken).ConfigureAwait(false);
        }

        else
        {
            var legacy = !existingObjects.Contains(ContractTable);
            await ValidateSchemaAsync(connection, schemaSql, cancellationToken, legacy).ConfigureAwait(false);
            await using var versionRead = connection.CreateCommand();
            versionRead.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(await versionRead.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (legacy ? version is not (0 or 1) : version != SchemaVersion)
                throw new CatalogSchemaException("The catalog format is newer than or incompatible with this build.");
            await ModelFigureSchemaAuthority.ReconcileAsync(connection, schemaSql, cancellationToken).ConfigureAwait(false);
            await ReconcileContractAsync(connection, schemaSql, legacy, cancellationToken).ConfigureAwait(false);
        }

        await ValidateSchemaAsync(connection, schemaSql, cancellationToken).ConfigureAwait(false);
        await using var contractRead = connection.CreateCommand();
        contractRead.CommandText = "SELECT COUNT(*) FROM catalog_contract WHERE singleton=1 AND schema_version=$version;";
        contractRead.Parameters.AddWithValue("$version", SchemaVersion);
        if (Convert.ToInt32(await contractRead.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1)
            throw new CatalogSchemaException("The catalog format contract is missing or invalid.");
        return new CatalogInitializationResult(created, SchemaVersion);
    }

    private static async Task ReconcileContractAsync(SqliteConnection connection, string schemaSql,
        bool legacy, CancellationToken ct)
    {
        await using var expected = new SqliteConnection("Data Source=:memory:");
        await expected.OpenAsync(ct).ConfigureAwait(false);
        await using (var create = expected.CreateCommand())
        {
            create.CommandText = schemaSql;
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        var expectedDefinitions = await ReadDefinitionsAsync(expected, ct).ConfigureAwait(false);
        var actualDefinitions = await ReadDefinitionsAsync(connection, ct).ConfigureAwait(false);
        foreach (var (name, sql) in actualDefinitions)
            if (!expectedDefinitions.TryGetValue(name, out var canonical)
                || NormalizeDefinition(sql) != NormalizeDefinition(canonical))
                throw new CatalogSchemaException("Unrecognized catalog object definition: " + name);
        if (!legacy) return;

        // SQLite journals the compatibility object, marker and header version in one commit.
        // Interruption before commit leaves v1 intact; re-entry after commit validates v2.
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var upgrade = connection.CreateCommand();
        upgrade.Transaction = transaction;
        upgrade.CommandText = string.Join(";", TransitionObjects.Select(name => expectedDefinitions[name])) + ";"
            + "INSERT INTO catalog_contract(singleton,schema_version) VALUES(1,$version);"
            + "PRAGMA user_version=2;";
        upgrade.Parameters.AddWithValue("$version", SchemaVersion);
        await upgrade.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, string>> ReadDefinitionsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name,sql FROM sqlite_master WHERE type IN ('table','index','trigger') AND name NOT LIKE 'sqlite_%';";
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }

    internal static string NormalizeDefinition(string sql) => Regex.Replace(sql,
        @"'(?:''|[^'])*'|/\*.*?\*/|--[^\r\n]*|\s+",
        match => match.Value.StartsWith("'", StringComparison.Ordinal) ? match.Value : string.Empty,
        RegexOptions.Singleline, TimeSpan.FromSeconds(1)).TrimEnd(';');

    private static string LoadSchemaSql()
    {
        var assembly = typeof(CatalogSchemaInitializer).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(SchemaResourceSuffix, StringComparison.Ordinal))
            ?? throw new CatalogSchemaException(
                "The embedded Catalog Schema v2 resource is missing.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new CatalogSchemaException(
                "The embedded Catalog Schema v2 resource could not be opened.");
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static async Task<HashSet<string>> ReadCatalogObjectsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var objects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type IN ('table','index','trigger')
              AND name NOT LIKE 'sqlite_%';
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            objects.Add(reader.GetString(0));
        }

        return objects;
    }

    private static async Task CreateSchemaAsync(
        SqliteConnection connection,
        string schemaSql,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = schemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }

            throw new CatalogSchemaException(
                "Catalog Schema v2 could not be created atomically.",
                exception);
        }
    }

    private static async Task ValidateSchemaAsync(
        SqliteConnection connection,
        string schemaSql,
        CancellationToken cancellationToken, bool legacy = false)
    {
        var expectedObjects = SchemaObjectDeclaration.Matches(schemaSql)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (legacy) expectedObjects.ExceptWith(TransitionObjects);
        if (expectedObjects.Count == 0)
        {
            throw new CatalogSchemaException(
                "Catalog Schema v2 declares no database objects.");
        }

        var actualObjects = await ReadCatalogObjectsAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var missing = expectedObjects
            .Where(name => !actualObjects.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length != 0)
        {
            throw new CatalogSchemaException(
                "Catalog Schema v2 is incomplete. Missing objects: "
                + string.Join(", ", missing));
        }

        var unexpected = actualObjects
            .Where(name => !expectedObjects.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (unexpected.Length != 0)
        {
            throw new CatalogSchemaException(
                "Catalog Schema v2 contains unsupported objects: "
                + string.Join(", ", unexpected));
        }

        await using var foreignKeyCheck = connection.CreateCommand();
        foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await foreignKeyCheck.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new CatalogSchemaException(
                "Catalog Schema v2 contains a foreign-key integrity violation.");
        }
    }
}

public sealed record CatalogInitializationResult(
    bool Created,
    int SchemaVersion);

public sealed class CatalogSchemaException : InvalidOperationException
{
    public CatalogSchemaException(string message)
        : base(message)
    {
    }

    public CatalogSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
