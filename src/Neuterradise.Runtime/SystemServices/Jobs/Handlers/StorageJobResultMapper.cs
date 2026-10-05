using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Jobs.Handlers;

internal static class StorageJobResultMapper
{
    public static JobExecutionResult Map(StorageOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsSuccess)
        {
            return JobExecutionResult.Succeeded;
        }

        var detail = string.IsNullOrWhiteSpace(result.SafeErrorDetail)
            ? $"Storage operation ended with {result.Status}."
            : result.SafeErrorDetail;

        return result.Status switch
        {
            StorageOperationStatus.Cancelled => JobExecutionResult.Cancelled(detail),
            StorageOperationStatus.FileLocked => JobExecutionResult.Failed(
                JobFailureClassification.FileLocked,
                "STORAGE_FILE_LOCKED",
                detail),
            StorageOperationStatus.AccessDenied => JobExecutionResult.Failed(
                JobFailureClassification.AccessTemporarilyDenied,
                "STORAGE_ACCESS_DENIED",
                detail),
            StorageOperationStatus.VolumeResolutionFailed => JobExecutionResult.Failed(
                JobFailureClassification.TransientIo,
                "STORAGE_VOLUME_RESOLUTION_FAILED",
                detail),
            StorageOperationStatus.Failed => JobExecutionResult.Failed(
                JobFailureClassification.TransientIo,
                "STORAGE_OPERATION_FAILED",
                detail),
            StorageOperationStatus.SourceChanged or StorageOperationStatus.VerificationFailed => JobExecutionResult.Failed(
                JobFailureClassification.ContentMismatch,
                $"STORAGE_{result.Status.ToString().ToUpperInvariant()}",
                detail),
            StorageOperationStatus.SourceMissing
                or StorageOperationStatus.TargetCollision
                or StorageOperationStatus.UnexpectedTarget
                or StorageOperationStatus.PathOutsideVault
                or StorageOperationStatus.NeedsAttention => JobExecutionResult.Failed(
                    JobFailureClassification.AmbiguousPhysicalState,
                    $"STORAGE_{result.Status.ToString().ToUpperInvariant()}",
                    detail),
            _ => JobExecutionResult.Failed(
                JobFailureClassification.AmbiguousPhysicalState,
                "STORAGE_UNKNOWN_FAILURE",
                detail),
        };
    }
}
