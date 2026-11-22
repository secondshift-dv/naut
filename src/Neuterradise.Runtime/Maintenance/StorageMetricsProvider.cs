using System.IO;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Maintenance;

public sealed class StorageMetricsProvider
{
    private readonly CatalogDb _catalog;
    private readonly CachePaths _cachePaths;
    private readonly PersistentCacheBudget _cacheBudget;
    private readonly TimeProvider _timeProvider;

    public StorageMetricsProvider(
        CatalogDb catalog,
        PersistentCacheBudget? cacheBudget = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _cachePaths = new CachePaths(catalog.Paths);
        _cacheBudget = cacheBudget ?? new PersistentCacheBudget(_cachePaths);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CachePaths CachePaths => _cachePaths;

    public PersistentCacheBudget CacheBudget => _cacheBudget;

    private readonly object _snapshotGate = new();
    private Task<StorageMetricsSnapshot>? _pendingSnapshot;
    private StorageMetricsSnapshot? _lastSnapshot;
    private long _snapshotGeneration;

    public StorageMetricsSnapshot? LastSnapshot
    {
        get { lock (_snapshotGate) return _lastSnapshot; }
    }

    public async Task<StorageMetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<StorageMetricsSnapshot> pending;
            long generation;
            lock (_snapshotGate)
            {
                generation = _snapshotGeneration;
                if (_pendingSnapshot is null || _pendingSnapshot.IsCompleted)
                {
                    _pendingSnapshot = Task.Run(() => ReadAndPublishSnapshotAsync(generation));
                    _ = _pendingSnapshot.ContinueWith(static task => { _ = task.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                pending = _pendingSnapshot;
            }

            var snapshot = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_snapshotGate)
            {
                if (generation == _snapshotGeneration) return snapshot;
            }
        }
    }

    internal void InvalidateSnapshot()
    {
        lock (_snapshotGate)
        {
            _snapshotGeneration++;
            _pendingSnapshot = null;
        }
    }

    private async Task<StorageMetricsSnapshot> ReadAndPublishSnapshotAsync(long generation)
    {
        var snapshot = await ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_snapshotGate)
        {
            if (_snapshotGeneration == generation)
                _lastSnapshot = snapshot;
        }
        return snapshot;
    }

    private async Task<StorageMetricsSnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var healthSummary = await _catalog.HealthReads.GetHealthSummaryAsync(cancellationToken)
            .ConfigureAwait(false);
        var authorityCounts = await _catalog.HealthReads.GetStorageMetricsCountsAsync(cancellationToken)
            .ConfigureAwait(false);
        var integrityFindings = await _catalog.HealthReads.GetDatabaseIntegrityFindingsAsync(cancellationToken)
            .ConfigureAwait(false);

        var filesystem = ComputeFilesystemMetrics(cancellationToken);

        var artifactBytes = await GetArtifactBytesAsync(cancellationToken).ConfigureAwait(false);

        return new StorageMetricsSnapshot(
            ManagedMediaBytes: healthSummary.ActiveManagedByteLength,
            CacheBytesByFamily: filesystem.CacheBytesByFamily,
            CacheBytesTotal: filesystem.CacheBytesTotal,
            CacheQuotaBytes: _cacheBudget.QuotaBytes,
            TrashBytes: filesystem.TrashBytes,
            StagingBytes: filesystem.StagingBytes,
            DatabaseBytes: filesystem.DatabaseBytes,
            ProfileCount: healthSummary.TotalProfiles,
            ActiveMediaCount: healthSummary.ActiveMedias,
            TrashItemCount: authorityCounts.TrashEntryCount,
            PendingCleanupCount: authorityCounts.PendingSourceCleanupCount,
            HealthFindingCount: integrityFindings.Count,
            EvaluatedAtUtc: _timeProvider.GetUtcNow())
        {
            MediaAssetBytes = artifactBytes,
            TemporaryBytes = filesystem.TemporaryBytes,
            UnresolvedFaceCount = healthSummary.UnresolvedFaceDetections,
        };
    }

    private async Task<long> GetArtifactBytesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              coalesce(sum(aa.byte_length), 0) FROM media_assets aa
              JOIN media a ON a.media_id = aa.media_id
              WHERE a.state = 'ACTIVE' AND a.trashed_at_ms IS NULL;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return reader.GetInt64(0);
    }

    private FilesystemMetrics ComputeFilesystemMetrics(CancellationToken cancellationToken)
    {
        var cacheBytesByFamily = new Dictionary<CacheFamily, long>();
        long cacheBytesTotal = 0;
        foreach (var family in Enum.GetValues<CacheFamily>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var familyBytes = ComputeDirectoryBytes(_cachePaths.GetFamilyPath(family), cancellationToken);
            cacheBytesByFamily[family] = familyBytes;
            cacheBytesTotal += familyBytes;
        }

        var stagingBytes = ComputeDirectoryBytes(_catalog.Paths.StagingPath, cancellationToken);
        var temporaryBytes = ComputeDirectoryBytes(_catalog.Paths.TempPath, cancellationToken);
        var trashBytes = ComputeDirectoryBytes(_catalog.Paths.TrashPath, cancellationToken);
        var databaseBytes = ComputeDatabaseBytes();

        return new FilesystemMetrics(cacheBytesByFamily, cacheBytesTotal, stagingBytes,
            temporaryBytes, trashBytes, databaseBytes);
    }

    private static long ComputeDirectoryBytes(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        var root = new DirectoryInfo(directory);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Storage metrics cannot read a redirected directory.");

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var file in root.EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += file.Length;
        }

        return total;
    }

    private long ComputeDatabaseBytes()
    {
        long total = 0;
        foreach (var suffix in DatabaseSidecarSuffixes)
        {
            var path = _catalog.Paths.CatalogDbPath + suffix;
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }

    private static readonly string[] DatabaseSidecarSuffixes = ["", "-wal", "-shm"];

    private sealed record FilesystemMetrics(
        IReadOnlyDictionary<CacheFamily, long> CacheBytesByFamily,
        long CacheBytesTotal,
        long StagingBytes,
        long TemporaryBytes,
        long TrashBytes,
        long DatabaseBytes);
}
