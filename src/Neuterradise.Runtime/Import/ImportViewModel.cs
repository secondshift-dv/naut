using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Neuterradise.App.Import.Intake;
using Neuterradise.App.Import.Preparation;
using Neuterradise.App.Localization;
using Neuterradise.App.Media;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Import;

/// <summary>
/// Which slice of import activity the user is looking at. "History" is deliberately separate so the
/// live area stays about what is happening now.
/// </summary>
public enum ImportActivityFilter
{
    Active,
    NeedsAttention,
    History,
}

/// <summary>
/// The Import page: a drop target, pickers, the compact two-step overlay, and a live list of the
/// user's own imports.
///
/// Flow: Source (pick or drop) → Step 1 Profile → Step 2 Details → Import. Source selection is not a
/// wizard step. The overlay opens as soon as intake has registered the files; preparation runs in the
/// background while the user chooses. After Import the overlay closes and the activity list shows the
/// import moving, pushed by <see cref="ImportActivityService"/>; there is no Refresh.
/// </summary>
public sealed class ImportViewModel : ScreenStateViewModel, IDisposable
{
    private readonly CatalogDb? _catalog;
    private readonly NavigationCoordinator? _navigation;
    private readonly ImportReads? _importReads;
    private readonly ImportIntakeCoordinator? _intake;
    private readonly ImportPreparationCoordinator? _preparationCoordinator;
    private readonly ImportUnitControlAuthority? _controlAuthority;
    private readonly ImportActivityService? _activity;
    private readonly ImportFinalizer? _finalizer;
    private readonly bool _ownsActivityService;
    private readonly CancellationTokenSource _lifetime = new();

    private readonly ObservableCollection<ImportUnitItemViewModel> _units = [];
    private readonly ObservableCollection<ImportUnitItemViewModel> _filteredUnits = [];
    private ImportActivityFilter _selectedFilter = ImportActivityFilter.Active;
    private int _waitingForChoicesCount;
    private int _needsAttentionCount;
    private int _totalUnitsCount;
    private int _activeUnitsCount;
    private string? _intakeStatusMessage;
    private bool _isDragOver;
    private bool _isIntaking;
    private ImportCancellationOutcome? _lastCancellationOutcome;
    private ImportWizardViewModel? _wizard;
    private bool _disposed;

    public ImportViewModel()
        : this(null, null)
    {
    }

    public ImportViewModel(
        CatalogDb? catalog = null,
        NavigationCoordinator? navigation = null,
        ImportReads? importReads = null,
        ImportUnitWrites? importUnitWrites = null,
        ImportIntakeCoordinator? intake = null,
        ImportPreparationCoordinator? preparationCoordinator = null,
        ImportActivityService? activityService = null,
        ImportFinalizer? finalizer = null,
        JobCancellationOperations? cancellation = null)
    {
        _catalog = catalog;
        _navigation = navigation;
        _importReads = importReads ?? _catalog?.ImportReads;
        _intake = intake ?? (_catalog is null ? null : new ImportIntakeCoordinator(_catalog));
        _preparationCoordinator = preparationCoordinator ?? (_catalog is not null ? new ImportPreparationCoordinator(_catalog) : null);
        var controlWrites = importUnitWrites ?? (_catalog is null ? null : new ImportUnitWrites(_catalog));
        _controlAuthority = _catalog is null
            ? null
            : new ImportUnitControlAuthority(
                _catalog,
                finalizer,
                jobCancellation: cancellation,
                unitWrites: controlWrites,
                preparationCoordinator: _preparationCoordinator);
        _finalizer = finalizer;

        if (activityService is not null)
        {
            _activity = activityService;
        }
        else if (_catalog is not null)
        {
            _activity = new ImportActivityService(_catalog);
            _ownsActivityService = true;
            _activity.Start();
        }

        ContinueImportCommand = new RelayCommand(parameter =>
        {
            if (ResolveUnit(parameter) is { } unit)
            {
                var mode = unit.Stage == ImportActivityStage.ReadyToVerify
                    || (unit.NeedsAttention && unit.CanContinue)
                    ? ImportWizardMode.Verify
                    : ImportWizardMode.ChooseProfile;
                OpenWizard(unit.UnitId, unit.SourceDisplayName, intakeSummary: null, mode: mode);
            }
        });

        OpenProfileCommand = new RelayCommand(parameter =>
        {
            switch (parameter)
            {
                case Guid profileId when profileId != Guid.Empty:
                    OpenProfile(profileId);
                    break;
                case ImportUnitItemViewModel { DestinationProfileId: { } id } when id != Guid.Empty:
                    OpenProfile(id);
                    break;
            }
        });

        PauseUnitCommand = new AsyncRelayCommand(parameter => PauseUnitAsync(ResolveUnit(parameter)));
        StartUnitCommand = new AsyncRelayCommand(parameter => StartUnitAsync(ResolveUnit(parameter)));
        RetryUnitCommand = new AsyncRelayCommand(parameter => RetryUnitAsync(ResolveUnitId(parameter)));
        CancelUnitCommand = new AsyncRelayCommand(async parameter =>
        {
            if (ResolveUnitId(parameter) is { } unitId)
            {
                await CancelUnitAsync(unitId);
            }
        });

        ClearHistoryItemCommand = new AsyncRelayCommand(parameter => ClearHistoryItemAsync(ResolveUnit(parameter)));
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryAsync);

        SelectFilterCommand = new RelayCommand(filter =>
        {
            switch (filter)
            {
                case ImportActivityFilter parsed:
                    SelectedFilter = parsed;
                    break;
                case string text when Enum.TryParse<ImportActivityFilter>(text, ignoreCase: true, out var value):
                    SelectedFilter = value;
                    break;
            }
        });

        if (_activity is not null)
        {
            _activity.Changed += OnActivityChanged;
            ApplyActivity(_activity.Current);
            _activity.RequestRefresh();
        }

        if (_importReads is null)
        {
            ShowEmpty();
        }
        else
        {
            ShowReady();
        }
    }

    /// <summary>
    /// Truthful source wording. Intake allocates every item with the COPY policy, so the originals are
    /// never removed; the old "moved" sentence described a policy this build does not apply.
    /// </summary>
    public string MoveSummary => SurfaceText.Get(
        "Import.CopySummary",
        "Media you import is copied into your Vault. Original files stay where they are.");

    public ObservableCollection<ImportUnitItemViewModel> Units => _units;

    public ObservableCollection<ImportUnitItemViewModel> FilteredUnits => _filteredUnits;

    public ImportActivityFilter SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                ApplyFilter();
                RaisePropertyChanged(nameof(IsActiveFilter));
                RaisePropertyChanged(nameof(IsAttentionFilter));
                RaisePropertyChanged(nameof(IsHistoryFilter));
                RaisePropertyChanged(nameof(HasClearableHistory));
            }
        }
    }

    public bool IsActiveFilter => _selectedFilter == ImportActivityFilter.Active;

    public bool IsAttentionFilter => _selectedFilter == ImportActivityFilter.NeedsAttention;

    public bool IsHistoryFilter => _selectedFilter == ImportActivityFilter.History;

    public bool HasClearableHistory => IsHistoryFilter && _filteredUnits.Any(unit => unit.CanClearHistory);

    public bool IsDragOver
    {
        get => _isDragOver;
        set => SetProperty(ref _isDragOver, value);
    }

    public bool IsIntaking
    {
        get => _isIntaking;
        private set
        {
            if (SetProperty(ref _isIntaking, value))
            {
                RaisePropertyChanged(nameof(HasIntakeStatus));
            }
        }
    }

    public int WaitingForChoicesCount
    {
        get => _waitingForChoicesCount;
        private set => SetProperty(ref _waitingForChoicesCount, value);
    }

    public int NeedsAttentionCount
    {
        get => _needsAttentionCount;
        private set
        {
            if (SetProperty(ref _needsAttentionCount, value))
            {
                RaisePropertyChanged(nameof(HasAttention));
            }
        }
    }

    public bool HasAttention => _needsAttentionCount > 0;

    public int TotalUnitsCount
    {
        get => _totalUnitsCount;
        private set => SetProperty(ref _totalUnitsCount, value);
    }

    public int ActiveUnitsCount
    {
        get => _activeUnitsCount;
        private set => SetProperty(ref _activeUnitsCount, value);
    }

    public string? IntakeStatusMessage
    {
        get => _intakeStatusMessage;
        set
        {
            if (SetProperty(ref _intakeStatusMessage, value))
            {
                RaisePropertyChanged(nameof(HasIntakeStatus));
            }
        }
    }

    public bool HasIntakeStatus => IsIntaking || !string.IsNullOrWhiteSpace(_intakeStatusMessage);

    public ImportCancellationOutcome? LastCancellationOutcome
    {
        get => _lastCancellationOutcome;
        private set => SetProperty(ref _lastCancellationOutcome, value);
    }

    /// <summary>The compact two-step overlay, or null when the Import page is shown on its own.</summary>
    public ImportWizardViewModel? Wizard
    {
        get => _wizard;
        private set
        {
            if (SetProperty(ref _wizard, value))
            {
                RaisePropertyChanged(nameof(HasWizard));
            }
        }
    }

    public bool HasWizard => _wizard is not null;

    public bool ShowEmptyActivity => _filteredUnits.Count == 0;

    public string EmptyActivityTitle => _selectedFilter switch
    {
        ImportActivityFilter.NeedsAttention =>
            SurfaceText.Get("Import.Empty.Attention.Title", "Nothing needs your attention"),
        ImportActivityFilter.History =>
            SurfaceText.Get("Import.Empty.History.Title", "No finished imports yet"),
        _ => SurfaceText.Get("Import.Empty.Active.Title", "No imports in progress"),
    };

    public string EmptyActivityMessage => _selectedFilter switch
    {
        ImportActivityFilter.NeedsAttention =>
            SurfaceText.Get("Import.Empty.Attention.Message", "Every import finished cleanly."),
        ImportActivityFilter.History =>
            SurfaceText.Get("Import.Empty.History.Message", "Imports you finish will be listed here."),
        _ => SurfaceText.Get(
            "Import.Empty.Active.Message",
            "Drop photos, videos or a folder above — or use one of the picker buttons."),
    };

    public ICommand ContinueImportCommand { get; }

    public ICommand OpenProfileCommand { get; }

    public ICommand CancelUnitCommand { get; }

    public ICommand PauseUnitCommand { get; }

    public ICommand StartUnitCommand { get; }

    public ICommand RetryUnitCommand { get; }

    public ICommand ClearHistoryItemCommand { get; }

    public ICommand ClearHistoryCommand { get; }

    public ICommand SelectFilterCommand { get; }

    /// <summary>A one-off read used by tests. Normal operation is push-driven.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_activity is not null)
        {
            var effective = cancellationToken.CanBeCanceled ? cancellationToken : RouteCancellationToken;
            await _activity.RefreshNowAsync(effective);
        }
    }

    private void OnActivityChanged(object? sender, ImportActivitySnapshot snapshot)
    {
        if (_disposed || !IsRouteActive)
        {
            return;
        }

        ApplyActivity(snapshot);
    }

    public void ApplyActivity(ImportActivitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Reconcile in place so the list does not flash and scroll position survives a tick.
        var byId = _units.ToDictionary(unit => unit.UnitId);
        var seen = new HashSet<Guid>();

        for (var index = 0; index < snapshot.Items.Count; index++)
        {
            var item = snapshot.Items[index];
            seen.Add(item.UnitId);

            if (byId.TryGetValue(item.UnitId, out var existing))
            {
                existing.Apply(item);

                var currentIndex = _units.IndexOf(existing);
                if (currentIndex != index && index < _units.Count)
                {
                    _units.Move(currentIndex, index);
                }
            }
            else
            {
                var created = new ImportUnitItemViewModel(item);
                if (index <= _units.Count)
                {
                    _units.Insert(index, created);
                }
                else
                {
                    _units.Add(created);
                }
            }
        }

        for (var index = _units.Count - 1; index >= 0; index--)
        {
            if (!seen.Contains(_units[index].UnitId))
            {
                _units.RemoveAt(index);
            }
        }

        WaitingForChoicesCount = snapshot.WaitingForChoicesCount;
        NeedsAttentionCount = snapshot.AttentionCount;
        ActiveUnitsCount = snapshot.ActiveCount;
        TotalUnitsCount = _units.Count;

        if (IsIntaking && snapshot.Items.Count > 0)
        {
            IsIntaking = false;
        }

        ApplyFilter();
        if (IsRouteActive)
        {
            ShowReady();
        }
    }

    public void PopulateUnits(IEnumerable<ImportUnitSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        ApplyActivity(new ImportActivitySnapshot(
            summaries.Select(summary => ImportActivityService.Project(summary, null)).ToList()));
    }

    /// <summary>
    /// Brings the filtered list in line without clearing it, so a progress tick does not recreate the
    /// visible rows (which reset their state and cost a full re-layout).
    /// </summary>
    private void ApplyFilter()
    {
        var wanted = _units.Where(_selectedFilter switch
        {
            ImportActivityFilter.NeedsAttention => (Func<ImportUnitItemViewModel, bool>)(unit => unit.NeedsAttention),
            ImportActivityFilter.History => unit => unit.IsTerminal && !unit.NeedsAttention,
            _ => unit => !unit.IsTerminal || unit.NeedsAttention,
        }).ToList();

        for (var index = _filteredUnits.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(_filteredUnits[index]))
            {
                _filteredUnits.RemoveAt(index);
            }
        }

        for (var index = 0; index < wanted.Count; index++)
        {
            var item = wanted[index];
            var current = _filteredUnits.IndexOf(item);
            if (current < 0)
            {
                _filteredUnits.Insert(index, item);
            }
            else if (current != index)
            {
                _filteredUnits.Move(current, index);
            }
        }

        RaisePropertyChanged(nameof(ShowEmptyActivity));
        RaisePropertyChanged(nameof(EmptyActivityTitle));
        RaisePropertyChanged(nameof(EmptyActivityMessage));
        RaisePropertyChanged(nameof(HasClearableHistory));
    }

    private ImportUnitItemViewModel? ResolveUnit(object? parameter) => parameter switch
    {
        ImportUnitItemViewModel item => item,
        Guid id => _units.FirstOrDefault(unit => unit.UnitId == id),
        _ => null,
    };

    private static Guid? ResolveUnitId(object? parameter) => parameter switch
    {
        Guid id when id != Guid.Empty => id,
        ImportUnitItemViewModel item => item.UnitId,
        _ => null,
    };

    public void OpenProfile(Guid profileId) => _navigation?.Navigate(new ProfileRoute(profileId));

    /// <summary>Shows the two-step overlay for one import. Opening never waits for background work.</summary>
    public ImportWizardViewModel OpenWizard(
        Guid unitId,
        string sourceDisplayName,
        ImportIntakeSummary? intakeSummary,
        ImportWizardMode mode = ImportWizardMode.ChooseProfile)
    {
        CloseWizard(Wizard);

        var wizard = new ImportWizardViewModel(
            unitId,
            sourceDisplayName,
            _catalog,
            _finalizer,
            close: CloseWizard,
            imported: OnWizardImported,
            intakeSummary: intakeSummary,
            mode: mode);
        Wizard = wizard;
        TaskObserver.Observe(wizard.InitializeAsync(), "Import overlay", exception => IntakeStatusMessage = OperationExecution.SafeMessage(exception));
        return wizard;
    }

    private void CloseWizard(ImportWizardViewModel? wizard)
    {
        if (wizard is null)
        {
            return;
        }

        if (ReferenceEquals(Wizard, wizard))
        {
            Wizard = null;
        }

        wizard.Dispose();
        _activity?.RequestRefresh();
    }

    private void OnWizardImported(Guid unitId)
    {
        SelectedFilter = ImportActivityFilter.Active;
        IntakeStatusMessage = null;
        _activity?.RequestRefresh();
    }

    private async Task PauseUnitAsync(ImportUnitItemViewModel? unit)
    {
        if (_controlAuthority is null || unit is null || !unit.CanPause)
        {
            return;
        }

        unit.BeginPendingControl(ImportPendingControl.Pausing);
        try
        {
            await _controlAuthority.PauseAsync(unit.UnitId, RouteCancellationToken);
            _activity?.RequestRefresh();
        }
        finally
        {
            unit.EndPendingControl();
        }
    }

    private async Task StartUnitAsync(ImportUnitItemViewModel? unit)
    {
        if (_controlAuthority is null || unit is null)
        {
            return;
        }

        var shouldStart = unit.CanStart;
        unit.BeginPendingControl(shouldStart ? ImportPendingControl.Starting : ImportPendingControl.None);
        try
        {
        if (shouldStart)
        {
            // Start/Resume: unpause, focus this import (P1/90), demote previous focus to P3/50,
            // wake finalizer. Uses persisted state — does not recreate the import graph.
            await _controlAuthority.StartAsync(unit.UnitId, RouteCancellationToken);
        }
        else if (unit.CanPrioritize)
        {
            // Prioritize: make this running-but-not-focused import the focused import.
            // Task E P1=90 authority. Previous focus demoted to P3=50. No failure mutation.
            await _controlAuthority.PrioritizeAsync(unit.UnitId, RouteCancellationToken);
        }

        _activity?.RequestRefresh();
        }
        finally
        {
            unit.EndPendingControl();
        }
    }

    private async Task RetryUnitAsync(Guid? unitId)
    {
        if (_controlAuthority is null || unitId is not { } id)
        {
            return;
        }

        var unit = _units.FirstOrDefault(item => item.UnitId == id);
        if (unit is null || !unit.CanRetry)
        {
            return;
        }

        await _controlAuthority.RetryAsync(id, RouteCancellationToken);
        _activity?.RequestRefresh();
    }

    private async Task ClearHistoryItemAsync(ImportUnitItemViewModel? unit)
    {
        if (_controlAuthority is null || unit is null || !unit.CanClearHistory)
        {
            return;
        }

        var cleared = await _controlAuthority
            .ClearHistoryItemAsync(unit.UnitId, RouteCancellationToken)
            .ConfigureAwait(true);
        IntakeStatusMessage = cleared
            ? null
            : SurfaceText.Get(
                "Import.ClearPending",
                "This import is still settling cleanup. Try Clear again in a moment.");
        _activity?.RequestRefresh();
    }

    private async Task ClearHistoryAsync()
    {
        if (_controlAuthority is null || !HasClearableHistory)
        {
            return;
        }

        await _controlAuthority.ClearHistoryAsync(RouteCancellationToken);
        _activity?.RequestRefresh();
    }

    /// <summary>Cancelling the durable import is its own explicit action, separate from closing the overlay.</summary>
    public async Task<ImportCancellationOutcome?> CancelUnitAsync(Guid unitId)
    {
        if (_controlAuthority is null)
        {
            return null;
        }

        var unit = _units.FirstOrDefault(item => item.UnitId == unitId);
        unit?.BeginPendingControl(ImportPendingControl.Cancelling);

        if (Wizard?.UnitId == unitId)
        {
            CloseWizard(Wizard);
        }

        try
        {
            var outcome = await _controlAuthority.CancelAsync(unitId, RouteCancellationToken);
            LastCancellationOutcome = outcome;
            IntakeStatusMessage = outcome.UserMessage;
            _activity?.RequestRefresh();
            return outcome;
        }
        finally
        {
            unit?.EndPendingControl();
        }
    }

    /// <summary>
    /// Takes files or folders the user dropped or picked (all entry points share this authority).
    /// Registers them durably, opens the overlay at once, and prepares in the background.
    /// </summary>
    public async Task IntakeSourcesAsync(
        IEnumerable<string> paths,
        IntakeOrigin origin = IntakeOrigin.Picker,
        Guid? suggestedDestinationProfileId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken = cancellationToken.CanBeCanceled ? cancellationToken : RouteCancellationToken;

        if (_intake is null)
        {
            return;
        }

        // Existence probes can block on removable, redirected, or temporarily unavailable
        // storage. Keep them off the UI thread just like the subsequent enumeration/intake work.
        var sourceList = await Task.Run(
            () => paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList(),
            cancellationToken).ConfigureAwait(true);
        if (sourceList.Count == 0)
        {
            IntakeStatusMessage = SurfaceText.Get(
                "Import.Intake.NothingUsable",
                "Nothing there could be imported. Try photos, videos or a folder containing them.");
            return;
        }

        try
        {
            IsIntaking = true;
            SelectedFilter = ImportActivityFilter.Active;
            IntakeStatusMessage = SurfaceText.Get("Import.Intake.Starting", "Looking through your files…");

            // Enumeration touches the file system; keep it off the UI thread.
            var request = new ImportIntakeRequest(sourceList, origin, suggestedDestinationProfileId);
            var intakeResult = await Task.Run(() => _intake.IntakeAsync(request, cancellationToken: cancellationToken), cancellationToken);
            _activity?.RequestRefresh();

            var unsupported = intakeResult.TotalDiscoveredItemCount - intakeResult.TotalAllocatedCandidateCount;
            var usableUnits = intakeResult.Units.Where(unit => unit.AllocatedCandidateCount > 0).ToList();
            if (usableUnits.Count == 0)
            {
                IntakeStatusMessage = SurfaceText.Format(
                    "Import.Intake.NoneSupported",
                    "None of the {0} files are supported photos, videos, or 3D models. They were left untouched.",
                    intakeResult.TotalDiscoveredItemCount);
                return;
            }

            IntakeStatusMessage = unsupported > 0
                ? SurfaceText.Format(
                    "Import.Intake.WithUnsupported",
                    "{0} media ready · {1} unsupported file(s) left untouched",
                    intakeResult.TotalAllocatedCandidateCount,
                    unsupported)
                : null;

            // Preparation (fingerprints, duplicate detection, previews) continues without holding the
            // overlay; the user initiates wizard by clicking Choose Profile on the scheduler row.
            if (_preparationCoordinator is not null)
            {
                foreach (var unit in usableUnits)
                {
                    var unitId = unit.UnitId;
                    TaskObserver.Observe(
                        PrepareInBackgroundAsync(unitId),
                        "Import preparation",
                        exception => IntakeStatusMessage = SurfaceText.Get(
                            "Import.Intake.PrepareFailed",
                            "naut could not prepare this import. Your original files were left untouched; try again from the list."));
                }
            }
        }
        catch (OperationCanceledException)
        {
            IntakeStatusMessage = null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Import intake failed: {0}", ex.GetType().Name);
            IntakeStatusMessage = SurfaceText.Get(
                "Import.Intake.Failed",
                "That import could not be started. Original files were left untouched; try again.");
        }
        finally
        {
            IsIntaking = false;
            _activity?.RequestRefresh();
        }
    }

    private async Task PrepareInBackgroundAsync(Guid unitId)
    {
        // Preparation outlives the route (the user may navigate away); only app shutdown stops it.
        await Task.Run(
            () => _preparationCoordinator!.PrepareUnitAsync(unitId, _lifetime.Token),
            _lifetime.Token);
        _activity?.RequestRefresh();
        _finalizer?.Wake();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        CloseWizard(Wizard);

        if (_activity is not null)
        {
            _activity.Changed -= OnActivityChanged;

            if (_ownsActivityService)
            {
                TaskObserver.Observe(_activity.DisposeAsync().AsTask(), "ImportViewModel.DisposeActivityAsync");
            }
        }

    }
}

/// <summary>
/// One row in the live import list. Mutable so the list is updated in place on every tick without
/// rebuilding items and losing scroll or focus.
/// </summary>
public enum ImportPendingControl
{
    None,
    Pausing,
    Starting,
    Cancelling,
}

public sealed class ImportUnitItemViewModel : ObservableObject
{
    private ImportActivityItem _item;
    private ImportPendingControl _pendingControl;

    public ImportUnitItemViewModel(ImportActivityItem item)
    {
        _item = item ?? throw new ArgumentNullException(nameof(item));
    }

    public Guid UnitId => _item.UnitId;

    public string SourceDisplayName => _item.SourceDisplayName;

    public string StageText => _pendingControl switch
    {
        ImportPendingControl.Pausing => "Pausing…",
        ImportPendingControl.Starting => "Starting…",
        ImportPendingControl.Cancelling => "Cancelling…",
        _ => _item.StageText,
    };

    public string DetailText => _pendingControl == ImportPendingControl.Cancelling
        ? "Stopping new work and finishing safely…"
        : _item.DetailText;

    public string? ProgressText => _item.ProgressText;

    public bool HasProgressText => !string.IsNullOrWhiteSpace(_item.ProgressText);

    public double ProgressValue => _item.Progress ?? 0;

    public bool IsIndeterminate => _item.IsIndeterminate;

    public bool ShowProgress => _item.HasProgressBar;

    public bool IsActive => _item.IsActive;

    public bool IsTerminal => _item.IsTerminal;

    public bool CanClearHistory => _item.CanClearHistory;

    public bool IsComplete => _item.IsComplete;

    public bool NeedsAttention => _item.NeedsAttention;

    public bool CanContinue => _item.CanContinue;

    public bool CanRetry => _item.CanRetry;

    public bool CanCancel => _pendingControl == ImportPendingControl.None && _item.CanCancel;

    public bool CanPause => _pendingControl == ImportPendingControl.None && _item.CanPause;

    public bool CanPrioritize => _pendingControl == ImportPendingControl.None && _item.CanPrioritize;

    public bool CanStart => _pendingControl == ImportPendingControl.None && _item.CanStart;

    public bool ShowTransportControls => _pendingControl != ImportPendingControl.None || _item.ShowTransportControls;

    public bool IsPriority => _item.IsPriority;

    public string TransportTooltip => _item.TransportTooltip;

    public bool CanOpenProfile => _item.CanOpenProfile;

    public Guid? DestinationProfileId => _item.DestinationProfileId;

    public ImportActivityStage Stage => _item.Stage;

    public DateTimeOffset UpdatedAtUtc => _item.UpdatedAtUtc;

    public void Apply(ImportActivityItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_item == item)
        {
            return;
        }

        var previous = _item;
        _item = item;

        // Raise only what changed: a progress tick should not re-evaluate every binding on the row.
        if (previous.SourceDisplayName != item.SourceDisplayName) RaisePropertyChanged(nameof(SourceDisplayName));
        if (previous.StageText != item.StageText) RaisePropertyChanged(nameof(StageText));
        if (previous.DetailText != item.DetailText) RaisePropertyChanged(nameof(DetailText));
        if (previous.ProgressText != item.ProgressText)
        {
            RaisePropertyChanged(nameof(ProgressText));
            RaisePropertyChanged(nameof(HasProgressText));
        }

        if (previous.Progress != item.Progress) RaisePropertyChanged(nameof(ProgressValue));
        if (previous.IsIndeterminate != item.IsIndeterminate) RaisePropertyChanged(nameof(IsIndeterminate));
        if (previous.HasProgressBar != item.HasProgressBar) RaisePropertyChanged(nameof(ShowProgress));
        if (previous.IsActive != item.IsActive) RaisePropertyChanged(nameof(IsActive));
        if (previous.IsTerminal != item.IsTerminal)
        {
            RaisePropertyChanged(nameof(IsTerminal));
        }
        if (previous.Stage != item.Stage)
        {
            RaisePropertyChanged(nameof(Stage));
            RaisePropertyChanged(nameof(IsComplete));
        }

        if (previous.NeedsAttention != item.NeedsAttention)
        {
            RaisePropertyChanged(nameof(NeedsAttention));
        }
        if (previous.CanClearHistory != item.CanClearHistory) RaisePropertyChanged(nameof(CanClearHistory));
        if (previous.CanContinue != item.CanContinue) RaisePropertyChanged(nameof(CanContinue));
        if (previous.CanRetry != item.CanRetry) RaisePropertyChanged(nameof(CanRetry));
        if (previous.CanCancel != item.CanCancel) RaisePropertyChanged(nameof(CanCancel));
        if (previous.CanPause != item.CanPause) RaisePropertyChanged(nameof(CanPause));
        if (previous.CanPrioritize != item.CanPrioritize) RaisePropertyChanged(nameof(CanPrioritize));
        if (previous.CanStart != item.CanStart) RaisePropertyChanged(nameof(CanStart));
        if (previous.ShowTransportControls != item.ShowTransportControls) RaisePropertyChanged(nameof(ShowTransportControls));
        if (previous.IsPriority != item.IsPriority) RaisePropertyChanged(nameof(IsPriority));
        if (previous.TransportTooltip != item.TransportTooltip) RaisePropertyChanged(nameof(TransportTooltip));
        if (previous.CanOpenProfile != item.CanOpenProfile) RaisePropertyChanged(nameof(CanOpenProfile));
        if (previous.DestinationProfileId != item.DestinationProfileId) RaisePropertyChanged(nameof(DestinationProfileId));
        if (previous.UpdatedAtUtc != item.UpdatedAtUtc) RaisePropertyChanged(nameof(UpdatedAtUtc));
    }

    public void BeginPendingControl(ImportPendingControl pendingControl)
    {
        if (_pendingControl == pendingControl)
        {
            return;
        }

        _pendingControl = pendingControl;
        RaisePropertyChanged(nameof(StageText));
        RaisePropertyChanged(nameof(DetailText));
        RaisePropertyChanged(nameof(CanCancel));
        RaisePropertyChanged(nameof(CanPause));
        RaisePropertyChanged(nameof(CanStart));
        RaisePropertyChanged(nameof(CanPrioritize));
        RaisePropertyChanged(nameof(ShowTransportControls));
    }

    public void EndPendingControl() => BeginPendingControl(ImportPendingControl.None);
}
