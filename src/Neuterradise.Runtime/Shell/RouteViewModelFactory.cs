using Neuterradise.App.Faces;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Gallery;
using Neuterradise.App.Home;
using Neuterradise.App.Import;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Profiles;
using Neuterradise.App.Settings;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Updates;

namespace Neuterradise.App.Shell;

public sealed class RouteErrorViewModel(AppRoute route, string reason)
{
    public AppRoute Route { get; } = route;

    public string Reason { get; } = reason;
}

public sealed class RouteViewModelFactory(
    CatalogDb? catalog = null,
    NavigationCoordinator? navigation = null,
    OverlayHostViewModel? overlay = null,
    ModelPreviewAdapterRegistry? modelAdapters = null,
    AppConfigurationStore? configurationStore = null,
    ImportActivityService? importActivity = null,
    ImportFinalizer? importFinalizer = null,
    JobCancellationOperations? cancellation = null,
    UpdateCoordinator? updateCoordinator = null,
    MediaResourceAuthority? mediaResources = null,
    ProfileRuntimeSnapshotCache? profileSnapshots = null)
{
    /// <summary>The application-wide import finalizer; Import pages hand confirmed imports to it.</summary>
    private readonly ImportFinalizer? _importFinalizer = importFinalizer;
    private readonly JobCancellationOperations? _cancellation = cancellation;
    private readonly CatalogDb? _catalog = catalog;
    private readonly NavigationCoordinator? _navigation = navigation;
    private readonly ModelPreviewAdapterRegistry? _modelAdapters = modelAdapters;
    private readonly MediaResourceAuthority? _mediaResources = mediaResources;
    private readonly ProfileRuntimeSnapshotCache? _profileSnapshots = profileSnapshots;
    private readonly AppConfigurationStore? _configurationStore = configurationStore;
    private readonly UpdateCoordinator? _updateCoordinator = updateCoordinator;

    /// <summary>
    /// The application-wide live import feed. Sharing one instance keeps Home, Import and the shell
    /// chrome describing the same reality, and keeps the poll cost to a single reader.
    /// </summary>
    private readonly ImportActivityService? _importActivity = importActivity;

    public OverlayHostViewModel? Overlay { get; set; } = overlay;

    public object Create(AppRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        try
        {
            // Warm navigation must not rebuild app-wide infrastructure; route creation is timed so a
            // regression shows up in diagnostics (budget: well under a frame-visible stall).
            using var measure = Neuterradise.App.SystemServices.Diagnostics.PerfTrace.Measure("route.create." + route.GetType().Name, 100);
            return CreateCore(route);
        }
        catch (Exception exception)
        {
            return new RouteErrorViewModel(route, exception.Message);
        }
    }

    private object CreateCore(AppRoute route) => route switch
    {
        HomeRoute => new HomeViewModel(
            _catalog,
            activityService: _importActivity,
            mediaResources: _mediaResources),
        GalleryRoute gallery => new GalleryViewModel(
            _catalog,
            _navigation,
            overlay: Overlay,
            origin: gallery.Origin,
            mediaResources: _mediaResources),
        ImportRoute => new ImportViewModel(
            _catalog,
            _navigation,
            activityService: _importActivity,
            finalizer: _importFinalizer,
            cancellation: _cancellation),
        SettingsRoute settings => new SettingsViewModel(
            settings.Section,
            settings.Subsection,
            _catalog,
            _navigation,
            configurationStore: _configurationStore,
            updateCoordinator: _updateCoordinator),
        ProfileRoute profile => new ProfileDetailViewModel(
            profile.ProfileId,
            _catalog,
            _navigation,
            overlay: Overlay,
            initialOrigin: profile.Origin,
            modelAdapters: _modelAdapters,
            initialInspectMediaId: profile.InspectMediaId,
            mediaResources: _mediaResources,
            runtimeSnapshots: _profileSnapshots),
        FaceReviewRoute faceReview => new FaceReviewViewModel(
            faceReview.ProfileId,
            _catalog,
            _navigation,
            overlay: Overlay),
        _ => throw new NotSupportedException(
            $"No view model is mapped for route '{route.GetType().Name}'."),
    };
}
