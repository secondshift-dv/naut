using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Neuterradise.App.Shell;

/// <summary>
/// Reads the work area (monitor minus taskbar and docked bars) of every connected monitor, in
/// device-independent units. The primary work area alone is not enough to restore a window that
/// lives on a secondary monitor.
/// </summary>
public static class MonitorWorkAreas
{
    private const uint MonitorInfoPrimary = 1;

    public static MonitorSnapshot Capture()
    {
        var fallback = PrimaryWorkAreaFallback();

        try
        {
            var areas = new List<Rect>();
            var scales = new Dictionary<Rect, double>();
            Rect? primary = null;

            bool Callback(IntPtr monitor, IntPtr hdc, ref NativeRect bounds, IntPtr data)
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var dpi = MonitorDpi(monitor);
                    var work = DpiGeometry.PixelsToDips(
                        new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top),
                        dpi);
                    areas.Add(work);
                    scales[work] = dpi / 96.0;
                    if ((info.Flags & MonitorInfoPrimary) != 0)
                    {
                        primary = work;
                    }
                }

                return true;
            }

            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero) || areas.Count == 0)
            {
                return new MonitorSnapshot([fallback], fallback);
            }

            return new MonitorSnapshot(areas, primary ?? areas[0], scales);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            Trace.TraceWarning("Monitor enumeration failed; using the primary work area: {0}", exception.GetType().Name);
            return new MonitorSnapshot([fallback], fallback);
        }
    }

    private static Rect PrimaryWorkAreaFallback()
    {
        try
        {
            var native = default(NativeRect);
            if (SystemParametersInfo(SpiGetWorkArea, 0, ref native, 0))
            {
                var scale = 96.0 / SystemDpi();
                return new Rect(native.Left * scale, native.Top * scale, (native.Right - native.Left) * scale, (native.Bottom - native.Top) * scale);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return new Rect(0, 0, 1280, 720);
    }

    private const uint SpiGetWorkArea = 0x0030;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref NativeRect value, uint winIni);

    private static double SystemDpi()
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

    private static uint MonitorDpi(IntPtr monitor)
    {
        try
        {
            return GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0
                ? x
                : (uint)SystemDpi();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return (uint)SystemDpi();
        }
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref NativeRect bounds, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}

public sealed record MonitorSnapshot(
    IReadOnlyList<Rect> WorkAreas,
    Rect PrimaryWorkArea,
    IReadOnlyDictionary<Rect, double>? ScaleByWorkArea = null)
{
    public double ScaleFor(Rect workArea) =>
        ScaleByWorkArea is not null && ScaleByWorkArea.TryGetValue(workArea, out var scale) ? scale : 1;
}

public static class DpiGeometry
{
    public static Rect PixelsToDips(Rect pixels, uint dpi)
    {
        var scale = (dpi > 0 ? dpi : 96) / 96.0;
        return new Rect(pixels.X / scale, pixels.Y / scale, pixels.Width / scale, pixels.Height / scale);
    }
}
