namespace Neuterradise.App.SystemServices.Jobs;

/// <summary>
/// Semantic priority class, derived from the durable numeric priority. Enum value increases
/// as urgency decreases: Interactive(0) is the most urgent, Maintenance(4) the least.
/// </summary>
public enum FairnessTier : byte
{
    /// <summary>P0: priority ≥ 95 — interactive, user-waiting.</summary>
    Interactive = 0,

    /// <summary>P1: priority 80..94 — current/focused import critical path.</summary>
    CurrentImport = 1,

    /// <summary>P2: priority 60..79 — visible/near-visible derivatives.</summary>
    Visible = 2,

    /// <summary>P3: priority 40..59 — background/offscreen work.</summary>
    Background = 3,

    /// <summary>Maintenance: priority &lt; 40 — cleanup, reconciliation.</summary>
    Maintenance = 4,
}

/// <summary>Result of the fairness evaluation for one dispatch decision.</summary>
public enum FairnessDispatchKind : byte
{
    /// <summary>Normal priority dispatch — select the highest effective priority candidate.</summary>
    Normal,

    /// <summary>Fairness turn — select the oldest eligible lower-tier candidate to prevent starvation.</summary>
    Fairness,
}

/// <summary>
/// Per-lane bounded burst fairness. Tracks consecutive normal (highest-priority) dispatches per
/// lane. When the burst budget is exhausted, the scheduler attempts one fairness dispatch of an
/// actual lower-tier candidate. This prevents permanent starvation without allowing lower tiers to
/// dominate or requiring observation heuristics — the scheduler queries for a real candidate.
///
/// State is in-memory only; restart rebuilds it naturally as dispatch resumes.
/// </summary>
public sealed class JobLaneFairness
{
    /// <summary>
    /// Default burst limit for non-interactive tiers (P1/P2/P3/maintenance).
    /// After this many consecutive dispatches of the same or higher tier, one fairness turn.
    /// </summary>
    public const int DefaultBurstLimit = 4;

    /// <summary>
    /// Burst limit for P0 (Interactive). Larger than the default so P0 remains overwhelmingly
    /// dominant, but not infinite — continuous P0 cannot permanently starve all lower work.
    /// </summary>
    public const int InteractiveBurstLimit = 8;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<JobLane, LaneFairnessState> _states = new();

    /// <summary>
    /// Classifies a numeric priority into a semantic fairness tier.
    /// </summary>
    public static FairnessTier ClassifyTier(int priority) => priority switch
    {
        >= 95 => FairnessTier.Interactive,
        >= 80 => FairnessTier.CurrentImport,
        >= 60 => FairnessTier.Visible,
        >= 40 => FairnessTier.Background,
        _ => FairnessTier.Maintenance,
    };

    /// <summary>
    /// Evaluates whether the next dispatch should be a normal priority dispatch or a fairness
    /// turn. The caller decides based on the returned kind; for fairness turns, queries for an
    /// actual lower-tier candidate via <c>GetOldestLowerTierCandidateAsync</c>.
    /// </summary>
    public (FairnessDispatchKind Kind, FairnessTier BelowTier) Evaluate(JobLane lane)
    {
        var state = GetOrCreate(lane);
        var burstLimit = state.LastDispatchedTier == FairnessTier.Interactive
            ? InteractiveBurstLimit
            : DefaultBurstLimit;

        if (state.ConsecutiveNormal < burstLimit)
        {
            return (FairnessDispatchKind.Normal, state.LastDispatchedTier);
        }

        // Burst budget exhausted. Signal a fairness turn — the caller will query for an actual
        // candidate. If no candidate exists (all blocked by dependencies/inflight), the caller
        // dispatches normally and the counter stays at the limit, re-checking next iteration.
        return (FairnessDispatchKind.Fairness, state.LastDispatchedTier);
    }

    /// <summary>
    /// Records a normal (highest-priority) dispatch. Increments the consecutive counter.
    /// <paramref name="actualTier"/> must be the real semantic tier of the dispatched job,
    /// derived from its persisted base priority — not inferred from lane.
    /// </summary>
    public void RecordNormalDispatch(JobLane lane, FairnessTier actualTier)
    {
        var state = GetOrCreate(lane);
        state.LastDispatchedTier = actualTier;
        state.ConsecutiveNormal++;
    }

    /// <summary>
    /// Records a fairness dispatch. Resets the consecutive counter so normal priority dispatch
    /// resumes for the next burst cycle.
    /// </summary>
    public void RecordFairnessDispatch(JobLane lane, FairnessTier dispatchedTier)
    {
        var state = GetOrCreate(lane);
        state.LastDispatchedTier = dispatchedTier;
        state.ConsecutiveNormal = 0;
    }

    private LaneFairnessState GetOrCreate(JobLane lane) =>
        _states.GetOrAdd(lane, static _ => new LaneFairnessState());

    private sealed class LaneFairnessState
    {
        public int ConsecutiveNormal;
        public FairnessTier LastDispatchedTier = FairnessTier.Background;
    }
}
