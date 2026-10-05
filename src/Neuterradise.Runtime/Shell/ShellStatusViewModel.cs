using Neuterradise.App.Import;
using Neuterradise.App.Localization;
using Neuterradise.App.SystemServices.MediaTools;
using Neuterradise.App.SystemServices.Lifecycle;

namespace Neuterradise.App.Shell;

/// <summary>
/// The shell's view of background work, expressed the way the shell contract requires: by human intent, not
/// by job state. <see cref="RunningCount"/> and friends remain available for diagnostics, but the
/// shell chrome renders <see cref="Headline"/> only.
/// </summary>
public sealed record ShellWorkSnapshot(
    int RunningCount = 0,
    int PendingVerificationCount = 0,
    int AttentionCount = 0,
    double? Progress = null,
    TimeSpan? Eta = null,
    bool IsPaused = false)
{

    public static ShellWorkSnapshot Idle { get; } = new();
}

public sealed class ShellStatusViewModel : ObservableObject
{
    private ShellWorkSnapshot _snapshot = ShellWorkSnapshot.Idle;
    private ImportActivitySnapshot _imports = ImportActivitySnapshot.Empty;
    private IReadOnlyList<RuntimeCapabilityStatus> _capabilityLimitations = [];
    private ProfilingInitializationState _profilingState = ProfilingInitializationState.Disabled;
    private string? _profilingReason;

    public int RunningCount => _snapshot.RunningCount;

    public int PendingVerificationCount => _snapshot.PendingVerificationCount;

    public int AttentionCount => _snapshot.AttentionCount;

    public double? Progress => _imports.OverallProgress ?? _snapshot.Progress;

    public TimeSpan? Eta => _snapshot.Eta;

    public bool IsPaused => _snapshot.IsPaused;

    /// <summary>
    /// True only while work the user actually started is moving. Derived background maintenance is
    /// deliberately excluded: the shell contract keeps housekeeping out of the primary surface.
    /// </summary>
    public bool HasRunningWork => _imports.ActiveCount > 0;

    public bool HasPendingVerification => _imports.WaitingForChoicesCount > 0;

    public bool HasAttention => _imports.AttentionCount > 0;

    public bool ShowAttentionDot => HasAttention && !HasRunningWork;

    /// <summary>
    /// The one sentence shown in the shell. Empty means there is nothing worth saying, and the whole
    /// status surface collapses rather than showing an idle counter.
    /// </summary>
    public string Headline => _imports.Headline;

    public string HeadlineDetail => _imports.HeadlineDetail;

    public bool HasVisibleStatus => !string.IsNullOrEmpty(Headline);

    public bool IsProgressIndeterminate => HasRunningWork && Progress is null;

    /// <summary>
    /// Capabilities this deployment cannot provide, each with its concrete reason. These are
    /// feature-scoped limitations, not startup failures, so the shell stays usable while showing them.
    /// </summary>
    public IReadOnlyList<RuntimeCapabilityStatus> CapabilityLimitations => _capabilityLimitations;

    public bool HasCapabilityLimitations => _capabilityLimitations.Count > 0;

    public ProfilingInitializationState ProfilingState => _profilingState;
    public string? ProfilingReason => _profilingReason;
    public bool IsProfilingAvailable => _profilingState == ProfilingInitializationState.Available;

    public void ApplyProfiling(ProfilingInitializationState state, string? reason)
    {
        _profilingState = state; _profilingReason = reason;
        RaisePropertyChanged(nameof(ProfilingState)); RaisePropertyChanged(nameof(ProfilingReason)); RaisePropertyChanged(nameof(IsProfilingAvailable));
    }

    /// <summary>
    /// True when a deployed artifact failed its integrity check. This is reported separately from a
    /// merely missing artifact because it must never be treated as a normal degraded state.
    /// </summary>
    public bool HasCapabilityIntegrityFailure =>
        _capabilityLimitations.Any(status => status.State == RuntimeCapabilityState.IntegrityFailed);

    /// <summary>
    /// Publishes the session capability facts once the runtime has probed them.
    /// </summary>
    public void ApplyCapabilities(RuntimeCapabilitySnapshot capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        _capabilityLimitations = capabilities.Limitations;
        RaisePropertyChanged(nameof(CapabilityLimitations));
        RaisePropertyChanged(nameof(HasCapabilityLimitations));
        RaisePropertyChanged(nameof(HasCapabilityIntegrityFailure));
    }

    /// <summary>
    /// Applies the live import picture. This is what the chrome actually renders; the scheduler
    /// snapshot below only contributes a progress fraction when no import is measurable.
    /// </summary>
    public void ApplyImports(ImportActivitySnapshot imports)
    {
        ArgumentNullException.ThrowIfNull(imports);

        _imports = imports;
        RaiseDerived();
    }

    public void Apply(ShellWorkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshot.RunningCount);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshot.PendingVerificationCount);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshot.AttentionCount);

        if (snapshot.Progress is { } progress
            && (double.IsNaN(progress) || progress < 0 || progress > 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(snapshot),
                progress,
                "Shell progress must be a fraction in the inclusive range 0..1, or null when indeterminate.");
        }

        if (snapshot == _snapshot)
        {
            return;
        }

        _snapshot = snapshot;

        RaisePropertyChanged(nameof(RunningCount));
        RaisePropertyChanged(nameof(PendingVerificationCount));
        RaisePropertyChanged(nameof(AttentionCount));
        RaisePropertyChanged(nameof(Eta));
        RaisePropertyChanged(nameof(IsPaused));
        RaiseDerived();
    }

    private void RaiseDerived()
    {
        RaisePropertyChanged(nameof(Progress));
        RaisePropertyChanged(nameof(HasRunningWork));
        RaisePropertyChanged(nameof(HasPendingVerification));
        RaisePropertyChanged(nameof(HasAttention));
        RaisePropertyChanged(nameof(ShowAttentionDot));
        RaisePropertyChanged(nameof(Headline));
        RaisePropertyChanged(nameof(HeadlineDetail));
        RaisePropertyChanged(nameof(HasVisibleStatus));
        RaisePropertyChanged(nameof(IsProgressIndeterminate));
    }

    public void Clear()
    {
        Apply(ShellWorkSnapshot.Idle);
        ApplyImports(ImportActivitySnapshot.Empty);
    }
}
