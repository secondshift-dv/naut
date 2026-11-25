using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Presentation;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Resources;
using Windows.Foundation;

namespace Neuterradise.App.Ui;

internal sealed class SceneMotionView : Grid
{
    private readonly FrameworkElement _visual;
    private readonly SceneMotionPlan _plan;
    private readonly double _opacity;
    private readonly CompositeTransform _transform = new();
    private ScrollViewer? _viewport;
    private bool _loaded;
    private bool _running;
    private bool _completed;
    private long _started;
    private long _lastFrame;

    public SceneMotionView(FrameworkElement visual, SceneMotionPlan plan, double opacity)
    {
        _visual = visual;
        _plan = plan;
        _opacity = opacity;
        IsHitTestVisible = false;
        visual.RenderTransformOrigin = new Point(0.5, 0.5);
        visual.RenderTransform = _transform;
        Children.Add(visual);
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ScrollViewer scroll)
                {
                    _viewport = scroll;
                    scroll.ViewChanged += OnViewportChanged;
                    break;
                }
            ResourceGovernor.Shared.Changed += OnChanged;
            ReducedMotionAuthority.Changed += OnChanged;
            RefreshActivity();
        };
        Unloaded += (_, _) =>
        {
            if (!_loaded) return;
            _loaded = false;
            ResourceGovernor.Shared.Changed -= OnChanged;
            ReducedMotionAuthority.Changed -= OnChanged;
            if (_viewport is not null) _viewport.ViewChanged -= OnViewportChanged;
            _viewport = null;
            PresentationVisualScheduler.SetSceneActive(this, false);
            Stop(reset: true);
        };
        SizeChanged += (_, _) => RefreshActivity();
    }

    private void OnChanged(object? sender, EventArgs args) => UiDispatch.Run(RefreshActivity);
    private void OnViewportChanged(object? sender, ScrollViewerViewChangedEventArgs args) => RefreshActivity();

    internal void RefreshActivity()
    {
        var governor = ResourceGovernor.Shared;
        if (!_loaded || ReducedMotionAuthority.IsReduced || governor.Tier != PresentationTier.Full
            || governor.IsMemoryPressure || governor.IsScrolling || !InViewport())
        {
            PresentationVisualScheduler.SetSceneActive(this, false);
            Stop(reset: true);
            return;
        }
        if (!_completed) PresentationVisualScheduler.SetSceneActive(this, true);
    }

    internal void ApplyAdmission(bool admitted)
    {
        if (!admitted)
        {
            Stop(reset: !_completed);
            return;
        }
        if (_running || _completed) return;
        _running = true;
        _started = Environment.TickCount64;
        _lastFrame = _started - 33;
        PresentationVisualScheduler.StartClock(OnFrame);
        OnFrame(_started);
    }

    private bool InViewport()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || !PresentationEffects.IsTreeActive(this)) return false;
        for (DependencyObject? current = this; current is FrameworkElement element; current = VisualTreeHelper.GetParent(current))
            if (element.Visibility != Visibility.Visible || element.Opacity <= 0) return false;
        if (_viewport is null) return true;
        try
        {
            var bounds = TransformToVisual(_viewport).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
            return bounds.Bottom > 0 && bounds.Right > 0 && bounds.X < _viewport.ActualWidth && bounds.Y < _viewport.ActualHeight;
        }
        catch (InvalidOperationException) { return false; }
    }

    private void Stop(bool reset)
    {
        if (_running)
        {
            _running = false;
            PresentationVisualScheduler.StopClock(OnFrame);
        }
        if (!reset) return;
        _completed = false;
        _transform.TranslateX = _transform.TranslateY = _transform.Rotation = 0;
        _transform.ScaleX = _transform.ScaleY = 1;
        _visual.Opacity = _opacity;
    }

    private void OnFrame(long now)
    {
        if (!_running || now - _lastFrame < 33) return;
        _lastFrame = now;
        var elapsed = (now - _started) / (_plan.Seconds * 1000);
        var t = _plan.Loop ? elapsed % 1 : Math.Min(1, elapsed);
        var frames = _plan.Keyframes;
        var index = 1;
        while (index < frames.Count - 1 && t > frames[index].Time) index++;
        var a = frames[index - 1];
        var b = frames[index];
        var amount = Math.Clamp((t - a.Time) / (b.Time - a.Time), 0, 1);
        static double Lerp(double a, double b, double t) => a + (b - a) * t;
        _transform.TranslateX = Lerp(a.X, b.X, amount) * ActualWidth;
        _transform.TranslateY = Lerp(a.Y, b.Y, amount) * ActualHeight;
        _transform.ScaleX = _transform.ScaleY = Lerp(a.Scale, b.Scale, amount);
        _transform.Rotation = Lerp(a.Rotation, b.Rotation, amount);
        _visual.Opacity = _opacity * Lerp(a.Opacity, b.Opacity, amount);
        if (!_plan.Loop && elapsed >= 1)
        {
            _completed = true;
            PresentationVisualScheduler.SetSceneActive(this, false);
        }
    }
}
