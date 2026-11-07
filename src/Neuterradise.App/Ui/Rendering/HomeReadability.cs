using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Neuterradise.App.Ui;

/// <summary>
/// Home labels outside Spotlight: bold neutral glyphs with a one-pixel contrasting text
/// outline, not a painted plate. Works on both light and dark user-selected Backdrops.
/// </summary>
public static class HomeReadability
{
    private static readonly Brush Ink = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 249, 251));
    private static readonly Brush Outline = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 31, 35, 42));
    private static readonly (double X, double Y)[] OutlineOffsets =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0),             (1, 0),
        (-1, 1),  (0, 1),   (1, 1),
    ];

    public static FrameworkElement Text(string? content, string role = "body", int maxLines = 0)
    {
        TextBlock Layer(bool outlined)
        {
            var block = UI.Text(content, role, maxLines: maxLines);
            if (outlined)
            {
                block.IsHitTestVisible = false;
                block.IsTextSelectionEnabled = false;
                AutomationProperties.SetAccessibilityView(block, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            }

            ThemeRuntime.Current.TrackThemeElement(block, text =>
            {
                text.Foreground = outlined ? Outline : Ink;
                text.FontWeight = new Windows.UI.Text.FontWeight(700);
            });
            return block;
        }

        var lettering = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (x, y) in OutlineOffsets)
        {
            var shadow = Layer(outlined: true);
            shadow.RenderTransform = new TranslateTransform { X = x, Y = y };
            lettering.Children.Add(shadow);
        }
        lettering.Children.Add(Layer(outlined: false));
        return lettering;
    }

    public static Border Region(UIElement content, double padding = 10) => new()
    {
        Child = content,
        Padding = new Thickness(padding),
        BorderThickness = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    public static Border Heading(string title, string role = "section-title", double padding = 8) =>
        Region(Text(title, role), padding);
}