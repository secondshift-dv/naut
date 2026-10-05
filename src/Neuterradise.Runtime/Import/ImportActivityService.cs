using System.Diagnostics;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Localization;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.Import;

/// <summary>
/// Human-facing stage of one import, in the order a person experiences it. The durable lifecycle in
/// <see cref="ImportUnitState"/> is richer; a normal user only needs to know what is happening and
/// whether it wants them.
/// </summary>
public enum ImportActivityStage
{
    Reading,
    WaitingForProfile,
    Checking,
    ReadyToVerify,
    Saving,
    Done,
    Paused,
    NeedsAttention,
    Cancelled,
}

/// <summary>
/// One import as a person sees it: a title, a stage, a truthful progress bar and the handful of
/// actions that apply right now. Normal progress and recovery copy stay human-readable; technical
/// capability, job and error provenance remains available through diagnostics instead of ordinary UI.
/// </summary>
public sealed record ImportActivityItem(
    Guid UnitId,
    string SourceDisplayName,
    ImportActivityStage Stage,
    string StageText,
    string DetailText,
    double? Progress,
    bool IsIndeterminate,
    bool IsActive,
    bool IsTerminal,
    bool NeedsAttention,
    bool CanContinue,
    bool CanRetry,
    bool CanCancel,
    bool CanClearHistory,
    bool CanPause,
    bool CanPrioritize,
    bool CanStart,
    bool ShowTransportControls,
    bool IsPriority,
    string TransportTooltip,
    bool CanOpenProfile,
    Guid? DestinationProfileId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? ProgressText = null)
{
    public bool IsComplete => Stage == ImportActivityStage.Done;

    public bool HasProgressBar => Progress is not null;
}

/// <summary>
/// The whole import picture in one immutable value: what is running, what wants attention, and the
/// single sentence the shell shows for background work.
/// </summary>
public sealed record ImportActivitySnapshot(IReadOnlyList<ImportActivityItem> Items)
{
    public static ImportActivitySnapshot Empty { get; } = new([]);

    public int ActiveCount => Items.Count(item => item.IsActive);

    /// <summary>Imports that are waiting for the user to finish the two short choices.</summary>
    public int WaitingForChoicesCount => Items.Count(item => item.CanContinue);

    public int AttentionCount => Items.Count(item => item.NeedsAttention);

    public bool HasActivity => ActiveCount > 0 || WaitingForChoicesCount > 0 || AttentionCount > 0;

    /// <summary>Mean progress across everything currently running, or null when nothing measurable is.</summary>
    public double? OverallProgress
    {
        get
        {
            var measured = Items.Where(item => item.IsActive && item.Progress is not null).ToList();
            return measured.Count == 0 ? null : measured.Average(item => item.Progress!.Value);
        }
    }

    public string Headline
    {
        get
        {
            if (AttentionCount > 0)
            {
                return AttentionCount == 1
                    ? SurfaceText.Get("Import.Headline.OneAttention", "1 import needs attention")
                    : SurfaceText.Format("Import.Headline.ManyAttention", "{0} imports need attention", AttentionCount);
            }

            if (ActiveCount > 0)
            {
                return ActiveCount == 1
                    ? SurfaceText.Get("Import.Headline.OneActive", "Importing 1 item")
                    : SurfaceText.Format("Import.Headline.ManyActive", "Importing {0} items", ActiveCount);
            }

            if (WaitingForChoicesCount > 0)
            {
                return WaitingForChoicesCount == 1
                    ? SurfaceText.Get("Import.Headline.OneWaiting", "1 import is ready for your choices")
                    : SurfaceText.Format("Import.Headline.ManyWaiting", "{0} imports are ready for your choices", WaitingForChoicesCount);
            }

            return string.Empty;
        }
    }

    public string HeadlineDetail
    {
        get
        {
            var leader = Items.FirstOrDefault(item => item.NeedsAttention)
                ?? Items.FirstOrDefault(item => item.IsPriority && item.IsActive)
                ?? Items.FirstOrDefault(item => item.IsActive)
                ?? Items.FirstOrDefault(item => item.CanContinue);

            return leader is null ? string.Empty : $"{leader.SourceDisplayName} — {leader.StageText}";
        }
    }
}

/// <summary>
/// The single live source of import activity for the whole application.
///
/// A person's own action appears immediately and keeps moving without a Refresh button: this service
/// owns one adaptive poll, wakes on catalog writes (coalesced), and pushes a snapshot to every surface
/// that cares (Import, Home, the shell status strip) only when something visible changed.
///
/// Progress comes from the import-critical aggregate (items prepared, items placed in the Vault),
/// read for all tracked imports in one query. Optional enrichment work is not part of it.
/// </summary>
public sealed class ImportActivityService : IAsyncDisposable
{
    /// <summary>Cadence while something is genuinely moving.</summary>
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(3);

    /// <summary>Cadence when nothing is running. Catalog writes still wake it.</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Minimum gap between polls. An import writes to the catalog many times per second; each write
    /// wakes the poll, so without this the "live" list became a tight read loop.
    /// </summary>
    public static readonly TimeSpan MinimumPollGap = TimeSpan.FromMilliseconds(300);

    private const int TrackedUnitCount = 40;

    private readonly CatalogDb _catalog;
    private readonly ImportReads _importReads;
    private readonly SynchronizationContext? _dispatcher;
    private readonly ImportPriorityOperations _priorityOperations;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, EtaObservationState> _etaStates = [];

    private Task? _loop;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ImportActivitySnapshot _current = ImportActivitySnapshot.Empty;
    private int _disposed;
    private long _pollCount;
    private long _publishCount;

    public ImportActivityService(CatalogDb catalog, SynchronizationContext? dispatcher = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _importReads = catalog.ImportReads;
        _priorityOperations = new ImportPriorityOperations(catalog);
        _dispatcher = dispatcher ?? UiDispatch.Context ?? SynchronizationContext.Current;

        _catalog.WriteCoordinator.Invalidated += OnCatalogInvalidated;
    }

    /// <summary>Raised on the UI dispatcher whenever the import picture actually changes.</summary>
    public event EventHandler<ImportActivitySnapshot>? Changed;

    public long PollCount => Interlocked.Read(ref _pollCount);

    public long PublishCount => Interlocked.Read(ref _publishCount);

    public ImportActivitySnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public void Start()
    {
        if (_loop is not null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _loop = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
    }

    /// <summary>Wakes the poll soon (coalesced with other wakes).</summary>
    public void RequestRefresh()
    {
        TaskCompletionSource wake;
        lock (_sync)
        {
            wake = _wake;
        }

        wake.TrySetResult();
    }

    /// <summary>Refreshes and waits for the result.</summary>
    public async Task<ImportActivitySnapshot> RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        return Current;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                // A transient read failure must never take the import surface down; the next tick retries.
                Trace.TraceWarning("Import activity poll failed: {0}", exception.GetType().Name);
            }

            var interval = Current.ActiveCount > 0 ? ActiveInterval : IdleInterval;

            TaskCompletionSource wake;
            lock (_sync)
            {
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wake = _wake;
            }

            try
            {
                await Task.WhenAny(wake.Task, Task.Delay(interval, cancellationToken)).ConfigureAwait(false);

                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed < MinimumPollGap)
                {
                    await Task.Delay(MinimumPollGap - elapsed, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;

        await _pollGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var measure = PerfTrace.Measure("import.activity.poll", 100);
            Interlocked.Increment(ref _pollCount);

            var units = await _importReads.ListRecentUnitsAsync(TrackedUnitCount, token).ConfigureAwait(false);
            var unitIds = units.Select(unit => unit.UnitId).ToArray();
            var progress = await _importReads
                .ListUnitProgressAsync(unitIds, token)
                .ConfigureAwait(false);
            var preparationFailures = await _importReads
                .ListRequiredPreparationFailuresAsync(unitIds, token)
                .ConfigureAwait(false);
            var focusedImportUnitId = await _priorityOperations
                .GetFocusedImportUnitAsync(token)
                .ConfigureAwait(false);

            var items = new List<ImportActivityItem>(units.Count);
            foreach (var unit in units)
            {
                var unitProgress = progress.GetValueOrDefault(unit.UnitId);
                var unitFailures = preparationFailures.GetValueOrDefault(unit.UnitId);
                var projected = Project(unit, unitProgress, preparationFailures: unitFailures);
                var isPriority = focusedImportUnitId == unit.UnitId && projected.IsActive;
                if (focusedImportUnitId == unit.UnitId && !projected.IsActive)
                {
                    await _priorityOperations.ClearIfFocusedAsync(unit.UnitId, token).ConfigureAwait(false);
                    focusedImportUnitId = null;
                }

                var etaText = ObserveEta(unit.UnitId, projected.Stage, unitProgress);
                items.Add(Project(unit, unitProgress, isPriority, etaText, unitFailures));
            }

            var tracked = units.Select(unit => unit.UnitId).ToHashSet();
            foreach (var stale in _etaStates.Keys.Where(id => !tracked.Contains(id)).ToList())
            {
                _etaStates.Remove(stale);
            }

            Publish(new ImportActivitySnapshot(items));
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>
    /// Turns durable import facts into the human stage, progress and available actions. This is the
    /// only place that mapping exists, so Import, Home and the shell can never disagree.
    /// </summary>
    public static ImportActivityItem Project(
        ImportUnitSummary unit,
        ImportUnitProgress? progress,
        bool isPriority = false,
        string? etaText = null,
        IReadOnlyList<ImportPreparationFailure>? preparationFailures = null)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var draft = ReadDraft(progress?.DraftJson);
        var requested = draft?.IsImportRequested == true;
        // Use IsUnitCommitted() for lifecycle/presentation decisions. IsEffectivelyCommitted()
        // includes DomainAuthorityCommitted (canonical commit) which is a storage boundary, not the
        // publication boundary. After canonical commit, the import is still in preparation and must
        // show as active, not "Done".
        var committed = unit.State.IsUnitCommitted();
        var failed = unit.State is ImportUnitState.FailedRetryable or ImportUnitState.FailedTerminal;
        var cancelled = unit.State == ImportUnitState.Cancelled;
        // Required preparation failures are actionable only while the import is still unpublished.
        // Cancelled/committed units can retain historical capability/job rows (for diagnostics and
        // repair provenance), but those rows must never resurrect a terminal import as active work.
        var blockingPreparationFailures = !committed && !cancelled
            ? preparationFailures
            : null;
        var hasPreparationFailure = blockingPreparationFailures is { Count: > 0 };

        var admitted = progress?.AdmittedItemCount ?? unit.IncludedCount;
        var prepared = progress?.PreparedItemCount ?? 0;
        var placed = progress?.PlacedItemCount ?? 0;
        var alreadyInLibrary = progress?.AlreadyInLibraryCount is > 0
            ? progress.AlreadyInLibraryCount
            : unit.ExactDuplicateCount;
        var duplicateOnly = unit.TotalItemCount > 0
            && admitted == unit.TotalItemCount
            && alreadyInLibrary == admitted
            && (progress?.UnusableItemCount ?? 0) == 0;
        var setAside = (draft?.AttentionItemIds?.Count ?? 0) + (progress?.UnusableItemCount ?? 0);
        var attentionCount = setAside + unit.CleanupFailedCount;
        var profileChosen = draft?.Destination.Kind is not null || requested;

        ImportActivityStage stage;
        if (cancelled)
        {
            stage = ImportActivityStage.Cancelled;
        }
        else if (committed && attentionCount > 0)
        {
            stage = ImportActivityStage.NeedsAttention;
        }
        else if (committed)
        {
            stage = ImportActivityStage.Done;
        }
        else if (failed || hasPreparationFailure)
        {
            stage = ImportActivityStage.NeedsAttention;
        }
        else if (unit.IsPaused)
        {
            stage = ImportActivityStage.Paused;
        }
        else if (unit.State == ImportUnitState.Committing)
        {
            stage = ImportActivityStage.Saving;
        }
        else if (unit.State == ImportUnitState.ReadyForVerification)
        {
            stage = ImportActivityStage.ReadyToVerify;
        }
        else if (unit.State == ImportUnitState.Intake)
        {
            stage = ImportActivityStage.Reading;
        }
        else if (!profileChosen)
        {
            stage = ImportActivityStage.WaitingForProfile;
        }
        else
        {
            stage = ImportActivityStage.Checking;
        }

        var operationalStage = unit.State == ImportUnitState.Committing ? ImportActivityStage.Saving
            : unit.State == ImportUnitState.ReadyForVerification ? ImportActivityStage.ReadyToVerify
            : profileChosen ? ImportActivityStage.Checking
            : unit.State == ImportUnitState.Intake ? ImportActivityStage.Reading : ImportActivityStage.WaitingForProfile;
        var timelineStage = stage is ImportActivityStage.Paused or ImportActivityStage.Cancelled or ImportActivityStage.NeedsAttention
            ? committed ? ImportActivityStage.Done : operationalStage
            : stage;
        var fraction = TimelineProgress(timelineStage, admitted, prepared, placed, unit.TotalItemCount);
        var duplicateOnlySettled = duplicateOnly
            && stage is ImportActivityStage.Checking or ImportActivityStage.Saving;
        var progressText = duplicateOnlySettled
            ? null
            : TimelineProgressText(
                timelineStage,
                fraction,
                admitted,
                timelineStage == ImportActivityStage.Saving ? placed : prepared,
                stage == ImportActivityStage.Paused
                    ? DescribeStage(ImportActivityStage.Paused)
                    : timelineStage is ImportActivityStage.WaitingForProfile or ImportActivityStage.ReadyToVerify
                        ? SurfaceText.Get("Import.Eta.WaitingForYou", "Waiting for your choice")
                        : etaText);

        var isActive = !duplicateOnlySettled
            && stage is ImportActivityStage.Reading or ImportActivityStage.Checking or ImportActivityStage.Saving;
        var isTerminal = committed
            || stage == ImportActivityStage.Cancelled
            || unit.State == ImportUnitState.FailedTerminal;
        var needsAttention = failed || hasPreparationFailure || (committed && attentionCount > 0);

        // ReadyForVerification: preparation is complete; expose Continue/Review, not Pause/Prioritize.
        var isReadyToVerify = stage == ImportActivityStage.ReadyToVerify;

        var destinationName = progress?.DestinationDisplayName
            ?? draft?.Destination.NewProfile?.DisplayName
            ?? (draft?.Destination.Kind == DestinationKind.SystemUnknown ? SurfaceText.Get("Import.DecideLater.Name", "unassigned media") : null);

        var title = stage switch
        {
            ImportActivityStage.Done when destinationName is not null =>
                SurfaceText.Format("Import.Title.ImportedTo", "Added to {0}", destinationName),
            ImportActivityStage.Saving when destinationName is not null =>
                SurfaceText.Format("Import.Title.ImportingTo", "Adding to {0}", destinationName),
            _ => string.IsNullOrWhiteSpace(unit.SourceDisplayName)
                ? SurfaceText.Get("Import.UnnamedSource", "Untitled import")
                : unit.SourceDisplayName,
        };

        var stageText = duplicateOnlySettled
            ? SurfaceText.Get("Import.Stage.AlreadyInVault", "Already in your Vault")
            : DescribeStageWithImportPhase(unit, stage);
        var detailText = duplicateOnly
            && stage is not (ImportActivityStage.Done or ImportActivityStage.NeedsAttention or ImportActivityStage.Cancelled)
            ? SurfaceText.Get(
                "Import.Detail.DuplicateOnly",
                "Already in your Vault — the existing media will be reused instead of importing another copy.")
            : DescribeDetail(unit, stage, destinationName, placed > 0 ? placed : admitted, attentionCount, progress, blockingPreparationFailures);

        return new ImportActivityItem(
            unit.UnitId,
            title,
            stage,
            stageText,
            detailText,
            duplicateOnlySettled || stage is ImportActivityStage.WaitingForProfile or ImportActivityStage.ReadyToVerify ? null : fraction,
            IsIndeterminate: stage == ImportActivityStage.Reading && unit.TotalItemCount == 0,
            IsActive: isActive,
            IsTerminal: isTerminal,
            NeedsAttention: needsAttention,
            CanContinue: stage is ImportActivityStage.WaitingForProfile or ImportActivityStage.ReadyToVerify
                || hasPreparationFailure,
            CanRetry: unit.State == ImportUnitState.FailedRetryable,
            // FAILED_TERMINAL exposes one decisive action: Clear. Clear internally performs the
            // ordered cancellation/rollback before hiding history, so a second Cancel button would
            // be redundant and misleading.
            CanCancel: unit.State.CanCancelUnpublished() && unit.State != ImportUnitState.FailedTerminal,
            CanClearHistory: unit.State == ImportUnitState.FailedTerminal
                || (!needsAttention
                    && unit.State is (ImportUnitState.Committed
                        or ImportUnitState.Completed
                        or ImportUnitState.Cancelled)),
            CanPause: isActive && !isReadyToVerify && !unit.IsPaused && unit.State != ImportUnitState.Committing,
            CanPrioritize: isActive && !isReadyToVerify && !isPriority && unit.State != ImportUnitState.Committing,
            CanStart: unit.IsPaused && !committed && !duplicateOnly,
            ShowTransportControls: !duplicateOnly
                && ((isActive && !isReadyToVerify && unit.State != ImportUnitState.Committing) || unit.IsPaused),
            IsPriority: isPriority,
            TransportTooltip: isPriority
                ? SurfaceText.Get("Import.Start.Prioritized", "This import is prioritized")
                : unit.IsPaused
                    ? SurfaceText.Get("Import.Start.Resume", "Start import")
                    : SurfaceText.Get("Import.Start.Prioritize", "Prioritize this import"),
            CanOpenProfile: committed
                && unit.DestinationProfileId.HasValue
                && unit.DestinationProfileId.Value != Guid.Empty,
            unit.DestinationProfileId,
            unit.CreatedAtUtc,
            unit.UpdatedAtUtc,
            progressText);
    }

    private static double TimelineProgress(
        ImportActivityStage stage,
        int admitted,
        int prepared,
        int placed,
        int totalItemCount) => stage switch
    {
        ImportActivityStage.Reading => totalItemCount > 0 ? 0.10 : 0.02,
        ImportActivityStage.WaitingForProfile => 0.15,
        ImportActivityStage.Checking => 0.15 + (0.20 * Fraction(prepared, admitted)),
        ImportActivityStage.ReadyToVerify => 0.35,
        ImportActivityStage.Saving => 0.35 + (0.60 * Fraction(placed, admitted)),
        ImportActivityStage.Done => 1.0,
        _ => 0,
    };

    private static string TimelineProgressText(
        ImportActivityStage stage,
        double fraction,
        int total,
        int completed,
        string? suffix)
    {
        if (stage is ImportActivityStage.WaitingForProfile or ImportActivityStage.ReadyToVerify)
        {
            return suffix ?? DescribeStage(stage);
        }

        return total > 0
            ? CountText(completed, total, fraction, suffix)
            : SurfaceText.Format(
                "Import.Progress.WithStatus",
                "{0} · {1}",
                SurfaceText.Format("Import.Progress.Percent", "{0}%", (int)Math.Floor(fraction * 100)),
                suffix ?? SurfaceText.Get("Import.Eta.Estimating", "estimating…"));
    }

    private static double Fraction(int completed, int total) =>
        total <= 0 ? 0 : Math.Clamp(completed / (double)total, 0, 1);

    private static string CountText(int done, int total, double fraction, string? suffix = null)
    {
        var count = SurfaceText.Format(
            "Import.Progress.Count",
            "{0} / {1} media · {2}%",
            done,
            total,
            (int)Math.Floor(fraction * 100));
        return string.IsNullOrWhiteSpace(suffix)
            ? count
            : SurfaceText.Format("Import.Progress.WithStatus", "{0} · {1}", count, suffix);
    }

    private string? ObserveEta(Guid unitId, ImportActivityStage stage, ImportUnitProgress? progress)
    {
        // Item counts do not price heterogeneous image, video and model preparation work.
        if (stage == ImportActivityStage.Checking)
        {
            _etaStates.Remove(unitId);
            return SurfaceText.Get("Import.Eta.Preparing", "Preparing remaining media…");
        }
        if (stage is not (ImportActivityStage.Checking or ImportActivityStage.Saving)
            || progress is null)
        {
            _etaStates.Remove(unitId);
            return null;
        }

        var total = progress.AdmittedItemCount;
        var completed = stage == ImportActivityStage.Checking
            ? progress.PreparedItemCount
            : progress.PlacedItemCount;
        if (total <= 0 || completed < 0 || completed >= total)
        {
            _etaStates.Remove(unitId);
            return null;
        }

        var now = Stopwatch.GetTimestamp();
        if (!_etaStates.TryGetValue(unitId, out var state)
            || state.Stage != stage
            || state.Total != total
            || completed < state.Completed)
        {
            _etaStates[unitId] = new EtaObservationState(stage, total, completed, now);
            return SurfaceText.Get("Import.Eta.Estimating", "estimating…");
        }

        var interval = Stopwatch.GetElapsedTime(state.LastTimestamp, now);
        if (completed == state.Completed || interval <= TimeSpan.Zero)
        {
            return Stopwatch.GetElapsedTime(state.LastProgressTimestamp, now) < TimeSpan.FromSeconds(5)
                   && state.Confidence >= 3
                   && Stopwatch.GetElapsedTime(state.FirstTimestamp, now) >= TimeSpan.FromSeconds(3)
                   && state.SmoothedRate is > 0
                ? FormatEta(TimeSpan.FromSeconds((total - completed) / state.SmoothedRate.Value))
                : SurfaceText.Get("Import.Eta.Estimating", "estimating…");
        }

        var observedRate = (completed - state.Completed) / interval.TotalSeconds;
        if (observedRate > 0 && double.IsFinite(observedRate))
        {
            if (state.SmoothedRate is { } previousRate)
            {
                observedRate = Math.Clamp(observedRate, previousRate * 0.25, previousRate * 4);
                state.SmoothedRate = (previousRate * 0.65) + (observedRate * 0.35);
            }
            else
            {
                state.SmoothedRate = observedRate;
            }

            state.Confidence++;
        }

        state.Completed = completed;
        state.LastTimestamp = now;
        state.LastProgressTimestamp = now;
        return state.Confidence >= 3
               && Stopwatch.GetElapsedTime(state.FirstTimestamp, now) >= TimeSpan.FromSeconds(3)
               && state.SmoothedRate is > 0
            ? FormatEta(TimeSpan.FromSeconds((total - completed) / state.SmoothedRate.Value))
            : SurfaceText.Get("Import.Eta.Estimating", "estimating…");
    }

    private static string FormatEta(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromMinutes(1))
        {
            return SurfaceText.Get("Import.Eta.LessThanMinute", "< 1 min remaining");
        }

        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        if (minutes < 60)
        {
            return SurfaceText.Format("Import.Eta.Minutes", "~{0} min remaining", minutes);
        }

        var hours = minutes / 60;
        var remainder = minutes % 60;
        return remainder == 0
            ? SurfaceText.Format("Import.Eta.Hours", "~{0} hr remaining", hours)
            : SurfaceText.Format("Import.Eta.HoursMinutes", "~{0} hr {1} min remaining", hours, remainder);
    }

    private sealed class EtaObservationState(
        ImportActivityStage stage,
        int total,
        int completed,
        long timestamp)
    {
        public ImportActivityStage Stage { get; } = stage;
        public int Total { get; } = total;
        public int Completed { get; set; } = completed;
        public long FirstTimestamp { get; } = timestamp;
        public long LastTimestamp { get; set; } = timestamp;
        public long LastProgressTimestamp { get; set; } = timestamp;
        public int Confidence { get; set; }
        public double? SmoothedRate { get; set; }
    }

    private static VerificationDraftV1? ReadDraft(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return VerificationDraftV1.FromJson(json, 0);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string DescribeStageWithImportPhase(
        ImportUnitSummary unit,
        ImportActivityStage stage)
    {
        if (unit.StageTwoStartedAtUtc is { } stageTwoStarted
            && stage is ImportActivityStage.Checking
                or ImportActivityStage.Paused
                or ImportActivityStage.NeedsAttention
                or ImportActivityStage.ReadyToVerify)
        {
            var end = stage == ImportActivityStage.Checking
                ? DateTimeOffset.UtcNow
                : unit.UpdatedAtUtc;
            if (end < stageTwoStarted)
            {
                end = stageTwoStarted;
            }

            return SurfaceText.Format(
                "Import.Stage.Stage2",
                "Stage 2 · {0} · {1}",
                DescribeStage(stage),
                FormatElapsed(end - stageTwoStarted));
        }

        if (unit.StageTwoStartedAtUtc is null
            && stage is ImportActivityStage.Reading
                or ImportActivityStage.WaitingForProfile
                or ImportActivityStage.Checking
                or ImportActivityStage.Paused)
        {
            return SurfaceText.Format(
                "Import.Stage.Stage1",
                "Stage 1 · {0}",
                DescribeStage(stage));
        }

        return DescribeStage(stage);
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }

        return $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
    }

    public static string DescribeStage(ImportActivityStage stage) => stage switch
    {
        ImportActivityStage.Reading => SurfaceText.Get("Import.Stage.Reading", "Finding media"),
        ImportActivityStage.WaitingForProfile => SurfaceText.Get("Import.Stage.WaitingForProfile", "Waiting for your choice"),
        ImportActivityStage.Checking => SurfaceText.Get("Import.Stage.Checking", "Preparing media"),
        ImportActivityStage.ReadyToVerify => SurfaceText.Get("Import.Stage.ReadyToVerify", "Ready to review"),
        ImportActivityStage.Saving => SurfaceText.Get("Import.Stage.Saving", "Adding to Vault"),
        ImportActivityStage.Done => SurfaceText.Get("Import.Stage.Done", "Done"),
        ImportActivityStage.Paused => SurfaceText.Get("Import.Stage.Paused", "Paused"),
        ImportActivityStage.Cancelled => SurfaceText.Get("Import.Stage.Cancelled", "Cancelled"),
        _ => SurfaceText.Get("Import.Stage.NeedsAttention", "Needs attention"),
    };

    private static string DescribeDetail(
        ImportUnitSummary unit,
        ImportActivityStage stage,
        string? destinationName,
        int importedCount,
        int attentionCount,
        ImportUnitProgress? progress,
        IReadOnlyList<ImportPreparationFailure>? preparationFailures)
    {
        if (preparationFailures is { Count: > 0 })
        {
            return DescribePreparationFailure(preparationFailures);
        }

        switch (stage)
        {
            case ImportActivityStage.Done when attentionCount > 0:
                return SurfaceText.Format(
                    "Import.Detail.Partial",
                    "{0} added · {1} need attention",
                    importedCount,
                    attentionCount);
            case ImportActivityStage.Done:
                return destinationName is null
                    ? SurfaceText.Format("Import.Detail.Imported", "{0} media added to your Vault", importedCount)
                    : SurfaceText.Format("Import.Detail.ImportedTo", "{0} media added to {1}", importedCount, destinationName);
            case ImportActivityStage.NeedsAttention:
                return SurfaceText.Get(
                    "Import.Detail.NeedsAttention",
                    "Something stopped this import. You can try again or cancel it.");
            case ImportActivityStage.Cancelled:
                return SurfaceText.Get("Import.Detail.Cancelled", "Cancelled. The original files were left untouched.");
        }

        if (unit.TotalItemCount == 0)
        {
            return SurfaceText.Get("Import.Detail.Scanning", "Looking through the files you picked…");
        }

        var media = unit.TotalItemCount == 1
            ? SurfaceText.Get("Import.Detail.OneItem", "1 item")
            : SurfaceText.Format("Import.Detail.ManyItems", "{0} items", unit.TotalItemCount);

        var alreadyInLibrary = progress?.AlreadyInLibraryCount is > 0 ? progress.AlreadyInLibraryCount : unit.ExactDuplicateCount;
        return alreadyInLibrary > 0
            ? SurfaceText.Format("Import.Detail.WithDuplicates", "{0} · {1} already in your Vault", media, alreadyInLibrary)
            : media;
    }

    private static string DescribePreparationFailure(IReadOnlyList<ImportPreparationFailure> failures)
    {
        var failure = failures[0];
        var item = string.IsNullOrWhiteSpace(failure.SourceFileName)
            ? SurfaceText.Get("Import.Detail.RequiredPreparationFailureFallback", "This media could not be prepared.")
            : failure.SourceFileName;

        var message = SurfaceText.Format(
            "Import.Detail.RequiredPreparationFailure",
            "Could not prepare {0}. Try again or cancel this import.",
            item);

        if (failures.Count > 1)
        {
            message += SurfaceText.Format(
                "Import.Detail.MoreRequiredPreparationFailures",
                " · {0} more item(s) need attention",
                failures.Count - 1);
        }

        return message;
    }

    private void Publish(ImportActivitySnapshot snapshot)
    {
        lock (_sync)
        {
            if (SnapshotsMatch(_current, snapshot))
            {
                return;
            }

            _current = snapshot;
        }

        Interlocked.Increment(ref _publishCount);
        if (_dispatcher is null || ReferenceEquals(SynchronizationContext.Current, _dispatcher))
        {
            Changed?.Invoke(this, snapshot);
        }
        else
        {
            _dispatcher.Post(_ => Changed?.Invoke(this, snapshot), null);
        }
    }

    /// <summary>
    /// Equality that ignores the persisted "updated at" instant: an import writes it constantly while
    /// nothing a person can see changes, and publishing each of those made the list re-render needlessly.
    /// </summary>
    public static bool SnapshotsMatch(ImportActivitySnapshot left, ImportActivitySnapshot right)
    {
        if (left.Items.Count != right.Items.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Items.Count; index++)
        {
            if (left.Items[index] with { UpdatedAtUtc = default } != right.Items[index] with { UpdatedAtUtc = default })
            {
                return false;
            }
        }

        return true;
    }

    private void OnCatalogInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        if (string.Equals(invalidation.DomainKind, CatalogInvalidationDomain.Import, StringComparison.Ordinal))
        {
            RequestRefresh();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidated;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        RequestRefresh();

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetime.Dispose();
        _pollGate.Dispose();
    }
}
