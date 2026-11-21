using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Neuterradise.App.Presentation;
using Neuterradise.App.SystemServices.Resources;
using Windows.Foundation;

namespace Neuterradise.App.Ui;

internal sealed class ArtworkCompositionView : Grid
{
    private readonly Grid _layers = new();
    private readonly Grid _stage = new();
    private readonly Grid _output = new();
    private readonly Grid _sourceHost = new();
    private readonly List<Storyboard> _motion = [];
    private readonly ArtworkPlan _plan;
    private CompositionVisualSurface? _source;
    private SpriteVisual? _sprite;
    private LoadedImageSurface? _mask;
    private bool _maskCompleted;
    private bool _active = true;
    private bool _animated;

    public ArtworkCompositionView(ArtworkPlan plan, Func<string, FrameworkElement?> role)
    {
        _plan = plan;
        foreach (var layer in plan.Layers)
        {
            var content = layer.Kind == "image"
                ? new SkImageView { Source = ImageRef.FromPath(layer.AssetPath, 1600), PlaceholderToken = "transparent" }
                : role(layer.Kind);
            if (content is null) continue;
            var host = new Grid();
            SurfaceComposer.Detach(content);
            host.Children.Add(content);
            _layers.Children.Add(host);
            AddRotation(host, layer.Rotation);
        }

        _sourceHost.Children.Add(_layers);
        _stage.Children.Add(_sourceHost);
        if (plan.MaskPath is not null)
        {
            _sourceHost.IsHitTestVisible = false;
            _output.IsHitTestVisible = false;
            _stage.Children.Add(_output);
        }
        Children.Add(_stage);
        AddRotation(_stage, plan.Rotation);
        SizeChanged += (_, _) => UpdateSize();
        _stage.SizeChanged += (_, _) => UpdateSourceSize();
        Loaded += (_, _) =>
        {
            ConnectMask();
            UpdateSize();
            ResourceGovernor.Shared.Changed += OnMotionChanged;
            ReducedMotionAuthority.Changed += OnMotionChanged;
            UpdateMotion();
        };
        Unloaded += (_, _) =>
        {
            ResourceGovernor.Shared.Changed -= OnMotionChanged;
            ReducedMotionAuthority.Changed -= OnMotionChanged;
            UpdateMotion();
            if (_sprite is not null)
            {
                ElementCompositionPreview.SetElementChildVisual(_output, null!);
                _sprite = null;
                _source = null;
            }
            var mask = _mask;
            _mask = null;
            if (_maskCompleted) mask?.Dispose();
            _maskCompleted = false;
            _sourceHost.Clip = null;
        };
    }

    private void ConnectMask()
    {
        if (_plan.MaskPath is not { } path) return;
        var visual = ElementCompositionPreview.GetElementVisual(_layers);
        var compositor = visual.Compositor;
        _source = compositor.CreateVisualSurface();
        _source.SourceVisual = visual;
        _source.SourceOffset = Vector2.Zero;
        var mask = LoadedImageSurface.StartLoadFromUri(new Uri(path), new Size(2048, 2048));
        _mask = mask;
        var brush = compositor.CreateMaskBrush();
        brush.Source = compositor.CreateSurfaceBrush(_source);
        brush.Mask = compositor.CreateSurfaceBrush(mask);
        ((CompositionSurfaceBrush)brush.Mask).Stretch = CompositionStretch.Fill;
        var sprite = compositor.CreateSpriteVisual();
        sprite.Brush = brush;
        _sprite = sprite;
        mask.LoadCompleted += (sender, args) =>
        {
            if (ReferenceEquals(_mask, sender) && ReferenceEquals(_sprite, sprite))
            {
                _maskCompleted = true;
                if (args.Status != LoadedImageSourceLoadStatus.Success) return;
                // Clip only the parent so the Skia visual surface can still paint the original layers.
                _sourceHost.Clip = new RectangleGeometry { Rect = new Rect(-2, -2, 1, 1) };
                ElementCompositionPreview.SetElementChildVisual(_output, sprite);
            }
            else sender.Dispose();
        };
    }

    private void UpdateSize()
    {
        if (_plan.Aspect is { } aspect && ActualWidth > 0 && ActualHeight > 0)
        {
            _stage.Width = Math.Min(ActualWidth, ActualHeight * aspect);
            _stage.Height = _stage.Width / aspect;
            _stage.HorizontalAlignment = HorizontalAlignment.Center;
            _stage.VerticalAlignment = VerticalAlignment.Center;
        }
        UpdateSourceSize();
    }

    private void UpdateSourceSize()
    {
        if (_source is null || _sprite is null) return;
        var size = new Vector2((float)_stage.ActualWidth, (float)_stage.ActualHeight);
        // The pinned Uno Skia visual surface uses X for both source clip dimensions.
        _source.SourceSize = new Vector2(Math.Max(size.X, size.Y));
        _sprite.Size = size;
    }

    public void SetActive(bool active)
    {
        _active = active;
        UpdateMotion();
    }

    private void AddRotation(FrameworkElement view, ArtworkRotation? plan)
    {
        if (plan is null) return;
        var transform = new RotateTransform();
        view.RenderTransform = transform;
        view.RenderTransformOrigin = new Point(plan.PivotX, plan.PivotY);
        var animation = new DoubleAnimation
        {
            From = 0, To = plan.Clockwise ? 360 : -360,
            Duration = new Duration(TimeSpan.FromSeconds(plan.Seconds)),
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "Angle");
        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        storyboard.Children.Add(animation);
        _motion.Add(storyboard);
    }

    private void OnMotionChanged(object? sender, EventArgs args) => UiDispatch.Run(UpdateMotion);

    private void UpdateMotion()
    {
        var animate = IsLoaded && _active && !ReducedMotionAuthority.IsReduced
            && ResourceGovernor.Shared.Tier == PresentationTier.Full && !ResourceGovernor.Shared.IsMemoryPressure;
        if (_animated == animate) return;
        _animated = animate;
        foreach (var storyboard in _motion)
        {
            if (animate) storyboard.Begin();
            else storyboard.Stop();
        }
    }
}
