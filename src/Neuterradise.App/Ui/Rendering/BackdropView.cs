using CommunityToolkit.WinUI.Lottie;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Presentation;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Resources;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;

namespace Neuterradise.App.Ui;

/// <summary>
/// The living backdrop (document 01 §13): a compiled <see cref="BackdropPlan"/> rendered as bounded
/// declarative layers. Procedural layers are drawn by Skia; video and Lottie layers are real elements
/// in the same stack. The active variant follows the ResourceGovernor tier and Reduced Motion; the
/// frame clock only runs while the owning surface is active, the window is visible and the variant is
/// animated. Nothing here is interpreted from JSON — the plan was compiled once.
/// </summary>
public sealed class BackdropView : Grid
{
    private BackdropPlan? _plan;
    private VisualTier _tier = VisualTier.Full;
    private VisualTier _scheduledTier = VisualTier.Full;
    private PresentationVisualPriority _visualPriority = PresentationVisualPriority.Surface;
    private Microsoft.UI.Xaml.Controls.ScrollViewer? _previewViewport;
    private bool _isPreview;
    private bool _active;
    private bool _clockRunning;
    private long _lastFrame;
    private readonly long _started = Environment.TickCount64;
    private string? _ambientPath;
    private string? _ambientVideoPath;
    public bool ReadabilityGuard { get; set; }
    public string? AmbientVideoPath
    {
        get => _ambientVideoPath;
        set { if (_ambientVideoPath == value) return; _ambientVideoPath = value; Rebuild(); }
    }
    private ArgbColor? _mediaTint;
    private SKImage? _ambientImage;
    private int _ambientGeneration;
    private (double X, double Y) _pointer = (0.5, 0.5);
    private readonly List<BackdropCanvas> _canvases = [];
    private readonly List<SkVideoView> _videos = [];
    private readonly List<AnimatedVisualPlayer> _players = [];

    public PresentationVisualPriority VisualPriority
    {
        get => _visualPriority;
        set { if (_visualPriority == value) return; _visualPriority = value; UpdateSchedule(); }
    }

    public void SetPreviewSelected(bool selected) => VisualPriority = selected
        ? PresentationVisualPriority.SelectedPreview : PresentationVisualPriority.VisiblePreview;

    public static BackdropView Preview(BackdropPlan plan, double height,
        PresentationVisualPriority priority = PresentationVisualPriority.SettingsPreview)
    {
        var view = new BackdropView { Plan = plan, Height = height, VisualPriority = priority, _isPreview = true };
        view.SizeChanged += (_, _) => view.RefreshPreviewVisibility();
        return view;
    }

    private void RefreshPreviewVisibility()
    {
        if (!_isPreview || !IsLoaded) return;
        if (_previewViewport is null)
            for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ScrollViewer scroll)
                {
                    _previewViewport = scroll;
                    scroll.ViewChanged += OnPreviewViewportChanged;
                    break;
                }
        var visible = ActualWidth > 0 && ActualHeight > 0 && PresentationEffects.IsTreeActive(this);
        for (DependencyObject? current = this; current is FrameworkElement framework; current = VisualTreeHelper.GetParent(current))
            visible &= framework.Visibility == Visibility.Visible && framework.Opacity > 0;
        if (_previewViewport is { } viewport)
        {
            try
            {
                var bounds = TransformToVisual(viewport).TransformBounds(new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight));
                visible &= bounds.Bottom > 0 && bounds.Right > 0 && bounds.X < viewport.ActualWidth && bounds.Y < viewport.ActualHeight;
            }
            catch (InvalidOperationException) { visible = false; }
        }
        IsSurfaceActive = visible;
    }

    internal void RefreshActivity() { if (_isPreview) RefreshPreviewVisibility(); }

    private void OnPreviewViewportChanged(object? sender, ScrollViewerViewChangedEventArgs args) => RefreshPreviewVisibility();
    private void UpdateSchedule() => PresentationVisualScheduler.Update(this,
        _active && IsLoaded && _plan?.Variants.Values.Any(variant => variant.IsAnimated) == true);
    internal void ApplyScheduledTier(VisualTier tier)
    {
        if (_scheduledTier == tier) return;
        _scheduledTier = tier;
        ApplyTier();
    }

    public BackdropView()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            ResourceGovernor.Shared.Changed += OnGovernorChanged;
            ThemeRuntime.Current.Changed += OnThemeChanged;
            ReducedMotionAuthority.Changed += OnMotionChanged;
            Rebuild();
            RefreshPreviewVisibility();
            UpdateSchedule();
            if (!string.IsNullOrWhiteSpace(_ambientPath))
            {
                var generation = ++_ambientGeneration;
                TaskObserver.Observe(
                    LoadAmbientAsync(_ambientPath, generation),
                    "BackdropView.LoadAmbientAsync");
            }
        };
        Unloaded += (_, _) =>
        {
            ResourceGovernor.Shared.Changed -= OnGovernorChanged;
            ThemeRuntime.Current.Changed -= OnThemeChanged;
            ReducedMotionAuthority.Changed -= OnMotionChanged;
            StopClock();
            PresentationVisualScheduler.Update(this, false);
            if (_previewViewport is not null) _previewViewport.ViewChanged -= OnPreviewViewportChanged;
            _previewViewport = null;
            StopVideos(releaseSources: true);
            foreach (var player in _players) player.Pause();
            RetireCanvases();
            ++_ambientGeneration;
            Interlocked.Exchange(ref _ambientImage, null)?.Dispose();
        };
    }

    /// <summary>Warms the bounded procedural resources and draw paths supplied by startup.</summary>
    internal static void PrewarmEffects(IEnumerable<BackdropPlan> plans) => BackdropCanvas.Prewarm(plans);

    /// <summary>The compiled plan. Changing it rebuilds the layer stack once; per-frame work only reads it.</summary>
    public BackdropPlan? Plan
    {
        get => _plan;
        set
        {
            if (ReferenceEquals(_plan, value))
            {
                return;
            }

            _plan = value;
            Rebuild();
            UpdateSchedule();
        }
    }

    /// <summary>Owning surface visibility. Inactive surfaces keep their last frame and stop the clock.</summary>
    public bool IsSurfaceActive
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            UpdateSchedule();
            UpdateClock();
            if (!value)
            {
                StopVideos(releaseSources: true);
            }
            else if (_videos.Any(video => video.SourcePath is null))
            {
                Rebuild();
            }
            else
            {
                StartVideos();
            }
            foreach (var player in _players) { if (value && _tier != VisualTier.Fallback) player.Resume(); else player.Pause(); }
        }
    }

    /// <summary>Living media: the Spotlight/Profile artwork used by "media-*" layers.</summary>
    public string? AmbientImagePath
    {
        get => _ambientPath;
        set
        {
            if (string.Equals(_ambientPath, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _ambientPath = value;
            var generation = ++_ambientGeneration;
            TaskObserver.Observe(
                LoadAmbientAsync(value, generation),
                "BackdropView.LoadAmbientAsync");
        }
    }

    public ArgbColor? MediaTint => _mediaTint;

    public event Action<ArgbColor?>? MediaTintChanged;

    public double ElapsedSeconds => _tier == VisualTier.Fallback || _plan?.For(_tier).Static == true || !_active
        ? 0 : (Environment.TickCount64 - _started) / 1000.0;

    /// <summary>Pointer position over the owning surface (0..1) for depth parallax.</summary>
    public void SetPointer(double x, double y)
    {
        _pointer = (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
    }

    internal (double X, double Y) Pointer => _pointer;

    internal SKImage? AmbientImage => _ambientImage;

    internal VisualTier Tier => _tier;

    private async Task LoadAmbientAsync(string? path, int generation)
    {
        SKImage? image = null;
        ArgbColor? tint = null;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            (image, tint) = await Task.Run(() =>
            {
                using var bitmap = TargetSizeDecoder.Decode(path, 960);
                if (bitmap is null)
                {
                    return ((SKImage?)null, (ArgbColor?)null);
                }

                return (SKImage.FromBitmap(bitmap), SampleTint(bitmap));
            }).ConfigureAwait(false);
        }

        try
        {
            UiDispatch.Run(() =>
            {
                if (generation != _ambientGeneration)
                {
                    image?.Dispose();
                    return;
                }

                var previous = _ambientImage;
                _ambientImage = image;
                _mediaTint = tint;
                MediaTintChanged?.Invoke(tint);
                foreach (var canvas in _canvases)
                {
                    canvas.OnAmbientChanged();
                }

                previous?.Dispose();
            });
        }
        catch (InvalidOperationException)
        {
            image?.Dispose();
        }
    }

    /// <summary>
    /// A small readability-safe palette sample : average colour with saturation/brightness
    /// clamped so tinted glass never undermines text contrast.
    /// </summary>
    private static ArgbColor SampleTint(SKBitmap bitmap)
    {
        using var tiny = bitmap.Resize(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear));
        if (tiny is null)
        {
            return default;
        }

        long r = 0, g = 0, b = 0;
        var count = 0;
        for (var y = 0; y < tiny.Height; y++)
        {
            for (var x = 0; x < tiny.Width; x++)
            {
                var c = tiny.GetPixel(x, y);
                r += c.Red;
                g += c.Green;
                b += c.Blue;
                count++;
            }
        }

        var color = new SKColor((byte)(r / count), (byte)(g / count), (byte)(b / count));
        color.ToHsl(out var h, out var s, out var l);
        var clamped = SKColor.FromHsl(h, Math.Clamp(s, 20, 60), Math.Clamp(l, 28, 52));
        return new ArgbColor(0xFF, clamped.Red, clamped.Green, clamped.Blue);
    }

    private void OnGovernorChanged(object? sender, EventArgs e) => UiDispatch.Run(ApplyTier);

    private void OnThemeChanged()
    {
        foreach (var canvas in _canvases)
        {
            canvas.Prepare();
        }
        foreach (var canvas in _canvases) canvas.Invalidate();
    }

    private void OnMotionChanged(object? sender, EventArgs e) => UiDispatch.Run(ApplyTier);

    private VisualTier CurrentTier()
    {
        if (ReducedMotionAuthority.IsReduced)
        {
            return VisualTier.Fallback;
        }

        var governorTier = ResourceGovernor.Shared.Tier switch
        {
            PresentationTier.Full => VisualTier.Full,
            PresentationTier.Reduced => VisualTier.Reduced,
            _ => VisualTier.Fallback,
        };
        return (VisualTier)Math.Max((int)governorTier, (int)_scheduledTier);
    }

    private void ApplyTier()
    {
        var tier = CurrentTier();
        if (tier != _tier)
        {
            _tier = tier;
            Rebuild();
        }
        else
        {
            UpdateClock();
        }
    }

    private void Rebuild()
    {
        StopClock();
        StopVideos(releaseSources: true);
        RetireCanvases();
        Children.Clear();
        _videos.Clear();
        foreach (var player in _players) player.Pause();
        _players.Clear();
        if (_plan is null)
        {
            return;
        }

        _tier = CurrentTier();
        var variant = _plan.For(_tier);
        var segment = new List<BackdropLayer>();

        void Flush()
        {
            if (segment.Count == 0)
            {
                return;
            }

            var canvas = new BackdropCanvas(this, [.. segment]);
            _canvases.Add(canvas);
            Children.Add(canvas);
            segment.Clear();
        }

        foreach (var layer in variant.Layers)
        {
            if (layer.Kind == "video")
            {
                Flush();
                if (_tier == VisualTier.Full && (layer.AssetPath ?? (layer.Source == "media-banner" ? AmbientVideoPath : null)) is { } videoPath)
                {
                    var video = new SkVideoView
                    {
                        Opacity = layer.Opacity,
                    };
                    video.Play(videoPath, loop: true);
                    video.SetActive(_active);
                    _videos.Add(video);
                    Children.Add(video);
                }

                continue;
            }

            if (layer.Kind == "lottie")
            {
                Flush();
                if (_tier != VisualTier.Fallback && layer.AssetPath is { } lottiePath)
                {
                    var player = new AnimatedVisualPlayer
                    {
                        Source = new LottieVisualSource { UriSource = new Uri(lottiePath) },
                        AutoPlay = _active,
                        Stretch = Stretch.UniformToFill,
                        Opacity = layer.Opacity,
                    };
                    Children.Add(player);
                    _players.Add(player);
                }

                continue;
            }

            segment.Add(layer);
        }

        Flush();
        if (ReadabilityGuard && variant.Layers.Any(l => l.Kind is "image" or "video"))
            Children.Add(new Border { Background = ThemeRuntime.Current.Brush("canvas"), Opacity = 0.78, IsHitTestVisible = false });
        StartVideos();
        UpdateClock();
    }

    private void RetireCanvases()
    {
        foreach (var canvas in _canvases)
        {
            canvas.Retire();
        }

        _canvases.Clear();
    }

    private void StartVideos()
    {
        if (!_active || _tier != VisualTier.Full)
        {
            return;
        }

        foreach (var video in _videos)
        {
            video.SetActive(true);
        }
    }

    private void StopVideos(bool releaseSources = false)
    {
        foreach (var video in _videos)
        {
            if (releaseSources)
            {
                video.Stop();
            }
            else
            {
                video.SetActive(false);
            }
        }
    }

    private void UpdateClock()
    {
        var animated = _plan?.For(_tier).IsAnimated == true;
        var allowed = _active && IsLoaded && animated && _tier != VisualTier.Fallback
            && ResourceGovernor.Shared.Tier != PresentationTier.Suspended;
        if (allowed && !_clockRunning)
        {
            _clockRunning = true;
            CompositionTarget.Rendering += OnFrame;
        }
        else if (!allowed)
        {
            StopClock();
            foreach (var canvas in _canvases)
            {
                canvas.Invalidate();
            }
        }
    }

    private void StopClock()
    {
        if (_clockRunning)
        {
            _clockRunning = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, object e)
    {
        // Scroll reduces drawing frequency without rebuilding layers or changing particle trajectories.
        var interval = _tier == VisualTier.Full && !ResourceGovernor.Shared.IsScrolling ? 33 : 66;
        var now = Environment.TickCount64;
        if (now - _lastFrame < interval)
        {
            return;
        }

        _lastFrame = now;
        foreach (var canvas in _canvases)
        {
            canvas.Invalidate();
        }
    }
}

/// <summary>Draws one contiguous run of procedural backdrop layers.</summary>
internal sealed class BackdropCanvas : SKCanvasElement
{
    private static readonly Lazy<SKImage> NoiseTexture = new(() => CreateNoise(96, grain: true));
    private static readonly Lazy<SKImage> FogTexture = new(() => CreateNoise(128, grain: false));

    private readonly BackdropView _owner;
    private readonly BackdropLayer[] _layers;
    private readonly Dictionary<BackdropLayer, SKImage?> _images = [];
    private readonly Dictionary<BackdropLayer, SKImage?> _blurred = [];
    private readonly Dictionary<BackdropLayer, Particle[]> _particles = [];
    private PreparedColors[] _colors = [];
    private int _generation;
    private bool _retired;

    public static void Prewarm(IEnumerable<BackdropPlan> plans)
    {
        var variants = plans.SelectMany(plan => plan.Variants.Values).ToArray();
        if (variants.SelectMany(variant => variant.Layers).Any(layer => layer.Kind == "noise")) _ = NoiseTexture.Value;
        if (variants.SelectMany(variant => variant.Layers).Any(layer => layer.Kind == "fog")) _ = FogTexture.Value;

        var owner = new BackdropView();
        using var surface = SKSurface.Create(new SKImageInfo(192, 108));
        foreach (var variant in variants)
        {
            var layers = variant.Layers.Where(layer => layer.Kind is not "video" and not "lottie").ToArray();
            if (layers.Length == 0) continue;
            var canvas = new BackdropCanvas(owner, layers);
            canvas.RenderOverride(surface.Canvas, new Windows.Foundation.Size(192, 108));
            surface.Canvas.Clear(SKColors.Transparent);
            canvas.Retire();
        }
    }

    public BackdropCanvas(BackdropView owner, BackdropLayer[] layers)
    {
        _owner = owner;
        _layers = layers;
        IsHitTestVisible = false;
        Prepare();
        foreach (var layer in layers.Where(l => l.Kind == "image" && l.AssetPath is not null))
        {
            TaskObserver.Observe(
                LoadMediaAsync(layer),
                "BackdropCanvas.LoadMediaAsync");
        }
    }

    /// <summary>Resolves token colours once per theme; never per frame.</summary>
    public void Prepare()
    {
        var tokens = ThemeRuntime.Current.Tokens;
        _colors = [.. _layers.Select(layer => new PreparedColors(
            layer.Color is null ? null : SkiaColor.From(layer.Kind == "calm-tint"
                ? PresentationVisualPolicy.CalmTint(tokens.Resolve("token:canvas"), tokens.Resolve(layer.Color))
                : tokens.Resolve(layer.Color)),
            layer.Color2 is null ? null : SkiaColor.From(tokens.Resolve(layer.Color2)),
            layer.Stops?.Select(stop => SkiaColor.From(tokens.Resolve(stop.Color))).ToArray(),
            layer.Stops?.Select(stop => (float)stop.Offset).ToArray()))];

        foreach (var particleLayer in _layers.Where(l => l.Kind == "particles" && !_particles.ContainsKey(l)))
        {
            var random = new Random(4242);
            _particles[particleLayer] = [.. Enumerable.Range(0, particleLayer.Count).Select(_ => new Particle(
                (float)random.NextDouble(), (float)random.NextDouble(),
                (float)(particleLayer.SizeMin + (random.NextDouble() * (particleLayer.SizeMax - particleLayer.SizeMin))),
                (float)(0.3 + random.NextDouble()), (float)(random.NextDouble() * Math.PI * 2)))];
        }

        Invalidate();
    }

    public void OnAmbientChanged()
    {
        foreach (var layer in _layers.Where(l => l.Source is not null))
        {
            if (_blurred.Remove(layer, out var image))
            {
                image?.Dispose();
            }
        }

        Invalidate();
    }

    private async Task LoadMediaAsync(BackdropLayer layer)
    {
        var generation = _generation;
        var image = await Task.Run(() =>
        {
            using var bitmap = TargetSizeDecoder.Decode(layer.AssetPath!, 1920);
            return bitmap is null ? null : SKImage.FromBitmap(bitmap);
        }).ConfigureAwait(false);
        try
        {
            UiDispatch.Run(() =>
            {
                if (_retired || generation != _generation)
                {
                    image?.Dispose();
                    return;
                }

                if (_images.Remove(layer, out var previous))
                {
                    previous?.Dispose();
                }

                _images[layer] = image;
                if (_blurred.Remove(layer, out var blurred))
                {
                    blurred?.Dispose();
                }
                Invalidate();
            });
        }
        catch (InvalidOperationException)
        {
            image?.Dispose();
        }
    }

    public void Retire()
    {
        if (_retired)
        {
            return;
        }

        _retired = true;
        _generation++;
        foreach (var image in _blurred.Values)
        {
            image?.Dispose();
        }

        foreach (var image in _images.Values)
        {
            image?.Dispose();
        }

        _blurred.Clear();
        _images.Clear();
    }

    protected override void RenderOverride(SKCanvas canvas, Windows.Foundation.Size area)
    {
        var w = (float)area.Width;
        var h = (float)area.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var t = _owner.ElapsedSeconds;
        const double parallax = 1;
        var (px, py) = _owner.Pointer;
        var rect = new SKRect(0, 0, w, h);

        for (var i = 0; i < _layers.Length; i++)
        {
            var layer = _layers[i];
            var colors = i < _colors.Length ? _colors[i] : default;
            using var paint = new SKPaint { IsAntialias = true, BlendMode = Blend(layer.Blend) };
            var wave = Math.Sin(2 * Math.PI * t / Math.Max(2, layer.Period));
            var wave2 = Math.Cos(2 * Math.PI * t / Math.Max(2, layer.Period * 1.3));
            var depthX = (float)((px - 0.5) * layer.Depth * 60 * parallax);
            var depthY = (float)((py - 0.5) * layer.Depth * 40 * parallax);

            switch (layer.Kind)
            {
                case "gradient":
                {
                    if (colors.Stops is not { Length: > 0 } stops)
                    {
                        break;
                    }

                    var angle = (layer.Angle + (wave * layer.Amplitude * 90)) * Math.PI / 180;
                    var dx = (float)Math.Cos(angle) * Math.Max(w, h) / 2;
                    var dy = (float)Math.Sin(angle) * Math.Max(w, h) / 2;
                    paint.Shader = SKShader.CreateLinearGradient(new SKPoint((w / 2) - dx, (h / 2) - dy), new SKPoint((w / 2) + dx, (h / 2) + dy),
                        [.. stops.Select(c => c.WithAlpha((byte)(c.Alpha * layer.Opacity)))], colors.Offsets, SKShaderTileMode.Clamp);
                    canvas.DrawRect(rect, paint);
                    break;
                }

                case "radial-glow":
                {
                    var color = colors.Color ?? SKColors.White;
                    var cx = (float)(layer.CenterX + (wave * layer.Amplitude)) * w + depthX;
                    var cy = (float)(layer.CenterY + (wave2 * layer.Amplitude)) * h + depthY;
                    var pulse = layer.Amplitude > 0 ? 1 + (0.25 * wave * layer.Amplitude * 4) : 1;
                    var radius = (float)(layer.Radius * Math.Max(w, h));
                    paint.Shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), radius,
                        [color.WithAlpha((byte)Math.Clamp(color.Alpha * layer.Opacity * pulse, 0, 255)), color.WithAlpha(0)], null, SKShaderTileMode.Clamp);
                    canvas.DrawRect(rect, paint);
                    break;
                }

                case "image":
                {
                    var image = ResolveImage(layer);
                    if (image is null)
                    {
                        break;
                    }

                    var scale = (float)(layer.Scale * (1 + (layer.Amplitude * 0.5 * (1 + wave))));
                    var baseScale = Math.Max(w / image.Width, h / image.Height) * scale;
                    var iw = image.Width * baseScale;
                    var ih = image.Height * baseScale;
                    var ox = ((w - iw) / 2) + (float)(wave2 * layer.Amplitude * w * 0.5) + depthX;
                    var oy = ((h - ih) / 2) + (float)(wave * layer.Amplitude * h * 0.3) + depthY;
                    paint.Color = SKColors.White.WithAlpha((byte)(255 * layer.Opacity));
                    canvas.DrawImage(image, SKRect.Create(ox, oy, iw, ih), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
                    break;
                }

                case "fog":
                {
                    var texture = FogTexture.Value;
                    var color = colors.Color ?? SKColors.White;
                    var shift = (float)(t * layer.Speed * 12);
                    var matrix = SKMatrix.CreateScale((float)(layer.Scale * 6), (float)(layer.Scale * 6)).PostConcat(SKMatrix.CreateTranslation(shift + depthX, (shift * 0.3f) + depthY));
                    paint.Shader = SKShader.CreateImage(texture, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, matrix);
                    paint.ColorFilter = SKColorFilter.CreateBlendMode(color.WithAlpha((byte)(255 * layer.Opacity)), SKBlendMode.SrcIn);
                    canvas.DrawRect(rect, paint);
                    break;
                }

                case "particles":
                {
                    var color = colors.Color ?? SKColors.White;
                    foreach (var particle in _particles[layer])
                    {
                        var travel = (float)(t * layer.Speed * 0.16 * particle.Speed);
                        var y = (particle.Y + (layer.Direction is "down" or "diagonal" ? travel : layer.Direction == "still" ? 0 : -travel)) % 1f;
                        if (y < 0)
                        {
                            y += 1;
                        }

                        var x = particle.X + (float)(Math.Sin((t * 0.3 * particle.Speed) + particle.Phase) * 0.015);
                        if (layer.Direction == "diagonal") x = (x + travel * 0.4f) % 1;
                        if (layer.Direction == "radial")
                        {
                            var radius = (particle.Y + travel) % 1;
                            x = 0.5f + (float)Math.Cos(particle.Phase) * radius * 0.5f;
                            y = 0.5f + (float)Math.Sin(particle.Phase) * radius * 0.5f;
                        }
                        var twinkle = 0.55 + (0.45 * Math.Sin((t * 1.4 * particle.Speed) + particle.Phase));
                        paint.Color = color.WithAlpha((byte)Math.Clamp(color.Alpha * layer.Opacity * twinkle, 0, 255));
                        var particleX = (x * w) + (depthX * 1.5f);
                        var particleY = (y * h) + (depthY * 1.5f);
                        if (layer.ParticleShape == "line") canvas.DrawLine(particleX, particleY, particleX - particle.Size * 2, particleY - particle.Size * 6, paint);
                        else if (layer.ParticleShape is "petal" or "leaf" or "diamond")
                        {
                            canvas.Save();
                            canvas.RotateDegrees((float)(particle.Phase * 57.3 + t * 16), particleX, particleY);
                            if (layer.ParticleShape == "diamond") canvas.DrawRect(SKRect.Create(particleX - particle.Size, particleY - particle.Size, particle.Size * 2, particle.Size * 2), paint);
                            else canvas.DrawOval(particleX, particleY, particle.Size, particle.Size * (layer.ParticleShape == "leaf" ? 2 : .6f), paint);
                            canvas.Restore();
                        }
                        else
                        {
                            paint.Style = layer.ParticleShape == "ring" ? SKPaintStyle.Stroke : SKPaintStyle.Fill;
                            canvas.DrawCircle(particleX, particleY, particle.Size, paint);
                            paint.Style = SKPaintStyle.Fill;
                        }
                    }

                    break;
                }

                case "noise":
                {
                    paint.Shader = SKShader.CreateImage(NoiseTexture.Value, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
                    paint.Color = SKColors.White.WithAlpha((byte)(255 * layer.Opacity));
                    paint.BlendMode = SKBlendMode.Overlay;
                    canvas.DrawRect(rect, paint);
                    break;
                }

                case "grid-field":
                {
                    paint.Color = (colors.Color ?? SKColors.White).WithAlpha((byte)(255 * layer.Opacity));
                    paint.StrokeWidth = 1;
                    var step = (float)(36 * layer.Scale);
                    for (var x = 0f; x <= w; x += step) canvas.DrawLine(x, 0, x, h, paint);
                    for (var y = 0f; y <= h; y += step) canvas.DrawLine(0, y, w, y, paint);
                    break;
                }
                case "wave-field":
                {
                    paint.Color = (colors.Color ?? SKColors.White).WithAlpha((byte)(255 * layer.Opacity));
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 1.2f;
                    for (var band = 0; band < 12; band++)
                    {
                        using var path = new SKPath();
                        for (var step = 0; step <= 48; step++)
                        {
                            var x = w * step / 48;
                            var y = h * band / 12 + (float)(Math.Sin(step * 0.24 + band * 0.6 + t * Math.Max(0.6, layer.Speed)) * 18 * layer.Scale);
                            if (step == 0) path.MoveTo(x, y); else path.LineTo(x, y);
                        }
                        canvas.DrawPath(path, paint);
                    }
                    break;
                }

                case "vignette":
                {
                    paint.Shader = SKShader.CreateRadialGradient(new SKPoint(w / 2, h / 2), Math.Max(w, h) * 0.75f,
                        [SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha((byte)(255 * layer.Opacity))], [0.45f, 1f], SKShaderTileMode.Clamp);
                    canvas.DrawRect(rect, paint);
                    break;
                }

                case "light-sweep":
                {
                    var color = colors.Color ?? SKColors.White;
                    var progress = (float)((t / Math.Max(2, layer.Period)) % 1.0);
                    var band = (float)layer.Width;
                    var position = (progress * (1 + (band * 2))) - band;
                    var angle = layer.Angle * Math.PI / 180;
                    var span = Math.Max(w, h);
                    var ax = (float)Math.Cos(angle) * span;
                    var ay = (float)Math.Sin(angle) * span;
                    paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(ax, ay),
                        [color.WithAlpha(0), color.WithAlpha((byte)(color.Alpha * layer.Opacity)), color.WithAlpha(0)],
                        [Math.Clamp(position - band, 0, 1), Math.Clamp(position, 0, 1), Math.Clamp(position + band, 0, 1)], SKShaderTileMode.Clamp);
                    paint.BlendMode = SKBlendMode.Plus;
                    canvas.DrawRect(rect, paint);
                    break;
                }

                case "calm-tint":
                case "scrim":
                {
                    // Scrim is a flat bounded colour wash. The old zero-angle gradient had identical
                    // start/end points, which made calm backdrops effectively invisible.
                    var color = colors.Color ?? SKColors.Black;
                    paint.Color = color.WithAlpha((byte)Math.Clamp(color.Alpha * layer.Opacity, 0, 255));
                    canvas.DrawRect(rect, paint);
                    break;
                }

                case "media-tint":
                {
                    if (_owner.MediaTint is not { } tint)
                    {
                        break;
                    }

                    paint.Color = SkiaColor.From(tint, layer.Opacity);
                    paint.BlendMode = SKBlendMode.SoftLight;
                    canvas.DrawRect(rect, paint);
                    break;
                }
            }
        }
    }

    private SKImage? ResolveImage(BackdropLayer layer)
    {
        var source = layer.AssetPath is not null
            ? _images.GetValueOrDefault(layer)
            : _owner.AmbientImage;
        if (source is null)
        {
            return null;
        }

        if (layer.Blur <= 0)
        {
            return source;
        }

        // Blur once and reuse: the animated frame only transforms a pre-blurred image .
        if (!_blurred.TryGetValue(layer, out var blurred) || blurred is null)
        {
            var scale = 0.5f;
            var info = new SKImageInfo(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
            using var surface = SKSurface.Create(info);
            using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur((float)layer.Blur * scale, (float)layer.Blur * scale, SKShaderTileMode.Clamp) };
            surface.Canvas.DrawImage(source, new SKRect(0, 0, info.Width, info.Height), new SKSamplingOptions(SKFilterMode.Linear), paint);
            blurred = surface.Snapshot();
            _blurred[layer] = blurred;
        }

        return blurred;
    }

    private static SKBlendMode Blend(string blend) => blend switch
    {
        "screen" => SKBlendMode.Screen,
        "multiply" => SKBlendMode.Multiply,
        "overlay" => SKBlendMode.Overlay,
        "soft-light" => SKBlendMode.SoftLight,
        "plus" => SKBlendMode.Plus,
        _ => SKBlendMode.SrcOver,
    };

    /// <summary>Pre-rendered tileable texture: grain (per-pixel) or fog (smooth value noise). Built once.</summary>
    private static SKImage CreateNoise(int size, bool grain)
    {
        var random = new Random(grain ? 7 : 11);
        using var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        var grid = 8;
        var lattice = new double[grid + 1, grid + 1];
        for (var y = 0; y <= grid; y++)
        {
            for (var x = 0; x <= grid; x++)
            {
                lattice[x, y] = random.NextDouble();
            }
        }

        for (var y = 0; y < grid; y++)
        {
            lattice[grid, y] = lattice[0, y];
        }

        for (var x = 0; x <= grid; x++)
        {
            lattice[x, grid] = lattice[x, 0];
        }

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                byte value;
                if (grain)
                {
                    value = (byte)random.Next(0, 256);
                    bitmap.SetPixel(x, y, new SKColor(value, value, value, 40));
                }
                else
                {
                    var gx = (double)x / size * grid;
                    var gy = (double)y / size * grid;
                    var x0 = (int)gx;
                    var y0 = (int)gy;
                    var fx = Smooth(gx - x0);
                    var fy = Smooth(gy - y0);
                    var v = Lerp(Lerp(lattice[x0, y0], lattice[x0 + 1, y0], fx), Lerp(lattice[x0, y0 + 1], lattice[x0 + 1, y0 + 1], fx), fy);
                    value = (byte)(Math.Pow(v, 1.6) * 255);
                    bitmap.SetPixel(x, y, new SKColor(255, 255, 255, value));
                }
            }
        }

        return SKImage.FromBitmap(bitmap);

        static double Smooth(double t) => t * t * (3 - (2 * t));

        static double Lerp(double a, double b, double t) => a + ((b - a) * t);
    }

    private readonly record struct Particle(float X, float Y, float Size, float Speed, float Phase);

    private readonly record struct PreparedColors(SKColor? Color, SKColor? Color2, SKColor[]? Stops, float[]? Offsets);
}
