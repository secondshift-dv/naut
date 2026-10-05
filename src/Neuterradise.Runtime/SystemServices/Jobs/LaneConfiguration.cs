namespace Neuterradise.App.SystemServices.Jobs;

public enum JobLane
{
    Fast,
    Io,
    Cpu,
    Media,
    Face
}

public enum JobState
{
    Pending,
    Runnable,
    Running,
    Paused,
    Succeeded,
    FailedRetryable,
    FailedTerminal,
    Cancelled
}

public sealed record LaneConfiguration(
    JobLane Lane,
    int MaxConcurrency);

public static class LaneDefaults
{
    public static int InitialConcurrency(JobLane lane) => lane switch
    {
        // Per-lane limits prevent one resource class from monopolizing scheduler capacity.
        // The global execution ceiling (JobDispatcher.DefaultGlobalMaxConcurrency) is set to the
        // sum of per-lane caps so that different resource classes can proceed independently.
        // Actual throttling during interaction is handled by the ResourceGovernor, not by the
        // global semaphore.
        JobLane.Fast => 3,
        JobLane.Io => 1,
        JobLane.Cpu => 2,
        JobLane.Media => 1,
        JobLane.Face => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, null),
    };

    public static IReadOnlyList<LaneConfiguration> CreateDefault() =>
        Enum.GetValues<JobLane>()
            .Select(lane => new LaneConfiguration(lane, InitialConcurrency(lane)))
            .ToArray();
}

public static class LaneQueueBounds
{
    /// <summary>
    /// Keep only one prefetched candidate per lane. Durable RUNNABLE/PENDING work stays in SQLite
    /// until capacity is close enough to use it, which keeps priority changes responsive and prevents
    /// normal work from becoming a stale in-memory FIFO ahead of a newly focused import.
    /// </summary>
    public static int WindowSize(LaneConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return 1;
    }
}
