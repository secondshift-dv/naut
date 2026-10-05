namespace Neuterradise.Release.Contracts;

/// <summary>
/// Stable physical layout for the portable Windows package. The package root is update/replacement
/// authority; product runtime files are isolated under RuntimeDirectoryName.
/// </summary>
public static class ReleaseLayout
{
    public const string RuntimeDirectoryName = "runtime";
    public const string LauncherExecutableName = "naut.exe";
    public const string RuntimeExecutableName = "naut.exe";
    public const string LegacyRuntimeDirectoryName = "NeuTerradise";
    public const string LegacyExecutableName = "NeuTerradise.exe";
    public const string ReleaseManifestFileName = "release-manifest.json";
    public const string InstallRootEnvironmentVariable = "NEUTERRADISE_INSTALL_ROOT";

    public static string RuntimePrefix => RuntimeDirectoryName + "/";

    public static bool IsLegacyPayload(IEnumerable<string> members) =>
        members.Contains(LegacyExecutableName, StringComparer.OrdinalIgnoreCase)
        && !members.Contains(LauncherExecutableName, StringComparer.OrdinalIgnoreCase);

    public static string LauncherName(bool legacy) => legacy ? LegacyExecutableName : LauncherExecutableName;
    public static string RuntimeDirectory(bool legacy) => legacy ? LegacyRuntimeDirectoryName : RuntimeDirectoryName;

    public static string RequiredMember(string currentMember, bool legacy)
    {
        if (!legacy) return currentMember;
        if (currentMember.Equals(LauncherExecutableName, StringComparison.OrdinalIgnoreCase)) return LegacyExecutableName;
        var suffix = currentMember[RuntimePrefix.Length..];
        if (suffix.Equals(RuntimeExecutableName, StringComparison.OrdinalIgnoreCase)) suffix = LegacyExecutableName;
        return LegacyRuntimeDirectoryName + "/" + suffix;
    }

    public static bool IsManifestPayloadMember(string normalizedRelativePath, bool legacy = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRelativePath);
        return string.Equals(
                   normalizedRelativePath,
                   LauncherName(legacy),
                   StringComparison.OrdinalIgnoreCase)
               || normalizedRelativePath.StartsWith(
                   RuntimeDirectory(legacy) + "/",
                   StringComparison.OrdinalIgnoreCase);
    }
}
