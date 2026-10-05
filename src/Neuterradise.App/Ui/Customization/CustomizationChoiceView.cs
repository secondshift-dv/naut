using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Design.Themes;

namespace Neuterradise.App.Ui;

internal static class CustomizationChoiceView
{
    public static Button Create(
        string name,
        FrameworkElement preview,
        bool selected,
        bool isDefault,
        bool isUser,
        Action select,
        string? tooltip = null,
        bool squarePreview = false)
    {
        var theme = ThemeRuntime.Current;
        var thumbnail = new Border
        {
            Width = 64,
            Height = squarePreview ? 64 : 48,
            CornerRadius = new CornerRadius(6),
            Background = theme.Brush("canvas"),
            IsHitTestVisible = false,
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Child = preview,
            },
        };
        var markers = UI.Wrap(
            4,
            selected ? UI.Badge(UI.T("Common.Selected", "Selected"), "accent") : null,
            isDefault ? UI.Badge(UI.T("Settings.Presentation.Default", "Default")) : null,
            isUser ? UI.Badge(UI.T("Settings.Presentation.User", "User")) : null);
        var labels = UI.V(3, UI.Text(name, "control", maxLines: 2).Tip(name), markers);
        labels.VerticalAlignment = VerticalAlignment.Center;
        var content = UI.Grid("auto", "auto,*", thumbnail.Margin(0, 0, 10, 0).At(0, 0), labels.At(0, 1));
        var button = new Button
        {
            Content = content,
            MinHeight = 64,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            Background = theme.Brush("surface2"),
            BorderBrush = theme.Brush(selected ? "borderSelected" : "borderSubtle"),
            BorderThickness = new Thickness(selected ? 2 : 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, string.IsNullOrWhiteSpace(tooltip) ? name : tooltip);
        button.Click += (_, _) => select();
        return button;
    }
}
