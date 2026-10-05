using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Maintenance;

public sealed record HealthFindingGroup(
    HealthSeverity Severity,
    IReadOnlyList<HealthFinding> Findings);

public sealed class LibraryHealthViewModel : ScreenStateViewModel, IDisposable
{
    private readonly CatalogDb? _catalog;
    private readonly NavigationCoordinator? _navigation;
    private readonly StorageMetricsProvider? _storageMetrics;
    private bool _disposed;
    private readonly CacheMaintenanceOperations? _cacheMaintenance;
    private readonly LibraryHealthEvaluator? _healthEvaluator;
    private readonly RepairPlanner? _repairPlanner;
    private readonly RepairExecutor? _repairExecutor;

    private StorageMetricsSnapshot? _metrics;
    private string? _actionFeedbackMessage;
    private HealthScanResult? _scanResult;
    private HealthFinding? _selectedFinding;
    private RepairPlan? _pendingRepair;
    private string? _scanProgressText;

    public LibraryHealthViewModel(
        CatalogDb? catalog = null,
        NavigationCoordinator? navigation = null,
        StorageMetricsProvider? storageMetrics = null,
        CacheMaintenanceOperations? cacheMaintenance = null,
        LibraryHealthEvaluator? healthEvaluator = null,
        RepairPlanner? repairPlanner = null,
        RepairExecutor? repairExecutor = null)
    {
        _catalog = catalog;
        _navigation = navigation;

        if (_catalog is not null)
        {
            _storageMetrics = storageMetrics ?? new StorageMetricsProvider(_catalog);
            var cachePaths = new CachePaths(_catalog.Paths);
            var cacheBudget = new PersistentCacheBudget(cachePaths);
            _cacheMaintenance = cacheMaintenance ?? new CacheMaintenanceOperations(cachePaths, cacheBudget);
            _healthEvaluator = healthEvaluator ?? new LibraryHealthEvaluator(_catalog);
            _repairPlanner = repairPlanner ?? new RepairPlanner(_catalog);
            _repairExecutor = repairExecutor ?? new RepairExecutor(_catalog);
        }
        else
        {
            _storageMetrics = storageMetrics;
            _cacheMaintenance = cacheMaintenance;
            _healthEvaluator = healthEvaluator;
            _repairPlanner = repairPlanner;
            _repairExecutor = repairExecutor;
        }

        RefreshCommand = new AsyncRelayCommand(() => LoadMetricsAsync());
        ClearCacheCommand = new AsyncRelayCommand(() => ClearCacheAsync());
        RunHealthCheckCommand = new AsyncRelayCommand(() => RunStandardScanAsync());
        RunDeepHealthCheckCommand = new AsyncRelayCommand(() => RunDeepScanAsync());
        PrepareRepairCommand = new AsyncRelayCommand(() => PrepareSelectedRepairAsync());
        ConfirmRepairCommand = new AsyncRelayCommand(() => ConfirmPendingRepairAsync());
        CancelRepairCommand = new RelayCommand(_ => CancelPendingRepair());
        ClearResultsCommand = new RelayCommand(_ => ClearResults());
        SelectFindingCommand = new RelayCommand(parameter =>
        {
            if (parameter is HealthFinding finding)
            {
                SelectedFinding = finding;
            }
        });

        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated += OnCatalogInvalidated;
        }
    }

    public StorageMetricsSnapshot? Metrics
    {
        get => _metrics;
        private set
        {
            if (SetProperty(ref _metrics, value))
            {
                RaiseAllMetricProperties();
            }
        }
    }

    public string? ActionFeedbackMessage
    {
        get => _actionFeedbackMessage;
        set => SetProperty(ref _actionFeedbackMessage, value);
    }

    public ObservableCollection<HealthFinding> Findings { get; } = [];

    public ObservableCollection<HealthFindingGroup> FindingGroups { get; } = [];

    public HealthFinding? SelectedFinding
    {
        get => _selectedFinding;
        set
        {
            if (SetProperty(ref _selectedFinding, value))
            {
                CancelPendingRepair();
                RaisePropertyChanged(nameof(CanPrepareSelectedRepair));
            }
        }
    }

    public RepairPlan? PendingRepair
    {
        get => _pendingRepair;
        private set
        {
            if (SetProperty(ref _pendingRepair, value))
            {
                RaisePropertyChanged(nameof(HasPendingRepair));
                RaisePropertyChanged(nameof(RepairConfirmationText));
            }
        }
    }

    public string? ScanProgressText
    {
        get => _scanProgressText;
        private set => SetProperty(ref _scanProgressText, value);
    }

    public bool HasPendingRepair => PendingRepair is not null;

    public bool HasScanResults => _scanResult is not null;

    public bool CanPrepareSelectedRepair => SelectedFinding?.RepairAvailable == true;

    public string RepairConfirmationText => PendingRepair is null
        ? string.Empty
        : $"Review before repair: {PendingRepair.IntendedTargetAction} The plan will be revalidated before it changes anything.";

    public long TotalProfiles => _metrics?.ProfileCount ?? 0;

    public long ActiveMedias => _metrics?.ActiveMediaCount ?? 0;

    public long TrashedMedias => _metrics?.TrashItemCount ?? 0;

    public long HealthFindingCount => _scanResult?.Findings.Count ?? _metrics?.HealthFindingCount ?? 0;

    public string HealthStatusWord => _scanResult is null
        ? "Ready to scan"
        : HealthFindingCount == 0 ? "Healthy" : $"{HealthFindingCount} Finding(s)";

    public string ManagedMediaBytesFormatted => FormatBytes(_metrics?.ManagedMediaBytes ?? 0);

    public string MediaAssetBytesFormatted => FormatBytes(_metrics?.MediaAssetBytes ?? 0);


    public string TemporaryBytesFormatted => FormatBytes((_metrics?.TemporaryBytes ?? 0) + (_metrics?.StagingBytes ?? 0));

    public string CacheTotalBytesFormatted => FormatBytes(_metrics?.CacheBytesTotal ?? 0);

    public string CacheQuotaBytesFormatted => FormatBytes(_metrics?.CacheQuotaBytes ?? 0);

    public string TrashBytesFormatted => FormatBytes(_metrics?.TrashBytes ?? 0);

    public string DatabaseBytesFormatted => FormatBytes(_metrics?.DatabaseBytes ?? 0);

    public string FaceCropsBytesFormatted => GetFamilyBytesFormatted(CacheFamily.FaceCrops);

    public string ModelPreviewsBytesFormatted => GetFamilyBytesFormatted(CacheFamily.ModelPreviews);

    public double CacheUsagePercent
    {
        get
        {
            if (_metrics is null || _metrics.CacheQuotaBytes <= 0)
            {
                return 0.0;
            }
            return Math.Clamp((double)_metrics.CacheBytesTotal / _metrics.CacheQuotaBytes * 100.0, 0.0, 100.0);
        }
    }

    public ICommand RefreshCommand { get; }

    public ICommand ClearCacheCommand { get; }

    public ICommand RunHealthCheckCommand { get; }

    public ICommand RunDeepHealthCheckCommand { get; }

    public ICommand PrepareRepairCommand { get; }

    public ICommand ConfirmRepairCommand { get; }

    public ICommand CancelRepairCommand { get; }

    public ICommand ClearResultsCommand { get; }

    public void ClearResults()
    {
        // Acknowledging a displayed scan never changes files, repair receipts or job authority.
        SelectedFinding = null;
        Findings.Clear();
        FindingGroups.Clear();
        _scanResult = null;
        ScanProgressText = null;
        ActionFeedbackMessage = null;
        ShowReady();
        RaisePropertyChanged(nameof(HealthFindingCount));
        RaisePropertyChanged(nameof(HealthStatusWord));
        RaisePropertyChanged(nameof(HasScanResults));
    }

    public ICommand SelectFindingCommand { get; }

    public Task RunStandardScanAsync(CancellationToken cancellationToken = default) =>
        RunScanAsync(HealthScanMode.Standard, cancellationToken);

    public Task RunDeepScanAsync(CancellationToken cancellationToken = default) =>
        RunScanAsync(HealthScanMode.Deep, cancellationToken);

    private async Task RunScanAsync(
        HealthScanMode mode,
        CancellationToken cancellationToken)
    {
        if (_healthEvaluator is null)
        {
            ActionFeedbackMessage = "Vault Health is unavailable until a Vault is open.";
            ShowReady();
            return;
        }

        ShowLoading();
        ScanProgressText = mode == HealthScanMode.Deep
            ? "Checking managed files and content integrity…"
            : "Checking Vault authority and managed placement…";

        try
        {
            var result = await _healthEvaluator.RunScanAsync(
                new HealthScanOptions(mode),
                cancellationToken: cancellationToken).ConfigureAwait(true);

            _scanResult = result;
            RaisePropertyChanged(nameof(HasScanResults));
            Findings.Clear();
            foreach (var finding in result.Findings
                         .OrderByDescending(item => item.Severity)
                         .ThenBy(item => item.Code, StringComparer.Ordinal))
            {
                Findings.Add(finding);
            }

            RebuildFindingGroups();
            SelectedFinding = Findings.FirstOrDefault();
            ScanProgressText = result.IsHealthy
                ? $"Scan complete — {result.ItemsScanned} items checked, no findings."
                : $"Scan complete — {result.ItemsScanned} items checked, {result.Findings.Count} finding(s).";
            ActionFeedbackMessage = result.IsHealthy
                ? "Vault Health found no issues."
                : "Review each finding before preparing an available repair.";
            RaisePropertyChanged(nameof(HealthFindingCount));
            RaisePropertyChanged(nameof(HealthStatusWord));
            ShowReady();
        }
        catch (OperationCanceledException)
        {
            ScanProgressText = "Health check cancelled. No repair was applied.";
            ShowReady();
        }
        catch (Exception)
        {
            ShowRecoverableError("The Vault Health check could not complete. Retry when the Vault is available.");
        }
    }

    public async Task PrepareSelectedRepairAsync(CancellationToken cancellationToken = default)
    {
        if (_repairPlanner is null || SelectedFinding is not { RepairAvailable: true } finding)
        {
            ActionFeedbackMessage = "Select a finding with an available conservative repair.";
            return;
        }

        var result = await _repairPlanner.PrepareAsync(finding, cancellationToken).ConfigureAwait(true);
        if (result.IsSuccess && result.Value is { } plan)
        {
            PendingRepair = plan;
            ActionFeedbackMessage = "Repair prepared. Review the exact action before confirming.";
        }
        else
        {
            PendingRepair = null;
            ActionFeedbackMessage = result.UserMessage ?? "That finding cannot be repaired safely in its current state.";
        }
    }

    public async Task ConfirmPendingRepairAsync(CancellationToken cancellationToken = default)
    {
        if (_repairExecutor is null || PendingRepair is not { } plan)
        {
            return;
        }

        BeginBackgroundUpdate();
        try
        {
            var result = await _repairExecutor.ExecuteAsync(plan, cancellationToken).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ActionFeedbackMessage = result.UserMessage ?? "The repair remains unresolved and no success was claimed.";
                return;
            }

            PendingRepair = null;
            ActionFeedbackMessage = "Repair completed and journaled. Vault Health is checking the result.";
            await RunStandardScanAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            EndBackgroundUpdate();
        }
    }

    public void CancelPendingRepair()
    {
        PendingRepair = null;
    }

    public async Task LoadMetricsAsync(CancellationToken cancellationToken = default)
    {
        if (_storageMetrics is null)
        {
            ShowReady();
            return;
        }

        ShowLoading();
        try
        {
            var snapshot = await _storageMetrics.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
            Metrics = snapshot;
            ShowReady();
        }
        catch (Exception ex)
        {
            ShowRecoverableError($"Failed to evaluate Vault metrics: {OperationExecution.SafeMessage(ex)}");
        }
    }

    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        if (_cacheMaintenance is null)
        {
            ActionFeedbackMessage = "Derived persistent cache cleared.";
            return;
        }

        BeginBackgroundUpdate();
        try
        {
            var deleted = await _cacheMaintenance.ClearAllDerivedCacheAsync(cancellationToken).ConfigureAwait(true);
            ActionFeedbackMessage = $"Cleared {deleted} cached files.";
            if (_storageMetrics is not null)
            {
                Metrics = await _storageMetrics.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ShowRecoverableError($"Failed to clear cache: {OperationExecution.SafeMessage(ex)}");
        }
        finally
        {
            EndBackgroundUpdate();
        }
    }

    public void ApplySnapshot(StorageMetricsSnapshot snapshot)
    {
        Metrics = snapshot;
        ShowReady();
    }

    private string GetFamilyBytesFormatted(CacheFamily family)
    {
        if (_metrics is not null && _metrics.CacheBytesByFamily.TryGetValue(family, out var bytes))
        {
            return FormatBytes(bytes);
        }
        return "0 B";
    }

    private void RaiseAllMetricProperties()
    {
        RaisePropertyChanged(nameof(TotalProfiles));
        RaisePropertyChanged(nameof(ActiveMedias));
        RaisePropertyChanged(nameof(TrashedMedias));
        RaisePropertyChanged(nameof(HealthFindingCount));
        RaisePropertyChanged(nameof(HealthStatusWord));
        RaisePropertyChanged(nameof(ManagedMediaBytesFormatted));
        RaisePropertyChanged(nameof(MediaAssetBytesFormatted));
        RaisePropertyChanged(nameof(TemporaryBytesFormatted));
        RaisePropertyChanged(nameof(CacheTotalBytesFormatted));
        RaisePropertyChanged(nameof(CacheQuotaBytesFormatted));
        RaisePropertyChanged(nameof(TrashBytesFormatted));
        RaisePropertyChanged(nameof(DatabaseBytesFormatted));
        RaisePropertyChanged(nameof(FaceCropsBytesFormatted));
        RaisePropertyChanged(nameof(ModelPreviewsBytesFormatted));
        RaisePropertyChanged(nameof(CacheUsagePercent));
    }

    private void RebuildFindingGroups()
    {
        FindingGroups.Clear();
        foreach (var group in Findings.GroupBy(item => item.Severity).OrderByDescending(group => group.Key))
        {
            FindingGroups.Add(new HealthFindingGroup(group.Key, group.ToList()));
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }
        if (bytes < 1024 * 1024)
        {
            return $"{(bytes / 1024.0):F1} KB";
        }
        if (bytes < 1024 * 1024 * 1024)
        {
            return $"{(bytes / (1024.0 * 1024.0)):F1} MB";
        }
        return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):F2} GB";
    }

    private void OnCatalogInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        if (_disposed)
        {
            return;
        }

        switch (invalidation.DomainKind)
        {
            case CatalogInvalidationDomain.Health:
                ScheduleRefresh();
                break;
        }
    }

    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            return;
        }

        UiDispatch.Run(() => Coalescer.Signal());
    }

    private RefreshCoalescer? _refreshCoalescer;

    private RefreshCoalescer Coalescer => _refreshCoalescer ??= new RefreshCoalescer(
        ct => _disposed ? Task.CompletedTask : LoadMetricsAsync(ct),
        TimeSpan.FromMilliseconds(600));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshCoalescer?.Dispose();
        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidated;
        }
    }
}
