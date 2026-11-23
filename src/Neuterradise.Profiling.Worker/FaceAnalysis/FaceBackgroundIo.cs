using System.Runtime.InteropServices;

namespace Neuterradise.Profiling.Worker.FaceAnalysis;

// Inference is synchronous here, so the priority switch is restored on the same thread.
internal readonly struct FaceBackgroundIo(bool enabled) : IDisposable
{
    private const int BackgroundBegin = 0x00010000;
    private const int BackgroundEnd = 0x00020000;

    public static FaceBackgroundIo Enter() =>
        new(OperatingSystem.IsWindows() && SetThreadPriority(GetCurrentThread(), BackgroundBegin));

    public void Dispose()
    {
        if (enabled) _ = SetThreadPriority(GetCurrentThread(), BackgroundEnd);
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(nint thread, int priority);
}
