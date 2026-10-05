using Neuterradise.App.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Neuterradise.App.Activity;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Maintenance;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.MediaTools;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Updates;
using Neuterradise.App.SystemServices;
using Neuterradise.App.Trash;

using Neuterradise.App.SystemServices.Database.Reads;

using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Settings;

public sealed class CategoryItemViewModel : ObservableObject
{
    private string _name;
    private int _usageCount;
    private string _editName = string.Empty;

    public CategoryItemViewModel(string categoryId, string name, int usageCount, long rowVersion)
    {
        CategoryId = categoryId;
        _name = name;
        _usageCount = usageCount;
        RowVersion = rowVersion;
    }

    public string CategoryId { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public int UsageCount
    {
        get => _usageCount;
        set => SetProperty(ref _usageCount, value);
    }

    public long RowVersion { get; set; }

    public string EditName
    {
        get => _editName;
        set => SetProperty(ref _editName, value);
    }
}

public sealed class TagItemViewModel : ObservableObject
{
    private string _name;
    private int _usageCount;
    private string _editName = string.Empty;

    public TagItemViewModel(string tagId, string name, int usageCount, long rowVersion)
    {
        TagId = tagId;
        _name = name;
        _usageCount = usageCount;
        RowVersion = rowVersion;
    }

    public string TagId { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public int UsageCount
    {
        get => _usageCount;
        set => SetProperty(ref _usageCount, value);
    }

    public long RowVersion { get; set; }

    public string EditName
    {
        get => _editName;
        set => SetProperty(ref _editName, value);
    }
}

public sealed class SettingsRailItem : ObservableObject
{
    private readonly string _titleKey;
    private readonly string _defaultTitle;
    private readonly string _descriptionKey;
    private readonly string _defaultDescription;

    public SettingsRailItem(
        SettingsSection key,
        string titleKey,
        string defaultTitle,
        string descriptionKey,
        string defaultDescription)
    {
        Key = key;
        _titleKey = titleKey;
        _defaultTitle = defaultTitle;
        _descriptionKey = descriptionKey;
        _defaultDescription = defaultDescription;

        SurfaceText.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RaisePropertyChanged(nameof(Title));
        RaisePropertyChanged(nameof(Description));
    }

    /// <summary>
    /// Releases the static language subscription. Settings view models are created per visit; without
    /// this every visit left its rail items (and through them the page) reachable from a static event.
    /// </summary>
    public void Detach() => SurfaceText.LanguageChanged -= OnLanguageChanged;

    public SettingsRailItem(SettingsSection key, string title, string description)
        : this(key, "Settings." + key + ".Title", title, "Settings." + key + ".Desc", description)
    {
    }

    public SettingsSection Key { get; }
    public string Title => SurfaceText.Get(_titleKey, _defaultTitle);
    public string Description => SurfaceText.Get(_descriptionKey, _defaultDescription);
}

public sealed class SettingsViewModel : ScreenStateViewModel, IDisposable
{
    private readonly CatalogDb? _catalog;
    private readonly NavigationCoordinator? _navigation;
    private readonly SettingsReads? _reads;
    private readonly StorageMetricsProvider? _storageMetrics;
    private readonly CacheMaintenanceOperations? _cacheMaintenance;
    private readonly AppConfigurationStore? _configurationStore;
    private readonly UpdateCoordinator? _updateCoordinator;
    private DiagnosticVersionReport? _versionReport;
    private long _initializeGeneration;
    private bool _isDisposed;

    private SettingsSection _activeSection;
    private SettingsSubsection? _activeSubsection;
    private string _updateFeedUrl = string.Empty;
    private UpdatePresentationState _updateState = UpdatePresentationState.Idle;

    private string _newCategoryName = string.Empty;
    private string _newTagName = string.Empty;
    private string? _organizationMessage;
    private string? _organizationWarningMessage;

    private StorageMetricsSnapshot? _systemMetrics;
    private long _unresolvedFaceCount;

    public SettingsViewModel(
        SettingsSection section = SettingsSection.Appearance,
        SettingsSubsection? subsection = null,
        CatalogDb? catalog = null,
        NavigationCoordinator? navigation = null,
        StorageMetricsProvider? storageMetrics = null,
        CacheMaintenanceOperations? cacheMaintenance = null,
        AppConfigurationStore? configurationStore = null,
        UpdateCoordinator? updateCoordinator = null)
    {
        Section = section;
        Subsection = subsection;

        _activeSection = section;
        _activeSubsection = subsection;
        _catalog = catalog;
        _navigation = navigation;
        _configurationStore = configurationStore;
        _updateCoordinator = updateCoordinator;
        _updateState = updateCoordinator?.State ?? UpdatePresentationState.Idle;

        if (_catalog is not null)
        {
            _reads = _catalog.SettingsReads;
            _storageMetrics = storageMetrics ?? new StorageMetricsProvider(_catalog);

            var cachePaths = new CachePaths(_catalog.Paths);
            var cacheBudget = new PersistentCacheBudget(cachePaths);
            _cacheMaintenance = cacheMaintenance ?? new CacheMaintenanceOperations(cachePaths, cacheBudget);
        }
        else
        {
            _storageMetrics = storageMetrics;
            _cacheMaintenance = cacheMaintenance;
        }

        Activity = new ActivityViewModel(_catalog, _navigation);
        Trash = new TrashViewModel(_catalog);
        Health = new LibraryHealthViewModel(_catalog, _navigation, _storageMetrics, _cacheMaintenance);

        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated += OnCatalogInvalidated;
        }

        SelectLanguageCommand = new AsyncRelayCommand(async param =>
        {
            if (param is string lang && !string.IsNullOrWhiteSpace(lang))
            {
                await ApplyLanguageAsync(lang);
            }
        });

        SurfaceText.LanguageChanged += OnSurfaceLanguageChanged;

        CreateCategoryCommand = new AsyncRelayCommand(() => CreateCategoryAsync());
        CommitRenameCategoryCommand = new RelayCommand(param =>
        {
            if (param is CategoryItemViewModel item && !string.IsNullOrWhiteSpace(item.EditName))
            {
                TaskObserver.Observe(RenameCategoryAsync(item, item.EditName), "SettingsViewModel.RenameCategoryAsync");
            }
        });
        DeleteCategoryCommand = new RelayCommand(param =>
        {
            if (param is CategoryItemViewModel item)
            {
                TaskObserver.Observe(DeleteCategoryAsync(item), "SettingsViewModel.DeleteCategoryAsync");
            }
        });

        CreateTagCommand = new AsyncRelayCommand(() => CreateTagAsync());
        CommitRenameTagCommand = new RelayCommand(param =>
        {
            if (param is TagItemViewModel item && !string.IsNullOrWhiteSpace(item.EditName))
            {
                TaskObserver.Observe(RenameTagAsync(item, item.EditName), "SettingsViewModel.RenameTagAsync");
            }
        });
        DeleteTagCommand = new RelayCommand(param =>
        {
            if (param is TagItemViewModel item)
            {
                TaskObserver.Observe(DeleteTagAsync(item), "SettingsViewModel.DeleteTagAsync");
            }
        });

        SaveUpdateFeedCommand = new AsyncRelayCommand(
            SaveUpdateFeedAsync,
            () => _updateCoordinator is not null);
        CheckForUpdatesCommand = new AsyncRelayCommand(
            CheckForUpdatesAsync,
            () => _updateCoordinator is not null);
        InstallUpdateCommand = new AsyncRelayCommand(
            InstallUpdateAsync,
            () => CanInstallUpdate);

        if (_updateCoordinator is not null)
        {
            _updateCoordinator.StateChanged += OnUpdateStateChanged;
        }

        NavigateToFaceReviewCommand = new RelayCommand(_ => _navigation?.Navigate(new FaceReviewRoute()));

        StartRouteTask(InitializeAsync, SurfaceText.Get("Settings.LoadFailed", "Settings could not be loaded."));
        StartRouteTask(LoadThirdPartyNoticesAsync, SurfaceText.Get("Settings.ThirdPartyNotices.LoadFailed", "Third-party notices could not be loaded."));
        StartRouteTask(LoadReleaseInformationAsync, SurfaceText.Get("Settings.LoadFailed", "Settings could not be loaded."));
    }

    public SettingsSection Section { get; }

    public SettingsSubsection? Subsection { get; }

    public SettingsSection ActiveSection
    {
        get => _activeSection;
        set
        {
            if (SetProperty(ref _activeSection, value))
            {
                if (_activeSubsection is not null)
                {
                    _activeSubsection = null;
                    RaisePropertyChanged(nameof(ActiveSubsection));
                }

                LoadActiveSection();
            }
        }
    }

    public SettingsSubsection? ActiveSubsection
    {
        get => _activeSubsection;
        private set => SetProperty(ref _activeSubsection, value);
    }

    public void ApplyRoute(SettingsRoute route)
    {
        var sectionChanged = _activeSection != route.Section;
        var subsectionChanged = _activeSubsection != route.Subsection;
        if (!sectionChanged && !subsectionChanged)
        {
            LoadActiveSection();
            return;
        }

        _activeSection = route.Section;
        _activeSubsection = route.Subsection;
        if (sectionChanged)
        {
            RaisePropertyChanged(nameof(ActiveSection));
        }

        if (subsectionChanged)
        {
            RaisePropertyChanged(nameof(ActiveSubsection));
        }

        LoadActiveSection();
    }

    public IReadOnlyList<SettingsRailItem> RailItems { get; } =
    [
        new(SettingsSection.Appearance, "Settings.Appearance.Title", "Appearance", "Settings.Appearance.Desc", "Theme, typography and control appearance"),
        new(SettingsSection.Vault, "Settings.Vault.Title", "Vault", "Settings.Vault.Desc", "Vault health, identity, storage, and location"),
        new(SettingsSection.PeopleOrganization, "Settings.PeopleOrganization.Title", "People & Organization", "Settings.PeopleOrganization.Desc", "People review, categories, and tags"),
        new(SettingsSection.Activity, "Settings.Activity.Title", "Activity", "Settings.Activity.Desc", "What has happened in your Vault"),
        new(SettingsSection.Language, "Settings.Language.Title", "Language", "Settings.Language.Desc", "Choose the language used by naut."),
        new(SettingsSection.Presentation, "Settings.Presentation.Title", "Presentation", "Settings.Presentation.Desc", "Manage packs and presentation assets"),
        new(SettingsSection.Trash, "Settings.Trash.Title", "Trash Bin", "Settings.Trash.Desc", "Reversible staging and permanent purge"),
        new(SettingsSection.About, "Settings.About.Title", "About", "Settings.About.Desc", "Version, updates, and product information"),
    ];

    private void OnSurfaceLanguageChanged(object? sender, EventArgs e)
    {
        RunOnUi(() =>
        {
            CurrentLanguage = SurfaceText.CurrentLanguage;
            RaisePropertyChanged(nameof(UpdateStatusText));
        });
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        RetireRoute();
        _isDisposed = true;
        Interlocked.Increment(ref _initializeGeneration);

        // Settings is recreated on every visit; a static-event subscription left behind kept each old
        // page (with its Activity, Trash and Health view models) alive for the life of the process.
        SurfaceText.LanguageChanged -= OnSurfaceLanguageChanged;
        if (_updateCoordinator is not null)
        {
            _updateCoordinator.StateChanged -= OnUpdateStateChanged;
        }

        foreach (var railItem in RailItems)
        {
            railItem.Detach();
        }


        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidated;
        }

        _organizationCoalescer?.Dispose();
        Activity.Dispose();
        Trash.Dispose();
        Health.Dispose();
    }

    public ActivityViewModel Activity { get; }

    public TrashViewModel Trash { get; }

    public LibraryHealthViewModel Health { get; }

    private string _currentLanguage = SurfaceText.CurrentLanguage;

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set => SetProperty(ref _currentLanguage, value);
    }

    public IReadOnlyList<LanguageDefinition> AvailableLanguages => SurfaceText.SupportedLanguages;

    public bool IsLanguageSelected(string languageCode) =>
        string.Equals(CurrentLanguage, languageCode, StringComparison.OrdinalIgnoreCase);

    public ICommand SelectLanguageCommand { get; }

    /// <summary>
    /// The real vault root of the open session. When no writable catalog is open there is no vault, so
    /// this reports that truthfully instead of showing an invented example path.
    /// </summary>
    public string VaultRoot => _catalog?.Paths.Root ?? "No vault is open.";

    public string ManagedStorageFormatted => LibraryHealthViewModel.FormatBytes(_systemMetrics?.ManagedMediaBytes ?? 0);

    public long TotalProfilesCount => _systemMetrics?.ProfileCount ?? 0;

    public long ActiveMediaCount => _systemMetrics?.ActiveMediaCount ?? 0;

    public ObservableCollection<CategoryItemViewModel> Categories { get; } = [];

    public ObservableCollection<TagItemViewModel> Tags { get; } = [];

    public string NewCategoryName
    {
        get => _newCategoryName;
        set => SetProperty(ref _newCategoryName, value);
    }

    public string NewTagName
    {
        get => _newTagName;
        set => SetProperty(ref _newTagName, value);
    }

    public string? OrganizationMessage
    {
        get => _organizationMessage;
        set => SetProperty(ref _organizationMessage, value);
    }

    public string? OrganizationWarningMessage
    {
        get => _organizationWarningMessage;
        set => SetProperty(ref _organizationWarningMessage, value);
    }

    public long UnresolvedFaceCount
    {
        get => _unresolvedFaceCount;
        private set => SetProperty(ref _unresolvedFaceCount, value);
    }

    public string UpdateFeedUrl
    {
        get => _updateFeedUrl;
        set => _updateFeedUrl = value ?? string.Empty;
    }

    public string UpdateStatusText => _updateState.Status switch
    {
        UpdateCoordinator.StatusNotChecked => SurfaceText.Get("Settings.Update.Status.NotChecked", "Not checked"),
        UpdateCoordinator.StatusFeedSaved => SurfaceText.Get("Settings.Update.Status.FeedSaved", "Update feed saved"),
        UpdateCoordinator.StatusChecking => SurfaceText.Get("Settings.Update.Status.Checking", "Checking for updates…"),
        UpdateCoordinator.StatusAvailable => SurfaceText.Get("Settings.Update.Status.Available", "Update available"),
        UpdateCoordinator.StatusUnavailable => SurfaceText.Get("Settings.Update.Status.Unavailable", "No update available"),
        UpdateCoordinator.StatusDownloading => SurfaceText.Get("Settings.Update.Status.Downloading", "Downloading update…"),
        UpdateCoordinator.StatusStaging => SurfaceText.Get("Settings.Update.Status.Staging", "Validating update package…"),
        UpdateCoordinator.StatusPreparing => SurfaceText.Get("Settings.Update.Status.Preparing", "Starting updater…"),
        UpdateCoordinator.StatusRestarting => SurfaceText.Get("Settings.Update.Status.Restarting", "Updater started; naut is closing…"),
        _ => _updateState.Status,
    };

    public string? UpdateErrorText => _updateState.Error;

    public string? UpdateCandidateVersion => _updateState.CandidateVersion;

    public bool CanInstallUpdate => _updateCoordinator?.HasAcceptedCandidate == true;

    private ThirdPartyNoticesReadModel _thirdPartyNotices = ThirdPartyNoticesReadModel.Missing("Third-party notices are unavailable in this deployment.");

    public ThirdPartyNoticesReadModel ThirdPartyNotices
    {
        get => _thirdPartyNotices;
        private set => SetProperty(ref _thirdPartyNotices, value);
    }

    public string AppTitle => ProductIdentity.DisplayName;

    public DiagnosticVersionReport VersionReport => _versionReport ??= CreateVersionReport();

    public string AppVersion => VersionReport.AppVersion;

    private ReleaseInformation? _releaseInformation;
    public ReleaseInformation? PublishedRelease
    {
        get => _releaseInformation;
        private set => SetProperty(ref _releaseInformation, value);
    }

    private IReadOnlyList<ThirdPartyComponent> _thirdPartyComponents = [];
    public IReadOnlyList<ThirdPartyComponent> ThirdPartyComponents
    {
        get => _thirdPartyComponents;
        private set => SetProperty(ref _thirdPartyComponents, value);
    }

    private async Task LoadReleaseInformationAsync(CancellationToken cancellationToken = default)
    {
        try { PublishedRelease = await GitHubReleaseInformation.ReadAsync(AppVersion, cancellationToken).ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (System.Net.Http.HttpRequestException) { }
        catch (System.Text.Json.JsonException) { }
        catch (InvalidOperationException) { }
        catch (IOException) { }
    }

    public string RuntimeInfo => string.Create(
        CultureInfo.InvariantCulture,
        $"{VersionReport.RuntimeDescription} ({RuntimeInformation.OSArchitecture}), {VersionReport.TargetFramework}");

    public string CatalogSchemaVersion => VersionReport.SchemaVersion;

    public string ProfilingWorkerProtocolVersion => VersionReport.ProtocolVersion;

    public string FaceModelInfo => string.Create(
        CultureInfo.InvariantCulture,
        $"YuNet {VersionReport.DetectionModelVersion ?? "not declared"}, "
            + $"SFace {VersionReport.RecognitionModelVersion ?? "not declared"}");

    private async Task LoadThirdPartyNoticesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var install = InstallPaths.CreateProduction();
            var artifacts = DeploymentArtifactCatalog.TryLoad(install);
            ThirdPartyComponents = artifacts?.Sources
                .Where(source => source.LicenseUrl is not null)
                .Select(source =>
                {
                    var license = new Uri(source.LicenseUrl!);
                    var sourcePage = source.LicenseUrl!.Split("/blob/", StringSplitOptions.None)[0];
                    var name = source.SourceId switch
                    {
                        "opencv-zoo-yunet-2023mar" => "YuNet",
                        "opencv-zoo-sface-2021dec" => "SFace",
                        "btbn-ffmpeg-8.1.2-win64-lgpl" or "btbn-ffmpeg-8.1.3-win64-lgpl" or "naut-ffmpeg-8.1.3-win64-lgpl" => "FFmpeg",
                        "opencvsharp5-runtime-win" => "OpenCvSharp",
                        _ => source.Package ?? source.SourceId,
                    };
                    return new ThirdPartyComponent(name, source.License, new Uri(sourcePage), license);
                }).ToArray() ?? [];
            var path = install.ThirdPartyNoticesFilePath;
            if (!install.IsWithinInstallRoot(path) || !File.Exists(path))
            {
                ThirdPartyNotices = ThirdPartyNoticesReadModel.Missing("Third-party notices are unavailable in this deployment.");
                return;
            }
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true);
            const int maxNoticeBytes = 2 * 1024 * 1024;
            ThirdPartyNotices = new ThirdPartyNoticesReadModel(true, content.Length > maxNoticeBytes ? content[..maxNoticeBytes] : content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException)
        {
            ThirdPartyNotices = ThirdPartyNoticesReadModel.Missing("Third-party notices could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            ThirdPartyNotices = ThirdPartyNoticesReadModel.Missing("Third-party notices could not be accessed.");
        }
    }

    private DiagnosticVersionReport CreateVersionReport()
    {

        var artifacts = DeploymentArtifactCatalog.TryLoad(InstallPaths.CreateProduction());
        if (artifacts is null)
        {
            Trace.TraceWarning(
                "This deployment has no readable artifact manifest; About reports declared versions as unavailable.");
        }

        return DiagnosticVersionReport.CreateCurrent(_catalog?.SchemaVersion, artifacts);
    }

    public ICommand CreateCategoryCommand { get; }
    public ICommand CommitRenameCategoryCommand { get; }
    public ICommand DeleteCategoryCommand { get; }
    public ICommand CreateTagCommand { get; }
    public ICommand CommitRenameTagCommand { get; }
    public ICommand DeleteTagCommand { get; }
    public ICommand NavigateToFaceReviewCommand { get; }
    public AsyncRelayCommand SaveUpdateFeedCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public AsyncRelayCommand InstallUpdateCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _initializeGeneration);
        await UiDispatch.InvokeAsync(() =>
        {
            if (!_isDisposed && IsRouteActive && generation == _initializeGeneration)
            {
                ShowLoading();
            }
        }).ConfigureAwait(false);

        try
        {
            var currentLanguage = _currentLanguage;
            var updateFeedUrl = _updateFeedUrl;
            if (_configurationStore is not null)
            {
                var appConfig = await _configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(appConfig.Configuration.UiLanguage))
                {
                    currentLanguage = appConfig.Configuration.UiLanguage;
                }
                updateFeedUrl = appConfig.Configuration.Updates.FeedUrl ?? string.Empty;
            }

            var organization = await ReadOrganizationSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var systemMetrics = await ReadSystemMetricsSnapshotAsync(cancellationToken).ConfigureAwait(false);

            await UiDispatch.InvokeAsync(() =>
            {
                if (_isDisposed || !IsRouteActive || generation != _initializeGeneration)
                {
                    return;
                }

                _currentLanguage = currentLanguage;
                _updateFeedUrl = updateFeedUrl;

                RaisePropertyChanged(nameof(CurrentLanguage));
                RaisePropertyChanged(nameof(UpdateFeedUrl));
                ApplyOrganizationSnapshot(organization);
                ApplySystemMetricsSnapshot(systemMetrics);
                ShowReady();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || !IsRouteActive)
        {
        }
        catch (Exception ex)
        {
            await UiDispatch.InvokeAsync(() =>
            {
                if (!_isDisposed && IsRouteActive && generation == _initializeGeneration)
                {
                    ShowRecoverableError($"Failed to load settings: {OperationExecution.SafeMessage(ex)}");
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task SaveUpdateFeedAsync()
    {
        if (_updateCoordinator is null)
        {
            return;
        }

        var result = await _updateCoordinator.SaveFeedAsync(UpdateFeedUrl).ConfigureAwait(false);
        if (result.Succeeded)
        {
            _updateFeedUrl = UpdateFeedUrl.Trim();
            RunOnUi(() => RaisePropertyChanged(nameof(UpdateFeedUrl)));
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateCoordinator is not null)
        {
            await _updateCoordinator.CheckAsync().ConfigureAwait(false);
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_updateCoordinator is not null)
        {
            await _updateCoordinator.DownloadAndInstallAsync().ConfigureAwait(false);
        }
    }

    public Task<LocalUpdateInspectionResult> InspectLocalUpdateAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        return _updateCoordinator is null
            ? Task.FromResult(LocalUpdateInspectionResult.Reject("The update service is unavailable."))
            : _updateCoordinator.InspectLocalPackageAsync(archivePath, cancellationToken);
    }

    public Task<UpdateCommandResult> InstallLocalUpdateAsync(
        string archivePath,
        string expectedPayloadSha256,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        return _updateCoordinator is null
            ? Task.FromResult(UpdateCommandResult.Failed("The update service is unavailable."))
            : _updateCoordinator.InstallFromLocalPackageAsync(
                archivePath,
                expectedPayloadSha256,
                userConfirmed,
                cancellationToken);
    }

    private void OnUpdateStateChanged(UpdatePresentationState state)
    {
        RunOnUi(() =>
        {
            _updateState = state;
            RaisePropertyChanged(nameof(UpdateStatusText));
            RaisePropertyChanged(nameof(UpdateErrorText));
            RaisePropertyChanged(nameof(UpdateCandidateVersion));
            RaisePropertyChanged(nameof(CanInstallUpdate));
            SaveUpdateFeedCommand.RaiseCanExecuteChanged();
            CheckForUpdatesCommand.RaiseCanExecuteChanged();
            InstallUpdateCommand.RaiseCanExecuteChanged();
        });
    }

    public async Task ApplyLanguageAsync(string languageCode, CancellationToken cancellationToken = default)
    {
        if (!LanguageCatalog.TryGet(languageCode, out var language))
        {
            return;
        }

        var normalized = language.Code;
        if (_configurationStore is not null)
        {
            try
            {
                var save = await _configurationStore
                    .UpdateAsync(configuration => configuration with { UiLanguage = normalized }, cancellationToken)
                    .ConfigureAwait(false);
                if (!save.IsSaved)
                {
                    await UiDispatch.InvokeAsync(() =>
                    {
                        if (!_isDisposed && IsRouteActive)
                            ShowRecoverableError(save.SafeErrorDetail ?? "The language preference could not be saved.");
                    }).ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await UiDispatch.InvokeAsync(() =>
                {
                    if (!_isDisposed && IsRouteActive)
                        ShowRecoverableError($"The language preference could not be saved: {OperationExecution.SafeMessage(exception)}");
                }).ConfigureAwait(false);
                return;
            }
        }

        await UiDispatch.InvokeAsync(() =>
        {
            if (_isDisposed || !IsRouteActive) return;
            CurrentLanguage = normalized;
            SurfaceText.ApplyLanguage(normalized);
        }).ConfigureAwait(false);
    }

    private void RunOnUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        UiDispatch.Run(() =>
        {
            if (!_isDisposed && IsRouteActive)
            {
                action();
            }
        });
    }

    private sealed record OrganizationSnapshot(
        IReadOnlyList<CategoryItemViewModel> Categories,
        IReadOnlyList<TagItemViewModel> Tags);

    private async Task<OrganizationSnapshot> ReadOrganizationSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_catalog is null)
        {
            return new OrganizationSnapshot([], []);
        }

        var categories = new List<CategoryItemViewModel>();
        var cats = await _catalog.SettingsReads.GetAllCategoriesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var cat in cats)
        {
            var usage = _reads is not null
                ? await _reads.GetCategoryUsageCountAsync(cat.CategoryId, cancellationToken).ConfigureAwait(false)
                : 0;
            categories.Add(new CategoryItemViewModel(cat.CategoryId, cat.Name, usage, cat.RowVersion));
        }

        var tags = new List<TagItemViewModel>();
        var storedTags = await _catalog.SettingsReads.GetAllTagsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var tag in storedTags)
        {
            var usage = _reads is not null
                ? await _reads.GetTagUsageCountAsync(tag.TagId, cancellationToken).ConfigureAwait(false)
                : 0;
            tags.Add(new TagItemViewModel(tag.TagId, tag.Name, usage, tag.RowVersion));
        }

        return new OrganizationSnapshot(categories, tags);
    }

    private void ApplyOrganizationSnapshot(OrganizationSnapshot snapshot)
    {
        Categories.Clear();
        foreach (var category in snapshot.Categories) Categories.Add(category);
        Tags.Clear();
        foreach (var tag in snapshot.Tags) Tags.Add(tag);
    }

    public async Task LoadOrganizationAsync(CancellationToken cancellationToken = default)
    {
        if (_catalog is null) return;
        var snapshot = await ReadOrganizationSnapshotAsync(cancellationToken).ConfigureAwait(false);
        await UiDispatch.InvokeAsync(() =>
        {
            if (!_isDisposed && IsRouteActive) ApplyOrganizationSnapshot(snapshot);
        }).ConfigureAwait(false);
    }

    public async Task CreateCategoryAsync(CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog?.MutationAdmission.Enter(nameof(CreateCategoryAsync));
        if (string.IsNullOrWhiteSpace(NewCategoryName) || _catalog is null)
        {
            return;
        }

        try
        {
            OrganizationMessage = null;
            OrganizationWarningMessage = null;

            var id = Guid.NewGuid().ToString("N")[..12];
            var writes = new SettingsWrites(_catalog);
            await writes.CreateCategoryAsync(id, NewCategoryName.Trim(), cancellationToken);

            Categories.Add(new CategoryItemViewModel(id, NewCategoryName.Trim(), 0, 0));
            OrganizationMessage = $"Category '{NewCategoryName.Trim()}' created.";
            NewCategoryName = string.Empty;
        }
        catch (Exception ex)
        {
            OrganizationWarningMessage = $"Could not create category: {OperationExecution.SafeMessage(ex)}";
        }
    }

    public async Task RenameCategoryAsync(CategoryItemViewModel item, string newName, CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog?.MutationAdmission.Enter(nameof(RenameCategoryAsync));
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(newName) || _catalog is null)
        {
            return;
        }

        try
        {
            OrganizationMessage = null;
            OrganizationWarningMessage = null;

            var writes = new SettingsWrites(_catalog);
            var nextVer = await writes.UpdateCategoryAsync(item.CategoryId, newName.Trim(), item.RowVersion, cancellationToken)
                ;

            item.Name = newName.Trim();
            item.RowVersion = nextVer;
            OrganizationMessage = $"Category renamed to '{item.Name}'.";
        }
        catch (Exception ex)
        {
            OrganizationWarningMessage = $"Could not rename category: {OperationExecution.SafeMessage(ex)}";
        }
    }

    public async Task DeleteCategoryAsync(CategoryItemViewModel item, CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog?.MutationAdmission.Enter(nameof(DeleteCategoryAsync));
        ArgumentNullException.ThrowIfNull(item);
        if (_catalog is null)
        {
            Categories.Remove(item);
            return;
        }

        try
        {
            OrganizationMessage = null;
            OrganizationWarningMessage = null;

            var writes = new SettingsWrites(_catalog);
            await writes.DeleteCategoryAsync(item.CategoryId, item.RowVersion, cancellationToken);

            Categories.Remove(item);
            OrganizationMessage = $"Category '{item.Name}' deleted.";
        }
        catch (Exception ex)
        {
            OrganizationWarningMessage = $"Could not delete category: {OperationExecution.SafeMessage(ex)}";
        }
    }

    public async Task CreateTagAsync(CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog?.MutationAdmission.Enter(nameof(CreateTagAsync));
        if (string.IsNullOrWhiteSpace(NewTagName) || _catalog is null)
        {
            return;
        }

        // K04.1: Support batch comma/Enter creation using TaxonomyNamePolicy.
        var tokens = TaxonomyNamePolicy.ParseTagTokens(NewTagName);
        if (tokens.Count == 0)
        {
            return;
        }

        try
        {
            OrganizationMessage = null;
            OrganizationWarningMessage = null;
            var writes = new SettingsWrites(_catalog);
            var created = 0;

            foreach (var token in tokens)
            {
                // K04.4: Check for duplicate canonical names.
                if (Tags.Any(t => TaxonomyNamePolicy.AreSameName(t.Name, token.CanonicalName)))
                {
                    continue;
                }

                var id = Guid.NewGuid().ToString("N")[..12];
                await writes.CreateTagAsync(id, token.DisplayName, cancellationToken);
                Tags.Add(new TagItemViewModel(id, token.DisplayName, 0, 0));
                created++;
            }

            if (created > 0)
            {
                OrganizationMessage = created == 1
                    ? $"Tag '{tokens[0].DisplayName}' created."
                    : $"{created} tags created.";
            }
            NewTagName = string.Empty;
        }
        catch (Exception ex)
        {
            OrganizationWarningMessage = $"Could not create tag: {OperationExecution.SafeMessage(ex)}";
        }
    }

    public async Task RenameTagAsync(TagItemViewModel item, string newName, CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog?.MutationAdmission.Enter(nameof(RenameTagAsync));
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(newName) || _catalog is null)
        {
            return;
        }

        // K04.3: Reject multi-token rename input.
        var tokens = TaxonomyNamePolicy.ParseTagTokens(newName);
        if (tokens.Count > 1)
        {
            OrganizationWarningMessage = "To add multiple tags, use the New Tag field. Rename only works for one tag at a time.";
            return;
        }

        if (tokens.Count == 0)
        {
            return;
        }

        var displayName = tokens[0].DisplayName;

        // K04.4: Check duplicate canonical name.
        if (Tags.Any(t => t.TagId != item.TagId && TaxonomyNamePolicy.AreSameName(t.Name, displayName)))
        {
            OrganizationWarningMessage = $"A tag with that name already exists.";
            return;
        }

        try
        {
            OrganizationMessage = null;
            OrganizationWarningMessage = null;

            var writes = new SettingsWrites(_catalog);
            var nextVer = await writes.UpdateTagAsync(item.TagId, displayName, item.RowVersion, cancellationToken);

            item.Name = displayName;
            item.RowVersion = nextVer;
            OrganizationMessage = $"Tag renamed to '{item.Name}'.";
        }
        catch (Exception ex)
        {
            OrganizationWarningMessage = $"Could not rename tag: {OperationExecution.SafeMessage(ex)}";
        }
    }

    public async Task DeleteTagAsync(TagItemViewModel item, CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog?.MutationAdmission.Enter(nameof(DeleteTagAsync));
        ArgumentNullException.ThrowIfNull(item);
        if (_catalog is null)
        {
            Tags.Remove(item);
            return;
        }

        try
        {
            OrganizationMessage = null;
            OrganizationWarningMessage = null;

            var writes = new SettingsWrites(_catalog);
            await writes.DeleteTagAsync(item.TagId, item.RowVersion, cancellationToken);

            Tags.Remove(item);
            OrganizationMessage = $"Tag '{item.Name}' deleted.";
        }
        catch (Exception ex)
        {
            OrganizationWarningMessage = $"Could not delete tag: {OperationExecution.SafeMessage(ex)}";
        }
    }

    private sealed record SystemMetricsReadSnapshot(StorageMetricsSnapshot? Metrics, long UnresolvedFaceCount);

    private async Task<SystemMetricsReadSnapshot> ReadSystemMetricsSnapshotAsync(CancellationToken cancellationToken)
    {
        StorageMetricsSnapshot? metrics = null;
        var unresolved = 0L;
        if (_storageMetrics is not null)
        {
            metrics = await _storageMetrics.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        if (_catalog is not null)
        {
            var summary = await _catalog.HealthReads.GetHealthSummaryAsync(cancellationToken).ConfigureAwait(false);
            unresolved = summary.UnresolvedFaceDetections;
        }
        return new SystemMetricsReadSnapshot(metrics, unresolved);
    }

    private void ApplySystemMetricsSnapshot(SystemMetricsReadSnapshot snapshot)
    {
        _systemMetrics = snapshot.Metrics;
        _unresolvedFaceCount = snapshot.UnresolvedFaceCount;
        RaisePropertyChanged(nameof(UnresolvedFaceCount));
        RaisePropertyChanged(nameof(ManagedStorageFormatted));
        RaisePropertyChanged(nameof(TotalProfilesCount));
        RaisePropertyChanged(nameof(ActiveMediaCount));
    }

    public async Task LoadSystemMetricsAsync(CancellationToken cancellationToken = default)
    {
        if (_storageMetrics is null && _catalog is null) return;
        try
        {
            var snapshot = await ReadSystemMetricsSnapshotAsync(cancellationToken).ConfigureAwait(false);
            await UiDispatch.InvokeAsync(() =>
            {
                if (!_isDisposed && IsRouteActive) ApplySystemMetricsSnapshot(snapshot);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || !IsRouteActive)
        {
        }
        catch (Exception ex)
        {
            await UiDispatch.InvokeAsync(() =>
            {
                if (!_isDisposed && IsRouteActive)
                    ShowRecoverableError($"Could not evaluate system metrics: {OperationExecution.SafeMessage(ex)}");
            }).ConfigureAwait(false);
        }
    }

    private void LoadActiveSection()
    {
        if (ActiveSection == SettingsSection.Activity)
        {
            TaskObserver.Observe(Activity.LoadAsync(reset: true), "SettingsViewModel.Activity.LoadAsync");
        }
        else if (ActiveSection == SettingsSection.Trash)
        {
            TaskObserver.Observe(Trash.LoadAsync(), "SettingsViewModel.Trash.LoadAsync");
        }
        else if (ActiveSection == SettingsSection.Vault)
        {
            TaskObserver.Observe(LoadSystemMetricsAsync(), "SettingsViewModel.LoadSystemMetricsAsync");
            if (ActiveSubsection == SettingsSubsection.VaultHealth)
            {
                TaskObserver.Observe(Health.LoadMetricsAsync(), "SettingsViewModel.Health.LoadMetricsAsync");
            }
        }
        else if (ActiveSection == SettingsSection.PeopleOrganization)
        {
            TaskObserver.Observe(LoadOrganizationAsync(), "SettingsViewModel.LoadOrganizationAsync");
            TaskObserver.Observe(LoadSystemMetricsAsync(), "SettingsViewModel.LoadSystemMetricsAsync");
        }
    }

    private void OnCatalogInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        switch (invalidation.DomainKind)
        {
            case CatalogInvalidationDomain.Category:
            case CatalogInvalidationDomain.Tag:
            case CatalogInvalidationDomain.TaxonomyUsage:
                SignalOrganizationCoalescer();
                break;
        }
    }

    private RefreshCoalescer? _organizationCoalescer;

    private void SignalOrganizationCoalescer()
    {
        _organizationCoalescer ??= new RefreshCoalescer(
            ct => LoadOrganizationAsync(ct),
            TimeSpan.FromMilliseconds(600));
        UiDispatch.Run(() => _organizationCoalescer.Signal());
    }
}
