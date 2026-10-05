using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Presentation;

namespace Neuterradise.App.Ui;

internal static class ThemePreviewView
{
    public static FrameworkElement Create(ThemePlan plan, double height)
    {
        var tokens = TokenAuthority.Resolve(plan.Theme, plan.Material, reducedMotion: true);
        SolidColorBrush Brush(string name, double opacity = 1)
        {
            var color = tokens.Color(name);
            return new SolidColorBrush(Windows.UI.Color.FromArgb((byte)Math.Round(color.A * opacity), color.R, color.G, color.B));
        }
        TextBlock Text(string value, string color, double size) => new()
        {
            Text = value, Foreground = Brush(color), FontSize = size,
            FontFamily = ThemeRuntime.Current.Font("body"), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var nav = new Border
        {
            Child = Text(UI.T("Navigation.Home", "Home"), "textPrimary", 11),
            Background = Brush("navSelectedBackground"), BorderBrush = Brush("navIndicator"),
            CornerRadius = new CornerRadius(tokens.Number("navCornerRadius", 5)),
            Padding = new Thickness(5, 2, 5, 2),
            BorderThickness = plan.Theme.EffectiveMorphology.NavigationTreatment switch
            {
                ThemeNavigationTreatment.AccentEdge => new Thickness(tokens.Number("navAccentEdgeWidth", 2), 0, 0, 0),
                ThemeNavigationTreatment.StrongBorder => new Thickness(tokens.Number("navSelectedBorderThickness", 1.5)),
                ThemeNavigationTreatment.Underline => new Thickness(0, 0, 0, tokens.Number("navIndicatorHeight", 2)),
                _ => new Thickness(0),
            },
        };
        var copy = new StackPanel { Spacing = 2 };
        copy.Children.Add(Text("naut", "textPrimary", 13));
        copy.Children.Add(Text(UI.T("Common.Open", "Open"), "textSecondary", 10));
        var card = new Border
        {
            Child = copy, Background = Brush("surface1", tokens.Number("cardSurfaceOpacity", 1)), BorderBrush = Brush("borderSubtle"),
            BorderThickness = new Thickness(tokens.Number("cardBorderThickness", 1)),
            CornerRadius = new CornerRadius(tokens.Number("radiusCard", 12)),
            Padding = new Thickness(7, 4, 7, 4),
        };
        var focus = new Border
        {
            Child = Text("Aa", "textOnAccent", 10), Background = Brush("accent"),
            BorderBrush = Brush("focus"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(tokens.Number("radiusSmall", 4)), Padding = new Thickness(4),
        };
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(nav);
        var row = new Grid { ColumnSpacing = 5 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(card); Grid.SetColumn(focus, 1); row.Children.Add(focus);
        content.Children.Add(new Border
        {
            Child = row, Padding = new Thickness(2), Background = Brush("glassFill"),
            BorderBrush = Brush("glassBorder"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(tokens.Number("radiusSmall", 4)),
        });
        return new Border
        {
            Child = content, Height = height, Padding = new Thickness(6),
            Background = Brush("canvas"), CornerRadius = new CornerRadius(tokens.Number("radiusSurface", 16)),
            IsHitTestVisible = false,
        };
    }
}
