using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Resources;
using Neuterradise.App.SystemServices.Storage;
using Windows.Graphics;
using NeutralRect = Neuterradise.App.SystemServices.Rect;

namespace Neuterradise.App.Ui;

/// <summary>Native-window helpers. Windows owns the caption, drag region and caption buttons.</summary>
public static class WindowChrome
{
    public static IntPtr GetNativeHandle(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return Uno.UI.Xaml.WindowHelper.GetNativeWindow(window)
            is Uno.UI.NativeElementHosting.Win32NativeWindow native ? native.Hwnd : IntPtr.Zero;
    }

    public static void UseNativeTitleBar(Window window)
    {
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            window.AppWindow.TitleBar.ExtendsContentIntoTitleBar = false;
        }
    }

    public static bool IsMaximized(Window window) =>
        window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };

}

/// <summary>
/// The single owner of main-window geometry for the Uno host. Manual window movement and resizing,
/// minimum-size enforcement, saved placement, and multi-monitor recovery stay in the framework-neutral
/// <see cref="WindowGeometry"/>; this adapter converts DIPs to the AppWindow's physical pixels.
/// The adapter also owns the native AppWindow subscription and detaches it deterministically.
/// </summary>
public sealed class UnoWindowPlacement : IDisposable
{
    private readonly AppConfigurationStore? _store;
    private Window? _window;
    private NeutralRect _restoreBounds;
    private bool _restoreBoundsKnown;
    private bool _adjusting;
    private readonly WindowStateProjection _windowState;

    public UnoWindowPlacement(AppConfigurationStore? store, WindowStateProjection? windowState = null)
    {
        _store = store;
        _windowState = windowState ?? new WindowStateProjection();
    }

    private bool IsMaximized => _window is not null && WindowChrome.IsMaximized(_window);

    private double WindowScale
    {
        get
        {
            try
            {
                var handle = _window is null ? IntPtr.Zero : WindowChrome.GetNativeHandle(_window);
                var dpi = handle == IntPtr.Zero ? 0u : GetDpiForWindow(handle);
                return Math.Max(1, (dpi == 0 ? SystemDpi() : dpi) / 96.0);
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
            {
                return Math.Max(1, SystemDpi() / 96.0);
            }
        }
    }

    private NeutralRect CurrentWorkArea()
    {
        var snapshot = MonitorWorkAreas.Capture();
        return _restoreBoundsKnown
            ? WindowGeometry.SelectWorkArea(_restoreBounds, snapshot.WorkAreas, snapshot.PrimaryWorkArea)
            : snapshot.PrimaryWorkArea;
    }

    /// <summary>Applies the saved placement (or a monitor-appropriate default) before the window is shown.</summary>
    public async Task AttachAsync(Window window, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        Detach();
        _window = window;

        WindowPlacementConfiguration? saved = null;
        if (_store is not null)
        {
            try
            {
                saved = (await _store.LoadAsync(cancellationToken).ConfigureAwait(true)).Configuration.WindowPlacement;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceWarning("Saved window placement could not be read: {0}", exception.GetType().Name);
            }
        }

        var snapshot = MonitorWorkAreas.Capture();
        var placement = WindowGeometry.ResolveStartup(saved, snapshot.WorkAreas, snapshot.PrimaryWorkArea);
        SetBounds(placement.Bounds, snapshot.ScaleFor(placement.WorkArea));
        if (placement.IsMaximized && window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        window.AppWindow.Changed += OnChanged;
    }

    /// <summary>Persists the restore rectangle and maximized state; never overwrites an unreadable configuration.</summary>
    public async Task PersistAsync()
    {
        if (_store is null || _window is null || !_restoreBoundsKnown)
        {
            return;
        }

        var placement = new WindowPlacementConfiguration(
            _restoreBounds.Left, _restoreBounds.Top, _restoreBounds.Width, _restoreBounds.Height, IsMaximized);
        try
        {
            var save = await _store.UpdateAsync(
                configuration => configuration with { WindowPlacement = placement }).ConfigureAwait(false);
            if (!save.IsSaved)
            {
                Trace.TraceWarning("Window placement could not be saved: {0}", save.SafeErrorDetail);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Window placement could not be saved: {0}", exception.GetType().Name);
        }
    }

    public void Dispose() => Detach();

    private void Detach()
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        window.AppWindow.Changed -= OnChanged;
        _window = null;
    }

    private void SetBounds(NeutralRect bounds, double? requestedScale = null)
    {
        if (_window is null)
        {
            return;
        }

        var scale = requestedScale ?? WindowScale;
        _adjusting = true;
        try
        {
            _window.AppWindow.MoveAndResize(new RectInt32
            {
                X = (int)Math.Round(bounds.Left * scale),
                Y = (int)Math.Round(bounds.Top * scale),
                Width = (int)Math.Round(bounds.Width * scale),
                Height = (int)Math.Round(bounds.Height * scale),
            });
        }
        finally
        {
            _adjusting = false;
        }

        _restoreBounds = bounds;
        _restoreBoundsKnown = true;
    }

    private void OnChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange)
        {
            var minimized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
            var state = _windowState.SetMinimized(minimized);
            ResourceGovernor.Shared.SetWindowState(state.IsActive, state.IsMinimized);
            if (minimized)
            {
                HoverVideoCoordinator.Shared.StopAll();
            }
        }

        if (_adjusting || sender.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored })
        {
            return;
        }

        if (args.DidSizeChange || args.DidPositionChange)
        {
            var scale = WindowScale;
            var bounds = new NeutralRect(sender.Position.X / scale, sender.Position.Y / scale, sender.Size.Width / scale, sender.Size.Height / scale);

            // 800 × 600 is the supported minimum; smaller drags snap back to it.
            var minimum = WindowGeometry.MinimumFor(CurrentWorkArea());
            if (bounds.Width + 0.5 < minimum.Width || bounds.Height + 0.5 < minimum.Height)
            {
                SetBounds(new NeutralRect(bounds.Left, bounds.Top, Math.Max(bounds.Width, minimum.Width), Math.Max(bounds.Height, minimum.Height)));
                return;
            }

            _restoreBounds = bounds;
            _restoreBoundsKnown = true;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private static uint SystemDpi()
    {
        try
        {
            var dpi = GetDpiForSystem();
            return dpi > 0 ? dpi : 96;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }
}
