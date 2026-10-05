namespace Neuterradise.App.SystemServices.Storage;

public enum VaultSelectionKind
{
    ExistingVault,
    EmptyFolder,
}

public sealed record VaultSelectionInspection(
    bool Accepted,
    VaultPaths? Paths,
    VaultSelectionKind? Kind,
    string? ErrorCode,
    string? SafeErrorDetail)
{
    public static VaultSelectionInspection Accept(VaultPaths paths, VaultSelectionKind kind) =>
        new(true, paths, kind, null, null);

    public static VaultSelectionInspection Reject(string code, string detail) =>
        new(false, null, null, code, detail);
}

/// <summary>
/// Acceptance policy for a user-selected final Vault folder. Existing Naut Vaults and empty folders
/// are accepted; bootstrap owns all catalog and Vault creation after selection.
/// </summary>
public sealed class VaultRootSelectionPolicy
{
    private readonly InstallPaths _install;
    private readonly AppStatePaths _appState;

    public VaultRootSelectionPolicy(InstallPaths install, AppStatePaths appState)
    {
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
    }

    public VaultSelectionInspection Inspect(string selectedRoot, string? currentRoot = null)
    {
        try
        {
            var paths = new VaultPaths(selectedRoot);
            if (!Directory.Exists(paths.Root))
            {
                return VaultSelectionInspection.Reject(
                    "VAULT_SELECTION_FOLDER_MISSING",
                    "Choose an existing folder. Create a new folder in the Windows picker first if you want a new Vault.");
            }

            paths.EnsureDisjointFrom(_install, _appState);
            RootPathRules.RejectExistingReparsePoints(paths.Root, paths.Root);

            if (!string.IsNullOrWhiteSpace(currentRoot))
            {
                var sourceRoot = RootPathRules.NormalizeRoot(currentRoot, nameof(currentRoot));
                if (RootPathRules.AreSameRoot(sourceRoot, paths.Root))
                {
                    return VaultSelectionInspection.Reject(
                        "VAULT_SELECTION_UNCHANGED",
                        "That folder is already the current Vault.");
                }

                RootPathRules.EnsureDisjoint("CurrentVault", sourceRoot, "SelectedVault", paths.Root);
            }

            if (File.Exists(paths.CatalogDbPath))
            {
                return VaultSelectionInspection.Accept(paths, VaultSelectionKind.ExistingVault);
            }

            using var entries = Directory.EnumerateFileSystemEntries(paths.Root).GetEnumerator();
            if (!entries.MoveNext())
            {
                for (var ancestor = Directory.GetParent(paths.Root); ancestor is not null; ancestor = ancestor.Parent)
                {
                    if (File.Exists(Path.Combine(ancestor.FullName, "_system", "catalog.db")))
                    {
                        return VaultSelectionInspection.Reject(
                            "VAULT_SELECTION_NESTED",
                            "Choose a folder outside an existing Vault.");
                    }
                }

                return VaultSelectionInspection.Accept(paths, VaultSelectionKind.EmptyFolder);
            }

            return VaultSelectionInspection.Reject(
                "VAULT_SELECTION_NOT_VAULT_OR_EMPTY",
                "Choose an existing naut Vault or an empty folder.");
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or UnauthorizedAccessException)
        {
            return VaultSelectionInspection.Reject(
                "VAULT_SELECTION_INVALID",
                exception.Message);
        }
    }
}
