using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Presentation;

namespace Neuterradise.App.Ui;

internal static class ThemePreviewView
{
    public static FrameworkElement Create(ThemePlan plan, double height)
    {
        var image = new SkImageView
        {
            Source = ImageRef.FromPath(plan.ArtworkPath!, height <= 60 ? 128 : 560, plan.ArtworkIdentity),
            Transform = MediaTransformState.Default,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
        };
        return new Border
        {
            Child = image,
            Height = height,
            Width = height <= 60 ? height : double.NaN,
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false,
        };
    }
}
