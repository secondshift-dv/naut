using System.Diagnostics;
using System.Threading.Channels;

namespace Neuterradise.App.SystemServices.Jobs;

public sealed class JobDispatcher : IAsyncDisposable
{
    /// <summary>
    /// Global execution ceiling. Must be at least the sum of per-lane MaxConcurrency values so that
    /// different resource classes can proceed independently. The <see cref="ResourceGovernor"/>
    /// provides the actual interaction-mode throttling: during foreground interaction, heavy work
    /// collapses to one unit per class and face work pauses entirely, so the higher global cap does
    /// not allow background saturation while the person is active.
    /// </summary>
    public const int DefaultGlobalMaxConcurrency = 5;

    private readonly IReadOnlyDictionary<JobLane, LaneRuntime> _lanes;
    private readonly IReadOnlyDictionary<JobLane, LaneConfiguration> _configurations;
    private readonly Func<Guid, CancellationToken, Task> _workItem;
    private readonly SemaphoreSlim _globalExecutionSlots;
    private readonly int _globalMaxConcurrency;
    private int _globalRunningCount;
    private int _started;

    public JobDispatcher(
        IEnumerable<LaneConfiguration> configurations,
        Func<Guid, CancellationToken, Task> workItem,
        int globalMaxConcurrency = DefaultGlobalMaxConcurrency)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        ArgumentNullException.ThrowIfNull(workItem);
        if (globalMaxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(globalMaxConcurrency),
                "The dispatcher must allow at least one globally running job.");
        }

        _workItem = workItem;
        _globalMaxConcurrency = globalMaxConcurrency;
        _globalExecutionSlots = new SemaphoreSlim(globalMaxConcurrency, globalMaxConcurrency);

        var configurationsList = configurations.ToList();
        if (configurationsList.Count == 0)
        {
            throw new ArgumentException("At least one lane configuration is required.", nameof(configurations));
        }

        _configurations = configurationsList.ToDictionary(
            configuration => configuration.Lane,
            configuration => configuration);

        _lanes = configurationsList.ToDictionary(
            configuration => configuration.Lane,
            configuration => new LaneRuntime(
                configuration,
                Channel.CreateBounded<Guid>(new BoundedChannelOptions(LaneQueueBounds.WindowSize(configuration))
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = false,
                }),
                new CancellationTokenSource()));
    }

    public IReadOnlyDictionary<JobLane, LaneConfiguration> Configurations => _configurations;

    public int GlobalMaxConcurrency => _globalMaxConcurrency;

    public int GlobalRunningCount => Volatile.Read(ref _globalRunningCount);

    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("A JobDispatcher can start only once.");
        }

        foreach (var runtime in _lanes.Values)
        {
            runtime.Workers = Enumerable.Range(0, Math.Min(runtime.Configuration.MaxConcurrency, _globalMaxConcurrency))
                .Select(_ => Task.Run(
                    () => RunWorkerAsync(runtime, runtime.WorkerShutdown.Token),
                    CancellationToken.None))
                .ToArray();
        }

        return Task.CompletedTask;
    }

    public int GetQueuedCount(JobLane lane) => _lanes[lane].Queue.Reader.Count;

    public int GetRunningCount(JobLane lane) => Volatile.Read(ref _lanes[lane].RunningCount);

    public int GetMaxObservedQueueDepth(JobLane lane) => Volatile.Read(ref _lanes[lane].MaxObservedDepth);

    public async Task EnqueueAsync(JobLane lane, Guid jobId, CancellationToken cancellationToken = default)
    {
        var runtime = _lanes[lane];
        while (await runtime.Queue.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
        {
            if (runtime.Queue.Writer.TryWrite(jobId))
            {
                TrackQueueDepth(runtime);
                return;
            }
        }

        throw new OperationCanceledException("The lane queue closed before the job could be dispatched.");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var runtime in _lanes.Values)
        {
            runtime.Queue.Writer.TryComplete();
        }

        var workers = _lanes.Values.SelectMany(runtime => runtime.Workers).ToArray();
        var allWorkers = workers.Length == 0 ? Task.CompletedTask : Task.WhenAll(workers);

        if (workers.Length > 0)
        {
            using var gracefulWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            gracefulWait.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await allWorkers.WaitAsync(gracefulWait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (gracefulWait.IsCancellationRequested)
            {
                // The bounded phase only decides when to signal worker cancellation. It is not
                // permission to dispose scheduler resources or release VaultLock while workers live.
            }
        }

        foreach (var runtime in _lanes.Values)
        {
            runtime.WorkerShutdown.Cancel();
        }

        // safety outranks the advertised shutdown latency. After cancellation is signalled,
        // every worker must actually terminate (or fault) before dispatcher disposal can complete.
        // This keeps JobScheduler/ProductionRuntimeRegistry/ShutdownCoordinator ownership honest:
        // VaultLock cannot be released while a worker can still mutate catalog or managed storage.
        await allWorkers.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var runtime in _lanes.Values)
        {
            runtime.WorkerShutdown.Dispose();
        }

        _globalExecutionSlots.Dispose();
    }

    private async Task RunWorkerAsync(LaneRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            while (await runtime.Queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Do not remove the durable job from the visible lane queue before global capacity is
                // available. Capacity pressure therefore remains a truthful waiting state, and a lane
                // cannot hide an arbitrary backlog in workers that are merely waiting for a slot.
                await _globalExecutionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                var acquiredGlobalSlot = true;
                try
                {
                    if (!runtime.Queue.Reader.TryRead(out var jobId))
                    {
                        continue;
                    }

                    Interlocked.Increment(ref _globalRunningCount);
                    Interlocked.Increment(ref runtime.RunningCount);
                    try
                    {
                        await _workItem(jobId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception exception)
                    {
                        Trace.TraceWarning(
                            "Job dispatcher work item {0} in lane {1} threw an unexpected exception: {2}",
                            jobId,
                            runtime.Configuration.Lane,
                            exception);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref runtime.RunningCount);
                        Interlocked.Decrement(ref _globalRunningCount);
                    }
                }
                finally
                {
                    if (acquiredGlobalSlot)
                    {
                        _globalExecutionSlots.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void TrackQueueDepth(LaneRuntime runtime)
    {
        var depth = runtime.Queue.Reader.Count;
        var current = Volatile.Read(ref runtime.MaxObservedDepth);
        while (depth > current)
        {
            var updated = Interlocked.CompareExchange(ref runtime.MaxObservedDepth, depth, current);
            if (updated == current)
            {
                break;
            }

            current = updated;
        }
    }

    private sealed class LaneRuntime
    {
        public LaneRuntime(LaneConfiguration configuration, Channel<Guid> queue, CancellationTokenSource workerShutdown)
        {
            Configuration = configuration;
            Queue = queue;
            WorkerShutdown = workerShutdown;
        }

        public LaneConfiguration Configuration { get; }

        public Channel<Guid> Queue { get; }

        public CancellationTokenSource WorkerShutdown { get; }

        public Task[] Workers { get; set; } = [];

        public int RunningCount;

        public int MaxObservedDepth;
    }
}
