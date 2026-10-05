namespace Neuterradise.App.SystemServices.Jobs;

/// <summary>
/// Transient in-memory intent for why a running job's cancellation token was triggered. Durable
/// ImportUnit lifecycle is the real authority; this only coordinates the current process so the
/// scheduler's completion mapping can distinguish Pause from Cancel from Shutdown.
/// Not persisted — restart rebuilds intent from durable state.
/// </summary>
public enum JobControlIntent
{
    /// <summary>No active control intent. Ordinary handler cancellation/failure policy applies.</summary>
    None = 0,

    /// <summary>Import was paused. Job should become PAUSED, not failed/cancelled.</summary>
    Pause = 1,

    /// <summary>Import was cancelled. Job should become CANCELLED.</summary>
    Cancel = 2,

    /// <summary>Application is shutting down. Leave job resumable on next start.</summary>
    Shutdown = 3,

    /// <summary>A focused import needs the IO lane; return this transfer to pending work.</summary>
    Yield = 4,
}

public enum JobFailureClassification
{

    TransientIo,

    FileLocked,

    AccessTemporarilyDenied,

    SqliteBusyAfterRollback,

    WorkerDisconnected,

    ToolLaunchTransient,

    SourceDeleteBlocked,

    DeterministicInvalidInput,

    ContentMismatch,

    MissingRequiredToolOrModel,

    ProtocolIncompatible,

    AmbiguousPhysicalState,

    Cancelled,

    /// <summary>
    /// The handler stopped because the owning import was paused. Not a failure — the job becomes
    /// PAUSED so it can resume on Start without consuming retry budget.
    /// </summary>
    Paused,

    Yielded,
}

public enum JobRetryOutcome
{

    Retry,

    Terminal,

    Cancelled,

    /// <summary>
    /// The job was paused because the owning import was paused. The durable job becomes PAUSED,
    /// not CANCELLED and not failed. Checkpoint and attempt count are preserved.
    /// </summary>
    Paused,

    Yielded,
}

public sealed record JobRetryDecision(
    JobRetryOutcome Outcome,
    JobState TerminalState,
    TimeSpan? Backoff,
    long? NotBeforeMs,
    string Reason);

public static class JobRetryPolicy
{

    public const int DefaultMaxAttempts = 5;

    public static IReadOnlyList<TimeSpan> BackoffSchedule { get; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
    ];

    public static bool IsRetryable(JobFailureClassification classification) => classification switch
    {
        JobFailureClassification.TransientIo
            or JobFailureClassification.FileLocked
            or JobFailureClassification.AccessTemporarilyDenied
            or JobFailureClassification.SqliteBusyAfterRollback
            or JobFailureClassification.WorkerDisconnected
            or JobFailureClassification.ToolLaunchTransient
            or JobFailureClassification.SourceDeleteBlocked => true,
        _ => false,
    };

    public static TimeSpan GetBackoff(int attempt)
    {
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attempt),
                attempt,
                "A backoff delay follows an attempt, so the attempt number starts at one.");
        }

        var index = Math.Min(attempt, BackoffSchedule.Count) - 1;
        return BackoffSchedule[index];
    }

    public static JobRetryDecision Evaluate(
        JobFailureClassification classification,
        int attempt,
        int maxAttempts,
        DateTimeOffset utcNow)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts),
                maxAttempts,
                "A durable job must allow at least one attempt.");
        }

        if (classification == JobFailureClassification.Cancelled)
        {
            return new JobRetryDecision(
                JobRetryOutcome.Cancelled,
                JobState.Cancelled,
                null,
                null,
                "The handler reached a safe state after cancellation was requested.");
        }

        if (classification == JobFailureClassification.Paused)
        {
            return new JobRetryDecision(
                JobRetryOutcome.Paused,
                JobState.Paused,
                null,
                null,
                "The handler stopped at a safe boundary because the import was paused.");
        }

        if (classification == JobFailureClassification.Yielded)
        {
            return new JobRetryDecision(JobRetryOutcome.Yielded, JobState.Pending,
                null, null, "The transfer yielded at a durable safe checkpoint.");
        }

        if (!IsRetryable(classification))
        {
            return new JobRetryDecision(
                JobRetryOutcome.Terminal,
                JobState.FailedTerminal,
                null,
                null,
                $"{classification} needs a new decision; repeating the same work cannot change it.");
        }

        if (attempt >= maxAttempts)
        {
            return new JobRetryDecision(
                JobRetryOutcome.Terminal,
                JobState.FailedTerminal,
                null,
                null,
                $"Attempt {attempt} of {maxAttempts} exhausted the retry budget for {classification}.");
        }

        var backoff = GetBackoff(attempt);
        return new JobRetryDecision(
            JobRetryOutcome.Retry,
            JobState.FailedRetryable,
            backoff,
            utcNow.Add(backoff).ToUnixTimeMilliseconds(),
            $"{classification} is transient; attempt {attempt + 1} of {maxAttempts} follows in {backoff}.");
    }

    public static JobRetryDecision Evaluate(
        JobExecutionResult result,
        int attempt,
        int maxAttempts,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsSucceeded)
        {
            throw new ArgumentException(
                "A succeeded result has no retry decision to make.",
                nameof(result));
        }

        if (result.Classification is { } classification)
        {
            return Evaluate(classification, attempt, maxAttempts, utcNow);
        }

        return Evaluate(
            result.FailureKind == JobFailureKind.Retryable
                ? JobFailureClassification.TransientIo
                : JobFailureClassification.DeterministicInvalidInput,
            attempt,
            maxAttempts,
            utcNow);
    }
}
