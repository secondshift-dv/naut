using System.IO;
using System.Text.Json;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Recovery;

public sealed class VaultLock : IDisposable, IAsyncDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private VaultLock(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    public bool IsHeld => !_disposed;

    public static async ValueTask<VaultLock> AcquireAsync(
        VaultPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();

        FileStream stream;

        try
        {
            stream = new FileStream(
                paths.LockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous);
        }
        catch (IOException ex)
        {
            throw new VaultLockUnavailableException(paths.LockPath, ex);
        }

        try
        {
            var diagnostics = JsonSerializer.SerializeToUtf8Bytes(new
            {
                processId = Environment.ProcessId,
                startedAtUtc = DateTimeOffset.UtcNow,
                buildVersion = ProductIdentity.Version,
            });

            stream.SetLength(0);
            await stream.WriteAsync(diagnostics, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Position = 0;

            return new VaultLock(paths.LockPath, stream);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class VaultLockUnavailableException : IOException
{
    public VaultLockUnavailableException(string lockPath, IOException innerException)
        : base($"Writable vault ownership is already held at '{lockPath}'.", innerException)
    {
        LockPath = lockPath;
    }

    public string LockPath { get; }
}
