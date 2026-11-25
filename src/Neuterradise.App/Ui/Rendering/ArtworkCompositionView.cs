using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Neuterradise.App.Presentation;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.Shell;
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
    private readonly List<(int Index, string SlotId, SceneBannerView View)> _bannerLayers = [];
    private string? _bannerSlotsJson;
    private IReadOnlyDictionary<string, BannerSlotAppearance>? _bannerSlots;
    private (MediaResourceAuthority Authority, Guid ProfileId, string? PrimaryPath, Guid? PrimaryId)? _bannerContext;
    private CancellationTokenSource? _bannerRequest;
    private enum BannerResolutionState { Pending, Resolved, RetryExhausted }
    private BannerResolutionState _bannerResolution;
    private ScrollViewer? _bannerViewport;
    private ContainerVisual? _maskHost;
    private CompositionVisualSurface? _source;
    private SpriteVisual? _sprite;
    private LoadedImageSurface? _mask;
    private bool _maskCompleted;
    private bool _spriteAttached;
    private bool _loaded;
    private bool _active = true;
    private bool _animated;

    public ArtworkCompositionView(ArtworkPlan plan, Func<string, FrameworkElement?> role)
    {
        _plan = plan;
        var positioned = plan.Layers.Any(layer => layer.Placement is not null || layer.Narrow is not null);
        for (var index = 0; index < plan.Layers.Count; index++)
        {
            var layer = plan.Layers[index];
            FrameworkElement? content;
            if (layer.Kind == "banner")
            {
                var banner = new SceneBannerView();
                _bannerLayers.Add((layer.BannerIndex, layer.BannerSlotId, banner));
                content = banner;
            }
            else content = layer.Kind == "image"
                ? new SkImageView { Source = ImageRef.FromPath(layer.AssetPath, 1600), PlaceholderToken = "transparent", IsHitTestVisible = false }
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
        SizeChanged += (_, _) =>
        {
            UpdateSize();
            if (_bannerLayers.Count != 0) RefreshBanners();
        };
        _stage.SizeChanged += (_, _) => UpdateStage();
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            ConnectMask();
            UpdateSize();
            if (_bannerLayers.Count != 0)
                for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                    if (parent is ScrollViewer scroll)
                    {
                        _bannerViewport = scroll;
                        scroll.ViewChanged += OnBannerViewportChanged;
                        break;
                    }
            if (_motion.Count == 0 && _bannerLayers.Count == 0) return;
            ResourceGovernor.Shared.Changed += OnMotionChanged;
            ReducedMotionAuthority.Changed += OnMotionChanged;
            UpdateMotion();
            RefreshBanners();
        };
        Unloaded += (_, _) =>
        {
            if (!_loaded) return;
            _loaded = false;
            if (_motion.Count != 0 || _bannerLayers.Count != 0)
            {
                ResourceGovernor.Shared.Changed -= OnMotionChanged;
                ReducedMotionAuthority.Changed -= OnMotionChanged;
                UpdateMotion();
            }
            if (_bannerViewport is not null) _bannerViewport.ViewChanged -= OnBannerViewportChanged;
            _bannerViewport = null;
            ResetBanners();
            RetireMask();
        };
    }

    private void ConnectMask()
    {
        if (_plan.MaskPath is not { } path || _mask is not null) return;
        var mask = LoadedImageSurface.StartLoadFromUri(new Uri(path), new Size(2048, 2048));
        _mask = mask;
        mask.LoadCompleted += OnMaskLoaded;
    }

    private void OnMaskLoaded(LoadedImageSurface sender, LoadedImageSourceLoadCompletedEventArgs args)
    {
        sender.LoadCompleted -= OnMaskLoaded;
        // A pending load owns its image until completion; only the current loaded generation may attach visuals.
        if (!_loaded || !ReferenceEquals(_mask, sender))
        {
            sender.Dispose();
            return;
        }

        _maskCompleted = true;
        if (args.Status != LoadedImageSourceLoadStatus.Success)
        {
            RetireMask();
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(_layers);
        var compositor = visual.Compositor;
        _source = compositor.CreateVisualSurface();
        _source.SourceVisual = visual;
        _source.SourceOffset = Vector2.Zero;
        var sprite = compositor.CreateSpriteVisual();
        _sprite = sprite;
        var brush = compositor.CreateMaskBrush();
        sprite.Brush = brush;
        brush.Source = compositor.CreateSurfaceBrush(_source);
        brush.Mask = compositor.CreateSurfaceBrush(sender);
        ((CompositionSurfaceBrush)brush.Mask).Stretch = CompositionStretch.Fill;
        if (_maskHost is null)
        {
            // Uno Skia cannot clear an element child visual by assigning null.
            _maskHost = compositor.CreateContainerVisual();
            ElementCompositionPreview.SetElementChildVisual(_output, _maskHost);
        }
        UpdateStage();
        _maskHost.Children.InsertAtTop(sprite);
        _spriteAttached = true;
        // Clip only the parent so the Skia visual surface can still paint the original layers.
        _sourceHost.Clip = new RectangleGeometry { Rect = new Rect(-2, -2, 1, 1) };
    }

    private void RetireMask()
    {
        var mask = _mask;
        var completed = _maskCompleted;
        _mask = null;
        _maskCompleted = false;
        _sourceHost.Clip = null;

        if (_sprite is { } sprite)
        {
            _sprite = null;
            if (_spriteAttached) _maskHost!.Children.Remove(sprite);
            _spriteAttached = false;
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
        }

        if (_source is { } source)
        {
            _source = null;
            source.SourceVisual = null;
            source.Dispose();
        }

        // Disposing an in-flight Uno image surface is unsafe; its completion handler retires stale loads.
        if (completed) mask?.Dispose();
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
        if (!active) ResetBanners();
        else RefreshBanners();
    }

    public void SetBannerContext(MediaResourceAuthority? authority, Guid? profileId, string? primaryPath, Guid? primaryId = null,
        ProfileAppearanceOverrides? appearance = null)
    {
        if (_bannerLayers.Count == 0) return;
        (MediaResourceAuthority Authority, Guid ProfileId, string? PrimaryPath, Guid? PrimaryId)? next =
            authority is not null && profileId is { } id ? (authority, id, primaryPath, primaryId) : null;
        if (_bannerContext != next)
        {
            ResetBanners();
            _bannerContext = next;
        }
        UpdateSlotAppearance(appearance);
        RefreshBanners();
    }

    public void UpdateSlotAppearance(ProfileAppearanceOverrides? appearance)
    {
        if (_bannerLayers.Count == 0 || _bannerSlotsJson == appearance?.BannerSlotsJson) return;
        var previous = _bannerSlots;
        _bannerSlotsJson = appearance?.BannerSlotsJson;
        _bannerSlots = _bannerSlotsJson is null ? null : BannerSlotState.Read(_bannerSlotsJson);
        var sourceChanged = false;
        foreach (var (_, id, view) in _bannerLayers)
        {
            var state = _bannerSlots?.GetValueOrDefault(id) ?? BannerSlotAppearance.Default;
            sourceChanged |= previous?.GetValueOrDefault(id)?.MediaId != state.MediaId;
            view.SetAppearance(state);
        }
        if (sourceChanged) ResetBanners();
        RefreshBanners();
    }

    private void ResetBanners()
    {
        if (_bannerLayers.Count == 0) return;
        var pending = _bannerRequest;
        _bannerRequest = null;
        pending?.Cancel();
        _bannerResolution = BannerResolutionState.Pending;
        foreach (var (_, _, view) in _bannerLayers) view.SetResource(null);
    }

    private void OnBannerViewportChanged(object? sender, ScrollViewerViewChangedEventArgs args) => RefreshBanners();

    private bool BannerInViewport()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || !PresentationEffects.IsTreeActive(this)) return false;
        for (DependencyObject? current = this; current is FrameworkElement element; current = VisualTreeHelper.GetParent(current))
            if (element.Visibility != Visibility.Visible || element.Opacity <= 0) return false;
        if (_bannerViewport is null) return true;
        try
        {
            var bounds = TransformToVisual(_bannerViewport).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
            return bounds.Bottom > 0 && bounds.Right > 0 && bounds.X < _bannerViewport.ActualWidth && bounds.Y < _bannerViewport.ActualHeight;
        }
        catch (InvalidOperationException) { return false; }
    }

    private void RefreshBanners()
    {
        if (_bannerLayers.Count == 0 || !_loaded || !_active) return;
        if (!BannerInViewport())
        {
            var pending = _bannerRequest;
            _bannerRequest = null;
            pending?.Cancel();
            foreach (var (_, _, view) in _bannerLayers) view.SetPlayback(false);
            return;
        }
        if (_bannerResolution == BannerResolutionState.Pending && _bannerRequest is null
            && !ResourceGovernor.Shared.IsScrolling && _bannerContext is { } context)
        {
            var request = new CancellationTokenSource();
            _bannerRequest = request;
            TaskObserver.Observe(ResolveBannersAsync(context, request), "ArtworkCompositionView.ResolveBanners");
        }
        var allow = ResourceGovernor.Shared.Tier == PresentationTier.Full && !ResourceGovernor.Shared.IsMemoryPressure
            && !ResourceGovernor.Shared.IsScrolling && !ReducedMotionAuthority.IsReduced;
        foreach (var (_, _, view) in _bannerLayers)
            view.SetPlayback(allow);
    }

    private async Task ResolveBannersAsync((MediaResourceAuthority Authority, Guid ProfileId, string? PrimaryPath, Guid? PrimaryId) context,
        CancellationTokenSource request)
    {
        bool IsCurrent() => ReferenceEquals(_bannerRequest, request) && _loaded && _active && !request.IsCancellationRequested;
        try
        {
            var choices = _bannerLayers.ToDictionary(layer => layer.Index,
                layer => _bannerSlots?.GetValueOrDefault(layer.SlotId)?.MediaId);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(attempt == 1 ? 250 : 1000, request.Token).ConfigureAwait(true);
                if (!IsCurrent() || !BannerInViewport() || ResourceGovernor.Shared.IsScrolling) return;
                var result = await Task.Run(() => context.Authority.ResolveSceneBannersAsync(
                    context.ProfileId, context.PrimaryPath, context.PrimaryId, choices, request.Token), request.Token).ConfigureAwait(true);
                if (!IsCurrent()) return;
                if (result.HasSnapshot)
                    foreach (var (index, _, view) in _bannerLayers)
                        view.SetResource(result.Resources.GetValueOrDefault(index));
                if (!result.RetryRequired)
                {
                    _bannerResolution = BannerResolutionState.Resolved;
                    return;
                }
                RefreshBanners();
            }
            if (IsCurrent()) _bannerResolution = BannerResolutionState.RetryExhausted;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch
        {
            // Unexpected faults are observed by TaskObserver; do not retry them indefinitely.
            if (IsCurrent()) _bannerResolution = BannerResolutionState.RetryExhausted;
            throw;
        }
        finally
        {
            if (ReferenceEquals(_bannerRequest, request))
            {
                _bannerRequest = null;
                RefreshBanners();
            }
            request.Dispose();
        }
    }

    // The shared video authority admits distinct sources; matching sources borrow the same frame.
    private sealed class SceneBannerView : Grid
    {
        private readonly SkImageView _poster = new() { PlaceholderToken = "transparent" };
        private SkVideoView? _video;
        private SceneBannerResource? _resource;
        private bool _failed;
        private BannerSlotAppearance _appearance = BannerSlotAppearance.Default;
        private BackdropView? _effect;

        public SceneBannerView()
        {
            IsHitTestVisible = false;
            Children.Add(_poster);
        }

        public void SetAppearance(BannerSlotAppearance appearance)
        {
            if (_appearance == appearance) return;
            var effectChanged = _appearance.Effect != appearance.Effect
                || _appearance.EffectIntensity != appearance.EffectIntensity;
            _appearance = appearance;
            _poster.Transform = appearance.Transform;
            _video?.SetPresentation(appearance.Transform);
            if (!effectChanged) return;
            var effectPlan = appearance.Effect is not null
                ? PresentationEffects.Runtime?.ResolveBannerSlotEffect(appearance) : null;
            if (effectPlan is null || effectPlan.Environment.Variants.Values.All(variant => variant.Layers.Count == 0))
            {
                if (_effect is not null)
                {
                    _effect.IsSurfaceActive = false;
                    Children.Remove(_effect);
                    _effect = null;
                }
                return;
            }
            if (_effect is null)
            {
                _effect = new BackdropView { IsHitTestVisible = false };
                Children.Add(_effect);
            }
            _effect.Plan = effectPlan.Environment;
        }

        public void SetResource(SceneBannerResource? resource)
        {
            if (_resource == resource) return;
            SetPlayback(false);
            _resource = resource;
            _failed = false;
            _poster.Source = ImageRef.FromPath(resource?.PosterPath, 1600);
        }

        public void SetPlayback(bool allowed)
        {
            if (_effect is not null) _effect.IsSurfaceActive = allowed && _resource is not null;
            if (!allowed || _resource is not { } resource || _failed)
            {
                ReleaseVideo();
                return;
            }
            if (_video is not null) return;
            var video = new SkVideoView { Purpose = VideoPurpose.Secondary, Visibility = Visibility.Collapsed };
            _video = video;
            video.FrameReady += () => { if (ReferenceEquals(_video, video)) video.Visibility = Visibility.Visible; };
            video.Failed += () =>
            {
                if (!ReferenceEquals(_video, video)) return;
                _failed = true;
                video.Visibility = Visibility.Collapsed;
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_video, video)) ReleaseVideo();
                });
            };
            Children.Insert(1, video);
            video.SetPresentation(_appearance.Transform);
            video.Play(resource.VideoPath, loop: true);
        }

        private void ReleaseVideo()
        {
            if (_video is { } video)
            {
                _video = null;
                Children.Remove(video);
                video.Dispose();
            }
        }
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

    private void OnMotionChanged(object? sender, EventArgs args) => UiDispatch.Run(() =>
    {
        UpdateMotion();
        RefreshBanners();
    });

    private void UpdateMotion()
    {
        if (_motion.Count == 0) return;
        var animate = _loaded && _active && !ReducedMotionAuthority.IsReduced
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
