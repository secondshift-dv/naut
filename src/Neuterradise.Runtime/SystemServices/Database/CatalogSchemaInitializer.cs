using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Neuterradise.App.SystemServices.Database;

/// <summary>
/// Owns the one supported catalog schema for this product build.
///
/// There is intentionally no database upgrade history or upgrade loop. A catalog is either empty
/// (Catalog Schema v1 is created atomically) or it already matches the current schema exactly.
/// Existing incompatible catalogs are rejected. The supported Model Figure trigger predicate
/// transition is reconciled atomically within v1; no tables or user data are rewritten.
/// </summary>
public sealed class CatalogSchemaInitializer
{
    public const int SchemaVersion = 1;

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

        await ValidateSchemaAsync(connection, schemaSql, cancellationToken).ConfigureAwait(false);
        await ModelFigureSchemaAuthority.ReconcileAsync(connection, schemaSql, cancellationToken).ConfigureAwait(false);
        return new CatalogInitializationResult(created, SchemaVersion);
    }

    private static string LoadSchemaSql()
    {
        var assembly = typeof(CatalogSchemaInitializer).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(SchemaResourceSuffix, StringComparison.Ordinal))
            ?? throw new CatalogSchemaException(
                "The embedded Catalog Schema v1 resource is missing.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new CatalogSchemaException(
                "The embedded Catalog Schema v1 resource could not be opened.");
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
                "Catalog Schema v1 could not be created atomically.",
                exception);
        }
    }

    private static async Task ValidateSchemaAsync(
        SqliteConnection connection,
        string schemaSql,
        CancellationToken cancellationToken)
    {
        var expectedObjects = SchemaObjectDeclaration.Matches(schemaSql)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expectedObjects.Count == 0)
        {
            throw new CatalogSchemaException(
                "Catalog Schema v1 declares no database objects.");
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
                "Catalog Schema v1 is incomplete. Missing objects: "
                + string.Join(", ", missing));
        }

        var unexpected = actualObjects
            .Where(name => !expectedObjects.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (unexpected.Length != 0)
        {
            throw new CatalogSchemaException(
                "Catalog Schema v1 contains unsupported objects: "
                + string.Join(", ", unexpected));
        }

        await using var foreignKeyCheck = connection.CreateCommand();
        foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await foreignKeyCheck.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new CatalogSchemaException(
                "Catalog Schema v1 contains a foreign-key integrity violation.");
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
