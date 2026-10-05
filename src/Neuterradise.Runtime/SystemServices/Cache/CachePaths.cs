using System.IO;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Cache;

public enum CacheFamily
{
    FaceCrops,
    ModelPreviews,
    Temp,
}

public sealed class CachePaths
{
    public const string FaceCropsDirectory = "face-crops";
    public const string ModelPreviewsDirectory = "model-previews";
    public const string TempDirectory = "temp";

    private static readonly string[] _familyDirectories =
    [
        FaceCropsDirectory,
        ModelPreviewsDirectory,
        TempDirectory,
    ];

    public CachePaths(VaultPaths vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        Vault = vault;
    }

    public VaultPaths Vault { get; }

    public string CacheRoot => Vault.CachePath;

    public string FaceCropsPath => Path.Combine(CacheRoot, FaceCropsDirectory);

    public string ModelPreviewsPath => Path.Combine(CacheRoot, ModelPreviewsDirectory);

    public string TempPath => Path.Combine(CacheRoot, TempDirectory);

    public string GetFamilyDirectoryName(CacheFamily family) => family switch
    {
        CacheFamily.FaceCrops => FaceCropsDirectory,
        CacheFamily.ModelPreviews => ModelPreviewsDirectory,
        CacheFamily.Temp => TempDirectory,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };

    public string GetFamilyPath(CacheFamily family) => family switch
    {
        CacheFamily.FaceCrops => FaceCropsPath,
        CacheFamily.ModelPreviews => ModelPreviewsPath,
        CacheFamily.Temp => TempPath,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };

    public string ResolveContainedPath(CacheFamily family, string familyRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyRelativePath);
        if (Path.IsPathRooted(familyRelativePath) || Path.IsPathFullyQualified(familyRelativePath))
        {
            throw new ArgumentException("A cache-relative path cannot be rooted.", nameof(familyRelativePath));
        }

        var normalizedRelative = familyRelativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (normalizedRelative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException("A cache-relative path cannot contain traversal segments ('..').", nameof(familyRelativePath));
        }

        var familyRoot = GetFamilyPath(family);
        var resolvedPath = Path.GetFullPath(Path.Combine(familyRoot, normalizedRelative));

        if (!IsWithinOrEqual(resolvedPath, familyRoot))
        {
            throw new ArgumentException($"The path '{resolvedPath}' escapes the {family} cache directory.", nameof(familyRelativePath));
        }

        RejectExistingReparsePoints(CacheRoot, resolvedPath);
        return resolvedPath;
    }

    public string ResolveContainedCachePath(string cacheRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRelativePath);
        if (Path.IsPathRooted(cacheRelativePath) || Path.IsPathFullyQualified(cacheRelativePath))
        {
            throw new ArgumentException("A cache-relative path cannot be rooted.", nameof(cacheRelativePath));
        }

        var normalizedRelative = cacheRelativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (normalizedRelative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException("A cache-relative path cannot contain traversal segments ('..').", nameof(cacheRelativePath));
        }

        var resolvedPath = Path.GetFullPath(Path.Combine(CacheRoot, normalizedRelative));

        if (!IsWithinOrEqual(resolvedPath, CacheRoot))
        {
            throw new ArgumentException($"The path '{resolvedPath}' escapes the cache root.", nameof(cacheRelativePath));
        }

        RejectExistingReparsePoints(CacheRoot, resolvedPath);
        return resolvedPath;
    }

    public bool IsContainedCachePath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(path);
            if (!IsWithinOrEqual(fullPath, CacheRoot))
            {
                return false;
            }

            RejectExistingReparsePoints(CacheRoot, fullPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(CacheRoot);
        foreach (var dir in _familyDirectories)
        {
            Directory.CreateDirectory(Path.Combine(CacheRoot, dir));
        }
    }

    private static bool IsWithinOrEqual(string candidate, string expectedRoot)
    {
        if (string.Equals(candidate, expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = Path.EndsInDirectorySeparator(expectedRoot)
            ? expectedRoot
            : expectedRoot + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectExistingReparsePoints(string authorityRoot, string candidate)
    {
        var relative = Path.GetRelativePath(authorityRoot, candidate);
        if (relative == ".")
        {
            return;
        }

        var current = authorityRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Cache path containment rejects reparse point '{current}'.");
            }
        }
    }
}
