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

    public async Task<StorageMetricsSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var healthSummary = await _catalog.HealthReads.GetHealthSummaryAsync(cancellationToken)
            .ConfigureAwait(false);
        var authorityCounts = await _catalog.HealthReads.GetStorageMetricsCountsAsync(cancellationToken)
            .ConfigureAwait(false);
        var integrityFindings = await _catalog.HealthReads.GetDatabaseIntegrityFindingsAsync(cancellationToken)
            .ConfigureAwait(false);

        var filesystem = await Task.Run(
                () => ComputeFilesystemMetrics(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

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
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
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
