using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.Import;

namespace Neuterradise.App.Maintenance;

public sealed record LibraryHealthSummary(
    long TotalProfiles,
    long NormalProfiles,
    long UnknownProfiles,
    long TotalMedias,
    long ActiveMedias,
    long CandidateMedias,
    long TrashedMedias,
    long RetiredMedias,
    long PendingReconciliationCount,
    long NeedsAttentionReconciliationCount,
    long UnresolvedFaceDetections,
    long ActiveJobsCount,
    long FailedJobsCount,
    DateTimeOffset EvaluatedAtUtc,
    long ActiveManagedByteLength = 0);

public sealed record HealthScanResult(
    IReadOnlyList<HealthFinding> Findings,
    HealthScanMode Mode,
    int ItemsScanned,
    TimeSpan Duration,
    DateTimeOffset CompletedAtUtc)
{
    public bool IsHealthy => Findings.Count == 0;
    public int CriticalCount => Findings.Count(f => f.Severity == HealthSeverity.Critical);
    public int ErrorCount => Findings.Count(f => f.Severity == HealthSeverity.Error);
    public int WarningCount => Findings.Count(f => f.Severity == HealthSeverity.Warning);
    public int InfoCount => Findings.Count(f => f.Severity == HealthSeverity.Info);
}

public sealed record HealthProfileItem(
    Guid ProfileId,
    ProfileKind Kind,
    string DisplayName,
    string? StorageToken,
    string? CurrentManagedRelativePath,
    ManagedPathState PathState,
    bool IsTrashed,
    long RowVersion,
    string? TargetManagedRelativePath = null);

public sealed record HealthMediaItem(
    Guid MediaId,
    MediaType MediaType,
    string Extension,
    string? StorageToken,
    string? Sha256,
    long ByteLength,
    string? CurrentManagedRelativePath,
    MediaState State,
    bool IsTrashed,
    Guid? OwnerProfileId,
    string? OwnerDisplayName,
    string? OwnerStorageToken,
    string? CurrentManagedFileName = null);

public sealed record HealthSourceCleanupItem(
    Guid ImportItemId,
    Guid MediaId,
    long RowVersion,
    SourceCleanupState SourceCleanupState,
    string SourcePath,
    string ExpectedSha256,
    long ExpectedByteLength,
    string CurrentManagedRelativePath,
    string CurrentManagedFileName);

public sealed record StorageMetricsSnapshot(
    long ManagedMediaBytes,
    IReadOnlyDictionary<CacheFamily, long> CacheBytesByFamily,
    long CacheBytesTotal,
    long CacheQuotaBytes,
    long TrashBytes,
    long StagingBytes,
    long DatabaseBytes,
    long ProfileCount,
    long ActiveMediaCount,
    long TrashItemCount,
    long PendingCleanupCount,
    long HealthFindingCount,
    DateTimeOffset EvaluatedAtUtc)
{
    public long MediaAssetBytes { get; init; }
    public long TemporaryBytes { get; init; }
}

public sealed record StorageMetricsAuthorityCounts(
    long TrashEntryCount,
    long TrashedMediaByteLength,
    long PendingSourceCleanupCount);
