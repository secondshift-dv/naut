using Neuterradise.Profiling.Protocol;
namespace Neuterradise.App.SystemServices.Jobs;

using System.Collections.Concurrent;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Jobs.Transport;

public sealed class ProfilingWorkerConnection : IAsyncDisposable
{
    private readonly ProfilingWorkerProcessHost _host;
    private readonly bool _ownsHost;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProfilingEnvelope>> _pendingRequests = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private Task? _readLoopTask;
    private CancellationTokenSource? _readLoopCts;
    private bool _disposed;

    public ProfilingWorkerProcessHost Host => _host;

    public bool IsHealthy => _host.State == ProfilingWorkerHostState.Ready &&
                             _host.CurrentProcess is { HasExited: false } &&
                             _host.CurrentProfilingServerTransport is { IsConnected: true };

    public ProfilingWorkerConnection(ProfilingWorkerProcessHost? host = null)
    {
        _ownsHost = host is null;
        _host = host ?? new ProfilingWorkerProcessHost();
    }

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (IsHealthy && _readLoopTask is { IsCompleted: false })
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsHealthy && _readLoopTask is { IsCompleted: false })
            {
                return;
            }

            await ObserveReadLoopAsync(CancelReadLoop()).ConfigureAwait(false);
            await _host.StartAsync(cancellationToken).ConfigureAwait(false);
            StartReadLoop();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<ProfilingEnvelope> SendRequestAsync(
        ProfilingEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        if (request.ProtocolVersion != ProfilingProtocolVersion.Current)
        {
            throw new ProfilingProtocolException(
                $"Request protocol version {request.ProtocolVersion} is incompatible with app protocol version {ProfilingProtocolVersion.Current}.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new ProfilingProtocolException("RequestId cannot be empty.");
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var pipeServer = _host.CurrentProfilingServerTransport;
        if (pipeServer is null || !pipeServer.IsConnected)
        {
            throw new ProfilingWorkerDisconnectedException("Profiling Worker transport is not connected.");
        }

        var tcs = new TaskCompletionSource<ProfilingEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(request.RequestId, tcs))
        {
            throw new ProfilingProtocolException($"RequestId '{request.RequestId}' is already in flight.");
        }

        CancellationTokenRegistration registration = default;
        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(() =>
            {
                if (_pendingRequests.TryRemove(request.RequestId, out var pendingTcs))
                {
                    pendingTcs.TrySetCanceled(cancellationToken);

                    if (_host.CurrentProfilingServerTransport is { IsConnected: true } server)
                    {
                        var cancelEnvelope = ProfilingEnvelope.Create(
                            ProfilingMessageType.CancelRequest,
                            new CancelRequestPayload(request.RequestId));
                        TaskObserver.Observe(
                            server.WriteEnvelopeAsync(cancelEnvelope, CancellationToken.None),
                            "ProfilingWorkerConnection.SendCancellation");
                    }
                }
            });
        }

        try
        {
            await pipeServer.WriteEnvelopeAsync(request, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(request.RequestId, out _);
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        var sentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var request = ProfilingEnvelope.Create(
            ProfilingMessageType.Ping,
            new PingPayload(sentTimestamp));

        var response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.MessageType != ProfilingMessageType.Pong)
        {
            throw new ProfilingProtocolException(
                $"Expected Pong response from worker, but received {response.MessageType}.");
        }

        var receivedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return TimeSpan.FromMilliseconds(Math.Max(0, receivedTimestamp - sentTimestamp));
    }

    public async Task ShutdownAsync(TimeSpan? gracePeriod = null, CancellationToken cancellationToken = default)
    {
        var readLoop = CancelReadLoop();
        FailPendingRequests(new ProfilingWorkerDisconnectedException("Profiling Worker connection is shutting down."));
        await _host.ShutdownAsync(gracePeriod, cancellationToken).ConfigureAwait(false);
        await ObserveReadLoopAsync(readLoop).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await ShutdownAsync().ConfigureAwait(false);
        if (_ownsHost)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }

        _disposed = true;
        _connectionLock.Dispose();
    }

    private void StartReadLoop()
    {
        var readLoopCts = new CancellationTokenSource();
        _readLoopCts = readLoopCts;
        _readLoopTask = Task.Run(() => ReadLoopAsync(readLoopCts.Token));
    }

    private (Task? Task, CancellationTokenSource? Cancellation) CancelReadLoop()
    {
        var task = _readLoopTask;
        var cancellation = _readLoopCts;
        _readLoopTask = null;
        _readLoopCts = null;

        if (cancellation is not null)
        {
            cancellation.Cancel();
        }

        return (task, cancellation);
    }

    private static async Task ObserveReadLoopAsync(
        (Task? Task, CancellationTokenSource? Cancellation) readLoop)
    {
        try
        {
            if (readLoop.Task is not null)
            {
                await readLoop.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (readLoop.Cancellation?.IsCancellationRequested == true)
        {
        }
        finally
        {
            readLoop.Cancellation?.Dispose();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var pipeServer = _host.CurrentProfilingServerTransport;
        if (pipeServer is null) return;

        try
        {
            while (!cancellationToken.IsCancellationRequested && pipeServer.IsConnected)
            {
                var envelope = await pipeServer.ReadEnvelopeAsync(cancellationToken).ConfigureAwait(false);
                if (envelope is null)
                {
                    break;
                }

                if (envelope.ProtocolVersion != ProfilingProtocolVersion.Current)
                {
                    throw new ProfilingProtocolException(
                        $"Profiling Worker response protocol version {envelope.ProtocolVersion} is incompatible with app protocol version {ProfilingProtocolVersion.Current}.");
                }

                if (_pendingRequests.TryRemove(envelope.RequestId, out var tcs))
                {
                    tcs.TrySetResult(envelope);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested || _host.State is ProfilingWorkerHostState.Stopping or ProfilingWorkerHostState.Stopped)
            {
                return;
            }

            _host.NotifyCrash();
            FailPendingRequests(new ProfilingWorkerDisconnectedException($"Profiling Worker transport read failure: {ex.Message}", ex));
            return;
        }

        if (cancellationToken.IsCancellationRequested || _host.State is ProfilingWorkerHostState.Stopping or ProfilingWorkerHostState.Stopped)
        {
            return;
        }

        _host.NotifyCrash();
        FailPendingRequests(new ProfilingWorkerDisconnectedException("Worker process disconnected unexpectedly."));
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var key in _pendingRequests.Keys.ToArray())
        {
            if (_pendingRequests.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(exception);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class ProfilingWorkerDisconnectedException : InvalidOperationException
{
    public ProfilingWorkerDisconnectedException(string message) : base(message) { }
    public ProfilingWorkerDisconnectedException(string message, Exception innerException) : base(message, innerException) { }
}
