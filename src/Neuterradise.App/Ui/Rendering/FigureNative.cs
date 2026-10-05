using System.Runtime.InteropServices;

namespace Neuterradise.App.Ui;

internal static class FigureNative
{
    internal const uint Child = 0x40000000;
    internal const uint ClipSiblings = 0x04000000;
    internal const uint ClipChildren = 0x02000000;
    internal const uint Size = 0x0005;
    internal const uint Paint = 0x000F;
    internal const uint MouseWheel = 0x020A;
    internal const uint LeftDown = 0x0201;
    internal const uint LeftUp = 0x0202;
    internal const uint LeftDoubleClick = 0x0203;
    internal const uint MouseMove = 0x0200;
    internal const uint CaptureChanged = 0x0215;
    internal const uint CancelMode = 0x001F;
    internal const long MouseLeftButton = 0x0001;
    internal const long MouseShift = 0x0004;
    internal const long MouseControl = 0x0008;
    internal const uint EraseBackground = 0x0014;
    internal const uint NcDestroy = 0x0082;
    internal const uint Show = 0x0018;
    internal static readonly Guid Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    internal static readonly Guid Factory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        public uint Size;
        public uint Style;
        public IntPtr Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rational { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ModeDescription
    {
        public uint Width;
        public uint Height;
        public Rational RefreshRate;
        public uint Format;
        public uint ScanlineOrdering;
        public uint Scaling;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SampleDescription { public uint Count; public uint Quality; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SwapChainDescription
    {
        public ModeDescription Buffer;
        public SampleDescription Sample;
        public uint BufferUsage;
        public uint BufferCount;
        public IntPtr OutputWindow;
        public int Windowed;
        public uint SwapEffect;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RenderTargetDescription
    {
        public uint Format;
        public uint Dimension;
        public uint MipSlice;
        public uint UnionPadding1;
        public uint UnionPadding2;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int CreateSwapChain(IntPtr self, IntPtr device, ref SwapChainDescription description, out IntPtr swapChain);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int GetBuffer(IntPtr self, uint index, ref Guid iid, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int CreateRenderTarget(IntPtr self, IntPtr resource, IntPtr description, out IntPtr target);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void ClearTarget(IntPtr self, IntPtr target, [MarshalAs(UnmanagedType.LPArray, SizeConst = 4)] float[] color);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int Present(IntPtr self, uint interval, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int ResizeBuffers(IntPtr self, uint bufferCount, uint width, uint height, uint format, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void ContextOperation(IntPtr self);

    internal static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    internal static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    internal static void Release(ref IntPtr pointer)
    {
        var owned = pointer;
        pointer = IntPtr.Zero;
        if (owned != IntPtr.Zero) Marshal.Release(owned);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    internal static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags,
        [In] uint[] featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out uint featureLevel, out IntPtr immediateContext);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    internal static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr GetModuleHandleW(string? module);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    internal static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);
}
