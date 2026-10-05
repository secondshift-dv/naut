namespace Neuterradise.App.SystemServices.Jobs;

public static class JobPriorityPolicy
{
    public const int MinimumPriority = 0;
    public const int MaximumPriority = 100;
    public const int DefaultPriority = 50;

    // Priority tier anchors. The scheduler dispatches by effective priority (base + aging),
    // highest first. Aging raises urgency *within* the semantic tier — it never promotes a job
    // into a higher semantic class. Cross-tier progress is handled by the fairness mechanism.
    //
    // P0  Interactive   ≥95 / anchor 100   user-triggered / user-waiting work only
    // P1  CurrentImport ≥80 / anchor  90   focused or current import critical path only
    // P2  Visible       ≥60 / anchor  70   near-viewport derivatives, recently navigated surfaces
    // P3  Background    ≥40 / anchor  50   offscreen preparation, non-focused import work, default
    // Maintenance       <40  / anchor  30   cleanup, reconciliation

    /// <summary>Interactive / user-waiting — genuinely interactive operations only.</summary>
    public const int PriorityInteractive = 100;

    /// <summary>Current or focused import critical path (canonical commit / media preparation work for the focused import only).</summary>
    public const int PriorityCurrentImport = 90;

    /// <summary>Visible or near-visible derivatives (viewport, recently navigated).</summary>
    public const int PriorityVisible = 70;

    /// <summary>Background / offscreen / maintenance — the default for non-focused imports.</summary>
    public const int PriorityBackground = 50;

    /// <summary>Low urgency maintenance, cleanup, reconciliation.</summary>
    public const int PriorityMaintenance = 30;

    /// <summary>
    /// Returns the highest priority a job with the given <paramref name="basePriority"/> may reach
    /// through aging. The ceiling keeps each job inside its semantic tier so that aging can never
    /// promote a P1 job into the P0 classifier range (≥95) or a P2 job into P1 (≥80), etc.
    /// Cross-tier progress is handled by the fairness mechanism, not by aging.
    ///
    /// P0 (base ≥95) → ceiling 100, P1 (base ≥80) → 94, P2 (base ≥60) → 79,
    /// P3/background (base ≥40) → 59, maintenance (base &lt;40) → 39.
    /// </summary>
    public static int TierCeiling(int basePriority) =>
        basePriority switch
        {
            >= 95 => MaximumPriority,                                            // P0  → 100
            >= 80 => 94,                                                         // P1  → 94
            >= 60 => 79,                                                         // P2  → 79
            >= 40 => 59,                                                         // P3  → 59
            _ => 39,                                                             // maintenance → 39
        };

    public static bool IsValid(int priority) =>
        priority >= MinimumPriority && priority <= MaximumPriority;

    public static int Clamp(int priority) =>
        Math.Clamp(priority, MinimumPriority, MaximumPriority);

    public static void Validate(int priority, string parameterName)
    {
        if (!IsValid(priority))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                priority,
                $"A job priority is persisted in the range {MinimumPriority}..{MaximumPriority}.");
        }
    }
}

public sealed record JobPriorityAging(TimeSpan Interval, int Step)
{
    public static JobPriorityAging Default { get; } = new(TimeSpan.FromSeconds(30), 5);

    public bool IsEnabled => Step > 0 && Interval > TimeSpan.Zero;

    public int EffectivePriority(int basePriority, TimeSpan waited)
    {
        var clamped = JobPriorityPolicy.Clamp(basePriority);
        if (!IsEnabled || waited <= TimeSpan.Zero)
        {
            return clamped;
        }

        var ceiling = JobPriorityPolicy.TierCeiling(clamped);
        var steps = waited.Ticks / Interval.Ticks;
        var remaining = ceiling - clamped;
        var delta = steps > remaining / (long)Step
            ? remaining
            : (int)(steps * Step);
        return clamped + delta;
    }

    public long IntervalMilliseconds =>
        IsEnabled ? (long)Interval.TotalMilliseconds : 0;
}
