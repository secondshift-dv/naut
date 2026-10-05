using Neuterradise.Profiling.Protocol;
namespace Neuterradise.App.SystemServices.Jobs.Transport;

using System.IO;
using System.IO.Pipes;

public sealed class ProfilingServerTransport : IAsyncDisposable
{
    private readonly NamedPipeServerStream _serverStream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public string PipeName { get; }

    public bool IsConnected => _serverStream.IsConnected;

    public ProfilingServerTransport(string? pipeName = null)
    {
        PipeName = pipeName ?? $"nt-worker-{Guid.NewGuid():N}";
        _serverStream = new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 65536,
            outBufferSize: 65536);
    }

    public async Task WaitForConnectionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _serverStream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteEnvelopeAsync(ProfilingEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProfilingProtocolSerializer.WriteFrameAsync(_serverStream, envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ProfilingEnvelope?> ReadEnvelopeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await ProfilingProtocolSerializer.ReadFrameAsync(_serverStream, cancellationToken).ConfigureAwait(false);
    }

    public void Disconnect()
    {
        if (_serverStream.IsConnected)
        {
            try
            {
                _serverStream.Disconnect();
            }
            catch (InvalidOperationException) { }
            catch (IOException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        Disconnect();
        await _serverStream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
