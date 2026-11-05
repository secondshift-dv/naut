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
    private IntPtr _nativeHandle;
    private readonly SubclassProcedure _subclass;

    public UnoWindowPlacement(AppConfigurationStore? store, WindowStateProjection? windowState = null)
    {
        _store = store;
        _windowState = windowState ?? new WindowStateProjection();
        _subclass = OnNativeMessage;
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
        _nativeHandle = WindowChrome.GetNativeHandle(window);
        if (_nativeHandle == IntPtr.Zero || !SetWindowSubclass(_nativeHandle, _subclass, 1, 0))
            throw new InvalidOperationException("The main window resize constraint could not be installed.");

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
        if (_nativeHandle != IntPtr.Zero) RemoveWindowSubclass(_nativeHandle, _subclass, 1);
        _nativeHandle = IntPtr.Zero;
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

        if (args.DidSizeChange || args.DidPositionChange)
        {
            ReconcileBounds(sender);
        }
    }

    private IntPtr OnNativeMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == 0x02E0 && lParam != IntPtr.Zero) // WM_DPICHANGED supplies an outer rectangle in the new DPI.
        {
            var suggested = Marshal.PtrToStructure<NativeRect>(lParam);
            var scale = ((long)wParam & 0xffff) / 96.0;
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (scale > 0 && GetMonitorInfo(MonitorFromRect(ref suggested, 2), ref monitor))
            {
                var width = Math.Min(monitor.Work.Right - monitor.Work.Left,
                    Math.Max(suggested.Right - suggested.Left, (int)Math.Ceiling(WindowGeometry.MinimumWidth * scale)));
                var height = Math.Min(monitor.Work.Bottom - monitor.Work.Top,
                    Math.Max(suggested.Bottom - suggested.Top, (int)Math.Ceiling(WindowGeometry.MinimumHeight * scale)));
                suggested.Left = Math.Clamp(suggested.Left, monitor.Work.Left, monitor.Work.Right - width);
                suggested.Top = Math.Clamp(suggested.Top, monitor.Work.Top, monitor.Work.Bottom - height);
                suggested.Right = suggested.Left + width;
                suggested.Bottom = suggested.Top + height;
                Marshal.StructureToPtr(suggested, lParam, false);
            }
        }
        var result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == 0x0024 && lParam != IntPtr.Zero) // WM_GETMINMAXINFO constrains all native resize edges.
        {
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var dpi = GetDpiForWindow(hwnd);
            var scale = (dpi == 0 ? 96 : dpi) / 96.0;
            var width = (int)Math.Ceiling(WindowGeometry.MinimumWidth * scale);
            var height = (int)Math.Ceiling(WindowGeometry.MinimumHeight * scale);
            if (GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor))
            {
                width = Math.Min(width, monitor.Work.Right - monitor.Work.Left);
                height = Math.Min(height, monitor.Work.Bottom - monitor.Work.Top);
            }
            limits.MinimumTrack.X = Math.Max(limits.MinimumTrack.X, width);
            limits.MinimumTrack.Y = Math.Max(limits.MinimumTrack.Y, height);
            Marshal.StructureToPtr(limits, lParam, false);
        }
        return result;
    }

    private void ReconcileBounds(AppWindow sender)
    {
        if (_adjusting || sender.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored })
        {
            return;
        }

        {
            var scale = WindowScale;
            if (!GetWindowRect(_nativeHandle, out var nativeBounds)) return;
            var bounds = new NeutralRect(nativeBounds.Left / scale, nativeBounds.Top / scale,
                (nativeBounds.Right - nativeBounds.Left) / scale, (nativeBounds.Bottom - nativeBounds.Top) / scale);

            // Clamp in DIPs against the current monitor before converting to physical pixels.
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaximumSize, MaximumPosition, MinimumTrack, MaximumTrack;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate IntPtr SubclassProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProcedure callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProcedure callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

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
