using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Neuterradise.App.Ui;

internal static class BrandAssets
{
    public const string PrimaryMarkUri = "ms-appx:///Assets/Brand/primary-mark.png";
    public const string WordmarkLockupUri = "ms-appx:///Assets/Brand/wordmark-lockup.png";
    public const string AppIconUri = "ms-appx:///Assets/Brand/app-icon.png";

    public static void ApplyNativeWindowIcon(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "app-icon.ico");
        if (File.Exists(path))
        {
            window.AppWindow.SetIcon(path);
        }
    }

    public static FrameworkElement Lockup(double markSize, double fontSize, bool animateMark = false)
    {
        var wordmark = new TextBlock
        {
            Text = "naut",
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = fontSize,
            FontWeight = new Windows.UI.Text.FontWeight(600),
            Foreground = ThemeRuntime.Current.Brush("textPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
            UseLayoutRounding = true,
        };
        AutomationProperties.SetName(wordmark, "naut");
        var mark = Image(PrimaryMarkUri, markSize, "naut");
        if (animateMark)
        {
            var rotation = new RotateTransform();
            mark.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            mark.RenderTransform = rotation;
            var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var spin = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = new Duration(TimeSpan.FromMilliseconds(1800)),
            };
            Storyboard.SetTarget(spin, rotation);
            Storyboard.SetTargetProperty(spin, "Angle");
            storyboard.Children.Add(spin);
            var loaded = false;
            void UpdateMotion()
            {
                storyboard.Stop();
                rotation.Angle = 0;
                if (loaded && !ReducedMotionAuthority.IsReduced) storyboard.Begin();
            }
            void OnMotionChanged(object? sender, EventArgs args) => UiDispatch.Run(UpdateMotion);
            mark.Loaded += (_, _) =>
            {
                loaded = true;
                ReducedMotionAuthority.Changed += OnMotionChanged;
                UpdateMotion();
            };
            mark.Unloaded += (_, _) =>
            {
                loaded = false;
                ReducedMotionAuthority.Changed -= OnMotionChanged;
                UpdateMotion();
            };
        }
        return UI.H(Math.Round(markSize * 0.2),
            mark,
            wordmark);
    }

    public static Image Image(string uri, double width, string accessibleName, double opacity = 1)
    {
        var view = new Image
        {
            Source = new BitmapImage(new Uri(uri)),
            Width = width,
            Height = width,
            UseLayoutRounding = true,
            Stretch = Stretch.Uniform,
            Opacity = opacity,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetName(view, accessibleName);
        return view;
    }
}
