namespace Neuterradise.App.SystemServices.Storage;

public sealed record StorageOperationResult(
    StorageOperationStatus Status,
    ManagedFileVerificationStatus? VerificationStatus = null,
    string? SafeErrorDetail = null,
    long? BytesProcessed = null,
    TimeSpan? Duration = null)
{
    public bool IsSuccess => Status is
        StorageOperationStatus.Success or StorageOperationStatus.AlreadyCompleted;

    public double? ThroughputBytesPerSecond =>
        BytesProcessed is long bytes
        && Duration is TimeSpan duration
        && duration > TimeSpan.Zero
            ? bytes / duration.TotalSeconds
            : null;
}

public enum StorageOperationStatus
{
    Success,
    AlreadyCompleted,
    SourceMissing,
    SourceChanged,
    TargetCollision,
    UnexpectedTarget,
    VerificationFailed,
    AccessDenied,
    FileLocked,
    VolumeResolutionFailed,
    PathOutsideVault,
    Cancelled,
    NeedsAttention,
    Failed,
}
