using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Neuterradise.App.SystemServices.Storage;

public sealed record NtfsChangeStamp(ulong VolumeId, string FileId, long Length,
    ulong JournalId, long FileUsn, long Cursor, long LowestValidUsn)
{
    public bool ProvesUnchanged(NtfsChangeStamp current) =>
        VolumeId == current.VolumeId && FileId == current.FileId && Length == current.Length
        && JournalId == current.JournalId && FileUsn == current.FileUsn
        && Cursor >= current.LowestValidUsn && Cursor <= current.Cursor;

    /// <summary>
    /// Read-only NTFS change detection. A missing/inaccessible/reset/wrapped journal provides no
    /// trust; callers must perform full validation. File USN never substitutes for a content hash.
    /// The caller retains a read handle denying writers and deletion throughout its validation.
    /// </summary>
    public static NtfsChangeStamp? TryRead(FileStream file, string path, Action<string>? unavailable = null)
    {
        NtfsChangeStamp? Failure(string stage)
        {
            unavailable?.Invoke(stage + ":" + Marshal.GetLastWin32Error());
            return null;
        }
        if (!OperatingSystem.IsWindows()) return null;
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root is null || root.Length != 3 || root[1] != ':') return null;
        // Query through a directory handle to the filesystem root. A raw-volume data handle
        // requires privileges that normal desktop use does not have or need.
        using var volume = CreateFileW(root, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (volume.IsInvalid) return Failure("volume");
        var journal = new byte[80];
        // FSCTL_QUERY_USN_JOURNAL / FSCTL_READ_FILE_USN_DATA (winioctl.h).
        if (!DeviceIoControl(volume, 0x000900F4, null, 0, journal, journal.Length, out var journalBytes, IntPtr.Zero)
            || journalBytes < 56) return Failure("journal");
        var identity = new byte[24];
        if (!GetFileInformationByHandleEx(file.SafeFileHandle, 18, identity, identity.Length)) return Failure("identity");
        var usn = new byte[4096];
        byte[] versions = [2, 0, 2, 0];
        if (!DeviceIoControl(file.SafeFileHandle, 0x000900EB, versions, versions.Length, usn, usn.Length,
                out var usnBytes, IntPtr.Zero) || usnBytes < 60
            || BinaryPrimitives.ReadUInt16LittleEndian(usn.AsSpan(4)) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(usn) > usnBytes) return Failure("file-usn");
        var cursor = BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(16));
        var lowest = Math.Max(BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(8)),
            BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(24)));
        var fileUsn = BinaryPrimitives.ReadInt64LittleEndian(usn.AsSpan(24));
        if (cursor < 0 || lowest < 0 || lowest > cursor || fileUsn < 0 || fileUsn > cursor) return Failure("journal-range");
        return new NtfsChangeStamp(BinaryPrimitives.ReadUInt64LittleEndian(identity),
            Convert.ToHexString(identity.AsSpan(8, 16)), file.Length,
            BinaryPrimitives.ReadUInt64LittleEndian(journal), fileUsn, cursor, lowest);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input,
        int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        byte[] information, int size);
}
