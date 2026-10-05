using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Uno.UI.NativeElementHosting;
using Neuterradise.App.Ui.Figure;
using Neuterradise.App.SystemServices.Resources;

namespace Neuterradise.App.Ui;

public sealed record FigureRuntimeSnapshot(
    int DeviceCreateCount,
    int ViewportCreateCount,
    int ViewportReleaseCount,
    int ActiveViewportCount,
    int ActiveSwapChainCount,
    long PresentCount,
    int BackBufferWidth,
    int BackBufferHeight,
    bool IsDisposed,
    string? LastError,
    int ActiveDeviceCount,
    int CachedSceneCount,
    long CachedSceneBytes);

/// <summary>App-owned shared device and exclusive native viewport lifetime, independent of Vault persistence.</summary>
public sealed class FigureRuntime : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly FigureNative.WindowProcedure _procedure;
    private readonly string _className = "NautFigureViewport_" + Guid.NewGuid().ToString("N");
    private readonly IntPtr _instance = FigureNative.GetModuleHandleW(null);
    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _factory;
    private bool _registered;
    private bool _disposed;
    private FigureViewportLease? _viewport;
    private int _deviceCreates;
    private int _viewportCreates;
    private int _viewportReleases;
    private long _presents;
    private string? _lastError;
    private FigureRenderer? _renderer;
    private readonly ResourceGovernor _governor;
    private readonly ThemeRuntime _theme = ThemeRuntime.Current;
    private bool _deviceRecoveryAttempted;
    private PresentationTier _appliedTier;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _loadSync = new();
    private readonly HashSet<Task<FigureSceneData>> _loads = [];
    private const int SceneCacheEntryLimit = 2;
    // A validated live scene must also fit the warm cache; otherwise source-detail models can
    // render within budget yet always pay a cold read on Profile re-entry.
    private const long SceneCacheByteLimit = FigureSceneData.CpuBudget;
    private readonly object _sceneCacheSync = new();
    private readonly Dictionary<FigureSceneCacheKey, LinkedListNode<FigureSceneCacheEntry>> _sceneCache = [];
    private readonly LinkedList<FigureSceneCacheEntry> _sceneLru = [];
    private long _sceneCacheBytes;

    private readonly record struct FigureSceneCacheKey(
        string Path, string SourceSha256, int Lod, int TextureDimension, long Length, long LastWriteTicks);

    private sealed record FigureSceneCacheEntry(FigureSceneCacheKey Key, FigureSceneData Scene);

    public Task<FigureSceneData> LoadSceneAsync(string path, string sourceSha256, int lod, int textureDimension, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var task = Task.Run(async () =>
        {
            request.Token.ThrowIfCancellationRequested();
            var key = CreateSceneCacheKey(path, sourceSha256, lod, textureDimension);
            if (TryGetCachedScene(key, out var cached)) return cached;

            using var permit = await _governor.AcquireAsync(ResourceClass.CpuHeavy, request.Token);
            FigureSceneData scene;
            try
            {
                scene = await FigureSceneData.LoadAsync(
                    key.Path, sourceSha256, lod, request.Token, textureDimension).ConfigureAwait(false);
            }
            catch (InvalidDataException exception) when (textureDimension > 2048
                && exception.Message.Contains("resident allocation budget", StringComparison.Ordinal))
            {
                scene = await FigureSceneData.LoadAsync(
                    key.Path, sourceSha256, lod, request.Token, 2048).ConfigureAwait(false);
            }

            StoreCachedScene(key, scene);
            return scene;
        }, request.Token);
        lock (_loadSync) _loads.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (_loadSync) _loads.Remove(task);
            request.Dispose();
            _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    public async Task PrewarmSceneAsync(string path, string sourceSha256, CancellationToken ct = default)
    {
        if (_disposed || !IsLiveAllowed) return;

        // Geometry fidelity is stable across camera distance. The prepared reduced meshes are not
        // trusted as presentation authority because structurally valid clustering can still create
        // visible surface loss on real human scans. Resource scaling therefore changes texture
        // residency only; the live Figure always uses source-detail LOD0 geometry.
        var textureDimension = Tier == PresentationTier.Reduced ? 1024 : 2048;
        _ = await LoadSceneAsync(path, sourceSha256, 0, textureDimension, ct).ConfigureAwait(false);
    }

    public bool TryGetCachedScene(
        string path,
        string sourceSha256,
        int lod,
        int textureDimension,
        out FigureSceneData scene)
    {
        scene = null!;
        if (_disposed) return false;
        try
        {
            return TryGetCachedScene(
                CreateSceneCacheKey(path, sourceSha256, lod, textureDimension),
                out scene);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static FigureSceneCacheKey CreateSceneCacheKey(
        string path, string sourceSha256, int lod, int textureDimension)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Prepared Figure scene is missing.", fullPath);
        return new(
            fullPath,
            sourceSha256.Trim().ToLowerInvariant(),
            lod,
            textureDimension,
            info.Length,
            info.LastWriteTimeUtc.Ticks);
    }

    private bool TryGetCachedScene(FigureSceneCacheKey key, out FigureSceneData scene)
    {
        lock (_sceneCacheSync)
        {
            if (!_sceneCache.TryGetValue(key, out var node))
            {
                scene = null!;
                return false;
            }

            _sceneLru.Remove(node);
            _sceneLru.AddFirst(node);
            scene = node.Value.Scene;
            return true;
        }
    }

    private void StoreCachedScene(FigureSceneCacheKey key, FigureSceneData scene)
    {
        if (scene.ByteLength <= 0 || scene.ByteLength > SceneCacheByteLimit) return;
        lock (_sceneCacheSync)
        {
            if (_disposed) return;
            if (_sceneCache.TryGetValue(key, out var existing))
            {
                _sceneCacheBytes -= existing.Value.Scene.ByteLength;
                _sceneLru.Remove(existing);
                _sceneCache.Remove(key);
            }

            var node = new LinkedListNode<FigureSceneCacheEntry>(new(key, scene));
            _sceneLru.AddFirst(node);
            _sceneCache[key] = node;
            _sceneCacheBytes = checked(_sceneCacheBytes + scene.ByteLength);

            while (_sceneCache.Count > SceneCacheEntryLimit || _sceneCacheBytes > SceneCacheByteLimit)
            {
                var last = _sceneLru.Last;
                if (last is null) break;
                _sceneLru.RemoveLast();
                _sceneCache.Remove(last.Value.Key);
                _sceneCacheBytes -= last.Value.Scene.ByteLength;
            }
        }
    }

    private void ClearSceneCache()
    {
        lock (_sceneCacheSync)
        {
            _sceneCache.Clear();
            _sceneLru.Clear();
            _sceneCacheBytes = 0;
        }
    }

    public async Task DrainAsync()
    {
        Task<FigureSceneData>[] tasks;
        lock (_loadSync) tasks = _loads.ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception) { /* Scene faults already produce a Thumbnail fallback; retirement must still drain file handles. */ }
    }

    public event Action? ResourcesChanged;
    public PresentationTier Tier => _governor.IsMemoryPressure ? PresentationTier.Fallback : _governor.Tier;
    public bool IsLiveAllowed => Tier is PresentationTier.Full or PresentationTier.Reduced;

    public FigureRenderer? Renderer => _renderer;

    internal FigureRenderer EnsureRenderer()
    {
        EnsureThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer ??= new FigureRenderer(_device, _context);
    }

    internal void Upload(FigureSceneData scene)
    {
        EnsureThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureRenderer().Upload(scene);
    }

    internal void RenderScene(IntPtr target, int width, int height, FigureCamera? camera = null)
    {
        if (_renderer is not { } renderer) return;
        var accent = ThemeRuntime.Current.Color("accent");
        renderer.SetStageAccent(new System.Numerics.Vector3(accent.R / 255f, accent.G / 255f, accent.B / 255f));
        renderer.Render(target, width, height, camera, reduced: Tier == PresentationTier.Reduced);
    }

    public FigureRuntime(ResourceGovernor? governor = null)
    {
        _governor = governor ?? ResourceGovernor.Shared;
        _appliedTier = Tier;
        _procedure = WindowProcedure;
        try
        {
            var windowClass = new FigureNative.WindowClass
            {
                Size = (uint)Marshal.SizeOf<FigureNative.WindowClass>(),
                Style = 0x0008,
                Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                Instance = _instance,
                ClassName = _className,
            };
            if (FigureNative.RegisterClassExW(ref windowClass) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _registered = true;
            CreateHardware();
            _governor.Changed += OnResourcesChanged;
            _governor.TrimRequested += OnTrimRequested;
            _theme.Changed += OnThemeChanged;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void CreateHardware()
    {
        FigureNative.Check(FigureNative.D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0,
            [0xB000, 0xA100], 2, 7, out _device, out _, out _context));
        _deviceCreates++;
        var iid = FigureNative.Factory1;
        FigureNative.Check(FigureNative.CreateDXGIFactory1(ref iid, out _factory));
    }

    private void OnResourcesChanged(object? sender, EventArgs args) => UiDispatch.Run(() =>
    {
        if (_disposed) return;
        if (_appliedTier == Tier) return;
        _appliedTier = Tier;
        _viewport?.SetResourceActive(IsLiveAllowed);
        ResourcesChanged?.Invoke();
    });

    private void OnThemeChanged() => UiDispatch.Run(() =>
    {
        if (!_disposed) _viewport?.RenderClear();
    });

    private void OnTrimRequested(object? sender, EventArgs args) => UiDispatch.Run(() =>
    {
        if (_disposed) return;
        ClearSceneCache();
        _viewport?.SetResourceActive(false);
        ResourcesChanged?.Invoke();
        if (_viewport is null) _renderer?.ClearScene();
    });

    public bool RecoverDeviceLoss(Exception exception)
    {
        EnsureThread();
        if (_disposed || (uint)exception.HResult is not (0x887A0005 or 0x887A0006 or 0x887A0007)) return false;
        _viewport?.Dispose();
        ReleaseHardware();
        if (!_deviceRecoveryAttempted)
        {
            _deviceRecoveryAttempted = true;
            try { CreateHardware(); _lastError = null; }
            catch (Exception failure) { ReleaseHardware(); Failed(failure); }
        }
        ResourcesChanged?.Invoke();
        return _device != IntPtr.Zero;
    }

    private void ReleaseHardware()
    {
        if (_context != IntPtr.Zero)
            FigureNative.Method<FigureNative.ContextOperation>(_context, 110)(_context);
        _renderer?.Dispose();
        _renderer = null;
        FigureNative.Release(ref _factory);
        FigureNative.Release(ref _context);
        FigureNative.Release(ref _device);
    }

    public FigureRuntimeSnapshot Snapshot
    {
        get
        {
            int cachedCount;
            long cachedBytes;
            lock (_sceneCacheSync)
            {
                cachedCount = _sceneCache.Count;
                cachedBytes = _sceneCacheBytes;
            }

            return new(
                _deviceCreates, _viewportCreates, _viewportReleases,
                _viewport is null ? 0 : 1, _viewport?.HasSwapChain == true ? 1 : 0,
                _presents, _viewport?.Width ?? 0, _viewport?.Height ?? 0, _disposed, _lastError,
                _device == IntPtr.Zero ? 0 : 1, cachedCount, cachedBytes);
        }
    }

    public FigureViewportLease Attach(ContentControl host, IntPtr parentHwnd)
    {
        EnsureThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_device == IntPtr.Zero || !IsLiveAllowed) throw new InvalidOperationException("Figure hardware is suspended or unavailable.");
        ArgumentNullException.ThrowIfNull(host);
        if (parentHwnd == IntPtr.Zero || !FigureNative.IsWindow(parentHwnd))
            throw new ArgumentException("An attached native parent window is required.", nameof(parentHwnd));
        _viewport?.Dispose();
        if (host.Content is not null) throw new InvalidOperationException("The native viewport host already contains content.");
        var viewport = new FigureViewportLease(this, host, parentHwnd);
        _viewport = viewport;
        try
        {
            viewport.Initialize(_className, _instance);
            _viewportCreates++;
            viewport.Counted = true;
            return viewport;
        }
        catch
        {
            viewport.Dispose();
            throw;
        }
    }

    internal IntPtr Device => _device;
    internal IntPtr Context => _context;
    internal IntPtr Factory => _factory;

    internal void EnsureThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Native Figure resources must be used on their owning UI thread.");
    }

    internal void Presented() => _presents++;
    internal void Failed(Exception exception) => _lastError = exception.GetType().Name + ": " + exception.Message;

    internal void Released(FigureViewportLease viewport)
    {
        if (viewport.Counted) _viewportReleases++;
        if (ReferenceEquals(_viewport, viewport))
        {
            _viewport = null;
            _renderer?.ClearScene();
        }
    }

    private IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (_viewport is { } viewport && viewport.Hwnd == hwnd)
            {
                if (message == FigureNative.Size)
                {
                    viewport.ResizeToWindow();
                    return IntPtr.Zero;
                }
                if (message == FigureNative.Paint)
                {
                    viewport.RenderClear();
                    FigureNative.ValidateRect(hwnd, IntPtr.Zero);
                    return IntPtr.Zero;
                }
                if (message == FigureNative.EraseBackground) return new IntPtr(1);
                if (message == FigureNative.Show)
                {
                    if (wParam != IntPtr.Zero) viewport.RenderClear();
                }
                if (message == FigureNative.MouseWheel)
                {
                    var state = wParam.ToInt64();
                    var delta = (short)(state >> 16);
                    if ((state & FigureNative.MouseControl) != 0) viewport.Zoom(delta);
                    else viewport.ScrollProfile(delta);
                    return IntPtr.Zero;
                }
                if (message == FigureNative.LeftDown)
                {
                    viewport.BeginGesture(lParam, (wParam.ToInt64() & FigureNative.MouseShift) != 0);
                    return IntPtr.Zero;
                }
                if (message == FigureNative.MouseMove)
                {
                    if ((wParam.ToInt64() & FigureNative.MouseLeftButton) != 0) viewport.MoveGesture(lParam);
                    else viewport.EndGesture();
                    return IntPtr.Zero;
                }
                if (message == FigureNative.CaptureChanged) { viewport.CancelGesture(); return IntPtr.Zero; }
                if (message == FigureNative.CancelMode) { viewport.EndGesture(); return IntPtr.Zero; }
                if (message == FigureNative.LeftUp) { viewport.EndGesture(); return IntPtr.Zero; }
                if (message == FigureNative.LeftDoubleClick) { viewport.SetCamera(new()); return IntPtr.Zero; }
            }
        }
        catch (Exception exception)
        {
            // Exceptions cannot unwind through the native window procedure.
            Failed(exception);
            if (!RecoverDeviceLoss(exception)) _viewport?.SuspendAfterFailure();
        }
        return FigureNative.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        EnsureThread();
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        ClearSceneCache();
        _governor.Changed -= OnResourcesChanged;
        _governor.TrimRequested -= OnTrimRequested;
        _theme.Changed -= OnThemeChanged;
        _viewport?.Dispose();
        _renderer?.Dispose();
        _renderer = null;
        if (_context != IntPtr.Zero)
        {
            FigureNative.Method<FigureNative.ContextOperation>(_context, 110)(_context);
            FigureNative.Method<FigureNative.ContextOperation>(_context, 111)(_context);
        }
        FigureNative.Release(ref _factory);
        FigureNative.Release(ref _context);
        FigureNative.Release(ref _device);
        if (_registered)
        {
            FigureNative.UnregisterClassW(_className, _instance);
            _registered = false;
        }
        GC.KeepAlive(_procedure);
    }
}

/// <summary>An exclusive viewport lease; release detaches Uno content before native resources and HWND destruction.</summary>
public sealed class FigureViewportLease : IDisposable
{
    private static float[] ClearColor
    {
        get
        {
            var canvas = ThemeRuntime.Current.Color("canvas");
            static float Linear(byte value)
            {
                var component = value / 255f;
                return component <= 0.04045f ? component / 12.92f : MathF.Pow((component + 0.055f) / 1.055f, 2.4f);
            }
            return [Linear(canvas.R), Linear(canvas.G), Linear(canvas.B), 1f];
        }
    }
    private readonly FigureRuntime _runtime;
    private readonly ContentControl _host;
    private Win32NativeWindow? _nativeContent;
    private IntPtr _swapChain;
    private IntPtr _target;
    private bool _disposed;
    private bool _active = true;
    private long _visibilitySubscription;
    private bool _hasScene;
    private bool _resourceActive = true;
    private FigureGesture _gesture;
    private int _dragX, _dragY;

    private enum FigureGesture
    {
        None,
        Orbit,
        Pan,
    }

    public FigureCamera Camera { get; private set; } = new();
    public event Action? CameraChanged;

    internal FigureViewportLease(FigureRuntime runtime, ContentControl host, IntPtr parent)
    {
        _runtime = runtime;
        _host = host;
        ParentHwnd = parent;
    }

    public IntPtr Hwnd { get; private set; }
    public IntPtr ParentHwnd { get; }
    public bool IsDisposed => _disposed;
    public bool IsActive => _active && _resourceActive && !_disposed;
    internal int Width { get; private set; }
    internal int Height { get; private set; }
    internal bool HasSwapChain => _swapChain != IntPtr.Zero;
    internal bool Counted { get; set; }

    public void SetScene(FigureSceneData scene)
    {
        _runtime.EnsureThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _runtime.Upload(scene);
        _hasScene = true;
        RenderClear();
    }

    public uint ReadbackPixel(int x, int y)
    {
        _runtime.EnsureThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var renderer = _runtime.EnsureRenderer();
        FigureNative.Method<FigureNative.ClearTarget>(_runtime.Context, 50)(_runtime.Context, _target, ClearColor);
        _runtime.RenderScene(_target, Width, Height, Camera);
        return renderer.ReadbackPixel(_target, Width, Height, x, y);
    }

    internal void Initialize(string className, IntPtr instance)
    {
        Hwnd = FigureNative.CreateWindowExW(0, className, string.Empty,
            FigureNative.Child | FigureNative.ClipSiblings | FigureNative.ClipChildren,
            0, 0, 256, 256, ParentHwnd, IntPtr.Zero, instance, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var description = new FigureNative.SwapChainDescription
        {
            Buffer = new FigureNative.ModeDescription { Width = 256, Height = 256, Format = 87 },
            Sample = new FigureNative.SampleDescription { Count = 1 },
            BufferUsage = 0x20,
            BufferCount = 2,
            OutputWindow = Hwnd,
            Windowed = 1,
            SwapEffect = 4,
        };
        FigureNative.Check(FigureNative.Method<FigureNative.CreateSwapChain>(_runtime.Factory, 10)(
            _runtime.Factory, _runtime.Device, ref description, out _swapChain));
        Width = Height = 256;
        CreateTarget();
        _nativeContent = new Win32NativeWindow(Hwnd);
        _host.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _host.VerticalContentAlignment = VerticalAlignment.Stretch;
        _host.Unloaded += OnUnloaded;
        _host.Loaded += OnLoaded;
        _visibilitySubscription = _host.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, OnVisibilityChanged);
        _host.Content = _nativeContent;
        ResizeToWindow();
        RenderClear();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_disposed) return;
        ResizeToWindow();
        FigureNative.ShowWindow(Hwnd, IsActive && _host.Visibility == Visibility.Visible ? 5 : 0);
        RenderClear();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_disposed) return;
        EndGesture();
        if (Hwnd != IntPtr.Zero) FigureNative.ShowWindow(Hwnd, 0);
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        if (_disposed || Hwnd == IntPtr.Zero) return;
        FigureNative.ShowWindow(
            Hwnd,
            IsActive && _host.IsLoaded && _host.Visibility == Visibility.Visible ? 5 : 0);
        if (_host.Visibility == Visibility.Visible) RenderClear();
    }

    public void SetActive(bool active)
    {
        _runtime.EnsureThread();
        if (_disposed) return;
        _active = active;
        if (!IsActive) EndGesture();
        FigureNative.ShowWindow(
            Hwnd,
            IsActive && _host.IsLoaded && _host.Visibility == Visibility.Visible ? 5 : 0);
        if (active) RenderClear();
    }

    internal void SetResourceActive(bool active)
    {
        _resourceActive = active;
        SetActive(_active);
    }

    public void SetCamera(FigureCamera camera)
    {
        _runtime.EnsureThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(camera.Yaw) || !float.IsFinite(camera.Pitch) || !float.IsFinite(camera.DistanceScale)
            || !float.IsFinite(camera.TargetOffsetX) || !float.IsFinite(camera.TargetOffsetY))
            throw new ArgumentException("Figure camera values must be finite.", nameof(camera));
        var next = camera with
        {
            Pitch = Math.Clamp(camera.Pitch, 0.02f, 1.2f),
            DistanceScale = Math.Clamp(camera.DistanceScale, 0.35f, 3f),
            TargetOffsetX = Math.Clamp(camera.TargetOffsetX, -1.5f, 1.5f),
            TargetOffsetY = Math.Clamp(camera.TargetOffsetY, -1.5f, 1.5f),
        };
        if (next == Camera) return;
        Camera = next;
        CameraChanged?.Invoke();
        RenderClear();
    }

    internal void BeginGesture(IntPtr position, bool pan)
    {
        if (!IsActive) return;
        _gesture = pan ? FigureGesture.Pan : FigureGesture.Orbit;
        _dragX = (short)position.ToInt64();
        _dragY = (short)(position.ToInt64() >> 16);
        FigureNative.SetCapture(Hwnd);
    }

    internal void MoveGesture(IntPtr position)
    {
        if (_gesture == FigureGesture.None || !IsActive) return;
        var x = (short)position.ToInt64();
        var y = (short)(position.ToInt64() >> 16);
        var dx = x - _dragX;
        var dy = y - _dragY;
        ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Pointer);
        if (_gesture == FigureGesture.Pan)
        {
            var viewport = Math.Max(1, Math.Min(Width, Height));
            var screenScale = 2.2f * Camera.DistanceScale / viewport;
            SetCamera(Camera with
            {
                TargetOffsetX = Camera.TargetOffsetX - dx * screenScale,
                TargetOffsetY = Camera.TargetOffsetY + dy * screenScale,
            });
        }
        else
        {
            SetCamera(Camera with
            {
                Yaw = MathF.IEEERemainder(Camera.Yaw + dx * 0.012f, MathF.Tau),
                Pitch = Camera.Pitch + dy * 0.008f,
            });
        }
        _dragX = x;
        _dragY = y;
    }

    internal void EndGesture()
    {
        if (_gesture == FigureGesture.None) return;
        _gesture = FigureGesture.None;
        FigureNative.ReleaseCapture();
    }

    internal void CancelGesture() => _gesture = FigureGesture.None;

    internal void Zoom(int delta)
    {
        if (!IsActive || delta == 0) return;
        ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Pointer);
        SetCamera(Camera with { DistanceScale = Camera.DistanceScale * MathF.Pow(0.85f, delta / 120f) });
    }

    internal void SuspendAfterFailure()
    {
        EndGesture();
        _active = false;
        if (Hwnd != IntPtr.Zero) FigureNative.ShowWindow(Hwnd, 0);
    }

    internal void ScrollProfile(int delta)
    {
        _runtime.EnsureThread();
        if (_disposed || delta == 0) return;
        ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Scroll);
        DependencyObject? parent = _host;
        while ((parent = VisualTreeHelper.GetParent(parent)) is not null)
        {
            if (parent is not ScrollViewer scroll) continue;
            FigureNative.SystemParametersInfo(0x0068, 0, out var lines, 0);
            var distance = lines == uint.MaxValue ? scroll.ViewportHeight : Math.Min(lines, 100) * 40;
            scroll.ChangeView(null, Math.Clamp(scroll.VerticalOffset - delta / 120.0 * distance,
                0, scroll.ScrollableHeight), null, true);
            return;
        }
    }

    internal void ResizeToWindow()
    {
        if (_disposed || _swapChain == IntPtr.Zero || !FigureNative.GetClientRect(Hwnd, out var rect)) return;
        var width = Math.Max(0, rect.Right - rect.Left);
        var height = Math.Max(0, rect.Bottom - rect.Top);
        if (width == 0 || height == 0 || (width == Width && height == Height)) return;
        FigureNative.Method<FigureNative.ContextOperation>(_runtime.Context, 110)(_runtime.Context);
        FigureNative.Release(ref _target);
        FigureNative.Check(FigureNative.Method<FigureNative.ResizeBuffers>(_swapChain, 13)(
            _swapChain, 2, (uint)width, (uint)height, 87, 0));
        Width = width;
        Height = height;
        CreateTarget();
        RenderClear();
    }

    private void CreateTarget()
    {
        var iid = FigureNative.Texture2D;
        FigureNative.Check(FigureNative.Method<FigureNative.GetBuffer>(_swapChain, 9)(_swapChain, 0, ref iid, out var buffer));
        try
        {
            // The swapchain remains BGRA UNORM; the sRGB view blends linear shader output before encoding.
            var description = new FigureNative.RenderTargetDescription { Format = 91, Dimension = 4 };
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FigureNative.RenderTargetDescription>());
            try
            {
                Marshal.StructureToPtr(description, pointer, false);
                FigureNative.Check(FigureNative.Method<FigureNative.CreateRenderTarget>(_runtime.Device, 9)(
                    _runtime.Device, buffer, pointer, out _target));
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        finally { FigureNative.Release(ref buffer); }
    }

    public void RenderClear()
    {
        _runtime.EnsureThread();
        if (_disposed || !IsActive || !_runtime.IsLiveAllowed || _host.Visibility != Visibility.Visible || _target == IntPtr.Zero
            || !FigureNative.IsWindowVisible(Hwnd) || FigureNative.IsIconic(ParentHwnd)) return;
        try
        {
            FigureNative.Method<FigureNative.ClearTarget>(_runtime.Context, 50)(_runtime.Context, _target, ClearColor);
            if (_hasScene) _runtime.RenderScene(_target, Width, Height, Camera);
            FigureNative.Check(FigureNative.Method<FigureNative.Present>(_swapChain, 8)(_swapChain, 0, 0));
            _runtime.Presented();
        }
        catch (Exception exception)
        {
            _runtime.Failed(exception);
            if (!_runtime.RecoverDeviceLoss(exception)) SuspendAfterFailure();
        }
    }

    public void Dispose()
    {
        _runtime.EnsureThread();
        if (_disposed) return;
        EndGesture();
        _disposed = true;
        _host.Unloaded -= OnUnloaded;
        _host.Loaded -= OnLoaded;
        if (_nativeContent is not null)
        {
            _host.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _visibilitySubscription);
            if (ReferenceEquals(_host.Content, _nativeContent)) _host.Content = null;
            _nativeContent = null;
        }
        FigureNative.Method<FigureNative.ContextOperation>(_runtime.Context, 110)(_runtime.Context);
        FigureNative.Release(ref _target);
        FigureNative.Release(ref _swapChain);
        var hwnd = Hwnd;
        Hwnd = IntPtr.Zero;
        if (hwnd != IntPtr.Zero) FigureNative.DestroyWindow(hwnd);
        _runtime.Released(this);
    }
}
