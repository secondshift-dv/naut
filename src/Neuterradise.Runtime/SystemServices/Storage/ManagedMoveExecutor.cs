using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Neuterradise.App.Import;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs.Handlers;
using Neuterradise.App.Trash;

namespace Neuterradise.App.SystemServices.Storage;

public sealed class ManagedMoveExecutor
{
    private const int _copyBufferSize = 1024 * 1024;

    private readonly VaultPaths _paths;
    private readonly IVolumeIdentityProvider _volumeIdentityProvider;
    private readonly ManagedFileVerifier _verifier;
    private readonly MediaWrites _assetWrites;

    public ManagedMoveExecutor(
        VaultPaths paths,
        IVolumeIdentityProvider volumeIdentityProvider,
        ManagedFileVerifier verifier,
        MediaWrites assetWrites)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _volumeIdentityProvider = volumeIdentityProvider
            ?? throw new ArgumentNullException(nameof(volumeIdentityProvider));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _assetWrites = assetWrites ?? throw new ArgumentNullException(nameof(assetWrites));
    }

    public async Task<StorageOperationResult> ExecuteAsync(
        Guid assetId,
        bool preserveSourceTimestamps = false,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("A stable MediaId cannot be empty.", nameof(assetId));
        }

        try
        {
            return await ExecuteCoreAsync(assetId, preserveSourceTimestamps, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }
    }

    public async Task<StorageOperationResult> ExecuteCrossVolumeAsync(
        Guid importItemId,
        bool preserveSourceTimestamps = false,
        CancellationToken cancellationToken = default)
    {
        if (importItemId == Guid.Empty)
        {
            throw new ArgumentException("A stable ImportItemId cannot be empty.", nameof(importItemId));
        }

        try
        {
            return await ExecuteCrossVolumeCoreAsync(importItemId, preserveSourceTimestamps, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }
    }

    private async Task<StorageOperationResult> ExecuteCoreAsync(
        Guid assetId,
        bool preserveSourceTimestamps,
        CancellationToken cancellationToken)
    {
        var plan = await _assetWrites.ReadSameVolumeMovePlanAsync(assetId, cancellationToken)
            .ConfigureAwait(false);
        if (plan is null)
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The persisted Media Move plan does not exist.");
        }

        if (!Path.IsPathFullyQualified(plan.SourcePath))
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The persisted source path is not fully qualified.");
        }

        string targetPath;
        try
        {
            targetPath = _paths.ResolveVaultRelativePath(
                CombineRelative(plan.TargetManagedRelativePath, plan.TargetManagedFileName));
        }
        catch (ArgumentException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }

        if (string.Equals(
                Path.GetFullPath(plan.SourcePath),
                targetPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The external source and managed target resolve to the same path.");
        }

        var authority = await _assetWrites.ResolveTargetAuthorityAsync(plan, cancellationToken)
            .ConfigureAwait(false);
        if (authority == ManagedTargetAuthority.OtherMedia)
        {
            return new StorageOperationResult(StorageOperationStatus.TargetCollision);
        }

        var components = await _assetWrites.GetMediaComponentsAsync(assetId, cancellationToken)
            .ConfigureAwait(false);
        var isMultiFile = components.Count > 1
            || (components.Count == 1 && components.Any(c => c.ComponentRole == ComponentRole.Dependency));
        if (isMultiFile)
        {
            var pkgResult = await ExecuteModelPackagePlacementAsync(
                assetId,
                plan.TargetManagedRelativePath,
                plan.SourcePath,
                plan.ReconciliationOperationId
                    ?? throw new InvalidOperationException("A pending placement has no durable OperationId."),
                components,
                preserveSourceTimestamps,
                cancellationToken).ConfigureAwait(false);
            if (!pkgResult.IsSuccess)
            {
                return pkgResult;
            }

            await _assetWrites.CheckpointSameVolumePlacementAsync(plan, cancellationToken)
                .ConfigureAwait(false);
            return pkgResult;
        }

        var targetExists = File.Exists(targetPath);
        if (targetExists)
        {
            // The persisted path/operation authority already proved that this target belongs
            // to this Media. A crash can leave both source and fully-published target present while
            // the durable placement checkpoint is behind. Verify bytes and converge instead of
            // treating that idempotent replay state as a collision.
            var existingVerification = await _verifier.VerifyAsync(
                    targetPath,
                    plan.ExpectedByteLength,
                    plan.ExpectedSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!existingVerification.IsMatch)
            {
                return MapTargetVerification(existingVerification);
            }

            await _assetWrites.CheckpointSameVolumePlacementAsync(plan, cancellationToken)
                .ConfigureAwait(false);
            return new StorageOperationResult(
                StorageOperationStatus.AlreadyCompleted,
                existingVerification.Status);
        }

        var publish = await PublishStagedOriginalAsync(
            plan.MediaId, targetPath, plan.ExpectedByteLength, plan.ExpectedSha256,
            cancellationToken).ConfigureAwait(false);
        if (!publish.IsSuccess)
        {
            return publish;
        }

        if (preserveSourceTimestamps)
        {
            ApplySourceTimestamp(plan.SourcePath, targetPath);
        }

        await _assetWrites.CheckpointSameVolumePlacementAsync(plan, cancellationToken)
            .ConfigureAwait(false);
        return publish;
    }

    private async Task<StorageOperationResult> ExecuteCrossVolumeCoreAsync(
        Guid importItemId,
        bool preserveSourceTimestamps,
        CancellationToken cancellationToken)
    {
        var plan = await _assetWrites.ReadCrossVolumeMovePlanAsync(importItemId, cancellationToken)
            .ConfigureAwait(false);
        if (plan is null)
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The persisted cross-volume Move plan does not exist.");
        }

        if (!Path.IsPathFullyQualified(plan.SourcePath))
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The persisted source path is not fully qualified.");
        }

        if (plan.ReconciliationOperationId is not { } operationId)
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "A pending cross-volume placement has no durable OperationId.");
        }

        string targetPath;
        try
        {
            var targetRelativePath = CombineRelative(
                plan.TargetManagedRelativePath,
                plan.TargetManagedFileName);
            targetPath = _paths.ResolveVaultRelativePath(targetRelativePath);
        }
        catch (ArgumentException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }

        if (string.Equals(Path.GetFullPath(plan.SourcePath), targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The external source overlaps a managed cross-volume path.");
        }

        var authority = await _assetWrites.ResolveTargetAuthorityAsync(plan, cancellationToken)
            .ConfigureAwait(false);
        if (authority == ManagedTargetAuthority.OtherMedia)
        {
            return new StorageOperationResult(StorageOperationStatus.TargetCollision);
        }

        var sourceVolume = _volumeIdentityProvider.TryGetVolumeIdentity(plan.SourcePath);
        var destinationVolume = _volumeIdentityProvider.TryGetVolumeIdentity(_paths.ProfilesPath);
        if (sourceVolume is not null
            && destinationVolume is not null
            && sourceVolume == destinationVolume)
        {
            return new StorageOperationResult(
                StorageOperationStatus.VolumeResolutionFailed,
                SafeErrorDetail: "The persisted plan resolves to one volume and must use same-volume Move.");
        }

        var components = await _assetWrites.GetMediaComponentsAsync(plan.MediaId, cancellationToken)
            .ConfigureAwait(false);
        var isMultiFile = components.Count > 1
            || (components.Count == 1 && components.Any(c => c.ComponentRole == ComponentRole.Dependency));
        if (isMultiFile)
        {
            var pkgResult = await ExecuteModelPackagePlacementAsync(
                plan.MediaId,
                plan.TargetManagedRelativePath,
                plan.SourcePath,
                operationId,
                components,
                preserveSourceTimestamps,
                cancellationToken).ConfigureAwait(false);
            if (!pkgResult.IsSuccess)
            {
                return pkgResult;
            }

            await _assetWrites.CheckpointCrossVolumePlacementAsync(plan, cancellationToken)
                .ConfigureAwait(false);
            return pkgResult;
        }

        if (File.Exists(targetPath))
        {
            var existingVerification = await _verifier.VerifyAsync(
                    targetPath,
                    plan.ExpectedByteLength,
                    plan.ExpectedSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!existingVerification.IsMatch)
            {
                return MapTargetVerification(existingVerification);
            }

            await _assetWrites.CheckpointCrossVolumePlacementAsync(plan, cancellationToken)
                .ConfigureAwait(false);
            return new StorageOperationResult(
                StorageOperationStatus.AlreadyCompleted,
                existingVerification.Status);
        }

        var published = await PublishStagedOriginalAsync(plan.MediaId, targetPath,
            plan.ExpectedByteLength, plan.ExpectedSha256, cancellationToken).ConfigureAwait(false);
        if (!published.IsSuccess) return published;
        if (preserveSourceTimestamps) ApplySourceTimestamp(plan.SourcePath, targetPath);
        await _assetWrites.CheckpointCrossVolumePlacementAsync(plan, cancellationToken).ConfigureAwait(false);
        return published;
    }

    private async Task<StorageOperationResult> PublishStagedOriginalAsync(
        Guid assetId, string targetPath, long expectedLength, string expectedSha,
        CancellationToken cancellationToken)
    {
        var transfer = await _assetWrites.ReadCompletedTransferAsync(assetId, cancellationToken)
            .ConfigureAwait(false);
        if (transfer is null)
            return new StorageOperationResult(StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "A completed TransferOriginal job is required for placement.");

        var stagedPath = _paths.ResolveStagedOriginalPartialPath(
            transfer.Value.ImportUnitId, new MediaStorageToken(transfer.Value.StorageToken));
        var relativePath = Path.GetRelativePath(_paths.Root, stagedPath).Replace('\\', '/');
        var completedInThisProcess = false;
        try
        {
            using var document = JsonDocument.Parse(transfer.Value.CheckpointJson);
            var checkpoint = document.RootElement;
            if (checkpoint.GetProperty("SchemaVersion").GetInt32() != 1
                || checkpoint.GetProperty("CandidateMediaId").GetGuid() != assetId
                || checkpoint.GetProperty("ImportUnitId").GetGuid() != transfer.Value.ImportUnitId
                || checkpoint.GetProperty("ExpectedLength").GetInt64() != expectedLength
                || checkpoint.GetProperty("SafeOffset").GetInt64() != expectedLength
                || !string.Equals(checkpoint.GetProperty("PrefixSha256").GetString(), expectedSha,
                    StringComparison.Ordinal)
                || !string.Equals(checkpoint.GetProperty("PartialRelativePath").GetString(), relativePath,
                    StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(checkpoint.GetProperty("SourceIdentity").GetString()))
                return new StorageOperationResult(StorageOperationStatus.NeedsAttention,
                    SafeErrorDetail: "The completed transfer checkpoint does not match placement authority.");
            completedInThisProcess = checkpoint.TryGetProperty("ProcessNonce", out var processNonce)
                && processNonce.TryGetGuid(out var nonce)
                && nonce == TransferOriginalJobHandler.ProcessNonce;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new StorageOperationResult(StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The completed transfer checkpoint is invalid.");
        }

        RootPathRules.RejectExistingReparsePoints(_paths.Root, stagedPath);
        if (!File.Exists(stagedPath) || new FileInfo(stagedPath).Length != expectedLength)
            return new StorageOperationResult(StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "Completed staged bytes are missing or shorter than the transfer checkpoint.");
        if (!completedInThisProcess)
        {
            var recovered = await _verifier.VerifyAsync(stagedPath, expectedLength, expectedSha, cancellationToken)
                .ConfigureAwait(false);
            if (!recovered.IsMatch) return MapTargetVerification(recovered);
        }
        if (!VolumeIdentity.CanUseSameVolumeMove(_volumeIdentityProvider, stagedPath, _paths.ProfilesPath))
            return new StorageOperationResult(StorageOperationStatus.VolumeResolutionFailed);
        try
        {
            // A current-process completion needs no destination reread. Restart replay verifies
            // staged bytes above; an already existing target is verified by the caller.
            RootPathRules.RejectExistingReparsePoints(_paths.Root, stagedPath);
            RootPathRules.RejectExistingReparsePoints(_paths.Root, targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            RootPathRules.RejectExistingReparsePoints(_paths.Root, targetPath);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedPath, targetPath);
            return new StorageOperationResult(StorageOperationStatus.Success,
                BytesProcessed: expectedLength);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageOperationResult(StorageOperationStatus.AccessDenied);
        }
        catch (IOException)
        {
            return new StorageOperationResult(File.Exists(targetPath)
                ? StorageOperationStatus.UnexpectedTarget : StorageOperationStatus.FileLocked);
        }
    }

    private async Task<StorageOperationResult> ExecuteModelPackagePlacementAsync(
        Guid assetId,
        string targetManagedRelativePath,
        string primarySourcePath,
        Guid operationId,
        IReadOnlyList<MediaComponentRecord> components,
        bool preserveSourceTimestamps,
        CancellationToken cancellationToken)
    {
        var primaryDir = Path.GetDirectoryName(primarySourcePath) ?? string.Empty;
        long totalBytes = 0;

        foreach (var c in components)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var compSourcePath = !string.IsNullOrWhiteSpace(c.OriginalSourcePath)
                ? c.OriginalSourcePath
                : Path.Combine(primaryDir, c.ComponentRelativePath.Replace('/', Path.DirectorySeparatorChar));

            string compTargetPath;
            try
            {
                var compRelative = CombineRelative(targetManagedRelativePath, c.ComponentRelativePath);
                compTargetPath = _paths.ResolveVaultRelativePath(compRelative);
            }
            catch (ArgumentException)
            {
                return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
            }
            catch (IOException)
            {
                return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
            }

            if (File.Exists(compTargetPath))
            {
                var existingVerification = await _verifier.VerifyAsync(
                    compTargetPath,
                    c.ByteLength,
                    c.Sha256,
                    cancellationToken).ConfigureAwait(false);

                if (!existingVerification.IsMatch)
                {
                    return MapTargetVerification(existingVerification);
                }

                await _assetWrites.UpdateComponentCleanupStateAsync(
                    assetId,
                    c.ComponentRelativePath,
                    SourceCleanupState.DestinationVerified,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                totalBytes += c.ByteLength;
                continue;
            }

            StorageOperationResult publish;
            if (c.ComponentRole == ComponentRole.Primary)
            {
                publish = await PublishStagedOriginalAsync(assetId, compTargetPath,
                    c.ByteLength, c.Sha256, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!File.Exists(compSourcePath))
                    return new StorageOperationResult(StorageOperationStatus.SourceMissing,
                        SafeErrorDetail: $"Model component source '{c.ComponentRelativePath}' does not exist.");
                var sourceVerification = await _verifier.VerifyAsync(compSourcePath,
                    c.ByteLength, c.Sha256, cancellationToken).ConfigureAwait(false);
                if (!sourceVerification.IsMatch) return MapSourceVerification(sourceVerification);
                publish = await PublishExternalCopyAsync(compSourcePath, compTargetPath, operationId,
                    c.ByteLength, c.Sha256, cancellationToken).ConfigureAwait(false);
            }

            if (!publish.IsSuccess)
            {
                return publish;
            }

            if (preserveSourceTimestamps)
            {
                ApplySourceTimestamp(compSourcePath, compTargetPath);
            }

            await _assetWrites.UpdateComponentCleanupStateAsync(
                assetId,
                c.ComponentRelativePath,
                SourceCleanupState.DestinationVerified,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            totalBytes += c.ByteLength;
        }

        return new StorageOperationResult(StorageOperationStatus.Success, BytesProcessed: totalBytes);
    }

    private async Task<StorageOperationResult> PublishExternalCopyAsync(
        string sourcePath,
        string targetPath,
        Guid operationId,
        long expectedByteLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var partialPath = $"{targetPath}.{operationId:N}.partial";
        try
        {
            RootPathRules.RejectExistingReparsePoints(_paths.Root, targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            RootPathRules.RejectExistingReparsePoints(_paths.Root, targetPath);

            if (File.Exists(partialPath))
            {
                var partialVerification = await _verifier.VerifyAsync(
                    partialPath, expectedByteLength, expectedSha256, cancellationToken).ConfigureAwait(false);
                if (!partialVerification.IsMatch)
                {
                    // This name is exclusively owned by the persisted OperationId.
                    File.Delete(partialPath);
                }
            }

            ManagedFileVerificationResult? copiedVerification = null;
            if (!File.Exists(partialPath))
            {
                copiedVerification = await CopyToStagingAndHashAsync(
                        sourcePath,
                        partialPath,
                        expectedByteLength,
                        expectedSha256,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var stagedVerification = copiedVerification ?? await _verifier.VerifyAsync(
                partialPath, expectedByteLength, expectedSha256, cancellationToken).ConfigureAwait(false);
            if (!stagedVerification.IsMatch)
            {
                return MapTargetVerification(stagedVerification);
            }

            cancellationToken.ThrowIfCancellationRequested();
            RootPathRules.RejectExistingReparsePoints(_paths.Root, targetPath);
            if (File.Exists(targetPath))
            {
                return new StorageOperationResult(StorageOperationStatus.UnexpectedTarget);
            }

            File.Move(partialPath, targetPath);
            var finalVerification = await _verifier.VerifyAsync(
                targetPath, expectedByteLength, expectedSha256, cancellationToken).ConfigureAwait(false);
            return finalVerification.IsMatch
                ? new StorageOperationResult(
                    StorageOperationStatus.Success,
                    finalVerification.Status,
                    BytesProcessed: expectedByteLength)
                : MapTargetVerification(finalVerification);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageOperationResult(StorageOperationStatus.AccessDenied);
        }
        catch (IOException)
        {
            return new StorageOperationResult(
                File.Exists(targetPath)
                    ? StorageOperationStatus.UnexpectedTarget
                    : StorageOperationStatus.FileLocked);
        }
    }

    private async Task<ManagedFileVerificationResult> CopyToStagingAndHashAsync(
        string sourcePath,
        string stagingPath,
        long expectedByteLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_copyBufferSize);
        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                _copyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                _copyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var hash = IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            long byteLength = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(
                        buffer.AsMemory(0, _copyBufferSize),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                byteLength += read;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            var actualSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            var status = byteLength != expectedByteLength
                ? ManagedFileVerificationStatus.LengthMismatch
                : string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal)
                    ? ManagedFileVerificationStatus.Match
                    : ManagedFileVerificationStatus.HashMismatch;
            return new ManagedFileVerificationResult(status, byteLength, actualSha256);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ApplySourceTimestamp(string sourcePath, string targetPath)
    {
        try
        {
            var sourceTimestamp = File.GetLastWriteTimeUtc(sourcePath);
            File.SetLastWriteTimeUtc(targetPath, sourceTimestamp);
        }
        catch (IOException)
        {
            // Non-fatal: the managed file is already durable; timestamp preservation is advisory.
        }
        catch (UnauthorizedAccessException)
        {
            // Non-fatal: same rationale.
        }
    }

    private static StorageOperationResult MapSourceVerification(
        ManagedFileVerificationResult verification) => verification.Status switch
        {
            ManagedFileVerificationStatus.Missing => new StorageOperationResult(
                StorageOperationStatus.SourceMissing,
                verification.Status),
            ManagedFileVerificationStatus.LengthMismatch or ManagedFileVerificationStatus.HashMismatch =>
                new StorageOperationResult(StorageOperationStatus.SourceChanged, verification.Status),
            ManagedFileVerificationStatus.Cancelled => new StorageOperationResult(
                StorageOperationStatus.Cancelled,
                verification.Status),
            ManagedFileVerificationStatus.ReadFailed => new StorageOperationResult(
                StorageOperationStatus.Failed,
                verification.Status,
                verification.SafeErrorDetail),
            _ => new StorageOperationResult(StorageOperationStatus.Failed, verification.Status),
        };

    private static StorageOperationResult MapTargetVerification(
        ManagedFileVerificationResult verification) => verification.Status switch
        {
            ManagedFileVerificationStatus.Missing => new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                verification.Status),
            ManagedFileVerificationStatus.LengthMismatch or ManagedFileVerificationStatus.HashMismatch =>
                new StorageOperationResult(StorageOperationStatus.VerificationFailed, verification.Status),
            ManagedFileVerificationStatus.Cancelled => new StorageOperationResult(
                StorageOperationStatus.Cancelled,
                verification.Status),
            ManagedFileVerificationStatus.ReadFailed => new StorageOperationResult(
                StorageOperationStatus.Failed,
                verification.Status,
                verification.SafeErrorDetail),
            _ => new StorageOperationResult(StorageOperationStatus.Failed, verification.Status),
        };

    public async Task<StorageOperationResult> ExecuteTrashMoveAsync(
        ManagedTrashMoveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await ExecuteRecoveryTransferAsync(
                    request.ManagedRelativePath,
                    request.ManagedFileName,
                    VaultPathArea.Profiles,
                    request.RecoveryRelativePath,
                    VaultPathArea.TrashMedias,
                    request.ExpectedByteLength,
                    request.ExpectedSha256,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }
    }

    public async Task<StorageOperationResult> ExecuteDurableMediaAssetMoveAsync(
        string sourceRelativePath,
        VaultPathArea sourceArea,
        string targetRelativePath,
        VaultPathArea targetArea,
        long expectedByteLength,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (sourceArea is not (VaultPathArea.MediaAssets or VaultPathArea.Profiles
                or VaultPathArea.TrashMedias or VaultPathArea.TrashProfiles)
            || targetArea is not (VaultPathArea.MediaAssets or VaultPathArea.Profiles
                or VaultPathArea.TrashMedias or VaultPathArea.TrashProfiles))
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);

        try
        {
            return await ExecuteRecoveryTransferAsync(
                RelativeDirectoryOf(sourceRelativePath), FileNameOf(sourceRelativePath), sourceArea,
                targetRelativePath, targetArea, expectedByteLength, expectedSha256,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }
    }

    public async Task<StorageOperationResult> ExecuteRestoreMoveAsync(
        ManagedRestoreMoveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await ExecuteRecoveryTransferAsync(
                    RelativeDirectoryOf(request.RecoveryRelativePath),
                    FileNameOf(request.RecoveryRelativePath),
                    VaultPathArea.TrashMedias,
                    CombineRelative(request.TargetManagedRelativePath, request.TargetManagedFileName),
                    VaultPathArea.Profiles,
                    request.ExpectedByteLength,
                    request.ExpectedSha256,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }
    }

    public async Task<StorageOperationResult> ExecuteRestoreCheckpointMoveAsync(
        ManagedRestoreCheckpointMoveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceArea is not (VaultPathArea.TrashMedias or VaultPathArea.Profiles))
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }

        try
        {
            return await ExecuteRecoveryTransferAsync(
                    RelativeDirectoryOf(request.SourceRelativePath),
                    FileNameOf(request.SourceRelativePath),
                    request.SourceArea,
                    CombineRelative(request.TargetManagedRelativePath, request.TargetManagedFileName),
                    VaultPathArea.Profiles,
                    request.ExpectedByteLength,
                    request.ExpectedSha256,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }
    }

    public Task<StorageOperationResult> ExecuteProfileManifestTrashMoveAsync(
        ManagedProfileManifestTrashRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteProfileManifestTransferAsync(
            request.ProfileId,
            CombineRelative(request.CurrentProfileRelativePath, ProfileManifestWriter.ManifestFileName),
            VaultPathArea.Profiles,
            CombineRelative(request.RecoveryRelativePath, ProfileManifestWriter.ManifestFileName),
            VaultPathArea.TrashProfiles,
            cleanupSourceDirectory: false,
            validateManifestIdentity: true,
            cancellationToken);
    }

    public async Task<StorageOperationResult> InspectProfileManifestTrashDestinationAsync(
        Guid profileId,
        string recoveryRelativePath,
        CancellationToken cancellationToken = default)
    {
        string destinationPath;
        try
        {
            destinationPath = _paths.ResolveVaultRelativePath(
                VaultPathArea.TrashProfiles,
                CombineRelative(recoveryRelativePath, ProfileManifestWriter.ManifestFileName));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }

        if (!File.Exists(destinationPath))
        {
            return new StorageOperationResult(StorageOperationStatus.SourceMissing);
        }

        var validation = await ValidateProfileManifestAsync(destinationPath, profileId, cancellationToken)
            .ConfigureAwait(false);
        return validation.IsSuccess
            ? new StorageOperationResult(StorageOperationStatus.AlreadyCompleted)
            : validation with { Status = StorageOperationStatus.VerificationFailed };
    }

    public Task<StorageOperationResult> ExecuteProfileManifestRestoreMoveAsync(
        ManagedProfileManifestRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteProfileManifestTransferAsync(
            request.ProfileId,
            CombineRelative(request.RecoveryRelativePath, ProfileManifestWriter.ManifestFileName),
            VaultPathArea.TrashProfiles,
            CombineRelative(request.TargetProfileRelativePath, ProfileManifestWriter.ManifestFileName),
            VaultPathArea.Profiles,
            cleanupSourceDirectory: true,
            validateManifestIdentity: true,
            cancellationToken);
    }

    public async Task<StorageOperationResult> ExecutePurgeDeleteAsync(
        ManagedPurgeDeleteRequest request,
        bool allowAlreadyMissing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string targetPath;
        try
        {
            targetPath = _paths.ResolveVaultRelativePath(request.Area, request.VaultRelativePath);
        }
        catch (ArgumentException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }

        var exists = request.Kind == PurgePhysicalTargetKind.File
            ? File.Exists(targetPath)
            : Directory.Exists(targetPath);
        if (!exists)
        {
            return new StorageOperationResult(
                allowAlreadyMissing
                    ? StorageOperationStatus.AlreadyCompleted
                    : StorageOperationStatus.SourceMissing);
        }

        if (request.Kind == PurgePhysicalTargetKind.File)
        {
            if (request.ExpectedByteLength is not { } byteLength
                || string.IsNullOrWhiteSpace(request.ExpectedSha256))
            {
                return new StorageOperationResult(StorageOperationStatus.NeedsAttention);
            }

            var verification = await _verifier.VerifyAsync(
                    targetPath,
                    byteLength,
                    request.ExpectedSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!verification.IsMatch)
            {
                return MapSourceVerification(verification);
            }
        }
        else
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(targetPath).Any())
                {
                    return new StorageOperationResult(StorageOperationStatus.UnexpectedTarget);
                }
            }
            catch (UnauthorizedAccessException)
            {
                return new StorageOperationResult(StorageOperationStatus.AccessDenied);
            }
            catch (IOException)
            {
                return new StorageOperationResult(StorageOperationStatus.FileLocked);
            }
        }

        try
        {
            if (request.Kind == PurgePhysicalTargetKind.File)
            {
                File.Delete(targetPath);
            }
            else
            {
                Directory.Delete(targetPath, recursive: false);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageOperationResult(StorageOperationStatus.AccessDenied);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.FileLocked);
        }

        return new StorageOperationResult(StorageOperationStatus.Success);
    }

    private async Task<StorageOperationResult> ExecuteRecoveryTransferAsync(
        string sourceRelativeDirectory,
        string sourceFileName,
        VaultPathArea sourceArea,
        string destinationRelativePath,
        VaultPathArea destinationArea,
        long expectedByteLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        string sourcePath;
        string destinationPath;
        try
        {
            sourcePath = _paths.ResolveVaultRelativePath(
                sourceArea,
                CombineRelative(sourceRelativeDirectory, sourceFileName));
            destinationPath = _paths.ResolveVaultRelativePath(
                destinationArea,
                destinationRelativePath);
        }
        catch (ArgumentException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }

        var sourceExists = File.Exists(sourcePath);
        if (File.Exists(destinationPath))
        {
            if (sourceExists)
            {
                return new StorageOperationResult(StorageOperationStatus.UnexpectedTarget);
            }

            var existingVerification = await _verifier.VerifyAsync(
                    destinationPath,
                    expectedByteLength,
                    expectedSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            return existingVerification.IsMatch
                ? new StorageOperationResult(
                    StorageOperationStatus.AlreadyCompleted,
                    existingVerification.Status)
                : MapTargetVerification(existingVerification);
        }

        if (!sourceExists)
        {
            return new StorageOperationResult(
                StorageOperationStatus.SourceMissing,
                SafeErrorDetail: "Neither the recorded source nor the recovery destination holds the managed bytes.");
        }

        var sourceVerification = await _verifier.VerifyAsync(
                sourcePath,
                expectedByteLength,
                expectedSha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (!sourceVerification.IsMatch)
        {
            return MapSourceVerification(sourceVerification);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageOperationResult(StorageOperationStatus.AccessDenied);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.Failed);
        }

        try
        {
            File.Move(sourcePath, destinationPath);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageOperationResult(StorageOperationStatus.AccessDenied);
        }
        catch (IOException)
        {
            return new StorageOperationResult(
                File.Exists(destinationPath)
                    ? StorageOperationStatus.UnexpectedTarget
                    : StorageOperationStatus.FileLocked);
        }

        var destinationVerification = await _verifier.VerifyAsync(
                destinationPath,
                expectedByteLength,
                expectedSha256,
                cancellationToken)
            .ConfigureAwait(false);
        return destinationVerification.IsMatch
            ? new StorageOperationResult(
                StorageOperationStatus.Success,
                destinationVerification.Status,
                BytesProcessed: expectedByteLength)
            : MapTargetVerification(destinationVerification);
    }

    private async Task<StorageOperationResult> ExecuteProfileManifestTransferAsync(
        Guid profileId,
        string sourceRelativePath,
        VaultPathArea sourceArea,
        string destinationRelativePath,
        VaultPathArea destinationArea,
        bool cleanupSourceDirectory,
        bool validateManifestIdentity,
        CancellationToken cancellationToken)
    {
        string sourcePath;
        string destinationPath;
        try
        {
            sourcePath = _paths.ResolveVaultRelativePath(sourceArea, sourceRelativePath);
            destinationPath = _paths.ResolveVaultRelativePath(destinationArea, destinationRelativePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }

        var sourceExists = File.Exists(sourcePath);
        if (File.Exists(destinationPath))
        {
            if (sourceExists)
            {
                return new StorageOperationResult(StorageOperationStatus.UnexpectedTarget);
            }

            if (validateManifestIdentity)
            {
                var destinationValidation = await ValidateProfileManifestAsync(
                    destinationPath, profileId, cancellationToken).ConfigureAwait(false);
                if (!destinationValidation.IsSuccess)
                {
                    return destinationValidation with { Status = StorageOperationStatus.VerificationFailed };
                }
            }

            if (cleanupSourceDirectory)
            {
                TryDeleteEmptyDirectory(Path.GetDirectoryName(sourcePath));
            }

            return new StorageOperationResult(StorageOperationStatus.AlreadyCompleted);
        }

        if (!sourceExists)
        {
            return new StorageOperationResult(StorageOperationStatus.SourceMissing);
        }

        if (validateManifestIdentity)
        {
            var sourceValidation = await ValidateProfileManifestAsync(
                sourcePath, profileId, cancellationToken).ConfigureAwait(false);
            if (!sourceValidation.IsSuccess)
            {
                return sourceValidation;
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageOperationResult(StorageOperationStatus.AccessDenied);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.Failed);
        }

        try
        {
            File.Move(sourcePath, destinationPath);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageOperationResult(StorageOperationStatus.AccessDenied);
        }
        catch (IOException)
        {
            return new StorageOperationResult(
                File.Exists(destinationPath)
                    ? StorageOperationStatus.UnexpectedTarget
                    : StorageOperationStatus.FileLocked);
        }

        if (validateManifestIdentity)
        {
            var finalValidation = await ValidateProfileManifestAsync(
                destinationPath, profileId, cancellationToken).ConfigureAwait(false);
            if (!finalValidation.IsSuccess)
            {
                return finalValidation;
            }
        }

        if (cleanupSourceDirectory)
        {
            TryDeleteEmptyDirectory(Path.GetDirectoryName(sourcePath));
        }

        return new StorageOperationResult(StorageOperationStatus.Success);
    }

    private static async Task<StorageOperationResult> ValidateProfileManifestAsync(
        string absolutePath,
        Guid expectedProfileId,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(absolutePath, cancellationToken).ConfigureAwait(false);
            var manifest = ProfileManifestWriter.Deserialize(json);
            return manifest.ProfileId == expectedProfileId
                ? new StorageOperationResult(StorageOperationStatus.Success)
                : new StorageOperationResult(StorageOperationStatus.SourceChanged);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException)
        {
            return new StorageOperationResult(StorageOperationStatus.SourceChanged);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageOperationResult(StorageOperationStatus.AccessDenied);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.FileLocked);
        }
    }

    private static void TryDeleteEmptyDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                Directory.Delete(directoryPath, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string RelativeDirectoryOf(string relativeFilePath)
    {
        var separator = relativeFilePath.LastIndexOf('/');
        return separator < 0 ? string.Empty : relativeFilePath[..separator];
    }

    private static string FileNameOf(string relativeFilePath)
    {
        var separator = relativeFilePath.LastIndexOf('/');
        return separator < 0 ? relativeFilePath : relativeFilePath[(separator + 1)..];
    }

    private static string CombineRelative(string directory, string fileName)
    {
        var trimmed = directory.TrimEnd('/', '\\');
        return trimmed.Length == 0 ? fileName : $"{trimmed}/{fileName}";
    }
}

public sealed record ManagedTrashMoveRequest(
    Guid MediaId,
    string ManagedRelativePath,
    string ManagedFileName,
    string RecoveryRelativePath,
    long ExpectedByteLength,
    string ExpectedSha256);

public sealed record ManagedRestoreMoveRequest(
    Guid MediaId,
    string RecoveryRelativePath,
    string TargetManagedRelativePath,
    string TargetManagedFileName,
    long ExpectedByteLength,
    string ExpectedSha256);

public sealed record ManagedRestoreCheckpointMoveRequest(
    Guid MediaId,
    VaultPathArea SourceArea,
    string SourceRelativePath,
    string TargetManagedRelativePath,
    string TargetManagedFileName,
    long ExpectedByteLength,
    string ExpectedSha256);

public sealed record ManagedProfileManifestTrashRequest(
    Guid ProfileId,
    string CurrentProfileRelativePath,
    string RecoveryRelativePath);

public sealed record ManagedProfileManifestRestoreRequest(
    Guid ProfileId,
    string RecoveryRelativePath,
    string TargetProfileRelativePath);

public sealed record ManagedPurgeDeleteRequest(
    VaultPathArea Area,
    string VaultRelativePath,
    PurgePhysicalTargetKind Kind,
    long? ExpectedByteLength,
    string? ExpectedSha256);
