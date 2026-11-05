using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.SystemServices.Storage;

/// <summary>
/// Vault-local validation receipts bind catalog hashes to one unchanged NTFS file object and
/// journal generation. Uncertainty always returns to full SHA-256 and structural validation.
/// Receipts carry no geometry or textures and cannot replace the canonical MediaAsset.
/// </summary>
public sealed class ModelRenderValidationReceipts(CatalogDb catalog)
{
    private const string Kind = "MODEL_RENDER_VALIDATED";
    private static readonly ConditionalWeakTable<CatalogDb, SemaphoreSlim[]> Gates = new();
    private long _fullBytes;
    private int _fullValidations;
    public long FullValidationBytes => Interlocked.Read(ref _fullBytes);
    public int FullValidationCount => Volatile.Read(ref _fullValidations);
    private sealed record Receipt(int Version, Guid VaultId, Guid MediaId, Guid AssetId,
        string SourceHash, string AssetHash, NtfsChangeStamp Stamp);

    public async Task<ModelRenderFile> ValidateAsync(string path, string sourceHash,
        CancellationToken ct = default, bool deep = false, bool backgroundIo = false)
    {
        var gates = Gates.GetValue(catalog, _ => Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1, 1)).ToArray());
        var gate = gates[StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetFullPath(path)) & 15];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ValidateCoreAsync(path, sourceHash, ct, deep, backgroundIo).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<ModelRenderFile> ValidateCoreAsync(string path, string sourceHash, CancellationToken ct, bool deep, bool backgroundIo)
    {
        var relative = Path.GetRelativePath(catalog.Paths.Root, Path.GetFullPath(path)).Replace('\\', '/');
        path = catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, relative);
        Guid mediaId, assetId;
        long length;
        string assetHash;
        await using (var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT ma.media_id,ma.media_asset_id,ma.byte_length,ma.sha256 FROM media_assets ma
                JOIN media m ON m.media_id=ma.media_id
                WHERE ma.relative_path=$path AND ma.role='MODEL_RENDER' AND ma.contract_version=1
                  AND ma.state='READY' AND m.state='ACTIVE' AND m.trashed_at_ms IS NULL AND m.sha256=$source;
                """;
            command.Parameters.AddWithValue("$path", relative);
            command.Parameters.AddWithValue("$source", sourceHash);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidDataException("NFIG has no matching active catalog authority.");
            mediaId = Guid.Parse(reader.GetString(0)); assetId = Guid.Parse(reader.GetString(1));
            length = reader.GetInt64(2); assetHash = reader.GetString(3);
        }
        var vault = await new VaultIdentityStore().TryReadAsync(catalog.Paths, ct).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous);
        if (file.Length != length) throw new InvalidDataException("NFIG length differs from its catalog fingerprint.");
        var stamp = NtfsChangeStamp.TryRead(file, path);
        if (!deep && stamp is not null && vault is not null)
        {
            var receipt = await ReadAsync(mediaId, ct).ConfigureAwait(false);
            if (receipt is { Version: 1 } && receipt.VaultId == vault.VaultId
                && receipt.MediaId == mediaId && receipt.AssetId == assetId
                && receipt.SourceHash == sourceHash && receipt.AssetHash == assetHash
                && receipt.Stamp is not null && receipt.Stamp.ProvesUnchanged(stamp))
                return await ModelRenderContainer.ReadTrustedDescriptorAsync(path, sourceHash, assetHash, length, ct).ConfigureAwait(false);
        }
        Interlocked.Increment(ref _fullValidations);
        var valid = await ModelRenderContainer.ValidateAsync(path, sourceHash, ct, backgroundIo).ConfigureAwait(false);
        Interlocked.Add(ref _fullBytes, valid.ByteLength);
        if (valid.Sha256 != assetHash || valid.ByteLength != length)
            throw new InvalidDataException("NFIG SHA-256 differs from its catalog fingerprint.");
        var after = NtfsChangeStamp.TryRead(file, path);
        if (vault is not null && stamp is not null && after is not null && stamp.ProvesUnchanged(after))
        {
            try { await SaveAsync(new Receipt(1, vault.VaultId, mediaId, assetId, sourceHash, assetHash, after), ct).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                || exception is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 8 or 13 })
            { System.Diagnostics.Trace.TraceWarning("NFIG receipt unavailable; full validation remains required."); }
        }
        return valid;
    }

    private async Task<Receipt?> ReadAsync(Guid mediaId, CancellationToken ct)
    {
        await using var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT checkpoint_json FROM storage_operations WHERE operation_id=$id AND kind=$kind AND state='TERMINAL';";
        command.Parameters.AddWithValue("$id", Key(mediaId)); command.Parameters.AddWithValue("$kind", Kind);
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string json || json.Length > 4096) return null;
        try { return JsonSerializer.Deserialize<Receipt>(json); }
        catch (JsonException) { return null; }
    }

    private async Task SaveAsync(Receipt receipt, CancellationToken ct)
    {
        await using var lease = await catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false);
        await using var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO storage_operations(operation_id,kind,entity_type,entity_id,state,checkpoint_json,created_at_ms,updated_at_ms,completed_at_ms)
            VALUES($id,$kind,'MEDIA',$media,'TERMINAL',$json,$now,$now,$now)
            ON CONFLICT(operation_id) DO UPDATE SET checkpoint_json=$json,updated_at_ms=$now,row_version=row_version+1;
            """;
        command.Parameters.AddWithValue("$id", Key(receipt.MediaId)); command.Parameters.AddWithValue("$kind", Kind);
        command.Parameters.AddWithValue("$media", receipt.MediaId.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(receipt));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Key(Guid mediaId) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{Kind}:{mediaId:D}")).AsSpan(0, 16)).ToString("D");
}
