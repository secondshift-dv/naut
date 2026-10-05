using System.Collections.Concurrent;
using Neuterradise.App.SystemServices.Resources;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Jobs;

public sealed class JobScheduler : IAsyncDisposable
{

    public static readonly TimeSpan DefaultShutdownGracePeriod = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Conservative recovery poll. Work is normally dispatched by wake signals (enqueue, dependency
    /// completion, retry eligibility, resume, prioritize, cancel); this only guarantees that a missed
    /// signal can never strand durable work.
    /// </summary>
    public static readonly TimeSpan RecoveryPollInterval = TimeSpan.FromMilliseconds(750);

    private static readonly TimeSpan _progressPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan _quiescencePollInterval = TimeSpan.FromMilliseconds(20);

    private readonly IClock _clock;
    private readonly CatalogDb _catalog;
    private readonly JobHandlerRegistry _registry;
    private readonly SchedulerReads _reads;
    private readonly JobWrites _writes;
    private readonly JobLeaseStore _leaseStore;
    private readonly JobDependencyEvaluator _evaluator;
    private readonly JobDispatcher _dispatcher;
    private readonly JobProgressHub _hub;
    private readonly JobCancellationOperations _cancellation;
    private readonly ImportPriorityOperations _importPriority;
    private readonly JobPriorityAging _aging;
    private readonly JobLaneFairness _fairness;
    private readonly IReadOnlyDictionary<JobLane, LaneConfiguration> _laneConfigurations;

    private readonly Func<ProfilingWorkerMetrics>? _workerMetrics;
    private readonly ResourceGovernor _governor;
    private readonly Action _onSignal;

    private readonly ConcurrentDictionary<Guid, JobLease> _runningLeases = new();
    private readonly ConcurrentDictionary<Guid, byte> _inflight = new();
    private readonly ConcurrentDictionary<JobLane, SemaphoreSlim> _laneWakeSignals = new();
    private readonly SemaphoreSlim _heavyExecutionSlots = new(2, 2);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _backgroundTasks = [];
    private int _started;
    private long _completedCount;
    private long _failedCount;
    private long _completionConflicts;
    private long _retryScheduledCount;
    private long _cancelledCount;

    public JobScheduler(
        CatalogDb catalog,
        JobHandlerRegistry registry,
        IClock? clock = null,
        IEnumerable<LaneConfiguration>? laneConfigurations = null,
        double progressUpdatesPerSecond = JobProgressHub.DefaultMaxUpdatesPerSecond,
        JobCancellationOperations? cancellation = null,
        JobPriorityAging? priorityAging = null,
        Func<ProfilingWorkerMetrics>? workerMetrics = null,
        ResourceGovernor? governor = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _clock = clock ?? new SystemClock();
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _laneConfigurations = (laneConfigurations ?? LaneDefaults.CreateDefault())
            .ToDictionary(configuration => configuration.Lane);
        _reads = new SchedulerReads(catalog);
        _writes = new JobWrites(catalog);
        _leaseStore = new JobLeaseStore(catalog, _clock);
        _evaluator = new JobDependencyEvaluator(_reads);
        _hub = new JobProgressHub(_clock, PublishProgressAsync, progressUpdatesPerSecond);
        _cancellation = cancellation ?? new JobCancellationOperations(catalog, _clock);
        _importPriority = new ImportPriorityOperations(catalog);
        _aging = priorityAging ?? JobPriorityAging.Default;
        _fairness = new JobLaneFairness();
        _workerMetrics = workerMetrics;
        _dispatcher = new JobDispatcher(_laneConfigurations.Values, ExecuteJobAsync);
        _governor = governor ?? ResourceGovernor.Shared;
        _onSignal = () => Wake();
    }

    public JobProgressHub Progress => _hub;

    public JobCancellationOperations Cancellation => _cancellation;

    public JobPriorityAging PriorityAging => _aging;

    /// <summary>
    /// Optional observer called after every job completion (success or failure).
    /// The first argument is the JobId; the second is the execution result.
    /// Used by media preparation capability tracking to update per-asset readiness on completion.
    /// </summary>
    public Func<Guid, JobExecutionResult, Task>? OnJobCompleted { get; set; }

    /// <summary>
    /// Optional durable-state reconciliation observer. It is invoked after process-restart
    /// reconciliation and on a bounded recovery poll while the scheduler is running. This covers
    /// terminal state changes that do not flow through normal handler completion, including idle
    /// cancellation and retry-budget exhaustion.
    /// </summary>
    public Func<CancellationToken, Task>? OnReconciled { get; set; }

    public IReadOnlyDictionary<JobLane, LaneConfiguration> LaneConfigurations => _laneConfigurations;

    public long CompletedJobCount => Interlocked.Read(ref _completedCount);

    public long FailedJobCount => Interlocked.Read(ref _failedCount);

    public long RetryScheduledCount => Interlocked.Read(ref _retryScheduledCount);

    public long CancelledJobCount => Interlocked.Read(ref _cancelledCount);

    public long CompletionConflictCount => Interlocked.Read(ref _completionConflicts);

    public int GetQueuedCount(JobLane lane) => _dispatcher.GetQueuedCount(lane);

    public int GetMaxObservedQueueDepth(JobLane lane) => _dispatcher.GetMaxObservedQueueDepth(lane);

    public Task PauseSchedulerAsync(CancellationToken cancellationToken = default) =>
        _cancellation.PauseGlobalAsync(cancellationToken);

    public Task ResumeSchedulerAsync(CancellationToken cancellationToken = default) =>
        _cancellation.ResumeGlobalAsync(cancellationToken);

    public Task<bool> IsSchedulerPausedAsync(CancellationToken cancellationToken = default) =>
        _cancellation.IsGlobalPausedAsync(cancellationToken);

    public Task<bool> PrioritizeJobAsync(
        Guid jobId,
        int priority = JobPriorityPolicy.MaximumPriority,
        bool boostDependencies = true,
        CancellationToken cancellationToken = default) =>
        _cancellation.PrioritizeJobAsync(jobId, priority, boostDependencies, cancellationToken);

    public Task<int> PrioritizeOwnerAsync(
        string ownerType,
        Guid ownerId,
        int priority = JobPriorityPolicy.MaximumPriority,
        CancellationToken cancellationToken = default) =>
        _cancellation.PrioritizeOwnerAsync(ownerType, ownerId, priority, cancellationToken);

    public Task<bool> SkipFailureAsync(
        Guid jobId,
        bool unblockDependents = false,
        CancellationToken cancellationToken = default) =>
        _cancellation.SkipFailureAsync(jobId, unblockDependents, cancellationToken);

    public Task<int> ClearFinishedOrCancelledHistoryAsync(
        string? ownerType = null,
        Guid? ownerId = null,
        CancellationToken cancellationToken = default) =>
        _cancellation.ClearFinishedOrCancelledAsync(ownerType, ownerId, cancellationToken);

    public void Wake(JobLane? lane = null)
    {
        if (lane is { } specificLane)
        {
            if (_laneWakeSignals.TryGetValue(specificLane, out var signal) && signal.CurrentCount == 0)
            {
                try { signal.Release(); } catch (SemaphoreFullException) { }
            }
        }
        else
        {
            foreach (var signal in _laneWakeSignals.Values)
            {
                if (signal.CurrentCount == 0)
                {
                    try { signal.Release(); } catch (SemaphoreFullException) { }
                }
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("A JobScheduler can start only once.");
        }

        await ReconciliationJobAuthority.RecoverMissingJobsAsync(_catalog, cancellationToken).ConfigureAwait(false);
        await _writes.ReconcileInterruptedRunningJobsAsync(cancellationToken).ConfigureAwait(false);

        if (OnReconciled is { } reconciled)
        {
            try
            {
                await reconciled(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Reconciliation observer failure must not prevent scheduler startup.
            }
        }

        await _dispatcher.StartAsync().ConfigureAwait(false);
        JobSignals.Raised += _onSignal;
        foreach (var lane in _laneConfigurations.Keys)
        {
            _backgroundTasks.Add(Task.Run(
                () => RefillLaneAsync(lane, _shutdown.Token),
                CancellationToken.None));
        }

        _backgroundTasks.Add(Task.Run(
            () => PublishLoopAsync(_shutdown.Token),
            CancellationToken.None));
        _backgroundTasks.Add(Task.Run(
            () => TerminalProjectionRecoveryLoopAsync(_shutdown.Token),
            CancellationToken.None));
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        JobSignals.Raised -= _onSignal;
        _shutdown.Cancel();
        try
        {
            await Task.WhenAll(_backgroundTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {

        }

        await _dispatcher.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SchedulerShutdownReport> ShutdownAsync(
        TimeSpan? gracePeriod = null,
        Func<CancellationToken, Task>? releaseWorkerAsync = null,
        CancellationToken cancellationToken = default)
    {
        var grace = gracePeriod ?? DefaultShutdownGracePeriod;
        if (grace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gracePeriod),
                "A graceful shutdown needs a positive bounded grace period.");
        }

        var startedAt = _clock.UtcNow;
        var cancelledBefore = CancelledJobCount;

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(grace);

        JobSignals.Raised -= _onSignal;
        foreach (var jobId in _runningLeases.Keys)
        {
            _cancellation.SetIntent(jobId, JobControlIntent.Shutdown);
        }
        _shutdown.Cancel();
        Wake();
        try
        {
            await Task.WhenAll(_backgroundTasks).WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {

        }

        long flushed = 0;
        try
        {
            flushed = await _hub.FlushPendingAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {

        }

        var drained = await WaitForInflightSafeBoundaryAsync(bounded.Token).ConfigureAwait(false);
        await _dispatcher.StopAsync(bounded.Token).ConfigureAwait(false);

        var workerReleased = false;
        if (releaseWorkerAsync is not null)
        {
            try
            {
                await releaseWorkerAsync(CancellationToken.None).ConfigureAwait(false);
                workerReleased = true;
            }
            catch (Exception)
            {
                workerReleased = false;
            }
        }

        var nonterminal = await _reads.CountNonterminalJobsAsync(CancellationToken.None).ConfigureAwait(false);

        return new SchedulerShutdownReport(
            DrainedWithinGracePeriod: drained,
            NonterminalJobsLeftForRestart: nonterminal,
            CancelledDuringShutdown: CancelledJobCount - cancelledBefore,
            ProgressSnapshotsFlushed: flushed,
            ProfilingWorkerReleased: workerReleased,
            ElapsedMs: (long)(_clock.UtcNow - startedAt).TotalMilliseconds);
    }

    private async Task<bool> WaitForInflightSafeBoundaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_inflight.IsEmpty)
            {
                await Task.Delay(_quiescencePollInterval, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return _inflight.IsEmpty;
        }
    }

    public async Task WaitForQuiescenceAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var hasDispatchableWork = await _reads.HasDispatchableWorkAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Lanes.Values.All(lane => lane.Running == 0 && lane.Queued == 0)
                && !hasDispatchableWork)
            {
                return;
            }

            await Task.Delay(_quiescencePollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<SchedulerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var lanes = await BuildLaneSnapshotsAsync(cancellationToken).ConfigureAwait(false);

        return new SchedulerSnapshot(
            _clock.UtcNow.ToUnixTimeMilliseconds(),
            lanes,
            lanes.Values.Sum(lane => lane.Running),
            lanes.Values.Sum(lane => lane.Queued),
            lanes.Values.Sum(lane => lane.Succeeded),
            lanes.Values.Sum(lane => lane.FailedTerminal));
    }

    public async Task<SchedulerMetricsSnapshot> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        var lanes = await BuildLaneSnapshotsAsync(cancellationToken).ConfigureAwait(false);

        return new SchedulerMetricsSnapshot(
            _clock.UtcNow.ToUnixTimeMilliseconds(),
            lanes,
            RetryScheduledCount,
            FailedJobCount,
            CancelledJobCount,
            CompletionConflictCount,
            _hub.ObservedPublishesPerSecond,
            _hub.MaxUpdatesPerSecond,
            _workerMetrics?.Invoke() ?? ProfilingWorkerMetrics.NotStarted);
    }

    private async Task<IReadOnlyDictionary<JobLane, LaneSnapshot>> BuildLaneSnapshotsAsync(
        CancellationToken cancellationToken)
    {
        var nowMs = _clock.UtcNow.ToUnixTimeMilliseconds();
        var counts = await _reads.GetStateCountsByLaneAsync(cancellationToken).ConfigureAwait(false);
        var oldestRunnable = await _reads.GetOldestRunnableCreatedAtByLaneAsync(nowMs, cancellationToken)
            .ConfigureAwait(false);

        var lanes = new Dictionary<JobLane, LaneSnapshot>();
        foreach (var (lane, configuration) in _laneConfigurations)
        {
            var laneCounts = counts.TryGetValue(lane, out var value)
                ? value
                : _emptyCounts;
            var laneSnapshot = new LaneSnapshot(
                lane,
                configuration.MaxConcurrency,
                Running: (int)GetCount(laneCounts, JobState.Running),
                Queued: _dispatcher.GetQueuedCount(lane),
                Pending: (int)GetCount(laneCounts, JobState.Pending),
                Succeeded: (int)GetCount(laneCounts, JobState.Succeeded),
                FailedRetryable: (int)GetCount(laneCounts, JobState.FailedRetryable),
                FailedTerminal: (int)GetCount(laneCounts, JobState.FailedTerminal),
                Cancelled: (int)GetCount(laneCounts, JobState.Cancelled),
                Runnable: (int)GetCount(laneCounts, JobState.Runnable),
                OldestRunnableAgeMs: oldestRunnable.TryGetValue(lane, out var createdAtMs)
                    ? Math.Max(0, nowMs - createdAtMs)
                    : null);
            lanes.Add(lane, laneSnapshot);
        }

        return lanes;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _dispatcher.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        foreach (var signal in _laneWakeSignals.Values)
        {
            signal.Dispose();
        }
        _heavyExecutionSlots.Dispose();
    }

    private async Task RefillLaneAsync(JobLane lane, CancellationToken cancellationToken)
    {
        var wakeSignal = _laneWakeSignals.GetOrAdd(lane, _ => new SemaphoreSlim(0, 1));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var isPaused = await _cancellation.IsGlobalPausedAsync(cancellationToken).ConfigureAwait(false);
                var rescheduled = false;
                var promoted = false;
                var enqueued = false;

                if (!isPaused)
                {
                    rescheduled = await PromoteRetriesAsync(lane, cancellationToken).ConfigureAwait(false);
                    promoted = await PromotePendingAsync(lane, cancellationToken).ConfigureAwait(false);
                    if (lane == JobLane.Io)
                        await YieldForFocusedTransferAsync(cancellationToken).ConfigureAwait(false);
                    enqueued = await EnqueueRunnableAsync(lane, cancellationToken).ConfigureAwait(false);
                }

                if (rescheduled || promoted || enqueued)
                {
                    await Task.Yield();
                }
                else
                {
                    try
                    {
                        await wakeSignal.WaitAsync(RecoveryPollInterval, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

        }
    }

    private async Task YieldForFocusedTransferAsync(CancellationToken cancellationToken)
    {
        var focused = await _importPriority.GetFocusedImportUnitAsync(cancellationToken).ConfigureAwait(false);
        if (focused is not { } unitId) return;
        foreach (var running in await _reads.GetYieldableTransfersAsync(unitId, cancellationToken)
                     .ConfigureAwait(false))
            _cancellation.RequestYieldRunning(running);
    }

    private async Task<bool> PromoteRetriesAsync(JobLane lane, CancellationToken cancellationToken)
    {
        var window = LaneQueueBounds.WindowSize(_laneConfigurations[lane]);
        var now = _clock.UtcNow;
        var changed = false;

        var retryable = await _reads.GetRetryableJobIdsAsync(
            lane, window, now.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);
        foreach (var jobId in retryable)
        {
            var record = await _reads.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (record is null || record.State != JobState.FailedRetryable)
            {
                continue;
            }

            if (await _writes.TryRescheduleRetryAsync(
                    jobId, record.RowVersion, now, cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _retryScheduledCount);
                changed = true;
            }
        }

        var exhausted = await _reads.GetExhaustedRetryJobIdsAsync(lane, window, cancellationToken)
            .ConfigureAwait(false);
        foreach (var jobId in exhausted)
        {
            var record = await _reads.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (record is null || record.State != JobState.FailedRetryable)
            {
                continue;
            }

            changed |= await _writes.TryExhaustRetriesAsync(
                jobId,
                record.RowVersion,
                record.ErrorCode ?? "RETRY_BUDGET_EXHAUSTED",
                $"Attempt {record.Attempt} of {record.MaxAttempts} exhausted the retry budget.",
                now,
                cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    private async Task<bool> PromotePendingAsync(JobLane lane, CancellationToken cancellationToken)
    {
        var window = LaneQueueBounds.WindowSize(_laneConfigurations[lane]);
        var pendingIds = await _reads.GetPendingJobIdsAsync(lane, window, cancellationToken).ConfigureAwait(false);
        var promoted = false;

        foreach (var pendingId in pendingIds)
        {
            var evaluation = await _evaluator.EvaluateAsync(pendingId, cancellationToken).ConfigureAwait(false);
            if (evaluation.Status != DependencyStatus.Satisfied)
            {
                continue;
            }

            var record = await _reads.GetJobAsync(pendingId, cancellationToken).ConfigureAwait(false);
            if (record is null || record.State != JobState.Pending)
            {
                continue;
            }

            promoted |= await _writes.MarkRunnableAsync(
                pendingId,
                record.RowVersion,
                _clock.UtcNow,
                cancellationToken).ConfigureAwait(false);
        }

        return promoted;
    }

    private async Task<bool> EnqueueRunnableAsync(JobLane lane, CancellationToken cancellationToken)
    {
        var nowMs = _clock.UtcNow.ToUnixTimeMilliseconds();
        var window = LaneQueueBounds.WindowSize(_laneConfigurations[lane]);

        var evaluation = _fairness.Evaluate(lane);

        if (evaluation.Kind == FairnessDispatchKind.Fairness)
        {
            var fairnessId = await _reads.GetOldestLowerTierCandidateAsync(
                lane, evaluation.BelowTier, nowMs, cancellationToken).ConfigureAwait(false);
            if (fairnessId is { } fid && _inflight.TryAdd(fid, 0))
            {
                var fairnessRecord = await _reads.GetJobAsync(fid, cancellationToken)
                    .ConfigureAwait(false);
                var candidateTier = fairnessRecord is not null
                    ? JobLaneFairness.ClassifyTier(fairnessRecord.Priority)
                    : (FairnessTier?)null;

                if (candidateTier is { } ct && ct > evaluation.BelowTier)
                {
                    try
                    {
                        await _dispatcher.EnqueueAsync(lane, fid, cancellationToken)
                            .ConfigureAwait(false);
                        _fairness.RecordFairnessDispatch(lane, ct);
                        return true;
                    }
                    catch
                    {
                        _inflight.TryRemove(fid, out _);
                        throw;
                    }
                }

                _inflight.TryRemove(fid, out _);
            }

            _fairness.RecordFairnessDispatch(lane, evaluation.BelowTier);
        }

        var runnableIds = await _reads.GetRunnableJobIdsAsync(
            lane,
            window,
            nowMs,
            _aging,
            cancellationToken).ConfigureAwait(false);
        var enqueued = false;

        foreach (var jobId in runnableIds)
        {
            if (!_inflight.TryAdd(jobId, 0))
            {
                continue;
            }

            try
            {
                await _dispatcher.EnqueueAsync(lane, jobId, cancellationToken).ConfigureAwait(false);
                enqueued = true;

                var record = await _reads.GetJobAsync(jobId, cancellationToken)
                    .ConfigureAwait(false);
                var actualTier = record is not null
                    ? JobLaneFairness.ClassifyTier(record.Priority)
                    : FairnessTier.Background;
                _fairness.RecordNormalDispatch(lane, actualTier);
            }
            catch
            {
                _inflight.TryRemove(jobId, out _);
                throw;
            }
        }

        return enqueued;
    }

    private async Task ExecuteJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            var record = await _reads.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (record is null || record.State != JobState.Runnable)
            {
                return;
            }

            if (_cancellation.IsCancellationRequested(jobId))
            {
                await _cancellation.RequestCancelAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            using var importPressure = record.Kind is "TransferOriginal" or "HashMedia"
                or "ExtractMetadata" or "GenerateThumbnail" or "GenerateVideoMediaAssets" or "FaceAnalysis"
                ? ResourceGovernor.Shared.EnterImportPressure()
                : null;
            using var permit = await AcquirePermitAsync(record.Lane, cancellationToken).ConfigureAwait(false);

            var lease = await _leaseStore.TryClaimAsync(record, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                return;
            }

            _runningLeases.TryAdd(jobId, lease);
            using var cancelSignal = _cancellation.Register(jobId, cancellationToken);
            var progress = new HubProgressReporter(_hub, jobId);
            try
            {
                var context = new JobExecutionContext(
                    lease.Record,
                    cancelSignal.Token,
                    progress,
                    lease,
                    _clock);

                if (!_registry.TryGet(lease.Record.Kind, out var handler))
                {
                    await lease.CompleteAsync(
                        JobExecutionResult.Failed(
                            JobFailureClassification.DeterministicInvalidInput,
                            JobHandlerRegistry.UnknownJobKindErrorCode,
                            $"No handler is registered for job kind '{lease.Record.Kind}'."),
                        CancellationToken.None).ConfigureAwait(false);
                    Interlocked.Increment(ref _failedCount);
                    return;
                }

                var result = await handler.ExecuteAsync(context, cancelSignal.Token).ConfigureAwait(false);
                if (result.IsSucceeded)
                {
                    await FlushFinalProgressAsync(lease, jobId, CancellationToken.None).ConfigureAwait(false);
                }

                await CompleteAttemptAsync(lease, jobId, result).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancelSignal.IsCancellationRequested)
            {
                var intent = _cancellation.PeekIntent(jobId);
                if (intent == JobControlIntent.None && cancellationToken.IsCancellationRequested)
                {
                    // A lease can cross the tiny interval between the shutdown intent snapshot and
                    // scheduler-token cancellation. Scheduler shutdown is still the semantic owner.
                    _cancellation.SetIntent(jobId, JobControlIntent.Shutdown);
                    intent = JobControlIntent.Shutdown;
                }

                if (intent != JobControlIntent.None)
                {
                    await CompleteAttemptAsync(
                        lease,
                        jobId,
                        JobExecutionResult.Cancelled(
                            "The handler stopped at a safe boundary after its execution token was signalled."))
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The linked cancellation registration normally handles scheduler shutdown above.
                // This fallback is only reachable if cancellation occurs before registration becomes observable.
            }
            catch (Exception exception)
            {
                await CompleteAttemptAsync(
                    lease,
                    jobId,
                    JobExecutionResult.Failed(
                        JobFailureClassification.DeterministicInvalidInput,
                        "HANDLER_EXCEPTION",
                        exception.Message)).ConfigureAwait(false);
            }
            finally
            {
                _runningLeases.TryRemove(jobId, out _);
            }
        }
        finally
        {
            _inflight.TryRemove(jobId, out _);
        }
    }

    private async Task CompleteAttemptAsync(JobLease lease, Guid jobId, JobExecutionResult result)
    {
        var intent = _cancellation.PeekIntent(jobId);
        if (!result.IsSucceeded && result.Classification == JobFailureClassification.Cancelled)
        {
            if (intent == JobControlIntent.Shutdown)
            {
                var interrupted = await lease.InterruptForShutdownAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                if (!interrupted)
                {
                    Interlocked.Increment(ref _completionConflicts);
                }

                _cancellation.Finalize(jobId);
                Wake();
                return;
            }

            result = intent switch
            {
                JobControlIntent.Pause => JobExecutionResult.Paused(
                    "The handler stopped at a safe boundary because the import was paused."),
                JobControlIntent.Yield => JobExecutionResult.Yielded(),
                JobControlIntent.Cancel => JobExecutionResult.Cancelled(
                    "The handler stopped at a safe boundary because cancellation was requested."),
                _ => result,
            };
        }

        var completed = await lease.CompleteAsync(result, CancellationToken.None).ConfigureAwait(false);
        if (!completed)
        {
            Interlocked.Increment(ref _completionConflicts);
            return;
        }

        if (result.IsSucceeded)
        {
            Interlocked.Increment(ref _completedCount);
            _cancellation.Finalize(jobId);
            Wake();

            try
            {
                if (OnJobCompleted is { } observer)
                {
                    await observer(jobId, result).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Observer failure must not prevent the scheduler from continuing.
            }

            return;
        }

        switch (lease.RetryDecision?.Outcome)
        {
            case JobRetryOutcome.Cancelled:
                Interlocked.Increment(ref _cancelledCount);
                _cancellation.Finalize(jobId);
                try
                {
                    if (OnJobCompleted is { } cancelledObserver)
                    {
                        await cancelledObserver(jobId, result).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    // Projection remains recoverable through scheduler reconciliation.
                }
                break;
            case JobRetryOutcome.Paused:
                _cancellation.Finalize(jobId);
                break;
            case JobRetryOutcome.Yielded:
                _cancellation.Finalize(jobId);
                break;
            case JobRetryOutcome.Retry:
                Interlocked.Increment(ref _failedCount);
                if (lease.RetryDecision?.Backoff is { } backoff)
                {
                    JobSignals.RaiseAfter(backoff + TimeSpan.FromMilliseconds(25));
                }

                break;
            default:
                Interlocked.Increment(ref _failedCount);
                _cancellation.Finalize(jobId);
                try
                {
                    if (OnJobCompleted is { } observer)
                    {
                        await observer(jobId, result).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    // Observer failure must not prevent the scheduler from continuing.
                }

                break;
        }

        Wake();
    }

    private async ValueTask<IDisposable?> AcquirePermitAsync(JobLane lane, CancellationToken cancellationToken)
    {
        var resourceClass = lane switch
        {
            JobLane.Fast => (ResourceClass?)null,
            JobLane.Io => ResourceClass.DiskHeavy,
            JobLane.Cpu => ResourceClass.CpuHeavy,
            JobLane.Media => ResourceClass.MediaDerivation,
            JobLane.Face => ResourceClass.Face,
            _ => ResourceClass.CpuHeavy,
        };

        if (resourceClass is not { } cls)
            return null;

        await _heavyExecutionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resourcePermit = await _governor.AcquireAsync(cls, cancellationToken)
                .ConfigureAwait(false);
            return new HeavyExecutionPermit(resourcePermit, _heavyExecutionSlots);
        }
        catch
        {
            _heavyExecutionSlots.Release();
            throw;
        }
    }

    private sealed class HeavyExecutionPermit : IDisposable
    {
        private IDisposable? _resourcePermit;
        private SemaphoreSlim? _globalGate;

        public HeavyExecutionPermit(IDisposable resourcePermit, SemaphoreSlim globalGate)
        {
            _resourcePermit = resourcePermit;
            _globalGate = globalGate;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _resourcePermit, null)?.Dispose();
            Interlocked.Exchange(ref _globalGate, null)?.Release();
        }
    }

    private async Task TerminalProjectionRecoveryLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RecoveryPollInterval, cancellationToken).ConfigureAwait(false);
                if (OnReconciled is not { } reconciled)
                {
                    continue;
                }

                try
                {
                    await reconciled(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // Durable reconciliation is idempotent. A later bounded pass retries failures.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

        }
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _hub.PublishDueAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(_progressPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

        }
    }

    private async Task PublishProgressAsync(JobProgressSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (_runningLeases.TryGetValue(snapshot.JobId, out var lease))
        {
            await lease.ReportProgressAsync(
                snapshot.Completed,
                snapshot.Total,
                snapshot.Stage,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FlushFinalProgressAsync(JobLease lease, Guid jobId, CancellationToken cancellationToken)
    {
        var total = _hub.GetLatest(jobId)?.Total
            ?? (lease.Record.ProgressTotal > 0 ? lease.Record.ProgressTotal : (long?)null);
        if (total is not null)
        {
            await lease.ReportProgressAsync(total.Value, total.Value, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static long GetCount(
        IReadOnlyDictionary<JobState, long> counts,
        JobState state) =>
        counts.TryGetValue(state, out var count) ? count : 0;

    private static readonly IReadOnlyDictionary<JobState, long> _emptyCounts =
        new Dictionary<JobState, long>();

    private sealed class HubProgressReporter : IJobProgressReporter
    {
        private readonly JobProgressHub _hub;
        private readonly Guid _jobId;

        public HubProgressReporter(JobProgressHub hub, Guid jobId)
        {
            _hub = hub;
            _jobId = jobId;
        }

        public Task ReportProgressAsync(
            long completed,
            long total,
            string? stage,
            CancellationToken cancellationToken = default)
        {
            _hub.Report(_jobId, completed, total, stage);
            return Task.CompletedTask;
        }
    }
}
