namespace Neuterradise.App.SystemServices.Jobs;

/// <summary>
/// In-memory wake hints for the durable scheduler. Anything that makes work dispatchable — a new job,
/// a new dependency edge, resume, prioritize, cancel, skip, retry eligibility — raises a signal after
/// its database commit. The database stays the only truth; a missed signal costs at most one
/// conservative recovery poll, never stranded work. Nothing here is persisted.
/// </summary>
public static class JobSignals
{
    private static long _raised;

    public static event Action? Raised;

    /// <summary>
    /// Post-commit notification for visible work/status projections. Unlike <see cref="Raised"/>,
    /// this event does not imply that scheduler dispatchability changed.
    /// </summary>
    public static event Action? StatusChanged;

    public static long RaisedCount => Interlocked.Read(ref _raised);

    public static void Raise()
    {
        Interlocked.Increment(ref _raised);
        Raised?.Invoke();
        StatusChanged?.Invoke();
    }

    public static void RaiseStatusChanged() => StatusChanged?.Invoke();

    /// <summary>Raises once after <paramref name="delay"/>, e.g. when a retry back-off expires.</summary>
    public static void RaiseAfter(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            Raise();
            return;
        }

        _ = Task.Delay(delay).ContinueWith(static _ => Raise(), TaskScheduler.Default);
    }
}
