using System.Diagnostics;

namespace Neuterradise.App.Shell;

/// <summary>
/// Collapses a burst of "something changed" signals into one refresh.
///
/// Catalog writes arrive in bursts (an import writes per item and per job; a favorite toggle is one
/// write the page already reflects). Refreshing a whole page for each signal re-ran every query and
/// rebuilt every card many times a second. This waits for the burst to settle (trailing debounce),
/// never runs two refreshes at once, and runs exactly one more if signals arrive during a refresh.
/// </summary>
public sealed class RefreshCoalescer : IDisposable
{
    private readonly Func<CancellationToken, Task> _refresh;
    private readonly TimeSpan _quietPeriod;
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _pending;
    private bool _running;
    private bool _rerunRequested;
    private long _signals;
    private long _runs;
    private bool _disposed;

    public RefreshCoalescer(Func<CancellationToken, Task> refresh, TimeSpan quietPeriod)
    {
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _quietPeriod = quietPeriod;
    }

    public long SignalCount => Interlocked.Read(ref _signals);

    public long RunCount => Interlocked.Read(ref _runs);

    /// <summary>Records a change. The refresh runs once the signals stop for the quiet period.</summary>
    public void Signal()
    {
        Interlocked.Increment(ref _signals);
        CancellationTokenSource debounce;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_running)
            {
                _rerunRequested = true;
                return;
            }

            _pending?.Cancel();
            _pending?.Dispose();
            _pending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            debounce = _pending;
        }

        TaskObserver.Observe(RunAfterQuietAsync(debounce.Token), "Coalesced refresh");
    }

    private async Task RunAfterQuietAsync(CancellationToken debounceToken)
    {
        try
        {
            await Task.Delay(_quietPeriod, debounceToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (true)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _running = true;
                _rerunRequested = false;
            }

            try
            {
                Interlocked.Increment(ref _runs);
                await _refresh(_lifetime.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Coalesced refresh failed: {0}", exception.GetType().Name);
            }
            finally
            {
                lock (_sync)
                {
                    _running = false;
                }
            }

            lock (_sync)
            {
                if (!_rerunRequested || _disposed)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending?.Cancel();
            _pending?.Dispose();
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
