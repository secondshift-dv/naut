using System.Collections.ObjectModel;
using Neuterradise.App.Import;
using Neuterradise.App.SystemServices.Recovery;
using Neuterradise.App.Localization;
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
    private readonly StaleResultGuard _metricsRequest = new();
    private readonly StaleResultGuard _scanRequest = new();
    private string? _metricsStatus;
    private string? _actionFeedbackMessage;
    private HealthScanResult? _scanResult;
    private HealthFinding? _selectedFinding;
    private RepairPlan? _pendingRepair;
    private IReadOnlyList<HealthFinding> _pendingBulk = [];
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
            _storageMetrics = storageMetrics ?? _catalog.StorageMetrics;
            _cacheMaintenance = cacheMaintenance
                ?? new CacheMaintenanceOperations(_storageMetrics.CachePaths, _storageMetrics.CacheBudget);
            _metrics = _storageMetrics.LastSnapshot;
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
        RunHealthCheckCommand = new AsyncRelayCommand(() => RunStandardScanAsync(RouteCancellationToken));
        RunDeepHealthCheckCommand = new AsyncRelayCommand(() => RunDeepScanAsync(RouteCancellationToken));
        CancelScanCommand = new RelayCommand(_ => { _scanRequest.Begin(); ScanProgressText = "Health check cancelled."; ShowReady(); });
        ImportActionCommand = new AsyncRelayCommand(async parameter =>
        {
            if (_catalog is null || parameter is not ValueTuple<HealthFinding, HealthImportAction> request
                || request.Item1.ImportUnitId is not { } unitId
                || request.Item1.ImportActions?.Contains(request.Item2) != true) return;
            var controls = new ImportUnitControlAuthority(_catalog);
            var succeeded = request.Item2 switch
            {
                HealthImportAction.CompleteCleanup or HealthImportAction.Cancel =>
                    (await controls.CancelAsync(unitId, RouteCancellationToken).ConfigureAwait(true)).Result
                        is ImportCancellationResult.Cancelled or ImportCancellationResult.AlreadyCancelled,
                HealthImportAction.CleanupTemporary => await ImportRecovery.ReleaseTerminalStagingAsync(_catalog, unitId, RouteCancellationToken).ConfigureAwait(true),
                HealthImportAction.Retry => await controls.RetryAsync(unitId, RouteCancellationToken).ConfigureAwait(true),
                HealthImportAction.Resume => await controls.StartAsync(unitId, RouteCancellationToken).ConfigureAwait(true),
                _ => false,
            };
            ActionFeedbackMessage = SurfaceText.Get(succeeded ? "Health.Import.ActionComplete" : "Health.Import.ActionPending",
                succeeded ? "Import action completed." : "Import action remains pending. Review Import Activity for details.");
            await RunStandardScanAsync(RouteCancellationToken).ConfigureAwait(true);
        });
        PrepareRepairCommand = new AsyncRelayCommand(() => PrepareSelectedRepairAsync(RouteCancellationToken));
        ConfirmRepairCommand = new AsyncRelayCommand(() => ConfirmPendingRepairAsync(RouteCancellationToken));
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

    public string? MetricsStatus
    {
        get => _metricsStatus;
        private set => SetProperty(ref _metricsStatus, value);
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

    public bool HasPendingRepair => PendingRepair is not null || _pendingBulk.Count > 0;

    public bool HasScanResults => _scanResult is not null;

    public bool CanPrepareSelectedRepair => SelectedFinding?.RepairAvailable == true;

    public string RepairConfirmationText => _pendingBulk.Count > 0
        ? SurfaceText.Format("Health.BulkConfirm", "Confirm {0} original integrity records. Each original is rechecked; no files will be changed.", _pendingBulk.Count)
        : PendingRepair is null
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
    public ICommand CancelScanCommand { get; }

    public ICommand ImportActionCommand { get; }

    public ICommand PrepareRepairCommand { get; }

    public ICommand ConfirmRepairCommand { get; }

    public ICommand CancelRepairCommand { get; }

    public ICommand ClearResultsCommand { get; }

    public void ClearResults()
    {
        if (_disposed) return;
        _scanRequest.Begin();
        _metricsRequest.Begin();
        MetricsStatus = null;

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

        if (_disposed) return;
        var request = _scanRequest.Begin();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, request.CancellationToken, RouteCancellationToken);
        ShowLoading();
        ScanProgressText = mode == HealthScanMode.Deep
            ? "Checking managed files and content integrity…"
            : "Checking Vault authority and managed placement…";

        try
        {
            var progress = new Progress<HealthScanProgress>(p =>
            {
                if (_scanRequest.IsCurrent(request) && !linked.IsCancellationRequested && IsLoading)
                    ScanProgressText = $"{p.CurrentStage} — {p.ItemsScanned} checked, {p.FindingsCount} findings";
            });
            var result = await Task.Run(() => _healthEvaluator.RunScanAsync(
                new HealthScanOptions(mode), progress, linked.Token), linked.Token).ConfigureAwait(true);
            if (!_scanRequest.IsCurrent(request) || linked.IsCancellationRequested) return;

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
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (!_scanRequest.IsCurrent(request)) return;
            ScanProgressText = "Health check cancelled. No repair was applied.";
            ShowReady();
        }
        catch (Exception)
        {
            if (_scanRequest.IsCurrent(request) && !linked.IsCancellationRequested)
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

    public async Task PrepareOriginalRecoveryAsync(HealthFinding finding, string sourcePath)
    {
        if (_repairPlanner is null || _disposed || !IsRouteActive) return;
        SelectedFinding = finding;
        var result = await _repairPlanner.PrepareOriginalRecoveryAsync(finding, sourcePath, RouteCancellationToken)
            .ConfigureAwait(true);
        if (_disposed || !IsRouteActive || SelectedFinding != finding) return;
        PendingRepair = result.IsSuccess ? result.Value : null;
        ActionFeedbackMessage = result.IsSuccess
            ? "Recovery prepared. Review the exact action before confirming."
            : result.UserMessage ?? "That source could not be verified.";
    }

    public async Task ConfirmPendingRepairAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingBulk.Count > 0)
        {
            await ConfirmBulkIntegrityAsync(cancellationToken).ConfigureAwait(true);
            return;
        }
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
            if (plan.MediaId is Guid mediaId && _healthEvaluator is not null)
                await RefreshOriginalFindingsAsync(mediaId, cancellationToken).ConfigureAwait(true);
            else if (plan.Kind == RepairKind.RegenerateProfileManifest && plan.ProfileId is Guid profileId && _healthEvaluator is not null)
                await RefreshFocusedFindingsAsync(null, profileId, cancellationToken).ConfigureAwait(true);
            else
                await RunStandardScanAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            EndBackgroundUpdate();
        }
    }

    private Task RefreshOriginalFindingsAsync(Guid mediaId, CancellationToken ct) =>
        RefreshFocusedFindingsAsync(mediaId, null, ct);

    private async Task RefreshFocusedFindingsAsync(Guid? mediaId, Guid? profileId, CancellationToken ct)
    {
        if (_healthEvaluator is null || _disposed) return;
        var request = _scanRequest.Begin();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, request.CancellationToken, RouteCancellationToken);
        var focused = await Task.Run(() => _healthEvaluator.RunScanAsync(
            new HealthScanOptions(HealthScanMode.Focused, FocusedProfileId: profileId, FocusedMediaId: mediaId), cancellationToken: linked.Token),
            linked.Token).ConfigureAwait(true);
        if (!_scanRequest.IsCurrent(request) || linked.IsCancellationRequested) return;
        bool IsAffected(HealthFinding finding) => mediaId.HasValue
            ? finding.MediaId == mediaId
            : profileId.HasValue && finding.ProfileId == profileId && !finding.MediaId.HasValue;
        var merged = Findings.Where(f => !IsAffected(f))
            .Concat(focused.Findings.Where(IsAffected))
            .Distinct().OrderByDescending(f => f.Severity).ThenBy(f => f.Code, StringComparer.Ordinal).ToArray();
        Findings.Clear();
        foreach (var finding in merged) Findings.Add(finding);
        _scanResult = focused with { Findings = merged };
        RebuildFindingGroups();
        SelectedFinding = Findings.FirstOrDefault();
        RaisePropertyChanged(nameof(HealthFindingCount));
        RaisePropertyChanged(nameof(HealthStatusWord));
        RaisePropertyChanged(nameof(HasScanResults));
    }

    public void PrepareBulkIntegrity()
    {
        if (_disposed || !IsRouteActive || IsLoading || IsBackgroundUpdating) return;
        PendingRepair = null;
        // Only independent catalog-only repairs can share one confirmation. Source selection,
        // replacement, deletion and replay of material operations always require individual review.
        _pendingBulk = Findings.Where(f => f.RepairAvailable
                && f.MediaId.HasValue
                && f.Code is HealthFindingCode.CanonicalOriginalMissing or HealthFindingCode.CanonicalOriginalMismatch
                    or HealthFindingCode.CanonicalOriginalReverify)
            .DistinctBy(f => f.MediaId).ToArray();
        RaisePropertyChanged(nameof(HasPendingRepair));
        RaisePropertyChanged(nameof(RepairConfirmationText));
    }

    private async Task ConfirmBulkIntegrityAsync(CancellationToken ct)
    {
        if (_repairPlanner is null || _repairExecutor is null || IsBackgroundUpdating) return;
        var batch = _pendingBulk;
        CancelPendingRepair();
        BeginBackgroundUpdate();
        var succeeded = 0;
        var failed = 0;
        var affected = new HashSet<Guid>();
        try
        {
            foreach (var finding in batch)
            {
                ct.ThrowIfCancellationRequested();
                var plan = await _repairPlanner.PrepareAsync(finding, ct).ConfigureAwait(true);
                if (!plan.IsSuccess || plan.Value is null)
                {
                    failed++;
                    continue;
                }
                var result = await _repairExecutor.ExecuteAsync(plan.Value, ct).ConfigureAwait(true);
                if (result.IsSuccess)
                {
                    succeeded++;
                    affected.Add(finding.MediaId!.Value);
                }
                else failed++;
            }
            foreach (var mediaId in affected)
                await RefreshOriginalFindingsAsync(mediaId, ct).ConfigureAwait(true);
        }
        finally
        {
            ActionFeedbackMessage = SurfaceText.Format("Health.BulkOutcome",
                "Integrity reconciliation: {0} completed, {1} unresolved, {2} not processed.",
                succeeded, failed, batch.Count - succeeded - failed);
            EndBackgroundUpdate();
        }
    }

    public void CancelPendingRepair()
    {
        _pendingBulk = [];
        PendingRepair = null;
        RaisePropertyChanged(nameof(HasPendingRepair));
        RaisePropertyChanged(nameof(RepairConfirmationText));
    }

    public async Task LoadMetricsAsync(CancellationToken cancellationToken = default)
    {
        if (_storageMetrics is null || _disposed) return;

        var request = _metricsRequest.Begin();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, request.CancellationToken, RouteCancellationToken);
        if (_storageMetrics.LastSnapshot is { } lastSnapshot) Metrics = lastSnapshot;
        MetricsStatus = SurfaceText.Get("Settings.Vault.MetricsLoading", "Calculating storage metrics…");
        try
        {
            var snapshot = await _storageMetrics.GetSnapshotAsync(linked.Token).ConfigureAwait(true);
            if (!_metricsRequest.IsCurrent(request) || linked.IsCancellationRequested) return;
            Metrics = snapshot;
            MetricsStatus = null;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (_metricsRequest.IsCurrent(request)) MetricsStatus = null;
        }
        catch (Exception)
        {
            if (_metricsRequest.IsCurrent(request) && !linked.IsCancellationRequested)
                MetricsStatus = SurfaceText.Get("Settings.Vault.MetricsUnavailable", "Storage metrics could not be read.");
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
            _storageMetrics?.InvalidateSnapshot();
            await LoadMetricsAsync(cancellationToken).ConfigureAwait(true);
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
        _metricsRequest.Dispose();
        _scanRequest.Dispose();
        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidated;
        }
    }
}
