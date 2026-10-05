using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Neuterradise.App.SystemServices.Storage;

public sealed class WindowsVolumeIdentityProvider : IVolumeIdentityProvider
{
    public VolumeIdentity? TryGetVolumeIdentity(string resolvedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedPath);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(resolvedPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return null;
        }

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return null;
        }

        var mountPath = new StringBuilder(capacity: 1024);
        if (!GetVolumePathName(fullPath, mountPath, (uint)mountPath.Capacity))
        {
            return null;
        }

        var mountPathText = mountPath.ToString();
        var normalizedMountPath = Path.EndsInDirectorySeparator(mountPathText)
            ? mountPathText
            : mountPathText + Path.DirectorySeparatorChar;
        var volumeName = new StringBuilder(capacity: 1024);
        if (!GetVolumeNameForVolumeMountPoint(
                normalizedMountPath,
                volumeName,
                (uint)volumeName.Capacity))
        {
            return null;
        }

        return volumeName.Length == 0 ? null : new VolumeIdentity(volumeName.ToString());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(
        string fileName,
        StringBuilder volumePathName,
        uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        StringBuilder volumeName,
        uint bufferLength);
}
