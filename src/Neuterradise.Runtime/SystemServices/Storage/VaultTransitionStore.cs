using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neuterradise.App.SystemServices.Storage;

internal sealed class VaultTransitionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    private readonly AppStatePaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VaultTransitionStore(AppStatePaths paths) =>
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public async Task<VaultTransitionRecord?> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_paths.VaultTransitionStateFilePath))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(
                _paths.VaultTransitionStateFilePath,
                cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<VaultTransitionRecord>(bytes, SerializerOptions)
                ?? throw new JsonException("Vault transition journal is empty.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(
        VaultTransitionRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureStructuralDirectories();
            var path = _paths.VaultTransitionStateFilePath;
            var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(record, SerializerOptions);
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

                _ = JsonSerializer.Deserialize<VaultTransitionRecord>(
                    await File.ReadAllBytesAsync(tempPath, cancellationToken).ConfigureAwait(false),
                    SerializerOptions)
                    ?? throw new JsonException("Vault transition journal verification failed.");

                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                TryDelete(tempPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_paths.VaultTransitionStateFilePath))
            {
                File.Delete(_paths.VaultTransitionStateFilePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
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
