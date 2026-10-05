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
        var hero = new Grid();
        var art = new SkImageView
        {
            Source = _data?.Banner ?? _data?.Cover,
            Transform = _data?.Banner is not null
                ? _data.BannerTransform
                : _data?.CoverTransform ?? MediaTransformState.Default,
            PlaceholderToken = "surface2",
            IsHitTestVisible = false,
        };
        if (_spotlight.Composition != "snapshot") hero.Children.Add(art);
        var scrim = new Microsoft.UI.Xaml.Media.LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 1),
            EndPoint = new Windows.Foundation.Point(0.7, 0),
        };
        scrim.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = theme.Color("heroScrimStrong"), Offset = 0 });
        scrim.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = Windows.UI.Color.FromArgb(0, 0, 0, 0), Offset = 1 });
        hero.Children.Add(new Border { Background = scrim, IsHitTestVisible = false });

        hero.Children.Add(_data is null
            ? EmptyHero()
            : BuildIdentity(_data));

        if (_spotlight.Pagination != "none")
        {
            hero.Children.Add(UI.Text(
                    _compact ? "‹  01 / 02  ›" : "‹   01 / 02   ›",
                    "micro",
                    "onHeroSecondary")
                .Margin(_compact ? 5 : 12)
                .Align(HorizontalAlignment.Right, VerticalAlignment.Bottom));
        }

        return new Border
        {
            Child = hero,
            CornerRadius = new CornerRadius(
                _layout.HeroPlacement == "inset" ? (_compact ? 6 : 12) : 0),
            Margin = _layout.HeroPlacement == "inset"
                ? new Thickness(
                    _compact ? 5 : 12,
                    _compact ? 4 : 10,
                    _compact ? 5 : 12,
                    0)
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
                _compact ? "micro" : "body-muted",
                _compact ? "onHeroSecondary" : "textSecondary",
                2)
            .Align(HorizontalAlignment.Center, VerticalAlignment.Center);
    }

    private FrameworkElement BuildIdentity(CardData data)
    {
        var textOverArt = _layout.HeroPlacement != "split" && _spotlight.Composition is "editorial" or "centered-poster";
        var primary = textOverArt ? "onHeroPrimary" : "textPrimary";
        var secondary = textOverArt ? "onHeroSecondary" : "textSecondary";
        var name = UI.Text(data.Name, _compact ? "micro" : "body-strong", primary, 1);
        var meta = UI.Text(
            string.Join(" · ", new[] { data.Category, data.TextFor(SemanticSlots.ProfileMediaCount) }
                .Where(static value => !string.IsNullOrWhiteSpace(value))),
            "micro",
            secondary,
            1);
        var overview = _spotlight.ShowOverview
            && !string.IsNullOrWhiteSpace(data.Overview)
            && !_compact
                ? UI.Text(data.Overview, "caption", secondary, 2)
                : null;
        FrameworkElement? cover = null;
        if (_spotlight.ShowCover && data.Cover is not null)
        {
            cover = new SkImageView
            {
                Source = data.Cover,
                Transform = data.CoverTransform,
                Width = _compact ? 20 : 42,
                Height = _compact ? 20 : 42,
                CornerRadiusValue = _compact ? 6 : 10,
                IsHitTestVisible = false,
            };
        }

        var content = UI.Grid("auto", cover is null ? "*" : "auto,*");
        content.ColumnSpacing = _compact ? 4 : 8;
        if (cover is not null)
        {
            content.Children.Add(cover.At(0, 0));
        }
        content.Children.Add(UI.V(_compact ? 1 : 3, name, meta, overview).At(0, cover is null ? 0 : 1));

        return (_layout.HeroPlacement == "split" ? "split-glass" : _spotlight.Composition) switch
        {
            "centered-poster" => CenteredIdentity(cover, name, meta),
            "split-glass" or "storefront" => SplitIdentity(content),
            "editorial" or "cinema" => EditorialIdentity(content),
            "snapshot" => UI.Surface(content, Material.Raised, 10, _compact ? 6 : 16),
            _ => CinematicIdentity(content),
        };
    }
    private FrameworkElement CenteredIdentity(
        FrameworkElement? cover,
        FrameworkElement name,
        FrameworkElement meta)
    {
        var content = UI.V(
            _compact ? 2 : 5,
            cover?.Align(HorizontalAlignment.Center),
            name.Align(HorizontalAlignment.Center),
            _compact ? null : meta.Align(HorizontalAlignment.Center));
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        return content;
    }

    private FrameworkElement SplitIdentity(FrameworkElement content)
    {
        var panel = UI.Surface(
            content,
            Material.Glass,
            _compact ? 6 : 10,
            _compact ? 5 : 10);
        panel.MaxWidth = _compact ? 100 : 240;
        panel.HorizontalAlignment = HorizontalAlignment.Right;
        panel.VerticalAlignment = VerticalAlignment.Center;
        panel.Margin = new Thickness(0, 0, _compact ? 5 : 12, 0);
        return panel;
    }
    private FrameworkElement EditorialIdentity(FrameworkElement content)
    {
        content.HorizontalAlignment = HorizontalAlignment.Left;
        content.VerticalAlignment = VerticalAlignment.Bottom;
        content.Margin = new Thickness(
            _compact ? 6 : 16,
            0,
            _compact ? 6 : 16,
            _compact ? 12 : 24);
        return content;
    }

    private FrameworkElement CinematicIdentity(FrameworkElement content)
    {
        var panel = UI.Surface(
            content,
            Material.Deep,
            _compact ? 6 : 10,
            _compact ? 5 : 10);
        panel.MaxWidth = _compact ? 140 : 280;
        panel.HorizontalAlignment = HorizontalAlignment.Left;
        panel.VerticalAlignment = VerticalAlignment.Bottom;
        panel.Margin = new Thickness(
            _compact ? 6 : 16,
            0,
            _compact ? 6 : 16,
            _compact ? 12 : 24);
        return panel;
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
            rails.Children.Add(UI.Text(
                UI.T("Customize.HomePreview.NoRails", "No Home rails in this layout."),
                "micro",
                "textMuted"));
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
            UI.Text(title, "micro", "textSecondary", 1),
            row);
    }
    private FrameworkElement BuildProfileTile(HomeRailPlan rail, CardData data)
    {
        var width = _compact ? 28 : rail.Style == "poster" ? 54 : 68;
        var height = rail.Style switch
        {
            "poster" => width * 1.25,
            "strip" => _compact ? 20 : 34,
            "compact" => _compact ? 22 : 38,
            _ => _compact ? 24 : 44,
        };
        var tile = new Grid { Width = width, Height = height };
        tile.Children.Add(new SkImageView
        {
            Source = data.Cover ?? data.Banner,
            Transform = data.Cover is not null
                ? data.CoverTransform
                : data.BannerTransform,
            PlaceholderToken = "surface2",
            CornerRadiusValue = _compact ? 4 : 7,
            IsHitTestVisible = false,
        });
        if (_compact)
        {
            return tile;
        }

        tile.Children.Add(new Border
        {
            Background = ThemeRuntime.Current.Brush("scrim"),
            Opacity = 0.2,
            IsHitTestVisible = false,
        });
        tile.Children.Add(UI.Text(
                data.Name,
                "micro",
                "onHeroPrimary",
                1)
            .Margin(5)
            .Align(HorizontalAlignment.Left, VerticalAlignment.Bottom));
        return tile;
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
            : UI.Text(value, rail.Style == "poster" && !_compact ? "control" : "micro");

        var tile = UI.Surface(
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
