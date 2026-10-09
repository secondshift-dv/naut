using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Neuterradise.App.Activity;
using Neuterradise.App.Gallery;
using Neuterradise.App.Import;
using Neuterradise.App.Localization;
using Neuterradise.App.Maintenance;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Jobs.Handlers;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Home;

public sealed class HomeViewModel : ScreenStateViewModel, IDisposable
{
    public const int MaxSpotlightCandidates = 12;
    public const int RecentlyActiveCount = 12;
    public const int MaxAttentionItems = 3;

    private readonly CatalogDb? _catalog;
    private readonly ImportReads? _importReads;
    private readonly ProfileOperations? _profileOperations;
    private readonly ImportActivityService? _activity;
    private readonly MediaResourceAuthority? _mediaResources;
    private readonly StaleResultGuard _loadGuard = new();
    private readonly ObservableCollection<ImportActivityItem> _activeImports = [];
    private readonly ObservableCollection<HomeSpotlightViewModel> _spotlightCandidates = [];
    private readonly ObservableCollection<HomeProfileTileViewModel> _recentlyActive = [];
    private readonly ObservableCollection<HomeAttentionItem> _needsAttention = [];
    private readonly ObservableCollection<HomeDiscoveryItem> _discovery = [];
    private readonly ObservableCollection<HomeActivityItem> _recentActivity = [];

    private int _spotlightIndex;
    private HomeVaultPulse _vaultPulse = HomeVaultPulse.Unknown;
    private string? _refreshNotice;
    private readonly HashSet<string> _failedRefreshSections = new(StringComparer.Ordinal);
    private bool _disposed;
    private RefreshCoalescer? _galleryCoalescer;
    private RefreshCoalescer? _attentionCoalescer;
    private RefreshCoalescer? _relatedCoalescer;
    private RefreshCoalescer? _chronicleCoalescer;

    public HomeViewModel() : this(null) { }

    public HomeViewModel(
        CatalogDb? catalog = null,
        ImportReads? importReads = null,
        ProfileOperations? profileOperations = null,
        ImportActivityService? activityService = null,
        MediaResourceAuthority? mediaResources = null)
    {
        _catalog = catalog;
        _activity = activityService;
        _mediaResources = mediaResources;
        _importReads = importReads ?? _catalog?.ImportReads;
        _profileOperations = profileOperations ?? (_catalog is not null ? new ProfileOperations(_catalog) : null);

        NextSpotlightCommand = new RelayCommand(_ => MoveSpotlight(1), _ => HasMultipleSpotlightCandidates);
        PreviousSpotlightCommand = new RelayCommand(_ => MoveSpotlight(-1), _ => HasMultipleSpotlightCandidates);

        if (_activity is not null)
        {
            _activity.Changed += OnImportActivityChanged;
            ApplyImportActivity(_activity.Current);
        }

        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated += OnCatalogInvalidated;
        }
        else
        {
            ShowEmpty();
        }
    }

    public string? RefreshNotice
    {
        get => _refreshNotice;
        private set
        {
            if (SetProperty(ref _refreshNotice, value))
            {
                RaisePropertyChanged(nameof(HasRefreshNotice));
            }
        }
    }
    public bool HasRefreshNotice => !string.IsNullOrWhiteSpace(RefreshNotice);
    public ObservableCollection<HomeSpotlightViewModel> SpotlightCandidates => _spotlightCandidates;
    public ObservableCollection<HomeProfileTileViewModel> RecentlyActive => _recentlyActive;
    public ObservableCollection<HomeAttentionItem> NeedsAttention => _needsAttention;
    public ObservableCollection<HomeDiscoveryItem> Discovery => _discovery;
    public HomeDiscoveryItem? FeaturedConnection => _discovery.Count > 0 ? _discovery[0] : null;
    public ObservableCollection<HomeActivityItem> RecentActivity => _recentActivity;
    public ObservableCollection<ImportActivityItem> ActiveImports => _activeImports;
    public HomeVaultPulse VaultPulse { get => _vaultPulse; private set => SetProperty(ref _vaultPulse, value); }
    public HomeSpotlightViewModel? CurrentSpotlight => _spotlightIndex >= 0 && _spotlightIndex < _spotlightCandidates.Count ? _spotlightCandidates[_spotlightIndex] : null;

    public int SpotlightIndex
    {
        get => _spotlightIndex;
        private set
        {
            if (SetProperty(ref _spotlightIndex, value))
            {
                RaisePropertyChanged(nameof(CurrentSpotlight));
            }
        }
    }

    public bool HasMultipleSpotlightCandidates => _spotlightCandidates.Count > 1;
    public bool IsLibraryEmpty => _spotlightCandidates.Count == 0 && _recentlyActive.Count == 0;
    public ICommand NextSpotlightCommand { get; }
    public ICommand PreviousSpotlightCommand { get; }

    public async Task RefreshAsync()
    {
        if (_catalog is null) return;
        var token = _loadGuard.Begin();
        if (_spotlightCandidates.Count == 0 && _recentlyActive.Count == 0)
        {
            ShowLoading();
        }
        else
        {
            BeginBackgroundUpdate();
        }

        try
        {
            var cancellation = token.CancellationToken;
            var spotlightTask = LoadHomeSectionAsync(
                "Spotlight",
                () => _catalog.GalleryReads.GetSpotlightCandidatesAsync(MaxSpotlightCandidates, cancellation));
            var recentTask = LoadHomeSectionAsync(
                "Recently active",
                () => _catalog.GalleryReads.GetGalleryPageAsync(
                    new GalleryQuery(
                        SortOrder: GallerySortOrder.UpdatedAtDesc,
                        PageSize: RecentlyActiveCount,
                        KindFilter: GalleryProfileKindFilter.NormalOnly),
                    cancellation));
            var healthTask = LoadHomeSectionAsync(
                "Vault health",
                () => _catalog.HealthReads.GetHealthSummaryAsync(cancellation));
            var actionableFaceTask = LoadHomeSectionAsync(
                "Actionable face review",
                () => _catalog.HealthReads.GetActionableFaceReviewCountAsync(cancellation));
            var unitsTask = LoadHomeSectionAsync(
                "Import attention",
                () => _importReads is null
                    ? Task.FromResult<IReadOnlyList<ImportUnitSummary>>([])
                    : _importReads.ListRecentUnitsAsync(50, cancellation));
            var featuredTask = LoadHomeSectionAsync(
                "Featured connection",
                () => _catalog.RelatedReads.GetFeaturedConnectionAsync(cancellation));

            var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
            var startOfMonthLocal = new DateTimeOffset(
                localNow.Year,
                localNow.Month,
                1,
                0,
                0,
                0,
                localNow.Offset);
            var endOfMonthLocal = startOfMonthLocal.AddMonths(1);
            var chronicleTask = LoadHomeSectionAsync(
                "Monthly activity",
                () => _catalog.ActivityReads.GetMonthlyChronicleSummaryAsync(
                    startOfMonthLocal.ToUniversalTime(),
                    endOfMonthLocal.ToUniversalTime(),
                    cancellation));

            await Task.WhenAll(
                    spotlightTask,
                    recentTask,
                    healthTask,
                    actionableFaceTask,
                    unitsTask,
                    featuredTask,
                    chronicleTask)
                .ConfigureAwait(false);

            if (!_loadGuard.IsCurrent(token)) return;

            var spotlight = spotlightTask.Result;
            var recent = recentTask.Result;
            var health = healthTask.Result;
            var actionableFaceCount = actionableFaceTask.Result;
            var units = unitsTask.Result;
            var featured = featuredTask.Result;
            var chronicle = chronicleTask.Result;
            var failures = new List<string>();

            if (!spotlight.IsSuccess) failures.Add(spotlight.Section);
            if (!recent.IsSuccess) failures.Add(recent.Section);
            if (!health.IsSuccess) failures.Add(health.Section);
            if (!units.IsSuccess) failures.Add(units.Section);
            if (!featured.IsSuccess) failures.Add(featured.Section);
            if (!chronicle.IsSuccess) failures.Add(chronicle.Section);

            SpotlightResolution? spotlightVisuals = null;
            RecentCoverResolution? recentVisuals = null;
            if (spotlight.IsSuccess)
            {
                spotlightVisuals = await ResolveSpotlightVisualsAsync(
                        spotlight.Value!,
                        cancellation)
                    .ConfigureAwait(false);
                if (spotlightVisuals.Degraded)
                {
                    failures.Add("Spotlight media");
                }
            }

            if (recent.IsSuccess)
            {
                recentVisuals = await ResolveRecentCoverPathsAsync(
                        recent.Value!,
                        cancellation)
                    .ConfigureAwait(false);
                if (recentVisuals.Degraded)
                {
                    failures.Add("Recent media");
                }
            }

            if (!_loadGuard.IsCurrent(token)) return;

            var appliedSections = 0;
            UiDispatch.Run(() =>
            {
                if (spotlight.IsSuccess)
                {
                    ApplySpotlight(spotlightVisuals?.Items ?? []);
                    appliedSections++;
                }

                if (recent.IsSuccess)
                {
                    ApplyRecentlyActive(recent.Value!, recentVisuals?.CoverPaths);
                    appliedSections++;
                }

                if (health.IsSuccess)
                {
                    ApplyVaultPulse(health.Value!);
                    appliedSections++;
                }

                if (health.IsSuccess && units.IsSuccess)
                {
                    ApplyNeedsAttention(units.Value!, health.Value!, actionableFaceCount.Value);
                    appliedSections++;
                }

                if (featured.IsSuccess)
                {
                    ApplyFeaturedConnection(featured.Value);
                    appliedSections++;
                }

                if (chronicle.IsSuccess)
                {
                    ApplyChronicle(chronicle.Value!);
                    appliedSections++;
                }

                ReplaceRefreshFailures(failures);
                if (appliedSections > 0)
                {
                    ShowReady();
                }
                else
                {
                    ShowRecoverableError(
                        SurfaceText.Get("Home.Error.Load", "Home could not be loaded."));
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (_loadGuard.IsCurrent(token))
            {
                Trace.TraceWarning("Home load coordinator failed: {0}", exception.GetType().Name);
                UiDispatch.Run(() =>
                {
                    SetRefreshFailure("Home coordinator", failed: true);
                    if (_spotlightCandidates.Count > 0
                        || _recentlyActive.Count > 0
                        || _discovery.Count > 0
                        || _recentActivity.Count > 0
                        || _activeImports.Count > 0)
                    {
                        ShowReady();
                    }
                    else
                    {
                        ShowRecoverableError(
                            SurfaceText.Get("Home.Error.Load", "Home could not be loaded."));
                    }
                });
            }
        }
        finally
        {
            if (_loadGuard.IsCurrent(token))
            {
                UiDispatch.Run(() => EndBackgroundUpdate());
            }
        }
    }

    private static async Task<HomeSectionLoad<T>> LoadHomeSectionAsync<T>(
        string section,
        Func<Task<T>> load)
    {
        try
        {
            return HomeSectionLoad<T>.Succeeded(section, await load().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "Home section {0} failed: {1}",
                section,
                exception.GetType().Name);
            return HomeSectionLoad<T>.Failed(section, exception.GetType().Name);
        }
    }

    private async Task<SpotlightResolution> ResolveSpotlightVisualsAsync(
        IReadOnlyList<HomeSpotlightCandidateReadModel> candidates,
        CancellationToken cancellationToken)
    {
        if (_catalog is null)
        {
            return new SpotlightResolution([], false);
        }

        var resolved = new List<(HomeSpotlightCandidateReadModel Candidate, string? BannerPath, string? CoverPath, string? BannerVideoPath)>(candidates.Count);
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selectedBanner = await ResolveSelectedBannerAsync(item.Summary.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            var cover = await ResolveHomeCoverAsync(item.Summary.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            var bannerVideo = selectedBanner?.Role == MediaAssetRole.Hover
                ? ResolveMediaAssetPath(selectedBanner)
                : null;
            string? bannerPath = null;
            if (selectedBanner?.Role == MediaAssetRole.Hover && _mediaResources is not null)
            {
                var thumbnail = await _mediaResources.ResolveSelectedProfileBannerThumbnailAsync(
                    item.Summary.ProfileId, selectedBanner.MediaAssetId, cancellationToken).ConfigureAwait(false);
                bannerPath = thumbnail.State == MediaAssetResourceState.Ready ? thumbnail.PhysicalPath : null;
            }
            resolved.Add((item, bannerPath, cover, bannerVideo));
        }

        return new SpotlightResolution(resolved, false);
    }

    private async Task<RecentCoverResolution> ResolveRecentCoverPathsAsync(
        GalleryPage page,
        CancellationToken cancellationToken)
    {
        if (_catalog is null)
        {
            return new RecentCoverResolution(new Dictionary<Guid, string?>(), false);
        }

        var covers = new Dictionary<Guid, string?>();
        foreach (var summary in page.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!covers.ContainsKey(summary.ProfileId))
            {
                covers[summary.ProfileId] = await ResolveHomeCoverAsync(
                    summary.ProfileId, cancellationToken).ConfigureAwait(false);
            }
        }

        var degraded = false;
        return new RecentCoverResolution(covers, degraded);
    }

    private void ReplaceRefreshFailures(IEnumerable<string> failures)
    {
        _failedRefreshSections.Clear();
        foreach (var failure in failures.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            _failedRefreshSections.Add(failure);
        }

        RefreshNotice = BuildRefreshNotice(_failedRefreshSections);
    }

    private void SetRefreshFailure(string section, bool failed)
    {
        if (failed)
        {
            _failedRefreshSections.Add(section);
        }
        else
        {
            _failedRefreshSections.Remove(section);
        }

        RefreshNotice = BuildRefreshNotice(_failedRefreshSections);
    }

    private static string? BuildRefreshNotice(IReadOnlyCollection<string> failures)
    {
        if (failures.Count == 0)
        {
            return null;
        }

        var distinct = failures
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinct.Length == 0)
        {
            return null;
        }

        var shown = distinct.Take(3).ToArray();
        var suffix = distinct.Length > shown.Length
            ? SurfaceText.Format(
                "Home.Refresh.MoreFailures",
                " +{0} more",
                distinct.Length - shown.Length)
            : string.Empty;
        return SurfaceText.Format(
            "Home.Refresh.PartialFailure.Detail",
            "Some Home sections could not refresh: {0}{1}.",
            string.Join(", ", shown),
            suffix);
    }

    public static IReadOnlyList<GalleryProfileSummary> RankSpotlightCandidates(IEnumerable<GalleryProfileSummary> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(candidate => candidate.Kind == ProfileKind.Normal)
            .OrderByDescending(candidate => candidate.CoverMediaId.HasValue || candidate.BannerMediaId.HasValue)
            .ThenByDescending(candidate => candidate.IsFavorite)
            .ThenByDescending(candidate => candidate.Rating.HasValue)
            .ThenByDescending(candidate => candidate.Rating ?? -1)
            .ThenByDescending(candidate => candidate.UpdatedAtUtc)
            .ThenBy(candidate => candidate.ProfileId)
            .Take(MaxSpotlightCandidates)
            .ToList();
    }

    public void ApplySpotlight(IReadOnlyList<HomeSpotlightCandidateReadModel> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var resolved = candidates
            .Select(static item => (Candidate: item, BannerPath: (string?)null, CoverPath: (string?)null, BannerVideoPath: (string?)null))
            .ToList();
        ApplySpotlight(resolved);
    }

    public void ApplySpotlight(IReadOnlyList<(HomeSpotlightCandidateReadModel Candidate, string? BannerPath, string? CoverPath, string? BannerVideoPath)> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        var previousId = CurrentSpotlight?.ProfileId;
        _spotlightCandidates.Clear();
        foreach (var (item, bannerPath, coverPath, bannerVideoPath) in resolved)
        {
            _spotlightCandidates.Add(new HomeSpotlightViewModel(item.Summary)
            {
                BannerImagePath = bannerPath,
                CoverImagePath = coverPath,
                BannerVideoPath = bannerVideoPath,
                OverviewExcerpt = item.OverviewExcerpt,
            });
        }

        if (_spotlightCandidates.Count == 0) SpotlightIndex = -1;
        else if (previousId.HasValue)
        {
            var matchedIndex = -1;
            for (var i = 0; i < _spotlightCandidates.Count; i++)
            {
                if (_spotlightCandidates[i].ProfileId == previousId.Value) { matchedIndex = i; break; }
            }
            SpotlightIndex = matchedIndex >= 0 ? matchedIndex : 0;
        }
        else SpotlightIndex = 0;

        RaisePropertyChanged(nameof(HasMultipleSpotlightCandidates));
        (NextSpotlightCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PreviousSpotlightCommand as RelayCommand)?.RaiseCanExecuteChanged();
        RaisePropertyChanged(nameof(CurrentSpotlight));
        RaisePropertyChanged(nameof(IsLibraryEmpty));
    }

    public void ApplySpotlight(GalleryPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ApplySpotlight(RankSpotlightCandidates(page.Items).Select(s => new HomeSpotlightCandidateReadModel(s, null)).ToList());
    }

    public void ApplyRecentlyActive(GalleryPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ApplyRecentlyActive(page, null);
    }

    public void ApplyRecentlyActive(GalleryPage page, Dictionary<Guid, string?>? preResolvedCovers)
    {
        ArgumentNullException.ThrowIfNull(page);
        _recentlyActive.Clear();
        foreach (var summary in page.Items.Where(item => item.Kind == ProfileKind.Normal).OrderByDescending(item => item.UpdatedAtUtc).ThenBy(item => item.ProfileId).Take(RecentlyActiveCount))
        {
            string? coverPath = null;
            if (preResolvedCovers is not null) preResolvedCovers.TryGetValue(summary.ProfileId, out coverPath);
            _recentlyActive.Add(new HomeProfileTileViewModel(summary) { CoverImagePath = coverPath });
        }
        RaisePropertyChanged(nameof(IsLibraryEmpty));
    }

    private async Task<string?> ResolveHomeCoverAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        if (_mediaResources is null)
        {
            return null;
        }

        try
        {
            var resource = await _mediaResources.ResolveSelectedProfileCoverAsync(profileId, cancellationToken)
                .ConfigureAwait(false);
            return resource.State == MediaAssetResourceState.Ready ? resource.PhysicalPath : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Home cover resolution failed: {0}", exception.GetType().Name);
            return null;
        }
    }

    private string? ResolveMediaAssetPath(MediaAssetRecord? asset)
    {
        if (_catalog is null || asset is null || asset.State != MediaAssetState.Ready)
        {
            return null;
        }

        try
        {
            return _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, asset.RelativePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Home MediaAsset path resolution failed: {0}", exception.GetType().Name);
            return null;
        }
    }

    private async Task<MediaAssetRecord?> ResolveSelectedBannerAsync(
        Guid profileId, CancellationToken cancellationToken)
    {
        if (_catalog is null) return null;
        var selection = await _catalog.MediaAssetReads.GetPublicSelectionAsync(profileId, cancellationToken)
            .ConfigureAwait(false);
        return selection?.BannerMediaAssetId is { } id
            ? await _catalog.MediaAssetReads.GetPublicSelectedMediaAsync(profileId, id, cancellationToken)
                .ConfigureAwait(false)
            : null;
    }

    public void ApplyNeedsAttention(
        IReadOnlyList<ImportUnitSummary> units,
        LibraryHealthSummary health,
        int? actionableFaceReviewCount = null)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(health);
        _needsAttention.Clear();
        var allCandidates = new List<HomeAttentionItem>();
        foreach (var unit in units.Where(unit => unit.State == ImportUnitState.ReadyForVerification))
        {
            allCandidates.Add(new HomeAttentionItem(HomeAttentionKind.VerificationReady,
                SurfaceText.Get("Home.Attention.Verification.Title", "Waiting for your choices"),
                SurfaceText.Format("Home.Attention.Verification.Detail", "{0} — choose who these {1} media are for.", unit.SourceDisplayName, unit.TotalItemCount), new ImportRoute()));
        }
        foreach (var unit in units.Where(unit => unit.State is ImportUnitState.FailedRetryable or ImportUnitState.FailedTerminal))
        {
            allCandidates.Add(new HomeAttentionItem(HomeAttentionKind.ImportFailure,
                SurfaceText.Get("Home.Attention.Import.Title", "Import needs attention"),
                SurfaceText.Format("Home.Attention.Import.Detail", "{0} stopped before finishing. You can try it again from Import.", unit.SourceDisplayName), new ImportRoute()));
        }
        foreach (var unit in units.Where(unit => unit.CleanupFailedCount > 0))
        {
            allCandidates.Add(new HomeAttentionItem(HomeAttentionKind.SourceCleanupFailure,
                SurfaceText.Get("Home.Attention.Cleanup.Title", "Previous import needs attention"),
                SurfaceText.Format("Home.Attention.Cleanup.Detail", "{0} is in your Vault, but an older import record still needs review.", unit.SourceDisplayName), new ImportRoute()));
        }
        if (health.NeedsAttentionReconciliationCount > 0)
        {
            allCandidates.Add(new HomeAttentionItem(HomeAttentionKind.PathReconciliationFailure,
                SurfaceText.Get("Home.Attention.Reconciliation.Title", "Some files need attention"),
                SurfaceText.Format("Home.Attention.Reconciliation.Detail", "{0} item(s) could not be filed into their final place in your Vault.", health.NeedsAttentionReconciliationCount), new VaultHealthRoute()));
        }
        var faceReviewCount = actionableFaceReviewCount ?? health.UnresolvedFaceDetections;
        if (faceReviewCount > 0)
        {
            allCandidates.Add(new HomeAttentionItem(HomeAttentionKind.FaceReviewPending,
                SurfaceText.Get("Home.Attention.Faces.Title", "People to confirm"),
                SurfaceText.Format("Home.Attention.Faces.Detail", "{0} face(s) are waiting for you to say who they are.", faceReviewCount), new FaceReviewRoute()));
        }
        foreach (var item in allCandidates.Take(MaxAttentionItems)) _needsAttention.Add(item);
    }

    public void ApplyFeaturedConnection(FeaturedConnectionReadModel? connection)
    {
        _discovery.Clear();
        if (connection is not null)
        {
            var evidence = DescribeFeaturedEvidence(connection);
            if (!string.IsNullOrWhiteSpace(evidence)) _discovery.Add(new HomeDiscoveryItem(connection.ProfileIdLow, connection.LowDisplayName, connection.HighDisplayName, evidence));
        }
        RaisePropertyChanged(nameof(FeaturedConnection));
    }

    public static string? DescribeFeaturedEvidence(FeaturedConnectionReadModel connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var parts = new List<string>();
        if (connection.SharedMediaCount > 0) parts.Add(connection.SharedMediaCount == 1 ? SurfaceText.Get("Home.Evidence.Shared.One", "1 shared media item") : SurfaceText.Format("Home.Evidence.Shared.Many", "{0} shared media items", connection.SharedMediaCount));
        if (connection.ConfirmedFaceCount > 0) parts.Add(connection.ConfirmedFaceCount == 1 ? SurfaceText.Get("Home.Evidence.Face.One", "1 confirmed face association") : SurfaceText.Format("Home.Evidence.Face.Many", "{0} confirmed face associations", connection.ConfirmedFaceCount));
        if (connection.ManualRelation) parts.Add(SurfaceText.Get("Home.Evidence.Manual", "a manual relation"));
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    public void ApplyChronicle(HomeChronicleReadModel chronicle)
    {
        ArgumentNullException.ThrowIfNull(chronicle);
        _recentActivity.Clear();
        if (chronicle.MediaCommittedCount > 0) _recentActivity.Add(new HomeActivityItem(chronicle.MediaCommittedCount == 1 ? SurfaceText.Get("Home.Chronicle.Media.One", "1 media added this month") : SurfaceText.Format("Home.Chronicle.Media.Many", "{0} media added this month", chronicle.MediaCommittedCount), chronicle.StartOfMonthUtc));
        if (chronicle.DurableRelatedPairsCount > 0) _recentActivity.Add(new HomeActivityItem(chronicle.DurableRelatedPairsCount == 1 ? SurfaceText.Get("Home.Chronicle.Connection.One", "1 evidence-backed profile connection formed or updated") : SurfaceText.Format("Home.Chronicle.Connection.Many", "{0} evidence-backed profile connections formed or updated", chronicle.DurableRelatedPairsCount), chronicle.StartOfMonthUtc));
        if (chronicle.FacesConfirmedCount > 0) _recentActivity.Add(new HomeActivityItem(chronicle.FacesConfirmedCount == 1 ? SurfaceText.Get("Home.Chronicle.Face.One", "1 face confirmed this month") : SurfaceText.Format("Home.Chronicle.Face.Many", "{0} faces confirmed this month", chronicle.FacesConfirmedCount), chronicle.StartOfMonthUtc));
        if (chronicle.ProfilesCreatedCount > 0) _recentActivity.Add(new HomeActivityItem(chronicle.ProfilesCreatedCount == 1 ? SurfaceText.Get("Home.Chronicle.Profile.One", "1 profile created this month") : SurfaceText.Format("Home.Chronicle.Profile.Many", "{0} profiles created this month", chronicle.ProfilesCreatedCount), chronicle.StartOfMonthUtc));
    }

    public void ApplyVaultPulse(LibraryHealthSummary health)
    {
        ArgumentNullException.ThrowIfNull(health);
        VaultPulse = new HomeVaultPulse(health.TotalProfiles, health.ActiveMedias, FormatBytes(health.ActiveManagedByteLength), DescribeHealth(health));
    }

    public void MoveSpotlight(int delta)
    {
        if (_spotlightCandidates.Count == 0) { SpotlightIndex = -1; return; }
        var count = _spotlightCandidates.Count;
        SpotlightIndex = ((_spotlightIndex + delta) % count + count) % count;
    }

    public static string DescribeHealth(LibraryHealthSummary health)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (health.NeedsAttentionReconciliationCount > 0) return SurfaceText.Get("Library.Condition.NeedsAttention", "Needs attention");
        if (health.PendingReconciliationCount > 0 || health.ActiveJobsCount > 0) return SurfaceText.Get("Library.Condition.Working", "Working");
        return SurfaceText.Get("Library.Condition.AllGood", "All good");
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B") : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    private void OnImportActivityChanged(object? sender, ImportActivitySnapshot snapshot) => ApplyImportActivity(snapshot);

    private void ApplyImportActivity(ImportActivitySnapshot snapshot)
    {
        if (_disposed) return;

        var next = snapshot.Items
            .Where(item => item.IsActive || item.CanContinue)
            .Take(3)
            .ToArray();

        // Home only renders import identity + stage. Progress-only ticks must not churn the
        // ObservableCollection because HomeSurface rebuilds its rail when membership/visible
        // stage changes. Suppressing equivalent snapshots keeps the Home visual tree stable.
        var visiblyEquivalent = _activeImports.Count == next.Length;
        if (visiblyEquivalent)
        {
            for (var index = 0; index < next.Length; index++)
            {
                var current = _activeImports[index];
                var candidate = next[index];
                if (current.UnitId != candidate.UnitId
                    || current.SourceDisplayName != candidate.SourceDisplayName
                    || current.Stage != candidate.Stage
                    || current.StageText != candidate.StageText
                    || current.IsPriority != candidate.IsPriority
                    || current.CanContinue != candidate.CanContinue)
                {
                    visiblyEquivalent = false;
                    break;
                }
            }
        }

        if (visiblyEquivalent)
        {
            return;
        }

        _activeImports.Clear();
        foreach (var item in next)
        {
            _activeImports.Add(item);
        }

    }

    private void OnCatalogInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        if (_disposed) return;
        switch (invalidation.DomainKind)
        {
            case CatalogInvalidationDomain.Profile:
            case CatalogInvalidationDomain.Category:
            case CatalogInvalidationDomain.Tag:
            case CatalogInvalidationDomain.Appearance:
                SignalCoalescer(ref _galleryCoalescer, RefreshGalleryAsync); break;
            case CatalogInvalidationDomain.Health:
            case CatalogInvalidationDomain.Import:
                SignalCoalescer(ref _attentionCoalescer, RefreshAttentionAsync); break;
            case CatalogInvalidationDomain.Related:
                SignalCoalescer(ref _relatedCoalescer, RefreshRelatedAsync); break;
            case CatalogInvalidationDomain.Activity:
                SignalCoalescer(ref _chronicleCoalescer, RefreshChronicleAsync); break;
        }
    }

    private void SignalCoalescer(ref RefreshCoalescer? field, Func<CancellationToken, Task> action)
    {
        if (_disposed) return;
        field ??= new RefreshCoalescer(ct => _disposed ? Task.CompletedTask : action(ct), TimeSpan.FromMilliseconds(600));
        var coalescer = field;
        UiDispatch.Run(coalescer.Signal);
    }

    private async Task RefreshGalleryAsync(CancellationToken cancellationToken)
    {
        if (_catalog is null) return;

        var spotlightTask = LoadHomeSectionAsync(
            "Spotlight",
            () => _catalog.GalleryReads.GetSpotlightCandidatesAsync(
                MaxSpotlightCandidates,
                cancellationToken));
        var recentTask = LoadHomeSectionAsync(
            "Recently active",
            () => _catalog.GalleryReads.GetGalleryPageAsync(
                new GalleryQuery(
                    SortOrder: GallerySortOrder.UpdatedAtDesc,
                    PageSize: RecentlyActiveCount,
                    KindFilter: GalleryProfileKindFilter.NormalOnly),
                cancellationToken));

        await Task.WhenAll(spotlightTask, recentTask).ConfigureAwait(false);
        if (_disposed) return;

        var spotlight = spotlightTask.Result;
        var recent = recentTask.Result;
        SpotlightResolution? spotlightVisuals = null;
        RecentCoverResolution? recentVisuals = null;

        if (spotlight.IsSuccess)
        {
            spotlightVisuals = await ResolveSpotlightVisualsAsync(
                    spotlight.Value!,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (recent.IsSuccess)
        {
            recentVisuals = await ResolveRecentCoverPathsAsync(
                    recent.Value!,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (_disposed) return;
        UiDispatch.Run(() =>
        {
            if (spotlight.IsSuccess)
            {
                ApplySpotlight(spotlightVisuals?.Items ?? []);
            }

            if (recent.IsSuccess)
            {
                ApplyRecentlyActive(recent.Value!, recentVisuals?.CoverPaths);
            }

            SetRefreshFailure("Spotlight", !spotlight.IsSuccess);
            SetRefreshFailure(
                "Spotlight media",
                spotlight.IsSuccess && spotlightVisuals?.Degraded == true);
            SetRefreshFailure("Recently active", !recent.IsSuccess);
            SetRefreshFailure(
                "Recent media",
                recent.IsSuccess && recentVisuals?.Degraded == true);
        });
    }

    private async Task RefreshAttentionAsync(CancellationToken cancellationToken)
    {
        if (_catalog is null) return;

        var healthTask = LoadHomeSectionAsync(
            "Vault health",
            () => _catalog.HealthReads.GetHealthSummaryAsync(cancellationToken));
        var actionableFaceTask = LoadHomeSectionAsync(
            "Actionable face review",
            () => _catalog.HealthReads.GetActionableFaceReviewCountAsync(cancellationToken));
        var unitsTask = LoadHomeSectionAsync(
            "Import attention",
            () => _importReads is null
                ? Task.FromResult<IReadOnlyList<ImportUnitSummary>>([])
                : _importReads.ListRecentUnitsAsync(50, cancellationToken));

        await Task.WhenAll(healthTask, actionableFaceTask, unitsTask).ConfigureAwait(false);
        if (_disposed) return;

        var health = healthTask.Result;
        var actionableFaceCount = actionableFaceTask.Result;
        var units = unitsTask.Result;
        UiDispatch.Run(() =>
        {
            if (health.IsSuccess)
            {
                ApplyVaultPulse(health.Value!);
            }

            if (health.IsSuccess && actionableFaceCount.IsSuccess && units.IsSuccess)
            {
                ApplyNeedsAttention(units.Value!, health.Value!, actionableFaceCount.Value);
            }

            SetRefreshFailure("Vault health", !health.IsSuccess);
            SetRefreshFailure("Actionable face review", !actionableFaceCount.IsSuccess);
            SetRefreshFailure("Import attention", !units.IsSuccess);
        });
    }

    private async Task RefreshRelatedAsync(CancellationToken cancellationToken)
    {
        if (_catalog is null) return;

        var result = await LoadHomeSectionAsync(
                "Featured connection",
                () => _catalog.RelatedReads.GetFeaturedConnectionAsync(cancellationToken))
            .ConfigureAwait(false);
        if (_disposed) return;

        UiDispatch.Run(() =>
        {
            if (result.IsSuccess)
            {
                ApplyFeaturedConnection(result.Value);
            }

            SetRefreshFailure("Featured connection", !result.IsSuccess);
        });
    }

    private async Task RefreshChronicleAsync(CancellationToken cancellationToken)
    {
        if (_catalog is null) return;

        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        var start = new DateTimeOffset(
            localNow.Year,
            localNow.Month,
            1,
            0,
            0,
            0,
            localNow.Offset);
        var result = await LoadHomeSectionAsync(
                "Monthly activity",
                () => _catalog.ActivityReads.GetMonthlyChronicleSummaryAsync(
                    start.ToUniversalTime(),
                    start.AddMonths(1).ToUniversalTime(),
                    cancellationToken))
            .ConfigureAwait(false);
        if (_disposed) return;

        UiDispatch.Run(() =>
        {
            if (result.IsSuccess)
            {
                ApplyChronicle(result.Value!);
            }

            SetRefreshFailure("Monthly activity", !result.IsSuccess);
        });
    }

    private sealed record HomeSectionLoad<T>(
        string Section,
        bool IsSuccess,
        T? Value,
        string? ErrorType)
    {
        public static HomeSectionLoad<T> Succeeded(string section, T value) =>
            new(section, true, value, null);

        public static HomeSectionLoad<T> Failed(string section, string errorType) =>
            new(section, false, default, errorType);
    }

    private sealed record SpotlightResolution(
        IReadOnlyList<(HomeSpotlightCandidateReadModel Candidate, string? BannerPath, string? CoverPath, string? BannerVideoPath)> Items,
        bool Degraded);

    private sealed record RecentCoverResolution(
        Dictionary<Guid, string?> CoverPaths,
        bool Degraded);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _galleryCoalescer?.Dispose();
        _attentionCoalescer?.Dispose();
        _relatedCoalescer?.Dispose();
        _chronicleCoalescer?.Dispose();
        if (_activity is not null) _activity.Changed -= OnImportActivityChanged;
        if (_catalog is not null) _catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidated;
        _loadGuard.Dispose();
    }
}

public sealed class HomeSpotlightViewModel
{
    public HomeSpotlightViewModel(GalleryProfileSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ProfileId = summary.ProfileId; DisplayName = summary.DisplayName; CategoryName = summary.CategoryName; Tags = summary.Tags; Rating = summary.Rating; IsFavorite = summary.IsFavorite; MediaCount = summary.ActiveOwnedMediaCount;
    }
    public Guid ProfileId { get; }
    public string DisplayName { get; }
    public string? CategoryName { get; }
    public IReadOnlyList<string> Tags { get; }
    public int? Rating { get; }
    public bool IsFavorite { get; }
    public long MediaCount { get; }
    public string? OverviewExcerpt { get; init; }
    public string? BannerImagePath { get; init; }
    public string? BannerVideoPath { get; init; }
    public bool HasBannerVideo => !string.IsNullOrWhiteSpace(BannerVideoPath);
    public string? CoverImagePath { get; init; }
    public string? HeroImagePath => !string.IsNullOrWhiteSpace(BannerImagePath) ? BannerImagePath : !string.IsNullOrWhiteSpace(CoverImagePath) ? CoverImagePath : null;
    public string MediaCountText => MediaCount == 1 ? SurfaceText.Get("Media.Count.One", "1 item") : SurfaceText.Format("Media.Count.Many", "{0} items", MediaCount);
    public string TagSummary => Tags.Count == 0 ? string.Empty : string.Join(" · ", Tags.Take(4));
}

public sealed class HomeProfileTileViewModel
{
    public HomeProfileTileViewModel(GalleryProfileSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ProfileId = summary.ProfileId; DisplayName = summary.DisplayName; MediaCount = summary.ActiveOwnedMediaCount; IsFavorite = summary.IsFavorite;
    }
    public Guid ProfileId { get; }
    public string DisplayName { get; }
    public string? CoverImagePath { get; init; }
    public long MediaCount { get; }
    public bool IsFavorite { get; }
    public string MediaCountText => MediaCount == 1 ? SurfaceText.Get("Media.Count.One", "1 item") : SurfaceText.Format("Media.Count.Many", "{0} items", MediaCount);
}

public enum HomeAttentionKind { VerificationReady, ImportFailure, SourceCleanupFailure, PathReconciliationFailure, FaceReviewPending }
public sealed record HomeAttentionItem(HomeAttentionKind Kind, string Title, string Detail, AppRoute? Route);
public sealed record HomeDiscoveryItem(Guid ProfileId, string ProfileDisplayName, string RelatedDisplayName, string EvidenceSummary);

public sealed record HomeActivityItem(string Description, DateTimeOffset OccurredAtUtc)
{
    public string RelativeTimeText => DescribeRelative(OccurredAtUtc, DateTimeOffset.UtcNow);
    public static string DescribeRelative(DateTimeOffset moment, DateTimeOffset now)
    {
        var elapsed = now - moment; if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed < TimeSpan.FromMinutes(1)) return SurfaceText.Get("Time.JustNow", "Just now");
        if (elapsed < TimeSpan.FromHours(1)) { var minutes = (int)elapsed.TotalMinutes; return minutes == 1 ? SurfaceText.Get("Time.OneMinute", "1 minute ago") : SurfaceText.Format("Time.Minutes", "{0} minutes ago", minutes); }
        if (elapsed < TimeSpan.FromDays(1)) { var hours = (int)elapsed.TotalHours; return hours == 1 ? SurfaceText.Get("Time.OneHour", "1 hour ago") : SurfaceText.Format("Time.Hours", "{0} hours ago", hours); }
        if (elapsed < TimeSpan.FromDays(30)) { var days = (int)elapsed.TotalDays; return days == 1 ? SurfaceText.Get("Time.Yesterday", "Yesterday") : SurfaceText.Format("Time.Days", "{0} days ago", days); }
        return TimeZoneInfo.ConvertTime(moment, TimeZoneInfo.Local).ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
    }
}

public sealed record HomeVaultPulse(long ActiveProfileCount, long ActiveMediaCount, string StorageSummary, string HealthState)
{
    public static HomeVaultPulse Unknown { get; } = new(0, 0, "-", "-");
}
