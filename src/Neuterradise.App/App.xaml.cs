using System.Diagnostics;
using System.Net.Http;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Gallery;
using Neuterradise.App.Home;
using Neuterradise.App.Import;
using Neuterradise.App.Localization;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Lifecycle;
using Neuterradise.App.SystemServices.Resources;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Updates;
using Neuterradise.App.Ui;

namespace Neuterradise.App;

/// <summary>
/// Uno Skia Desktop entry. Startup order is unchanged from the WPF product: roots → configuration and
/// language → crash diagnostics → approved Vault root → bootstrapper (lock, schema initialization, recovery) →
/// runtime prewarm (ResourceGovernor, caches, Presentation registry + compiled plans) → shell.
/// Shutdown stays controlled: placement and scale flush, the import finalizer stops at a durable
/// checkpoint, then the runtime and Vault lock are released in order.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private InstallPaths? _install;
    private AppStatePaths? _appState;
    private AppConfigurationStore? _configuration;
    private VaultTransitionAuthority? _vaultTransitions;
    private HttpClient? _updateHttpClient;
    private UpdateCoordinator? _updateCoordinator;
    private CrashDiagnostics? _crash;
    private AppBootstrapper? _bootstrapper;
    private BootstrapContext? _context;
    private ProductionRuntimeRegistry? _runtime;
    private ProfileRuntimeSnapshotCache? _profileSnapshots;
    private PresentationRuntime? _presentation;
    private ShutdownCoordinator? _shutdown;
    private ImportActivityService? _importActivity;
    private ImportFinalizer? _finalizer;
    private UnoWindowPlacement? _placement;
    private DerivedImageLoader? _derivedImages;
    private ResourceGovernor? _resourceGovernor;
    private FigureRuntime? _figures;
    private CancellationTokenSource? _figurePrewarmCancellation;
    private Task? _figurePrewarmTask;
    private AppServices? _services;
    private int _bootstrapAttemptActive;
    private int _shutdownStarted;
    private int _vaultTransitionStarted;
    private bool _allowClose;
    private readonly WindowStateProjection _windowState = new();
    private Windows.UI.ViewManagement.UISettings? _uiSettings;

    public App()
    {
        Uno.UI.FeatureConfiguration.Font.FallbackService = LocalPresentationFonts.Current;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window { Title = ProductIdentity.DisplayName };
        BrandAssets.ApplyNativeWindowIcon(_window);
        var queue = DispatcherQueue.GetForCurrentThread();
        UiDispatch.Install(new DispatcherQueueSyncContext(queue));
        SynchronizationContext.SetSynchronizationContext(UiDispatch.Context);
        ThemeRuntime.Install(new ThemeRuntime());
        _window.Content = StartupScreens.Splash(UI.T("Startup.Opening", "Opening your Vault…"));
        _window.AppWindow.Closing += OnClosing;
        _uiSettings = new Windows.UI.ViewManagement.UISettings();
        _window.Activated += (_, e) =>
        {
            ApplyWindowState(_windowState.SetActive(e.WindowActivationState != Windows.UI.Core.CoreWindowActivationState.Deactivated));
            RefreshSystemReducedMotion();
        };
        _window.Activate();
        ObserveStartup(StartAsync(), "App.StartAsync");
    }

    private async Task StartAsync()
    {
        _install = InstallPaths.CreateProduction();
        _appState = AppStatePaths.CreateProduction();
        _configuration = new AppConfigurationStore(_appState);
        _vaultTransitions = new VaultTransitionAuthority(_appState, _install);

        try
        {
            var initial = await _configuration.LoadAsync().ConfigureAwait(true);
            SurfaceText.ApplyLanguage(initial.Configuration.UiLanguage);
        }
        catch (Exception)
        {
            SurfaceText.ApplyLanguage(LanguageCatalog.DefaultCode);
        }

        VaultTransitionRecoveryResult transitionRecovery;
        try
        {
            transitionRecovery = await _vaultTransitions
                .RecoverStartupAsync(_configuration, CreateVaultMoveProgressReporter())
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            transitionRecovery = VaultTransitionRecoveryResult.Failed("VAULT_TRANSITION_RECOVERY_FAILED", exception.Message);
        }

        _crash = CrashDiagnostics.CreateProduction(_appState, CreateCrashContext, RequestControlledShutdown);
        _crash.Install();
        UnhandledException += (_, e) => e.Handled = _crash?.HandleUiThreadException(e.Exception) ?? false;

        if (!transitionRecovery.Succeeded)
        {
            ShowFailure(
                transitionRecovery.ErrorCode ?? "VAULT_TRANSITION_RECOVERY_FAILED",
                new InvalidOperationException(transitionRecovery.SafeErrorDetail ?? "Vault transition recovery failed."));
            return;
        }

        await BootstrapLoopAsync().ConfigureAwait(true);
    }

    private async Task BootstrapLoopAsync()
    {
        if (Interlocked.CompareExchange(ref _bootstrapAttemptActive, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await CleanupPartialStartupAsync().ConfigureAwait(true);

            var resolution = await ResolveVaultRootAsync().ConfigureAwait(true);
            if (resolution.VaultPaths is null)
            {
                if (resolution.ErrorCode is not null)
                {
                    ShowFailure(resolution.ErrorCode, null, resolution.PreservedCopyPath);
                }

                return;
            }

            _window!.Content = StartupScreens.Splash(UI.T("Startup.Opening", "Opening your Vault…"));
            _bootstrapper = AppBootstrapper.CreateProduction(resolution.VaultPaths, _appState!);
            var result = await _bootstrapper.BootstrapAsync(
                onReady: ShowShellAsync,
                prewarm: PrewarmAsync,
                rollbackActivatedRuntime: RollbackActivatedStartupAsync,
                cancellationToken: CancellationToken.None).ConfigureAwait(true);
            if (result.IsReady)
            {
                _context = result.Context;
                if (_vaultTransitions is not null && _services is not null && result.Context is not null)
                {
                    TaskObserver.Observe(
                        FinalizeVaultTransitionAfterBootstrapAsync(
                            result.Context.CreateSuccessfulBootstrapProof(),
                            _services),
                        "App.FinalizeVaultTransitionAfterBootstrapAsync",
                        exception => _services.Toast(exception.Message, "warning"));
                }

                return;
            }

            if (result.PreservesWritableAuthority)
            {
                _context = result.Context;
                ShowFailure(result.ErrorCode, result.Exception);
                return;
            }

            await CleanupPartialStartupAsync().ConfigureAwait(true);
            if (await TryRecoverFailedChangeTargetAndRestartAsync(resolution.VaultPaths.Root).ConfigureAwait(true))
            {
                return;
            }

            ShowFailure(result.ErrorCode, result.Exception);
        }
        catch (Exception exception)
        {
            await CleanupPartialStartupAsync().ConfigureAwait(true);
            _crash?.Capture(exception, CrashOrigin.StartupFailure);
            ShowFailure(StartupRecoveryViewModel.GenericErrorCode, exception);
        }
        finally
        {
            Volatile.Write(ref _bootstrapAttemptActive, 0);
        }
    }

    private async Task<bool> TryRecoverFailedChangeTargetAndRestartAsync(string failedRoot)
    {
        if (_vaultTransitions is null || _configuration is null || _window is null)
        {
            return false;
        }

        var recovery = await _vaultTransitions
            .RecoverFailedChangeTargetAsync(failedRoot, _configuration)
            .ConfigureAwait(true);
        if (!recovery.Succeeded)
        {
            ShowFailure(
                recovery.ErrorCode ?? "VAULT_CHANGE_RECOVERY_FAILED",
                new InvalidOperationException(
                    recovery.SafeErrorDetail
                        ?? "The selected Vault failed and the previous Vault could not be restored automatically."));
            return true;
        }

        if (!recovery.Recovered
            || string.IsNullOrWhiteSpace(recovery.EffectiveRoot)
            || RootPathRules.AreSameRoot(recovery.EffectiveRoot, failedRoot))
        {
            return false;
        }

        _window.Content = StartupScreens.Splash(
            UI.T(
                "Vault.Change.Returning",
                "The selected Vault could not open. Returning to the previous Vault…"));
        if (TryRelaunchApplication())
        {
            _allowClose = true;
            Environment.ExitCode = 0;
            Exit();
            return true;
        }

        ShowFailure(
            "VAULT_CHANGE_RETURN_RESTART_FAILED",
            new InvalidOperationException(
                "The previous Vault was restored in configuration, but naut could not restart itself."));
        return true;
    }

    private async Task RollbackActivatedStartupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HoverVideoCoordinator.Shared.StopAll();
        SkVideoView.StopAllPlayback();
        await RetireFiguresAsync().ConfigureAwait(true);

        _shutdown = null;
        _services = null;

        _updateCoordinator?.Dispose();
        _updateCoordinator = null;
        _updateHttpClient?.Dispose();
        _updateHttpClient = null;

        _placement?.Dispose();
        _placement = null;

        if (_finalizer is not null)
        {
            await _finalizer.DisposeAsync().ConfigureAwait(true);
            _finalizer = null;
        }

        if (_importActivity is not null)
        {
            await _importActivity.DisposeAsync().ConfigureAwait(true);
            _importActivity = null;
        }

        _presentation = null;

        if (_profileSnapshots is not null)
        {
            await _profileSnapshots.DisposeAsync().ConfigureAwait(true);
            _profileSnapshots = null;
        }

        if (_runtime is not null)
        {
            await _runtime.DisposeAsync().ConfigureAwait(true);
            _runtime = null;
        }

        if (_resourceGovernor is not null)
        {
            _resourceGovernor.TrimRequested -= OnTrimRequested;
        }

        _derivedImages?.Dispose();
        _derivedImages = null;
        _resourceGovernor?.Dispose();
        _resourceGovernor = null;
    }

    private async Task CleanupPartialStartupAsync()
    {
        HoverVideoCoordinator.Shared.StopAll();
        SkVideoView.StopAllPlayback();
        await RetireFiguresAsync().ConfigureAwait(true);

        if (_finalizer is not null)
        {
            try
            {
                await _finalizer.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Partial-startup import finalizer disposal failed: {0}", exception.GetType().Name);
            }
            finally
            {
                _finalizer = null;
            }
        }

        if (_importActivity is not null)
        {
            try
            {
                await _importActivity.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Partial-startup import activity disposal failed: {0}", exception.GetType().Name);
            }
            finally
            {
                _importActivity = null;
            }
        }

        _placement?.Dispose();
        _placement = null;
        _shutdown = null;

        if (_profileSnapshots is not null)
        {
            try
            {
                await _profileSnapshots.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Partial-startup Profile snapshot disposal failed: {0}", exception.GetType().Name);
            }
            finally
            {
                _profileSnapshots = null;
            }
        }

        if (_runtime is not null)
        {
            try
            {
                await _runtime.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Partial-startup runtime disposal failed: {0}", exception.GetType().Name);
            }
            finally
            {
                _runtime = null;
            }
        }

        if (_resourceGovernor is not null)
        {
            _resourceGovernor.TrimRequested -= OnTrimRequested;
        }

        _derivedImages?.Dispose();
        _derivedImages = null;
        _resourceGovernor?.Dispose();
        _resourceGovernor = null;
        _presentation = null;
        _context = null;
        _services = null;
    }

    private void ObserveStartup(Task task, string context) =>
        TaskObserver.Observe(task, context, exception =>
        {
            _crash?.Capture(exception, CrashOrigin.StartupFailure);
            ShowFailure(StartupRecoveryViewModel.GenericErrorCode, exception);
        });

    private async Task<VaultRootResolution> ResolveVaultRootAsync()
    {
        var load = await _configuration!.LoadAsync().ConfigureAwait(true);
        if (load.Status == AppConfigurationStatus.AccessDenied)
        {
            return new(null, "CONFIGURATION_ACCESS_DENIED", null);
        }

        if (load.Status == AppConfigurationStatus.Unreadable)
        {
            Trace.TraceError("Configuration at {0} is unreadable; the original was preserved at {1}.", load.ConfigurationFilePath, load.PreservedCopyPath ?? "(preservation failed)");
            return new(null, "CONFIGURATION_UNREADABLE");
        }

        if (load.Configuration.HasVaultRoot)
        {
            try
            {
                var vault = new VaultPaths(load.Configuration.VaultRoot!);
                vault.EnsureDisjointFrom(_install!, _appState!);
                if (!Directory.Exists(vault.Root))
                {
                    Trace.TraceWarning(
                        "Configured vault root is unavailable and will not be recreated automatically: {0}",
                        vault.Root);
                    return new(null, "VAULT_ROOT_MISSING");
                }

                return new(vault, null);
            }
            catch (ArgumentException exception)
            {
                Trace.TraceError("Refused the configured vault root: {0}", exception.Message);
                return new(null, "VAULT_ROOT_INVALID");
            }
        }

        var completion = new TaskCompletionSource<VaultRootResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> AcceptAsync(string selected)
        {
            var resolution = await AcceptVaultRootAsync(selected).ConfigureAwait(true);
            if (resolution.VaultPaths is null)
                return VaultWelcomeError(resolution.ErrorCode);
            completion.TrySetResult(resolution);
            return null;
        }
        _window!.Content = new VaultWelcomeView(
            () => Pickers.PickFolderAsync(_window),
            AcceptAsync,
            () => { completion.TrySetResult(new(null, null)); Exit(); });
        return await completion.Task.ConfigureAwait(true);
    }

    private static string VaultWelcomeError(string? code) => code switch
    {
        "VAULT_SELECTION_NOT_VAULT_OR_EMPTY" => UI.T("Startup.Vault.NotEmpty", "Choose an existing naut Vault or an empty folder."),
        "VAULT_SELECTION_NESTED" => UI.T("Startup.Vault.Nested", "Choose a location outside an existing Vault."),
        _ => UI.T("Startup.Vault.ActionFailed", "The Vault could not be opened. Check the location and try again."),
    };
    /// <summary>
    /// One reusable Vault-selection acceptance path used by both first-run onboarding and invalid-Vault
    /// recovery. Validates the same safety contract (disjoint roots, reparse-point rejection, unsafe
    /// placement) and atomically persists the chosen root through <see cref="AppConfigurationStore"/>.
    /// </summary>
    private async Task<VaultRootResolution> AcceptVaultRootAsync(string selected)
    {
        var inspection = new VaultRootSelectionPolicy(_install!, _appState!).Inspect(selected);
        if (!inspection.Accepted || inspection.Paths is null)
        {
            Trace.TraceError(
                "Refused the selected Vault root: {0}",
                inspection.SafeErrorDetail ?? inspection.ErrorCode ?? "unknown selection error");
            return new(
                null,
                inspection.ErrorCode ?? "VAULT_ROOT_INVALID",
                null);
        }

        var save = await _configuration!
            .UpdateAsync(configuration => configuration with { VaultRoot = inspection.Paths.Root })
            .ConfigureAwait(true);
        return save.IsSaved
            ? new(inspection.Paths, null, null)
            : new(null, "CONFIGURATION_ACCESS_DENIED", null);
    }
    /// <summary>
    /// Recovery action for a missing or invalid persisted Vault: opens the folder picker, validates and persists
    /// the chosen root through the shared acceptance path, then resumes bootstrap from the corrected
    /// authority without requiring app-config editing.
    /// </summary>
    private void RecoverVaultRootAsync() =>
        ObserveStartup(ChooseVaultAndResumeAsync(), "App.RecoverVaultRoot");

    private async Task ChooseVaultAndResumeAsync()
    {
        var selected = await Pickers.PickFolderAsync(_window!).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        var resolution = await AcceptVaultRootAsync(selected).ConfigureAwait(true);
        if (resolution.VaultPaths is null)
        {
            ShowFailure(resolution.ErrorCode, null, resolution.PreservedCopyPath);
            return;
        }

        await BootstrapLoopAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Recovery action for an unreadable configuration: the original bytes are preserved by
    /// <see cref="AppConfigurationStore.LoadAsync"/>; this replaces only the application configuration
    /// with a valid default, then resumes bootstrap into normal Vault selection. The Vault is never
    /// deleted or mutated by this recovery.
    /// </summary>
    private void ResetConfigurationSafely() =>
        ObserveStartup(ResetConfigurationAndResumeAsync(), "App.ResetConfiguration");

    private async Task ResetConfigurationAndResumeAsync()
    {
        var reset = await _configuration!.UpdateAsync(_ => _configuration.CreateDefaultConfiguration()).ConfigureAwait(true);
        if (!reset.IsSaved)
        {
            ShowFailure("CONFIGURATION_ACCESS_DENIED", null, null);
            return;
        }

        await BootstrapLoopAsync().ConfigureAwait(true);
    }

    private async Task PrewarmAsync(BootstrapContext context, CancellationToken cancellationToken)
    {
        // One process-wide authority for heavy work, installed before any job or decode can start.
        _resourceGovernor = new ResourceGovernor();
        ResourceGovernor.InstallShared(_resourceGovernor);
        ApplyWindowState(_windowState.Current);
        try { _figures = new FigureRuntime(); }
        catch (Exception exception) { Trace.TraceWarning("Figure hardware unavailable: {0}", exception.Message); }
        _derivedImages = new DerivedImageLoader(maxEntries: 640, maxBytes: 128L * 1024 * 1024, maxConcurrentDecodes: 2);
        DerivedImageLoader.InstallShared(_derivedImages);
        _resourceGovernor.TrimRequested += OnTrimRequested;

        var systemReduceMotion = !(_uiSettings?.AnimationsEnabled ?? true);
        ReducedMotionAuthority.ApplySystem(systemReduceMotion);
        _runtime = await ProductionRuntimeRegistry.CreateAsync(context, cancellationToken).ConfigureAwait(true);
        ResourceGovernor.Shared.SetReducedMotion(ReducedMotionAuthority.IsReduced);

        new CachePaths(context.Paths).EnsureDirectories();
        context.Paths.EnsureStructuralDirectories();

        // Presentation registry: built-in + Vault packs validated and compiled once, during warm startup.
        var builtInAssets = _install!.ResolveContainedPath(
            InstallPathArea.Assets,
            Path.Combine("Presentation", "BuiltIn"));
        _presentation = new PresentationRuntime(context.Catalog, new PresentationPackStore(context.Paths.PresentationPacksPath, builtInAssets));
        var legacyConfiguration = await _configuration!.LoadAsync(cancellationToken).ConfigureAwait(true);
        await _presentation.InitializeAsync(cancellationToken, legacyConfiguration.Configuration.LegacyThemeId).ConfigureAwait(true);
        if (legacyConfiguration.Configuration.LegacyThemeId is not null)
            await _configuration.UpdateAsync(configuration => configuration with { LegacyThemeId = null }, cancellationToken).ConfigureAwait(true);
        PresentationBinder.ApplyGlobal(_presentation);
        PresentationEffects.Prewarm(_presentation);

        foreach (var limitation in _runtime.Capabilities.Limitations)
        {
            Trace.TraceWarning("Prewarm capability {0}: {1} ({2})", limitation.CapabilityId, limitation.State, limitation.Reason);
        }

        try
        {
            await Task.WhenAll(
                context.Catalog.GalleryReads.GetSpotlightCandidatesAsync(HomeViewModel.MaxSpotlightCandidates, cancellationToken),
                context.Catalog.GalleryReads.GetGalleryPageAsync(new GalleryQuery(PageSize: GalleryPageSizes.Default), cancellationToken),
                context.Catalog.SettingsReads.GetAllCategoriesAsync(cancellationToken),
                context.Catalog.SettingsReads.GetAllTagsAsync(cancellationToken),
                context.Catalog.ImportReads.ListRecentUnitsAsync(50, cancellationToken)).ConfigureAwait(true);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Trace.TraceWarning("Prewarm read failed (non-fatal): {0}", exception.Message);
        }
    }

    private void StartFigurePrewarm(BootstrapContext context)
    {
        var figures = _figures;
        if (figures is null || !figures.IsLiveAllowed || _figurePrewarmTask is not null) return;

        var cancellation = new CancellationTokenSource();
        _figurePrewarmCancellation = cancellation;
        _figurePrewarmTask = PrewarmFiguresAsync(context, figures, cancellation.Token);
        TaskObserver.Observe(
            _figurePrewarmTask,
            "App.PrewarmFigures",
            exception => Trace.TraceWarning("Figure prewarm failed (non-fatal): {0}", exception.Message));
    }

    private static async Task PrewarmFiguresAsync(
        BootstrapContext context,
        FigureRuntime figures,
        CancellationToken cancellationToken)
    {
        var candidates = await context.Catalog.ProfileReads
            .GetFigurePrewarmCandidatesAsync(2, cancellationToken)
            .ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!figures.IsLiveAllowed) break;

            try
            {
                var path = context.Paths.ResolveVaultRelativePath(
                    VaultPathArea.MediaAssets,
                    candidate.ModelRenderRelativePath);
                await figures.PrewarmSceneAsync(
                    path,
                    candidate.SourceSha256,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                Trace.TraceWarning(
                    "Figure prewarm skipped media {0:D}: {1}",
                    candidate.MediaId,
                    exception.GetType().Name);
            }
        }
    }

    private async Task RetireFiguresAsync()
    {
        var cancellation = _figurePrewarmCancellation;
        _figurePrewarmCancellation = null;
        cancellation?.Cancel();

        // Release presentation synchronously; a prepared-scene read may need time to drain.
        var figures = _figures;
        _figures = null;
        figures?.Dispose();

        var prewarm = _figurePrewarmTask;
        _figurePrewarmTask = null;
        if (prewarm is not null)
        {
            try { await prewarm.ConfigureAwait(true); }
            catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true) { }
            catch (Exception exception)
            {
                Trace.TraceWarning("Figure prewarm retirement observed: {0}", exception.GetType().Name);
            }
        }
        cancellation?.Dispose();

        if (figures is null) return;
        await figures.DrainAsync().ConfigureAwait(true);
    }

    private void OnTrimRequested(object? sender, EventArgs e) => _derivedImages?.Trim(0.5);

    private static void ApplyWindowState(WindowResourceState state) =>
        ResourceGovernor.Shared.SetWindowState(state.IsActive, state.IsMinimized);

    private void RefreshSystemReducedMotion()
    {
        if (_uiSettings is null)
        {
            return;
        }

        ReducedMotionAuthority.ApplySystem(!_uiSettings.AnimationsEnabled);
        ResourceGovernor.Shared.SetReducedMotion(ReducedMotionAuthority.IsReduced);
    }

    private async Task ShowShellAsync(BootstrapContext context, CancellationToken cancellationToken)
    {
        var runtime = _runtime ?? throw new InvalidOperationException("The runtime must be composed before the shell.");
        var presentation = _presentation ?? throw new InvalidOperationException("Presentation must be composed before the shell.");

        var navigation = new NavigationCoordinator();
        var status = new ShellStatusViewModel();
        status.ApplyCapabilities(runtime.Capabilities);

        _importActivity = new ImportActivityService(context.Catalog, UiDispatch.Context);
        _importActivity.Changed += (_, snapshot) => status.ApplyImports(snapshot);
        status.ApplyImports(_importActivity.Current);
        _importActivity.Start();

        _finalizer = new ImportFinalizer(context.Catalog);
        _finalizer.Changed += (_, _) => _importActivity.RequestRefresh();
        _finalizer.Start();

        _placement = new UnoWindowPlacement(_configuration, _windowState);
        await _placement.AttachAsync(_window!, cancellationToken).ConfigureAwait(true);

        _updateCoordinator?.Dispose();
        _updateHttpClient?.Dispose();
        var updateHttpClient = new HttpClient();
        _updateHttpClient = updateHttpClient;

        var updateTrust = new UpdateTrustPolicy();
        var updateValidator = new UpdatePackageValidator(_install!);
        var updateStager = new UpdatePackageStager(_appState!, updateValidator);
        var updateHandoff = new UpdateHandoffService(_appState!, _install!);
        _updateCoordinator = new UpdateCoordinator(
            context.Catalog.MutationAdmission,
            _configuration!,
            _appState!,
            _install!,
            context.Paths.Root,
            new UpdateCheckService(updateHttpClient, updateTrust),
            new UpdateDownloadService(updateHttpClient, _appState!),
            new LocalUpdatePackageReader(),
            updateTrust,
            updateStager,
            updateHandoff,
            RequestUpdateShutdown);

        var profileSnapshots = new ProfileRuntimeSnapshotCache(context.Catalog);
        _profileSnapshots = profileSnapshots;

        var overlay = new OverlayHostViewModel();
        var factory = new RouteViewModelFactory(
            catalog: context.Catalog,
            navigation: navigation,
            overlay: overlay,
            modelAdapters: runtime.ModelAdapters,
            configurationStore: _configuration,
            importActivity: _importActivity,
            importFinalizer: _finalizer,
            cancellation: runtime.Cancellation,
            updateCoordinator: _updateCoordinator,
            mediaResources: runtime.MediaResources,
            profileSnapshots: profileSnapshots);

        var services = new AppServices
        {
            Catalog = context.Catalog,
            Paths = context.Paths,
            Runtime = runtime,
            ProfileSnapshots = profileSnapshots,
            Presentation = presentation,
            Navigation = navigation,
            Factory = factory,
            Overlay = overlay,
            ImportActivity = _importActivity,
            Finalizer = _finalizer,
            Status = status,
            Configuration = _configuration!,
            Window = _window!,
            Figures = _figures,
            RequestVaultRenameAsync = RenameVaultAndRestartAsync,
            RequestVaultMoveAsync = MoveVaultAndRestartAsync,
            RequestVaultChangeAsync = ChangeVaultAndRestartAsync,
        };

        _shutdown = new ShutdownCoordinator(
            context,
            shutdownSchedulerAsync: runtime.ShutdownSchedulerAsync,
            services: [profileSnapshots, runtime],
            appState: _appState);

        await runtime.StartAsync(snapshot => UiDispatch.Run(() => status.Apply(snapshot)), cancellationToken).ConfigureAwait(true);
        status.ApplyProfiling(runtime.ProfilingState, runtime.ProfilingStateReason);
        _context = context;
        _services = services;
        _window!.Content = new ProductRoot(services);
        StartFigurePrewarm(context);
    }

    private async Task<VaultTransitionCommandResult> RenameVaultAndRestartAsync(string requestedName)
    {
        var unavailable = ValidateVaultTransitionAvailability("rename");
        if (unavailable is not null)
        {
            return unavailable;
        }

        if (!TryReserveVaultTransition())
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_TRANSITION_ACTIVE",
                "A Vault transition is already in progress.");
        }

        VaultTransitionCommandResult begin;
        try
        {
            begin = await _vaultTransitions!
                .BeginRenameAsync(_context!.Paths.Root, requestedName)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _vaultTransitionStarted, 0);
            return VaultTransitionCommandResult.Failed("VAULT_RENAME_PREFLIGHT_FAILED", exception.Message);
        }

        if (!begin.Succeeded || begin.RenamePlan is null)
        {
            Interlocked.Exchange(ref _vaultTransitionStarted, 0);
            return begin;
        }

        var plan = begin.RenamePlan;
        return await ExecuteVaultTransitionAndRestartAsync(
            UI.F("Vault.Rename.Progress", "Renaming Vault to {0}…", plan.TargetName),
            "VAULT_RENAME_FAILED",
            async retirementProof =>
            {
                var retired = await _vaultTransitions
                    .MarkRuntimeRetiredAsync(plan, retirementProof)
                    .ConfigureAwait(true);
                return retired.Succeeded
                    ? await _vaultTransitions
                        .ApplyRenameAfterRetirementAsync(plan, _configuration!)
                        .ConfigureAwait(true)
                    : retired;
            }).ConfigureAwait(true);
    }

    private async Task<VaultTransitionCommandResult> MoveVaultAndRestartAsync(string targetParent)
    {
        var unavailable = ValidateVaultTransitionAvailability("move");
        if (unavailable is not null)
        {
            return unavailable;
        }

        if (!TryReserveVaultTransition())
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_TRANSITION_ACTIVE",
                "A Vault transition is already in progress.");
        }

        VaultTransitionCommandResult begin;
        try
        {
            begin = await _vaultTransitions!
                .BeginMoveAsync(_context!.Paths.Root, targetParent)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _vaultTransitionStarted, 0);
            return VaultTransitionCommandResult.Failed("VAULT_MOVE_PREFLIGHT_FAILED", exception.Message);
        }

        if (!begin.Succeeded || begin.MovePlan is null)
        {
            Interlocked.Exchange(ref _vaultTransitionStarted, 0);
            return begin;
        }

        var plan = begin.MovePlan;
        var progress = CreateVaultMoveProgressReporter();
        return await ExecuteVaultTransitionAndRestartAsync(
            UI.F("Vault.Move.Progress.Start", "Preparing to move Vault to {0}…", plan.TargetRoot),
            "VAULT_MOVE_FAILED",
            async retirementProof =>
            {
                var retired = await _vaultTransitions
                    .MarkRuntimeRetiredAsync(plan, retirementProof)
                    .ConfigureAwait(true);
                return retired.Succeeded
                    ? await _vaultTransitions
                        .ApplyMoveAfterRetirementAsync(plan, _configuration!, progress)
                        .ConfigureAwait(true)
                    : retired;
            }).ConfigureAwait(true);
    }

    private async Task<VaultTransitionCommandResult> ChangeVaultAndRestartAsync(string targetRoot)
    {
        var unavailable = ValidateVaultTransitionAvailability("change");
        if (unavailable is not null)
        {
            return unavailable;
        }

        if (!TryReserveVaultTransition())
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_TRANSITION_ACTIVE",
                "A Vault transition is already in progress.");
        }

        VaultTransitionCommandResult begin;
        try
        {
            begin = await _vaultTransitions!
                .BeginChangeAsync(_context!.Paths.Root, targetRoot)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _vaultTransitionStarted, 0);
            return VaultTransitionCommandResult.Failed(
                "VAULT_CHANGE_PREFLIGHT_FAILED",
                exception.Message);
        }

        if (!begin.Succeeded || begin.ChangePlan is null)
        {
            Interlocked.Exchange(ref _vaultTransitionStarted, 0);
            return begin;
        }

        var plan = begin.ChangePlan;
        return await ExecuteVaultTransitionAndRestartAsync(
            UI.F(
                "Vault.Change.Progress",
                "Switching to Vault {0}…",
                Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.TargetRoot))),
            "VAULT_CHANGE_FAILED",
            async retirementProof =>
            {
                var retired = await _vaultTransitions
                    .MarkRuntimeRetiredAsync(plan, retirementProof)
                    .ConfigureAwait(true);
                return retired.Succeeded
                    ? await _vaultTransitions
                        .ApplyChangeAfterRetirementAsync(plan, _configuration!)
                        .ConfigureAwait(true)
                    : retired;
            }).ConfigureAwait(true);
    }
    private VaultTransitionCommandResult? ValidateVaultTransitionAvailability(string operation)
    {
        if (_vaultTransitions is null
            || _configuration is null
            || _install is null
            || _context is null
            || _window is null)
        {
            return VaultTransitionCommandResult.Failed(
                $"VAULT_{operation.ToUpperInvariant()}_UNAVAILABLE",
                $"Vault {operation} is unavailable until the Vault is fully ready.");
        }

        return null;
    }

    private bool TryReserveVaultTransition() =>
        Interlocked.CompareExchange(ref _vaultTransitionStarted, 1, 0) == 0;

    private async Task<VaultTransitionCommandResult> ExecuteVaultTransitionAndRestartAsync(
        string initialStatus,
        string failureCode,
        Func<VaultRuntimeRetirementProof, Task<VaultTransitionCommandResult>> execute)
    {
        VaultTransitionCommandResult outcome;
        try
        {
            Interlocked.Exchange(ref _shutdownStarted, 1);
            _window!.Content = StartupScreens.Splash(initialStatus);
            await Task.Yield();

            var retirementProof = await RetireRuntimeForVaultTransitionAsync().ConfigureAwait(true);
            outcome = await execute(retirementProof).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            outcome = VaultTransitionCommandResult.Failed(failureCode, exception.Message);
        }

        if (TryRelaunchApplication())
        {
            _allowClose = true;
            Environment.ExitCode = outcome.Succeeded ? 0 : 1;
            Exit();
        }
        else
        {
            _window!.Content = StartupScreens.Failure(
                UI.T("Vault.RestartFailed.Title", "Restart required"),
                UI.T("Vault.RestartFailed.Message", "naut finished the Vault transition step but could not restart itself."),
                UI.T("Vault.RestartFailed.Next", "Close naut and open it again. Startup recovery will converge the Vault safely."),
                "VAULT_RESTART_FAILED",
                retry: null,
                openDiagnostics: () => _crash?.OpenLocation(),
                exit: () =>
                {
                    _allowClose = true;
                    Exit();
                });
            _allowClose = true;
        }

        return outcome;
    }

    private IProgress<VaultMoveProgress> CreateVaultMoveProgressReporter()
    {
        var lastStage = string.Empty;
        var lastPercent = -1;
        return new Progress<VaultMoveProgress>(progress =>
        {
            var percent = progress.TotalBytes > 0
                ? (int)Math.Clamp(progress.CompletedBytes * 100L / progress.TotalBytes, 0L, 100L)
                : progress.TotalFiles > 0
                    ? (int)Math.Clamp(progress.CompletedFiles * 100L / progress.TotalFiles, 0L, 100L)
                    : 0;
            if (string.Equals(lastStage, progress.Stage, StringComparison.Ordinal)
                && percent == lastPercent)
            {
                return;
            }

            lastStage = progress.Stage;
            lastPercent = percent;
            if (_window is not null)
            {
                _window.Content = StartupScreens.Splash(
                    UI.F(
                        "Vault.Move.Progress.Simple",
                        "Moving Vault — {0}%",
                        percent));
            }
        });
    }
    private async Task FinalizeVaultTransitionAfterBootstrapAsync(
        VaultSuccessfulBootstrapProof bootstrapProof,
        AppServices services)
    {
        var result = await _vaultTransitions!
            .FinalizeSuccessfulBootstrapAsync(bootstrapProof)
            .ConfigureAwait(true);
        if (!result.Succeeded)
        {
            var fallback = result.ErrorCode?.StartsWith("VAULT_MOVE_", StringComparison.Ordinal) == true
                ? UI.T("Vault.Move.CleanupDeferred", "The moved Vault is open, but cleanup of the old source was deferred.")
                : UI.T("Vault.Transition.FinalizeDeferred", "The Vault is open, but transition finalization needs attention.");
            services.Toast(result.SafeErrorDetail ?? fallback, "warning");
        }
    }

    private async Task<VaultRuntimeRetirementProof> RetireRuntimeForVaultTransitionAsync()
    {
        var context = _context
            ?? throw new InvalidOperationException("Vault transition retirement has no active bootstrap context.");

        context.Catalog.MutationAdmission.Close();
        await context.Catalog.MutationAdmission
            .WaitForIdleAsync(CancellationToken.None)
            .ConfigureAwait(true);

        HoverVideoCoordinator.Shared.StopAll();
        SkVideoView.StopAllPlayback();
        await RetireFiguresAsync().ConfigureAwait(true);

        if (_placement is not null)
        {
            await _placement.PersistAsync().ConfigureAwait(true);
            _placement.Dispose();
            _placement = null;
        }

        if (_finalizer is not null)
        {
            await _finalizer.DisposeAsync().ConfigureAwait(true);
            _finalizer = null;
        }

        if (_importActivity is not null)
        {
            await _importActivity.DisposeAsync().ConfigureAwait(true);
            _importActivity = null;
        }

        _updateCoordinator?.Dispose();
        _updateCoordinator = null;
        _updateHttpClient?.Dispose();
        _updateHttpClient = null;

        if (_shutdown is null)
        {
            throw new InvalidOperationException(
                "Vault transition requires the active ShutdownCoordinator authority.");
        }

        var (_, retirementProof) = await _shutdown
            .ShutdownForVaultTransitionAsync(ShutdownCoordinator.DefaultShutdownBudget)
            .ConfigureAwait(true);

        _shutdown = null;
        _profileSnapshots = null;
        _runtime = null;
        _presentation = null;
        _context = null;
        _services = null;

        if (_resourceGovernor is not null)
        {
            _resourceGovernor.TrimRequested -= OnTrimRequested;
        }

        _derivedImages?.Dispose();
        _derivedImages = null;
        _resourceGovernor?.Dispose();
        _resourceGovernor = null;
        return retirementProof;
    }

    private bool TryRelaunchApplication()
    {
        if (_install is null)
        {
            return false;
        }

        var executable = File.Exists(_install.AppExecutablePath)
            ? _install.AppExecutablePath
            : Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return false;
        }

        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? _install.Root,
            };
            return Process.Start(start) is not null;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or IOException)
        {
            Trace.TraceError("Vault transition relaunch failed: {0}", exception.Message);
            return false;
        }
    }

    private void ShowFailure(string? errorCode, Exception? exception, string? preservedCopyPath = null)
    {
        if (exception is not null)
        {
            _crash?.Capture(exception, CrashOrigin.StartupFailure);
        }

        var code = errorCode ?? StartupRecoveryViewModel.GenericErrorCode;
        Action? primary = null;
        string? primaryLabel = null;
        var (title, message, next, canRetry) = code.StartsWith("VAULT_TRANSITION_", StringComparison.Ordinal)
            ? (
                UI.T("Vault.Recovery.Title", "Vault recovery needs attention"),
                UI.T("Vault.Recovery.Message", "naut stopped automatic Vault transition recovery because filesystem state is ambiguous or configuration could not be converged safely."),
                UI.T("Vault.Recovery.Next", "Do not delete or rename either Vault folder. Open diagnostics and verify the folders before trying again."),
                false)
            : code switch
        {
            "VAULT_LOCKED" or "VAULT_LOCK_UNAVAILABLE" => (UI.T("Startup.Locked.Title", "naut is already open"), UI.T("Startup.Locked.Message", "Another window is using this Vault."), UI.T("Startup.Locked.Next", "Close the other window, then try again."), true),
            "VAULT_ROOT_MISSING" => (UI.T("Startup.Vault.MissingTitle", "The Vault folder can't be found"), UI.T("Startup.Vault.MissingMessage", "The saved Vault folder is missing or currently unavailable. naut did not recreate it."), UI.T("Startup.Vault.MissingNext", "Locate the existing Vault or choose a new Vault folder."), true),
            "VAULT_ROOT_INVALID" => (UI.T("Startup.Vault.Title", "The Vault folder can't be used"), UI.T("Startup.Vault.Message", "The configured Vault folder is not a safe, writable location."), UI.T("Startup.Vault.Next", "Choose a different Vault folder, then try again."), true),
            "VAULT_SELECTION_FOLDER_MISSING" or "VAULT_SELECTION_NOT_VAULT_OR_EMPTY" or "VAULT_SELECTION_INVALID" => (UI.T("Startup.Vault.Title", "The Vault folder can't be used"), UI.T("Startup.Vault.SelectionMessage", "Choose an existing naut Vault or an empty folder that you created in the Windows folder picker."), UI.T("Startup.Vault.SelectionNext", "The app does not create the selected folder. Create a new folder in Windows first if you want a new Vault."), true),
            "VAULT_CHANGE_RETURN_RESTART_FAILED" => (UI.T("Vault.RestartFailed.Title", "Restart required"), UI.T("Vault.Change.ReturnRestartMessage", "The previous Vault was restored, but naut could not restart itself."), UI.T("Vault.Change.ReturnRestartNext", "Close naut and open it again to return to the previous Vault."), false),
            "VAULT_CHANGE_RECOVERY_FAILED" or "VAULT_CHANGE_PREVIOUS_ROOT_MISSING" or "VAULT_CHANGE_PREVIOUS_ROOT_INVALID" => (UI.T("Vault.Recovery.Title", "Vault recovery needs attention"), UI.T("Vault.Change.RecoveryMessage", "The selected Vault could not open and the previous Vault could not be restored automatically."), UI.T("Vault.Recovery.Next", "Do not delete or rename either Vault folder. Open diagnostics and verify the folders before trying again."), false),
            "CONFIGURATION_UNREADABLE" => (UI.T("Startup.Config.Title", "Settings could not be read"), UI.T("Startup.Config.UnreadableMessage", "naut could not read its configuration. The original file was preserved for diagnostics. Reset settings to a safe default, then choose your Vault folder again."), UI.T("Startup.Config.UnreadableNext", "Reset settings safely to continue, or open diagnostics to inspect the preserved file."), true),
            "CONFIGURATION_ACCESS_DENIED" => (UI.T("Startup.Config.Title", "Settings could not be read"), UI.T("Startup.Config.Message", "naut could not read its configuration. The original file was kept."), UI.T("Startup.Config.Next", "Check folder permissions, then try again."), true),
            "STARTUP_ROLLBACK_UNSAFE" => (UI.T("Startup.Rollback.Title", "naut must close"), UI.T("Startup.Rollback.Message", "Startup stopped after the Vault was opened, and naut could not confirm a complete shutdown."), UI.T("Startup.Rollback.Next", "Close naut before trying again so the Vault remains safely locked to one app instance."), false),
            _ => (UI.T("Startup.Failed.Title", "naut could not start"), UI.T("Startup.Failed.Message", "Something prevented your Vault from opening. Nothing was changed."), UI.T("Startup.Failed.Next", "Try again. If it keeps happening, open diagnostics."), true),
        };

        if (code is "VAULT_ROOT_MISSING" or "VAULT_ROOT_INVALID"
            || code.StartsWith("VAULT_SELECTION_", StringComparison.Ordinal))
        {
            primary = RecoverVaultRootAsync;
            primaryLabel = UI.T("Startup.Vault.Choose", "Locate or choose Vault folder…");
        }
        else if (code == "CONFIGURATION_UNREADABLE")
        {
            primary = ResetConfigurationSafely;
            primaryLabel = UI.T("Startup.Config.Reset", "Reset settings safely…");
            if (!string.IsNullOrWhiteSpace(preservedCopyPath))
            {
                next = UI.T("Startup.Config.PreservedAt", "The original was preserved for diagnostics.") + " " + preservedCopyPath;
            }
        }

        _window!.Content = StartupScreens.Failure(
            title,
            message,
            next,
            code,
            canRetry ? () => ObserveStartup(BootstrapLoopAsync(), "App.BootstrapRetry") : null,
            () => _crash?.OpenLocation(),
            Exit,
            primary,
            primaryLabel);
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        TaskObserver.Observe(
            ControlledShutdownAsync(0),
            "App.WindowShutdown",
            exception => _crash?.Capture(exception, CrashOrigin.ControlledShutdownFailure));
    }

    private void RequestControlledShutdown() =>
        UiDispatch.Post(() => TaskObserver.Observe(
            ControlledShutdownAsync(-1),
            "App.ControlledShutdown",
            exception => _crash?.Capture(exception, CrashOrigin.ControlledShutdownFailure)));

    private void RequestUpdateShutdown(DateTimeOffset shutdownDeadlineUtc) =>
        UiDispatch.Post(() => TaskObserver.Observe(
            ControlledShutdownAsync(0, shutdownDeadlineUtc),
            "App.UpdateShutdown",
            exception => _crash?.Capture(exception, CrashOrigin.ControlledShutdownFailure)));

    private async Task ControlledShutdownAsync(
        int exitCode,
        DateTimeOffset? shutdownDeadlineUtc = null)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        var absoluteDeadlineUtc = shutdownDeadlineUtc
            ?? DateTimeOffset.UtcNow.Add(ShutdownCoordinator.DefaultShutdownBudget);

        // close user mutation admission synchronously at top-level shutdown entry,
        // before persistence/finalizer/activity awaits can consume the shared deadline.
        _context?.Catalog.MutationAdmission.Close();

        try
        {
            HoverVideoCoordinator.Shared.StopAll();
            SkVideoView.QuiesceForShutdown();
            if (_window?.Content is ProductRoot root)
            {
                root.ShowClosingOverlay();
            }
            if (_window?.Content is UIElement content)
            {
                content.IsHitTestVisible = false;
            }
            await RetireFiguresAsync().ConfigureAwait(true);

            if (_context is not null)
            {
                var remainingForCommandDrain = absoluteDeadlineUtc - DateTimeOffset.UtcNow;
                if (remainingForCommandDrain > TimeSpan.Zero)
                {
                    using var commandDrain = new CancellationTokenSource(remainingForCommandDrain);
                    try
                    {
                        await _context.Catalog.MutationAdmission
                            .WaitForIdleAsync(commandDrain.Token)
                            .ConfigureAwait(true);
                    }
                    catch (OperationCanceledException) when (commandDrain.IsCancellationRequested)
                    {
                        // Safety outranks the advertised deadline. The updater shares the same
                        // absolute deadline and will deterministically defer before InstallRoot
                        // mutation if the process cannot quiesce in time.
                        await _context.Catalog.MutationAdmission
                            .WaitForIdleAsync(CancellationToken.None)
                            .ConfigureAwait(true);
                    }
                }
                else
                {
                    await _context.Catalog.MutationAdmission
                        .WaitForIdleAsync(CancellationToken.None)
                        .ConfigureAwait(true);
                }
            }

            if (_placement is not null)
            {
                await _placement.PersistAsync().ConfigureAwait(true);
            }

            // Stop finishing imports first; an in-flight commit stops at a durable checkpoint and resumes next start.
            if (_finalizer is not null)
            {
                await _finalizer.DisposeAsync().ConfigureAwait(true);
                _finalizer = null;
            }

            if (_importActivity is not null)
            {
                await _importActivity.DisposeAsync().ConfigureAwait(true);
                _importActivity = null;
            }

            if (_shutdown is not null)
            {
                var remaining = absoluteDeadlineUtc - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    remaining = TimeSpan.FromMilliseconds(1);
                }

                var report = await _shutdown.ShutdownAsync(remaining).ConfigureAwait(true);
                Trace.TraceInformation("Shutdown: timedOut={0}, remainingJobs={1}, cancelled={2}, workerReleased={3}",
                    report.TimedOut, report.NonterminalJobsLeftForRestart, report.CancelledDuringShutdown, report.ProfilingWorkerReleased);
            }
            else
            {
                if (_profileSnapshots is not null)
                {
                    await _profileSnapshots.DisposeAsync().ConfigureAwait(true);
                    _profileSnapshots = null;
                }

                if (_runtime is not null)
                {
                    await _runtime.DisposeAsync().ConfigureAwait(true);
                }

                _context?.Dispose();
            }
        }
        catch (Exception exception)
        {
            _crash?.Capture(exception, CrashOrigin.ControlledShutdownFailure);
        }
        finally
        {
            await RetireFiguresAsync().ConfigureAwait(true);
            _placement?.Dispose();
            _placement = null;
            if (_resourceGovernor is not null)
            {
                _resourceGovernor.TrimRequested -= OnTrimRequested;
            }
            _derivedImages?.Dispose();
            _derivedImages = null;
            _resourceGovernor?.Dispose();
            _resourceGovernor = null;
            _updateCoordinator?.Dispose();
            _updateCoordinator = null;
            _profileSnapshots = null;
            _updateHttpClient?.Dispose();
            _updateHttpClient = null;
            _crash?.Dispose();
            _allowClose = true;
            Environment.ExitCode = exitCode;
            Exit();
        }
    }

    private CrashDiagnosticContext CreateCrashContext() => new(
        ProductIdentity.Version,
        _context?.SchemaVersion.ToString("D4"),
        _context?.Paths.Root,
        _shutdown?.State ?? _bootstrapper?.State ?? StartupState.ProcessStart);

    private sealed record VaultRootResolution(VaultPaths? VaultPaths, string? ErrorCode, string? PreservedCopyPath = null);
}
