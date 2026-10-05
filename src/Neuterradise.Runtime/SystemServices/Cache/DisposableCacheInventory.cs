using System.Globalization;

namespace Neuterradise.App.SystemServices.Cache;

/// <summary>Disposable in-process lookup for legacy cache files. The catalog owns durable MediaAssets.</summary>
public sealed class DisposableCacheInventory
{
    private readonly CachePaths _paths;
    private readonly object _gate = new();
    private readonly Dictionary<string, DisposableCacheInventoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _totalBytes;

    public DisposableCacheInventory(CachePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        foreach (var family in Enum.GetValues<CacheFamily>())
        {
            if (family == CacheFamily.Temp) continue;
            var root = _paths.GetFamilyPath(family);
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 0)
                        Record(path, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public long TotalBytes { get { lock (_gate) return _totalBytes; } }

    public bool TryResolve(CacheFamily family, string relativePathWithinFamily,
        out string physicalPath, out DisposableCacheInventoryEntry? entry)
    {
        physicalPath = string.Empty;
        entry = null;
        if (family == CacheFamily.Temp || string.IsNullOrWhiteSpace(relativePathWithinFamily)) return false;
        var relative = Path.Combine(_paths.GetFamilyDirectoryName(family), relativePathWithinFamily)
            .Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        lock (_gate)
        {
            if (!_entries.TryGetValue(MakeKey(family, relative), out entry)) return false;
            physicalPath = _paths.ResolveContainedCachePath(entry.RelativePath);
            return true;
        }
    }

    public IReadOnlyList<DisposableCacheInventoryEntry> GetEvictionCandidates()
    {
        lock (_gate)
            return _entries.Values.OrderBy(x => x.LastAccessedAtUtc)
                .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void RecordPublished(string physicalPath, long byteLength)
    {
        if (byteLength > 0) Record(physicalPath, byteLength, DateTimeOffset.UtcNow);
    }

    public void RecordAccess(string physicalPath, DateTimeOffset? timestamp = null)
    {
        if (!TryDescribePath(physicalPath, out var family, out var relative)) return;
        lock (_gate)
        {
            var key = MakeKey(family, relative);
            if (_entries.TryGetValue(key, out var entry))
                _entries[key] = entry with { LastAccessedAtUtc = timestamp ?? DateTimeOffset.UtcNow };
        }
    }

    public void Forget(string physicalPath)
    {
        if (TryDescribePath(physicalPath, out var family, out var relative))
            ForgetKey(MakeKey(family, relative));
    }

    public int ForgetUnderDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return 0;
        var full = Path.GetFullPath(directory);
        var prefix = Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
        string[] keys;
        lock (_gate)
            keys = _entries.Where(pair => _paths.ResolveContainedCachePath(pair.Value.RelativePath)
                    .StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key).ToArray();
        foreach (var key in keys) ForgetKey(key);
        return keys.Length;
    }

    public int ForgetFamily(CacheFamily family)
    {
        string[] keys;
        lock (_gate) keys = _entries.Where(pair => pair.Value.Family == family)
            .Select(pair => pair.Key).ToArray();
        foreach (var key in keys) ForgetKey(key);
        return keys.Length;
    }

    private void Record(string path, long bytes, DateTimeOffset accessed)
    {
        if (!TryDescribePath(path, out var family, out var relative)) return;
        var metadata = ParseMetadata(relative);
        var entry = new DisposableCacheInventoryEntry(family, relative, metadata.EntityId,
            metadata.ContentIdentity, metadata.DerivationVersion, metadata.VariantKey, bytes, accessed);
        lock (_gate)
        {
            var key = MakeKey(family, relative);
            if (_entries.TryGetValue(key, out var previous)) _totalBytes -= previous.ByteLength;
            _entries[key] = entry;
            _totalBytes += bytes;
        }
    }

    private void ForgetKey(string key)
    {
        lock (_gate)
        {
            if (_entries.Remove(key, out var removed))
                _totalBytes = Math.Max(0, _totalBytes - removed.ByteLength);
        }
    }

    private bool TryDescribePath(string physicalPath, out CacheFamily family, out string relative)
    {
        family = CacheFamily.Temp;
        relative = string.Empty;
        if (string.IsNullOrWhiteSpace(physicalPath) || !_paths.IsContainedCachePath(physicalPath)) return false;
        relative = Path.GetRelativePath(_paths.CacheRoot, Path.GetFullPath(physicalPath))
            .Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        var first = relative.Split('/')[0];
        foreach (var candidate in Enum.GetValues<CacheFamily>())
        {
            if (candidate != CacheFamily.Temp && string.Equals(first,
                _paths.GetFamilyDirectoryName(candidate), StringComparison.OrdinalIgnoreCase))
            {
                family = candidate;
                return true;
            }
        }
        return false;
    }

    private static (string? EntityId, string? ContentIdentity, int? DerivationVersion,
        string? VariantKey) ParseMetadata(string relative)
    {
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var entity = segments.Length >= 2 ? segments[^2] : null;
        var stem = Path.GetFileNameWithoutExtension(relative);
        var marker = stem.IndexOf("_v", StringComparison.Ordinal);
        if (marker <= 0) return (entity, null, null, stem);
        var variant = stem.IndexOf('_', marker + 2);
        if (variant < 0) return (entity, stem[..marker], null, null);
        var version = int.TryParse(stem[(marker + 2)..variant], NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null;
        return (entity, stem[..marker], version,
            variant + 1 < stem.Length ? stem[(variant + 1)..] : null);
    }

    private static string MakeKey(CacheFamily family, string relative) =>
        $"{family}:{relative.Replace('\\', '/')}";
}

public sealed record DisposableCacheInventoryEntry(CacheFamily Family, string RelativePath,
    string? EntityId, string? ContentIdentity, int? DerivationVersion,
    string? VariantKey, long ByteLength, DateTimeOffset LastAccessedAtUtc);
