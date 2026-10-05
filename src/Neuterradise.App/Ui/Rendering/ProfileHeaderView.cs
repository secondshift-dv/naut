using System.Windows.Input;
using Neuterradise.App.Ui.Figure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Media;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;

namespace Neuterradise.App.Ui;

/// <summary>
/// Stable visual authority for the Profile header. Profile and Customization feed the same
/// layout, Cover, Banner and appearance state into this view so preview and committed rendering
/// cannot drift into separate implementations.
/// </summary>
public sealed record ProfileHeaderViewState(
    ProfileLayoutDefinition Layout,
    ImageRef? CoverSource,
    string? BannerVideoPath,
    ProfileAppearanceOverrides Appearance,
    FramePlan Frame,
    Func<FrameworkElement> IdentityFactory,
    bool ReducedMotion,
    PresentationContext? Context = null,
    FrameworkElement? FigureView = null,
    double FigureSize = 300);

public static class ProfileIdentityView
{
    public static FrameworkElement Create(
        string name, string? category, IReadOnlyList<string> tags, int? rating, bool isFavorite,
        ProfileLayoutIdentityAlignment alignment, Action<int>? onRating = null, ICommand? favoriteCommand = null,
        string? favoriteTooltip = null,
        FrameworkElement? actions = null, FrameworkElement? status = null)
    {
        var nameView = UI.Text(name, "hero", maxLines: 2);
        var taxonomy = new VariableWrap(6);
        if (!string.IsNullOrWhiteSpace(category))
        {
            taxonomy.Children.Add(new Border
            {
                Child = UI.Text(category, "micro", "textPrimary"),
                Padding = new Thickness(7, 2, 7, 2),
                CornerRadius = new CornerRadius(5),
                BorderBrush = ThemeRuntime.Current.Brush("borderDefault"),
                BorderThickness = new Thickness(1),
                Background = ThemeRuntime.Current.Brush("transparent"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        foreach (var tag in tags.Where(static value => !string.IsNullOrWhiteSpace(value)).Take(5))
        {
            taxonomy.Children.Add(UI.Badge(tag));
        }
        var stars = UI.H(3);
        for (var i = 1; i <= 5; i++)
        {
            if (onRating is null)
            {
                stars.Children.Add(new IconView(
                    "icon.profile.rating",
                    18,
                    (rating ?? 0) >= i ? "warning" : "textMuted"));
                continue;
            }

            var value = i;
            var star = UI.IconButton("icon.profile.rating", UI.F("Profile.RateN", "Rate {0} of 5", value), () => onRating(value), 28);
            ((IconView)star.Content).ColorToken = (rating ?? 0) >= value ? "warning" : "textMuted";
            stars.Children.Add(star);
        }

        FrameworkElement favorite;
        if (favoriteCommand is null)
        {
            favorite = new IconView("icon.profile.favorite", 20, isFavorite ? "danger" : "textMuted");
        }
        else
        {
            var favoriteButton = UI.IconButton("icon.profile.favorite", favoriteTooltip ?? UI.T("Card.Favorite", "Favorite"), null, 32, favoriteCommand);
            ((IconView)favoriteButton.Content).ColorToken = isFavorite ? "danger" : "textMuted";
            favorite = favoriteButton;
        }
        stars.VerticalAlignment = VerticalAlignment.Center;
        favorite.VerticalAlignment = VerticalAlignment.Center;
        var preferences = UI.H(10, stars, favorite);
        taxonomy.Visibility = taxonomy.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var content = UI.V(6, nameView, taxonomy, preferences, actions, status);
        if (alignment == ProfileLayoutIdentityAlignment.Center)
        {
            nameView.TextAlignment = TextAlignment.Center;
            taxonomy.HorizontalAlignment = HorizontalAlignment.Center;
            preferences.HorizontalAlignment = HorizontalAlignment.Center;
            if (actions is not null) actions.HorizontalAlignment = HorizontalAlignment.Center;
            content.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else if (alignment == ProfileLayoutIdentityAlignment.Right)
        {
            nameView.TextAlignment = TextAlignment.Right;
            taxonomy.HorizontalAlignment = HorizontalAlignment.Right;
            preferences.HorizontalAlignment = HorizontalAlignment.Right;
            if (actions is not null) actions.HorizontalAlignment = HorizontalAlignment.Right;
            content.HorizontalAlignment = HorizontalAlignment.Right;
        }
        return UI.Surface(content, Material.Deep, ThemeRuntime.Current.Tokens.Number("radiusSurface", 16), 12);
    }
}

public sealed class ProfileHeaderView : Grid, IDisposable
{
    private readonly Border _bannerFrame = new();
    private readonly Grid _bannerRegion = new();
    private readonly Border _bannerScrim = new() { IsHitTestVisible = false };
    private readonly Border _bannerFramingSurface = new()
    {
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
        IsHitTestVisible = false,
    };
    private readonly ContentControl _identityHost = new();
    private readonly ContentControl _figureRegion = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };
    private readonly SkVideoView _bannerVideo = new()
    {
        Visibility = Visibility.Collapsed,
    };
    private ProfileHeaderViewState? _state;
    private int _identityLayoutBucket = -1;
    private double _geometryWidth = -1;
    private bool _active = true;
    private bool _disposed;
    private CoverFrameView? _coverView;

    public event Action<FrameworkElement?>? CoverFramingSurfaceChanged;
    public FrameworkElement? CoverFramingSurface => _coverView;
    public FrameworkElement BannerFramingSurface => _bannerFramingSurface;

    public (double X, double Y) GetBannerPanDelta(double deltaX, double deltaY) =>
        _bannerVideo.FocalDeltaForDrag(deltaX, deltaY);

    public void UpdateFraming(ProfileAppearanceOverrides appearance)
    {
        if (_state is null) return;
        _state = _state with { Appearance = appearance };
        if (_coverView is not null) _coverView.Transform = MediaTransformState.FromCover(appearance);
        ApplyBannerTransform();
    }

    public ProfileHeaderView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _bannerRegion.Children.Add(_bannerVideo);
        _bannerRegion.Children.Add(_bannerScrim);
        _bannerRegion.Children.Add(_bannerFramingSurface);
        _bannerRegion.Background = ThemeRuntime.Current.Brush("transparent");

        _bannerFrame.Child = _bannerRegion;
        Grid.SetColumnSpan(_bannerFrame, 2);
        Children.Add(_bannerFrame);
        Grid.SetRow(_identityHost, 1);
        Grid.SetColumnSpan(_identityHost, 2);
        Children.Add(_identityHost);
        SizeChanged += (_, _) =>
        {
            var width = ActualWidth > 0 ? ActualWidth : 1200;
            // Height changes are an output of this geometry, never a new responsive input.
            if (Math.Abs(width - _geometryWidth) < 1) return;
            ApplyGeometry(IdentityLayoutBucket(width) != _identityLayoutBucket);
        };
    }
    public void Apply(ProfileHeaderViewState state, bool rebuildIdentity = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);

        var previousState = _state;
        var figureChanged = !ReferenceEquals(previousState?.FigureView, state.FigureView);
        var layoutChanged = previousState is null || previousState.Layout != state.Layout;
        if (figureChanged && previousState?.FigureView is ProfileFigureView previous)
        {
            previous.SetActive(false);
        }

        _state = state;
        if (layoutChanged || figureChanged)
        {
            _identityLayoutBucket = -1;
        }

        Visibility = Visibility.Visible;
        var width = ActualWidth > 0 ? ActualWidth : 1200;
        ApplyGeometry(rebuildIdentity
            || layoutChanged
            || figureChanged
            || IdentityLayoutBucket(width) != _identityLayoutBucket);
    }

    public void SetActive(bool active)
    {
        if (_disposed)
        {
            return;
        }

        var changed = _active != active;
        _active = active;
        _figureRegion.Visibility = active && _state?.FigureView is not null
            ? Visibility.Visible : Visibility.Collapsed;

        // Always forward the requested state. A retained Figure lease may have been temporarily
        // hidden by an XAML reparent without changing the header's logical active flag.
        if (_state?.FigureView is ProfileFigureView figure) figure.SetActive(active);
        if (changed) UpdateBannerState();
    }

    public void Clear()
    {
        if (_disposed)
        {
            return;
        }

        if (_state?.FigureView is ProfileFigureView figure) figure.SetActive(false);
        _state = null;
        _figureRegion.Content = null;
        _figureRegion.Visibility = Visibility.Collapsed;
        _identityHost.Content = null;
        _bannerFrame.Visibility = Visibility.Collapsed;
        ClearBanner();
    }

    private void ApplyGeometry(bool rebuildIdentity = false)
    {
        if (_state is not { } state)
        {
            return;
        }

        var layout = state.Layout;
        var theme = ThemeRuntime.Current;
        var width = ActualWidth > 0 ? ActualWidth : 1200;
        _geometryWidth = width;
        var pagePadding = UI.PagePadding(width);
        var height = layout.Height switch
        {
            ProfileLayoutHeight.Low => 180.0,
            ProfileLayoutHeight.Medium => 240.0,
            _ => 300.0,
        };
        if (width < 960)
        {
            height *= 0.8;
        }
        height = Math.Min(height, width < 960 ? 180.0 : width < 1200 ? 220.0 : 280.0);

        var showBanner = layout.BannerMode != ProfileLayoutBannerMode.None;
        var wideSplit = showBanner && layout.BannerMode == ProfileLayoutBannerMode.Split && width >= 960;
        var splitBannerOnLeft = layout.Id != "split-right";

        _bannerFrame.Visibility = showBanner ? Visibility.Visible : Visibility.Collapsed;
        _bannerFrame.Width = double.NaN;
        _bannerFrame.HorizontalAlignment = HorizontalAlignment.Stretch;
        _bannerFrame.VerticalAlignment = VerticalAlignment.Stretch;
        _identityHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _identityHost.VerticalContentAlignment = wideSplit ? VerticalAlignment.Center : VerticalAlignment.Stretch;

        if (wideSplit)
        {
            RowDefinitions[1].Height = new GridLength(0);
            Grid.SetRow(_bannerFrame, 0);
            Grid.SetColumn(_bannerFrame, splitBannerOnLeft ? 0 : 1);
            Grid.SetColumnSpan(_bannerFrame, 1);
            Grid.SetRow(_identityHost, 0);
            Grid.SetColumn(_identityHost, splitBannerOnLeft ? 1 : 0);
            Grid.SetColumnSpan(_identityHost, 1);

            _bannerFrame.Height = double.NaN;
            _bannerFrame.MinHeight = height;
            _identityHost.MinHeight = height;
            _bannerFrame.Margin = splitBannerOnLeft
                ? new Thickness(pagePadding, 12, 6, 8)
                : new Thickness(6, 12, pagePadding, 8);
            _bannerFrame.CornerRadius = new CornerRadius(theme.Tokens.Number("radiusHero", 20));
        }
        else
        {
            RowDefinitions[1].Height = GridLength.Auto;
            Grid.SetRow(_bannerFrame, 0);
            Grid.SetColumn(_bannerFrame, 0);
            Grid.SetColumnSpan(_bannerFrame, 2);
            Grid.SetRow(_identityHost, 1);
            Grid.SetColumn(_identityHost, 0);
            Grid.SetColumnSpan(_identityHost, 2);

            _bannerFrame.MinHeight = 0;
            _identityHost.MinHeight = 0;
            _bannerFrame.Height = showBanner ? height : 0;
            var contained = layout.BannerMode == ProfileLayoutBannerMode.Contained
                || (layout.BannerMode == ProfileLayoutBannerMode.Split && width < 960);
            _bannerFrame.Margin = contained
                ? new Thickness(pagePadding, 12, pagePadding, 0)
                : new Thickness(0);
            _bannerFrame.CornerRadius = new CornerRadius(
                contained ? theme.Tokens.Number("radiusHero", 20) : 0);
        }
        var scrimAlpha = layout.Overlay switch
        {
            ProfileLayoutOverlay.Soft => 0.45,
            ProfileLayoutOverlay.Medium => 0.7,
            _ => 0.9,
        };
        var scrim = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.5, layout.Fade == ProfileLayoutFade.Full ? 0 : 1),
            EndPoint = new Windows.Foundation.Point(0.5, 0.2),
        };
        var scrimColor = theme.Color("heroScrimStrong");
        scrim.GradientStops.Add(new GradientStop
        {
            Color = Windows.UI.Color.FromArgb(
                (byte)Math.Clamp(scrimColor.A * scrimAlpha, 0, 255),
                scrimColor.R,
                scrimColor.G,
                scrimColor.B),
            Offset = 0,
        });
        scrim.GradientStops.Add(new GradientStop
        {
            Color = Windows.UI.Color.FromArgb(0, scrimColor.R, scrimColor.G, scrimColor.B),
            Offset = 1,
        });
        _bannerScrim.Background = scrim;
        _bannerScrim.Visibility = layout.Fade == ProfileLayoutFade.None
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (rebuildIdentity)
        {
            UpdateIdentity(width);
        }
        ApplyBannerTransform();
        UpdateBannerState();
    }

    private void UpdateIdentity(double width)
    {
        if (_state is not { } state)
        {
            return;
        }

        var layout = state.Layout;
        // Reflow the same viewport rather than multiplying native hosts during resize.
        if (_figureRegion.Parent is Panel oldParent) oldParent.Children.Remove(_figureRegion);
        _figureRegion.Content = state.FigureView;
        if (state.FigureView is ProfileFigureView figure) figure.SetActive(_active);
        _figureRegion.Visibility = _active && state.FigureView is not null
            ? Visibility.Visible : Visibility.Collapsed;
        var coverSize = layout.CoverSize switch
        {
            ProfileLayoutCoverSize.Small => 80.0,
            ProfileLayoutCoverSize.Medium => 104.0,
            ProfileLayoutCoverSize.Large => 132.0,
            _ => 160.0,
        };
        if (width < 960)
        {
            coverSize *= 0.8;
        }

        var coverVisual = layout.CoverPlacement == ProfileLayoutCoverPlacement.None
            ? null
            : new CoverFrameView
            {
                Source = state.CoverSource,
                Transform = MediaTransformState.FromCover(state.Appearance),
                Plan = state.Frame,
                Appearance = CardDataFactory.AppearanceFor(
                    state.Appearance,
                    state.Frame,
                    state.ReducedMotion) with
                {
                    DetailLevel = layout.CoverDetailLevel,
                },
                FrameMode = layout.CoverDetailLevel == Design.CoverFrames.CoverFrameDetailLevel.Hidden
                    ? "none"
                    : layout.CoverDetailLevel == Design.CoverFrames.CoverFrameDetailLevel.Compact ? "lite" : "full",
                Width = coverSize,
                Height = coverSize,
            };
        _coverView = coverVisual;
        CoverFramingSurfaceChanged?.Invoke(coverVisual);
        FrameworkElement? cover = coverVisual;
        var identity = state.IdentityFactory()
            ?? throw new InvalidOperationException("Profile identity factory returned no visual.");
        var overlap = layout.CoverPlacement is
            ProfileLayoutCoverPlacement.BottomLeftOverlap or ProfileLayoutCoverPlacement.BottomCenterOverlap;
        var center = layout.CoverPlacement is
                ProfileLayoutCoverPlacement.BottomCenterOverlap or ProfileLayoutCoverPlacement.InlineCenter
            || layout.IdentityAlignment == ProfileLayoutIdentityAlignment.Center;
        var coverRight = layout.CoverPlacement == ProfileLayoutCoverPlacement.Right;

        FrameworkElement band;
        if (cover is null)
        {
            band = identity;
        }
        else if (center || width < 520)
        {
            band = UI.V(8, cover.Align(HorizontalAlignment.Center), identity);
            identity.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            var grid = UI.Grid("auto", coverRight ? "*,auto" : "auto,*");
            grid.Children.Add(cover.Margin(coverRight ? 12 : 0, 0, coverRight ? 0 : 12, 0)
                .At(0, coverRight ? 1 : 0));
            grid.Children.Add(identity.Align(vertical: VerticalAlignment.Bottom)
                .At(0, coverRight ? 0 : 1));
            band = grid;
        }

        var wideSplit = layout.BannerMode == ProfileLayoutBannerMode.Split && width >= 960;
        var identityWidth = wideSplit ? width * 0.5 : width;
        if (state.FigureView is not null)
        {
            var figureWidth = Math.Min(Math.Max(1, identityWidth - 2 * UI.PagePadding(identityWidth)),
                Math.Clamp(identityWidth * 0.3, 240, Math.Clamp(state.FigureSize, 240, 300)));
            _figureRegion.Height = figureWidth + 72;
            // A native child has no intrinsic XAML size. Auto width lets its retained scene or
            // Thumbnail become the next measure input, feeding header reflow back into itself.
            _figureRegion.Width = figureWidth;
            _figureRegion.MaxWidth = 360;
            _figureRegion.HorizontalAlignment = HorizontalAlignment.Center;
            // Overlap layouts intentionally lift the Cover/identity band into the Banner.
            // When Figure shares that desktop row, compensate only its viewport so the native
            // child starts below the Banner instead of inheriting the band's negative top margin.
            _figureRegion.Margin = overlap && !wideSplit && identityWidth >= 760
                ? new Thickness(0, coverSize * 0.45 + 10, 0, 0)
                : new Thickness(0);
            if (identityWidth >= 760)
            {
                var composition = UI.Grid("auto", "*,auto");
                composition.ColumnSpacing = 16;
                composition.Children.Add(band.At(0, 0));
                composition.Children.Add(_figureRegion.At(0, 1));
                band = composition;
            }
            else
            {
                band = UI.V(8, band, _figureRegion);
            }
        }
        var padding = UI.PagePadding(identityWidth);
        if (wideSplit)
        {
            var bannerOnLeft = layout.Id != "split-right";
            band.Margin = bannerOnLeft
                ? new Thickness(12, 16, 24, 16)
                : new Thickness(24, 16, 12, 16);
            band.MaxWidth = 720;
            band.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            band.Margin = new Thickness(padding, overlap ? -coverSize * 0.45 : 10, padding, 8);
            band.MaxWidth = layout.ContentWidth == ProfileLayoutContentWidth.Wide ? 1440 : 1000;
            band.HorizontalAlignment = HorizontalAlignment.Center;
        }
        _identityHost.Content = band;
        _identityLayoutBucket = IdentityLayoutBucket(width);
    }

    private static int IdentityLayoutBucket(double width) =>
        width < 520 ? 0
        : width < 640 ? 1
        : width < 760 ? 2
        : width < 960 ? 3
        : width < 1200 ? 4
        : width < 1280 ? 5
        : width < 1520 ? 6
        : 7;

    private void UpdateBannerState()
    {
        if (_state is not { } state || string.IsNullOrWhiteSpace(state.BannerVideoPath))
        {
            ClearBanner();
            return;
        }

        if (!_active || state.Layout.BannerMode == ProfileLayoutBannerMode.None)
        {
            PauseBanner();
            return;
        }

        PlayBanner(state.BannerVideoPath);
    }

    private void PlayBanner(string path)
    {
        _bannerVideo.Visibility = Visibility.Visible;
        _bannerVideo.Play(path, loop: true);
    }

    private void PauseBanner()
    {
        _bannerVideo.SetActive(false);
        _bannerVideo.Visibility = Visibility.Collapsed;
    }

    private void ClearBanner()
    {
        _bannerVideo.Stop();
        _bannerVideo.Visibility = Visibility.Collapsed;
    }

    private void ApplyBannerTransform()
    {
        if (_state is not { } state)
        {
            return;
        }

        _bannerVideo.SetPresentation(MediaTransformState.FromBanner(state.Appearance));
    }
    public new void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _active = false;
        if (_state?.FigureView is ProfileFigureView figure) figure.SetActive(false);
        _state = null;
        _figureRegion.Content = null;
        _identityHost.Content = null;
        _bannerVideo.Dispose();
    }
}
