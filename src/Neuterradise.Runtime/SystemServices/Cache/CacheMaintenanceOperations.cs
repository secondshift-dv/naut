using System.IO;

namespace Neuterradise.App.SystemServices.Cache;

public sealed class CacheMaintenanceOperations
{
    private readonly CachePaths _paths;
    private readonly PersistentCacheBudget _budget;

    public CacheMaintenanceOperations(CachePaths paths, PersistentCacheBudget budget)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(budget);

        _paths = paths;
        _budget = budget;
    }

    public CachePaths Paths => _paths;

    public PersistentCacheBudget Budget => _budget;

    public Task<int> CleanOrphanCacheAsync(ISet<string> activeKeysOrPaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activeKeysOrPaths);

        return Task.Run(() =>
        {
            if (!Directory.Exists(_paths.CacheRoot))
            {
                return 0;
            }

            var deleted = 0;
            var families = new[]
            {
                _paths.FaceCropsPath,
                _paths.ModelPreviewsPath,
            };

            foreach (var familyDir in families)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(familyDir))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(familyDir, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!_paths.IsContainedCachePath(file))
                    {
                        continue;
                    }

                    if (_budget.IsPinned(file))
                    {
                        continue;
                    }

                    var relativeToCache = Path.GetRelativePath(_paths.CacheRoot, file).Replace('\\', '/');
                    var relativeToFamily = Path.GetRelativePath(familyDir, file).Replace('\\', '/');

                    if (!activeKeysOrPaths.Contains(file) &&
                        !activeKeysOrPaths.Contains(relativeToCache) &&
                        !activeKeysOrPaths.Contains(relativeToFamily))
                    {
                        if (TryDeleteFile(file))
                        {
                            _budget.Forget(file);
                            deleted++;
                        }
                    }
                }
            }

            return deleted;
        }, ct);
    }

    public async Task<int> ClearAllDerivedCacheAsync(CancellationToken ct = default)
    {
        var total = 0;
        foreach (var family in Enum.GetValues<CacheFamily>())
            total += await ResetFamilyAsync(family, ct).ConfigureAwait(false);
        return total;
    }

    private Task<int> ResetFamilyAsync(CacheFamily family, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var familyDir = _paths.GetFamilyPath(family);
            if (!Directory.Exists(familyDir))
            {
                return 0;
            }

            var deleted = 0;
            foreach (var file in Directory.EnumerateFiles(familyDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (!_paths.IsContainedCachePath(file))
                {
                    continue;
                }

                if (TryDeleteFile(file))
                {
                    _budget.Forget(file);
                    deleted++;
                }
            }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(familyDir, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length))
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir, recursive: false);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return deleted;
        }, ct);
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return false;
    }
}
