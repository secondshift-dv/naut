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
    private UIElement? _pagination;
    private readonly TextBlock _counter = UI.Text(string.Empty, "numeric", "onHeroSecondary");
    private readonly ScaleTransform _progressScale = new() { ScaleX = 0, ScaleY = 1 };
    private readonly Border _progressFill = new() { Height = 2, HorizontalAlignment = HorizontalAlignment.Left, Width = 120 };
    private readonly DispatcherTimer _rotation = new();
    private readonly Dictionary<Guid, ProfileAppearanceOverrides> _appearance = [];
    private CoverFrameView? _heroCover;
    private Storyboard? _kenBurns;
    private Storyboard? _progress;
    private HomeLayoutPlan _layout = null!;
    private SpotlightPlan _spotlight = null!;
    private bool _pointerOverHero;
    private bool _railRenderQueued;
    private bool _retired;
    private int _mediaGeneration;

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
            frame);
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
        if (args.Slots.Contains(PresentationSlots.ProfileCover) || args.Slots.Contains(PresentationSlots.ProfileBanner))
        {
            _appearance.Clear();
            Services.RunUserAction(_vm.RefreshAsync(), "HomeSurface.RefreshProfilePresentation",
                UI.T("Home.RefreshFailed", "Home could not be refreshed."));
            OnSpotlightChanged();
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
        SurfaceComposer.Detach(_heroImage);
        SurfaceComposer.Detach(_heroVideo);
        SurfaceComposer.Detach(_identityHost);
        var inset = _layout.HeroPlacement == "inset";
        var art = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _heroImage.HorizontalAlignment = HorizontalAlignment.Stretch;
        _heroImage.VerticalAlignment = VerticalAlignment.Stretch;
        _heroVideo.HorizontalAlignment = HorizontalAlignment.Stretch;
        _heroVideo.VerticalAlignment = VerticalAlignment.Stretch;
        art.SizeChanged += (_, args) =>
        {
            if (args.NewSize.Width <= 0 || args.NewSize.Height <= 0)
            {
                return;
            }

            _heroImage.Width = args.NewSize.Width;
            _heroImage.Height = args.NewSize.Height;
            _heroVideo.Width = args.NewSize.Width;
            _heroVideo.Height = args.NewSize.Height;
        };
        art.Children.Add(_heroImage);
        art.Children.Add(_heroVideo);

        var visual = new Grid();
        visual.Children.Add(art);
        visual.Children.Add(Scrim());
        var horizontalInset = UI.PagePadding(_root.ActualWidth);
        var topInset = 12.0;
        var clip = new Border
        {
            Child = visual,
            CornerRadius = new CornerRadius(inset ? ThemeRuntime.Current.Tokens.Number("radiusHero", 20) : 0),
            Margin = inset ? new Thickness(horizontalInset, topInset, horizontalInset, 0) : new Thickness(0),
        };

        if (_layout.HeroPlacement == "split" || _spotlight.Composition is "split-glass" or "storefront")
        {
            clip.HorizontalAlignment = HorizontalAlignment.Left;
        }

        if (_spotlight.Composition != "snapshot") _hero.Children.Add(clip);
        _hero.Children.Add(_identityHost);
        _pagination = Pagination();
        _hero.Children.Add(_pagination);
        LayoutHero();
        OnSpotlightChanged();
    }

    private UIElement Scrim()
    {
        var theme = ThemeRuntime.Current;
        var strength = _spotlight.ScrimStrength;
        var grid = new Grid { IsHitTestVisible = false };
        var bottom = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.5, 1), EndPoint = new Windows.Foundation.Point(0.5, 0.2) };
        bottom.GradientStops.Add(new GradientStop { Color = WithAlpha(theme.Color("heroScrimStrong"), strength), Offset = 0 });
        bottom.GradientStops.Add(new GradientStop { Color = WithAlpha(theme.Color("heroScrimStrong"), 0), Offset = 1 });
        grid.Children.Add(new Border { Background = bottom });
        if (_spotlight.Composition is "cinematic-left" or "editorial" or "cinema")
        {
            var left = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0.5), EndPoint = new Windows.Foundation.Point(0.65, 0.5) };
            left.GradientStops.Add(new GradientStop { Color = WithAlpha(theme.Color("heroScrimStrong"), strength * 0.85), Offset = 0 });
            left.GradientStops.Add(new GradientStop { Color = WithAlpha(theme.Color("heroScrimSoft"), 0), Offset = 1 });
            grid.Children.Add(new Border { Background = left });
        }

        return grid;
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
        var panel = UI.H(10,
            previous,
            _counter,
            _spotlight.Pagination == "counter-rail" ? track.Align(vertical: VerticalAlignment.Center) : null,
            next);
        panel.HorizontalAlignment = HorizontalAlignment.Right;
        panel.VerticalAlignment = VerticalAlignment.Bottom;
        panel.Margin = new Thickness(0, 0, ThemeRuntime.Current.Tokens.Number("space24", 24), 22);
        var control = new Border
        {
            Child = panel,
            Background = theme.Brush("mediaOverlay"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12, 0, UI.PagePadding(_root.ActualWidth), 10),
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
        var height = _root.ActualHeight > 0 ? _root.ActualHeight : 720;
        var width = _hero.ActualWidth > 0 ? _hero.ActualWidth : _root.ActualWidth > 0 ? _root.ActualWidth : 1280;
        if (_vm.CurrentSpotlight is null)
        {
            _hero.Height = double.NaN;
            _hero.MinHeight = 0;
            if (_identityHost.Children.FirstOrDefault() is FrameworkElement welcome)
            {
                welcome.MaxWidth = Math.Min(760, Math.Max(1, width - 2 * UI.PagePadding(width)));
                welcome.Margin = new Thickness(UI.PagePadding(width), 20, UI.PagePadding(width), 20);
            }
            return;
        }
        var minimum = Math.Min(_layout.HeroMinHeight, Math.Max(160, height * 0.52));
        var maximum = Math.Max(minimum, Math.Min(_layout.HeroMaxHeight, height * 0.56));
        _hero.Height = Math.Clamp(height * _layout.HeroFraction, minimum, maximum);
        var pagePadding = UI.PagePadding(width);
        if (_hero.Children.Count > 0 && _hero.Children[0] is Border art)
        {
            var inset = _layout.HeroPlacement == "inset";
            art.Margin = inset ? new Thickness(pagePadding, 12, pagePadding, 0) : new Thickness(0);
            if (_layout.HeroPlacement == "split" || _spotlight.Composition is "split-glass" or "storefront")
            {
                art.Width = width >= 840 ? width * 0.6 : width;
            }
        }
        if (_hero.Children.Count > 2 && _hero.Children[2] is Border pagination)
        {
            pagination.Margin = new Thickness(12, 0, pagePadding + (_layout.HeroPlacement == "inset" ? 12 : 0), 14);
            pagination.Width = double.NaN;
            pagination.MaxWidth = double.PositiveInfinity;
        }
        if (_identityHost.Children.FirstOrDefault() is FrameworkElement identity)
        {
            identity.MaxWidth = (_layout.HeroPlacement == "split" || _spotlight.Composition is "split-glass" or "storefront") && width >= 840
                ? Math.Min(420, width * 0.44)
                : Math.Min(560, Math.Max(180, width - (2 * pagePadding)));
            if (_layout.HeroPlacement == "split" || _spotlight.Composition is "split-glass" or "storefront")
            {
                var rightInset = pagePadding + (_layout.HeroPlacement == "inset" ? 12 : 0);
                identity.Margin = new Thickness(12, 0, rightInset, 58);
                if (_spotlight.Composition == "storefront" && _hero.Children.LastOrDefault() is Border storefrontPagination)
                {
                    identity.MaxWidth = Math.Min(420, Math.Max(180, width - rightInset - 12));
                    identity.MinWidth = Math.Min(280, identity.MaxWidth);
                    identity.VerticalAlignment = VerticalAlignment.Bottom;
                    identity.Measure(new Windows.Foundation.Size(identity.MaxWidth, double.PositiveInfinity));
                    storefrontPagination.Width = identity.ActualWidth > 0
                        ? identity.ActualWidth
                        : Math.Max(0, identity.DesiredSize.Width - identity.Margin.Left - identity.Margin.Right);
                    storefrontPagination.MaxWidth = identity.MaxWidth;
                    _hero.Height = Math.Max(_hero.Height, identity.DesiredSize.Height + 14);
                }
            }
            else if (_spotlight.Composition == "editorial")
            {
                var contentInset = pagePadding + (_layout.HeroPlacement == "inset" ? 28 : 12);
                identity.Margin = new Thickness(contentInset, 24, contentInset, 56);
                identity.MaxWidth = Math.Min(520, Math.Max(120, width - (2 * contentInset)));
                identity.Measure(new Windows.Foundation.Size(identity.MaxWidth, double.PositiveInfinity));
                _hero.Height = Math.Max(_hero.Height, identity.DesiredSize.Height + 12);
            }
        }
    }

    private void OnSpotlightChanged()
    {
        var generation = ++_mediaGeneration;
        var spotlight = _vm.CurrentSpotlight;
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

        var heroPresentation = spotlight.HasBannerVideo || spotlight.BannerImagePath is not null
            ? media.Banner
            : media.Cover;
        _heroImage.Transform = heroPresentation;
        _heroVideo.SetPresentation(heroPresentation);
        PlayHeroVideo(spotlight, media.Playback);
    }

    private void PlayHeroVideo(HomeSpotlightViewModel spotlight, PlaybackPresentationState playback)
    {
        if (_spotlight.Composition == "snapshot") { StopHeroVideo(); return; }
        StopHeroVideo();
        if (!IsActive || !spotlight.HasBannerVideo || ResourceGovernor.Shared.Tier != PresentationTier.Full || ReducedMotionAuthority.IsReduced)
        {
            return;
        }

        // Keep the still fallback visible until the decoder has produced a frame. This avoids a cold-start
        // transparency window while FFmpeg starts, without ever giving the video a different geometry.
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
        var textOverImage = _spotlight.Composition is "editorial" or "centered-poster" or "cinema";
        var name = UI.Text(spotlight.DisplayName, _spotlight.Composition == "snapshot" ? "section-title" : _spotlight.Composition == "editorial" ? "display" : "hero", textOverImage ? "onHeroPrimary" : "textPrimary", _spotlight.Composition == "centered-poster" ? 1 : 2);
        var meta = UI.Text(string.Join(" · ", new[] { spotlight.CategoryName, spotlight.TagSummary, spotlight.MediaCountText }.Where(s => !string.IsNullOrWhiteSpace(s))), "metadata", textOverImage ? "onHeroSecondary" : "textSecondary", 1);
        var overview = _spotlight.ShowOverview && !string.IsNullOrWhiteSpace(spotlight.OverviewExcerpt)
            ? UI.Text(spotlight.OverviewExcerpt, "body", textOverImage ? "onHeroSecondary" : "textSecondary", 2)
            : null;
        var actions = UI.H(8,
            UI.Button(
                UI.T("Home.OpenProfile", "Open Profile"),
                () => Services.Navigation.Navigate(new ProfileRoute(spotlight.ProfileId)),
                ButtonKind.Primary,
                "icon.action.open"));

        CoverFrameView? cover = null;
        if (_spotlight.ShowCover && spotlight.CoverImagePath is not null)
        {
            var overrides = _appearance.GetValueOrDefault(spotlight.ProfileId) ?? ProfileAppearanceOverrides.Default;
            var state = new ProfilePresentationState(spotlight.ProfileId, 0, null, overrides);
            var frame = ResolvePresentation(
                PresentationSlots.ProfileFrame,
                PresentationContext.ForProfile(state, "home")).PlanAs<FramePlan>();
            cover = new CoverFrameView
            {
                Source = ImageRef.FromPath(spotlight.CoverImagePath, 480),
                Transform = Services.Presentation.ResolveMedia(state).Cover,
                Appearance = CardDataFactory.AppearanceFor(overrides, frame, ReducedMotionAuthority.IsReduced),
                Plan = frame,
                FrameMode = "full",
                Width = 80,
                Height = 80,
            };
            _heroCover = cover;
        }

        switch (_layout.HeroPlacement == "split" ? "split-glass" : _spotlight.Composition)
        {
            case "snapshot":
                return UI.Surface(UI.Grid("auto", "auto,*",
                    (cover ?? new CoverFrameView { Width = 72, Height = 72 }).At(0, 0),
                    UI.V(6, name, meta, overview, UI.Wrap(8, UI.Badge(spotlight.MediaCountText), actions)).Margin(12, 0, 0, 0).At(0, 1)),
                    Material.Raised, 12, 20).Align(HorizontalAlignment.Stretch, VerticalAlignment.Center).Margin(20, 12, 20, 44);
            case "storefront":
                return UI.Surface(UI.V(10, name, meta, overview, UI.Wrap(8, UI.Badge(spotlight.MediaCountText), actions)),
                    Material.Deep, 12, 20).Align(HorizontalAlignment.Right, VerticalAlignment.Center).Margin(12, 12, 24, 44);
            case "cinema":
                return UI.V(10, name, actions).Align(HorizontalAlignment.Left, VerticalAlignment.Bottom).Margin(24, 0, 24, 56);
            case "centered-poster":
                return UI.V(8, cover?.Align(HorizontalAlignment.Center), name.Align(HorizontalAlignment.Center), meta.Align(HorizontalAlignment.Center), actions.Align(HorizontalAlignment.Center))
                    .Align(HorizontalAlignment.Center, VerticalAlignment.Center).Margin(12);
            case "split-glass":
            {
                var content = UI.Grid("auto", cover is null ? "*" : "auto,*");
                content.ColumnSpacing = 12;
                if (cover is not null)
                {
                    content.Children.Add(cover.At(0, 0));
                }
                content.Children.Add(UI.V(6, name, meta, overview, actions).At(0, cover is null ? 0 : 1));
                var panel = UI.Surface(content, Material.Deep, ThemeRuntime.Current.Tokens.Number("radiusHero", 20), 16);
                panel.MaxWidth = 420;
                panel.HorizontalAlignment = HorizontalAlignment.Right;
                panel.VerticalAlignment = VerticalAlignment.Center;
                panel.Margin = new Thickness(12, 0, UI.PagePadding(_root.ActualWidth), 44);
                return panel;
            }

            case "editorial":
                return UI.V(8, meta, name, overview, actions).Align(HorizontalAlignment.Left, VerticalAlignment.Bottom).Margin(18, 0, 18, 56);
            default:
            {
                var content = UI.Grid("auto", cover is null ? "*" : "auto,*");
                if (cover is not null)
                {
                    content.Children.Add(cover.Margin(0, 0, 12, 0).At(0, 0));
                }

                content.Children.Add(UI.V(8, name, meta, overview, actions).At(0, cover is null ? 0 : 1));
                var panel = UI.Surface(content, Material.Deep, ThemeRuntime.Current.Tokens.Number("radiusHero", 20), 16);
                panel.MaxWidth = 560;
                panel.HorizontalAlignment = HorizontalAlignment.Left;
                panel.VerticalAlignment = VerticalAlignment.Bottom;
                panel.Margin = new Thickness(18, 0, 18, 56);
                return panel;
            }
        }
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
                "Start with a few favorites, shape them into Profiles, and make the collection yours. Your originals stay untouched; imports are copied into the Vault."),
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
        if (!IsActive || ReducedMotionAuthority.IsReduced || _spotlight.Motion == "still")
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
        title.Children.Add(UI.Text("Vault", "section-title").At(0, 0));
        title.Children.Add(
            UI.Button(
                    UI.T("Home.Gallery", "Browse all"),
                    () => Services.Navigation.Navigate(new GalleryRoute()),
                    ButtonKind.Ghost,
                    "icon.navigation.gallery")
                .Align(HorizontalAlignment.Right, VerticalAlignment.Center)
                .At(0, 1));

        FrameworkElement body;
        if (rail.Style == "compact")
        {
            var summary = UI.H(0);
            for (var index = 0; index < stats.Length; index++)
            {
                if (index > 0)
                {
                    summary.Children.Add(PulseDivider());
                }

                summary.Children.Add(
                    UI.H(
                        5,
                        UI.Text(stats[index].Label, "micro", "textMuted"),
                        UI.Text(stats[index].Value, "control", "textPrimary"))
                    .Align(vertical: VerticalAlignment.Center)
                    .Margin(12, 6, 12, 6));
            }
            body = UI.Scroll(summary, horizontal: true);
        }
        else if (rail.Style is "tile" or "poster")
        {
            var cards = UI.H(rail.Style == "poster" ? 12 : 10);
            foreach (var stat in stats)
            {
                cards.Children.Add(PulseStatCard(stat.Value, stat.Label, rail.Style));
            }
            body = UI.Scroll(cards, horizontal: true);
        }
        else
        {
            var strip = UI.H(0);
            for (var index = 0; index < stats.Length; index++)
            {
                if (index > 0)
                {
                    strip.Children.Add(PulseDivider());
                }
                strip.Children.Add(PulseStat(stats[index].Value, stats[index].Label));
            }
            body = UI.Scroll(strip, horizontal: true);
        }

        return UI.V(8, title, body).Margin(padding, 12, padding, 0);
    }

    private static FrameworkElement PulseStat(string value, string label)
    {
        return UI.V(1,
            UI.Text(value, "section-title", maxLines: 1),
            UI.Text(label, "micro", "textMuted"))
            .Margin(16, 8, 16, 8);
    }

    private static FrameworkElement PulseStatCard(string value, string label, string style)
    {
        var card = UI.Surface(
            UI.V(
                style == "poster" ? 6 : 3,
                UI.Text(value, style == "poster" ? "page-title" : "section-title", maxLines: 1),
                UI.Text(label, "caption", "textSecondary", 1)),
            style == "poster" ? Material.Glass : Material.Raised,
            style == "poster" ? 14 : 10,
            style == "poster" ? 16 : 12);
        card.Width = style == "poster" ? 178 : 148;
        card.MinHeight = style == "poster" ? 96 : 68;
        return card;
    }

    private static FrameworkElement PulseDivider()
    {
        return new Border
        {
            Width = 1,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.15,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128)),
        };
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
            UI.Text(titleText, "section-title"),
            UI.Text(
                UI.F("Home.Rail.Recent.Count", "{0} profiles", items.Count),
                "micro",
                "textMuted").Align(vertical: VerticalAlignment.Center));
        var (itemWidth, itemHeight) = ProfileRailGeometry(rail.Style);
        return UI.V(6, title, ProfileCarousel(items, itemWidth, itemHeight))
            .Margin(padding, 10, padding, 0);
    }

    private static (double Width, double Height) ProfileRailGeometry(string style) => style switch
    {
        "poster" => (120, 150),
        "strip" => (208, 104),
        "compact" => (184, 103),
        _ => (176, 114),
    };

    private FrameworkElement ProfileCarousel(
        IReadOnlyList<(Guid ProfileId, string DisplayName, string? CoverImagePath, string MediaCountText, bool IsFavorite)> items,
        double width, double height)
    {
        var theme = ThemeRuntime.Current;
        var radius = theme.Tokens.Number("radiusCard", 12);
        var source = new ObservableCollection<object>(items.Select(i => (object)i));
        var factory = new PooledElementFactory(
            _ => "poster-card",
            (_, _) =>
            {
                var image = new SkImageView { CornerRadiusValue = radius, PlaceholderToken = "surface2" };
                var name = UI.Text(null, "control", "onMediaPrimary", maxLines: 1);
                var count = UI.Text(null, "micro", "onMediaSecondary", maxLines: 1);
                var fav = new IconView("icon.profile.favorite", 12, "warning") { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 6, 0) };
                var overlay = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.5, 1), EndPoint = new Windows.Foundation.Point(0.5, 0.5) };
                overlay.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(180, 0, 0, 0), Offset = 0 });
                overlay.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, 0, 0, 0), Offset = 1 });
                var scrim = new Border { Background = overlay, IsHitTestVisible = false, CornerRadius = new CornerRadius(0, 0, radius, radius) };
                var textStack = UI.V(1, name, count)
                    .Align(HorizontalAlignment.Stretch, VerticalAlignment.Bottom)
                    .Margin(8, 0, 8, 8);
                var body = new Grid { Width = width, Height = height, IsHitTestVisible = false };
                body.Children.Add(image);
                body.Children.Add(scrim);
                body.Children.Add(textStack);
                body.Children.Add(fav);

                var tile = new Grid
                {
                    Background = theme.Brush("transparent"),
                    Width = width,
                    Height = height,
                };
                tile.Children.Add(body);
                var scale = new ScaleTransform();
                tile.RenderTransform = scale;
                tile.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                tile.PointerEntered += (_, _) => { if (!ReducedMotionAuthority.IsReduced) { scale.ScaleX = scale.ScaleY = 1.04; } };
                tile.PointerExited += (_, _) => scale.ScaleX = scale.ScaleY = 1;
                UI.DisableTextSelection(tile);
                tile.Tapped += (_, _) =>
                {
                    if (tile.DataContext is ValueTuple<Guid, string, string?, string, bool> data)
                    {
                        Services.Navigation.Navigate(new ProfileRoute(data.Item1));
                    }
                };
                return tile;
            },
            (element, data) =>
            {
                if (data is not ValueTuple<Guid, string, string?, string, bool> item)
                {
                    return;
                }

                element.DataContext = item;
                Services.ProfileSnapshots.Warm(item.Item1);
                var body = (Grid)((Grid)element).Children[0];
                var image = body.Children.OfType<SkImageView>().First();
                image.Source = ImageRef.FromPath(item.Item3, 320);
                var texts = ((StackPanel)body.Children[2]).Children.OfType<TextBlock>().ToList();
                texts[0].Text = item.Item2;
                texts[0].Tip(item.Item2);
                texts[1].Text = item.Item4;
                var fav = body.Children.OfType<IconView>().First();
                fav.Visibility = item.Item5 ? Visibility.Visible : Visibility.Collapsed;
            });
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
            UI.Text(UI.T("Home.Rail.Attention", "Needs your attention"), "section-title"),
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
            body = UI.Text(
                UI.T("Home.Rail.Activity.Empty", "No activity this month yet."),
                "caption",
                "textMuted",
                1);
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
            UI.Text(UI.T("Home.Rail.Activity", "Recently"), "section-title"),
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
            UI.Text(UI.T("Home.Rail.Imports", "Importing"), "section-title"),
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
