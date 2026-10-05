using System.Diagnostics;
using Neuterradise.Release.Contracts;

namespace Neuterradise.Launcher;

/// <summary>
/// Stable public entry point for the portable package. Product runtime files stay under
/// runtime/ so extracting the ZIP never spills framework files beside the launcher.
/// </summary>
public static class Program
{
    private const string RuntimeExecutableName = ReleaseLayout.RuntimeExecutableName;
    private const string InstallRootEnvironmentVariable = ReleaseLayout.InstallRootEnvironmentVariable;

    public static int Main(string[] args)
    {
        try
        {
            var installRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(AppContext.BaseDirectory));
            var runtimeRoot = Path.GetFullPath(
                Path.Combine(installRoot, ReleaseLayout.RuntimeDirectoryName));
            var rootPrefix = installRoot + Path.DirectorySeparatorChar;

            if (!File.Exists(Path.Combine(installRoot, ReleaseLayout.ReleaseManifestFileName))
                || !runtimeRoot.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(runtimeRoot)
                || (File.GetAttributes(runtimeRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return 2;
            }

            var executable = Path.GetFullPath(
                Path.Combine(runtimeRoot, RuntimeExecutableName));
            if (!executable.StartsWith(
                    runtimeRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(executable)
                || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            {
                return 2;
            }

            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = runtimeRoot,
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            start.Environment[InstallRootEnvironmentVariable] = installRoot;
            foreach (var argument in args)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            return process is null ? 3 : 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            return 3;
        }
    }
}
