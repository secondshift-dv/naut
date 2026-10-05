using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neuterradise.App.SystemServices.Storage;

public sealed record VaultIdentity(
    int SchemaVersion,
    Guid VaultId,
    long CreatedAtUnixMs);

public sealed class VaultIdentityStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    private readonly TimeProvider _timeProvider;

    public VaultIdentityStore(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<VaultIdentity> GetOrCreateAsync(
        VaultPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var existing = await TryReadAsync(paths, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        Directory.CreateDirectory(paths.SystemPath);
        var identity = new VaultIdentity(
            CurrentSchemaVersion,
            Guid.NewGuid(),
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await WriteNewAsync(paths, identity, cancellationToken).ConfigureAwait(false);
        return await TryReadAsync(paths, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("Vault identity publish completed without a readable identity file.");
    }

    public async Task<VaultIdentity?> TryReadAsync(
        VaultPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(paths.IdentityFilePath))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(paths.IdentityFilePath, cancellationToken)
            .ConfigureAwait(false);
        var identity = JsonSerializer.Deserialize<VaultIdentity>(bytes, JsonOptions)
            ?? throw new JsonException("Vault identity file is empty.");
        Validate(identity);
        return identity;
    }

    private static void Validate(VaultIdentity identity)
    {
        if (identity.SchemaVersion != CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Vault identity schema {identity.SchemaVersion} is not supported.");
        }

        if (identity.VaultId == Guid.Empty)
        {
            throw new JsonException("Vault identity contains an empty VaultId.");
        }

        if (identity.CreatedAtUnixMs <= 0)
        {
            throw new JsonException("Vault identity contains an invalid creation timestamp.");
        }
    }

    private static async Task WriteNewAsync(
        VaultPaths paths,
        VaultIdentity identity,
        CancellationToken cancellationToken)
    {
        Validate(identity);
        if (File.Exists(paths.IdentityFilePath))
        {
            return;
        }

        var tempPath = paths.IdentityFilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions);
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(tempPath, paths.IdentityFilePath, overwrite: false);
            }
            catch (IOException) when (File.Exists(paths.IdentityFilePath))
            {
                // Another holder cannot normally exist because creation happens under VaultLock,
                // but if a previous process completed the atomic publish first, read the winner.
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
