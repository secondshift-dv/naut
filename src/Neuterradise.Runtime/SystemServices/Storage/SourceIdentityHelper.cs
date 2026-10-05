using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Neuterradise.App.SystemServices.Storage;

/// <summary>
/// Windows file-object identity used for transfer resume and destructive source cleanup.
/// A pathname, timestamp, hash, or length is not an object identity; destructive cleanup therefore
/// requires the volume serial + file index obtained from the same open handle that is deleted.
/// </summary>
public static class SourceIdentityHelper
{
    public static string? CaptureIdentity(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            using var handle = File.OpenHandle(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return TryReadIdentity(handle, fullPath, out var identity)
                ? JsonSerializer.Serialize(identity, SourceFileIdentityContext.Default.SourceFileIdentity)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool TryOpenForVerifiedDelete(
        string filePath,
        out SafeFileHandle? handle,
        out int win32Error)
    {
        handle = null;
        win32Error = 0;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var opened = CreateFileW(
                fullPath,
                GenericRead | DeleteAccess,
                FileShareRead | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero);

            if (opened.IsInvalid)
            {
                win32Error = Marshal.GetLastWin32Error();
                opened.Dispose();
                return false;
            }

            handle = opened;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool VerifyIdentity(string? capturedJson, string currentFilePath)
    {
        if (capturedJson is null || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var handle = File.OpenHandle(
                currentFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return VerifyIdentity(capturedJson, handle, currentFilePath);
        }
        catch
        {
            return false;
        }
    }

    public static bool VerifyIdentity(
        string? capturedJson,
        SafeFileHandle handle,
        string currentFilePath)
    {
        if (capturedJson is null || handle.IsInvalid || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var captured = JsonSerializer.Deserialize(
                capturedJson,
                SourceFileIdentityContext.Default.SourceFileIdentity);
            if (captured is null
                || !TryReadIdentity(handle, Path.GetFullPath(currentFilePath), out var current))
            {
                return false;
            }

            return string.Equals(captured.FullPath, current.FullPath, StringComparison.OrdinalIgnoreCase)
                && captured.VolumeSerialNumber == current.VolumeSerialNumber
                && captured.FileIndex == current.FileIndex
                && captured.ByteLength == current.ByteLength
                && captured.LastWriteTimeUtcTicks == current.LastWriteTimeUtcTicks;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryDeleteOpenedFile(SafeFileHandle handle, out int win32Error)
    {
        win32Error = 0;
        if (!OperatingSystem.IsWindows() || handle.IsInvalid)
        {
            return false;
        }

        var disposition = new FileDispositionInfo { DeleteFile = 1 };
        if (SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfo>()))
        {
            return true;
        }

        win32Error = Marshal.GetLastWin32Error();
        return false;
    }

    private static bool TryReadIdentity(
        SafeFileHandle handle,
        string fullPath,
        out SourceFileIdentity identity)
    {
        identity = default!;
        if (!GetFileInformationByHandle(handle, out var info))
        {
            return false;
        }

        var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        var byteLength = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        var lastWrite = ((long)info.LastWriteTime.dwHighDateTime << 32)
            | (uint)info.LastWriteTime.dwLowDateTime;

        identity = new SourceFileIdentity(
            fullPath,
            info.VolumeSerialNumber,
            fileIndex,
            byteLength,
            lastWrite);
        return true;
    }

    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    private enum FileInfoByHandleClass
    {
        FileBasicInfo = 0,
        FileStandardInfo = 1,
        FileNameInfo = 2,
        FileRenameInfo = 3,
        FileDispositionInfo = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}

internal sealed record SourceFileIdentity(
    string FullPath,
    uint VolumeSerialNumber,
    ulong FileIndex,
    long ByteLength,
    long LastWriteTimeUtcTicks);

[System.Text.Json.Serialization.JsonSerializable(typeof(SourceFileIdentity))]
internal sealed partial class SourceFileIdentityContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
