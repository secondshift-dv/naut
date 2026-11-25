using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Neuterradise.App.SystemServices.Storage;

/// <summary>
/// Windows file-object identity used for intake, hashing, and transfer resume.
/// The volume serial and file index distinguish a replacement from the admitted source.
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

            return ReadIdentity(handle, fullPath) is { } identity ? SerializeIdentity(identity) : null;
        }
        catch
        {
            return null;
        }
    }

    internal static SourceFileIdentity? ReadIdentity(SafeFileHandle handle, string path) =>
        OperatingSystem.IsWindows() && !handle.IsInvalid
            && TryReadIdentity(handle, Path.GetFullPath(path), out var identity) ? identity : null;

    internal static string SerializeIdentity(SourceFileIdentity identity) =>
        JsonSerializer.Serialize(identity, SourceFileIdentityContext.Default.SourceFileIdentity);

    internal static bool SameIdentity(string? first, string? second)
    {
        if (first is null || second is null) return false;
        try
        {
            var a = JsonSerializer.Deserialize(first, SourceFileIdentityContext.Default.SourceFileIdentity);
            var b = JsonSerializer.Deserialize(second, SourceFileIdentityContext.Default.SourceFileIdentity);
            return a is not null && b is not null && MatchesIdentity(a, b);
        }
        catch (JsonException) { return false; }
    }

    private static bool MatchesIdentity(SourceFileIdentity captured, SourceFileIdentity current) =>
        string.Equals(captured.FullPath, current.FullPath, StringComparison.OrdinalIgnoreCase)
        && captured.VolumeSerialNumber == current.VolumeSerialNumber
        && captured.FileIndex == current.FileIndex
        && captured.ByteLength == current.ByteLength
        && captured.LastWriteTimeUtcTicks == current.LastWriteTimeUtcTicks
        && (captured.ChangeTime is null || captured.ChangeTime == current.ChangeTime);

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

            return MatchesIdentity(captured, current);
        }
        catch
        {
            return false;
        }
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
            lastWrite,
            ReadChangeTime(handle));
        return true;
    }

    // Check the opened file rather than trusting the preceding path-based reparse scan.
    internal static bool MatchesOpenedPath(SafeFileHandle handle, string expectedPath)
    {
        if (!OperatingSystem.IsWindows() || handle.IsInvalid) return false;
        var buffer = new System.Text.StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= 32768) return false;
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity((int)length + 1);
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return false;
        }
        var finalPath = buffer.ToString();
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            finalPath = @"\\" + finalPath[8..];
        else if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            finalPath = finalPath[4..];
        return string.Equals(Path.GetFullPath(finalPath), Path.GetFullPath(expectedPath),
            StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,
        System.Text.StringBuilder path, uint capacity, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    // ChangeTime detects in-place modification even if LastWriteTime is restored.
    private static long? ReadChangeTime(SafeFileHandle handle) =>
        GetFileInformationByHandleEx(handle, 0, out var basic, (uint)Marshal.SizeOf<FileBasicInfo>())
            && basic.ChangeTime > 0 ? basic.ChangeTime : null;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int infoClass, out FileBasicInfo info, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
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
    long LastWriteTimeUtcTicks,
    long? ChangeTime = null);

[System.Text.Json.Serialization.JsonSerializable(typeof(SourceFileIdentity))]
internal sealed partial class SourceFileIdentityContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
