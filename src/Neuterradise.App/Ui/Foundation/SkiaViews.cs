using Microsoft.UI.Xaml;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Presentation;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Cache;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace Neuterradise.App.Ui;

/// <summary>Skia helpers shared by the renderer-owned views.</summary>
internal static class SkiaColor
{
    public static SKColor From(ArgbColor color, double opacity = 1) =>
        new(color.R, color.G, color.B, (byte)Math.Clamp(Math.Round(color.A * opacity), 0, 255));

    public static SKColor Token(string reference, double opacity = 1) =>
        From(ThemeRuntime.Current.Tokens.Resolve(reference.StartsWith('#') || reference.StartsWith("token:", StringComparison.Ordinal) ? reference : "token:" + reference), opacity);
}

internal sealed class LiveHeartOverlay : SKCanvasElement
{
    private static readonly (float X, float Y, float Size, byte Alpha)[] Hearts =
        [(0.89f, 0.57f, 11, 105), (0.94f, 0.65f, 8, 68), (0.87f, 0.72f, 14, 115),
         (0.94f, 0.80f, 9, 85), (0.88f, 0.88f, 12, 98)];

    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        if (area.Width <= 0 || area.Height <= 0) return;
        var w = (float)area.Width;
        var h = (float)area.Height;
        var bottom = Math.Min(88, h * .42f);
        using var shader = SKShader.CreateLinearGradient(new SKPoint(0, h - bottom), new SKPoint(0, h),
            [SKColors.Transparent, new SKColor(0, 0, 0, 164)], null, SKShaderTileMode.Clamp);
        using var scrim = new SKPaint { Shader = shader };
        canvas.DrawRect(SKRect.Create(0, h - bottom, w, bottom), scrim);
        using var heart = SKPath.ParseSvgPathData("M12 21 C10 19 3 14 3 8 C3 2 10 1 12 6 C14 1 21 2 21 8 C21 14 14 19 12 21 Z");
        using var paint = new SKPaint { IsAntialias = true };
        foreach (var (x, y, size, alpha) in Hearts)
        {
            canvas.Save();
            canvas.Translate(w * x - size / 2, h * y - size / 2);
            canvas.Scale(size / 24);
            paint.Color = new SKColor(255, 96, 141, alpha);
            canvas.DrawPath(heart, paint);
            canvas.Restore();
        }
    }
}

internal sealed class BreadcrumbStripeView : SKCanvasElement
{
    private SKColor _color;

    public void SetPattern(double opacity)
    {
        _color = SkiaColor.Token("accent", opacity);
        Visibility = opacity > 0 ? Visibility.Visible : Visibility.Collapsed;
        Invalidate();
    }

    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        if (area.Width <= 0 || area.Height <= 0 || _color.Alpha == 0) return;
        using var paint = new SKPaint { Color = _color, IsAntialias = true, StrokeWidth = 3 };
        canvas.Save();
        canvas.ClipRect(SKRect.Create(0, 0, (float)area.Width, (float)area.Height));
        for (float x = -(float)area.Height; x < area.Width; x += 14)
            canvas.DrawLine(x, 0, x + (float)area.Height, (float)area.Height, paint);
        canvas.Restore();
    }
}

/// <summary>A semantic icon using the active control skin or the built-in Neu Line fallback.</summary>
public sealed class IconView : SKCanvasElement
{
    private const int MaxCachedPaths = 128;
    private static readonly Lock PathCacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<CachedPath>> PathCache = new(StringComparer.Ordinal);
    private static readonly LinkedList<CachedPath> PathRecency = new();
    private string _key;
    private string _color;
    private bool _filled;
    private bool _selected;
    private ThemeRuntime? _registeredTheme;

    private sealed record CachedPath(string Data, SKPath Path);

    public IconView(string key, double size = 16, string color = "textPrimary")
    {
        _key = key;
        _color = color;
        Width = size;
        Height = size;
        IsHitTestVisible = false;
        Unloaded += OnUnloaded;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _registeredTheme?.UnregisterIcon(this);
        _registeredTheme = ThemeRuntime.Current;
        _registeredTheme.RegisterIcon(this);
        Invalidate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _registeredTheme?.UnregisterIcon(this);
        _registeredTheme = null;
    }

    public string Key
    {
        get => _key;
        set
        {
            if (_key != value)
            {
                _key = value;
                Invalidate();
            }
        }
    }

    public string ColorToken
    {
        get => _color;
        set
        {
            if (_color != value)
            {
                _color = value;
                Invalidate();
            }
        }
    }

    public bool Filled
    {
        get => _filled;
        set
        {
            if (_filled == value) return;
            _filled = value;
            Invalidate();
        }
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
        }
    }

    private static SKPath AcquirePathCopy(string data)
    {
        lock (PathCacheLock)
        {
            if (PathCache.TryGetValue(data, out var cached))
            {
                PathRecency.Remove(cached);
                PathRecency.AddFirst(cached);
                return new SKPath(cached.Value.Path);
            }

            var parsed = SKPath.ParseSvgPathData(data) ?? new SKPath();
            var node = PathRecency.AddFirst(new CachedPath(data, parsed));
            PathCache[data] = node;

            while (PathCache.Count > MaxCachedPaths && PathRecency.Last is { } oldest)
            {
                PathRecency.RemoveLast();
                PathCache.Remove(oldest.Value.Data);
                oldest.Value.Path.Dispose();
            }

            return new SKPath(parsed);
        }
    }

    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        var skin = ThemeRuntime.Current.ControlSkin?.Icons.GetValueOrDefault(_key);
        var data = skin is null ? IconCatalog.PathFor(_key)
            : _selected && skin.SelectedPath is { } selected ? selected : skin.Path;
        var scale = (float)(Math.Min(area.Width, area.Height) / 24.0);

        using var path = AcquirePathCopy(data);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SkiaColor.From(ThemeRuntime.Current.ScopedArgb(this, _color)),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)(skin?.StrokeWidth ?? IconCatalog.StrokeWidth),
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };
        canvas.Save();
        canvas.Scale(scale);
        if (_filled)
        {
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawPath(path, paint);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 0.85f;
        }
        canvas.DrawPath(path, paint);
        canvas.Restore();
    }
}

internal sealed class ViewImageRequest
{
    private CancellationTokenSource? _pending;
    public bool IsPending => _pending is not null;

    public void Cancel()
    {
        var request = _pending;
        _pending = null;
        request?.Cancel();
    }

    public void Start(string? identity, string path, int width, Func<ImageLease, bool> accept)
    {
        if (_pending is not null) return;
        var request = new CancellationTokenSource();
        _pending = request;
        TaskObserver.Observe(LoadAsync(request, identity, path, width, accept), "ViewImageRequest.LoadAsync");
    }

    private async Task LoadAsync(CancellationTokenSource request, string? identity, string path, int width,
        Func<ImageLease, bool> accept)
    {
        ImageLease? lease = null;
        try
        {
            lease = string.IsNullOrWhiteSpace(identity)
                ? await DerivedImageLoader.Shared.LoadAsync(path, width, request.Token).ConfigureAwait(false)
                : await DerivedImageLoader.Shared.LoadAsync(identity, path, width, request.Token).ConfigureAwait(false);
            if (lease is null || request.IsCancellationRequested) return;
            await UiDispatch.InvokeAsync(() =>
            {
                if (ReferenceEquals(_pending, request) && !request.IsCancellationRequested
                    && lease is not null && accept(lease)) lease = null;
            }).ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
            await UiDispatch.InvokeAsync(() =>
            {
                if (ReferenceEquals(_pending, request)) _pending = null;
                request.Dispose();
            }).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A derived image drawn through Skia at its decode width, placed with the canonical normalized media
/// transform (focal point, zoom, pan, fit). The decoded image lives in the bounded shared cache; this
/// element holds no decode of its own, cancels stale loads when recycled and never decodes full size.
/// </summary>
public sealed class SkImageView : SKCanvasElement
{
    private ImageRef? _source;
    private SKImage? _image;
    private ImageLease? _imageLease;
    private SKImage? _animationFrame;
    private MediaTransformState _transform = MediaTransformState.Default;
    private double _cornerRadius;
    private bool _circle;
    private readonly ViewImageRequest _imageRequest = new();
    private double _blur;
    private string _placeholderToken = "surface3";

    public SkImageView()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => EnsureImage();
        Unloaded += (_, _) =>
        {
            ReleaseImage();
            SetAnimationFrame(null);
        };
    }

    public ImageRef? Source
    {
        get => _source;
        set
        {
            if (Equals(_source, value))
            {
                return;
            }

            _source = value;
            ReleaseImage();
            SetAnimationFrame(null);
            EnsureImage();
        }
    }

    public MediaTransformState Transform
    {
        get => _transform;
        set
        {
            var next = value ?? MediaTransformState.Default;
            if (_transform == next) return;
            _transform = next;
            Invalidate();
        }
    }

    public double CornerRadiusValue
    {
        get => _cornerRadius;
        set
        {
            if (_cornerRadius == value) return;
            _cornerRadius = value;
            Invalidate();
        }
    }

    public bool Circle
    {
        get => _circle;
        set
        {
            if (_circle == value) return;
            _circle = value;
            Invalidate();
        }
    }

    /// <summary>Ambient blur for backdrop use; zero for ordinary artwork.</summary>
    public double Blur
    {
        get => _blur;
        set
        {
            if (_blur == value) return;
            _blur = value;
            Invalidate();
        }
    }

    public string PlaceholderToken
    {
        get => _placeholderToken;
        set
        {
            if (_placeholderToken == value) return;
            _placeholderToken = value;
            Invalidate();
        }
    }

    public bool HasImage => _image is not null;

    public Size ImagePixelSize => _image is null ? default : new Size(_image.Width, _image.Height);

    public void SetAnimationFrame(SKImage? frame)
    {
        _animationFrame?.Dispose();
        _animationFrame = frame;
        Invalidate();
    }

    private void EnsureImage()
    {
        if (_image is not null || _source is not { } source || !IsLoaded)
        {
            Invalidate();
            return;
        }

        if (_imageRequest.IsPending) return;
        var cached = string.IsNullOrWhiteSpace(source.ResourceKey)
            ? DerivedImageLoader.Shared.TryGetCached(source.Path, source.DecodeWidth)
            : DerivedImageLoader.Shared.TryGetCached(source.ResourceKey, source.Path, source.DecodeWidth);
        if (cached?.Image is SKImage image)
        {
            _imageLease = cached;
            _image = image;
            Invalidate();
            return;
        }

        cached?.Dispose();
        Invalidate();
        _imageRequest.Start(source.ResourceKey, source.Path, source.DecodeWidth, loaded =>
        {
            if (!IsLoaded || loaded.Image is not SKImage decodedImage) return false;
            _imageLease?.Dispose();
            _imageLease = loaded;
            _image = decodedImage;
            Invalidate();
            return true;
        });
    }

    private void ReleaseImage()
    {
        _imageRequest.Cancel();
        _image = null;
        _imageLease?.Dispose();
        _imageLease = null;
        Invalidate();
    }

    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        var rect = new SKRect(0, 0, (float)area.Width, (float)area.Height);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        canvas.Save();
        if (_circle)
        {
            using var circle = new SKPath();
            circle.AddOval(rect);
            canvas.ClipPath(circle, antialias: true);
        }
        else if (_cornerRadius > 0)
        {
            canvas.ClipRoundRect(new SKRoundRect(rect, (float)_cornerRadius), antialias: true);
        }

        var currentImage = _animationFrame ?? _image;
        if (currentImage is null)
        {
            if (_placeholderToken == "transparent")
            {
                canvas.Restore();
                return;
            }
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(rect.Width, rect.Height),
                [SkiaColor.Token(_placeholderToken), SkiaColor.Token("surface1"), SkiaColor.Token("accentSecondary", 0.35)],
                null, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(rect, paint);
            canvas.Restore();
            return;
        }

        var placement = _transform.Place(currentImage.Width, currentImage.Height, rect.Width, rect.Height);
        using (var paint = new SKPaint { IsAntialias = true })
        {
            using var blurFilter = _blur > 0
                ? SKImageFilter.CreateBlur((float)_blur, (float)_blur)
                : null;
            paint.ImageFilter = blurFilter;

            canvas.Save();
            if (Math.Abs(placement.Rotation) > 0.01)
            {
                canvas.RotateDegrees((float)placement.Rotation, rect.MidX, rect.MidY);
            }

            var destination = SKRect.Create((float)placement.TranslateX, (float)placement.TranslateY, (float)placement.Width, (float)placement.Height);
            canvas.DrawImage(currentImage, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            canvas.Restore();
        }

        canvas.Restore();
    }
}

/// <summary>
/// Cover + Shape + collectible Frame : Cover image → shape mask → frame visual → optional
/// effect. Frames are drawn procedurally per family with a Full / Lite / static tier. Continuous frame
/// motion runs only in Full mode, only while visible and never under Reduced Motion.
/// </summary>
public sealed class CoverFrameView : SKCanvasElement
{
    private SKImage? _cover;
    private ImageLease? _coverLease;
    private ImageRef? _source;
    private readonly ViewImageRequest _coverRequest = new();
    private CoverAppearance? _appearance;
    private FramePlan? _plan;
    private MediaTransformState _transform = MediaTransformState.Default;
    private string _frameMode = "lite";
    private bool _hover;
    private double _phase;
    private bool _animating;
    private bool _animationEligible;
    private SKImage? _overlay;
    private ImageLease? _overlayLease;
    private readonly ViewImageRequest _overlayRequest = new();
    private bool _showPlaceholderPerson = true;

    public bool ShowPlaceholderPerson
    {
        get => _showPlaceholderPerson;
        set
        {
            if (_showPlaceholderPerson == value) return;
            _showPlaceholderPerson = value;
            Invalidate();
        }
    }

    public CoverFrameView()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => { UpdateAnimation(); EnsureCover(); EnsureOverlay(); };
        Unloaded += (_, _) => { SetAnimationEligible(false); ReleaseCover(); ReleaseOverlay(); };
    }

    public ImageRef? Source
    {
        get => _source;
        set
        {
            if (Equals(_source, value))
            {
                return;
            }

            _source = value;
            ReleaseCover();
            EnsureCover();
        }
    }

    public CoverAppearance? Appearance
    {
        get => _appearance;
        set
        {
            if (Equals(_appearance, value)) return;
            _appearance = value;
            UpdateAnimation();
            Invalidate();
        }
    }

    public FramePlan? Plan
    {
        get => _plan;
        set
        {
            if (Equals(_plan, value)) return;
            _plan = value;
            ReleaseOverlay();
            EnsureOverlay();
        }
    }

    public MediaTransformState Transform
    {
        get => _transform;
        set
        {
            var next = value ?? MediaTransformState.Default;
            if (_transform == next) return;
            _transform = next;
            Invalidate();
        }
    }

    public (double X, double Y) FocalDeltaForDrag(double deltaX, double deltaY)
    {
        if (_cover is null || ActualWidth <= 0 || ActualHeight <= 0) return (0, 0);
        var size = Math.Min(ActualWidth, ActualHeight);
        var appearance = _appearance ?? CoverFrameCatalog.Resolve(null, ThemeRuntime.Current.ReducedMotion).Appearance;
        var hasFrame = appearance.Frame.Family != CoverFrameFamily.None && _frameMode != "none";
        var ring = hasFrame ? Math.Max(2, size * 0.06 * Math.Clamp(appearance.FrameScale, 0.75, 1.5)) : 0;
        var inset = hasFrame ? _plan?.CoverInsetFraction is { } fraction ? size * fraction : ring * 1.1 : 0;
        var artworkSize = Math.Max(1, size - inset * 2);
        var placement = _transform.Place(_cover.Width, _cover.Height, artworkSize, artworkSize);
        var overflowX = Math.Max(0, placement.Width - artworkSize);
        var overflowY = Math.Max(0, placement.Height - artworkSize);
        return (overflowX > 0.5 ? -deltaX / overflowX : 0,
            overflowY > 0.5 ? -deltaY / overflowY : 0);
    }

    public string FrameMode
    {
        get => _frameMode;
        set
        {
            if (_frameMode == value) return;
            _frameMode = value;
            UpdateAnimation();
            Invalidate();
        }
    }

    public bool IsHovered
    {
        get => _hover;
        set
        {
            if (_hover == value) return;
            _hover = value;
            UpdateAnimation();
            Invalidate();
        }
    }

    private void EnsureCover()
    {
        if (_cover is not null || _source is not { } source || !IsLoaded)
        {
            Invalidate();
            return;
        }

        if (_coverRequest.IsPending) return;
        var cached = string.IsNullOrWhiteSpace(source.ResourceKey)
            ? DerivedImageLoader.Shared.TryGetCached(source.Path, source.DecodeWidth)
            : DerivedImageLoader.Shared.TryGetCached(source.ResourceKey, source.Path, source.DecodeWidth);
        if (cached?.Image is SKImage image)
        {
            _coverLease = cached;
            _cover = image;
        }
        else
        {
            cached?.Dispose();
            _coverRequest.Start(source.ResourceKey, source.Path, source.DecodeWidth, loaded =>
            {
                if (!IsLoaded || loaded.Image is not SKImage decodedImage) return false;
                _coverLease?.Dispose();
                _coverLease = loaded;
                _cover = decodedImage;
                Invalidate();
                return true;
            });
        }

        Invalidate();
    }

    private void ReleaseCover()
    {
        _coverRequest.Cancel();
        _cover = null;
        _coverLease?.Dispose();
        _coverLease = null;
        Invalidate();
    }

    private void EnsureOverlay()
    {
        if (_overlay is not null || !IsLoaded || _plan?.OverlayAssetPath is not { } path)
        {
            return;
        }

        if (_overlayRequest.IsPending) return;
        var cached = DerivedImageLoader.Shared.TryGetCached(_plan.OverlayAssetIdentity ?? path, path, 512);
        if (cached?.Image is SKImage image)
        {
            _overlayLease = cached;
            _overlay = image;
            Invalidate();
            return;
        }

        cached?.Dispose();
        _overlayRequest.Start(_plan.OverlayAssetIdentity ?? path, path, 512, loaded =>
        {
            if (!IsLoaded || loaded.Image is not SKImage decodedImage) return false;
            _overlayLease?.Dispose();
            _overlayLease = loaded;
            _overlay = decodedImage;
            Invalidate();
            return true;
        });
    }

    private void ReleaseOverlay()
    {
        _overlayRequest.Cancel();
        _overlay = null;
        _overlayLease?.Dispose();
        _overlayLease = null;
        Invalidate();
    }

    private void UpdateAnimation() =>
        SetAnimationEligible(IsLoaded
            && _appearance is { Frame.SupportsAnimation: true, FrameAnimation: not CoverFrameAnimation.None }
            && (_frameMode == "full" || _hover)
            && !ThemeRuntime.Current.ReducedMotion);

    private void SetAnimationEligible(bool eligible)
    {
        if (_animationEligible != eligible)
        {
            _animationEligible = eligible;
            if (eligible)
                SystemServices.Resources.ResourceGovernor.Shared.Changed += OnAnimationGovernorChanged;
            else
                SystemServices.Resources.ResourceGovernor.Shared.Changed -= OnAnimationGovernorChanged;
        }

        UpdateAnimationClock();
    }

    private void OnAnimationGovernorChanged(object? sender, EventArgs args) => UiDispatch.Run(UpdateAnimationClock);

    private void UpdateAnimationClock()
    {
        var animated = _animationEligible && IsLoaded && SystemServices.Resources.ResourceGovernor.Shared.Tier is not
            (SystemServices.Resources.PresentationTier.Suspended or SystemServices.Resources.PresentationTier.Fallback);
        if (animated && !_animating)
        {
            _animating = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
        }
        else if (!animated)
        {
            StopAnimation();
        }
    }

    private void StopAnimation()
    {
        if (_animating)
        {
            _animating = false;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
        }
    }

    private long _lastTick;

    private void OnFrame(object? sender, object e)
    {
        var tier = SystemServices.Resources.ResourceGovernor.Shared.Tier;
        if (tier is SystemServices.Resources.PresentationTier.Suspended or SystemServices.Resources.PresentationTier.Fallback)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastTick < 33)
        {
            return;
        }

        _lastTick = now;
        _phase = (now % 6000) / 6000.0;
        Invalidate();
    }

    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        var size = (float)Math.Min(area.Width, area.Height);
        if (size <= 0)
        {
            return;
        }

        var appearance = _appearance ?? CoverFrameCatalog.Resolve(null, ThemeRuntime.Current.ReducedMotion).Appearance;
        var frame = appearance.Frame;
        var hasFrame = frame.Family != CoverFrameFamily.None && _frameMode != "none";
        var scale = hasFrame ? (float)Math.Clamp(appearance.FrameScale, 0.75, 1.5) : 1f;
        var ring = hasFrame ? Math.Max(2f, size * 0.06f * scale) : 0f;
        var inset = hasFrame
            ? _plan?.CoverInsetFraction is { } artworkInset ? size * (float)artworkInset : ring * 1.1f
            : 0f;
        var cx = (float)area.Width / 2;
        var cy = (float)area.Height / 2;
        var coverRect = new SKRect(cx - (size / 2) + inset, cy - (size / 2) + inset, cx + (size / 2) - inset, cy + (size / 2) - inset);
        var shape = appearance.Shape;
        using var shapePath = ShapePath(shape, coverRect);

        if (appearance.CoverShadow)
        {
            using var shadow = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.Black.WithAlpha(_frameMode == "full" ? (byte)56 : (byte)32),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.035f),
            };
            canvas.DrawPath(shapePath, shadow);
        }

        canvas.Save();
        canvas.ClipPath(shapePath, antialias: true);
        if (_cover is { } cover)
        {
            var placement = _transform.Place(cover.Width, cover.Height, coverRect.Width, coverRect.Height);
            var destination = SKRect.Create(coverRect.Left + (float)placement.TranslateX, coverRect.Top + (float)placement.TranslateY, (float)placement.Width, (float)placement.Height);
            canvas.DrawImage(cover, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        else
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(coverRect.Left, coverRect.Top), new SKPoint(coverRect.Right, coverRect.Bottom),
                [SkiaColor.Token("surface3"), SkiaColor.Token("accentSecondary", 0.6)], null, SKShaderTileMode.Clamp);
            using var fill = new SKPaint { Shader = shader };
            canvas.DrawRect(coverRect, fill);
            if (_showPlaceholderPerson)
            {
                using var person = new SKPaint { IsAntialias = true, Color = SkiaColor.Token("textMuted", 0.5) };
                canvas.DrawCircle(coverRect.MidX, coverRect.Top + (coverRect.Height * 0.4f), coverRect.Width * 0.16f, person);
                canvas.DrawOval(new SKRect(coverRect.MidX - (coverRect.Width * 0.3f), coverRect.Top + (coverRect.Height * 0.62f), coverRect.MidX + (coverRect.Width * 0.3f), coverRect.Bottom + (coverRect.Height * 0.2f)), person);
            }
        }

        canvas.Restore();

        if (!hasFrame)
        {
            return;
        }

        if (_overlay is not null)
        {
            var overlayRect = new SKRect(cx - (size / 2), cy - (size / 2), cx + (size / 2), cy + (size / 2));
            canvas.DrawImage(_overlay, overlayRect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            return;
        }

        DrawFrame(canvas, frame.Family, ShapePath(shape, InflateRect(coverRect, ring * 0.55f)), ring, appearance, size);
    }

    private void DrawFrame(SKCanvas canvas, CoverFrameFamily family, SKPath path, float ring, CoverAppearance appearance, float size)
    {
        using var _ = path;
        var lite = _frameMode == "lite" && !_hover;
        var intensity = (float)Math.Clamp(appearance.FrameIntensity, 0, 1);
        var bounds = path.Bounds;
        var tint = appearance.FrameTint is { } t && TokenAuthority.TryParseLiteral(t, out var tc) ? SkiaColor.From(tc) : (SKColor?)null;

        SKColor[] colors = family switch
        {
            CoverFrameFamily.Gold or CoverFrameFamily.Legendary => [new(0xFF, 0xF4, 0xD0, 0x7A), new(0xFF, 0xB8, 0x86, 0x2B), new(0xFF, 0xFF, 0xEC, 0xB3), new(0xFF, 0x9A, 0x6B, 0x1C)],
            CoverFrameFamily.Brass or CoverFrameFamily.Champion => [new(0xFF, 0xD8, 0xA8, 0x5C), new(0xFF, 0x8C, 0x62, 0x28), new(0xFF, 0xE9, 0xC8, 0x8A)],
            CoverFrameFamily.Silver or CoverFrameFamily.Metal => [new(0xFF, 0xF2, 0xF4, 0xF8), new(0xFF, 0x9C, 0xA3, 0xAF), new(0xFF, 0xD7, 0xDB, 0xE2), new(0xFF, 0x6B, 0x72, 0x80)],
            CoverFrameFamily.Obsidian => [new(0xFF, 0x2A, 0x24, 0x38), new(0xFF, 0x0B, 0x0A, 0x10), new(0xFF, 0x55, 0x48, 0x78)],
            CoverFrameFamily.Arcane or CoverFrameFamily.Mythic => [new(0xFF, 0x8B, 0x5C, 0xF6), new(0xFF, 0x3B, 0x1D, 0x7A), new(0xFF, 0xC4, 0xB5, 0xFD)],
            CoverFrameFamily.Celestial => [new(0xFF, 0xE0, 0xE7, 0xFF), new(0xFF, 0x60, 0x7B, 0xD8), new(0xFF, 0xFF, 0xFF, 0xFF)],
            CoverFrameFamily.Royal => [new(0xFF, 0x7C, 0x2D, 0x12), new(0xFF, 0xD9, 0xA4, 0x41), new(0xFF, 0x7C, 0x2D, 0x12)],
            CoverFrameFamily.Floral => [new(0xFF, 0xF9, 0xA8, 0xD4), new(0xFF, 0x86, 0xEF, 0xAC), new(0xFF, 0xFB, 0xCF, 0xE8)],
            CoverFrameFamily.Cyber or CoverFrameFamily.Hud => [new(0xFF, 0x22, 0xD3, 0xEE), new(0xFF, 0x0E, 0x74, 0x90), new(0xFF, 0x67, 0xE8, 0xF9)],
            CoverFrameFamily.NeonCircuit => [new(0xFF, 0x22, 0xD3, 0xEE), new(0xFF, 0xE8, 0x79, 0xF9), new(0xFF, 0x22, 0xD3, 0xEE)],
            CoverFrameFamily.Holographic => [new(0xFF, 0xF4, 0x72, 0xB6), new(0xFF, 0x60, 0xA5, 0xFA), new(0xFF, 0x34, 0xD3, 0x99), new(0xFF, 0xFA, 0xCC, 0x15), new(0xFF, 0xF4, 0x72, 0xB6)],
            CoverFrameFamily.SoftRing or CoverFrameFamily.DoubleRing => [SkiaColor.Token("accent"), SkiaColor.Token("accentSecondary"), SkiaColor.Token("accent")],
            _ => [SkiaColor.Token("borderStrong"), SkiaColor.Token("textMuted")],
        };

        if (tint is { } tintColor)
        {
            colors = [.. colors.Select(c => Mix(c, tintColor, 0.45f))];
        }

        var rotation = (float)(_phase * 360);
        using var shader = family is CoverFrameFamily.Holographic or CoverFrameFamily.NeonCircuit or CoverFrameFamily.Arcane or CoverFrameFamily.Celestial or CoverFrameFamily.Mythic
            ? SKShader.CreateSweepGradient(new SKPoint(bounds.MidX, bounds.MidY), colors, null, SKShaderTileMode.Clamp, rotation, rotation + 360)
            : SKShader.CreateLinearGradient(new SKPoint(bounds.Left, bounds.Top), new SKPoint(bounds.Right, bounds.Bottom), colors, null, SKShaderTileMode.Mirror);
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = lite ? Math.Max(1.5f, ring * 0.55f) : ring,
            Shader = shader,
        };

        if (!lite && family is CoverFrameFamily.NeonCircuit or CoverFrameFamily.Cyber or CoverFrameFamily.Arcane or CoverFrameFamily.Celestial or CoverFrameFamily.Mythic or CoverFrameFamily.Legendary or CoverFrameFamily.Holographic)
        {
            var pulse = appearance.FrameAnimation == CoverFrameAnimation.Glow ? 0.6 + (0.4 * Math.Sin(_phase * Math.PI * 2)) : 1.0;
            using var glow = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = ring * 1.6f,
                Color = colors[0].WithAlpha((byte)(110 * intensity * pulse)),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, ring * 1.2f),
            };
            canvas.DrawPath(path, glow);
        }

        canvas.DrawPath(path, stroke);

        if (family == CoverFrameFamily.DoubleRing || (!lite && family is CoverFrameFamily.Gold or CoverFrameFamily.Royal or CoverFrameFamily.Champion or CoverFrameFamily.Legendary))
        {
            using var inner = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, ring * 0.22f), Color = colors[^1].WithAlpha(200) };
            using var innerPath = ShapePathFromBounds(path, -ring * 0.9f);
            canvas.DrawPath(innerPath, inner);
        }

        if (!lite && family is CoverFrameFamily.Champion or CoverFrameFamily.Mythic or CoverFrameFamily.Legendary or CoverFrameFamily.Royal)
        {
            using var gem = new SKPaint { IsAntialias = true, Shader = shader };
            var r = ring * 1.15f;
            foreach (var angle in new[] { -90f, 30f, 150f })
            {
                var rad = angle * MathF.PI / 180f;
                var gx = bounds.MidX + (MathF.Cos(rad) * bounds.Width / 2);
                var gy = bounds.MidY + (MathF.Sin(rad) * bounds.Height / 2);
                using var diamond = new SKPath();
                diamond.MoveTo(gx, gy - r);
                diamond.LineTo(gx + (r * 0.7f), gy);
                diamond.LineTo(gx, gy + r);
                diamond.LineTo(gx - (r * 0.7f), gy);
                diamond.Close();
                canvas.DrawPath(diamond, gem);
            }
        }

        if (family is CoverFrameFamily.Hud or CoverFrameFamily.Cyber && !lite)
        {
            using var tick = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, ring * 0.35f), Color = colors[^1], StrokeCap = SKStrokeCap.Round };
            for (var i = 0; i < 4; i++)
            {
                var start = (i * 90) + 20 + (float)(_phase * 90);
                using var arc = new SKPath();
                arc.AddArc(InflateRect(bounds, ring * 0.9f), start, 40);
                canvas.DrawPath(arc, tick);
            }
        }

        if ((_hover || _frameMode == "full") && appearance.FrameAnimation == CoverFrameAnimation.Shimmer && !ThemeRuntime.Current.ReducedMotion)
        {
            canvas.Save();
            canvas.ClipPath(path, SKClipOperation.Intersect, true);
            var x = bounds.Left + ((float)_phase * bounds.Width * 1.6f) - (bounds.Width * 0.3f);
            using var sheen = SKShader.CreateLinearGradient(new SKPoint(x, bounds.Top), new SKPoint(x + (bounds.Width * 0.25f), bounds.Bottom),
                [SKColors.Transparent, SKColors.White.WithAlpha((byte)(120 * intensity)), SKColors.Transparent], null, SKShaderTileMode.Clamp);
            using var sheenPaint = new SKPaint { Shader = sheen, Style = SKPaintStyle.Stroke, StrokeWidth = ring * 1.2f, BlendMode = SKBlendMode.Plus };
            canvas.DrawPath(path, sheenPaint);
            canvas.Restore();
        }
    }

    private static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + ((b.Red - a.Red) * t)), (byte)(a.Green + ((b.Green - a.Green) * t)), (byte)(a.Blue + ((b.Blue - a.Blue) * t)), a.Alpha);

    private static SKRect InflateRect(SKRect rect, float amount) =>
        new(rect.Left - amount, rect.Top - amount, rect.Right + amount, rect.Bottom + amount);

    private static SKPath ShapePathFromBounds(SKPath source, float inset)
    {
        var bounds = source.Bounds;
        var path = new SKPath();
        var r = new SKRect(bounds.Left - inset, bounds.Top - inset, bounds.Right + inset, bounds.Bottom + inset);
        path.AddPath(source, SKMatrix.CreateScale(r.Width / Math.Max(1, bounds.Width), r.Height / Math.Max(1, bounds.Height), bounds.MidX, bounds.MidY));
        return path;
    }

    public static SKPath ShapePath(CoverShape shape, SKRect rect)
    {
        var path = new SKPath();
        var w = rect.Width;
        switch (shape)
        {
            case CoverShape.Circle:
                path.AddOval(rect);
                break;
            case CoverShape.Flower:
                const int samples = 160;
                for (var i = 0; i < samples; i++)
                {
                    var angle = MathF.Tau * i / samples - MathF.PI / 2;
                    var radius = 0.44f + 0.06f * MathF.Cos(8 * (angle + MathF.PI / 2));
                    var point = new SKPoint(rect.MidX + MathF.Cos(angle) * w * radius,
                        rect.MidY + MathF.Sin(angle) * rect.Height * radius);
                    if (i == 0) path.MoveTo(point); else path.LineTo(point);
                }
                path.Close();
                break;
            case CoverShape.Square:
                path.AddRoundRect(rect, w * 0.04f, w * 0.04f);
                break;
            case CoverShape.Squircle:
                path.AddRoundRect(rect, w * 0.3f, w * 0.3f);
                break;
            case CoverShape.Portrait:
                var portrait = new SKRect(rect.MidX - (w * 0.4f), rect.Top, rect.MidX + (w * 0.4f), rect.Bottom);
                path.AddRoundRect(portrait, w * 0.1f, w * 0.1f);
                break;
            case CoverShape.Hexagon:
                for (var i = 0; i < 6; i++)
                {
                    var angle = (MathF.PI / 3 * i) - (MathF.PI / 2);
                    var point = new SKPoint(rect.MidX + (MathF.Cos(angle) * w / 2), rect.MidY + (MathF.Sin(angle) * rect.Height / 2));
                    if (i == 0) path.MoveTo(point); else path.LineTo(point);
                }

                path.Close();
                break;
            case CoverShape.Diamond:
                path.MoveTo(rect.MidX, rect.Top);
                path.LineTo(rect.Right, rect.MidY);
                path.LineTo(rect.MidX, rect.Bottom);
                path.LineTo(rect.Left, rect.MidY);
                path.Close();
                break;
            default:
                path.AddRoundRect(rect, w * 0.16f, w * 0.16f);
                break;
        }

        return path;
    }
}
