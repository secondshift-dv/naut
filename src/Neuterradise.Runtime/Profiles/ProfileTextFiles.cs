using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Profiles;

public enum ProfileTextState { Current, ExternalChange, Conflict, Missing, Invalid }

public sealed record ProfileTextInspection(
    Guid ProfileId, string FileName, ProfileTextState State, string Token, string? ExternalText,
    Guid? ImportOfferId = null);

/// <summary>
/// Catalog text remains authoritative. A versioned storage-operation receipt records the last
/// common hash and a pending replacement before filesystem publication. Unknown external bytes
/// are preserved until an explicit, token-checked resolution. No startup directory scan is needed.
/// </summary>
public sealed class ProfileTextFiles(CatalogDb catalog)
{
    private const string Kind = "PROFILE_TEXT_SYNC";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record Receipt(int Version, string FileName, string CommonHash, string? PendingHash);
    private sealed record ImportOffer(int Version, string Text, string SourceHash);
    private const string ImportKind = "PROFILE_OVERVIEW_IMPORT";

    public async Task<IReadOnlyList<ProfileTextInspection>> InspectAsync(Guid profileId,
        CancellationToken cancellationToken = default, bool synchronize = true)
    {
        using var admission = catalog.MutationAdmission.Enter(nameof(InspectAsync));
        await using var lease = await catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ProfileTextInspection>(2);
        foreach (var name in new[] { "overview.txt", "private-note.txt" })
        {
            var item = await InspectCoreAsync(connection, profileId, name, cancellationToken, synchronize).ConfigureAwait(false);
            if (item is not null) result.Add(item);
        }
        (string Text, string Path, long Version)? source;
        try { source = await ReadSourceAsync(connection, profileId, "overview.txt", cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { source = null; }
        if (source is not null)
        {
            await using var offers = connection.CreateCommand();
            offers.CommandText = "SELECT operation_id,checkpoint_json FROM storage_operations WHERE entity_type='Profile' AND kind=$kind AND entity_id=$id AND state='PENDING' ORDER BY created_at_ms LIMIT 20;";
            offers.Parameters.AddWithValue("$kind", ImportKind); offers.Parameters.AddWithValue("$id", profileId.ToString("D"));
            await using var reader = await offers.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ImportOffer? offer;
                try { offer = JsonSerializer.Deserialize<ImportOffer>(reader.GetString(1)); }
                catch (JsonException) { continue; }
                if (offer is not { Version: 1 } || offer.Text is null || offer.Text.Length > ProfileRules.MaximumOverviewLength || Hash(offer.Text) != offer.SourceHash) continue;
                result.Add(new ProfileTextInspection(profileId, "overview.txt",
                    source.Value.Text.Length == 0 ? ProfileTextState.ExternalChange : ProfileTextState.Conflict,
                    Hash($"{source.Value.Version}:{Hash(source.Value.Text)}:{offer.SourceHash}"), offer.Text,
                    Guid.Parse(reader.GetString(0))));
            }
        }
        return result;
    }

    public async Task<bool> ResolveAsync(ProfileTextInspection expected, bool adoptFile,
        CancellationToken cancellationToken = default)
    {
        using var admission = catalog.MutationAdmission.Enter(nameof(ResolveAsync));
        await using var lease = await catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (expected.ImportOfferId is not null)
            return await ResolveImportAsync(connection, expected, adoptFile, cancellationToken).ConfigureAwait(false);
        var current = await InspectCoreAsync(connection, expected.ProfileId, expected.FileName, cancellationToken)
            .ConfigureAwait(false);
        if (current is null || current.Token != expected.Token || current.State == ProfileTextState.Invalid) return false;
        var source = await ReadSourceAsync(connection, expected.ProfileId, expected.FileName, cancellationToken).ConfigureAwait(false);
        if (source is null) return false;
        if (adoptFile)
        {
            if (current.ExternalText is null) return false;
            await using var transaction = CatalogTransaction.Begin(connection, catalog.WriteCoordinator);
            await using var command = transaction.CreateCommand($"""
                UPDATE profiles SET {Column(expected.FileName)}=$text, row_version=row_version+1, updated_at_ms=$now
                WHERE profile_id=$id AND row_version=$version AND trashed_at_ms IS NULL;
                """);
            command.Parameters.AddWithValue("$text", current.ExternalText);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$id", expected.ProfileId.ToString("D"));
            command.Parameters.AddWithValue("$version", source.Value.Version);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
            await SaveReceiptAsync(connection, transaction, expected.ProfileId,
                new Receipt(1, expected.FileName, Hash(current.ExternalText), null), cancellationToken).ConfigureAwait(false);
            transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [expected.ProfileId],
                CatalogInvalidationDomain.Profile, source.Value.Version + 1));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await PublishAsync(connection, expected.ProfileId, expected.FileName, source.Value.Text,
                source.Value.Path, current.ExternalText is null ? null : Hash(current.ExternalText), cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    public async Task QueueImportedOverviewAsync(Guid profileId, Guid importUnitId, string sourceFolder, CancellationToken ct = default)
    {
        var path = Path.Combine(Path.GetFullPath(sourceFolder), "overview.txt");
        RootPathRules.RejectExistingReparsePoints(Path.GetPathRoot(path)!, path);
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > ProfileRules.MaximumOverviewLength * 4L + 3) return;
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var offset = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0;
        string value;
        try { value = Utf8.GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException) { return; }
        if (!ProfileRules.IsOverviewValid(value)) return;
        await using var lease = await catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false);
        await using var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO storage_operations(operation_id,kind,entity_type,entity_id,state,checkpoint_json,created_at_ms,updated_at_ms)
            VALUES($key,$kind,'Profile',$id,'PENDING',$json,$now,$now);
            """;
        command.Parameters.AddWithValue("$key", ImportKey(importUnitId)); command.Parameters.AddWithValue("$kind", ImportKind);
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new ImportOffer(1, value, Hash(value))));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string ImportKey(Guid unitId) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{ImportKind}:{unitId:D}")).AsSpan(0, 16)).ToString("D");

    private async Task<bool> ResolveImportAsync(Microsoft.Data.Sqlite.SqliteConnection connection,
        ProfileTextInspection expected, bool adopt, CancellationToken ct)
    {
        var source = await ReadSourceAsync(connection, expected.ProfileId, "overview.txt", ct).ConfigureAwait(false);
        if (source is null) return false;
        await using var transaction = CatalogTransaction.Begin(connection, catalog.WriteCoordinator);
        await using var read = transaction.CreateCommand("SELECT checkpoint_json FROM storage_operations WHERE operation_id=$key AND kind=$kind AND entity_id=$id AND state='PENDING';");
        read.Parameters.AddWithValue("$key", expected.ImportOfferId!.Value.ToString("D"));
        read.Parameters.AddWithValue("$kind", ImportKind); read.Parameters.AddWithValue("$id", expected.ProfileId.ToString("D"));
        if (await read.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string json) return false;
        ImportOffer? offer;
        try { offer = JsonSerializer.Deserialize<ImportOffer>(json); }
        catch (JsonException) { return false; }
        if (offer is not { Version: 1 } || offer.Text is null || offer.Text.Length > ProfileRules.MaximumOverviewLength
            || Hash(offer.Text) != offer.SourceHash || expected.Token != Hash($"{source.Value.Version}:{Hash(source.Value.Text)}:{offer.SourceHash}")) return false;
        if (adopt)
        {
            await using var update = transaction.CreateCommand("UPDATE profiles SET overview=$text,row_version=row_version+1,updated_at_ms=$now WHERE profile_id=$id AND row_version=$version;");
            update.Parameters.AddWithValue("$text", offer.Text); update.Parameters.AddWithValue("$id", expected.ProfileId.ToString("D"));
            update.Parameters.AddWithValue("$version", source.Value.Version); update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return false;
            transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [expected.ProfileId], CatalogInvalidationDomain.Profile, source.Value.Version + 1));
        }
        await using var settle = transaction.CreateCommand("UPDATE storage_operations SET state='TERMINAL',completed_at_ms=$now,updated_at_ms=$now,row_version=row_version+1 WHERE operation_id=$key;");
        settle.Parameters.AddWithValue("$key", expected.ImportOfferId.Value.ToString("D")); settle.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await settle.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task<ProfileTextInspection?> InspectCoreAsync(Microsoft.Data.Sqlite.SqliteConnection connection,
        Guid id, string name, CancellationToken ct, bool synchronize = true)
    {
        (string Text, string Path, long Version)? source;
        try { source = await ReadSourceAsync(connection, id, name, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new ProfileTextInspection(id, name, ProfileTextState.Invalid, Hash($"{id}:{name}:unsafe"), null); }
        if (source is null) return null;
        var (text, path, version) = source.Value;
        var databaseHash = Hash(text);
        string? external = null;
        ProfileTextState state;
        try
        {
            RootPathRules.RejectExistingReparsePoints(catalog.Paths.Root, path);
            if (File.Exists(path))
            {
                var limit = name == "overview.txt" ? ProfileRules.MaximumOverviewLength : ProfileRules.MaximumPrivateNotesLength;
                if (new FileInfo(path).Length > limit * 4L + 3) throw new InvalidDataException();
                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                var offset = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0;
                external = Utf8.GetString(bytes, offset, bytes.Length - offset);
                if (external.Length > limit) throw new InvalidDataException();
            }
            var receipt = await ReadReceiptAsync(connection, id, name, ct).ConfigureAwait(false);
            var fileHash = external is null ? null : Hash(external);
            if (fileHash == databaseHash)
            {
                if (synchronize && (receipt?.CommonHash != databaseHash || receipt.PendingHash is not null))
                    await SaveReceiptAsync(connection, null, id, new Receipt(1, name, databaseHash, null), ct).ConfigureAwait(false);
                state = ProfileTextState.Current;
            }
            else if (receipt is null && external is null
                || receipt is not null && fileHash == receipt.CommonHash
                || receipt?.PendingHash == databaseHash && external is null)
            {
                if (synchronize)
                {
                    await PublishAsync(connection, id, name, text, path, fileHash, ct).ConfigureAwait(false);
                    state = ProfileTextState.Current;
                }
                else state = external is null ? ProfileTextState.Missing : ProfileTextState.Conflict;
            }
            else if (external is null) state = ProfileTextState.Missing;
            else state = receipt?.CommonHash == databaseHash ? ProfileTextState.ExternalChange : ProfileTextState.Conflict;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            state = ProfileTextState.Invalid;
            external = null;
        }
        return new ProfileTextInspection(id, name, state,
            Hash($"{version}:{databaseHash}:{state}:{(external is null ? "missing" : Hash(external))}"), external);
    }

    private async Task PublishAsync(Microsoft.Data.Sqlite.SqliteConnection connection, Guid id, string name,
        string text, string path, string? previousHash, CancellationToken ct)
    {
        var hash = Hash(text);
        RootPathRules.RejectExistingReparsePoints(catalog.Paths.Root, path);
        var temporary = path + ".tmp-" + Key(id, name);
        RootPathRules.RejectExistingReparsePoints(catalog.Paths.Root, temporary);
        if (File.Exists(temporary))
        {
            var previous = await ReadReceiptAsync(connection, id, name, ct).ConfigureAwait(false);
            if (previous?.PendingHash is null) throw new InvalidDataException("An unowned text staging file was preserved.");
            File.Delete(temporary);
        }
        await SaveReceiptAsync(connection, null, id, new Receipt(1, name, previousHash ?? string.Empty, hash), ct).ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(Utf8.GetBytes(text), ct).ConfigureAwait(false);
                stream.Flush(true);
            }
            RootPathRules.RejectExistingReparsePoints(catalog.Paths.Root, path);
            var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, Utf8, ct).ConfigureAwait(false) : null;
            if ((existing is null ? null : Hash(existing)) != previousHash)
                throw new InvalidDataException("Profile text changed during publication; the external file was preserved.");
            File.Move(temporary, path, true);
            await SaveReceiptAsync(connection, null, id, new Receipt(1, name, hash, null), ct).ConfigureAwait(false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<(string Text, string Path, long Version)?> ReadSourceAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection, Guid id, string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Column(name)}, current_managed_relative_path, row_version FROM profiles
            WHERE profile_id=$id AND kind='NORMAL' AND trashed_at_ms IS NULL
              AND path_state='NONE' AND current_managed_relative_path IS NOT NULL
              AND NOT EXISTS(SELECT 1 FROM trash_entries t WHERE t.entity_type='PROFILE'
                  AND t.entity_id=profiles.profile_id AND t.state IN ('PENDING','EXECUTING'));
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var folder = catalog.Paths.ResolveVaultRelativePath(VaultPathArea.Profiles, reader.GetString(1));
        if (!Directory.Exists(folder)) return null;
        return (reader.IsDBNull(0) ? string.Empty : reader.GetString(0), Path.Combine(folder, name), reader.GetInt64(2));
    }

    private static string Column(string name) => name switch
    {
        "overview.txt" => "overview", "private-note.txt" => "notes", _ => throw new ArgumentException("Unknown Profile text file.")
    };

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text))).ToLowerInvariant();

    private static async Task<Receipt?> ReadReceiptAsync(Microsoft.Data.Sqlite.SqliteConnection connection,
        Guid id, string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT checkpoint_json FROM storage_operations WHERE kind=$kind AND entity_id=$id AND operation_id=$key;";
        command.Parameters.AddWithValue("$kind", Kind);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$key", Key(id, name));
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string json) return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<Receipt>(json);
            return receipt?.Version == 1 && receipt.FileName == name ? receipt : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Key(Guid id, string name) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{Kind}:{id:D}:{name}")).AsSpan(0, 16)).ToString("D");

    private static async Task SaveReceiptAsync(Microsoft.Data.Sqlite.SqliteConnection connection,
        CatalogTransaction? transaction, Guid id, Receipt receipt, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO storage_operations(operation_id,kind,entity_type,entity_id,state,checkpoint_json,created_at_ms,updated_at_ms,completed_at_ms)
            VALUES($key,$kind,'Profile',$id,'TERMINAL',$json,$now,$now,$now)
            ON CONFLICT(operation_id) DO UPDATE SET checkpoint_json=$json,updated_at_ms=$now,row_version=row_version+1;
            """;
        await using var command = transaction is null ? connection.CreateCommand() : transaction.CreateCommand(sql);
        command.CommandText = sql;
        command.Parameters.AddWithValue("$key", Key(id, receipt.FileName));
        command.Parameters.AddWithValue("$kind", Kind);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(receipt));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
