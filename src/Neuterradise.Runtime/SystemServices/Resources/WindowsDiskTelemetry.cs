using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Resources;

internal enum DiskSeekPenalty { Unknown, Rotational, SolidState }
internal enum DiskIoPriority { VeryLow = 0, Low = 1, Normal = 2 }

internal readonly record struct DiskCounters(
    long ReadTime, long WriteTime, long IdleTime, uint Reads, uint Writes, uint QueueDepth, long QueryTime);

internal static unsafe partial class WindowsDiskTelemetry
{
    private const uint VolumeDiskExtents = 0x00560000;
    private const uint StorageQueryProperty = 0x002D1400;
    private const uint DiskPerformance = 0x00070020;
    private static readonly WindowsVolumeIdentityProvider Volumes = new();

    public static string? GetVolume(string path) =>
        OperatingSystem.IsWindows() ? Volumes.TryGetVolumeIdentity(path)?.Value : null;

    public static int[] GetDisks(string? volume)
    {
        if (volume is null) return [];
        using var file = OpenDevice(volume.TrimEnd('\\'));
        if (file.IsInvalid) return [];
        // Windows returns a count, alignment padding, then DISK_EXTENT records (24 bytes each).
        Span<byte> extents = stackalloc byte[1024];
        if (!Query(file, VolumeDiskExtents, [], extents, out var bytes) || bytes < 8) return [];
        var count = BinaryPrimitives.ReadUInt32LittleEndian(extents);
        const int extentSize = 24;
        if (count == 0 || count > (bytes - 8) / extentSize) return [];
        var disks = new int[count];
        for (var i = 0; i < disks.Length; i++)
            disks[i] = BinaryPrimitives.ReadInt32LittleEndian(extents[(8 + i * extentSize)..]);
        return [.. disks.Distinct().Order()];
    }

    public static DiskSeekPenalty GetSeekPenalty(int disk)
    {
        using var file = OpenDisk(disk);
        if (file.IsInvalid) return DiskSeekPenalty.Unknown;
        Span<byte> input = [7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Span<byte> output = stackalloc byte[32];
        if (!Query(file, StorageQueryProperty, input, output, out var size) || size < 9)
            return DiskSeekPenalty.Unknown;
        return output[8] == 0 ? DiskSeekPenalty.SolidState : DiskSeekPenalty.Rotational;
    }

    public static DiskCounters? GetCounters(int disk)
    {
        using var file = OpenDisk(disk);
        if (file.IsInvalid) return null;
        Span<byte> output = stackalloc byte[96];
        if (!Query(file, DiskPerformance, [], output, out var size) || size < 64) return null;
        return new(
            BinaryPrimitives.ReadInt64LittleEndian(output[16..]), BinaryPrimitives.ReadInt64LittleEndian(output[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(output[32..]), BinaryPrimitives.ReadUInt32LittleEndian(output[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(output[44..]), BinaryPrimitives.ReadUInt32LittleEndian(output[48..]),
            BinaryPrimitives.ReadInt64LittleEndian(output[56..]));
    }

    public static void SetPriority(SafeFileHandle file, DiskIoPriority value)
    {
        if (OperatingSystem.IsWindows())
        {
            const int fileIoPriorityHintInfo = 12;
            var priority = (int)value;
            _ = SetFileInformationByHandle(file, fileIoPriorityHintInfo, &priority, sizeof(int));
        }
    }

    private static SafeFileHandle OpenDisk(int disk) => OpenDevice($@"\\.\PhysicalDrive{disk}");

    private static SafeFileHandle OpenDevice(string name) => CreateFile(name, 0, 7, 0, 3, 0, 0);

    private static bool Query(SafeFileHandle file, uint control, ReadOnlySpan<byte> input, Span<byte> output, out int bytes)
    {
        fixed (byte* inputPointer = input)
        fixed (byte* outputPointer = output)
            return DeviceIoControl(file, control, inputPointer, input.Length, outputPointer, output.Length, out bytes, 0);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint attributes,
        uint disposition, uint flags, nint templateFile);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle file, uint code, byte* input, int inputSize,
        byte* output, int outputSize, out int received, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        void* information, int size);
}
