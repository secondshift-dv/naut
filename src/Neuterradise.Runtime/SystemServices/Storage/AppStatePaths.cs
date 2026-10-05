using System.IO;
using System.Threading;

namespace Neuterradise.App.SystemServices.Storage;

/// <summary>
/// Value contract for AppStateRoot (%LOCALAPPDATA%\naut): stable application configuration
/// plus update state/downloads/staging and crash diagnostics. AppStateRoot never holds user media
/// and is never a Vault authority.
/// </summary>
public sealed class AppStatePaths
{
    private static readonly string[] _structuralRelativePaths =
    [
        "diagnostics",
        "updates",
        "updates/downloads",
        "updates/staging",
        "update-tools",
        "vault-transitions",
    ];

    private static readonly Lazy<AppStatePaths> Production = new(
        CreateProductionCore,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public AppStatePaths(string root)
    {
        Root = RootPathRules.NormalizeRoot(root, nameof(root));
    }

    /// <summary>
    /// Production AppStateRoot is %LOCALAPPDATA%\naut. The product-owned folder follows the public
    /// Naut identity so diagnostics, updater state, Vault selection and UI preferences remain stable
    /// across application builds and updates. The production authority is resolved once per
    /// process; explicit constructors remain available for deterministic isolated callers.
    /// </summary>
    public static AppStatePaths CreateProduction() => Production.Value;

    private static AppStatePaths CreateProductionCore()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "Windows did not report a local application data folder, so application state has no safe root.");
        }

        return new AppStatePaths(Path.Combine(localAppData, "naut"));
    }

    public string Root { get; }

    /// <summary>
    /// Stable product-level configuration authority. It deliberately does not include assembly or
    /// build identity so user choices survive a binary replacement/update.
    /// </summary>
    public string ConfigurationFilePath => Path.Combine(Root, "config.json");

    public string DiagnosticsPath => Path.Combine(Root, "diagnostics");

    public string UpdatesPath => Path.Combine(Root, "updates");

    public string UpdateStateFilePath => Path.Combine(UpdatesPath, "update-state.json");

    public string UpdateDownloadsPath => Path.Combine(UpdatesPath, "downloads");

    public string UpdateStagingPath => Path.Combine(UpdatesPath, "staging");


    /// <summary>
    /// App-owned copy of the updater helper, per update operation, so replacement never runs the
    /// binary it is about to overwrite.
    /// </summary>
    public string UpdateToolsPath => Path.Combine(Root, "update-tools");

    public string VaultTransitionsPath => Path.Combine(Root, "vault-transitions");

    public string VaultTransitionStateFilePath => Path.Combine(VaultTransitionsPath, "active.json");

    public string GetVaultTransitionOperationPath(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A Vault transition operation id is required.", nameof(operationId));
        }

        return Path.Combine(VaultTransitionsPath, operationId.ToString("D"));
    }

    public string GetVaultTransitionInventoryFilePath(Guid operationId) =>
        Path.Combine(GetVaultTransitionOperationPath(operationId), "inventory.json");

    public string GetUpdateToolsPath(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An update operation id is required.", nameof(operationId));
        }

        return Path.Combine(UpdateToolsPath, operationId.ToString("D"));
    }


    public string GetAreaRoot(AppStatePathArea area) => area switch
    {
        AppStatePathArea.Root => Root,
        AppStatePathArea.Diagnostics => DiagnosticsPath,
        AppStatePathArea.Updates => UpdatesPath,
        AppStatePathArea.UpdateDownloads => UpdateDownloadsPath,
        AppStatePathArea.UpdateStaging => UpdateStagingPath,
        AppStatePathArea.UpdateTools => UpdateToolsPath,
        AppStatePathArea.VaultTransitions => VaultTransitionsPath,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
    };

    public string ResolveContainedPath(AppStatePathArea area, string relativePath)
        => RootPathRules.ResolveContainedPath(Root, GetAreaRoot(area), relativePath, nameof(relativePath));

    public bool IsWithinAppStateRoot(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        return RootPathRules.IsWithinOrEqual(Root, Path.GetFullPath(absolutePath));
    }

    /// <summary>
    /// Creates the AppState directory skeleton. AppState is application-owned, so it may be created
    /// before the user has chosen a Vault; this never creates or touches a Vault.
    /// </summary>
    public void EnsureStructuralDirectories()
    {
        Directory.CreateDirectory(Root);
        foreach (var relativePath in _structuralRelativePaths)
        {
            Directory.CreateDirectory(
                Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        var configurationDirectory = Path.GetDirectoryName(ConfigurationFilePath);
        if (!string.IsNullOrWhiteSpace(configurationDirectory))
        {
            Directory.CreateDirectory(configurationDirectory);
        }
    }

    public void EnsureDisjointFrom(InstallPaths install, string? vaultRoot = null)
    {
        ArgumentNullException.ThrowIfNull(install);
        RootPathRules.EnsureRuntimeRootsDisjoint(install.Root, Root, vaultRoot);
    }
}

public enum AppStatePathArea
{
    Root,
    Diagnostics,
    Updates,
    UpdateDownloads,
    UpdateStaging,
    UpdateTools,
    VaultTransitions,
}
