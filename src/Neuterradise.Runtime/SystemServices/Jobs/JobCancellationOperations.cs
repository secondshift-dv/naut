using System.Collections.Concurrent;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Jobs;

public enum JobCancellationOutcome
{

    NotFound,

    AlreadyTerminal,

    Cancelled,

    RequestedForRunningJob,
}

public sealed class JobCancellationOperations
{
    private readonly JobWrites _writes;
    private readonly SchedulerReads _reads;
    private readonly SettingsReads _settingsReads;
    private readonly SettingsWrites _settingsWrites;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<Guid, byte> _requested = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<Guid, JobControlIntent> _intents = new();

    public JobCancellationOperations(CatalogDb catalog, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _writes = new JobWrites(catalog);
        _reads = new SchedulerReads(catalog);
        _settingsReads = new SettingsReads(catalog);
        _settingsWrites = new SettingsWrites(catalog);
        _clock = clock ?? new SystemClock();
    }

    public IReadOnlyCollection<Guid> PendingCancellations => _requested.Keys.ToArray();

    public Func<Guid, JobExecutionResult, Task>? OnJobCancelled { get; set; }

    public bool IsCancellationRequested(Guid jobId) => _requested.ContainsKey(jobId);

    public async Task<bool> IsGlobalPausedAsync(CancellationToken cancellationToken = default)
    {
        var value = await _settingsReads.GetSettingValueAsync("scheduler.pause", cancellationToken).ConfigureAwait(false);
        return string.Equals(value?.Trim(), "\"true\"", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    public Task PauseGlobalAsync(CancellationToken cancellationToken = default) =>
        _settingsWrites.SetSettingAsync("scheduler.pause", "true", cancellationToken);

    public async Task ResumeGlobalAsync(CancellationToken cancellationToken = default)
    {
        await _settingsWrites.SetSettingAsync("scheduler.pause", "false", cancellationToken).ConfigureAwait(false);
        JobSignals.Raise();
    }

    private static async Task<T> SignalAfter<T>(Task<T> write)
    {
        var result = await write.ConfigureAwait(false);
        JobSignals.Raise();
        return result;
    }

    public async Task<bool> PauseJobAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        await _writes.PauseAsync(jobId, null, null, cancellationToken).ConfigureAwait(false) == 1;

    public Task<int> PauseOwnerAsync(
        string ownerType,
        Guid ownerId,
        CancellationToken cancellationToken = default) =>
        _writes.PauseAsync(null, ownerType, ownerId, cancellationToken);

    public async Task<bool> ResumeJobAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        await SignalAfter(_writes.ResumeAsync(jobId, null, null, cancellationToken)).ConfigureAwait(false) == 1;

    public Task<int> ResumeOwnerAsync(
        string ownerType,
        Guid ownerId,
        CancellationToken cancellationToken = default) =>
        SignalAfter(_writes.ResumeAsync(null, ownerType, ownerId, cancellationToken));

    public async Task<bool> IsPausedAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var record = await _reads.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        return record?.State == JobState.Paused;
    }

    public Task<bool> PrioritizeJobAsync(
        Guid jobId,
        int priority = JobPriorityPolicy.MaximumPriority,
        bool boostDependencies = true,
        CancellationToken cancellationToken = default) =>
        SignalAfter(_writes.PrioritizeJobAsync(jobId, priority, boostDependencies, cancellationToken));

    public Task<int> PrioritizeOwnerAsync(
        string ownerType,
        Guid ownerId,
        int priority = JobPriorityPolicy.MaximumPriority,
        CancellationToken cancellationToken = default) =>
        SignalAfter(_writes.PrioritizeOwnerAsync(ownerType, ownerId, priority, cancellationToken));

    public Task<bool> SkipFailureAsync(
        Guid jobId,
        bool unblockDependents = false,
        CancellationToken cancellationToken = default) =>
        SignalAfter(_writes.SkipJobFailureAsync(jobId, _clock.UtcNow, unblockDependents, cancellationToken));

    public Task<int> ClearFinishedOrCancelledAsync(
        string? ownerType = null,
        Guid? ownerId = null,
        CancellationToken cancellationToken = default) =>
        _writes.ClearFinishedHistoryAsync(ownerType, ownerId, cancellationToken);

    public async Task<JobCancellationOutcome> RequestCancelAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(jobId);

        var record = await _reads.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return JobCancellationOutcome.NotFound;
        }

        if (IsTerminal(record.State))
        {
            return JobCancellationOutcome.AlreadyTerminal;
        }

        RecordIntent(jobId);

        var cancelled = await _writes
            .CancelIdleAsync(jobId, null, null, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (cancelled == 1)
        {
            _requested.TryRemove(jobId, out _);
            await NotifyCancelledAsync(jobId).ConfigureAwait(false);
            return JobCancellationOutcome.Cancelled;
        }

        var current = await _reads.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            _requested.TryRemove(jobId, out _);
            return JobCancellationOutcome.NotFound;
        }

        if (IsTerminal(current.State))
        {
            _requested.TryRemove(jobId, out _);
            return current.State == JobState.Cancelled
                ? JobCancellationOutcome.Cancelled
                : JobCancellationOutcome.AlreadyTerminal;
        }

        return JobCancellationOutcome.RequestedForRunningJob;
    }

    public async Task<int> RequestCancelOwnerAsync(
        string ownerType,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerType);
        EnsureNonEmpty(ownerId);

        var scoped = await _reads
            .GetNonTerminalJobIdsForOwnerAsync(ownerType, ownerId, cancellationToken)
            .ConfigureAwait(false);
        foreach (var jobId in scoped)
        {
            RecordIntent(jobId);
        }

        var cancelled = await _writes
            .CancelIdleAsync(null, ownerType, ownerId, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        foreach (var jobId in scoped)
        {
            var record = await _reads.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (record is null || IsTerminal(record.State))
            {
                _requested.TryRemove(jobId, out _);
            }

            if (record?.State == JobState.Cancelled)
            {
                await NotifyCancelledAsync(jobId).ConfigureAwait(false);
            }
        }

        return cancelled;
    }

    internal JobCancellationRegistration Register(Guid jobId, CancellationToken schedulerToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(schedulerToken);
        _running[jobId] = source;

        if (_requested.ContainsKey(jobId))
        {
            Cancel(source);
        }

        return new JobCancellationRegistration(this, jobId, source);
    }

    internal bool RequestYieldRunning(Guid jobId)
    {
        if (!_running.TryGetValue(jobId, out var source) || !_intents.TryAdd(jobId, JobControlIntent.Yield))
            return false;
        Cancel(source);
        return true;
    }

    /// <summary>
    /// Sets the control intent for a set of jobs. Used by ImportUnitControlAuthority to signal
    /// whether running jobs are being paused, cancelled, or shut down.
    /// </summary>
    internal void SetIntent(Guid jobId, JobControlIntent intent)
    {
        _intents[jobId] = intent;
    }

    /// <summary>
    /// Returns the control intent for a job and removes it. Returns None if no intent was set.
    /// </summary>
    internal JobControlIntent PeekIntent(Guid jobId) =>
        _intents.TryGetValue(jobId, out var intent) ? intent : JobControlIntent.None;

    internal JobControlIntent ConsumeIntent(Guid jobId)
    {
        _intents.TryRemove(jobId, out var intent);
        return intent;
    }

    internal void Finalize(Guid jobId)
    {
        _requested.TryRemove(jobId, out _);
        _intents.TryRemove(jobId, out _);
    }

    internal void Release(Guid jobId)
    {
        _intents.TryRemove(jobId, out _);
        if (_running.TryRemove(jobId, out var source))
        {
            source.Dispose();
        }
    }

    private async Task NotifyCancelledAsync(Guid jobId)
    {
        var observer = OnJobCancelled;
        if (observer is null)
        {
            return;
        }

        try
        {
            await observer(
                jobId,
                JobExecutionResult.Cancelled("The idle job was cancelled before execution.")).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Projection remains recoverable through scheduler reconciliation.
        }
    }

    private void RecordIntent(Guid jobId)
    {
        _requested[jobId] = 0;
        if (_running.TryGetValue(jobId, out var source))
        {
            Cancel(source);
        }
    }

    private static void Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {

        }
    }

    private static bool IsTerminal(JobState state) =>
        state is JobState.Succeeded
            or JobState.FailedTerminal
            or JobState.Cancelled;

    private static void EnsureNonEmpty(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", nameof(value));
        }
    }
}

internal sealed class JobCancellationRegistration : IDisposable
{
    private readonly JobCancellationOperations _service;
    private readonly Guid _jobId;
    private readonly CancellationTokenSource _source;

    internal JobCancellationRegistration(
        JobCancellationOperations service,
        Guid jobId,
        CancellationTokenSource source)
    {
        _service = service;
        _jobId = jobId;
        _source = source;
    }

    public CancellationToken Token => _source.Token;

    public bool IsCancellationRequested => _source.IsCancellationRequested;

    public void Dispose() => _service.Release(_jobId);
}
