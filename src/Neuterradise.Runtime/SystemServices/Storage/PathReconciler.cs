using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;

namespace Neuterradise.App.SystemServices.Storage;

public sealed class PathReconciler
{
    private const int _copyBufferSize = 1024 * 1024;
    private const int _renameItemPageSize = 256;

    private readonly VaultPaths _paths;
    private readonly ProfileWrites _profileWrites;
    private readonly MediaWrites _assetWrites;
    private readonly ManagedFileVerifier _verifier;
    private readonly IVolumeIdentityProvider _volumeIdentityProvider;

    public PathReconciler(
        VaultPaths paths,
        ProfileWrites profileWrites,
        MediaWrites assetWrites,
        ManagedFileVerifier verifier,
        IVolumeIdentityProvider volumeIdentityProvider)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _profileWrites = profileWrites ?? throw new ArgumentNullException(nameof(profileWrites));
        _assetWrites = assetWrites ?? throw new ArgumentNullException(nameof(assetWrites));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _volumeIdentityProvider = volumeIdentityProvider
            ?? throw new ArgumentNullException(nameof(volumeIdentityProvider));
    }

    public async Task<StorageOperationResult> ReconcileProfileRenameAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A stable ProfileId cannot be empty.", nameof(profileId));
        }

        try
        {
            return await ReconcileProfileRenameCoreAsync(profileId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }
    }

    public async Task<StorageOperationResult> ReconcileOwnerRelocationAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("A stable MediaId cannot be empty.", nameof(assetId));
        }

        try
        {
            return await ReconcileOwnerRelocationCoreAsync(assetId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }
    }

    private async Task<StorageOperationResult> ReconcileProfileRenameCoreAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var plan = await _profileWrites.ReadProfileRenamePlanAsync(profileId, cancellationToken)
            .ConfigureAwait(false);
        if (plan is null)
        {
            return NeedsAttention("The Profile named by the reconciliation no longer exists.");
        }

        if (plan.PathState == ManagedPathState.None)
        {
            return new StorageOperationResult(StorageOperationStatus.AlreadyCompleted);
        }

        if (plan.PathState == ManagedPathState.NeedsAttention)
        {
            return NeedsAttention("This Profile's naming reconciliation is parked for explicit repair.");
        }

        if (plan.ReconciliationOperationId is not { } operationId
            || plan.TargetManagedRelativePath is not { } targetFolder
            || plan.StorageToken is null)
        {
            return NeedsAttention("The persisted Profile rename plan is incomplete.");
        }

        if (plan.IsTrashed)
        {
            return NeedsAttention("A trashed Profile is not reconciled toward an active canonical folder.");
        }

        string targetFolderPath;
        string intermediateFolderPath;
        try
        {
            targetFolderPath = _paths.ResolveVaultRelativePath(targetFolder);
            intermediateFolderPath = _paths.ResolveVaultRelativePath(
                $"profiles/{IntermediateName(operationId)}");
        }
        catch (ArgumentException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }

        var currentFolder = plan.CurrentManagedRelativePath;
        if (currentFolder is null)
        {
            var derived = await DeriveCurrentProfileFolderAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
            if (derived.IsAmbiguous)
            {
                await ParkProfileAsync(
                        profileId,
                        operationId,
                        "This Profile's owned media is spread across more than one managed folder.",
                        cancellationToken)
                    .ConfigureAwait(false);
                return NeedsAttention("This Profile's owned media is spread across more than one managed folder.");
            }

            currentFolder = derived.Folder ?? targetFolder;
            await _profileWrites.AdoptProfileCurrentFolderAsync(
                    profileId,
                    operationId,
                    currentFolder,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string currentFolderPath;
        try
        {
            currentFolderPath = _paths.ResolveVaultRelativePath(currentFolder);
        }
        catch (ArgumentException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }

        if (!string.Equals(currentFolder, targetFolder, StringComparison.Ordinal))
        {
            var folderResult = await MoveProfileFolderAsync(
                    profileId,
                    operationId,
                    targetFolder,
                    currentFolderPath,
                    targetFolderPath,
                    intermediateFolderPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!folderResult.IsSuccess)
            {
                return folderResult;
            }

            await _profileWrites.CheckpointProfileFolderPlacementAsync(
                    profileId,
                    operationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (!Directory.Exists(targetFolderPath))
        {
            var created = TryCreateDirectory(targetFolderPath);
            if (created is not null)
            {
                return created;
            }
        }

        return await ReconcileOwnedFilenamesAsync(profileId, operationId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<StorageOperationResult> ReconcileOwnedFilenamesAsync(
        Guid profileId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var itemsProcessed = 0;
        var bytesProcessed = 0L;
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _profileWrites.ReadPendingRenameMediaPlansAsync(
                    operationId,
                    _renameItemPageSize,
                    cancellationToken)
                .ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            var advancedInPage = 0;
            foreach (var item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var itemResult = await ReconcileOwnedFilenameAsync(item, operationId, cancellationToken)
                    .ConfigureAwait(false);
                if (itemResult.Status is StorageOperationStatus.NeedsAttention
                    or StorageOperationStatus.UnexpectedTarget
                    or StorageOperationStatus.TargetCollision
                    or StorageOperationStatus.VerificationFailed
                    or StorageOperationStatus.SourceMissing
                    or StorageOperationStatus.SourceChanged
                    or StorageOperationStatus.PathOutsideVault)
                {
                    await _assetWrites.MarkPlacementNeedsAttentionAsync(
                            item.MediaId,
                            operationId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await ParkProfileAsync(
                            profileId,
                            operationId,
                            "At least one managed file could not be reconciled from persisted state.",
                            cancellationToken)
                        .ConfigureAwait(false);
                    return itemResult;
                }

                if (!itemResult.IsSuccess)
                {
                    return itemResult;
                }

                advancedInPage++;
                itemsProcessed++;
                bytesProcessed += itemResult.BytesProcessed ?? 0;
            }

            if (advancedInPage == 0)
            {
                return NeedsAttention("The rename made no progress on its outstanding managed files.");
            }
        }

        stopwatch.Stop();

        var completed = await _profileWrites.CompleteProfileRenameAsync(
                profileId,
                operationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!completed)
        {
            return NeedsAttention("The Profile rename could not be closed against its persisted state.");
        }

        return new StorageOperationResult(
            itemsProcessed == 0 ? StorageOperationStatus.AlreadyCompleted : StorageOperationStatus.Success,
            BytesProcessed: bytesProcessed,
            Duration: stopwatch.Elapsed);
    }

    private async Task<StorageOperationResult> ReconcileOwnedFilenameAsync(
        PersistedRenameMediaPlan item,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (item.CurrentManagedRelativePath is not { } currentDirectory
            || item.CurrentManagedFileName is not { } currentFileName
            || item.TargetManagedRelativePath is not { } targetDirectory
            || item.TargetManagedFileName is not { } targetFileName)
        {
            return NeedsAttention("A planned managed file has no complete persisted placement.");
        }

        var components = await _assetWrites.GetMediaComponentsAsync(item.MediaId, cancellationToken)
            .ConfigureAwait(false);
        var isMultiFile = components.Count > 1
            || (components.Count == 1 && components.Any(c => c.ComponentRole == ComponentRole.Dependency));
        if (isMultiFile)
        {
            var pkgResult = await ReconcileModelPackageAsync(
                item.MediaId,
                currentDirectory,
                targetDirectory,
                operationId,
                components,
                cancellationToken).ConfigureAwait(false);
            if (!pkgResult.IsSuccess)
            {
                return pkgResult;
            }

            var packageCheckpointed = await _assetWrites.CheckpointReconciledPlacementAsync(
                    item.MediaId,
                    operationId,
                    completeOperation: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return packageCheckpointed
                ? pkgResult
                : NeedsAttention("A managed file moved but its placement checkpoint no longer applies.");
        }

        string sourcePath;
        string targetPath;
        string intermediatePath;
        try
        {
            sourcePath = _paths.ResolveVaultRelativePath(
                CombineRelative(currentDirectory, currentFileName));
            targetPath = _paths.ResolveVaultRelativePath(
                CombineRelative(targetDirectory, targetFileName));
            intermediatePath = _paths.ResolveVaultRelativePath(
                CombineRelative(targetDirectory, IntermediateName(item.MediaId)));
        }
        catch (ArgumentException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }

        var moveResult = await MoveManagedFileAsync(
                sourcePath,
                targetPath,
                intermediatePath,
                item.ExpectedByteLength,
                item.ExpectedSha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (!moveResult.IsSuccess)
        {
            return moveResult;
        }

        var checkpointed = await _assetWrites.CheckpointReconciledPlacementAsync(
                item.MediaId,
                operationId,
                completeOperation: false,
                cancellationToken)
            .ConfigureAwait(false);
        return checkpointed
            ? moveResult
            : NeedsAttention("A managed file moved but its placement checkpoint no longer applies.");
    }

    private async Task<StorageOperationResult> ReconcileOwnerRelocationCoreAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var plan = await _assetWrites.ReadOwnerRelocationPlanAsync(assetId, cancellationToken)
            .ConfigureAwait(false);
        if (plan is null)
        {
            return NeedsAttention("The Media named by the relocation no longer exists.");
        }

        if (plan.PathState == ManagedPathState.None)
        {
            return new StorageOperationResult(StorageOperationStatus.AlreadyCompleted);
        }

        if (plan.PathState == ManagedPathState.NeedsAttention)
        {
            return NeedsAttention("This media item's placement is parked for explicit repair.");
        }

        if (plan.ReconciliationOperationId is not { } operationId
            || plan.CurrentManagedRelativePath is not { } currentDirectory
            || plan.CurrentManagedFileName is not { } currentFileName
            || plan.TargetManagedRelativePath is not { } targetDirectory
            || plan.TargetManagedFileName is not { } targetFileName
            || plan.StorageToken is null)
        {
            return NeedsAttention("The persisted OWNER relocation plan is incomplete.");
        }

        if (plan.State != MediaState.Active || plan.IsTrashed)
        {
            return NeedsAttention("Only ACTIVE managed media is relocated to a new owner's folder.");
        }

        if (plan.ExpectedSha256 is not { } expectedSha256
            || plan.ExpectedByteLength is not { } expectedByteLength)
        {
            return NeedsAttention("This media item has no authoritative fingerprint to verify a relocation against.");
        }

        string sourcePath;
        string targetPath;
        string intermediatePath;
        try
        {
            sourcePath = _paths.ResolveVaultRelativePath(
                CombineRelative(currentDirectory, currentFileName));
            targetPath = _paths.ResolveVaultRelativePath(
                CombineRelative(targetDirectory, targetFileName));
            intermediatePath = _paths.ResolveVaultRelativePath(
                CombineRelative(targetDirectory, IntermediateName(operationId)));
        }
        catch (ArgumentException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }

        var authority = await _assetWrites.ResolveTargetAuthorityAsync(
                assetId,
                targetDirectory,
                targetFileName,
                cancellationToken)
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
            var pkgResult = await ReconcileModelPackageAsync(
                assetId,
                currentDirectory,
                targetDirectory,
                operationId,
                components,
                cancellationToken).ConfigureAwait(false);
            if (!pkgResult.IsSuccess)
            {
                if (pkgResult.Status is StorageOperationStatus.UnexpectedTarget
                    or StorageOperationStatus.VerificationFailed
                    or StorageOperationStatus.SourceChanged)
                {
                    await _assetWrites.MarkPlacementNeedsAttentionAsync(
                            assetId,
                            operationId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                return pkgResult;
            }

            var packageCheckpointed = await _assetWrites.CheckpointReconciledPlacementAsync(
                    assetId,
                    operationId,
                    completeOperation: true,
                    cancellationToken)
                .ConfigureAwait(false);
            return packageCheckpointed
                ? pkgResult
                : NeedsAttention("The media item moved but its placement checkpoint no longer applies.");
        }

        var sameVolume = VolumeIdentity.CanUseSameVolumeMove(
            _volumeIdentityProvider,
            File.Exists(sourcePath) ? sourcePath : _paths.ProfilesPath,
            _paths.ProfilesPath);

        var result = sameVolume
            ? await MoveManagedFileAsync(
                    sourcePath,
                    targetPath,
                    intermediatePath,
                    expectedByteLength,
                    expectedSha256,
                    cancellationToken)
                .ConfigureAwait(false)
            : await CopyManagedFileAsync(
                    sourcePath,
                    targetPath,
                    intermediatePath,
                    expectedByteLength,
                    expectedSha256,
                    cancellationToken)
                .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            if (result.Status is StorageOperationStatus.UnexpectedTarget
                or StorageOperationStatus.VerificationFailed
                or StorageOperationStatus.SourceChanged)
            {
                await _assetWrites.MarkPlacementNeedsAttentionAsync(
                        assetId,
                        operationId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return result;
        }

        var checkpointed = await _assetWrites.CheckpointReconciledPlacementAsync(
                assetId,
                operationId,
                completeOperation: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!checkpointed)
        {
            return NeedsAttention("The media item moved but its placement checkpoint no longer applies.");
        }

        if (!sameVolume)
        {
            TryDeleteSupersededOriginal(sourcePath, targetPath);
        }

        return result;
    }

    private async Task<StorageOperationResult> ReconcileModelPackageAsync(
        Guid assetId,
        string currentDirectory,
        string targetDirectory,
        Guid operationId,
        IReadOnlyList<MediaComponentRecord> components,
        CancellationToken cancellationToken)
    {
        long totalBytes = 0;
        string sourceDir;
        string targetDir;
        try
        {
            sourceDir = _paths.ResolveVaultRelativePath(currentDirectory);
            targetDir = _paths.ResolveVaultRelativePath(targetDirectory);
        }
        catch (ArgumentException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }

        RootPathRules.RejectExistingReparsePoints(_paths.Root, targetDir);
        Directory.CreateDirectory(targetDir);
        RootPathRules.RejectExistingReparsePoints(_paths.Root, targetDir);

        var sameVolume = VolumeIdentity.CanUseSameVolumeMove(
            _volumeIdentityProvider,
            sourceDir,
            targetDir);

        foreach (var comp in components)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string compSourcePath;
            string compTargetPath;
            string compIntermediatePath;
            try
            {
                compSourcePath = _paths.ResolveVaultRelativePath(
                    CombineRelative(currentDirectory, comp.ComponentRelativePath));
                compTargetPath = _paths.ResolveVaultRelativePath(
                    CombineRelative(targetDirectory, comp.ComponentRelativePath));
                compIntermediatePath = _paths.ResolveVaultRelativePath(
                    CombineRelative(targetDirectory, $"{comp.ComponentRelativePath}.{IntermediateName(operationId)}"));
            }
            catch (ArgumentException)
            {
                return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
            }
            catch (IOException)
            {
                return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
            }

            if (string.Equals(compSourcePath, compTargetPath, StringComparison.Ordinal))
            {
                var v = await _verifier.VerifyAsync(compTargetPath, comp.ByteLength, comp.Sha256, cancellationToken)
                    .ConfigureAwait(false);
                if (!v.IsMatch)
                {
                    return new StorageOperationResult(StorageOperationStatus.VerificationFailed, v.Status);
                }

                totalBytes += comp.ByteLength;
                continue;
            }

            var targetCompDir = Path.GetDirectoryName(compTargetPath);
            if (targetCompDir is not null)
            {
                Directory.CreateDirectory(targetCompDir);
            }

            var transferResult = sameVolume
                ? await MoveManagedFileAsync(
                        compSourcePath,
                        compTargetPath,
                        compIntermediatePath,
                        comp.ByteLength,
                        comp.Sha256,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await CopyManagedFileAsync(
                        compSourcePath,
                        compTargetPath,
                        compIntermediatePath,
                        comp.ByteLength,
                        comp.Sha256,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!transferResult.IsSuccess)
            {
                return transferResult;
            }

            if (!sameVolume)
            {
                TryDeleteSupersededOriginal(compSourcePath, compTargetPath);
            }

            totalBytes += comp.ByteLength;
        }

        if (!string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase))
        {
            TryCleanEmptyDirectoryTree(sourceDir);
        }

        return new StorageOperationResult(StorageOperationStatus.Success, BytesProcessed: totalBytes);
    }

    private static void TryCleanEmptyDirectoryTree(string rootPath)
    {
        try
        {
            if (!Directory.Exists(rootPath))
            {
                return;
            }

            foreach (var subDir in Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length))
            {
                if (Directory.Exists(subDir) && !Directory.EnumerateFileSystemEntries(subDir).Any())
                {
                    Directory.Delete(subDir, recursive: false);
                }
            }

            if (Directory.Exists(rootPath) && !Directory.EnumerateFileSystemEntries(rootPath).Any())
            {
                Directory.Delete(rootPath, recursive: false);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<StorageOperationResult> MoveProfileFolderAsync(
        Guid profileId,
        Guid operationId,
        string targetFolder,
        string currentFolderPath,
        string targetFolderPath,
        string intermediateFolderPath,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(intermediateFolderPath))
        {
            if (Directory.Exists(targetFolderPath))
            {
                return new StorageOperationResult(StorageOperationStatus.UnexpectedTarget);
            }

            return MoveDirectory(intermediateFolderPath, targetFolderPath);
        }

        var currentExists = Directory.Exists(currentFolderPath);
        var caseOnlyRename = !string.Equals(currentFolderPath, targetFolderPath, StringComparison.Ordinal)
            && string.Equals(currentFolderPath, targetFolderPath, StringComparison.OrdinalIgnoreCase);

        if (!caseOnlyRename && Directory.Exists(targetFolderPath))
        {
            if (currentExists)
            {
                await ParkProfileAsync(
                        profileId,
                        operationId,
                        "Both the previous and the new Profile folder exist and cannot be told apart.",
                        cancellationToken)
                    .ConfigureAwait(false);
                return new StorageOperationResult(StorageOperationStatus.UnexpectedTarget);
            }

            return new StorageOperationResult(StorageOperationStatus.Success);
        }

        if (!currentExists)
        {
            var created = TryCreateDirectory(targetFolderPath);
            return created ?? new StorageOperationResult(StorageOperationStatus.Success);
        }

        if (await _profileWrites.IsProfileFolderClaimedByAnotherProfileAsync(
                profileId,
                targetFolder,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return new StorageOperationResult(StorageOperationStatus.TargetCollision);
        }

        var parent = Path.GetDirectoryName(targetFolderPath);
        if (parent is not null)
        {
            var created = TryCreateDirectory(parent);
            if (created is not null)
            {
                return created;
            }
        }

        if (caseOnlyRename)
        {
            var toIntermediate = MoveDirectory(currentFolderPath, intermediateFolderPath);
            if (!toIntermediate.IsSuccess)
            {
                return toIntermediate;
            }

            return MoveDirectory(intermediateFolderPath, targetFolderPath);
        }

        return MoveDirectory(currentFolderPath, targetFolderPath);
    }

    private static StorageOperationResult MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageOperationResult(StorageOperationStatus.AccessDenied);
        }
        catch (IOException)
        {
            return new StorageOperationResult(
                Directory.Exists(destination)
                    ? StorageOperationStatus.UnexpectedTarget
                    : StorageOperationStatus.FileLocked);
        }

        return new StorageOperationResult(StorageOperationStatus.Success);
    }

    private async Task<StorageOperationResult> MoveManagedFileAsync(
        string sourcePath,
        string targetPath,
        string intermediatePath,
        long? expectedByteLength,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        if (File.Exists(intermediatePath))
        {
            if (File.Exists(targetPath))
            {
                return new StorageOperationResult(StorageOperationStatus.UnexpectedTarget);
            }

            var resumed = MoveFile(intermediatePath, targetPath);
            return resumed.IsSuccess
                ? await ConfirmMovedFileAsync(targetPath, expectedByteLength, expectedSha256, cancellationToken).ConfigureAwait(false)
                : resumed;
        }

        var sourceExists = File.Exists(sourcePath);
        var caseOnlyRename = !string.Equals(sourcePath, targetPath, StringComparison.Ordinal)
            && string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase);

        if (!caseOnlyRename && File.Exists(targetPath))
        {
            if (sourceExists)
            {
                return new StorageOperationResult(StorageOperationStatus.UnexpectedTarget);
            }

            return await AdoptExistingTargetAsync(
                    targetPath,
                    expectedByteLength,
                    expectedSha256,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!sourceExists)
        {
            return new StorageOperationResult(
                StorageOperationStatus.SourceMissing,
                SafeErrorDetail: "Neither the recorded managed location nor its target holds the media file.");
        }

        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (targetDirectory is not null)
        {
            var created = TryCreateDirectory(targetDirectory);
            if (created is not null)
            {
                return created;
            }
        }

        if (caseOnlyRename)
        {
            var toIntermediate = MoveFile(sourcePath, intermediatePath);
            if (!toIntermediate.IsSuccess)
            {
                return toIntermediate;
            }

            var promoted = MoveFile(intermediatePath, targetPath);
            return promoted.IsSuccess
                ? await ConfirmMovedFileAsync(targetPath, expectedByteLength, expectedSha256, cancellationToken).ConfigureAwait(false)
                : promoted;
        }

        var moved = MoveFile(sourcePath, targetPath);
        return moved.IsSuccess
            ? await ConfirmMovedFileAsync(targetPath, expectedByteLength, expectedSha256, cancellationToken).ConfigureAwait(false)
            : moved;
    }

    private static StorageOperationResult MoveFile(string source, string destination)
    {
        try
        {
            File.Move(source, destination);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageOperationResult(StorageOperationStatus.AccessDenied);
        }
        catch (IOException)
        {
            return new StorageOperationResult(
                File.Exists(destination)
                    ? StorageOperationStatus.UnexpectedTarget
                    : StorageOperationStatus.FileLocked);
        }

        return new StorageOperationResult(StorageOperationStatus.Success);
    }

    private async Task<StorageOperationResult> CopyManagedFileAsync(
        string sourcePath,
        string targetPath,
        string intermediatePath,
        long expectedByteLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (File.Exists(targetPath))
        {
            return File.Exists(sourcePath)
                ? await AdoptOrRejectDuplicatedTargetAsync(
                        sourcePath,
                        targetPath,
                        expectedByteLength,
                        expectedSha256,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await AdoptExistingTargetAsync(
                        targetPath,
                        expectedByteLength,
                        expectedSha256,
                        cancellationToken)
                    .ConfigureAwait(false);
        }

        if (!File.Exists(sourcePath))
        {
            return new StorageOperationResult(
                StorageOperationStatus.SourceMissing,
                SafeErrorDetail: "Neither the recorded managed location nor its target holds the media file.");
        }

        var sourceVerification = await _verifier
            .VerifyAsync(sourcePath, expectedByteLength, expectedSha256, cancellationToken)
            .ConfigureAwait(false);
        if (!sourceVerification.IsMatch)
        {
            return MapVerification(sourceVerification, isSource: true);
        }

        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (targetDirectory is not null)
        {
            var created = TryCreateDirectory(targetDirectory);
            if (created is not null)
            {
                return created;
            }
        }

        try
        {
            if (File.Exists(intermediatePath))
            {
                File.Delete(intermediatePath);
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

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await CopyToIntermediateAsync(sourcePath, intermediatePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageOperationResult(StorageOperationStatus.AccessDenied);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.FileLocked);
        }
        finally
        {
            stopwatch.Stop();
        }

        var copyVerification = await _verifier
            .VerifyAsync(intermediatePath, expectedByteLength, expectedSha256, cancellationToken)
            .ConfigureAwait(false);
        if (!copyVerification.IsMatch)
        {
            return MapVerification(copyVerification, isSource: false);
        }

        if (File.Exists(targetPath))
        {
            return new StorageOperationResult(StorageOperationStatus.UnexpectedTarget);
        }

        var promoted = MoveFile(intermediatePath, targetPath);
        if (!promoted.IsSuccess)
        {
            return promoted;
        }

        var targetVerification = await _verifier
            .VerifyAsync(targetPath, expectedByteLength, expectedSha256, cancellationToken)
            .ConfigureAwait(false);
        return targetVerification.IsMatch
            ? new StorageOperationResult(
                StorageOperationStatus.Success,
                targetVerification.Status,
                BytesProcessed: expectedByteLength,
                Duration: stopwatch.Elapsed)
            : MapVerification(targetVerification, isSource: false);
    }

    private async Task CopyToIntermediateAsync(
        string sourcePath,
        string intermediatePath,
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
                intermediatePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                _copyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

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

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<StorageOperationResult> AdoptExistingTargetAsync(
        string targetPath,
        long? expectedByteLength,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        if (expectedByteLength is not { } byteLength || expectedSha256 is not { } sha256)
        {
            return NeedsAttention(
                "A file already occupies the target and no authoritative fingerprint can identify it.");
        }

        var verification = await _verifier
            .VerifyAsync(targetPath, byteLength, sha256, cancellationToken)
            .ConfigureAwait(false);
        return verification.IsMatch
            ? new StorageOperationResult(StorageOperationStatus.AlreadyCompleted, verification.Status)
            : MapVerification(verification, isSource: false);
    }

    private async Task<StorageOperationResult> AdoptOrRejectDuplicatedTargetAsync(
        string sourcePath,
        string targetPath,
        long expectedByteLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var targetVerification = await _verifier
            .VerifyAsync(targetPath, expectedByteLength, expectedSha256, cancellationToken)
            .ConfigureAwait(false);
        if (!targetVerification.IsMatch)
        {
            return new StorageOperationResult(StorageOperationStatus.UnexpectedTarget);
        }

        var sourceVerification = await _verifier
            .VerifyAsync(sourcePath, expectedByteLength, expectedSha256, cancellationToken)
            .ConfigureAwait(false);
        return sourceVerification.IsMatch
            ? new StorageOperationResult(StorageOperationStatus.AlreadyCompleted, targetVerification.Status)
            : new StorageOperationResult(StorageOperationStatus.UnexpectedTarget);
    }

    private async Task<StorageOperationResult> ConfirmMovedFileAsync(
        string targetPath,
        long? expectedByteLength,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedByteLength is { } byteLength && expectedSha256 is { } sha256)
        {
            var verification = await _verifier.VerifyAsync(
                targetPath, byteLength, sha256, cancellationToken).ConfigureAwait(false);
            return verification.IsMatch
                ? new StorageOperationResult(
                    StorageOperationStatus.Success,
                    verification.Status,
                    BytesProcessed: byteLength)
                : MapVerification(verification, isSource: false);
        }

        // Without a persisted digest, verification can assert only observed length.
        // Never invent hash authority.
        try
        {
            var file = new FileInfo(targetPath);
            if (!file.Exists)
            {
                return new StorageOperationResult(
                    StorageOperationStatus.VerificationFailed,
                    ManagedFileVerificationStatus.Missing,
                    "The moved media file is not at its target.");
            }

            if (expectedByteLength is { } expected && file.Length != expected)
            {
                return new StorageOperationResult(
                    StorageOperationStatus.VerificationFailed,
                    ManagedFileVerificationStatus.LengthMismatch,
                    "The moved media file does not match its authoritative byte length.");
            }

            return new StorageOperationResult(
                StorageOperationStatus.Success,
                BytesProcessed: expectedByteLength);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.FileLocked);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageOperationResult(StorageOperationStatus.AccessDenied);
        }
    }

    private async Task<DerivedProfileFolder> DeriveCurrentProfileFolderAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var page = await _profileWrites.ReadPendingRenameMediaPlansAsync(
                operationId,
                _renameItemPageSize,
                cancellationToken)
            .ConfigureAwait(false);

        string? folder = null;
        foreach (var item in page)
        {
            if (item.CurrentManagedRelativePath is not { } currentDirectory)
            {
                continue;
            }

            var candidate = ProfileFolderOf(currentDirectory);
            if (candidate is null)
            {
                return new DerivedProfileFolder(null, IsAmbiguous: true);
            }

            if (folder is null)
            {
                folder = candidate;
            }
            else if (!string.Equals(folder, candidate, StringComparison.Ordinal))
            {
                return new DerivedProfileFolder(null, IsAmbiguous: true);
            }
        }

        return new DerivedProfileFolder(folder, IsAmbiguous: false);
    }

    private async Task ParkProfileAsync(
        Guid profileId,
        Guid operationId,
        string safeErrorDetail,
        CancellationToken cancellationToken) =>
        await _profileWrites.MarkProfileRenameNeedsAttentionAsync(
                profileId,
                operationId,
                safeErrorDetail,
                cancellationToken)
            .ConfigureAwait(false);

    private static void TryDeleteSupersededOriginal(string sourcePath, string targetPath)
    {
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(sourcePath);
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static StorageOperationResult? TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageOperationResult(StorageOperationStatus.AccessDenied);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.Failed);
        }
    }

    private static StorageOperationResult MapVerification(
        ManagedFileVerificationResult verification,
        bool isSource) => verification.Status switch
        {
            ManagedFileVerificationStatus.Missing => new StorageOperationResult(
                isSource ? StorageOperationStatus.SourceMissing : StorageOperationStatus.VerificationFailed,
                verification.Status),
            ManagedFileVerificationStatus.LengthMismatch or ManagedFileVerificationStatus.HashMismatch =>
                new StorageOperationResult(
                    isSource ? StorageOperationStatus.SourceChanged : StorageOperationStatus.VerificationFailed,
                    verification.Status),
            ManagedFileVerificationStatus.ReadFailed => new StorageOperationResult(
                StorageOperationStatus.FileLocked,
                verification.Status,
                verification.SafeErrorDetail),
            ManagedFileVerificationStatus.Cancelled => new StorageOperationResult(
                StorageOperationStatus.Cancelled,
                verification.Status),
            _ => new StorageOperationResult(StorageOperationStatus.Failed, verification.Status),
        };

    private static StorageOperationResult NeedsAttention(string safeErrorDetail) =>
        new(StorageOperationStatus.NeedsAttention, SafeErrorDetail: safeErrorDetail);

    private static string IntermediateName(Guid stableId) => $".nt-reconcile-{stableId:N}";

    private static string? ProfileFolderOf(string managedRelativeDirectory)
    {
        var segments = managedRelativeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && string.Equals(segments[0], "profiles", StringComparison.Ordinal)
            ? $"{segments[0]}/{segments[1]}"
            : null;
    }

    private static string CombineRelative(string directory, string fileName)
    {
        var trimmed = directory.TrimEnd('/', '\\');
        return trimmed.Length == 0 ? fileName : $"{trimmed}/{fileName}";
    }

    private sealed record DerivedProfileFolder(string? Folder, bool IsAmbiguous);
}
