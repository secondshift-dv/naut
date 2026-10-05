using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Neuterradise.App.SystemServices;

/// <summary>
/// Plain-text clipboard writes through Win32. Retry waits are asynchronous and application writes are
/// serialized, so a busy clipboard never blocks the UI dispatcher.
/// </summary>
public static class Win32Clipboard
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const int MaximumOpenAttempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    public static async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < MaximumOpenAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (OpenClipboard(IntPtr.Zero))
                {
                    WriteOpenedClipboard(text);
                    return;
                }

                if (attempt + 1 < MaximumOpenAttempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new ExternalException("The clipboard is in use by another application.");
        }
        finally
        {
            WriteGate.Release();
        }
    }

    private static void WriteOpenedClipboard(string text)
    {
        try
        {
            if (!EmptyClipboard())
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var bytes = checked((text.Length + 1) * 2);
            var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var target = GlobalLock(handle);
            if (target == IntPtr.Zero)
            {
                GlobalFree(handle);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
