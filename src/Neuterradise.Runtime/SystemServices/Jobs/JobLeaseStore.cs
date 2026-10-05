using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Jobs;

public sealed class JobLeaseStore
{
    private readonly JobWrites _writes;
    private readonly SchedulerReads _reads;
    private readonly IClock _clock;

    public JobLeaseStore(CatalogDb catalog, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _writes = new JobWrites(catalog);
        _reads = new SchedulerReads(catalog);
        _clock = clock ?? new SystemClock();
    }

    public async Task<JobLease?> TryClaimAsync(JobRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.State != JobState.Runnable)
        {
            return null;
        }

        var claimed = await _writes.TryClaimAsync(record.JobId, record.RowVersion, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (!claimed)
        {
            return null;
        }

        var fresh = await _reads.GetJobAsync(record.JobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Claimed job {record.JobId:D} is absent from the catalog.");
        return new JobLease(fresh, this);
    }

    internal Task<bool> UpdateProgressAsync(
        JobLease lease,
        long completed,
        long total,
        string? stage,
        CancellationToken cancellationToken) =>
        _writes.UpdateRunningProgressAsync(
            lease.Record.JobId,
            lease.Record.RowVersion,
            completed,
            total,
            stage,
            checkpointJson: null,
            cancellationToken);

    internal Task<bool> UpdateCheckpointAsync(
        JobLease lease,
        string checkpointJson,
        CancellationToken cancellationToken) =>
        _writes.UpdateRunningProgressAsync(
            lease.Record.JobId,
            lease.Record.RowVersion,
            completed: null,
            total: null,
            stage: null,
            checkpointJson,
            cancellationToken);

    internal Task<bool> InterruptForShutdownAsync(
        JobLease lease,
        CancellationToken cancellationToken) =>
        _writes.TryInterruptForShutdownAsync(
            lease.Record.JobId,
            lease.Record.RowVersion,
            cancellationToken);

    internal async Task<bool> CompleteAsync(
        JobLease lease,
        JobExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (result.IsSucceeded)
        {
            return await _writes.TryCompleteAsync(
                lease.Record.JobId,
                lease.Record.RowVersion,
                JobState.Succeeded,
                errorCode: null,
                errorDetailSafe: null,
                progressCompleted: null,
                progressTotal: null,
                stage: null,
                checkpointJson: null,
                _clock.UtcNow,
                cancellationToken).ConfigureAwait(false);
        }

        var decision = JobRetryPolicy.Evaluate(
            result,
            lease.Record.Attempt,
            lease.Record.MaxAttempts,
            _clock.UtcNow);
        lease.RetryDecision = decision;

        // Pause is a control action, not a failure: do not persist error codes or consume retry
        // budget. The durable job becomes PAUSED and preserves its checkpoint/progress.
        var isControl = decision.Outcome is JobRetryOutcome.Paused or JobRetryOutcome.Yielded;

        return await _writes.TryCompleteAsync(
            lease.Record.JobId,
            lease.Record.RowVersion,
            decision.TerminalState,
            isControl ? null : result.ErrorCode,
            isControl ? null : result.ErrorDetailSafe ?? decision.Reason,
            progressCompleted: null,
            progressTotal: null,
            stage: null,
            checkpointJson: null,
            _clock.UtcNow,
            cancellationToken,
            isControl ? null : decision.NotBeforeMs).ConfigureAwait(false);
    }
}

public sealed class JobLease : IJobProgressReporter, IJobCheckpointStore
{
    private readonly JobRecord _record;
    private readonly JobLeaseStore _store;
    private int _completed;

    internal JobLease(JobRecord record, JobLeaseStore store)
    {
        _record = record;
        _store = store;
    }

    public JobRecord Record => _record;

    public JobRetryDecision? RetryDecision { get; internal set; }

    public Task<bool> CompleteAsync(JobExecutionResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            throw new InvalidOperationException("A job lease can complete only once.");
        }

        return _store.CompleteAsync(this, result, cancellationToken);
    }

    internal Task<bool> InterruptForShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            throw new InvalidOperationException("A job lease can complete only once.");
        }

        return _store.InterruptForShutdownAsync(this, cancellationToken);
    }

    public Task ReportProgressAsync(long completed, long total, string? stage, CancellationToken cancellationToken = default)
        => _store.UpdateProgressAsync(this, completed, total, stage, cancellationToken);

    public Task UpdateCheckpointAsync(string checkpointJson, CancellationToken cancellationToken = default)
        => _store.UpdateCheckpointAsync(this, checkpointJson, cancellationToken);
}
