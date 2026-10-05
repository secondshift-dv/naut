using Neuterradise.App.Import;
using Neuterradise.App.SystemServices.Database.Writes;

namespace Neuterradise.App.SystemServices.Storage;

/// <summary>
/// Final import-source settlement. Naut imports are COPY-only: verified publication preserves the
/// external source and no import path is authorized to delete it.
/// </summary>
public sealed class SourceCleanupExecutor
{
    private readonly ImportWrites _importWrites;

    public SourceCleanupExecutor(ImportWrites importWrites)
    {
        _importWrites = importWrites ?? throw new ArgumentNullException(nameof(importWrites));
    }

    public async Task<StorageOperationResult> ExecuteAsync(
        Guid importItemId,
        CancellationToken cancellationToken = default)
    {
        if (importItemId == Guid.Empty)
        {
            throw new ArgumentException("A stable ImportItemId cannot be empty.", nameof(importItemId));
        }

        var obligation = await _importWrites
            .ReadSourceCleanupObligationAsync(importItemId, cancellationToken)
            .ConfigureAwait(false);
        if (obligation is null)
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The persisted source-cleanup obligation does not exist.");
        }

        if (obligation.CleanupPolicy != ImportCleanupPolicy.Copy)
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The import cleanup policy is not COPY.");
        }

        if (obligation.SourceCleanupState == SourceCleanupState.SourcePreserved)
        {
            return new StorageOperationResult(StorageOperationStatus.AlreadyCompleted);
        }

        await _importWrites
            .MarkSourceCleanupPreservedAsync(obligation, CancellationToken.None)
            .ConfigureAwait(false);
        return new StorageOperationResult(StorageOperationStatus.Success);
    }
}
