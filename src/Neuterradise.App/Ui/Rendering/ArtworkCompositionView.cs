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
    private readonly Canvas _positionedLayers = new();
    private readonly List<(Grid Host, ArtworkLayer Plan, int Index)> _placements = [];
    private readonly Grid _stage = new();
    private readonly Grid _output = new();
    private readonly Grid _sourceHost = new();
    private readonly List<Storyboard> _motion = [];
    private readonly ArtworkPlan _plan;
    private ContainerVisual? _maskHost;
    private CompositionVisualSurface? _source;
    private SpriteVisual? _sprite;
    private LoadedImageSurface? _mask;
    private bool _maskCompleted;
    private bool _active = true;
    private bool _animated;

    public ArtworkCompositionView(ArtworkPlan plan, Func<string, FrameworkElement?> role)
    {
        _plan = plan;
        var positioned = plan.Layers.Any(layer => layer.Placement is not null || layer.Narrow is not null);
        for (var index = 0; index < plan.Layers.Count; index++)
        {
            var layer = plan.Layers[index];
            var content = layer.Kind == "image"
                ? new SkImageView { Source = ImageRef.FromPath(layer.AssetPath, 1600), PlaceholderToken = "transparent" }
                : role(layer.Kind);
            if (content is null) continue;
            var host = new Grid();
            SurfaceComposer.Detach(content);
            host.Children.Add(content);
            if (positioned)
            {
                _positionedLayers.Children.Add(host);
                _placements.Add((host, layer, index));
            }
            else _layers.Children.Add(host);
            AddRotation(host, layer.Rotation);
        }

        if (positioned) _layers.Children.Add(_positionedLayers);
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
        _stage.SizeChanged += (_, _) => UpdateStage();
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
            if (_sprite is { } sprite)
            {
                _maskHost?.Children.Remove(sprite);
                if (sprite.Brush is CompositionMaskBrush brush)
                {
                    var sourceBrush = brush.Source;
                    var maskBrush = brush.Mask;
                    sprite.Brush = null;
                    brush.Dispose();
                    sourceBrush?.Dispose();
                    maskBrush?.Dispose();
                }
                sprite.Dispose();
                _sprite = null;
            }

            if (_source is { } source)
            {
                source.SourceVisual = null;
                source.Dispose();
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
            if (ReferenceEquals(_mask, sender) && _sprite is { } loadedSprite)
            {
                _maskCompleted = true;
                if (args.Status != LoadedImageSourceLoadStatus.Success) return;
                if (_maskHost is null)
                {
                    // Uno Skia cannot clear an element child visual by assigning null.
                    _maskHost = compositor.CreateContainerVisual();
                    ElementCompositionPreview.SetElementChildVisual(_output, _maskHost);
                }
                _maskHost.Children.InsertAtTop(loadedSprite);
                _maskHost.Size = loadedSprite.Size;
                // Clip only the parent so the Skia visual surface can still paint the original layers.
                _sourceHost.Clip = new RectangleGeometry { Rect = new Rect(-2, -2, 1, 1) };
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
        UpdateStage();
    }

    private void UpdateStage()
    {
        foreach (var (host, layer, index) in _placements)
        {
            var placement = (ActualWidth < _plan.Breakpoint ? layer.Narrow : null) ?? layer.Placement;
            var width = _stage.ActualWidth;
            var height = _stage.ActualHeight;
            host.Width = width * (placement?.Width ?? 1);
            host.Height = height * (placement?.Height ?? 1);
            var horizontal = placement?.Horizontal switch { "center" => 0.5, "right" => 1, _ => 0 };
            var vertical = placement?.Vertical switch { "center" => 0.5, "bottom" => 1, _ => 0 };
            Canvas.SetLeft(host, (width - host.Width) * horizontal + width * (placement?.X ?? 0));
            Canvas.SetTop(host, (height - host.Height) * vertical + height * (placement?.Y ?? 0));
            Canvas.SetZIndex(host, placement?.Order ?? index);
        }
        if (_source is null || _sprite is null) return;
        var size = new Vector2((float)_stage.ActualWidth, (float)_stage.ActualHeight);
        // The pinned Uno Skia visual surface uses X for both source clip dimensions.
        _source.SourceSize = new Vector2(Math.Max(size.X, size.Y));
        _sprite.Size = size;
        if (_maskHost is not null) _maskHost.Size = size;
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
