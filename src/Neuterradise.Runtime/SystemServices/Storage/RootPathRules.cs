using System.IO;

namespace Neuterradise.App.SystemServices.Storage;

/// <summary>
/// Single owner of the normalization and containment rules shared by the three runtime roots
/// (InstallRoot, AppStateRoot, VaultRoot). Windows comparisons are case-insensitive and
/// directory-separator aware so that a raw string prefix can never be mistaken for containment.
/// </summary>
public static class RootPathRules
{
    /// <summary>
    /// Normalizes an explicitly supplied root. Relative paths are rejected because they would make
    /// the current working directory an implicit root authority.
    /// </summary>
    public static string NormalizeRoot(string root, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root, parameterName);

        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException(
                "A runtime root must be a fully qualified path; relative paths would make the current working directory authoritative.",
                parameterName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public static bool AreSameRoot(string left, string right)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(left);
        ArgumentException.ThrowIfNullOrWhiteSpace(right);

        return string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a strict descendant of <paramref name="ancestor"/>.
    /// </summary>
    public static bool IsStrictDescendant(string ancestor, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ancestor);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        var normalizedAncestor = Path.TrimEndingDirectorySeparator(ancestor);
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(candidate);
        if (AreSameRoot(normalizedAncestor, normalizedCandidate))
        {
            return false;
        }

        var ancestorWithSeparator = normalizedAncestor + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(ancestorWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWithinOrEqual(string ancestor, string candidate)
        => AreSameRoot(ancestor, candidate) || IsStrictDescendant(ancestor, candidate);

    /// <summary>
    /// Two-way overlap: identical roots, or either root containing the other.
    /// </summary>
    public static bool Overlaps(string left, string right)
        => AreSameRoot(left, right)
            || IsStrictDescendant(left, right)
            || IsStrictDescendant(right, left);

    public static void EnsureDisjoint(string leftName, string left, string rightName, string right)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftName);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightName);

        if (AreSameRoot(left, right))
        {
            throw new ArgumentException(
                $"{leftName} and {rightName} must not be the same directory ('{left}').");
        }

        if (IsStrictDescendant(left, right))
        {
            throw new ArgumentException(
                $"{rightName} ('{right}') must not live inside {leftName} ('{left}').");
        }

        if (IsStrictDescendant(right, left))
        {
            throw new ArgumentException(
                $"{leftName} ('{left}') must not live inside {rightName} ('{right}').");
        }
    }

    /// <summary>
    /// Validates the whole runtime root set two-way. VaultRoot may still be unselected before onboarding.
    /// </summary>
    public static void EnsureRuntimeRootsDisjoint(string installRoot, string appStateRoot, string? vaultRoot)
    {
        EnsureDisjoint("InstallRoot", installRoot, "AppStateRoot", appStateRoot);

        if (string.IsNullOrWhiteSpace(vaultRoot))
        {
            return;
        }

        EnsureDisjoint("InstallRoot", installRoot, "VaultRoot", vaultRoot);
        EnsureDisjoint("AppStateRoot", appStateRoot, "VaultRoot", vaultRoot);
    }

    /// <summary>
    /// Validates the v0.0.1 writable-Vault location without creating or modifying it.
    /// Network/UNC and known cloud-sync roots are rejected because catalog locking and atomic rename
    /// cannot be assumed there.
    /// </summary>
    public static void EnsureSupportedWritableVaultRoot(string vaultRoot)
    {
        var normalized = NormalizeRoot(vaultRoot, nameof(vaultRoot));
        if (normalized.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("VaultRoot must be on a local Windows volume.", nameof(vaultRoot));
        }

        var root = Path.GetPathRoot(normalized)
            ?? throw new ArgumentException("VaultRoot has no volume root.", nameof(vaultRoot));
        var drive = new DriveInfo(root);
        if (drive.DriveType is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory)
        {
            throw new ArgumentException("VaultRoot must be on a writable local volume.", nameof(vaultRoot));
        }

        var cloudRoots = new[]
        {
            Environment.GetEnvironmentVariable("OneDrive"),
            Environment.GetEnvironmentVariable("OneDriveConsumer"),
            Environment.GetEnvironmentVariable("OneDriveCommercial"),
            Environment.GetEnvironmentVariable("Dropbox"),
            Environment.GetEnvironmentVariable("GoogleDrive"),
        };
        if (cloudRoots.Where(path => !string.IsNullOrWhiteSpace(path))
            .Any(path => IsWithinOrEqual(NormalizeRoot(path!, nameof(vaultRoot)), normalized)))
        {
            throw new ArgumentException("VaultRoot cannot be inside a known cloud-sync folder.", nameof(vaultRoot));
        }

        RejectExistingReparsePoints(root, normalized);
    }

    /// <summary>
    /// Resolves a root-relative path and proves it stays inside <paramref name="areaRoot"/>, which must
    /// itself stay inside <paramref name="authorityRoot"/>. Rooted paths and traversal segments are rejected.
    /// </summary>
    public static string ResolveContainedPath(
        string authorityRoot,
        string areaRoot,
        string relativePath,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(areaRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath, parameterName);

        if (Path.IsPathRooted(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw new ArgumentException(
                "A managed path must be root-relative and cannot inject a rooted path.",
                parameterName);
        }

        var platformRelativePath = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (platformRelativePath
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "A managed path cannot contain traversal segments.",
                parameterName);
        }

        var resolvedPath = Path.GetFullPath(Path.Combine(areaRoot, platformRelativePath));

        if (!IsWithinOrEqual(areaRoot, resolvedPath))
        {
            throw new ArgumentException(
                $"The managed path escapes the expected root '{areaRoot}'.",
                parameterName);
        }

        RejectExistingReparsePoints(authorityRoot, resolvedPath);
        return resolvedPath;
    }

    /// <summary>
    /// Resolves a <paramref name="combineRoot"/>-relative path (combines against <paramref name="combineRoot"/>)
    /// while validating the result stays inside <paramref name="areaRoot"/>. Rooted paths, traversal
    /// segments, area escapes and existing reparse points are all rejected.
    ///
    /// Unlike <see cref="ResolveContainedPath"/>, the relative path is combined with
    /// <paramref name="combineRoot"/>, not with <paramref name="areaRoot"/>.
    /// Use this when the relative path already carries the area prefix (e.g. <c>profiles/X/…</c>).
    /// </summary>
    public static string ResolveVaultContainedPath(
        string combineRoot,
        string areaRoot,
        string relativePath,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(combineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(areaRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath, parameterName);

        if (Path.IsPathRooted(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw new ArgumentException(
                "A managed path must be root-relative and cannot inject a rooted path.",
                parameterName);
        }

        var platformRelativePath = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (platformRelativePath
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "A managed path cannot contain traversal segments.",
                parameterName);
        }

        var resolvedPath = Path.GetFullPath(Path.Combine(combineRoot, platformRelativePath));

        if (!IsWithinOrEqual(areaRoot, resolvedPath))
        {
            throw new ArgumentException(
                $"The managed path escapes the expected area '{areaRoot}'.",
                parameterName);
        }

        RejectExistingReparsePoints(combineRoot, resolvedPath);
        return resolvedPath;
    }

    /// <summary>
    /// Rejects reparse points on the components of <paramref name="candidate"/> that already exist below
    /// <paramref name="authorityRoot"/>. Components that do not exist yet cannot redirect anything.
    /// </summary>
    public static void RejectExistingReparsePoints(string authorityRoot, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        var relative = Path.GetRelativePath(authorityRoot, candidate);
        if (relative == "." || Path.IsPathRooted(relative))
        {
            return;
        }

        var current = Path.TrimEndingDirectorySeparator(authorityRoot);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                return;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Managed path containment rejects reparse point '{current}'.");
            }
        }
    }
}
