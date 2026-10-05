using System.Collections.Concurrent;
using System.IO;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Cache;

public sealed class PersistentCacheBudget
{
    public const long MinQuotaBytes = 1L * 1024 * 1024 * 1024;
    public const long MaxQuotaBytes = 100L * 1024 * 1024 * 1024;
    public const long DefaultQuotaBytes = 10L * 1024 * 1024 * 1024;
    public const long RequiredDerivedBudgetBytes = 2L * 1024 * 1024 * 1024;

    private readonly CachePaths _paths;
    private readonly IClock _clock;
    private readonly DisposableCacheInventory _index;
    private readonly object _syncLock = new();

    private readonly ConcurrentDictionary<string, int> _pinnedPaths = new(StringComparer.OrdinalIgnoreCase);
    private long _quotaBytes;

    public PersistentCacheBudget(
        CachePaths paths,
        long quotaBytes = DefaultQuotaBytes,
        IClock? clock = null,
        DisposableCacheInventory? index = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ValidateQuota(quotaBytes);

        _paths = paths;
        _quotaBytes = quotaBytes;
        _clock = clock ?? new SystemClock();
        _index = index ?? new DisposableCacheInventory(paths);
    }

    public long QuotaBytes
    {
        get => Interlocked.Read(ref _quotaBytes);
        set
        {
            ValidateQuota(value);
            Interlocked.Exchange(ref _quotaBytes, value);
        }
    }

    public CachePaths Paths => _paths;

    public DisposableCacheInventory Index => _index;

    public static void ValidateQuota(long bytes)
    {
        if (bytes < MinQuotaBytes || bytes > MaxQuotaBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytes),
                bytes,
                $"Persistent cache quota must be between {MinQuotaBytes} (1 GiB) and {MaxQuotaBytes} (100 GiB) bytes.");
        }
    }

    public IDisposable AcquireReadLease(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = NormalizePath(path);

        _pinnedPaths.AddOrUpdate(normalized, 1, (_, count) => count + 1);
        RecordAccess(normalized);

        return new CacheReadLease(this, normalized);
    }

    public bool IsPinned(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = NormalizePath(path);
        return _pinnedPaths.TryGetValue(normalized, out var count) && count > 0;
    }

    /// <summary>
    /// Records cache recency in the disposable cache inventory. This deliberately does not touch
    /// filesystem last-access timestamps, which would turn normal rendering into filesystem writes.
    /// </summary>
    public void RecordAccess(string path, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _index.RecordAccess(NormalizePath(path), now ?? _clock.UtcNow);
    }

    public void RecordPublished(string path, long byteLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(byteLength, 0);
        _index.RecordPublished(NormalizePath(path), byteLength);
    }

    public void Forget(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _index.Forget(NormalizePath(path));
        }
    }

    public int ForgetUnderDirectory(string directory) =>
        string.IsNullOrWhiteSpace(directory) ? 0 : _index.ForgetUnderDirectory(NormalizePath(directory));

    public int ForgetFamily(CacheFamily family) => _index.ForgetFamily(family);

    public long GetTotalCurrentCacheBytes() => _index.TotalBytes;

    public bool EnsureCapacity(long incomingBytes, out long freedBytes)
    {
        freedBytes = 0;
        var currentQuota = QuotaBytes;

        if (incomingBytes > currentQuota)
        {
            return false;
        }

        lock (_syncLock)
        {
            var currentBytes = _index.TotalBytes;
            if (currentBytes + incomingBytes <= currentQuota)
            {
                return true;
            }

            foreach (var candidate in _index.GetEvictionCandidates())
            {
                var path = _paths.ResolveContainedCachePath(candidate.RelativePath);
                if (IsPinned(path))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    freedBytes += candidate.ByteLength;
                    _index.Forget(path);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if (_index.TotalBytes + incomingBytes <= currentQuota)
                {
                    return true;
                }
            }

            return _index.TotalBytes + incomingBytes <= currentQuota;
        }
    }

    private void ReleaseReadLease(string normalizedPath)
    {
        _pinnedPaths.AddOrUpdate(normalizedPath, 0, (_, count) => Math.Max(0, count - 1));
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private sealed class CacheReadLease : IDisposable
    {
        private readonly PersistentCacheBudget _budget;
        private readonly string _normalizedPath;
        private int _disposed;

        public CacheReadLease(PersistentCacheBudget budget, string normalizedPath)
        {
            _budget = budget;
            _normalizedPath = normalizedPath;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _budget.ReleaseReadLease(_normalizedPath);
            }
        }
    }
}
