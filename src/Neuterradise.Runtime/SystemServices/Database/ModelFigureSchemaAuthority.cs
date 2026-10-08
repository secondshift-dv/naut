using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Neuterradise.App.SystemServices.Database;

/// <summary>Reconciles only the recognized Figure format predicates within the existing v1 catalog.</summary>
internal static class ModelFigureSchemaAuthority
{
    private static readonly string[] Names =
    [
        "trg_media_asset_authority_insert", "trg_media_asset_authority_update",
        "trg_profile_figure_source_insert", "trg_profile_figure_source_update",
    ];
    private const string FormatRegion = @"/\* model-figure-format-start \*/.*?/\* model-figure-format-end \*/";

    public static async Task ReconcileAsync(SqliteConnection connection, string schema, CancellationToken ct)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        var replacements = new List<(string Name, string Sql)>();
        foreach (var name in Names)
        {
            var expected = Regex.Match(schema, @"CREATE TRIGGER " + name + @"\b.*?\nEND;",
                RegexOptions.Singleline, TimeSpan.FromSeconds(1)).Value;
            if (expected.Length == 0) throw new CatalogSchemaException("Figure trigger authority is missing.");
            await using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = $name;";
            read.Parameters.AddWithValue("$name", name);
            var actual = await read.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if (actual is null || actual.Length > 65536)
                throw new CatalogSchemaException("Figure trigger authority is missing or invalid.");
            if (Normalize(actual) == Normalize(expected)) continue;
            var legacyPredicate = name.StartsWith("trg_media_asset", StringComparison.Ordinal)
                ? "AND a.dependency_status = 'SELF_CONTAINED' AND lower(coalesce(a.current_managed_file_name, '')) LIKE '%.glb'"
                : "AND m.dependency_status = 'SELF_CONTAINED' AND lower(coalesce(m.current_managed_file_name,m.original_file_name,'')) LIKE '%.glb'";
            var recognized = Regex.Replace(expected, FormatRegion, legacyPredicate,
                RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            if (Normalize(actual) != Normalize(recognized))
                throw new CatalogSchemaException("Unrecognized Figure trigger definition: " + name);
            replacements.Add((name, expected));
        }
        foreach (var (name, sql) in replacements)
        {
            await using var replace = connection.CreateCommand();
            replace.Transaction = transaction;
            replace.CommandText = "DROP TRIGGER " + name + ";" + sql;
            await replace.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static string Normalize(string sql)
    {
        var withoutComments = Regex.Replace(sql, @"/\*.*?\*/", "", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        return Regex.Replace(withoutComments, @"'(?:''|[^'])*'|\s+",
            match => match.Value.StartsWith("'") ? match.Value : "",
            RegexOptions.None, TimeSpan.FromSeconds(1)).TrimEnd(';');
    }
}
