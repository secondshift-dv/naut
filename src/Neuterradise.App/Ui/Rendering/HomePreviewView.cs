using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Media;
using Neuterradise.App.Presentation;

namespace Neuterradise.App.Ui;

/// <summary>
/// Read-only Home presentation preview. It consumes the same compiled Home plans and real Profile
/// presentation data as the live surface; it owns no presentation state and writes nothing.
/// </summary>
public sealed class HomePreviewView : Grid
{
    private readonly bool _compact;
    private readonly CardData? _data;
    private readonly HomeLayoutPlan _layout;
    private readonly SpotlightPlan _spotlight;

    public HomePreviewView(
        HomeLayoutPlan layout,
        BackdropPlan backdropPlan,
        SpotlightPlan spotlight,
        CardData? data,
        bool compact)
    {
        _layout = layout;
        _spotlight = spotlight;
        _data = data;
        _compact = compact;
        Background = ThemeRuntime.Current.Brush("canvas");
        IsHitTestVisible = false;
        MinHeight = compact ? 118 : 340;

        var backdrop = new BackdropView
        {
            Plan = backdropPlan,
            IsSurfaceActive = !compact,
            AmbientImagePath = data?.Banner?.Path ?? data?.Cover?.Path,
        };
        Children.Add(backdrop);

        if (layout.Surface is { } surface)
        {
            FrameworkElement? Region(string id) => id == "featured" ? BuildHero() : BuildRail(
                HomeRailPolicy.Resolve(layout).FirstOrDefault(r => r.Id == id) ?? new HomeRailPlan(id, true, "compact", 3));
            var content = SurfaceComposer.Build(surface, Region, 1000);
            content.Width = 1000;
            Children.Add(new Viewbox { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, Child = content });
            return;
        }

        var composition = new Grid { MinHeight = MinHeight };
        var heroShare = Math.Clamp(layout.HeroFraction, 0.32, 0.52);
        composition.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(heroShare, GridUnitType.Star),
        });
        composition.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1 - heroShare, GridUnitType.Star),
        });
        Children.Add(composition);

        composition.Children.Add(BuildHero().At(0, 0));
        composition.Children.Add(BuildRails().At(1, 0));
    }
    private FrameworkElement BuildHero()
    {
        var theme = ThemeRuntime.Current;
        var geometry = _spotlight.GeometryFor(_compact ? 720 : 1000, _layout.HeroPlacement);
        var split = geometry is SpotlightGeometry.ArtworkLeft or SpotlightGeometry.IdentityLeft;
        var stacked = geometry is SpotlightGeometry.ArtworkAbove or SpotlightGeometry.IdentityAbove;
        var separated = split || stacked;
        var image = new SkImageView
        {
            Source = _data?.Banner ?? _data?.Cover,
            Transform = _data?.Banner is not null
                ? _data.BannerTransform
                : _data?.CoverTransform ?? MediaTransformState.Default,
            PlaceholderToken = "surface2",
            Height = _compact ? 138 : 260,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false,
        };
        var identity = _data is null ? EmptyHero() : BuildIdentity(_data, !separated);
        var footer = UI.V(_compact ? 3 : 8, identity);
        footer.VerticalAlignment = separated ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        if (_spotlight.Pagination != "none")
        {
            footer.Children.Add(UI.Text(
                "?  01 / 02  ?", "micro", separated ? "textSecondary" : "onHeroSecondary")
                .Align(HorizontalAlignment.Right));
        }
        var footerPanel = new Border
        {
            Child = footer,
            Background = theme.Brush(separated ? "surface2" : "transparent"),
            BorderBrush = theme.Brush(separated ? "borderSubtle" : "transparent"),
            BorderThickness = new Thickness(separated ? 1 : 0),
            Padding = new Thickness(_compact ? 8 : 18),
            VerticalAlignment = separated ? VerticalAlignment.Stretch : VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        FrameworkElement composition;
        if (split)
        {
            var identityFirst = geometry == SpotlightGeometry.IdentityLeft;
            var identityShare = _spotlight.IdentityShare;
            var first = identityFirst ? identityShare : 1 - identityShare;
            var splitGrid = new Grid();
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(first, GridUnitType.Star) });
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1 - first, GridUnitType.Star) });
            splitGrid.Children.Add(image.At(0, identityFirst ? 1 : 0));
            splitGrid.Children.Add(footerPanel.At(0, identityFirst ? 0 : 1));
            composition = splitGrid;
        }
        else if (stacked)
        {
            var identityFirst = geometry == SpotlightGeometry.IdentityAbove;
            var stackGrid = new Grid();
            stackGrid.RowDefinitions.Add(new RowDefinition
                { Height = identityFirst ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            stackGrid.RowDefinitions.Add(new RowDefinition
                { Height = identityFirst ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            stackGrid.Children.Add(image.At(identityFirst ? 1 : 0, 0));
            stackGrid.Children.Add(footerPanel.At(identityFirst ? 0 : 1, 0));
            composition = stackGrid;
        }
        else
        {
            var layer = new Grid();
            layer.Children.Add(image);
            var strength = Math.Clamp(_spotlight.ScrimStrength, 0, 1);
            var bottomShade = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.5, 1),
                EndPoint = new Windows.Foundation.Point(0.5, 0),
            };
            var shade = theme.Color("heroScrimStrong");
            bottomShade.GradientStops.Add(new GradientStop
            {
                Color = Windows.UI.Color.FromArgb(
                    (byte)Math.Clamp(shade.A * strength * 0.82, 0, 255),
                    shade.R, shade.G, shade.B),
                Offset = 0,
            });
            bottomShade.GradientStops.Add(new GradientStop
            {
                Color = Windows.UI.Color.FromArgb(0, shade.R, shade.G, shade.B),
                Offset = 0.58,
            });
            bottomShade.GradientStops.Add(new GradientStop
            {
                Color = Windows.UI.Color.FromArgb(0, shade.R, shade.G, shade.B),
                Offset = 1,
            });
            layer.Children.Add(new Border
            {
                Background = bottomShade,
                IsHitTestVisible = false,
            });
            layer.Children.Add(footerPanel);
            composition = layer;
        }
        return new Border
        {
            Child = composition,
            CornerRadius = new CornerRadius(
                _layout.HeroPlacement == "inset" ? (_compact ? 6 : 12) : 0),
            Margin = _layout.HeroPlacement == "inset"
                ? new Thickness(_compact ? 5 : 12, _compact ? 4 : 10,
                    _compact ? 5 : 12, 0)
                : new Thickness(0),
            BorderBrush = _layout.HeroPlacement == "inset"
                ? theme.Brush("borderSubtle")
                : theme.Brush("transparent"),
            BorderThickness = new Thickness(_layout.HeroPlacement == "inset" ? 1 : 0),
        };
    }

    private FrameworkElement EmptyHero()
    {
        return UI.Text(
                UI.T("Customize.NoSample", "Add a Profile to see a live preview here."),
                _compact ? "micro" : "body-muted", "textSecondary", 2)
            .Align(HorizontalAlignment.Left, VerticalAlignment.Center);
    }

    private FrameworkElement BuildIdentity(CardData data, bool onArtwork)
    {
        var name = UI.Text(data.Name, _compact ? "micro" : _spotlight.Composition == "editorial" ? "section-title" : "body-strong",
            onArtwork ? "onHeroPrimary" : "textPrimary", 2);
        var meta = UI.Text(
            string.Join(" ? ", new[] { data.Category, data.TextFor(SemanticSlots.ProfileMediaCount) }
                .Where(static value => !string.IsNullOrWhiteSpace(value))),
            "micro", onArtwork ? "onHeroSecondary" : "textSecondary", 1);
        var overview = _spotlight.ShowOverview
            && !string.IsNullOrWhiteSpace(data.Overview) && !_compact
                ? UI.Text(data.Overview, "caption",
                    onArtwork ? "onHeroSecondary" : "textSecondary", 2)
                : null;
        var details = _spotlight.Composition switch
        {
            "cinema" => UI.V(4, name),
            "editorial" => UI.V(_compact ? 4 : 12, meta, name, overview),
            _ => UI.V(4, name, TagCapsules.Create(data.AssignedTags, _compact ? 2 : 4), meta, overview),
        };
        if (!_spotlight.ShowCover || data.Cover is null
            || _spotlight.Composition is "cinema" or "editorial")
            return details;

        var identity = UI.Grid("auto", "auto,*",
            new CoverFrameView
            {
                Source = data.Cover,
                Transform = data.CoverTransform,
                Appearance = data.Appearance, Plan = data.Frame,
                Width = _compact ? 30 : 80,
                Height = _compact ? 30 : 80,
                IsHitTestVisible = false,
            }.At(0, 0),
            details.At(0, 1));
        identity.ColumnSpacing = _compact ? 5 : 10;
        return identity;
    }

    private FrameworkElement BuildRails()
    {
        var rails = UI.V(_compact ? 2 : 5);
        foreach (var rail in HomeRailPolicy.Resolve(_layout)
                     .Take(_compact ? 3 : 6))
        {
            rails.Children.Add(BuildRail(rail));
        }

        if (rails.Children.Count == 0)
        {
            rails.Children.Add(HomeReadability.Heading(
                UI.T("Customize.HomePreview.NoRails", "No Home rails in this layout."),
                "micro", _compact ? 4 : 8));
        }

        rails.Margin = _compact
            ? new Thickness(6, 3, 6, 4)
            : new Thickness(14, 8, 14, 10);
        return rails;
    }

    private FrameworkElement BuildRail(HomeRailPlan rail)
    {
        var title = rail.Id switch
        {
            "recent" => UI.T("Home.Rail.Recent", "Continue"),
            "favorites" => UI.T("Home.Rail.Favorites", "Favorites"),
            "profiles" => UI.T("Home.Rail.Profiles", "Profiles"),
            "activity" => UI.T("Home.Rail.Activity", "Recently"),
            "statistics" => UI.T("Home.Rail.Vault", "Vault"),
            "attention" => UI.T("Home.Rail.Attention", "Needs your attention"),
            "imports" => UI.T("Home.Rail.Imports", "Importing"),
            "discovery" => UI.T("Home.Connection.Featured", "Connected"),
            _ => rail.Id.Replace('-', ' '),
        };

        var row = UI.H(_compact ? 3 : 6);
        var count = Math.Clamp(rail.MaxItems, 1, _compact ? 3 : 4);
        for (var index = 0; index < count; index++)
        {
            row.Children.Add(
                _data is not null
                && (rail.Id is "recent" or "favorites" or "profiles")
                    ? BuildProfileTile(rail, _data)
                    : BuildUtilityTile(rail, index));
        }

        return UI.V(
            _compact ? 1 : 3,
            HomeReadability.Heading(title, "micro", _compact ? 4 : 8),
            row);
    }
    private FrameworkElement BuildProfileTile(HomeRailPlan rail, CardData data)
    {
        var width = rail.Style is "strip" or "compact" ? 156d : 98d;
        var height = rail.Style is "strip" or "compact" ? 72d : 138d;
        var tile = new HomeProfileTile(rail.Style, width, height);
        tile.Bind(data.ProfileId, data.Name, data.Cover?.Path, data.TextFor(SemanticSlots.ProfileMediaCount), data.IsFavorite);
        if (data.Frame is { } frame) tile.Apply(frame, data.Appearance, data.CoverTransform);
        if (!_compact) return tile;
        return new Viewbox { Width = rail.Style is "strip" or "compact" ? 58 : 32, Child = tile };
    }

    private FrameworkElement BuildUtilityTile(HomeRailPlan rail, int index)
    {
        var value = rail.Id == "statistics"
            ? (index + 1).ToString()
            : string.Empty;
        var content = string.IsNullOrEmpty(value)
            ? (FrameworkElement)new IconView(
                rail.Id == "activity" ? "icon.navigation.history"
                : rail.Id == "attention" ? "icon.status.warning"
                : rail.Id == "imports" ? "icon.navigation.import"
                : rail.Id == "discovery" ? "icon.profile.related"
                : "icon.profile.person",
                _compact ? 9 : 13)
            : HomeReadability.Text(value, rail.Style == "poster" && !_compact ? "control" : "micro");

        var tile = rail.Id == "statistics"
            ? HomeReadability.Region(content, _compact ? 3 : rail.Style == "poster" ? 10 : 6)
            : UI.Surface(
                content,
                rail.Style == "poster" ? Material.Glass : Material.Raised,
                _compact ? 4 : rail.Style == "poster" ? 10 : 7,
                _compact ? 3 : rail.Style == "poster" ? 10 : 6);

        (tile.Width, tile.Height) = rail.Style switch
        {
            "poster" => _compact ? (38, 28) : (72, 54),
            "tile" => _compact ? (31, 23) : (54, 38),
            "compact" => _compact ? (24, 18) : (42, 28),
            _ => _compact ? (27, 20) : (46, 30),
        };
        return tile;
    }
}
