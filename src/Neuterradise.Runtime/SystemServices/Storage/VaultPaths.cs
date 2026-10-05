using System.IO;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.SystemServices.Storage;

/// <summary>
/// Complete typed location boundary for one managed media asset (TRUNK_AUTHORITY_MASTER §12).
///
/// Every consumer — MediaDetail, FFmpeg, face pipeline, thumbnail, Gallery artwork, explorer/open,
/// Customize, maintenance and repair — uses this single boundary instead of reading
/// <c>current_managed_relative_path</c> + <c>current_managed_file_name</c> separately and
/// independently resolving them.
///
/// <see cref="Exists"/> is <c>true</c> only when the absolute path points to an existing regular file.
/// <see cref="PathState"/> carries the database-level reconciliation state.
/// A missing file is not an error in this type; it is a fact the caller can act on.
/// </summary>
public sealed record ManagedMediaLocation(
    Guid MediaId,
    Guid? OwnerProfileId,
    string MediaType,
    string VaultRelativeDirectory,
    string ManagedFileName,
    string VaultRelativeFilePath,
    string? AbsoluteManagedFilePath,
    string? ContentFingerprint,
    bool Exists,
    ManagedPathState PathState);

/// <summary>
/// Value contract for VaultRoot: the user-selected directory that holds the catalog and the managed
/// media library. A VaultRoot is always supplied explicitly from approved configuration. There is no
/// production factory that derives it from <c>AppContext.BaseDirectory</c>, the working directory or a
/// repository layout, so the application folder can never become an accidental Vault.
/// Runtime tools, inference models, built-in themes and notices live under InstallRoot
/// (see <see cref="InstallPaths"/>), never inside the Vault.
/// </summary>
public sealed class VaultPaths
{
    private static readonly string[] _structuralRelativePaths =
    [
        "profiles",
        "media-assets",
        "_system",
        "_system/cache",
        "_system/cache/face-crops",
        "_system/cache/model-previews",
        "_system/cache/temp",
        "_system/staging/imports",
        "_system/temp/media-assets",
        "_system/temp/face-analysis",
        "_system/manifests",
        "_system/presentation-packs",
        "_trash/media",
        "_trash/profiles",
    ];

    public VaultPaths(string root)
    {
        Root = RootPathRules.NormalizeRoot(root, nameof(root));
        RootPathRules.EnsureSupportedWritableVaultRoot(Root);
    }

    public string Root { get; }

    public string ProfilesPath => Path.Combine(Root, "profiles");

    public string MediaAssetsPath => Path.Combine(Root, "media-assets");

    public string SystemPath => Path.Combine(Root, "_system");

    public string StagingPath => Path.Combine(SystemPath, "staging");

    public string StagingImportsPath => Path.Combine(StagingPath, "imports");

    public string TempPath => Path.Combine(SystemPath, "temp");

    public string TempMediaAssetsPath => Path.Combine(TempPath, "media-assets");

    public string TempFaceAnalysisPath => Path.Combine(TempPath, "face-analysis");

    public string CachePath => Path.Combine(SystemPath, "cache");

    public string ManifestsPath => Path.Combine(SystemPath, "manifests");

    public string TrashPath => Path.Combine(Root, "_trash");

    public string TrashMediasPath => Path.Combine(TrashPath, "media");

    public string TrashProfilesPath => Path.Combine(TrashPath, "profiles");

    /// <summary>
    /// Durable, Vault-owned user Presentation Packs. Same safety class as other user content: updater,
    /// uninstall and packaging never own or delete it, and import discovery never treats it as media.
    /// </summary>
    public string PresentationPacksPath => Path.Combine(SystemPath, "presentation-packs");

    public string CatalogDbPath => Path.Combine(SystemPath, "catalog.db");

    public string IdentityFilePath => Path.Combine(SystemPath, "vault.identity.json");

    public string LockPath => Path.Combine(SystemPath, "neuterradise.lock");

    /// <summary>
    /// Resolves a vault-relative managed path inside the expected area. Rooted paths, traversal
    /// segments, area escapes and existing reparse points are all rejected.
    ///
    /// IMPORTANT: paths produced by <see cref="ManagedPathPlanner"/> are VaultRoot-relative
    /// (they start with <c>profiles/</c>). Call <see cref="ResolveVaultRelativePath"/> for those;
    /// passing them here with <see cref="VaultPathArea.Profiles"/> would double-prefix.
    /// </summary>
    public string ResolveContainedPath(VaultPathArea expectedArea, string vaultRelativePath)
        => RootPathRules.ResolveContainedPath(
            Root,
            GetAreaRoot(expectedArea),
            vaultRelativePath,
            nameof(vaultRelativePath));

    /// <summary>
    /// Resolves a VaultRoot-relative path (the format stored in <c>current_managed_relative_path</c>
    /// and produced by <see cref="ManagedPathPlanner"/>) into an absolute path.
    ///
    /// The path is combined with <see cref="Root"/> (VaultRoot), but the result is validated
    /// against <paramref name="expectedArea"/> so a Profile/media consumer cannot accidentally
    /// accept a path under <c>_trash</c>, <c>_system/cache</c>, etc.
    ///
    /// Use this — not <see cref="ResolveContainedPath"/> — for paths that already carry their
    /// <c>profiles/</c> prefix.
    /// </summary>
    public string ResolveVaultRelativePath(VaultPathArea expectedArea, string vaultRelativePath)
        => RootPathRules.ResolveVaultContainedPath(
            Root,
            GetAreaRoot(expectedArea),
            vaultRelativePath,
            nameof(vaultRelativePath));

    /// <summary>
    /// Convenience overload defaulting to <see cref="VaultPathArea.Profiles"/>,
    /// the most common area for managed media paths.
    /// </summary>
    public string ResolveVaultRelativePath(string vaultRelativePath)
        => ResolveVaultRelativePath(VaultPathArea.Profiles, vaultRelativePath);

    public string ResolveMediaAssetPath(MediaStorageToken token, MediaAssetRole role)
    {
        var name = role switch
        {
            MediaAssetRole.Thumbnail => "thumbnail.webp",
            MediaAssetRole.Hover => "hover.mp4",
            MediaAssetRole.ModelRender => "model-render.nfig",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
        };
        return ResolveContainedPath(VaultPathArea.MediaAssets, $"{token.Value}/{name}");
    }

    public string ResolveStagedOriginalPartialPath(Guid importUnitId, MediaStorageToken token)
    {
        if (importUnitId == Guid.Empty)
            throw new ArgumentException("Import Unit ID cannot be empty.", nameof(importUnitId));
        return ResolveContainedPath(VaultPathArea.StagingImports,
            $"{importUnitId:D}/{token.Value}/original.partial");
    }

    public bool IsContainedPath(VaultPathArea expectedArea, string vaultRelativePath)
    {
        try
        {
            ResolveContainedPath(expectedArea, vaultRelativePath);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void EnsureStructuralDirectories()
    {
        foreach (var relativePath in _structuralRelativePaths)
        {
            Directory.CreateDirectory(
                Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    /// <summary>
    /// Two-way disjointness against the application roots. A Vault may never be the InstallRoot or the
    /// AppStateRoot, nor live inside or contain either of them.
    /// </summary>
    public void EnsureDisjointFrom(InstallPaths install, AppStatePaths appState)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(appState);
        RootPathRules.EnsureRuntimeRootsDisjoint(install.Root, appState.Root, Root);
    }

    /// <summary>
    /// Resolves a managed media location into the complete typed boundary. This is the single
    /// authority for converting stored Vault-relative paths into absolute paths. Every consumer
    /// (MediaDetail, FFmpeg, face pipeline, thumbnail, Gallery artwork, explorer, Customize,
    /// maintenance) should call this instead of independently combining Root + relative path.
    ///
    /// Returns <c>null</c> when the stored fields are missing or empty.
    /// </summary>
    public ManagedMediaLocation? ResolveManagedMediaLocation(
        Guid assetId,
        Guid? ownerProfileId,
        string mediaType,
        string? vaultRelativeDirectory,
        string? managedFileName,
        string? contentFingerprint,
        ManagedPathState pathState)
    {
        if (string.IsNullOrWhiteSpace(vaultRelativeDirectory) || string.IsNullOrWhiteSpace(managedFileName))
        {
            return new ManagedMediaLocation(
                assetId, ownerProfileId, mediaType,
                string.Empty, string.Empty, string.Empty,
                null, contentFingerprint, false, pathState);
        }

        var vaultRelativeFile = vaultRelativeDirectory + "/" + managedFileName;
        string? absolutePath = null;
        bool exists = false;

        try
        {
            absolutePath = ResolveVaultRelativePath(VaultPathArea.Profiles, vaultRelativeFile);
            exists = File.Exists(absolutePath);
        }
        catch (ArgumentException)
        {
            // Path outside area or invalid: absolutePath stays null.
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new ManagedMediaLocation(
            assetId, ownerProfileId, mediaType,
            vaultRelativeDirectory, managedFileName, vaultRelativeFile,
            absolutePath, contentFingerprint, exists, pathState);
    }

    public string GetAreaRoot(VaultPathArea area) => area switch
    {
        VaultPathArea.Root => Root,
        VaultPathArea.Profiles => ProfilesPath,
        VaultPathArea.MediaAssets => MediaAssetsPath,
        VaultPathArea.StagingImports => StagingImportsPath,
        VaultPathArea.TempMediaAssets => TempMediaAssetsPath,
        VaultPathArea.TempFaceAnalysis => TempFaceAnalysisPath,
        VaultPathArea.TrashMedias => TrashMediasPath,
        VaultPathArea.TrashProfiles => TrashProfilesPath,
        VaultPathArea.Cache => CachePath,
        VaultPathArea.Manifests => ManifestsPath,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
    };
}

public enum VaultPathArea
{
    Root,
    Profiles,
    MediaAssets,
    StagingImports,
    TempMediaAssets,
    TempFaceAnalysis,
    TrashMedias,
    TrashProfiles,
    Cache,
    Manifests,
}
