using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Neuterradise.App.Gallery;
using Neuterradise.App.Home;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Resources;

namespace Neuterradise.App.Ui;

/// <summary>
/// Home: cinematic lobby with a dominant spotlight, integrated library pulse, meaningful content rails,
/// and purposeful empty states. Uses the existing BackdropView, presentation system, and theme tokens
/// throughout — no private design system.
/// </summary>
public sealed class HomeSurface : Surface, ITopBarCustomizableSurface
{
    private readonly HomeViewModel _vm;
    private readonly Grid _root = new();
    private readonly BackdropView _backdrop = new();
    private readonly Grid _hero = new();
    private readonly StackPanel _rails = new() { Spacing = 0 };
    private readonly StackPanel _page = UI.V(0);
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _notice = UI.WrappedText(string.Empty, "body", "danger");
    private readonly SkImageView _heroImage = new() { PlaceholderToken = "surface1" };
    private readonly CompositeTransform _heroMotion = new();
    private readonly SkVideoView _heroVideo = new() { Visibility = Visibility.Collapsed };
    private readonly Grid _identityHost = new();
    private readonly Grid _heroFrame = new();
    private readonly Grid _heroFooter = new();
    private Border? _heroArtworkHost;
    private Border? _heroFooterPanel;
    private UIElement? _heroScrim;
    private TextBlock? _heroMeta;
    private SpotlightGeometry? _heroGeometry;
    private bool? _footerNavBelow;
    private UIElement? _pagination;
    private readonly TextBlock _counter = UI.Text(string.Empty, "numeric", "onHeroSecondary");
    private readonly ScaleTransform _progressScale = new() { ScaleX = 0, ScaleY = 1 };
    private readonly Border _progressFill = new() { Height = 2, HorizontalAlignment = HorizontalAlignment.Left, Width = 120 };
    private readonly DispatcherTimer _rotation = new();
    private readonly Dictionary<Guid, ProfileAppearanceOverrides> _appearance = [];
    private readonly List<WeakReference<HomeProfileTile>> _profileTiles = [];
    private int _railAppearanceGeneration;
    private CoverFrameView? _heroCover;
    private TextBlock? _heroName;
    private TextBlock? _heroOverview;
    private Storyboard? _kenBurns;
    private Storyboard? _progress;
    private HomeLayoutPlan _layout = null!;
    private SpotlightPlan _spotlight = null!;
    private bool _pointerOverHero;
    private bool _railRenderQueued;
    private bool _retired;
    private int _mediaGeneration;
    private HomeSpotlightViewModel? _displayedSpotlight;

    public CardData? SampleCard()
    {
        var spotlight = _vm.CurrentSpotlight ?? _vm.SpotlightCandidates.FirstOrDefault();
        if (spotlight is null)
        {
            return null;
        }

        var overrides = _appearance.GetValueOrDefault(spotlight.ProfileId) ?? ProfileAppearanceOverrides.Default;
        var state = new ProfilePresentationState(spotlight.ProfileId, 0, null, overrides);
        var media = Services.Presentation.ResolveMedia(state);
        var frame = ResolvePresentation(
            PresentationSlots.ProfileFrame,
            PresentationContext.ForProfile(state, "home")).PlanAs<FramePlan>();
        return new CardData(
            spotlight.ProfileId,
            spotlight.DisplayName,
            spotlight.CategoryName,
            spotlight.Tags,
            spotlight.Rating,
            spotlight.IsFavorite,
            spotlight.MediaCount,
            0,
            spotlight.OverviewExcerpt,
            ImageRef.FromPath(spotlight.CoverImagePath, 480),
            ImageRef.FromPath(spotlight.BannerImagePath, 900),
            CardDataFactory.AppearanceFor(overrides, frame, ReducedMotionAuthority.IsReduced),
            media.Cover,
            media.Banner,
            frame) { AssignedTags = spotlight.AssignedTags };
    }

    public HomeSurface(AppServices services, HomeViewModel vm) : base(services)
    {
        _vm = vm;
        _root.Background = ThemeRuntime.Current.Brush("canvas");
        PresentationEffects.SetContextScope(_root, () => Context);
        _root.Children.Add(_backdrop);
        _root.Children.Add(PresentationEffects.View(PresentationSlots.HomeEffect, () => Context));
        _heroImage.RenderTransform = _heroMotion;
        _heroImage.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        _heroVideo.FrameReady += OnHeroVideoFrameReady;

        _notice.Visibility = Visibility.Collapsed;
        _scroll = UI.Scroll(_page);
        _scroll.ViewChanged += (_, _) => HoverVideoCoordinator.Shared.NotifyScroll();
        _root.Children.Add(_scroll);

        _root.PointerMoved += (_, args) =>
        {
            if (_root.ActualWidth > 0)
            {
                var point = args.GetCurrentPoint(_root).Position;
                _backdrop.SetPointer(point.X / _root.ActualWidth, point.Y / Math.Max(1, _root.ActualHeight));
            }
        };
        _root.SizeChanged += (_, _) =>
        {
            LayoutHero();
            QueueRebuildRails();
        };
        _hero.SizeChanged += (_, _) => LayoutHero();
        _hero.PointerEntered += (_, _) => _pointerOverHero = true;
        _hero.PointerExited += (_, _) => _pointerOverHero = false;
        _rotation.Tick += (_, _) =>
        {
            if (IsActive && !_pointerOverHero && !ReducedMotionAuthority.IsReduced && _vm.HasMultipleSpotlightCandidates)
            {
                _vm.NextSpotlightCommand.Execute(null);
            }
        };

        ResolvePlans();
        Bag.Add(Observe.Props(_vm, OnSpotlightChanged, nameof(HomeViewModel.CurrentSpotlight), nameof(HomeViewModel.SpotlightIndex), nameof(HomeViewModel.IsLibraryEmpty)));
        Bag.Add(Observe.Props(
            _vm,
            UpdateNotice,
            nameof(HomeViewModel.Status),
            nameof(HomeViewModel.ErrorMessage),
            nameof(HomeViewModel.RefreshNotice)));
        Bag.Add(Observe.Collection(_vm.SpotlightCandidates, () => { OnSpotlightChanged(); QueueRebuildRails(); }));
        Bag.Add(Observe.Collection(_vm.RecentlyActive, QueueRebuildRails));
        Bag.Add(Observe.Collection(_vm.NeedsAttention, QueueRebuildRails));
        Bag.Add(Observe.Collection(_vm.RecentActivity, QueueRebuildRails));
        Bag.Add(Observe.Collection(_vm.Discovery, QueueRebuildRails));
        Bag.Add(Observe.Collection(_vm.ActiveImports, QueueRebuildRails));
        Bag.Add(Observe.Props(_vm, QueueRebuildRails, nameof(HomeViewModel.VaultPulse)));
        ResourceGovernor.Shared.Changed += OnGovernorChanged;
        Bag.Add(() => ResourceGovernor.Shared.Changed -= OnGovernorChanged);
        UpdateNotice();
    }

    public override FrameworkElement View => _root;

    public override Neuterradise.App.Shell.ScreenStateViewModel Model => _vm;

    public string TopBarCustomizeLabel => UI.T("Home.Customize", "Customize Home");

    public bool CanTopBarCustomize => true;

    public void OpenTopBarCustomization() =>
        Services.OpenCustomization(CustomizationCategories.Home, Context);

    private PresentationContext Context => new("home");

    private void UpdateNotice()
    {
        _notice.Text = _vm.HasError
            ? _vm.ErrorMessage ?? string.Empty
            : _vm.RefreshNotice ?? string.Empty;
        _notice.Foreground = ThemeRuntime.Current.Brush(_vm.HasError ? "textDanger" : "textSecondary");
        _notice.Visibility = string.IsNullOrWhiteSpace(_notice.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    protected override void RefreshPreviewPresentation(string? slot = null)
    {
        if (slot == PresentationSlots.HomeEffect) return;
        if (slot is PresentationSlots.ProfileFrame or PresentationSlots.ProfileCover or PresentationSlots.ProfileBanner)
        {
            RefreshRailFrames();
            OnSpotlightChanged();
            return;
        }
        if (slot == PresentationSlots.HomeBackdrop)
        {
            _backdrop.Plan = ResolvePresentation(slot, Context).PlanAs<BackdropPlan>();
            return;
        }
        if (slot == PresentationSlots.HomeSpotlight)
        {
            _spotlight = ResolvePresentation(slot, Context).PlanAs<SpotlightPlan>();
            _rotation.Interval = TimeSpan.FromSeconds(_spotlight.RotationSeconds);
            BuildHero();
            OnSpotlightChanged();
            return;
        }
        if (slot is not null && slot != PresentationSlots.HomeLayout) return;
        var vertical = _scroll.VerticalOffset;
        ResolvePlans();
        OnSpotlightChanged();
        _scroll.DispatcherQueue.TryEnqueue(() => _scroll.ChangeView(null, vertical, null, disableAnimation: true));
    }

    private void ResolvePlans()
    {
        _layout = ResolvePresentation(PresentationSlots.HomeLayout, Context).PlanAs<HomeLayoutPlan>();
        _spotlight = ResolvePresentation(PresentationSlots.HomeSpotlight, Context).PlanAs<SpotlightPlan>();
        _backdrop.Plan = ResolvePresentation(PresentationSlots.HomeBackdrop, Context).PlanAs<BackdropPlan>();
        _rotation.Interval = TimeSpan.FromSeconds(_spotlight.RotationSeconds);
        BuildHero();
        RebuildRails();
    }

    protected override void OnActivated(AppRoute route)
    {
        _backdrop.IsSurfaceActive = true;
        _rotation.Start();
        StartSpotlightMotion();
        SyncHeroPlayback();
        Services.RunUserAction(
            _vm.RefreshAsync(),
            "HomeSurface.ActivateAsync",
            UI.T("Home.RefreshFailed", "Home could not be refreshed."));
    }

    protected override void OnSuspended()
    {
        _backdrop.IsSurfaceActive = false;
        _rotation.Stop();
        _kenBurns?.Stop();
        _progress?.Stop();
        StopHeroVideo();
    }

    public override void OnPresentationChanged(PresentationChangedEventArgs args)
    {
        if (args.PacksChanged || args.Slots.Contains(PresentationSlots.ProfileFrame)
            || args.Slots.Contains(PresentationSlots.ProfileCover) || args.Slots.Contains(PresentationSlots.ProfileBanner))
        {
            _appearance.Clear();
            RefreshRailFrames();
            if (!args.PacksChanged) OnSpotlightChanged();
        }
        if (args.Slots.Contains(PresentationSlots.ProfileCover) || args.Slots.Contains(PresentationSlots.ProfileBanner))
        {
            _appearance.Clear();
            Services.RunUserAction(_vm.RefreshAsync(), "HomeSurface.RefreshProfilePresentation",
                UI.T("Home.RefreshFailed", "Home could not be refreshed."));
        }
        if (!args.PacksChanged && args.Slots.Count == 1 && args.Slots.First() is { } slot
            && (slot.StartsWith("home.", StringComparison.Ordinal) || slot == PresentationSlots.HomeEffect))
        {
            RefreshPreviewPresentation(slot);
            return;
        }
        if (args.PacksChanged || args.Slots.Any(s => s.StartsWith("home.", StringComparison.Ordinal) || s.StartsWith("appearance.", StringComparison.Ordinal) || s.StartsWith("surface.spotlight", StringComparison.Ordinal)))
        {
            _appearance.Clear();
            ResolvePlans();
            OnSpotlightChanged();
        }
    }

    // ─────────────────────────────────────────── hero

    private void BuildHero()
    {
        _hero.Children.Clear();
        _heroFrame.Children.Clear();
        _heroFrame.RowDefinitions.Clear();
        _heroFrame.ColumnDefinitions.Clear();
        _heroFooter.Children.Clear();
        _heroFooter.RowDefinitions.Clear();
        _heroFooter.ColumnDefinitions.Clear();
        _heroGeometry = null;
        _footerNavBelow = null;
        SurfaceComposer.Detach(_heroImage);
        SurfaceComposer.Detach(_heroVideo);
        SurfaceComposer.Detach(_identityHost);

        var art = new Grid();
        _heroImage.HorizontalAlignment = HorizontalAlignment.Stretch;
        _heroImage.VerticalAlignment = VerticalAlignment.Stretch;
        _heroVideo.HorizontalAlignment = HorizontalAlignment.Stretch;
        _heroVideo.VerticalAlignment = VerticalAlignment.Stretch;
        art.SizeChanged += (_, args) =>
        {
            if (args.NewSize.Width <= 0 || args.NewSize.Height <= 0) return;
            art.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(
                    0, 0, args.NewSize.Width, args.NewSize.Height),
            };
            _heroImage.Width = args.NewSize.Width;
            _heroImage.Height = args.NewSize.Height;
            _heroVideo.Width = args.NewSize.Width;
            _heroVideo.Height = args.NewSize.Height;
        };
        art.Children.Add(_heroImage);
        art.Children.Add(_heroVideo);
        art.Children.Add(PresentationEffects.View(PresentationSlots.BannerEffect, () =>
            _vm.CurrentSpotlight is { } current ? PresentationContext.ForProfile(new ProfilePresentationState(current.ProfileId, 0, null,
                _appearance.GetValueOrDefault(current.ProfileId) ?? ProfileAppearanceOverrides.Default), "home") : Context));

        var imageLayer = new Grid();
        imageLayer.Children.Add(art);
        _heroScrim = Scrim();
        imageLayer.Children.Add(_heroScrim);
        _heroArtworkHost = new Border
        {
            Child = imageLayer,
            CornerRadius = new CornerRadius(_layout.HeroPlacement == "inset"
                ? ThemeRuntime.Current.Tokens.Number("radiusHero", 20) : 0),
        };
        _heroFrame.Children.Add(_heroArtworkHost);

        _pagination = Pagination();
        _heroFooter.Children.Add(_identityHost);
        _heroFooter.Children.Add(_pagination);
        _heroFooterPanel = new Border
        {
            Child = _heroFooter,
            Padding = new Thickness(20, 12, 20, 12),
            Background = ThemeRuntime.Current.Brush("transparent"),
            BorderThickness = new Thickness(0),
        };
        _heroFrame.Children.Add(_heroFooterPanel);
        _hero.Children.Add(_heroFrame);

        LayoutHero();
        OnSpotlightChanged();
    }

    private UIElement Scrim()
    {
        var theme = ThemeRuntime.Current;
        var strength = Math.Clamp(_spotlight.ScrimStrength, 0, 1);
        var fade = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.5, 1),
            EndPoint = new Windows.Foundation.Point(0.5, 0),
        };
        fade.GradientStops.Add(new GradientStop
        {
            Color = WithAlpha(theme.Color("heroScrimStrong"), strength * 0.97),
            Offset = 0,
        });
        fade.GradientStops.Add(new GradientStop
        {
            Color = WithAlpha(theme.Color("heroScrimStrong"), strength * 0.88),
            Offset = 0.34,
        });
        fade.GradientStops.Add(new GradientStop
        {
            Color = WithAlpha(theme.Color("heroScrimSoft"), strength * 0.12),
            Offset = 0.75,
        });
        fade.GradientStops.Add(new GradientStop
        {
            Color = WithAlpha(theme.Color("heroScrimSoft"), 0),
            Offset = 1,
        });
        return new Border
        {
            Background = fade,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
        };
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, double factor) =>
        Windows.UI.Color.FromArgb((byte)Math.Clamp(color.A * factor, 0, 255), color.R, color.G, color.B);

    private UIElement Pagination()
    {
        var theme = ThemeRuntime.Current;
        _progressFill.Background = theme.Brush("accent");
        _progressFill.RenderTransform = _progressScale;
        _progressFill.RenderTransformOrigin = new Windows.Foundation.Point(0, 0.5);
        var track = new Grid { Height = 2, Width = 120, Background = theme.Brush("glassBorder") };
        track.Children.Add(_progressFill);
        var previous = SpotlightArrowButton(
            UI.T("Home.Previous", "Previous"),
            () => _vm.PreviousSpotlightCommand.Execute(null),
            mirrored: false);
        var next = SpotlightArrowButton(
            UI.T("Home.Next", "Next"),
            () => _vm.NextSpotlightCommand.Execute(null),
            mirrored: true);
        _counter.VerticalAlignment = VerticalAlignment.Center;
        _counter.Foreground = theme.Brush("onHeroPrimary");
        var panel = UI.H(10,
            previous,
            _counter,
            _spotlight.Pagination == "counter-rail" ? track.Align(vertical: VerticalAlignment.Center) : null,
            next);
        panel.HorizontalAlignment = HorizontalAlignment.Right;
        panel.VerticalAlignment = VerticalAlignment.Bottom;
        panel.Margin = new Thickness(0);
        var control = new Border
        {
            Child = panel,
            Background = theme.Brush("transparent"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0),
        };
        panel.Margin = new Thickness(0);
        return _spotlight.Pagination == "none" ? new Grid() : control;
    }

    private static Button SpotlightArrowButton(string accessibleName, Action onClick, bool mirrored)
    {
        var button = UI.IconButton("icon.action.back", accessibleName, onClick, 30);
        ToolTipService.SetToolTip(button, null);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, accessibleName);
        if (button.Content is IconView icon)
        {
            icon.ColorToken = "onHeroPrimary";
            if (mirrored)
            {
                icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                icon.RenderTransform = new ScaleTransform { ScaleX = -1 };
            }
        }

        return button;
    }

    private void LayoutHero()
    {
        if (_heroArtworkHost is null || _heroFooterPanel is null) return;
        var theme = ThemeRuntime.Current;
        var height = _root.ActualHeight > 0 ? _root.ActualHeight : 720;
        var width = _hero.ActualWidth > 0 ? _hero.ActualWidth
            : _root.ActualWidth > 0 ? _root.ActualWidth : 1280;

        if (_vm.CurrentSpotlight is null)
        {
            _hero.Height = double.NaN;
            _hero.MinHeight = 0;
            _heroArtworkHost.Visibility = Visibility.Collapsed;
            _heroFooterPanel.Background = theme.Brush("transparent");
            _heroFooterPanel.VerticalAlignment = VerticalAlignment.Top;
            _heroFrame.Background = theme.Brush("transparent");
            if (_identityHost.Children.FirstOrDefault() is FrameworkElement welcome)
            {
                welcome.MaxWidth = Math.Min(760, Math.Max(1, width - 40));
                welcome.Margin = new Thickness(20);
            }
            return;
        }

        // Home owns the featured envelope; Spotlight owns the composition inside it.
        var geometry = _spotlight.GeometryFor(width, _layout.HeroPlacement);
        var split = geometry is SpotlightGeometry.ArtworkLeft or SpotlightGeometry.IdentityLeft;
        var stacked = geometry is SpotlightGeometry.ArtworkAbove or SpotlightGeometry.IdentityAbove;
        var separated = split || stacked;
        var centered = !separated && _spotlight.Composition == "storefront";
        var lowerLayer = !separated && _spotlight.Composition == "editorial";
        var compact = width < 760 || height < 540;
        _heroArtworkHost.Visibility = Visibility.Visible;
        _heroFooterPanel.Background = separated ? theme.Brush("surface2")
            : lowerLayer ? new SolidColorBrush(WithAlpha(theme.Color("heroScrimStrong"), 0.46))
            : theme.Brush("transparent");
        _heroFooterPanel.BorderBrush = theme.Brush(separated ? "borderSubtle" : "transparent");
        _heroFooterPanel.BorderThickness = new Thickness(separated ? 1 : 0);
        _heroFrame.Background = theme.Brush("transparent");
        if (_heroScrim is not null)
            _heroScrim.Visibility = separated ? Visibility.Collapsed : Visibility.Visible;

        if (_heroGeometry != geometry)
        {
            _heroGeometry = geometry;
            _heroFrame.RowDefinitions.Clear();
            _heroFrame.ColumnDefinitions.Clear();
            if (split)
            {
                _heroFrame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                var identityFirst = geometry == SpotlightGeometry.IdentityLeft;
                var first = identityFirst ? _spotlight.IdentityShare : 1 - _spotlight.IdentityShare;
                _heroFrame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(first, GridUnitType.Star) });
                _heroFrame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - first, GridUnitType.Star) });
                Grid.SetRow(_heroArtworkHost, 0); Grid.SetRow(_heroFooterPanel, 0);
                Grid.SetColumn(_heroArtworkHost, identityFirst ? 1 : 0);
                Grid.SetColumn(_heroFooterPanel, identityFirst ? 0 : 1);
            }
            else if (stacked)
            {
                var identityFirst = geometry == SpotlightGeometry.IdentityAbove;
                _heroFrame.RowDefinitions.Add(new RowDefinition { Height = identityFirst ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
                _heroFrame.RowDefinitions.Add(new RowDefinition { Height = identityFirst ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
                Grid.SetRow(_heroArtworkHost, identityFirst ? 1 : 0);
                Grid.SetRow(_heroFooterPanel, identityFirst ? 0 : 1);
                Grid.SetColumn(_heroArtworkHost, 0); Grid.SetColumn(_heroFooterPanel, 0);
            }
            else
            {
                _heroFrame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(_heroArtworkHost, 0); Grid.SetRow(_heroFooterPanel, 0);
                Grid.SetColumn(_heroArtworkHost, 0); Grid.SetColumn(_heroFooterPanel, 0);
            }
        }

        if (split && _heroFrame.ColumnDefinitions.Count == 2)
        {
            var first = geometry == SpotlightGeometry.IdentityLeft
                ? _spotlight.IdentityShare : 1 - _spotlight.IdentityShare;
            _heroFrame.ColumnDefinitions[0].Width = new GridLength(first, GridUnitType.Star);
            _heroFrame.ColumnDefinitions[1].Width = new GridLength(1 - first, GridUnitType.Star);
        }

        var navBelow = separated || width < 840 || centered;
        if (_footerNavBelow != navBelow)
        {
            _footerNavBelow = navBelow;
            _heroFooter.RowDefinitions.Clear();
            _heroFooter.ColumnDefinitions.Clear();
            if (navBelow)
            {
                _heroFooter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _heroFooter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(_identityHost, 0);
                Grid.SetRow(_pagination, 1);
                Grid.SetColumn(_identityHost, 0);
                Grid.SetColumn(_pagination, 0);
            }
            else
            {
                _heroFooter.ColumnDefinitions.Add(new ColumnDefinition
                    { Width = new GridLength(1, GridUnitType.Star) });
                _heroFooter.ColumnDefinitions.Add(new ColumnDefinition
                    { Width = GridLength.Auto });
                Grid.SetRow(_identityHost, 0);
                Grid.SetRow(_pagination, 0);
                Grid.SetColumn(_identityHost, 0);
                Grid.SetColumn(_pagination, 1);
            }
        }

        var inset = compact ? 14 : _spotlight.Composition == "editorial" ? 32 : 24;
        _heroFooterPanel.Padding = new Thickness(inset, lowerLayer ? (compact ? 20 : 28) : 12, inset, compact ? 14 : 20);
        _heroFooterPanel.VerticalAlignment = separated ? VerticalAlignment.Stretch
            : centered ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        _heroFooter.VerticalAlignment = separated || centered
            ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        _heroFooterPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (_heroName is not null)
        {
            _heroName.FontSize = compact ? 20 : Math.Clamp(
                TokenAuthority.Foundations.TypeStyles["hero"].Size * (_spotlight.Composition == "editorial" ? 1.12 : 1),
                _spotlight.Composition == "editorial" ? 30 : 24,
                _spotlight.Composition == "editorial" ? 42 : 36);
            _heroName.Foreground = theme.Brush(separated ? "textPrimary" : "onHeroPrimary");
        }
        if (_heroMeta is not null)
            _heroMeta.Foreground = theme.Brush(separated ? "textSecondary" : "onHeroSecondary");
        if (_heroOverview is not null)
        {
            _heroOverview.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            _heroOverview.Foreground = theme.Brush(separated ? "textSecondary" : "onHeroSecondary");
        }
        _counter.Foreground = theme.Brush(separated ? "textSecondary" : "onHeroPrimary");
        if (_pagination is Border navigation && navigation.Child is Panel panel)
        {
            foreach (var button in panel.Children.OfType<Button>())
                if (button.Content is IconView icon)
                    icon.ColorToken = separated ? "textPrimary" : "onHeroPrimary";
        }
        if (_heroCover is not null)
            _heroCover.Width = _heroCover.Height = centered ? (compact ? 60 : 88) : (compact ? 48 : 64);
        if (_identityHost.Children.FirstOrDefault() is FrameworkElement identity)
        {
            identity.MaxWidth = split ? Math.Max(1, width * _spotlight.IdentityShare - 2 * inset)
                : centered ? Math.Min(680, Math.Max(1, width - 2 * inset))
                : navBelow ? Math.Max(1, width - 2 * inset)
                : Math.Max(1, width - 310);
            identity.HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            identity.VerticalAlignment = VerticalAlignment.Center;
            identity.Margin = new Thickness(0, lowerLayer ? 10 : 0, navBelow || centered ? 0 : 14, 0);
        }
        if (_pagination is FrameworkElement pagination)
        {
            pagination.HorizontalAlignment = centered ? HorizontalAlignment.Center
                : navBelow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            pagination.VerticalAlignment = VerticalAlignment.Center;
            pagination.Margin = new Thickness(0, navBelow ? 10 : 0, 0, 0);
        }
        _heroArtworkHost.Margin = _layout.HeroPlacement == "inset" && !separated
            ? new Thickness(12, 8, 12, 0) : new Thickness(0);
        _heroFooterPanel.Margin = _heroArtworkHost.Margin;
        _heroFooterPanel.CornerRadius = separated ? new CornerRadius(0) : _heroArtworkHost.CornerRadius;
        if (_heroScrim is Border mask) mask.CornerRadius = _heroArtworkHost.CornerRadius;

        var minimum = Math.Min(_layout.HeroMinHeight, Math.Max(180, height * 0.44));
        var maximum = Math.Max(minimum, Math.Min(_layout.HeroMaxHeight, height * 0.8));
        var authoredHeight = Math.Clamp(height * _layout.HeroFraction, minimum, maximum);
        var widthBound = width * (split ? 0.54 : 0.67);
        var targetHeight = Math.Clamp(Math.Min(authoredHeight, widthBound),
            split ? 240 : 270, Math.Max(split ? 240 : 270, maximum));
        if (stacked) targetHeight = Math.Max(compact ? 390 : 450, targetHeight);
        if (double.IsNaN(_hero.Height) || Math.Abs(_hero.Height - targetHeight) > 1)
            _hero.Height = targetHeight;
    }

    private void OnSpotlightChanged()
    {
        var generation = ++_mediaGeneration;
        var spotlight = _vm.CurrentSpotlight;
        var previous = _displayedSpotlight;
        var changedSource = previous?.ProfileId != spotlight?.ProfileId
            || !string.Equals(previous?.HeroImagePath, spotlight?.HeroImagePath,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous?.BannerVideoPath, spotlight?.BannerVideoPath,
                StringComparison.OrdinalIgnoreCase);
        _displayedSpotlight = spotlight;
        if (changedSource)
        {
            // Retire the previous clip before the new poster/identity enters the tree.
            // Appearance reads complete asynchronously, so teardown cannot wait for them.
            StopHeroVideo();
            _kenBurns?.Stop();
            _heroMotion.ScaleX = 1;
            _heroMotion.ScaleY = 1;
            _heroMotion.TranslateX = 0;
            _heroMotion.TranslateY = 0;
        }
        if (_pagination is not null) _pagination.Visibility = _vm.HasMultipleSpotlightCandidates ? Visibility.Visible : Visibility.Collapsed;
        _counter.Text = _vm.SpotlightCandidates.Count == 0 ? string.Empty : $"{_vm.SpotlightIndex + 1:00} / {_vm.SpotlightCandidates.Count:00}";
        _heroCover = null;
        _identityHost.Children.Clear();

        if (spotlight is null)
        {
            _heroImage.Source = null;
            _backdrop.AmbientImagePath = null;
            _identityHost.Children.Add(EmptyWelcome());
            StopHeroVideo();
            LayoutHero();
            return;
        }

        if (changedSource || _appearance.TryGetValue(spotlight.ProfileId, out _))
        {
            var initialAppearance = _appearance.GetValueOrDefault(spotlight.ProfileId)
                ?? ProfileAppearanceOverrides.Default;
            var initialMedia = Services.Presentation.ResolveMedia(
                new ProfilePresentationState(spotlight.ProfileId, 0, null, initialAppearance));
            _heroImage.Transform = spotlight.BannerImagePath is not null
                ? initialMedia.Banner : initialMedia.Cover;
            _heroVideo.SetPresentation(initialMedia.Banner);
        }
        _heroImage.Source = ImageRef.FromPath(spotlight.HeroImagePath, 1600);
        _backdrop.AmbientImagePath = spotlight.HeroImagePath ?? spotlight.CoverImagePath;
        var identity = Identity(spotlight);
        if (_spotlight.Composition == "storefront") identity.SizeChanged += (_, _) => LayoutHero();
        _identityHost.Children.Add(identity);
        LayoutHero();
        Services.RunUserAction(
            ApplyMediaStateAsync(spotlight, generation),
            "HomeSurface.ApplyMediaStateAsync",
            UI.T("Home.MediaFailed", "Spotlight media could not be loaded."));
        FadeIn(_identityHost);
        FadeIn(_heroImage);
        StartSpotlightMotion();
    }

    private async Task ApplyMediaStateAsync(HomeSpotlightViewModel spotlight, int generation)
    {
        if (!_appearance.TryGetValue(spotlight.ProfileId, out var overrides))
        {
            var summary = await Services.Catalog.GalleryReads.GetGalleryProfileSummaryAsync(spotlight.ProfileId).ConfigureAwait(true);
            if (_retired || generation != _mediaGeneration) return;
            overrides = summary?.EffectiveAppearance ?? ProfileAppearanceOverrides.Default;
            _appearance[spotlight.ProfileId] = overrides;
        }

        if (_retired || generation != _mediaGeneration || _vm.CurrentSpotlight?.ProfileId != spotlight.ProfileId)
        {
            return;
        }

        var state = new ProfilePresentationState(spotlight.ProfileId, 0, null, overrides);
        var media = Services.Presentation.ResolveMedia(state);
        if (_heroCover is not null)
        {
            var frame = ResolvePresentation(
                PresentationSlots.ProfileFrame,
                PresentationContext.ForProfile(state, "home")).PlanAs<FramePlan>();
            _heroCover.Transform = media.Cover;
            _heroCover.Plan = frame;
            _heroCover.Appearance = CardDataFactory.AppearanceFor(
                overrides,
                frame,
                ReducedMotionAuthority.IsReduced);
        }

        // A missing Banner poster falls back to the Cover and therefore uses Cover framing.
        // The actual Banner video always uses Banner framing, independent of that fallback.
        _heroImage.Transform = spotlight.BannerImagePath is not null ? media.Banner : media.Cover;
        _heroVideo.SetPresentation(media.Banner);
        PresentationEffects.RefreshTree(_heroFrame, PresentationSlots.BannerEffect);
        PlayHeroVideo(spotlight, media.Playback);
    }

    private void OnGovernorChanged(object? sender, EventArgs args) =>
        UiDispatch.Run(SyncHeroPlayback);

    private void SyncHeroPlayback()
    {
        if (_retired || !IsActive) return;
        if (_vm.CurrentSpotlight is not { } spotlight)
        {
            StopHeroVideo();
            return;
        }

        var overrides = _appearance.GetValueOrDefault(spotlight.ProfileId)
            ?? ProfileAppearanceOverrides.Default;
        var state = new ProfilePresentationState(spotlight.ProfileId, 0, null, overrides);
        PlayHeroVideo(spotlight, Services.Presentation.ResolveMedia(state).Playback);
    }

    private void PlayHeroVideo(HomeSpotlightViewModel spotlight, PlaybackPresentationState playback)
    {
        if (_spotlight.Composition == "snapshot" || !IsActive
            || !spotlight.HasBannerVideo || !ResourceGovernor.Shared.IsPrimaryVideoAllowed
            || ReducedMotionAuthority.IsReduced)
        {
            StopHeroVideo();
            return;
        }

        if (string.Equals(_heroVideo.SourcePath, spotlight.BannerVideoPath,
            StringComparison.OrdinalIgnoreCase) && _heroVideo.Visibility == Visibility.Visible)
        {
            return;
        }

        // Keep the poster visible until the new source emits its first decoded frame.
        _heroVideo.Visibility = Visibility.Collapsed;
        _heroVideo.Play(spotlight.BannerVideoPath, loop: playback.LoopMode == "loop");
    }

    private void OnHeroVideoFrameReady()
    {
        var spotlight = _vm.CurrentSpotlight;
        if (!IsActive
            || spotlight is null
            || !spotlight.HasBannerVideo
            || !string.Equals(_heroVideo.SourcePath, spotlight.BannerVideoPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _heroVideo.Visibility = Visibility.Visible;
    }

    private void StopHeroVideo()
    {
        _heroVideo.Stop();
        _heroVideo.Visibility = Visibility.Collapsed;
    }

    private FrameworkElement Identity(HomeSpotlightViewModel spotlight)
    {
        Services.ProfileSnapshots.Warm(spotlight.ProfileId);
        var name = UI.Text(spotlight.DisplayName, "section-title", "textPrimary", 2);
        var meta = UI.Text(string.Join(" \u00b7 ",
            new[] { spotlight.CategoryName, spotlight.MediaCountText }
                .Where(value => !string.IsNullOrWhiteSpace(value))),
            "metadata", "textSecondary", 1);
        var overview = _spotlight.ShowOverview && !string.IsNullOrWhiteSpace(spotlight.OverviewExcerpt)
            ? UI.Text(spotlight.OverviewExcerpt, "body", "textSecondary", 2)
            : null;
        _heroName = name;
        _heroMeta = meta;
        _heroOverview = overview;
        name.Tip(spotlight.DisplayName);

        var action = UI.Button(
            UI.T("Home.OpenProfile", "Open Profile"),
            () => Services.Navigation.Navigate(new ProfileRoute(spotlight.ProfileId)),
            ButtonKind.Primary,
            "icon.action.open");

        CoverFrameView? cover = null;
        if (_spotlight.ShowCover && spotlight.CoverImagePath is not null)
        {
            var overrides = _appearance.GetValueOrDefault(spotlight.ProfileId)
                ?? ProfileAppearanceOverrides.Default;
            var state = new ProfilePresentationState(spotlight.ProfileId, 0, null, overrides);
            var frame = ResolvePresentation(PresentationSlots.ProfileFrame,
                PresentationContext.ForProfile(state, "home")).PlanAs<FramePlan>();
            cover = new CoverFrameView
            {
                Source = ImageRef.FromPath(spotlight.CoverImagePath, 480),
                Transform = Services.Presentation.ResolveMedia(state).Cover,
                Appearance = CardDataFactory.AppearanceFor(overrides, frame,
                    ReducedMotionAuthority.IsReduced),
                Plan = frame,
                FrameMode = "full",
                Width = _spotlight.Composition == "storefront" ? 88 : 64,
                Height = _spotlight.Composition == "storefront" ? 88 : 64,
            };
            _heroCover = cover;
        }

        var tags = TagCapsules.Create(spotlight.AssignedTags, 4);
        var rating = UI.H(4,
            new IconView("icon.profile.rating", 16, spotlight.Rating is > 0 ? "warning" : "textMuted"),
            UI.Text(spotlight.Rating is { } value ? $"{value}/5" : "-/5", "caption", "onHeroPrimary"));
        var favorite = new IconView("icon.profile.favorite", 18,
            spotlight.IsFavorite ? "danger" : "onHeroSecondary");
        favorite.Tip(UI.T("Card.Favorite", "Favorite"));
        var indicators = UI.H(12, rating, favorite);
        var details = _spotlight.Composition switch
        {
            "cinema" => UI.V(8, name, tags, indicators, action),
            "editorial" => UI.V(8, name, meta, tags, indicators, overview, action),
            _ => UI.V(8, name, meta, tags, indicators, overview, action),
        };
        if (cover is null || _spotlight.Composition == "cinema")
            return details;

        var identity = UI.Grid("auto", "auto,*",
            cover.Align(vertical: VerticalAlignment.Center).At(0, 0),
            details.Align(vertical: VerticalAlignment.Center).At(0, 1));
        identity.ColumnSpacing = 16;
        return identity;
    }

    private FrameworkElement EmptyWelcome()
    {
        var theme = ThemeRuntime.Current;
        var title = UI.WrappedText(
            UI.T("Home.Empty.Title", "Your collection starts here"),
            "display",
            "textPrimary");
        var body = UI.WrappedText(
            UI.T("Home.Empty.Body", "Build a local Vault for the photos, videos and 3D models worth keeping."),
            "body",
            "textSecondary");
        var localNote = UI.WrappedText(
            UI.T(
                "Home.Empty.LocalNote",
                "Import favorites to start your collection. Originals stay untouched; copies go into the Vault."),
            "caption",
            "textSecondary");
        var importBtn = UI.Button(
            UI.T("Home.Empty.Import", "Import media"),
            () => Services.Navigation.ResetToTopLevel(new ImportRoute()),
            ButtonKind.Primary,
            "icon.navigation.import");

        Border Ring(double size, string stroke, double opacity = 1) => new()
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            BorderBrush = theme.Brush(stroke),
            BorderThickness = new Thickness(1),
            Opacity = opacity,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        Border Dot(
            double size,
            string fill,
            Thickness margin,
            HorizontalAlignment horizontal,
            VerticalAlignment vertical) => new()
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = theme.Brush(fill),
            Margin = margin,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            IsHitTestVisible = false,
        };

        var artwork = new Grid
        {
            Width = 164,
            Height = 164,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        artwork.Children.Add(Ring(150, "borderSubtle", 0.85));
        artwork.Children.Add(Ring(116, "borderInteractive", 0.72));
        artwork.Children.Add(new Border
        {
            Width = 88,
            Height = 88,
            CornerRadius = new CornerRadius(44),
            Background = theme.Brush("surface2"),
            BorderBrush = theme.Brush("glassBorder"),
            BorderThickness = new Thickness(1),
            Child = BrandAssets.Image(BrandAssets.PrimaryMarkUri, 62, "naut"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        artwork.Children.Add(Dot(12, "accent", new Thickness(0, 17, 22, 0), HorizontalAlignment.Right, VerticalAlignment.Top));
        artwork.Children.Add(Dot(8, "accentSecondary", new Thickness(24, 0, 0, 25), HorizontalAlignment.Left, VerticalAlignment.Bottom));
        artwork.Children.Add(Dot(6, "textMuted", new Thickness(18, 35, 0, 0), HorizontalAlignment.Left, VerticalAlignment.Top));

        var copy = UI.V(12, title, body, localNote, importBtn);
        copy.MaxWidth = 500;
        copy.VerticalAlignment = VerticalAlignment.Center;

        var composition = UI.Wrap(24, artwork, copy);
        composition.HorizontalAlignment = HorizontalAlignment.Stretch;

        var panel = UI.Surface(
            composition,
            Material.Deep,
            theme.Tokens.Number("radiusHero", 20),
            28);
        panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.VerticalAlignment = VerticalAlignment.Top;
        panel.Margin = new Thickness(18, 20, 18, 20);
        return panel;
    }

    private void StartSpotlightMotion()
    {
        _kenBurns?.Stop();
        _progress?.Stop();
        var seconds = _spotlight.RotationSeconds;
        _progressScale.ScaleX = 0;
        if (IsActive && _spotlight.Pagination == "counter-rail")
        {
            _progress = new Storyboard();
            var grow = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(grow, _progressScale);
            Storyboard.SetTargetProperty(grow, "ScaleX");
            _progress.Children.Add(grow);
            _progress.Begin();
        }

        _heroMotion.ScaleX = 1;
        _heroMotion.ScaleY = 1;
        _heroMotion.TranslateX = 0;
        _heroMotion.TranslateY = 0;
        // A video and its thumbnail need one stable framing during the poster-to-motion
        // handoff. Ken Burns applies only to images with no playable Banner clip.
        if (!IsActive || ReducedMotionAuthority.IsReduced
            || _spotlight.Motion == "still" || _vm.CurrentSpotlight?.HasBannerVideo == true)
        {
            return;
        }

        _kenBurns = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        const double amplitude = 1;
        void Add(string property, double to)
        {
            var animation = new DoubleAnimation { From = property.StartsWith("Scale", StringComparison.Ordinal) ? 1 : 0, To = to, Duration = new Duration(TimeSpan.FromSeconds(seconds)) };
            Storyboard.SetTarget(animation, _heroMotion);
            Storyboard.SetTargetProperty(animation, property);
            _kenBurns.Children.Add(animation);
        }

        if (_spotlight.Motion == "ken-burns")
        {
            Add("ScaleX", 1 + (0.08 * amplitude));
            Add("ScaleY", 1 + (0.08 * amplitude));
            Add("TranslateX", -24 * amplitude);
        }
        else
        {
            Add("TranslateX", -18 * amplitude);
            Add("TranslateY", -8 * amplitude);
        }

        _kenBurns.Begin();
    }

    private static void FadeIn(UIElement element)
    {
        var duration = ThemeRuntime.Current.Duration("slow");
        if (duration <= 0)
        {
            element.Opacity = 1;
            return;
        }

        var animation = new DoubleAnimation { From = 0.25, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(duration)) };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    // ─────────────────────────────────────────── rails

    private void QueueRebuildRails()
    {
        if (_retired || _railRenderQueued)
        {
            return;
        }

        _railRenderQueued = true;
        if (!_root.DispatcherQueue.TryEnqueue(() =>
        {
            _railRenderQueued = false;
            if (!_retired)
            {
                RebuildRails();
            }
        }))
        {
            _railRenderQueued = false;
            if (!_retired)
            {
                RebuildRails();
            }
        }
    }

    private void RebuildRails()
    {
        if (_layout is null || _retired)
        {
            return;
        }

        _rails.Children.Clear();
        _page.Children.Clear();
        var padding = UI.PagePadding(_root.ActualWidth > 0 ? _root.ActualWidth : 1280);

        FrameworkElement? Region(string id)
        {
            var rail = _layout.Rails.FirstOrDefault(r => r.Id == id) ?? new HomeRailPlan(id, true, "compact", 12);
            return id switch
            {
                "featured" => _hero,
                "local-navigation" => UI.Wrap(8,
                    UI.Button(UI.T("Nav.Gallery", "Gallery"), () => Services.Navigation.Navigate(new GalleryRoute()), ButtonKind.Secondary),
                    UI.Button(UI.T("Nav.Import", "Import"), () => Services.Navigation.Navigate(new ImportRoute()), ButtonKind.Ghost),
                    UI.Button(UI.T("Settings.Vault.Title", "Vault"), () => Services.Navigation.Navigate(new SettingsRoute(SettingsSection.Vault)), ButtonKind.Ghost)),
                "recent" or "profiles" => ProfileRail(padding, rail, false, UI.T("Home.Rail.Recent", "Continue")),
                "favorites" => ProfileRail(padding, rail, true, UI.T("Gallery.Favorites", "Favorites")),
                "statistics" => PulseBar(padding, rail),
                "activity" => ChronicleRail(padding, rail),
                "discovery" when _vm.Discovery.Count > 0 => FeaturedConnectionRail(padding),
                "attention" when _vm.NeedsAttention.Count > 0 => AttentionRail(padding, rail.MaxItems),
                "imports" when _vm.ActiveImports.Count > 0 => ActiveImportsRail(padding, rail.MaxItems),
                _ => null,
            };
        }
        if (_layout.Surface is { } surfacePlan)
        {
            _page.Children.Add(_notice.Margin(20, 12, 20, 0));
            _page.Children.Add(SurfaceComposer.Build(surfacePlan, Region, _root.ActualWidth > 0 ? _root.ActualWidth : 1280).Margin(0, 0, 0, 24));
            return;
        }
        SurfaceComposer.Detach(_hero);
        SurfaceComposer.Detach(_rails);
        _page.Children.Add(_hero);
        _page.Children.Add(_notice.Margin(20, 12, 20, 0));
        _page.Children.Add(_rails.Margin(0, 0, 0, 36));

        // Layouts own rail order and form. Product policy keeps Library and This month present.
        foreach (var rail in HomeRailPolicy.Resolve(_layout))
        {
            FrameworkElement? surface = rail.Id switch
            {
                "recent" => ProfileRail(padding, rail, favoritesOnly: false, UI.T("Home.Rail.Recent", "Continue")),
                "favorites" => ProfileRail(padding, rail, favoritesOnly: true, "Favorites"),
                "profiles" => ProfileRail(padding, rail, favoritesOnly: false, "Profiles"),
                "statistics" => PulseBar(padding, rail),
                "discovery" when _vm.Discovery.Count > 0 => FeaturedConnectionRail(padding),
                "attention" when _vm.NeedsAttention.Count > 0 => AttentionRail(padding, rail.MaxItems),
                "activity" => ChronicleRail(padding, rail),
                "imports" when _vm.ActiveImports.Count > 0 => ActiveImportsRail(padding, rail.MaxItems),
                _ => null,
            };

            if (surface is not null)
            {
                _rails.Children.Add(surface);
            }
        }
    }

    // ─── Library pulse: its form is controlled by the selected Home style.

    private FrameworkElement PulseBar(double padding, HomeRailPlan rail)
    {
        var pulse = _vm.VaultPulse;
        var stats = new (string Value, string Label)[]
        {
            (pulse.ActiveProfileCount.ToString("N0"), UI.T("Home.Stat.Profiles", "Profiles")),
            (pulse.ActiveMediaCount.ToString("N0"), UI.T("Home.Stat.Media", "Media")),
            (pulse.StorageSummary, UI.T("Home.Stat.Storage", "Space used")),
            (pulse.HealthState, UI.T("Home.Stat.Condition", "Condition")),
        };

        var title = UI.Grid("auto", "*,auto");
        title.Children.Add(HomeReadability.Heading("Vault", "page-title").At(0, 0));
        title.Children.Add(
            UI.Button(
                    UI.T("Home.Gallery", "Browse all"),
                    () => Services.Navigation.Navigate(new GalleryRoute()),
                    ButtonKind.Ghost,
                    "icon.navigation.gallery")
                .Align(HorizontalAlignment.Right, VerticalAlignment.Center)
                .At(0, 1));

        var body = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnSpacing = 12,
            RowSpacing = 8,
        };

        void ArrangeStats(double width)
        {
            var columns = width >= 880 ? stats.Length : width >= 460 ? 2 : 1;
            if (body.ColumnDefinitions.Count == columns) return;

            body.Children.Clear();
            body.ColumnDefinitions.Clear();
            body.RowDefinitions.Clear();
            body.ColumnSpacing = columns == stats.Length ? 16 : 10;
            body.RowSpacing = 8;

            for (var column = 0; column < columns; column++)
            {
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            var rows = (int)Math.Ceiling(stats.Length / (double)columns);
            for (var row = 0; row < rows; row++)
            {
                body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (var index = 0; index < stats.Length; index++)
            {
                FrameworkElement item = rail.Style switch
                {
                    "tile" or "poster" => PulseStatCard(stats[index].Value, stats[index].Label, rail.Style),
                    "compact" => HomeReadability.Region(UI.V(
                        2,
                        HomeReadability.Text(stats[index].Label, "caption"),
                        HomeReadability.Text(stats[index].Value, "section-title")), 10)
                        .Margin(10, 6, 10, 6),
                    _ => PulseStat(stats[index].Value, stats[index].Label),
                };

                item.HorizontalAlignment = HorizontalAlignment.Stretch;
                Grid.SetRow(item, index / columns);
                Grid.SetColumn(item, index % columns);
                body.Children.Add(item);
            }
        }

        body.SizeChanged += (_, args) => ArrangeStats(args.NewSize.Width);
        var availableWidth = Math.Max(0, _root.ActualWidth - (padding * 2));
        ArrangeStats(availableWidth);
        return UI.V(8, title, body).Margin(padding, 12, padding, 0);
    }

    private static FrameworkElement PulseStat(string value, string label) =>
        HomeReadability.Region(UI.V(1,
            HomeReadability.Text(value, "section-title", 1),
            HomeReadability.Text(label, "caption")), 12)
            .Margin(16, 8, 16, 8);

    private static FrameworkElement PulseStatCard(string value, string label, string style)
    {
        var card = HomeReadability.Region(
            UI.V(
                style == "poster" ? 6 : 3,
                HomeReadability.Text(value, style == "poster" ? "page-title" : "section-title", 1),
                HomeReadability.Text(label, "caption", 1)),
            style == "poster" ? 16 : 12);
        card.MaxWidth = style == "poster" ? 178 : 148;
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        card.MinHeight = style == "poster" ? 96 : 68;
        return card;
    }

    // ─── Profile rails: the layout decides order, visibility and item cap.

    private FrameworkElement? ProfileRail(
        double padding,
        HomeRailPlan rail,
        bool favoritesOnly,
        string titleText)
    {
        var candidates = favoritesOnly
            ? _vm.RecentlyActive.Where(static item => item.IsFavorite)
            : _vm.RecentlyActive;
        var items = candidates
            .Take(Math.Max(1, rail.MaxItems))
            .Select(t => (t.ProfileId, t.DisplayName, t.CoverImagePath, t.MediaCountText, t.IsFavorite))
            .ToList();
        if (items.Count == 0)
        {
            return null;
        }

        var title = UI.H(8,
            HomeReadability.Heading(titleText),
            HomeReadability.Heading(
                UI.F("Home.Rail.Recent.Count", "{0} Profiles", items.Count),
                "micro", 6).Align(vertical: VerticalAlignment.Center));
        var (itemWidth, itemHeight) = ProfileRailGeometry(rail.Style);
        return UI.V(6, title, ProfileCarousel(items, itemWidth, itemHeight, rail.Style))
            .Margin(padding, 10, padding, 0);
    }

    private static (double Width, double Height) ProfileRailGeometry(string style) => style switch
    {
        "poster" => (132, 178),
        "strip" => (232, 104),
        "compact" => (200, 88),
        _ => (164, 166),
    };

    private FrameworkElement ProfileCarousel(
        IReadOnlyList<(Guid ProfileId, string DisplayName, string? CoverImagePath, string MediaCountText, bool IsFavorite)> items,
        double width, double height, string style)
    {
        var theme = ThemeRuntime.Current;
        var source = new ObservableCollection<object>(items.Select(i => (object)i));
        var factory = new PooledElementFactory(
            _ => "poster-card",
            (_, _) =>
            {
                var tile = new HomeProfileTile(style, width, height,
                    id => Services.Navigation.Navigate(new ProfileRoute(id)));
                _profileTiles.RemoveAll(reference => !reference.TryGetTarget(out _));
                _profileTiles.Add(new WeakReference<HomeProfileTile>(tile));
                return tile;
            },
            (element, data) =>
            {
                if (data is not ValueTuple<Guid, string, string?, string, bool> item)
                {
                    return;
                }

                var tile = (HomeProfileTile)element;
                tile.Bind(item.Item1, item.Item2, item.Item3, item.Item4, item.Item5);
                Services.ProfileSnapshots.Warm(item.Item1);
                RefreshRailTile(tile);
            }, element => ((HomeProfileTile)element).Clear());
        const double gap = 10;
        var (scroll, repeater) = Repeaters.Virtualized(factory, Repeaters.Stack(true, gap), horizontal: true);
        repeater.ItemsSource = source;
        var hoverInset = Math.Ceiling(height * 0.02);
        repeater.Margin = new Thickness(0, hoverInset, 0, hoverInset);
        var gutter = theme.Tokens.Number("scrollbarContentGutter", 16);
        void UpdateEnvelope()
        {
            var viewport = scroll.ActualWidth;
            var overflow = viewport <= 0 || (items.Count * width) + (Math.Max(0, items.Count - 1) * gap) > viewport;
            scroll.Padding = new Thickness(0, 0, 0, overflow ? gutter : 0);
            scroll.Height = height + (hoverInset * 2) + (overflow ? gutter : 0);
        }
        scroll.SizeChanged += (_, _) => UpdateEnvelope();
        UpdateEnvelope();
        return scroll;
    }

    private void RefreshRailTile(HomeProfileTile tile)
    {
        Services.RunUserAction(ApplyRailAppearanceAsync(tile), "HomeSurface.RailAppearance",
            UI.T("Home.MediaFailed", "Spotlight media could not be loaded."));
    }

    private async Task ApplyRailAppearanceAsync(HomeProfileTile tile)
    {
        var id = tile.ProfileId;
        var binding = tile.BindingVersion;
        var generation = _railAppearanceGeneration;
        if (!_appearance.TryGetValue(id, out var appearance))
        {
            var summary = await Services.Catalog.GalleryReads.GetGalleryProfileSummaryAsync(id).ConfigureAwait(true);
            if (_retired || generation != _railAppearanceGeneration || binding != tile.BindingVersion) return;
            appearance = summary?.EffectiveAppearance ?? ProfileAppearanceOverrides.Default;
            _appearance[id] = appearance;
        }
        if (_retired || generation != _railAppearanceGeneration || binding != tile.BindingVersion) return;
        var state = PreviewContext(PresentationContext.ForProfile(new ProfilePresentationState(id, 0, null, appearance), "home")).Profile!;
        var frame = ResolvePresentation(PresentationSlots.ProfileFrame, PresentationContext.ForProfile(state, "home")).PlanAs<FramePlan>();
        tile.Apply(frame, CardDataFactory.AppearanceFor(state.Overrides, frame, ReducedMotionAuthority.IsReduced),
            Services.Presentation.ResolveMedia(state).Cover);
    }

    private void RefreshRailFrames()
    {
        _railAppearanceGeneration++;
        _profileTiles.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in _profileTiles)
            if (reference.TryGetTarget(out var tile) && tile.IsLoaded && tile.ProfileId != Guid.Empty) RefreshRailTile(tile);
    }

    // ─── Featured connection

    private FrameworkElement? FeaturedConnectionRail(double padding)
    {
        var item = _vm.FeaturedConnection;
        if (item is null)
        {
            return null;
        }

        var theme = ThemeRuntime.Current;
        var icon = new IconView("icon.profile.related", 18, "accent");
        var label = UI.Text(UI.T("Home.Connection.Featured", "Connected"), "micro", "accent");
        var names = UI.Text(
            UI.F("Home.Connection.Pair", "{0} · {1}", item.ProfileDisplayName, item.RelatedDisplayName),
            "body-strong", maxLines: 1);
        var evidence = UI.Text(item.EvidenceSummary, "caption", "textSecondary", 1);

        var content = UI.H(12,
            icon.Align(vertical: VerticalAlignment.Center),
            UI.V(2, label, names, evidence));
        var card = UI.Surface(content, Material.Grounded, theme.Tokens.Number("radiusCard", 12), 14);
        Services.ProfileSnapshots.Warm(item.ProfileId);
        card.MaxWidth = 520;
        UI.DisableTextSelection(card);
        card.Tapped += (_, _) => Services.Navigation.Navigate(new ProfileRoute(item.ProfileId));
        card.PointerEntered += (_, _) => card.BorderBrush = theme.Brush("borderSubtle");
        card.PointerExited += (_, _) => card.BorderBrush = theme.Brush("transparent");
        card.BorderThickness = new Thickness(1);
        card.BorderBrush = theme.Brush("transparent");

        var row = UI.H(12, card);
        return row.Margin(padding, 16, padding, 0);
    }

    // ─── Attention items

    private FrameworkElement AttentionRail(double padding, int maxItems)
    {
        var theme = ThemeRuntime.Current;
        var cards = UI.H(12);
        foreach (var item in _vm.NeedsAttention.Take(Math.Max(1, maxItems)))
        {
            var icon = new IconView("icon.status.warning", 16, "warning");
            var title = UI.Text(item.Title, "body-strong", maxLines: 1);
            var detail = UI.Text(item.Detail, "caption", "textSecondary", 2);
            var card = UI.Surface(UI.H(10, icon.Align(vertical: VerticalAlignment.Center), UI.V(3, title, detail)), Material.Raised, theme.Tokens.Number("radiusCard", 12), 14);
            card.Width = 320;
            card.MinWidth = 260;
            if (item.Route is { } route)
            {
                UI.DisableTextSelection(card);
                card.Tapped += (_, _) => Services.Navigation.Navigate(route);
                    }

            cards.Children.Add(card);
        }

        return UI.V(10,
            HomeReadability.Heading(UI.T("Home.Rail.Attention", "Needs your attention")),
            UI.Scroll(cards, horizontal: true))
            .Margin(padding, 16, padding, 0);
    }

    // ─── Monthly chronicle: persistent, with a shape chosen by the Home style.

    private FrameworkElement ChronicleRail(double padding, HomeRailPlan rail)
    {
        var items = _vm.RecentActivity.Take(Math.Max(1, rail.MaxItems)).ToList();
        FrameworkElement body;
        if (items.Count == 0)
        {
            body = HomeReadability.Heading(
                UI.T("Home.Rail.Activity.Empty", "No activity this month yet."),
                "caption");
        }
        else if (rail.Style == "compact")
        {
            var row = UI.V(6);
            foreach (var item in items)
            {
                row.Children.Add(UI.Surface(UI.V(2,
                    UI.WrappedText(item.Description, "caption", "textSecondary"),
                    UI.Text(item.RelativeTimeText, "micro", "textMuted")), Material.Raised, 8, 8));
            }
            body = row;
        }
        else if (rail.Style is "tile" or "poster")
        {
            var cards = UI.H(rail.Style == "poster" ? 12 : 10);
            foreach (var item in items)
            {
                var card = UI.Surface(
                    UI.V(
                        rail.Style == "poster" ? 6 : 3,
                        UI.Text(item.Description, rail.Style == "poster" ? "body-strong" : "control", maxLines: rail.Style == "poster" ? 3 : 2),
                        UI.Text(item.RelativeTimeText, "micro", "textMuted", 1)),
                    rail.Style == "poster" ? Material.Glass : Material.Raised,
                    rail.Style == "poster" ? 14 : 10,
                    rail.Style == "poster" ? 16 : 12);
                card.Width = rail.Style == "poster" ? 220 : 260;
                card.MinHeight = rail.Style == "poster" ? 112 : 72;
                cards.Children.Add(card);
            }
            body = UI.Scroll(cards, horizontal: true);
        }
        else
        {
            var chips = UI.H(8);
            foreach (var item in items)
            {
                var chip = UI.Chip(item.Description, onClick: null);
                chip.Opacity = 0.85;
                chips.Children.Add(chip);
            }
            body = UI.Scroll(chips, horizontal: true);
        }

        return UI.V(
            10,
            HomeReadability.Heading(UI.T("Home.Rail.Activity", "Recently")),
            body)
            .Margin(padding, 16, padding, 0);
    }
    // ─── Active imports

    private FrameworkElement ActiveImportsRail(double padding, int maxItems)
    {
        var items = UI.H(12);
        foreach (var import in _vm.ActiveImports.Take(Math.Max(1, maxItems)))
        {
            var icon = new IconView("icon.navigation.import", 16, "accent");
            var name = UI.Text(System.Net.WebUtility.HtmlDecode(import.SourceDisplayName), "body-strong", maxLines: 1);
            var stage = UI.Text(import.StageText, "caption", "textSecondary", 1);
            var card = UI.Surface(UI.H(10, icon.Align(vertical: VerticalAlignment.Center), UI.V(2, name, stage)), Material.Raised, 10, 12);
            card.Width = 280;
            UI.DisableTextSelection(card);
            card.Tapped += (_, _) => Services.Navigation.Navigate(new ImportRoute());
            items.Children.Add(card);
        }

        return UI.V(10,
            HomeReadability.Heading(UI.T("Home.Rail.Imports", "Importing")),
            UI.Scroll(items, horizontal: true))
            .Margin(padding, 16, padding, 0);
    }

    public override void Dispose()
    {
        _retired = true;
        _railRenderQueued = false;
        OnSuspended();
        _heroVideo.Dispose();
        base.Dispose();
    }
}
