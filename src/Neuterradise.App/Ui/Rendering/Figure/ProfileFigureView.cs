using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Resources;

namespace Neuterradise.App.Ui.Figure;

public sealed record FigureSource(Guid MediaId, string? RenderPath, string? SourceSha256, ImageRef? Thumbnail);

/// <summary>Immediate durable Thumbnail with a cancellable, exclusive native scene lease.</summary>
public sealed class ProfileFigureView : Grid, IDisposable
{
    private readonly FigureRuntime? _runtime;
    private readonly Window _window;
    private readonly Grid _viewport = new();
    private readonly StackPanel _controls = UI.V(4);
    private readonly TextBlock _state = UI.WrappedText(string.Empty, "caption", "textSecondary");
    private readonly Button _reset;
    private readonly SkImageView _thumbnail = new();
    private readonly ContentControl _host = new() { Visibility = Visibility.Collapsed };
    private FigureSource? _source;
    private FigureViewportLease? _lease;
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _qualityLoad;
    private int _requestedLod = -1;
    private int _requestedTextureDimension;
    private bool _active;
    private bool _occluded;
    private bool _disposed;
    private FigureCamera _camera = new();
    private int _loadedLod = -1;
    private int _loadedTextureDimension;

    public ProfileFigureView(AppServices services) : this(services.Figures, services.Window) { }

    public ProfileFigureView(FigureRuntime? runtime, Window window)
    {
        _runtime = runtime;
        _window = window;
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        _viewport.Children.Add(_thumbnail);
        _viewport.Children.Add(_host);
        Children.Add(_viewport);
        _reset = UI.Button(
            UI.T("Figure.ResetView", "Reset view"),
            () =>
            {
                _camera = new();
                _lease?.SetCamera(_camera);
            },
            ButtonKind.Ghost,
            "icon.action.restore");
        _reset.IsEnabled = false;
        var gestureHelp = new Neuterradise.App.Ui.VariableWrap(
            6,
            UI.Badge(UI.T("Figure.Control.Rotate", "Rotate · drag")),
            UI.Badge(UI.T("Figure.Control.Pan", "Pan · Shift + drag")),
            UI.Badge(UI.T("Figure.Control.Zoom", "Zoom · Ctrl + wheel")),
            _reset);
        _controls.Children.Add(gestureHelp);
        _controls.Children.Add(_state);
        _controls.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(_controls, 1);
        Children.Add(_controls);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        _viewport.SizeChanged += OnSizeChanged;
        if (runtime is not null) runtime.ResourcesChanged += OnResourcesChanged;
    }

    public bool IsLive => _lease is { IsDisposed: false };
    public FigureViewportLease? ActiveLease => IsLive ? _lease : null;

    public void SetSource(FigureSource? source)
    {
        if (Equals(_source, source)) return;
        Release();
        _source = source;
        _camera = new();
        _thumbnail.Source = source?.Thumbnail;
        Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateControls();
        EnsureLive();
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (!active)
        {
            Release();
            return;
        }

        if (_lease is { IsDisposed: false } lease)
        {
            lease.SetActive(!_occluded && IsLoaded && HasUsableViewport);
            UpdateControls();
            if (lease.IsActive) EnsureQuality();
            return;
        }

        EnsureLive();
    }

    public void SetOccluded(bool occluded)
    {
        if (_occluded == occluded) return;
        _occluded = occluded;

        // XAML dialogs/customization can cover native HWND airspace only when the child HWND is
        // hidden. Keep the lease, swapchain, scene, and camera resident so closing an in-Profile
        // overlay is an instant resume rather than a release/recreate/load cycle.
        if (_lease is { IsDisposed: false } lease)
        {
            lease.SetActive(!occluded && _active && IsLoaded && HasUsableViewport);
            UpdateControls();
            return;
        }

        if (!occluded) EnsureLive();
    }

    private bool HasUsableViewport => ActualWidth >= 200 && _viewport.ActualHeight >= 180;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_lease is { IsDisposed: false } lease)
        {
            lease.SetActive(_active && !_occluded && HasUsableViewport);
            UpdateControls();
            return;
        }
        EnsureLive();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        // Header reflow/reparenting can transiently unload this XAML host while the Profile is still
        // active. Hide the native child but retain the expensive live lease; route suspension and
        // Dispose remain the authorities that actually release it.
        if (_active && _lease is { IsDisposed: false } lease)
        {
            lease.SetActive(false);
            return;
        }
        Release();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_lease is { IsDisposed: false } lease)
        {
            lease.SetActive(_active && !_occluded && IsLoaded && HasUsableViewport);
            UpdateControls();
            return;
        }
        if (HasUsableViewport) EnsureLive();
    }

    private void EnsureLive()
    {
        if (_disposed || !_active || _occluded || !IsLoaded || !HasUsableViewport
            || _load is not null || _lease is { IsDisposed: false }
            || _source is not { RenderPath: not null, SourceSha256: not null } source
            || _runtime is not { IsLiveAllowed: true } runtime || runtime.Snapshot.IsDisposed
            || runtime.Snapshot.ActiveDeviceCount == 0) return;
        var (lod, textureDimension) = DesiredQuality(runtime);
        if (runtime.TryGetCachedScene(
                source.RenderPath!,
                source.SourceSha256!,
                lod,
                textureDimension,
                out var cachedScene))
        {
            try
            {
                _host.Visibility = Visibility.Visible;
                _lease = runtime.Attach(_host, WindowChrome.GetNativeHandle(_window));
                _lease.SetCamera(_camera);
                _lease.SetScene(cachedScene);
                _loadedLod = lod;
                _loadedTextureDimension = textureDimension;
                _lease.CameraChanged += OnCameraChanged;
                UpdateControls();
            }
            catch (Exception exception)
            {
                _lease?.Dispose();
                _lease = null;
                _host.Visibility = Visibility.Collapsed;
                Trace.TraceWarning("Figure cached-scene attach fell back to durable Thumbnail: {0}", exception.Message);
                runtime.RecoverDeviceLoss(exception);
                UpdateControls();
            }
            return;
        }

        _load = new CancellationTokenSource();
        UpdateControls();
        _ = LoadObservedAsync(source, runtime, _load);
    }

    private async Task LoadObservedAsync(FigureSource source, FigureRuntime runtime, CancellationTokenSource request)
    {
        try
        {
            var (lod, textureDimension) = DesiredQuality(runtime);
            var scene = await runtime.LoadSceneAsync(source.RenderPath!, source.SourceSha256!, lod, textureDimension, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (_disposed || !_active || _occluded || !IsLoaded || !ReferenceEquals(_load, request)) return;
            _host.Visibility = Visibility.Visible;
            _lease = runtime.Attach(_host, WindowChrome.GetNativeHandle(_window));
            _lease.SetCamera(_camera);
            _lease.SetScene(scene);
            if (_lease is not { IsDisposed: false } || !ReferenceEquals(_load, request)) return;
            _loadedLod = lod;
            _loadedTextureDimension = textureDimension;
            _lease.CameraChanged += OnCameraChanged;
            UpdateControls();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _lease?.Dispose();
            _lease = null;
            _host.Visibility = Visibility.Collapsed;
            Trace.TraceWarning("Figure remains on durable Thumbnail: {0}", exception.Message);
            runtime.RecoverDeviceLoss(exception);
        }
        finally
        {
            if (ReferenceEquals(_load, request)) _load = null;
            request.Dispose();
            UpdateControls();
        }
    }

    private (int Lod, int TextureDimension) DesiredQuality(FigureRuntime runtime)
    {
        // Keep source-detail geometry at every camera distance. Reduced geometry produced by
        // vertex clustering can be structurally valid yet visibly tear thin or articulated
        // surfaces, which made a Figure appear broken until the camera crossed into LOD0.
        // Resource scaling and close-detail refinement therefore operate on texture residency only.
        if (runtime.Tier == PresentationTier.Reduced) return (0, 1024);

        // Hysteresis keeps wheel motion around the texture boundary from thrashing scene loads.
        // A cold/recreated viewport uses the midpoint. Once high-detail textures are live,
        // crossing back requires a deliberate movement beyond the wider exit threshold.
        var close = _loadedTextureDimension switch
        {
            >= 4096 => _camera.DistanceScale < 0.78f,
            2048 => _camera.DistanceScale < 0.68f,
            _ => _camera.DistanceScale < 0.72f,
        };
        return close ? (0, 4096) : (0, 2048);
    }

    private void OnCameraChanged()
    {
        if (_lease is null) return;
        _camera = _lease.Camera;
        EnsureQuality();
    }

    private void EnsureQuality()
    {
        if (_disposed || !_active || _occluded || !IsLoaded || !HasUsableViewport || _load is not null
            || _lease is not { IsDisposed: false } lease
            || _source is not { RenderPath: not null, SourceSha256: not null } source
            || _runtime is not { IsLiveAllowed: true } runtime || runtime.Snapshot.IsDisposed) return;

        var (lod, textureDimension) = DesiredQuality(runtime);
        if (_loadedLod == lod && _loadedTextureDimension == textureDimension)
        {
            _qualityLoad?.Cancel();
            return;
        }
        if (_qualityLoad is not null && _requestedLod == lod && _requestedTextureDimension == textureDimension) return;

        _qualityLoad?.Cancel();
        var request = new CancellationTokenSource();
        _qualityLoad = request;
        _requestedLod = lod;
        _requestedTextureDimension = textureDimension;
        _ = SwapQualityObservedAsync(source, runtime, lease, lod, textureDimension, request);
    }

    private async Task SwapQualityObservedAsync(
        FigureSource source,
        FigureRuntime runtime,
        FigureViewportLease lease,
        int lod,
        int textureDimension,
        CancellationTokenSource request)
    {
        try
        {
            var scene = await runtime.LoadSceneAsync(
                source.RenderPath!, source.SourceSha256!, lod, textureDimension, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (_disposed || !_active || _occluded || !IsLoaded
                || !ReferenceEquals(_qualityLoad, request)
                || !ReferenceEquals(_lease, lease) || lease.IsDisposed
                || !Equals(_source, source)) return;

            // FigureRenderer.Upload builds the replacement GPU scene first and only then swaps
            // renderer authority, so the existing live scene remains visible until this succeeds.
            lease.SetScene(scene);
            _loadedLod = lod;
            _loadedTextureDimension = textureDimension;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Trace.TraceWarning("Figure quality swap kept the current live scene: {0}", exception.Message);
            if (runtime.RecoverDeviceLoss(exception))
            {
                Release();
                EnsureLive();
            }
        }
        finally
        {
            if (ReferenceEquals(_qualityLoad, request))
            {
                _qualityLoad = null;
                _requestedLod = -1;
                _requestedTextureDimension = 0;
            }
            request.Dispose();
        }
    }

    private void OnResourcesChanged()
    {
        if (_disposed || _runtime is not { } runtime) return;
        if (!runtime.IsLiveAllowed)
        {
            Release();
            return;
        }
        if (IsLive) EnsureQuality();
        else EnsureLive();
    }

    private void Release()
    {
        _load?.Cancel();
        _load = null;
        _qualityLoad?.Cancel();
        _qualityLoad = null;
        _requestedLod = -1;
        _requestedTextureDimension = 0;
        if (_lease is { } lease)
        {
            _camera = lease.Camera;
            lease.CameraChanged -= OnCameraChanged;
            lease.Dispose();
        }
        _lease = null;
        _loadedLod = -1;
        _loadedTextureDimension = 0;
        _host.Visibility = Visibility.Collapsed;
        UpdateControls();
    }

    private void UpdateControls()
    {
        _reset.IsEnabled = _lease is { IsDisposed: false, IsActive: true };
        _state.Text = IsLive ? string.Empty : _load is not null
            ? UI.T("Figure.Loading", "Loading 3D...")
            : UI.T("Figure.ThumbnailFallback", "Preview image - live 3D is unavailable");
        _state.Visibility = IsLive ? Visibility.Collapsed : Visibility.Visible;
    }

    public new void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;
        _viewport.SizeChanged -= OnSizeChanged;
        if (_runtime is { } runtime) runtime.ResourcesChanged -= OnResourcesChanged;
        _thumbnail.Source = null;
    }
}
