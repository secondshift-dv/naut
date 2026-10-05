using System.Diagnostics;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Input;
using Neuterradise.App.Faces;
using Neuterradise.App.Gallery;
using Neuterradise.App.Home;
using Neuterradise.App.Import;
using Neuterradise.App.Import.Intake;
using Neuterradise.App.Localization;
using Neuterradise.App.Maintenance;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Resources;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.Trash;
using Windows.System;

namespace Neuterradise.App.Ui;

/// <summary>
/// The product shell below the native Windows title bar: primary navigation (Home, Gallery, Import,
/// Settings — no global
/// sidebar), a content host that keeps the four top-level surfaces warm, a bounded LRU for contextual
/// surfaces, and the overlay / Customization / toast layers. Interaction feeds the ResourceGovernor.
/// </summary>
public sealed class ProductRoot : Grid
{
    private const int ContextualCapacity = 3;

    private readonly AppServices _services;
    private readonly Grid _content = new();
    private readonly Grid _presentationWorkspace = new();
    private readonly Grid _overlayLayer = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _toasts = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 20, 20) };
    private readonly Dictionary<Type, Surface> _topLevel = [];
    private readonly LinkedList<(AppRoute Route, Surface Surface)> _contextual = new();
    private readonly List<(AppRoute Route, LivingNavigationButton Button, string LabelKey, string Fallback)> _navButtons = [];
    private readonly Grid _customizationLayer = new() { Visibility = Visibility.Collapsed };
    private LivingNavigationButton? _vaultButton;
    private LivingNavigationButton? _customizeButton;
    private FrameworkElement? _primaryNavigationHost;
    private readonly StackPanel _breadcrumb = UI.H(4);
    private readonly Border _breadcrumbHost = new();
    private Surface? _current;
    private ProfileDetailViewModel? _breadcrumbProfile;
    private CustomizationCenter? _customization;
    private TextBlock? _activityHeadline;
    private Border? _activityPill;
    private ProgressBar? _activityProgress;
    private IconView? _activityIcon;
    private bool _activityWasRunning;
    private bool _languageRefreshQueued;

    public ProductRoot(AppServices services)
    {
        _services = services;
        PresentationEffects.Runtime = services.Presentation;
        services.Root = this;
        Dialogs = new DialogService(this);
        Dialogs.Changed += RefreshFigureOcclusion;
        Overlays = new OverlayPresenter(services, this);
        Background = ThemeRuntime.Current.Brush("canvas");
        ApplyNativeTheme();
        ThemeRuntime.Current.Changed += OnThemeChanged;
        Loaded += (_, _) =>
        {
            ThemeRuntime.Current.TrackNativeControls(this);
            ThemeRuntime.Current.Changed -= OnThemeChanged;
            ThemeRuntime.Current.Changed += OnThemeChanged;
            ApplyNativeTheme();
            ObserveBreadcrumbProfile();
        };
        Unloaded += (_, _) =>
        {
            ThemeRuntime.Current.Changed -= OnThemeChanged;
            if (_breadcrumbProfile is not null) _breadcrumbProfile.PropertyChanged -= OnBreadcrumbProfileChanged;
            _breadcrumbProfile = null;
        };
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        WindowChrome.UseNativeTitleBar(services.Window);

        var shell = UI.Grid("auto,auto,*", "*");
        _primaryNavigationHost = BuildPrimaryNavigation();
        shell.Children.Add(_primaryNavigationHost.At(0));
        shell.Children.Add(BuildBreadcrumb().At(1));
        _presentationWorkspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _presentationWorkspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        _presentationWorkspace.Children.Add(_content.At(0, 0));
        _presentationWorkspace.Children.Add(_customizationLayer.At(0, 1));
        shell.Children.Add(_presentationWorkspace.At(2));
        Children.Add(shell);

        Children.Add(_overlayLayer);
        Children.Add(_toasts);
        services.Navigation.Navigated += OnNavigated;
        services.Overlay.PropertyChanged += (_, _) => UiDispatch.Run(Overlays.Sync);
        services.Status.PropertyChanged += (_, _) => UiDispatch.Run(UpdateActivity);
        services.Presentation.Changed += (_, args) => UiDispatch.Run(() => OnPresentationChanged(args));
        SurfaceText.LanguageChanged += (_, _) => QueueLanguageRefresh();

        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Pointer)), true);
        SidebarDismissal.Observe(this, point =>
        {
            return _customization is { IsEditor: false } customization
                && _overlayLayer.Visibility != Visibility.Visible && !customization.ContainsPointer(point, this);
        }, () => _customization?.RequestClose());
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        SizeChanged += (_, _) =>
        {
            ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Resize);
            UpdateCustomizationLayout();
        };

        RegisterAccelerators();
        UpdateActivity();
        Show(services.Navigation.CurrentRoute);
    }

    public AppServices Services => _services;

    public DialogService Dialogs { get; }

    public OverlayPresenter Overlays { get; }

    internal Grid OverlayLayer => _overlayLayer;

    private void OnThemeChanged() => UiDispatch.Run(() =>
    {
        ApplyNativeTheme();
        UpdateNavSelection();
        UpdateContextualAction();
    });

    private void ApplyNativeTheme() => RequestedTheme = ThemeRuntime.Current.NativeElementTheme;

    private FrameworkElement BuildPrimaryNavigation()
    {
        var theme = ThemeRuntime.Current;
        var height = theme.Tokens.Number("chromeHeight", 50);
        var navigation = UI.Grid("*", "auto,*,auto");
        theme.TrackThemeElement(navigation, nav =>
        {
            nav.Height = ThemeRuntime.Current.Tokens.Number("chromeHeight", height);
            nav.Background = ThemeRuntime.Current.Brush("chromeBackground");
            nav.BorderBrush = ThemeRuntime.Current.Brush("chromeBorder");
            nav.BorderThickness = new Thickness(0, 0, 0, ThemeRuntime.Current.Tokens.Number("chromeBorderThickness", 1));
        });

        var vaultName = Path.GetFileName(_services.Paths.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _vaultButton = new LivingNavigationButton(
            LivingNavigationRole.Vault,
            "Vault",
            () => NavigateTop(new SettingsRoute(SettingsSection.Vault)),
            secondaryLabel: vaultName)
        {
            MinWidth = 128,
            MaxWidth = 280,
            Margin = new Thickness(10, 0, 14, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _vaultButton.Tip(_services.Paths.Root);
        navigation.Children.Add(_vaultButton.At(0, 0));

        var nav = UI.H(4);
        nav.VerticalAlignment = VerticalAlignment.Center;
        foreach (var (route, key, fallback, role) in new (AppRoute, string, string, LivingNavigationRole)[]
        {
            (new HomeRoute(), "Nav.Home", "Home", LivingNavigationRole.Home),
            (new GalleryRoute(), "Nav.Gallery", "Gallery", LivingNavigationRole.Gallery),
            (new ImportRoute(), "Nav.Import", "Import", LivingNavigationRole.Import),
            (new SettingsRoute(), "Nav.Settings", "Settings", LivingNavigationRole.Settings),
        })
        {
            var button = new LivingNavigationButton(
                role,
                UI.T(key, fallback),
                () => NavigateTop(route));
            _navButtons.Add((route, button, key, fallback));
            nav.Children.Add(button);
        }

        navigation.Children.Add(nav.At(0, 1));

        _activityHeadline = UI.Text(string.Empty, "caption", "textSecondary", 1);
        _activityProgress = new ProgressBar { Width = 72, Height = 3, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 4, 0, 0) };
        _activityIcon = new IconView("icon.status.activity", 14, "accent");
        _activityPill = UI.Surface(UI.H(8, _activityIcon, UI.V(0, _activityHeadline, _activityProgress)), Material.Frost, 8, 6);
        _activityPill.Padding = new Thickness(10, 4, 10, 4);
        _activityPill.MaxWidth = 260;
        _activityPill.VerticalAlignment = VerticalAlignment.Center;
        UI.DisableTextSelection(_activityPill);
        _activityPill.Tapped += (_, _) => NavigateTop(new ImportRoute());

        _customizeButton = new LivingNavigationButton(
            LivingNavigationRole.Customize,
            UI.T("Home.Customize", "Customize Home"),
            OpenCurrentCustomization,
            signature: true)
        {
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var utilities = UI.H(6, _activityPill, _customizeButton);
        utilities.VerticalAlignment = VerticalAlignment.Center;
        utilities.Margin = new Thickness(0, 0, 8, 0);
        navigation.Children.Add(utilities.At(0, 2));

        navigation.SizeChanged += (_, _) => UpdateResponsiveTopBar(navigation.ActualWidth);
        return navigation;
    }

    private FrameworkElement BuildBreadcrumb()
    {
        var theme = ThemeRuntime.Current;
        _breadcrumbHost.Child = _breadcrumb;
        _breadcrumbHost.Padding = new Thickness(10, 0, 10, 0);
        _breadcrumbHost.Height = theme.Tokens.Number("breadcrumbHeight", 36);
        _breadcrumb.Visibility = Visibility.Collapsed;
        _breadcrumbHost.Visibility = Visibility.Collapsed;
        _breadcrumbHost.Background = theme.Brush("surface2");
        _breadcrumbHost.BorderBrush = theme.Brush("borderSubtle");
        _breadcrumbHost.BorderThickness = new Thickness(0, 0, 0, 1);
        return _breadcrumbHost;
    }

    private void UpdateBreadcrumb(AppRoute route)
    {
        _breadcrumb.Children.Clear();
        var items = NavigationContextResolver.Breadcrumb(route);
        if (items.Count <= 1)
        {
            _breadcrumb.Visibility = Visibility.Collapsed;
            _breadcrumbHost.Visibility = Visibility.Collapsed;
            return;
        }

        _breadcrumbHost.Visibility = Visibility.Visible;
        _breadcrumb.Visibility = Visibility.Visible;
        for (var index = 0; index < items.Count; index++)
        {
            if (index > 0)
            {
                _breadcrumb.Children.Add(UI.Text("/", "caption", "textMuted").Align(vertical: VerticalAlignment.Center));
            }

            var item = items[index];
            if (route is ProfileRoute && index == items.Count - 1 && _current?.Model is ProfileDetailViewModel profile
                && !string.IsNullOrWhiteSpace(profile.DisplayName))
            {
                item = item with { Label = profile.DisplayName };
            }
            if (item.Target is null)
            {
                _breadcrumb.Children.Add(UI.Text(item.Label, "caption", "textPrimary").Align(vertical: VerticalAlignment.Center));
            }
            else
            {
                _breadcrumb.Children.Add(UI.Button(item.Label, () => _services.Navigation.Navigate(item.Target), ButtonKind.Ghost));
            }
        }
    }

    private void ObserveBreadcrumbProfile()
    {
        var profile = _current?.Model as ProfileDetailViewModel;
        if (ReferenceEquals(profile, _breadcrumbProfile)) return;
        if (_breadcrumbProfile is not null) _breadcrumbProfile.PropertyChanged -= OnBreadcrumbProfileChanged;
        _breadcrumbProfile = profile;
        if (_breadcrumbProfile is not null) _breadcrumbProfile.PropertyChanged += OnBreadcrumbProfileChanged;
    }

    private void OnBreadcrumbProfileChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or nameof(ProfileDetailViewModel.DisplayName))
            UiDispatch.Run(() => UpdateBreadcrumb(_services.Navigation.CurrentRoute));
    }

    private void UpdateShellLanguage()
    {
        foreach (var (_, button, key, fallback) in _navButtons)
        {
            button.Label = UI.T(key, fallback);
        }

        UpdateBreadcrumb(_services.Navigation.CurrentRoute);
        UpdateContextualAction();
    }

    private void QueueLanguageRefresh()
    {
        if (_languageRefreshQueued)
        {
            return;
        }

        _languageRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(RefreshLanguage))
        {
            _languageRefreshQueued = false;
            UiDispatch.Run(RefreshLanguage);
        }
    }

    private void RefreshLanguage()
    {
        _languageRefreshQueued = false;
        UpdateShellLanguage();

        var route = _services.Navigation.CurrentRoute;
        if (_customization is not null)
        {
            _customization.Close(apply: false);
        }

        _current?.Suspend();
        _current = null;

        foreach (var surface in _topLevel.Values)
        {
            _content.Children.Remove(surface.View);
            surface.Dispose();
        }
        _topLevel.Clear();

        foreach (var (_, surface) in _contextual)
        {
            _content.Children.Remove(surface.View);
            surface.Dispose();
        }
        _contextual.Clear();
        _content.Children.Clear();

        Show(route);
        Overlays.Sync();
        UpdateActivity();
    }

    private void UpdateActivity()
    {
        var status = _services.Status;
        if (_activityPill is null || _activityHeadline is null || _activityProgress is null)
        {
            return;
        }

        _activityPill.Visibility = status.HasVisibleStatus ? Visibility.Visible : Visibility.Collapsed;
        _activityHeadline.Text = status.Headline;
        _activityPill.Tip(status.HeadlineDetail);
        _activityProgress.IsIndeterminate = status.IsProgressIndeterminate;
        _activityProgress.Value = status.Progress ?? 0;
        _activityProgress.Visibility = status.HasRunningWork ? Visibility.Visible : Visibility.Collapsed;
        if (status.HasRunningWork && !_activityWasRunning && _activityIcon is not null)
        {
            UI.AnimateStatusIcon(_activityIcon, "activity");
        }

        _activityWasRunning = status.HasRunningWork;
    }

    public void RefreshContextualActions() => UiDispatch.Run(UpdateContextualAction);

    private void OpenCurrentCustomization()
    {
        if (_current is ITopBarCustomizableSurface customizable && customizable.CanTopBarCustomize)
        {
            customizable.OpenTopBarCustomization();
        }
    }

    private void UpdateContextualAction()
    {
        if (_customizeButton is null)
        {
            return;
        }

        if (_current is ITopBarCustomizableSurface customizable && customizable.CanTopBarCustomize)
        {
            var customizationOpen = _customization is not null;
            _customizeButton.Label = customizable.TopBarCustomizeLabel;
            _customizeButton.IsEnabled = !customizationOpen;
            _customizeButton.Visibility = Visibility.Visible;
            _customizeButton.SetSelected(customizationOpen);
        }
        else
        {
            _customizeButton.IsEnabled = false;
            _customizeButton.Visibility = Visibility.Collapsed;
            _customizeButton.SetSelected(false);
        }
    }

    private void UpdateResponsiveTopBar(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var compactUtilities = width < 980;
        if (_activityHeadline is not null)
        {
            _activityHeadline.Visibility = compactUtilities ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_activityProgress is not null)
        {
            _activityProgress.Width = compactUtilities ? 44 : 72;
        }

        foreach (var item in _navButtons) item.Button.SetCompact(width < 600);
        _customizeButton?.SetCompact(width < 900);
        _vaultButton?.SetSecondaryMaxWidth(width < 920 ? 82 : 150);
    }

    private void NavigateTop(AppRoute route)
    {
        ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Navigation);
        if (_customization is not null)
        {
            _customization.RequestClose();
            return;
        }
        if (_current is GallerySurface gallery && _services.Navigation.CurrentRoute is GalleryRoute)
        {
            _services.Navigation.UpdateCurrentRoute(new GalleryRoute(gallery.CaptureOrigin()));
        }

        _services.Navigation.ResetToTopLevel(route);
    }

    private bool IsSelectedNav(AppRoute route) =>
        NavigationContextResolver.OwningTopLevel(_services.Navigation.CurrentRoute)?.GetType() == route.GetType();

    private void UpdateNavSelection()
    {
        foreach (var (route, button, _, _) in _navButtons)
        {
            button.SetSelected(IsSelectedNav(route));
        }

        var currentRoute = _services.Navigation.CurrentRoute;
        _vaultButton?.SetSelected(
            currentRoute is SettingsRoute { Section: SettingsSection.Vault }
            or VaultHealthRoute);
    }

    private void OnNavigated(object? sender, NavigationChangedEventArgs e) => UiDispatch.Run(() => Show(e.Current));

    private void Show(AppRoute route)
    {
        HoverVideoCoordinator.Shared.StopAll();
        var next = ResolveSurface(route);
        var surfaceChanged = !ReferenceEquals(next, _current);
        if (surfaceChanged)
        {
            _current?.Suspend();
            foreach (UIElement child in _content.Children)
            {
                child.Visibility = Visibility.Collapsed;
            }

            if (!_content.Children.Contains(next.View))
            {
                _content.Children.Add(next.View);
            }

            next.View.Visibility = Visibility.Visible;
            AnimateEntrance(next.View);
            _current = next;
        }

        if (surfaceChanged || next.LastRoute != route)
        {
            next.Activate(route);
        }
        ObserveBreadcrumbProfile();
        UpdateBreadcrumb(route);
        UpdateNavSelection();
        UpdateContextualAction();
        ThemeRuntime.Current.TrackNativeControls(next.View);
        RefreshFigureOcclusion();
    }

    public string? CurrentSurfaceName => _current?.GetType().Name;

    public bool IsCurrentSurfaceRendered => _current is { View: { ActualWidth: > 0, ActualHeight: > 0 } };

    public ScreenStatus? CurrentContentStatus => _current?.Model?.Status;

    private Surface ResolveSurface(AppRoute route)
    {
        try
        {
            return ResolveSurfaceCore(route);
        }
        catch (Exception exception)
        {
            Trace.TraceError("Surface for route {0} could not be created: {1}", route, exception);
            return new RouteFailureSurface(_services, route, () => Show(route));
        }
    }

    private Surface ResolveSurfaceCore(AppRoute route)
    {
        if (route is HomeRoute or GalleryRoute or ImportRoute or SettingsRoute)
        {
            var key = route.GetType();
            if (!_topLevel.TryGetValue(key, out var surface))
            {
                surface = route switch
                {
                    HomeRoute => new HomeSurface(_services, CreateModel<HomeViewModel>(route)),
                    GalleryRoute => new GallerySurface(_services, CreateModel<GalleryViewModel>(route)),
                    ImportRoute => new ImportSurface(_services, CreateModel<ImportViewModel>(route)),
                    _ => new SettingsSurface(_services, CreateModel<SettingsViewModel>(route)),
                };
                _topLevel[key] = surface;
            }

            return surface;
        }

        if (route is TrashRoute or VaultHealthRoute)
        {
            var settings = ResolveSurface(new SettingsRoute());
            return settings;
        }

        for (var node = _contextual.First; node is not null; node = node.Next)
        {
            if (SameContext(node.Value.Route, route))
            {
                node.Value = (route, node.Value.Surface);
                _contextual.Remove(node);
                _contextual.AddFirst(node);
                return node.Value.Surface;
            }
        }

        Surface created = route switch
        {
            ProfileRoute profile => new ProfileSurface(_services, CreateModel<ProfileDetailViewModel>(profile)),
            FaceReviewRoute face => new PeopleSurface(_services, CreateModel<FaceReviewViewModel>(face)),
            _ => throw new NotSupportedException($"No surface for {route.GetType().Name}."),
        };
        _contextual.AddFirst((route, created));
        while (_contextual.Count > ContextualCapacity)
        {
            var candidate = _contextual.Last;
            while (candidate is not null && ReferenceEquals(candidate.Value.Surface, _current))
            {
                candidate = candidate.Previous;
            }

            if (candidate is null)
            {
                break;
            }

            var evicted = candidate.Value;
            _contextual.Remove(candidate);
            _content.Children.Remove(evicted.Surface.View);
            evicted.Surface.Dispose();
        }

        return created;
    }

    private T CreateModel<T>(AppRoute route) where T : class
    {
        var model = _services.Factory.Create(route);
        return model as T ?? throw new InvalidOperationException(
            model is RouteErrorViewModel error
                ? $"{route.GetType().Name} view model creation failed: {error.Reason}"
                : $"{route.GetType().Name} resolved an incompatible view model.");
    }

    private static bool SameContext(AppRoute a, AppRoute b) => (a, b) switch
    {
        (ProfileRoute x, ProfileRoute y) => x.ProfileId == y.ProfileId,
        (FaceReviewRoute x, FaceReviewRoute y) => x.ProfileId == y.ProfileId,
        _ => false,
    };

    private void AnimateEntrance(FrameworkElement view)
    {
        var theme = ThemeRuntime.Current;
        var duration = theme.Duration("normal");
        if (duration <= 0)
        {
            view.Opacity = 1;
            return;
        }

        var transform = new TranslateTransform();
        view.RenderTransform = transform;
        var storyboard = new Storyboard();
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(duration)) };
        Storyboard.SetTarget(fade, view);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);
        var slide = new DoubleAnimation
        {
            From = theme.Tokens.Number("motionPageTranslate", 12),
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(duration)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "Y");
        storyboard.Children.Add(slide);

        storyboard.Begin();
    }

    public void ShowCustomization(string category, PresentationContext context, string? slot, object? subject)
    {
        if (_customization is not null)
        {
            _customization.RequestClose();
            return;
        }
        if (_current is ProfileSurface profileSurface)
            profileSurface.CloseInspectorForCustomization();
        _customization = new CustomizationCenter(_services, category, context, slot, subject, () => HideCustomization(),
            () => _current is ProfileSurface profile ? profile.RefreshCommittedAppearanceAsync() : Task.CompletedTask);
        _customizationLayer.Children.Clear();
        _customizationLayer.Children.Add(_customization.View);
        _customizationLayer.Visibility = Visibility.Visible;
        SetCustomizationChromeLocked(true);
        _customization.EditorChanged += _ => UpdateCustomizationLayout();
        _customization.PreviewMediaChanged += UpdatePreviewMedia;
        _current?.SetPreviewSession(_customization.Session);
        _customization.PublishPreviewMedia();
        UpdateCustomizationLayout();
    }

    public void HideCustomization()
    {
        if (_customizationLayer.Visibility == Visibility.Collapsed)
        {
            return;
        }

        _customizationLayer.Visibility = Visibility.Collapsed;
        _customizationLayer.Children.Clear();
        var closing = _customization;
        _customization = null;
        closing?.Dispose();
        _current?.SetPreviewSession(null);
        SetCustomizationChromeLocked(false);
        UpdateCustomizationLayout();
        UpdateContextualAction();
    }

    private void SetCustomizationChromeLocked(bool locked)
    {
        if (_primaryNavigationHost is not null)
        {
            _primaryNavigationHost.IsHitTestVisible = !locked;
        }

        foreach (var item in _navButtons)
        {
            item.Button.IsEnabled = !locked;
        }

        if (_vaultButton is not null) _vaultButton.IsEnabled = !locked;
        if (_customizeButton is not null)
        {
            _customizeButton.IsEnabled = !locked;
            _customizeButton.SetSelected(locked);
        }

        if (_activityPill is not null) _activityPill.IsHitTestVisible = !locked;
        _breadcrumbHost.IsHitTestVisible = !locked;
    }

    private void UpdatePreviewMedia(ImageRef? cover, string? bannerPath)
    {
        if (_current is ProfileSurface profile) profile.SetPreviewMedia(cover, bannerPath);
    }

    private void RefreshFigureOcclusion()
    {
        // Native children occupy HWND airspace; XAML modal scrims cannot cover them.
        if (_current is ProfileSurface profile)
            profile.SetFigureOccluded(Dialogs.HasOpenDialog || _customization?.IsEditor == true);
        _customization?.SetFigureOccluded(Dialogs.HasOpenDialog);
    }

    private void UpdateCustomizationLayout()
    {
        RefreshFigureOcclusion();
        if (_customization is null)
        {
            _presentationWorkspace.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetColumn(_customizationLayer, 1);
            Grid.SetColumnSpan(_customizationLayer, 1);
            return;
        }

        var inspectorWidth = 320d;
        if (ActualWidth > 0)
        {
            inspectorWidth = Math.Min(inspectorWidth, Math.Max(280, ActualWidth * 0.42));
        }

        Grid.SetColumn(_customizationLayer, 0);
        Grid.SetColumnSpan(_customizationLayer, 2);
        _customization.SetInspectorWidth(inspectorWidth);
        _presentationWorkspace.ColumnDefinitions[1].Width = new GridLength(_customization.IsCardOnly ? 0 : inspectorWidth);
    }

    public Neuterradise.App.Gallery.GalleryCardViewModel? SampleCard(Guid? profileId) =>
        _topLevel.Values.OfType<GallerySurface>().FirstOrDefault()?.SampleCard(profileId);

    public CardData? SampleHomeCard() =>
        _topLevel.Values.OfType<HomeSurface>().FirstOrDefault()?.SampleCard();

    public Task ImportAsync(
        IEnumerable<string> paths,
        Guid? destinationProfileId,
        IntakeOrigin origin = IntakeOrigin.Picker)
    {
        var import = (ImportSurface)ResolveSurface(new ImportRoute());
        _services.Navigation.ResetToTopLevel(new ImportRoute());
        return import.IntakeAsync(paths, destinationProfileId, origin);
    }

    public void ShowClosingOverlay()
    {
        // Stop the active surface before the durability drain. Closing may legitimately wait for a
        // safe mutation boundary, but animation/video/native presentation must become cold
        // immediately instead of keeping the laptop hot behind the closing overlay.
        _current?.Suspend();
        _customization?.SetFigureOccluded(true);
        HoverVideoCoordinator.Shared.StopAll();
        SkVideoView.StopAllPlayback();

        var panel = UI.Surface(
            UI.V(
                8,
                UI.Text(UI.T("Shutdown.Title", "Closing naut…"), "section-title"),
                UI.Text(
                    UI.T(
                        "Shutdown.Detail",
                        "Finishing safe Vault operations. Unfinished background work will resume next time."),
                    "body-muted",
                    maxLines: 3)),
            Material.Deep,
            ThemeRuntime.Current.Tokens.Number("radiusHero", 20),
            24);
        panel.MaxWidth = 520;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Center;

        var scrim = new Grid
        {
            Background = ThemeRuntime.Current.Brush("scrimStrong"),
            IsHitTestVisible = true,
        };
        scrim.Children.Add(panel);

        _customizationLayer.Visibility = Visibility.Collapsed;
        _overlayLayer.Children.Clear();
        _overlayLayer.Children.Add(scrim);
        _overlayLayer.Visibility = Visibility.Visible;
    }

    public void ShowToast(string message, string tone)
    {
        var icon = tone switch
        {
            "success" => "icon.status.success",
            "warning" => "icon.status.warning",
            "error" => "icon.status.error",
            _ => "icon.status.info",
        };
        var statusIcon = new IconView(icon, 18, tone switch { "success" => "success", "warning" => "warning", "error" => "danger", _ => "info" });
        var toast = UI.Surface(UI.H(10, statusIcon, UI.Text(message, "body", maxLines: 3)), Material.Deep, 10, 12);
        toast.MaxWidth = 420;
        _toasts.Children.Add(toast);
        UI.AnimateStatusIcon(statusIcon, tone);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(tone == "error" ? 8 : 4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _toasts.Children.Remove(toast);
        };
        timer.Start();
    }

    private void OnPresentationChanged(PresentationChangedEventArgs args)
    {
        if (args.PacksChanged || args.Slots.Any(s => s.StartsWith("appearance.", StringComparison.Ordinal)))
        {
            PresentationBinder.ApplyGlobal(_services.Presentation);
        }

        _services.RaisePresentationChanged(args);
        foreach (var surface in _topLevel.Values.Concat(_contextual.Select(c => c.Surface)))
        {
            surface.OnPresentationChanged(args);
        }
    }

    private void RegisterAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            if (key == VirtualKey.None
                || !Enum.IsDefined(key)
                || modifiers == VirtualKeyModifiers.None)
            {
                return;
            }

            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                action();
                args.Handled = true;
            };
            KeyboardAccelerators.Add(accelerator);
        }

        Add(VirtualKey.Left, VirtualKeyModifiers.Menu, () =>
        {
            if (_customization is not null)
            {
                _customization.RequestClose();
                return;
            }

            _services.Navigation.GoBack();
        });
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Keyboard);
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        if (Dialogs.TryDismissTop())
        {
            e.Handled = true;
        }
        else if (_services.Overlay.HasOverlay)
        {
            _services.Overlay.RequestEscapeClose();
            e.Handled = true;
        }
        else if (_customization is not null)
        {
            _customization.RequestClose();
            e.Handled = true;
        }
    }
}

internal sealed class RouteFailureSurface : Surface
{
    private readonly Grid _view;

    public RouteFailureSurface(AppServices services, AppRoute route, Action retry) : base(services)
    {
        _view = UI.Grid("*", "*");
        _view.Children.Add(UI.V(12,
            UI.Text(UI.T("Route.Error.Title", "This page couldn't be opened."), "section-title"),
            UI.Text(UI.T("Route.Error.Description", "The page failed to initialize. Retry, or return to the previous page."), "body", "textSecondary", 3),
            UI.H(8,
                UI.Button(UI.T("Common.Retry", "Retry"), retry, ButtonKind.Primary),
                UI.Button(UI.T("Common.Back", "Back"), () => services.Navigation.GoBack(), ButtonKind.Secondary)))
            .Align(HorizontalAlignment.Center, VerticalAlignment.Center));
    }

    public override FrameworkElement View => _view;
}
