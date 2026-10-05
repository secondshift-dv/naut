using System.IO;
using System.Threading;
using Neuterradise.Profiling.Protocol;
using Neuterradise.Release.Contracts;

namespace Neuterradise.App.SystemServices.Storage;

/// <summary>
/// Read-only value contract for the extracted application directory (InstallRoot).
/// InstallRoot holds executables, runtime tools, inference models, built-in themes, media and notices.
/// It is mutated only by ZIP extraction/replacement/updater, never by data operations, and it is never
/// a Vault authority.
/// </summary>
public sealed class InstallPaths
{
    private static readonly Lazy<InstallPaths> Production = new(
        static () => new InstallPaths(ResolveProductionRoot()),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public InstallPaths(string root)
    {
        Root = RootPathRules.NormalizeRoot(root, nameof(root));
    }

    /// <summary>
    /// Production InstallRoot is the directory the running executable was deployed into.
    /// This is an application-resource authority only; it never implies a Vault location. The
    /// production authority is created once per process so startup, runtime capability checks,
    /// worker launch and update recovery all observe the same immutable root instance.
    /// </summary>
    public static InstallPaths CreateProduction() => Production.Value;

    private static string ResolveProductionRoot()
    {
        var executableRoot = RootPathRules.NormalizeRoot(AppContext.BaseDirectory, nameof(AppContext.BaseDirectory));
        var explicitRoot = Environment.GetEnvironmentVariable(
            ProfilingRuntimeEnvironment.InstallRootEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var root = RootPathRules.NormalizeRoot(
                explicitRoot,
                ProfilingRuntimeEnvironment.InstallRootEnvironmentVariable);
            var approvedRuntimeRoot = RootPathRules.NormalizeRoot(
                Path.Combine(root, ProductIdentity.RuntimeDirectoryName),
                nameof(ProductIdentity.RuntimeDirectoryName));
            var legacyRuntimeRoot = RootPathRules.NormalizeRoot(
                Path.Combine(root, ReleaseLayout.LegacyRuntimeDirectoryName),
                nameof(ReleaseLayout.LegacyRuntimeDirectoryName));

            if (!RootPathRules.AreSameRoot(executableRoot, approvedRuntimeRoot)
                && !RootPathRules.AreSameRoot(executableRoot, legacyRuntimeRoot)
                && !RootPathRules.AreSameRoot(executableRoot, root))
            {
                throw new InvalidOperationException(
                    "NEUTERRADISE_INSTALL_ROOT does not contain the running application.");
            }

            return root;
        }

        var trimmedExecutableRoot = Path.TrimEndingDirectorySeparator(executableRoot);
        var directoryName = Path.GetFileName(trimmedExecutableRoot);
        if (string.Equals(directoryName, ProductIdentity.RuntimeDirectoryName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(directoryName, ReleaseLayout.LegacyRuntimeDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(trimmedExecutableRoot);
            if (!string.IsNullOrWhiteSpace(parent)
                && File.Exists(Path.Combine(parent, string.Equals(directoryName, ReleaseLayout.LegacyRuntimeDirectoryName,
                    StringComparison.OrdinalIgnoreCase) ? ReleaseLayout.LegacyExecutableName : ProductIdentity.AppExecutableName))
                && File.Exists(Path.Combine(parent, "release-manifest.json")))
            {
                return RootPathRules.NormalizeRoot(parent, nameof(AppContext.BaseDirectory));
            }
        }

        return executableRoot;
    }

    public string Root { get; }

    /// <summary>
    /// Runtime payload root. Canonical packages expose only the launcher and release manifest at
    /// InstallRoot; the application, dependencies, worker, tools and notices live under runtime/.
    /// Legacy extracted packages retain their matching launcher/runtime pair.
    /// Developer/direct-run layouts without that package shape continue to use Root directly.
    /// </summary>
    public string RuntimeRoot
    {
        get
        {
            foreach (var legacy in new[] { false, true })
            {
                var nested = Path.Combine(Root, ReleaseLayout.RuntimeDirectory(legacy));
                var executable = ReleaseLayout.LauncherName(legacy);
                if (File.Exists(Path.Combine(Root, executable))
                    && File.Exists(Path.Combine(Root, ReleaseLayout.ReleaseManifestFileName))
                    && Directory.Exists(nested)
                    && File.Exists(Path.Combine(nested, executable)))
                    return RootPathRules.NormalizeRoot(nested, nameof(RuntimeRoot));
            }
            return Root;
        }
    }

    public string ToolsPath => Path.Combine(RuntimeRoot, "tools");

    public string ModelsPath => Path.Combine(RuntimeRoot, "workers", "models");

    public string ThemesPath => Path.Combine(RuntimeRoot, "themes");

    public string AssetsPath => Path.Combine(RuntimeRoot, "Assets");

    public string NoticesPath => Path.Combine(RuntimeRoot, "LICENSES");

    public string ThirdPartyNoticesFilePath => Path.Combine(RuntimeRoot, "THIRD-PARTY-NOTICES.txt");

    public string ReleaseManifestPath => Path.Combine(Root, "release-manifest.json");

    /// <summary>Public launcher path at InstallRoot.</summary>
    public string AppExecutablePath => Path.Combine(Root, ExecutableName);

    private string ExecutableName => !File.Exists(Path.Combine(Root, ProductIdentity.AppExecutableName))
        && File.Exists(Path.Combine(Root, ReleaseLayout.LegacyExecutableName))
            ? ReleaseLayout.LegacyExecutableName : ProductIdentity.AppExecutableName;

    /// <summary>Actual Uno application executable inside the runtime payload.</summary>
    public string RuntimeAppExecutablePath =>
        Path.Combine(RuntimeRoot, ExecutableName);

    public string ProfilingWorkerExecutablePath =>
        Path.Combine(RuntimeRoot, "workers", ProductIdentity.ProfilingWorkerExecutableName);

    public string UpdaterExecutablePath =>
        Path.Combine(RuntimeRoot, ProductIdentity.UpdaterExecutableName);

    public string GetAreaRoot(InstallPathArea area) => area switch
    {
        InstallPathArea.Root => Root,
        InstallPathArea.Runtime => RuntimeRoot,
        InstallPathArea.Tools => ToolsPath,
        InstallPathArea.Models => ModelsPath,
        InstallPathArea.Themes => ThemesPath,
        InstallPathArea.Assets => AssetsPath,
        InstallPathArea.Notices => NoticesPath,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
    };

    /// <summary>
    /// Resolves a deployment-relative resource path inside the requested area. The path must be
    /// relative and traversal-free; resolution never searches PATH, the working directory or any
    /// other implicit location.
    /// </summary>
    public string ResolveContainedPath(InstallPathArea area, string relativePath)
        => RootPathRules.ResolveContainedPath(Root, GetAreaRoot(area), relativePath, nameof(relativePath));

    /// <summary>
    /// Returns the resolved path only when the deployed resource actually exists, so a missing tool or
    /// model becomes a truthful unavailable capability instead of a fabricated success.
    /// </summary>
    public string? FindExistingFile(InstallPathArea area, string relativePath)
    {
        string resolvedPath;
        try
        {
            resolvedPath = ResolveContainedPath(area, relativePath);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return File.Exists(resolvedPath) ? resolvedPath : null;
    }

    public bool IsWithinInstallRoot(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        return RootPathRules.IsWithinOrEqual(Root, Path.GetFullPath(absolutePath));
    }

    /// <summary>
    /// Two-way disjointness against the other runtime roots. InstallRoot must never contain or live
    /// inside AppStateRoot or VaultRoot.
    /// </summary>
    public void EnsureDisjointFrom(AppStatePaths appState, string? vaultRoot = null)
    {
        ArgumentNullException.ThrowIfNull(appState);
        RootPathRules.EnsureRuntimeRootsDisjoint(Root, appState.Root, vaultRoot);
    }
}

public enum InstallPathArea
{
    Root,
    Runtime,
    Tools,
    Models,
    Themes,
    Assets,
    Notices,
}
