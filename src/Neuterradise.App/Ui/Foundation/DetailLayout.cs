using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Neuterradise.App.Ui;

internal static class DetailLayout
{
    public static FrameworkElement Heading(string title, string? description, string icon)
    {
        var theme = ThemeRuntime.Current;
        var glyph = new Border
        {
            Child = new IconView(icon, 18, "textAccent"),
            Width = 32,
            Height = 32,
            Background = theme.Brush("surface2"),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var text = UI.V(2, UI.Text(title, "body-strong"),
            string.IsNullOrWhiteSpace(description) ? null : UI.WrappedText(description, "caption", "textSecondary"));
        text.VerticalAlignment = string.IsNullOrWhiteSpace(description)
            ? VerticalAlignment.Center
            : VerticalAlignment.Top;
        var heading = UI.Grid("auto", "auto,*", glyph.At(0, 0), text.At(0, 1));
        heading.ColumnSpacing = 10;
        return heading;
    }

    public static Border Section(string title, string? description, string icon, params UIElement?[] content)
    {
        var children = new UIElement?[] { Heading(title, description, icon) }.Concat(content).ToArray();
        var section = UI.Surface(UI.V(10, children), Material.Grounded, 12, 12);
        section.BorderBrush = ThemeRuntime.Current.Brush("borderSubtle");
        return section;
    }

    public static FrameworkElement Property(string label, string? value)
    {
        var displayValue = value ?? "—";
        var valueText = UI.WrappedText(displayValue, "body");
        valueText.IsTextSelectionEnabled = true;
        valueText.Tip(displayValue);

        return new Border
        {
            Child = UI.V(2, UI.Text(label, "micro", "textMuted"), valueText),
            Padding = new Thickness(0, 6, 0, 8),
            BorderBrush = ThemeRuntime.Current.Brush("borderSubtle"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    public static Button Item(string title, string detail, string icon, Action onClick)
    {
        var theme = ThemeRuntime.Current;
        var button = UI.Button(title, onClick, ButtonKind.Ghost);
        var content = UI.Grid("auto", "auto,*,auto",
            new IconView(icon, 20, "textAccent").Align(vertical: VerticalAlignment.Center).At(0, 0),
            UI.V(2, UI.Text(title, "body-strong", maxLines: 1), UI.Text(detail, "caption", "textSecondary", 1)).At(0, 1),
            new IconView("icon.action.chevron", 14, "textMuted").Align(vertical: VerticalAlignment.Center).At(0, 2));
        content.ColumnSpacing = 10;
        button.Content = content;
        button.Width = 240;
        button.Padding = new Thickness(10);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Background = theme.Brush("surface2");
        button.BorderBrush = theme.Brush("borderSubtle");
        return button;
    }

    public static void Select(Button button, bool selected)
    {
        var theme = ThemeRuntime.Current;
        button.Background = theme.Brush(selected ? "surfaceSelected" : "transparent");
        button.BorderBrush = theme.Brush(selected ? "borderInteractive" : "transparent");
        button.Foreground = theme.Brush(selected ? "textPrimary" : "textSecondary");
        button.BorderThickness = new Thickness(1);
    }
}
