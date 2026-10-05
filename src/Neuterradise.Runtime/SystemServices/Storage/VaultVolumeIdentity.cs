using System.Runtime.InteropServices;
using System.Text;

namespace Neuterradise.App.SystemServices.Storage;

internal static class VaultVolumeIdentity
{
    public static bool AreSameVolume(string leftPath, string rightPath)
    {
        var left = RootPathRules.NormalizeRoot(leftPath, nameof(leftPath));
        var right = RootPathRules.NormalizeRoot(rightPath, nameof(rightPath));

        if (OperatingSystem.IsWindows()
            && TryGetVolumeName(left, out var leftVolume)
            && TryGetVolumeName(right, out var rightVolume))
        {
            return string.Equals(leftVolume, rightVolume, StringComparison.OrdinalIgnoreCase);
        }

        var leftRoot = Path.GetPathRoot(left);
        var rightRoot = Path.GetPathRoot(right);
        return !string.IsNullOrWhiteSpace(leftRoot)
            && !string.IsNullOrWhiteSpace(rightRoot)
            && string.Equals(leftRoot, rightRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetVolumeName(string path, out string volumeName)
    {
        volumeName = string.Empty;
        var mountPoint = new StringBuilder(1024);
        if (!GetVolumePathName(path, mountPoint, mountPoint.Capacity))
        {
            return false;
        }

        var mount = mountPoint.ToString();
        if (!mount.EndsWith(Path.DirectorySeparatorChar))
        {
            mount += Path.DirectorySeparatorChar;
        }

        var volume = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPoint(mount, volume, volume.Capacity))
        {
            return false;
        }

        volumeName = volume.ToString();
        return !string.IsNullOrWhiteSpace(volumeName);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(
        string fileName,
        StringBuilder volumePathName,
        int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        StringBuilder volumeName,
        int bufferLength);
}
