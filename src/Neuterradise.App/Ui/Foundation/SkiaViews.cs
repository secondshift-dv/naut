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

/// <summary>A semantic icon drawn from Naut's fixed Neu Line icon system.</summary>
public sealed class IconView : SKCanvasElement
{
    private const int MaxCachedPaths = 128;
    private static readonly Lock PathCacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<CachedPath>> PathCache = new(StringComparer.Ordinal);
    private static readonly LinkedList<CachedPath> PathRecency = new();
    private string _key;
    private string _color;

    private sealed record CachedPath(string Data, SKPath Path);

    public IconView(string key, double size = 16, string color = "textPrimary")
    {
        _key = key;
        _color = color;
        Width = size;
        Height = size;
        IsHitTestVisible = false;
        ThemeRuntime.Current.Changed += Invalidate;
        Unloaded += (_, _) => ThemeRuntime.Current.Changed -= Invalidate;
        Loaded += (_, _) =>
        {
            ThemeRuntime.Current.Changed -= Invalidate;
            ThemeRuntime.Current.Changed += Invalidate;
        };
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
        var data = IconCatalog.PathFor(_key);
        var scale = (float)(Math.Min(area.Width, area.Height) / 24.0);

        using var path = AcquirePathCopy(data);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SkiaColor.Token(_color),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = IconCatalog.StrokeWidth,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };
        canvas.Save();
        canvas.Scale(scale);
        canvas.DrawPath(path, paint);
        canvas.Restore();
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
    private MediaTransformState _transform = MediaTransformState.Default;
    private double _cornerRadius;
    private bool _circle;
    private int _generation;
    private double _blur;
    private string _placeholderToken = "surface3";

    public SkImageView()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => EnsureImage();
        Unloaded += (_, _) => ReleaseImage();
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
            ++_generation;
            EnsureImage();
        }
    }

    public MediaTransformState Transform
    {
        get => _transform;
        set
        {
            _transform = value ?? MediaTransformState.Default;
            Invalidate();
        }
    }

    public double CornerRadiusValue
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = value;
            Invalidate();
        }
    }

    public bool Circle
    {
        get => _circle;
        set
        {
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
            _blur = value;
            Invalidate();
        }
    }

    public string PlaceholderToken
    {
        get => _placeholderToken;
        set
        {
            _placeholderToken = value;
            Invalidate();
        }
    }

    public bool HasImage => _image is not null;

    public Size ImagePixelSize => _image is null ? default : new Size(_image.Width, _image.Height);

    private async Task LoadAsync(ImageRef reference, int generation)
    {
        var loaded = string.IsNullOrWhiteSpace(reference.ResourceKey)
            ? await DerivedImageLoader.Shared.LoadAsync(reference.Path, reference.DecodeWidth).ConfigureAwait(false)
            : await DerivedImageLoader.Shared.LoadAsync(reference.ResourceKey, reference.Path, reference.DecodeWidth).ConfigureAwait(false);
        UiDispatch.Run(() =>
        {
            if (generation == _generation && loaded?.Image is SKImage image && IsLoaded)
            {
                _imageLease?.Dispose();
                _imageLease = loaded;
                _image = image;
                Invalidate();
            }
            else
            {
                loaded?.Dispose();
            }
        });
    }

    private void EnsureImage()
    {
        if (_image is not null || _source is not { } source || !IsLoaded)
        {
            Invalidate();
            return;
        }

        var generation = _generation;
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
        TaskObserver.Observe(LoadAsync(source, generation), "SkImageView.LoadAsync");
    }

    private void ReleaseImage()
    {
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

        if (_image is null)
        {
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(rect.Width, rect.Height),
                [SkiaColor.Token(_placeholderToken), SkiaColor.Token("surface1"), SkiaColor.Token("accentSecondary", 0.35)],
                null, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(rect, paint);
            canvas.Restore();
            return;
        }

        var placement = _transform.Place(_image.Width, _image.Height, rect.Width, rect.Height);
        using (var paint = new SKPaint { IsAntialias = true })
        {
            if (_blur > 0)
            {
                paint.ImageFilter = SKImageFilter.CreateBlur((float)_blur, (float)_blur);
            }

            canvas.Save();
            if (Math.Abs(placement.Rotation) > 0.01)
            {
                canvas.RotateDegrees((float)placement.Rotation, rect.MidX, rect.MidY);
            }

            var destination = SKRect.Create((float)placement.TranslateX, (float)placement.TranslateY, (float)placement.Width, (float)placement.Height);
            canvas.DrawImage(_image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
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
    private readonly SkImageView _probe = new();
    private SKImage? _cover;
    private ImageLease? _coverLease;
    private ImageRef? _source;
    private int _generation;
    private CoverAppearance? _appearance;
    private FramePlan? _plan;
    private MediaTransformState _transform = MediaTransformState.Default;
    private string _frameMode = "lite";
    private bool _hover;
    private double _phase;
    private bool _animating;
    private SKImage? _overlay;
    private ImageLease? _overlayLease;
    private int _overlayGeneration;
    private bool _showPlaceholderPerson = true;

    public bool ShowPlaceholderPerson
    {
        get => _showPlaceholderPerson;
        set
        {
            _showPlaceholderPerson = value;
            Invalidate();
        }
    }

    public CoverFrameView()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => { UpdateAnimation(); EnsureCover(); EnsureOverlay(); };
        Unloaded += (_, _) => { StopAnimation(); ReleaseCover(); ReleaseOverlay(); };
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
            ++_generation;
            EnsureCover();
        }
    }

    public CoverAppearance? Appearance
    {
        get => _appearance;
        set
        {
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
            _transform = value ?? MediaTransformState.Default;
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
            _hover = value;
            UpdateAnimation();
            Invalidate();
        }
    }

    private async Task LoadAsync(ImageRef reference, int generation)
    {
        var loaded = string.IsNullOrWhiteSpace(reference.ResourceKey)
            ? await DerivedImageLoader.Shared.LoadAsync(reference.Path, reference.DecodeWidth).ConfigureAwait(false)
            : await DerivedImageLoader.Shared.LoadAsync(reference.ResourceKey, reference.Path, reference.DecodeWidth).ConfigureAwait(false);
        UiDispatch.Run(() =>
        {
            if (generation == _generation && loaded?.Image is SKImage image && IsLoaded)
            {
                _coverLease?.Dispose();
                _coverLease = loaded;
                _cover = image;
                Invalidate();
            }
            else
            {
                loaded?.Dispose();
            }
        });
    }

    private void EnsureCover()
    {
        if (_cover is not null || _source is not { } source || !IsLoaded)
        {
            Invalidate();
            return;
        }

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
            TaskObserver.Observe(LoadAsync(source, _generation), "CoverFrameView.LoadAsync");
        }

        Invalidate();
    }

    private void ReleaseCover()
    {
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

        var cached = DerivedImageLoader.Shared.TryGetCached(path, 512);
        if (cached?.Image is SKImage image)
        {
            _overlayLease = cached;
            _overlay = image;
            Invalidate();
            return;
        }

        cached?.Dispose();
        var generation = _overlayGeneration;
        TaskObserver.Observe(
            LoadOverlayAsync(path, generation),
            "CoverFrameView.LoadOverlayAsync");
    }

    private async Task LoadOverlayAsync(string path, int generation)
    {
        var loaded = await DerivedImageLoader.Shared
            .LoadAsync(path, 512)
            .ConfigureAwait(false);

        UiDispatch.Run(() =>
        {
            if (generation == _overlayGeneration
                && IsLoaded
                && string.Equals(_plan?.OverlayAssetPath, path, StringComparison.OrdinalIgnoreCase)
                && loaded?.Image is SKImage image)
            {
                _overlayLease?.Dispose();
                _overlayLease = loaded;
                _overlay = image;
                Invalidate();
            }
            else
            {
                loaded?.Dispose();
            }
        });
    }

    private void ReleaseOverlay()
    {
        ++_overlayGeneration;
        _overlay = null;
        _overlayLease?.Dispose();
        _overlayLease = null;
        Invalidate();
    }

    private void UpdateAnimation()
    {
        var animated = _appearance is { Frame.SupportsAnimation: true, FrameAnimation: not CoverFrameAnimation.None }
            && (_frameMode == "full" || _hover)
            && !ThemeRuntime.Current.ReducedMotion
            && IsLoaded;
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
