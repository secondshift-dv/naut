using Neuterradise.Profiling.Protocol;
namespace Neuterradise.Profiling.Worker.Transport;

using System.IO.Pipes;

public sealed class ProfilingClientTransport : IAsyncDisposable
{
    private readonly NamedPipeClientStream _clientStream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public string PipeName { get; }
    public bool IsConnected => _clientStream.IsConnected;

    public ProfilingClientTransport(string pipeName)
    {
        PipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        _clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public async Task ConnectAsync(int timeoutMs = 10000, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _clientStream.ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteEnvelopeAsync(ProfilingEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProfilingProtocolSerializer.WriteFrameAsync(_clientStream, envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ProfilingEnvelope?> ReadEnvelopeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await ProfilingProtocolSerializer.ReadFrameAsync(_clientStream, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _clientStream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
