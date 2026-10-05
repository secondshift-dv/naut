namespace Neuterradise.App.SystemServices.Jobs;

public sealed record JobRecord(
    Guid JobId,
    string Kind,
    JobLane Lane,
    JobState State,
    int Priority,
    string OwnerType,
    Guid OwnerId,
    int Attempt,
    int MaxAttempts,
    long? NotBeforeMs,
    long ProgressCompleted,
    long ProgressTotal,
    string? Stage,
    string? CheckpointJson,
    string? ErrorCode,
    string? ErrorDetailSafe,
    long CreatedAtMs,
    long? StartedAtMs,
    long? CompletedAtMs,
    long RowVersion);

public sealed record JobProgressSnapshot(
    Guid JobId,
    long Completed,
    long Total,
    string? Stage,
    JobState State);

public sealed record SchedulerUnitSummary(
    string OwnerType,
    Guid OwnerId,
    int Pending,
    int Running,
    int Succeeded,
    int FailedRetryable,
    int FailedTerminal,
    int Cancelled,
    long ProgressCompleted,
    long ProgressTotal,
    string? Stage)
{
    public int Total => Pending + Running + Succeeded + FailedRetryable + FailedTerminal + Cancelled;

    public double? PercentComplete =>
        ProgressTotal > 0 ? ProgressCompleted / (double)ProgressTotal : null;
}

public sealed record LaneSnapshot(
    JobLane Lane,
    int MaxConcurrency,
    int Running,
    int Queued,
    int Pending,
    int Succeeded,
    int FailedRetryable,
    int FailedTerminal,
    int Cancelled,
    int Runnable = 0,
    long? OldestRunnableAgeMs = null)
{

    public bool IsAtConcurrencyBound => Running >= MaxConcurrency;
}

public sealed record ProfilingWorkerMetrics(
    ProfilingWorkerHostState State,
    int? ProcessId,
    int RestartCount,
    int ConsecutiveFailures,
    long? WorkingSetBytes,
    int? ProtocolVersion)
{

    public bool IsRunning => ProcessId.HasValue && State is ProfilingWorkerHostState.Ready or ProfilingWorkerHostState.Starting;

    public static ProfilingWorkerMetrics NotStarted { get; } =
        new(ProfilingWorkerHostState.Stopped, null, 0, 0, null, null);
}

public sealed record SchedulerMetricsSnapshot(
    long CreatedAtMs,
    IReadOnlyDictionary<JobLane, LaneSnapshot> Lanes,
    long RetryScheduledCount,
    long FailedJobCount,
    long CancelledJobCount,
    long CompletionConflictCount,
    double ProgressPublishesPerSecond,
    double ProgressPublishRateLimitPerSecond,
    ProfilingWorkerMetrics Worker)
{

    public int MediaProcessCount => Lanes.TryGetValue(JobLane.Media, out var lane) ? lane.Running : 0;

    public int FaceProcessCount => Lanes.TryGetValue(JobLane.Face, out var lane) ? lane.Running : 0;

    public int TotalQueued => Lanes.Values.Sum(lane => lane.Queued);

    public long? OldestRunnableAgeMs =>
        Lanes.Values.Select(lane => lane.OldestRunnableAgeMs).Where(age => age.HasValue).DefaultIfEmpty(null).Max();

    public bool EveryLaneWithinConcurrencyBound =>
        Lanes.Values.All(lane => lane.Running <= lane.MaxConcurrency);
}

public sealed record SchedulerShutdownReport(
    bool DrainedWithinGracePeriod,
    int NonterminalJobsLeftForRestart,
    long CancelledDuringShutdown,
    long ProgressSnapshotsFlushed,
    bool ProfilingWorkerReleased,
    long ElapsedMs)
{

    public bool PreservedUnfinishedWork => CancelledDuringShutdown == 0;
}

public sealed record SchedulerSnapshot(
    long CreatedAtMs,
    IReadOnlyDictionary<JobLane, LaneSnapshot> Lanes,
    int TotalRunning,
    int TotalQueued,
    int TotalSucceeded,
    int TotalFailedTerminal);
