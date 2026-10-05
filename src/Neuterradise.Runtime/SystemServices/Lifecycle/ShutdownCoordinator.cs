using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Lifecycle;

public sealed class ShutdownCoordinator
{
    public static readonly TimeSpan DefaultShutdownBudget = TimeSpan.FromSeconds(10);
    private readonly BootstrapContext _context;
    private readonly Action? _stopAcceptingCommands;
    private readonly Func<TimeSpan, CancellationToken, Task<SchedulerShutdownReport>>? _shutdownSchedulerAsync;
    private readonly IReadOnlyList<IAsyncDisposable> _services;
    private readonly List<StartupState> _stateHistory = [StartupState.Ready];
    private readonly AppStatePaths? _appState;
    private readonly Guid _sessionId;
    private readonly Guid _sessionGeneration;
    private readonly object _shutdownGate = new();
    private Task<ShutdownReport>? _shutdown;
    private int _markerWritten;

    public ShutdownCoordinator(BootstrapContext context, Action? stopAcceptingCommands = null,
        Func<TimeSpan, CancellationToken, Task<SchedulerShutdownReport>>? shutdownSchedulerAsync = null,
        IEnumerable<IAsyncDisposable>? services = null, AppStatePaths? appState = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _stopAcceptingCommands = stopAcceptingCommands;
        _shutdownSchedulerAsync = shutdownSchedulerAsync;
        _services = (services ?? []).ToArray();
        _appState = appState;
        _sessionId = context.SessionId;
        _sessionGeneration = context.SessionGeneration;
    }

    public StartupState State { get; private set; } = StartupState.Ready;
    public Guid SessionId => _sessionId;
    public IReadOnlyList<StartupState> StateHistory => _stateHistory.AsReadOnly();

    public Task<ShutdownReport> ShutdownAsync(TimeSpan? budget = null, CancellationToken cancellationToken = default)
    {
        var effectiveBudget = budget ?? DefaultShutdownBudget;
        if (effectiveBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }

        lock (_shutdownGate)
        {
            // One coordinator owns exactly one ordered shutdown pipeline. Later callers observe the
            // same task instead of racing a second scheduler/service/context retirement sequence.
            return _shutdown ??= ShutdownCoreAsync(effectiveBudget, cancellationToken);
        }
    }

    public async Task<(ShutdownReport Report, VaultRuntimeRetirementProof Proof)> ShutdownForVaultTransitionAsync(
        TimeSpan? budget = null,
        CancellationToken cancellationToken = default)
    {
        var report = await ShutdownAsync(budget, cancellationToken).ConfigureAwait(false);
        if (State != StartupState.Stopped
            || report.TimedOut
            || report.Failures.Count != 0
            || !report.CleanMarkerWritten)
        {
            throw new InvalidOperationException(
                "Vault transition retirement did not complete cleanly; physical Vault mutation was refused.");
        }

        // Short-lived SQLite connections are pooled for browsing performance. After all mutation
        // authorities and the VaultLock are retired, clear those process pools so Windows releases
        // catalog/derivative file handles before a root-level rename or move.
        SqliteConnection.ClearAllPools();

        return (
            report,
            new VaultRuntimeRetirementProof(_context.Paths.Root, _context.VaultId, _sessionId));
    }

    private async Task<ShutdownReport> ShutdownCoreAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        TransitionTo(StartupState.ShuttingDown);

        // command admission closes synchronously at the first shutdown transition. Internal
        // scheduler/recovery writes remain available; only new user mutation commands are refused.
        _context.Catalog.MutationAdmission.Close();

        var stopwatch = Stopwatch.StartNew();
        var failures = new List<string>();
        SchedulerShutdownReport? schedulerReport = null;
        var timedOut = false;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(budget);

        try
        {
            try
            {
                _stopAcceptingCommands?.Invoke();
            }
            catch (Exception e)
            {
                failures.Add("COMMAND_QUIESCE_FAILED:" + e.GetType().Name);
            }

            try
            {
                await _context.Catalog.MutationAdmission.WaitForIdleAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (bounded.IsCancellationRequested)
            {
                timedOut = true;
                failures.Add("COMMAND_DRAIN_TIMEOUT");
                // Safety beats latency: do not release VaultLock while an admitted command can mutate.
                await _context.Catalog.MutationAdmission.WaitForIdleAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (_shutdownSchedulerAsync is not null)
            {
                Task<SchedulerShutdownReport>? schedulerTask = null;
                try
                {
                    schedulerTask = _shutdownSchedulerAsync(Remaining(budget, stopwatch.Elapsed), bounded.Token);
                    schedulerReport = await schedulerTask.WaitAsync(bounded.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (bounded.IsCancellationRequested)
                {
                    timedOut = true;
                    failures.Add("SCHEDULER_SHUTDOWN_TIMEOUT");
                    if (schedulerTask is not null)
                    {
                        try
                        {
                            schedulerReport = await schedulerTask.ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            failures.Add("SCHEDULER_SHUTDOWN_FAILED:" + e.GetType().Name);
                        }
                    }
                }
                catch (Exception e)
                {
                    failures.Add("SCHEDULER_SHUTDOWN_FAILED:" + e.GetType().Name);
                }
            }

            foreach (var service in _services)
            {
                Task disposalTask;
                try
                {
                    disposalTask = service.DisposeAsync().AsTask();
                }
                catch (Exception e)
                {
                    failures.Add("SERVICE_DISPOSAL_FAILED:" + e.GetType().Name);
                    continue;
                }

                try
                {
                    await disposalTask.WaitAsync(bounded.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (bounded.IsCancellationRequested)
                {
                    timedOut = true;
                    failures.Add("SERVICE_DISPOSAL_TIMEOUT");
                    try
                    {
                        // a timeout stops waiting for the advertised budget, not ownership.
                        // Keep VaultLock until the mutation-capable disposal task actually terminates.
                        await disposalTask.ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        failures.Add("SERVICE_DISPOSAL_FAILED:" + e.GetType().Name);
                    }
                }
                catch (Exception e)
                {
                    failures.Add("SERVICE_DISPOSAL_FAILED:" + e.GetType().Name);
                }
            }

            if (!timedOut && failures.Count == 0)
            {
                await WriteCleanMarkerAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                failures.Add("CLEAN_MARKER_NOT_WRITTEN");
            }
        }
        finally
        {
            // VaultLock ownership is deliberately last. Every admitted command, scheduler shutdown
            // task, and mutation-capable service disposal has terminated before this point.
            await _context.DisposeAsync().ConfigureAwait(false);
            stopwatch.Stop();
            TransitionTo(StartupState.Stopped);
        }

        return new ShutdownReport(
            timedOut,
            schedulerReport?.NonterminalJobsLeftForRestart ?? 0,
            schedulerReport?.CancelledDuringShutdown ?? 0,
            schedulerReport?.ProfilingWorkerReleased ?? false,
            stopwatch.Elapsed,
            failures.AsReadOnly(),
            _sessionId,
            _markerWritten != 0);
    }

    private async Task WriteCleanMarkerAsync(CancellationToken cancellationToken)
    {
        if (_appState is null)
        {
            return;
        }

        _appState.EnsureStructuralDirectories();
        var store = new SessionMarkerStore(_appState);
        await store.WriteCleanAsync(_sessionId, _sessionGeneration, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _markerWritten, 1);
    }

    private static TimeSpan Remaining(TimeSpan budget, TimeSpan elapsed) =>
        budget - elapsed > TimeSpan.Zero ? budget - elapsed : TimeSpan.FromTicks(1);

    private void TransitionTo(StartupState state)
    {
        State = state;
        _stateHistory.Add(state);
    }
}

public sealed record ShutdownReport(
    bool TimedOut,
    int NonterminalJobsLeftForRestart,
    long CancelledDuringShutdown,
    bool ProfilingWorkerReleased,
    TimeSpan Elapsed,
    IReadOnlyList<string> Failures,
    Guid SessionId,
    bool CleanMarkerWritten)
{
    public bool PreservedUnfinishedWork => CancelledDuringShutdown == 0;
}
