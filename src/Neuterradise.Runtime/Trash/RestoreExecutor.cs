using System.IO;
using Neuterradise.App.RelatedProfiles;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.Trash;

public sealed class RestoreExecutor
{
    private readonly CatalogDb _catalog;
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly ManagedMoveExecutor _moveExecutor;
    private readonly ManagedPathPlanner _pathPlanner;
    private readonly ProfileManifestWriter _manifestWriter;
    private readonly ProfileOwnershipRestoreCoordinator _profileOwnershipRestore;
    private readonly TimeProvider _timeProvider;

    public RestoreExecutor(
        CatalogDb catalog,
        ManagedMoveExecutor moveExecutor,
        TimeProvider? timeProvider = null,
        ManagedPathPlanner? pathPlanner = null,
        ProfileManifestWriter? manifestWriter = null,
        ProfileOwnershipRestoreCoordinator? profileOwnershipRestore = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _moveExecutor = moveExecutor ?? throw new ArgumentNullException(nameof(moveExecutor));
        _pathPlanner = pathPlanner ?? new ManagedPathPlanner(_catalog.Paths.Root);
        _manifestWriter = manifestWriter ?? new ProfileManifestWriter(catalog.Paths);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _profileOwnershipRestore = profileOwnershipRestore
            ?? new ProfileOwnershipRestoreCoordinator(catalog);
    }

    public async Task<OperationResult<MediaRestoreOutcome>> RestoreMediaAsync(
        Guid trashEntryId,
        Guid? targetOwnerProfileId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(trashEntryId, nameof(trashEntryId));

        MediaTrashPlan plan;
        MediaTrashState asset;

        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var entry = await TrashCoordinator.ReadTrashEntryAsync(connection, trashEntryId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null || entry.EntityType != TrashEntityType.Media)
            {
                return OperationResult<MediaRestoreOutcome>.NotFound(
                    OperationErrorCode.TrashEntryNotFound,
                    "That Trash record no longer exists.");
            }

            var parsed = MediaTrashPlan.FromJson(entry.PlanJson);
            if (parsed is null || entry.RecoveryRelativePath is null)
            {
                return OperationResult<MediaRestoreOutcome>.NeedsAttention(
                    OperationErrorCode.TrashPlanUnreadable,
                    "This Trash record cannot be read, so its media cannot be restored automatically.");
            }

            plan = parsed;

            var current = await TrashCoordinator.ReadMediaTrashStateAsync(
                    connection, transaction: null, entry.EntityId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return OperationResult<MediaRestoreOutcome>.NotFound(
                    OperationErrorCode.MediaNotFound,
                    "That media item no longer exists.");
            }

            if (entry.State == TrashEntryState.Restored
                && current.State == MediaState.Active
                && current.OwnerProfileId is { } restoredOwnerId
                && current.CurrentManagedRelativePath is { } restoredPath
                && current.CurrentManagedFileName is { } restoredFileName)
            {
                var priorOutcome = OperationResult<MediaRestoreOutcome>.Success(
                    new MediaRestoreOutcome(
                        trashEntryId,
                        current.MediaId,
                        restoredOwnerId,
                        restoredPath,
                        restoredFileName,
                        current.RowVersion),
                    parsed.OperationId);
                await RefreshRelatedProjectionsAfterRestoreAsync(current.MediaId, cancellationToken)
                    .ConfigureAwait(false);
                return await RefreshManifestAfterRestoreAsync(
                        restoredOwnerId, priorOutcome, parsed.OperationId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (entry.State != TrashEntryState.InTrash)
            {
                return OperationResult<MediaRestoreOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "That Trash record is not in a state that can be restored.");
            }

            if (current.State != MediaState.Trashed)
            {
                return OperationResult<MediaRestoreOutcome>.Conflict(
                    OperationErrorCode.MediaNotActive,
                    "That media item is not in Trash.");
            }

            if (current.Sha256 is null || current.ByteLength is null || current.StorageToken is null)
            {
                return OperationResult<MediaRestoreOutcome>.NeedsAttention(
                    OperationErrorCode.MediaContentMismatch,
                    "This media item's content record is incomplete, so its recovery bytes cannot be verified.");
            }

            asset = current;
        }

        if (plan.RestoreCheckpoint is null)
        {
            var ownerId = targetOwnerProfileId ?? plan.OwnerProfileId;
            var target = await PlanCurrentRestoreTargetAsync(
                    asset,
                    plan,
                    ownerId,
                    verifiedOwnedRelativePath: null,
                    verifiedOwnedFileName: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (target.Failure is not null)
            {
                return target.Failure;
            }

            var checkpoint = new MediaRestoreCheckpoint(
                ownerId,
                VaultPathArea.TrashMedias,
                plan.RecoveryRelativePath,
                target.TargetManagedRelativePath!,
                target.TargetManagedFileName!);
            var persisted = await PersistRestoreCheckpointAsync(plan, checkpoint, cancellationToken)
                .ConfigureAwait(false);
            if (!persisted.IsSuccess || persisted.Value is null)
            {
                return Propagate<MediaTrashPlan, MediaRestoreOutcome>(persisted);
            }

            plan = persisted.Value;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var checkpoint = plan.RestoreCheckpoint!;
            if (plan.PackageComponents is { Count: > 0 } packageComponents)
            {
                foreach (var comp in packageComponents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceRelative = checkpoint.SourceArea == VaultPathArea.TrashMedias
                        ? comp.RecoveryRelativePath
                        : CombineRelative(checkpoint.SourceRelativePath, comp.ComponentRelativePath);

                    var move = await _moveExecutor.ExecuteRestoreCheckpointMoveAsync(
                        new ManagedRestoreCheckpointMoveRequest(
                            asset.MediaId,
                            checkpoint.SourceArea,
                            sourceRelative,
                            checkpoint.TargetManagedRelativePath,
                            comp.ComponentRelativePath,
                            comp.ByteLength,
                            comp.Sha256),
                        cancellationToken).ConfigureAwait(false);
                    if (!move.IsSuccess)
                    {
                        return TrashCoordinator.MapTrashMoveFailure<MediaRestoreOutcome>(move);
                    }
                }
            }
            else
            {
                var move = await _moveExecutor.ExecuteRestoreCheckpointMoveAsync(
                        new ManagedRestoreCheckpointMoveRequest(
                            asset.MediaId,
                            checkpoint.SourceArea,
                            checkpoint.SourceRelativePath,
                            checkpoint.TargetManagedRelativePath,
                            checkpoint.TargetManagedFileName,
                            asset.ByteLength!.Value,
                            asset.Sha256!),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!move.IsSuccess)
                {
                    return TrashCoordinator.MapTrashMoveFailure<MediaRestoreOutcome>(move);
                }
            }

            var desiredOwnerId = targetOwnerProfileId ?? checkpoint.OwnerProfileId;
            var currentTarget = await PlanCurrentRestoreTargetAsync(
                    asset,
                    plan,
                    desiredOwnerId,
                    checkpoint.TargetManagedRelativePath,
                    checkpoint.TargetManagedFileName,
                    cancellationToken)
                .ConfigureAwait(false);
            if (currentTarget.Failure is not null)
            {
                return currentTarget.Failure;
            }

            if (checkpoint.OwnerProfileId != desiredOwnerId
                || checkpoint.TargetManagedRelativePath != currentTarget.TargetManagedRelativePath
                || checkpoint.TargetManagedFileName != currentTarget.TargetManagedFileName)
            {
                var next = new MediaRestoreCheckpoint(
                    desiredOwnerId,
                    VaultPathArea.Profiles,
                    $"{checkpoint.TargetManagedRelativePath}/{checkpoint.TargetManagedFileName}",
                    currentTarget.TargetManagedRelativePath!,
                    currentTarget.TargetManagedFileName!);
                var persisted = await PersistRestoreCheckpointAsync(plan, next, cancellationToken)
                    .ConfigureAwait(false);
                if (!persisted.IsSuccess || persisted.Value is null)
                {
                    return Propagate<MediaTrashPlan, MediaRestoreOutcome>(persisted);
                }

                plan = persisted.Value;
                continue;
            }

            var restoredMediaAssets = new List<DurableMediaAssetTrashPlan>(plan.MediaAssets.Count);
            foreach (var mediaAsset in plan.MediaAssets)
            {
                if (!mediaAsset.HasValidPaths(plan.MediaId,plan.MediaStorageToken,plan.MediaType))
                    return OperationResult<MediaRestoreOutcome>.NeedsAttention(OperationErrorCode.TrashPlanUnreadable,"MediaAsset Restore paths are invalid.");
                if (mediaAsset.RepairOnRestore)
                {
                    restoredMediaAssets.Add(mediaAsset);
                    continue;
                }
                var mediaAssetMove = await _moveExecutor.ExecuteDurableMediaAssetMoveAsync(
                    mediaAsset.RecoveryRelativePath, VaultPathArea.TrashMedias,
                    mediaAsset.SourceRelativePath, VaultPathArea.MediaAssets,
                    mediaAsset.ByteLength, mediaAsset.Sha256, cancellationToken).ConfigureAwait(false);
                if (mediaAssetMove.Status == StorageOperationStatus.SourceMissing)
                {
                    restoredMediaAssets.Add(mediaAsset with { RepairOnRestore = true });
                    continue;
                }
                if (!mediaAssetMove.IsSuccess)
                    return TrashCoordinator.MapTrashMoveFailure<MediaRestoreOutcome>(mediaAssetMove);
                if (mediaAsset.Role == MediaAssetRole.ModelRender)
                {
                    try
                    {
                        var restoredModelRenderPath = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets,mediaAsset.SourceRelativePath);
                        await new MediaAssetFileValidator().ValidateAsync(restoredModelRenderPath,MediaAssetRole.ModelRender,cancellationToken,plan.Sha256).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        restoredMediaAssets.Add(mediaAsset with {RepairOnRestore = true});
                        continue;
                    }
                }
                restoredMediaAssets.Add(mediaAsset);
            }
            plan = plan with { MediaAssets = restoredMediaAssets };

            var committed = await CommitMediaRestoreAsync(
                    trashEntryId,
                    asset,
                    plan,
                    checkpoint,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!committed.IsSuccess)
            {
                return committed;
            }

            await RefreshRelatedProjectionsAfterRestoreAsync(plan.MediaId, cancellationToken)
                .ConfigureAwait(false);
            return await RefreshManifestAfterRestoreAsync(
                    checkpoint.OwnerProfileId, committed, plan.OperationId, cancellationToken)
                .ConfigureAwait(false);
        }

        return OperationResult<MediaRestoreOutcome>.Conflict(
            OperationErrorCode.TrashPlanStale,
            "The destination Profile kept changing while this media item was being restored. Retry when that change is finished.");
    }

    public async Task<OperationResult<ProfileRestoreOutcome>> RestoreProfileAsync(
        Guid trashEntryId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(trashEntryId, nameof(trashEntryId));

        TrashEntryRow entry;
        ProfileTrashPlan plan;
        ProfileTrashState profile;
        await using (var readConnection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            var currentEntry = await TrashCoordinator.ReadTrashEntryAsync(
                    readConnection, trashEntryId, cancellationToken)
                .ConfigureAwait(false);
            if (currentEntry is null || currentEntry.EntityType != TrashEntityType.Profile)
            {
                return OperationResult<ProfileRestoreOutcome>.NotFound(
                    OperationErrorCode.TrashEntryNotFound,
                    "That Trash record no longer exists.");
            }

            entry = currentEntry;
            var parsedPlan = ProfileTrashPlan.FromJson(entry.PlanJson);
            if (parsedPlan is null)
            {
                return OperationResult<ProfileRestoreOutcome>.NeedsAttention(
                    OperationErrorCode.TrashPlanUnreadable,
                    "This Profile Trash record cannot be read safely.");
            }
            plan = parsedPlan;
            var currentProfile = await TrashCoordinator.ReadProfileTrashStateAsync(
                    readConnection, transaction: null, entry.EntityId, cancellationToken)
                .ConfigureAwait(false);
            if (currentProfile is null)
            {
                return OperationResult<ProfileRestoreOutcome>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "That Profile no longer exists.");
            }

            profile = currentProfile;
        }

        if (entry.State == TrashEntryState.Restored && !profile.IsTrashed)
        {
            return OperationResult<ProfileRestoreOutcome>.Success(
                new ProfileRestoreOutcome(
                    trashEntryId,
                    profile.ProfileId,
                    profile.RowVersion,
                    ReturnedOwnedMediaCount: 0,
                    PreservedChangedOwnershipCount: 0),
                plan.OperationId);
        }

        if (entry.State == TrashEntryState.RestoreFinalizing && !profile.IsTrashed)
        {
            var recoveredOwnership = await _profileOwnershipRestore.RestoreAsync(plan, cancellationToken).ConfigureAwait(false);
            if (recoveredOwnership.Failure is not null)
            {
                return OperationResult<ProfileRestoreOutcome>.NeedsAttention(
                    recoveredOwnership.Failure.ErrorCode,
                    recoveredOwnership.Failure.Message,
                    plan.OperationId);
            }

            var alreadyCommitted = OperationResult<ProfileRestoreOutcome>.Success(
                new ProfileRestoreOutcome(
                    trashEntryId,
                    profile.ProfileId,
                    profile.RowVersion,
                    recoveredOwnership.ReturnedOwnedMediaCount,
                    recoveredOwnership.PreservedChangedOwnershipCount),
                plan.OperationId);
            return await FinalizeProfileRestoreAsync(
                    entry,
                    profile.ProfileId,
                    alreadyCommitted,
                    plan.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (entry.State is not (TrashEntryState.InTrash or TrashEntryState.RestoreExecuting))
        {
            return OperationResult<ProfileRestoreOutcome>.Conflict(
                OperationErrorCode.TrashPlanStale,
                "That Trash record is not in a state that can be restored.");
        }

        if (!profile.IsTrashed)
        {
            return OperationResult<ProfileRestoreOutcome>.Conflict(
                OperationErrorCode.ProfileConflict,
                "That Profile is not in Trash.");
        }

        if (string.IsNullOrWhiteSpace(profile.CurrentManagedRelativePath))
        {
            return OperationResult<ProfileRestoreOutcome>.NeedsAttention(
                OperationErrorCode.ProfilePathReconciliationBlocked,
                "That Profile has no current managed folder, so its recovery material cannot be restored safely.");
        }

        var recoveryRelativePath = entry.RecoveryRelativePath ?? $"_trash/profiles/{profile.ProfileId:D}";
        if (entry.State == TrashEntryState.InTrash)
        {
            var checkpointed = await PersistProfileRestoreCheckpointAsync(
                    entry,
                    plan,
                    recoveryRelativePath,
                    profile.CurrentManagedRelativePath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!checkpointed.IsSuccess || checkpointed.Value is null)
            {
                return Propagate<ProfileTrashPlan, ProfileRestoreOutcome>(checkpointed);
            }
            plan = checkpointed.Value;
        }
        else if (plan.RestoreCheckpoint is null)
        {
            return OperationResult<ProfileRestoreOutcome>.NeedsAttention(
                OperationErrorCode.TrashPlanUnreadable,
                "The Profile Restore is marked as executing but its durable restore checkpoint is missing.");
        }

        var restoreCheckpoint = plan.RestoreCheckpoint!;
        var move = await _moveExecutor.ExecuteProfileManifestRestoreMoveAsync(
                new ManagedProfileManifestRestoreRequest(
                    profile.ProfileId,
                    restoreCheckpoint.RecoveryRelativePath,
                    restoreCheckpoint.TargetManagedRelativePath),
                cancellationToken)
            .ConfigureAwait(false);
        if (!move.IsSuccess)
        {
            return MapProfileManifestRestoreFailure(move);
        }

        OperationResult<ProfileRestoreOutcome> committed;
        await using (var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false))
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = CatalogTransaction.Begin(connection, _writeCoordinator))
        {
            var currentEntry = await TrashCoordinator.ReadTrashEntryAsync(
                    connection, transaction, trashEntryId, cancellationToken)
                .ConfigureAwait(false);
            var currentProfile = await TrashCoordinator.ReadProfileTrashStateAsync(
                    connection, transaction, profile.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (currentEntry is null || currentProfile is null)
            {
                return OperationResult<ProfileRestoreOutcome>.NotFound(
                    OperationErrorCode.TrashEntryNotFound,
                    "That Trash record no longer exists.");
            }

            if (currentEntry.State != TrashEntryState.RestoreExecuting)
            {
                return OperationResult<ProfileRestoreOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "The Profile Restore authority changed before its catalog commit.");
            }

            if (!currentProfile.IsTrashed || currentProfile.RowVersion != profile.RowVersion)
            {
                return OperationResult<ProfileRestoreOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "This Profile changed while it was being restored.");
            }

            var now = DbTime.Format(_timeProvider.GetUtcNow());
            var newRowVersion = currentProfile.RowVersion + 1;
            var updated = await TrashCoordinator.ExecuteAsync(
                transaction,
                """
                UPDATE profiles
                SET trashed_at_ms = NULL,
                    updated_at_ms = $now,
                    row_version = $newRowVersion
                WHERE profile_id = $profileId AND row_version = $expectedRowVersion AND trashed_at_ms IS NOT NULL;
                """,
                cancellationToken,
                ("$now", now),
                ("$newRowVersion", newRowVersion),
                ("$profileId", DbGuid.Format(currentProfile.ProfileId)),
                ("$expectedRowVersion", currentProfile.RowVersion)).ConfigureAwait(false);
            if (updated != 1)
            {
                throw new CatalogInvariantException(
                    $"Profile {currentProfile.ProfileId:D} changed while it was being restored.");
            }

            foreach (var relation in plan.RelationSnapshots.Where(item => item.RelationType != "OWNER"))
            {
                await TrashCoordinator.ExecuteAsync(
                    transaction,
                    """
                    INSERT OR IGNORE INTO profile_media(
                        profile_id, media_id, relation_type, provenance_key, created_at_ms, publication_import_unit_id)
                    SELECT $profileId, $assetId, $relationType, $provenanceKey, $createdAt, $publicationImportUnitId
                    WHERE EXISTS (
                        SELECT 1 FROM media WHERE media_id = $assetId AND state = 'ACTIVE'
                    );
                    """,
                    cancellationToken,
                    ("$profileId", DbGuid.Format(currentProfile.ProfileId)),
                    ("$assetId", DbGuid.Format(relation.MediaId)),
                    ("$relationType", relation.RelationType),
                    ("$provenanceKey", (object?)relation.ProvenanceKey ?? DBNull.Value),
                    ("$createdAt", relation.CreatedAtMilliseconds),
                    ("$publicationImportUnitId", relation.PublicationImportUnitId.HasValue
                        ? DbGuid.Format(relation.PublicationImportUnitId.Value)
                        : DBNull.Value)).ConfigureAwait(false);

                await TrashCoordinator.ExecuteAsync(
                    transaction,
                    """
                    UPDATE profile_media
                    SET publication_import_unit_id = $publicationImportUnitId
                    WHERE profile_id = $profileId
                      AND media_id = $assetId
                      AND relation_type = $relationType;
                    """,
                    cancellationToken,
                    ("$publicationImportUnitId", relation.PublicationImportUnitId.HasValue
                        ? DbGuid.Format(relation.PublicationImportUnitId.Value)
                        : DBNull.Value),
                    ("$profileId", DbGuid.Format(currentProfile.ProfileId)),
                    ("$assetId", DbGuid.Format(relation.MediaId)),
                    ("$relationType", relation.RelationType)).ConfigureAwait(false);
            }

            if (plan.ActiveIdentitySnapshot is { } identity)
            {
                await TrashCoordinator.ExecuteAsync(
                    transaction,
                    """
                    UPDATE identities
                    SET is_active = 1,
                        retired_at_ms = $retiredAt,
                        row_version = row_version + 1
                    WHERE identity_id = $identityId
                      AND profile_id = $profileId
                      AND is_active = 0;
                    """,
                    cancellationToken,
                    ("$retiredAt", identity.RetiredAtMilliseconds is { } retiredAt ? retiredAt : DBNull.Value),
                    ("$identityId", DbGuid.Format(identity.IdentityId)),
                    ("$profileId", DbGuid.Format(currentProfile.ProfileId))).ConfigureAwait(false);
            }

            await TrashCoordinator.CheckpointTrashEntryAsync(
                transaction,
                trashEntryId,
                TrashEntryState.RestoreFinalizing,
                restoreCheckpoint.RecoveryRelativePath,
                completedAtMs: null,
                currentEntry.RowVersion,
                now,
                cancellationToken).ConfigureAwait(false);

            await TrashCoordinator.AppendActivityAsync(
                transaction,
                ActivityEventType.ProfileRestored,
                currentProfile.ProfileId,
                assetId: null,
                plan.OperationId,
                now,
                cancellationToken).ConfigureAwait(false);

            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [currentProfile.ProfileId],
                CatalogInvalidationDomain.Profile,
                newRowVersion));
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [currentProfile.ProfileId],
                CatalogInvalidationDomain.Trash,
                0));
            transaction.QueueInvalidation(CatalogInvalidationDomain.Health);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = OperationResult<ProfileRestoreOutcome>.Success(
                new ProfileRestoreOutcome(
                    trashEntryId,
                    currentProfile.ProfileId,
                    newRowVersion,
                    ReturnedOwnedMediaCount: 0,
                    PreservedChangedOwnershipCount: 0),
                plan.OperationId);
        }

        var ownershipSummary = await _profileOwnershipRestore.RestoreAsync(plan, cancellationToken).ConfigureAwait(false);
        if (ownershipSummary.Failure is not null)
        {
            return OperationResult<ProfileRestoreOutcome>.NeedsAttention(
                ownershipSummary.Failure.ErrorCode,
                ownershipSummary.Failure.Message,
                plan.OperationId);
        }

        committed = OperationResult<ProfileRestoreOutcome>.Success(
            committed.Value! with
            {
                ReturnedOwnedMediaCount = ownershipSummary.ReturnedOwnedMediaCount,
                PreservedChangedOwnershipCount = ownershipSummary.PreservedChangedOwnershipCount,
            },
            plan.OperationId);

        return await FinalizeProfileRestoreAsync(
                null,
                profile.ProfileId,
                committed,
                plan.OperationId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<OperationResult<ProfileTrashPlan>> PersistProfileRestoreCheckpointAsync(
        TrashEntryRow entry,
        ProfileTrashPlan plan,
        string recoveryRelativePath,
        string targetManagedRelativePath,
        CancellationToken cancellationToken)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var currentEntry = await TrashCoordinator.ReadTrashEntryAsync(
                connection, transaction, entry.TrashEntryId, cancellationToken)
            .ConfigureAwait(false);
        if (currentEntry is null)
        {
            return OperationResult<ProfileTrashPlan>.NotFound(
                OperationErrorCode.TrashEntryNotFound,
                "That Trash record no longer exists.");
        }
        if (currentEntry.State != TrashEntryState.InTrash)
        {
            return OperationResult<ProfileTrashPlan>.Conflict(
                OperationErrorCode.TrashPlanStale,
                "That Profile is no longer in a state that can start Restore.");
        }

        if (await HasActiveProfilePurgeAsync(
                connection, transaction, currentEntry.EntityId, cancellationToken).ConfigureAwait(false))
        {
            return OperationResult<ProfileTrashPlan>.Conflict(
                OperationErrorCode.PurgeStateInvalid,
                "This Profile already has an active Purge authorization, so Restore cannot start.");
        }

        var currentPlan = ProfileTrashPlan.FromJson(currentEntry.PlanJson);
        if (currentPlan is null)
        {
            return OperationResult<ProfileTrashPlan>.NeedsAttention(
                OperationErrorCode.TrashPlanUnreadable,
                "This Profile Trash record cannot be read safely.");
        }
        var checkpointed = currentPlan.RestoreCheckpoint is not null
            ? currentPlan
            : currentPlan with
            {
                RestoreCheckpoint = new ProfileRestoreCheckpoint(recoveryRelativePath, targetManagedRelativePath),
            };
        await TrashCoordinator.CheckpointTrashEntryAsync(
            transaction,
            currentEntry.TrashEntryId,
            TrashEntryState.RestoreExecuting,
            recoveryRelativePath,
            completedAtMs: null,
            currentEntry.RowVersion,
            DbTime.Format(_timeProvider.GetUtcNow()),
            cancellationToken,
            checkpointed.ToJson()).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult<ProfileTrashPlan>.Success(checkpointed, checkpointed.OperationId);
    }

    private async Task<OperationResult<ProfileRestoreOutcome>> FinalizeProfileRestoreAsync(
        TrashEntryRow? knownEntry,
        Guid profileId,
        OperationResult<ProfileRestoreOutcome> committed,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var refresh = await _manifestWriter.RegenerateManifestAsync(_catalog, profileId, cancellationToken)
            .ConfigureAwait(false);
        if (!refresh.IsSuccess)
        {
            return OperationResult<ProfileRestoreOutcome>.NeedsAttention(
                OperationErrorCode.ProfileManifestWriteFailed,
                "The Profile authority is restored, but manifest.json still needs durable recovery. Startup recovery will retry it.",
                operationId);
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var currentEntry = await TrashCoordinator.ReadTrashEntryAsync(
                connection,
                transaction,
                knownEntry?.TrashEntryId ?? committed.Value!.TrashEntryId,
                cancellationToken)
            .ConfigureAwait(false);
        if (currentEntry is null)
        {
            return OperationResult<ProfileRestoreOutcome>.NotFound(
                OperationErrorCode.TrashEntryNotFound,
                "That Trash record no longer exists.");
        }

        if (currentEntry.State == TrashEntryState.Restored)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return committed;
        }

        if (currentEntry.State != TrashEntryState.RestoreFinalizing)
        {
            return OperationResult<ProfileRestoreOutcome>.Conflict(
                OperationErrorCode.TrashPlanStale,
                "The Profile Restore authority changed before manifest finalization.");
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await TrashCoordinator.CheckpointTrashEntryAsync(
            transaction,
            currentEntry.TrashEntryId,
            TrashEntryState.Restored,
            currentEntry.RecoveryRelativePath,
            completedAtMs: now,
            currentEntry.RowVersion,
            now,
            cancellationToken).ConfigureAwait(false);
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [profileId],
            CatalogInvalidationDomain.Trash,
            0));
        transaction.QueueInvalidation(CatalogInvalidationDomain.Health);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return committed;
    }

    private static async Task<bool> HasActiveProfilePurgeAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = TrashCoordinator.CreateCommand(
            connection,
            transaction,
            """
            SELECT EXISTS(
                SELECT 1
                FROM trash_entries
                WHERE entity_type = 'PURGE_PROFILE'
                  AND entity_id = $profileId
                  AND state IN ('PURGE_PENDING','PURGE_EXECUTING','PURGE_RETRY_REQUIRED')
            );
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private Task RefreshRelatedProjectionsAfterRestoreAsync(
        Guid assetId,
        CancellationToken cancellationToken) =>
        new RelatedEvidenceProjector(_catalog, _timeProvider)
            .RefreshRelatedProjectionsForMediaAsync(assetId, cancellationToken);

    private async Task<OperationResult<T>> RefreshManifestAfterRestoreAsync<T>(
        Guid profileId,
        OperationResult<T> committed,
        Guid? operationId,
        CancellationToken cancellationToken)
    {
        var refresh = await _manifestWriter.RegenerateManifestAsync(_catalog, profileId, cancellationToken)
            .ConfigureAwait(false);
        return refresh.IsSuccess
            ? committed
            : OperationResult<T>.NeedsAttention(
                OperationErrorCode.ProfileManifestWriteFailed,
                "The restore committed safely, but manifest.json could not be refreshed from current authority. Retry the restore to repair it.",
                operationId);
    }

    private async Task<RestoreTargetResult> PlanCurrentRestoreTargetAsync(
        MediaTrashState asset,
        MediaTrashPlan plan,
        Guid ownerProfileId,
        string? verifiedOwnedRelativePath,
        string? verifiedOwnedFileName,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var owner = await TrashCoordinator.ReadProfileTrashStateAsync(
                connection, transaction: null, ownerProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (owner is null)
        {
            return RestoreTargetResult.Rejected(OperationResult<MediaRestoreOutcome>.NotFound(
                OperationErrorCode.ProfileNotFound,
                "The Profile this media item would return to no longer exists. Choose another Profile."));
        }

        if (owner.IsTrashed)
        {
            return RestoreTargetResult.Rejected(OperationResult<MediaRestoreOutcome>.Conflict(
                OperationErrorCode.RestoreOwnerUnavailable,
                "The Profile this media item would return to is itself in Trash. Choose another Profile."));
        }

        return PlanRestoreTarget(
            asset,
            plan,
            owner,
            verifiedOwnedRelativePath,
            verifiedOwnedFileName);
    }

    private RestoreTargetResult PlanRestoreTarget(
        MediaTrashState asset,
        MediaTrashPlan plan,
        ProfileTrashState owner,
        string? verifiedOwnedRelativePath,
        string? verifiedOwnedFileName)
    {
        if (owner.StorageToken is null)
        {
            return RestoreTargetResult.Rejected(OperationResult<MediaRestoreOutcome>.NeedsAttention(
                OperationErrorCode.ProfilePathReconciliationBlocked,
                "That Profile has no managed folder yet, so media cannot be restored into it."));
        }

        try
        {
            if (asset.MediaType == MediaType.Model && plan.PackageComponents is { Count: > 0 })
            {
                var primaryComponentPath = plan.PackageComponents
                    .FirstOrDefault(c => c.Role == ComponentRole.Primary)
                    ?.ComponentRelativePath
                    ?? plan.CurrentManagedFileName;

                var pkgPlan = _pathPlanner.AllocateModelPackagePlan(
                    owner.ProfileId,
                    owner.Label,
                    new ProfileStorageToken(owner.StorageToken),
                    asset.MediaId,
                    new MediaStorageToken(asset.StorageToken!),
                    primaryComponentPath,
                    candidate =>
                    {
                        if (!string.IsNullOrWhiteSpace(verifiedOwnedRelativePath)
                            && string.Equals(
                                candidate.PackageDirectoryRelativePath,
                                verifiedOwnedRelativePath,
                                StringComparison.Ordinal))
                        {
                            return false;
                        }

                        try
                        {
                            return Directory.Exists(_catalog.Paths.ResolveVaultRelativePath(
                                candidate.PackageDirectoryRelativePath));
                        }
                        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                        {
                            return true;
                        }
                    });
                return RestoreTargetResult.Accepted(
                    pkgPlan.PackageDirectoryRelativePath,
                    pkgPlan.PrimaryFileName);
            }

            var planned = _pathPlanner.AllocateMediaPlan(
                owner.ProfileId,
                owner.Label,
                new ProfileStorageToken(owner.StorageToken),
                asset.MediaId,
                new MediaStorageToken(asset.StorageToken!),
                asset.MediaType,
                Path.GetExtension(plan.CurrentManagedFileName),
                candidate =>
                {
                    var verifiedOwnedFilePath =
                        !string.IsNullOrWhiteSpace(verifiedOwnedRelativePath)
                        && !string.IsNullOrWhiteSpace(verifiedOwnedFileName)
                            ? $"{verifiedOwnedRelativePath}/{verifiedOwnedFileName}"
                            : null;
                    if (verifiedOwnedFilePath is not null
                        && string.Equals(
                            candidate.ManagedFileRelativePath,
                            verifiedOwnedFilePath,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }

                    try
                    {
                        return File.Exists(_catalog.Paths.ResolveVaultRelativePath(
                            candidate.ManagedFileRelativePath!));
                    }
                    catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                    {
                        return true;
                    }
                });
            var targetFilePath = planned.ManagedFileRelativePath!;
            return RestoreTargetResult.Accepted(
                targetFilePath[..targetFilePath.LastIndexOf('/')],
                planned.ManagedFileName!);
        }
        catch (ArgumentException)
        {
            return RestoreTargetResult.Rejected(OperationResult<MediaRestoreOutcome>.NeedsAttention(
                OperationErrorCode.ProfilePathReconciliationBlocked,
                "That Profile's stored folder or this item's file naming needs repair before the restore."));
        }
    }

    private async Task<OperationResult<MediaTrashPlan>> PersistRestoreCheckpointAsync(
        MediaTrashPlan expectedPlan,
        MediaRestoreCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        var entry = await TrashCoordinator.ReadTrashEntryAsync(
                connection, transaction, expectedPlan.TrashEntryId, cancellationToken)
            .ConfigureAwait(false);
        var currentPlan = MediaTrashPlan.FromJson(entry?.PlanJson);
        if (entry is null || currentPlan is null)
        {
            return OperationResult<MediaTrashPlan>.NeedsAttention(
                OperationErrorCode.TrashPlanUnreadable,
                "The durable Trash plan cannot be checkpointed safely.");
        }

        if (entry.State != TrashEntryState.InTrash)
        {
            return OperationResult<MediaTrashPlan>.Conflict(
                OperationErrorCode.TrashPlanStale,
                "That Trash record is no longer available for restore.");
        }

        if (currentPlan.RestoreCheckpoint == checkpoint)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult<MediaTrashPlan>.Success(currentPlan, currentPlan.OperationId);
        }

        if (currentPlan.RestoreCheckpoint != expectedPlan.RestoreCheckpoint)
        {
            return OperationResult<MediaTrashPlan>.Conflict(
                OperationErrorCode.TrashPlanStale,
                "Another restore attempt advanced the physical checkpoint. Reload and retry.");
        }

        var updatedPlan = currentPlan with { RestoreCheckpoint = checkpoint };
        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await TrashCoordinator.CheckpointTrashEntryAsync(
            transaction,
            entry.TrashEntryId,
            entry.State,
            entry.RecoveryRelativePath,
            completedAtMs: null,
            entry.RowVersion,
            now,
            cancellationToken,
            updatedPlan.ToJson()).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult<MediaTrashPlan>.Success(updatedPlan, updatedPlan.OperationId);
    }

    private static OperationResult<ProfileRestoreOutcome> MapProfileManifestRestoreFailure(
        StorageOperationResult result) => result.Status switch
        {
            StorageOperationStatus.SourceMissing => OperationResult<ProfileRestoreOutcome>.NeedsAttention(
                OperationErrorCode.CurrentPathMissing,
                "Neither the Profile recovery manifest nor a verified restored manifest exists, so Restore stopped before changing catalog authority."),
            StorageOperationStatus.SourceChanged or StorageOperationStatus.VerificationFailed =>
                OperationResult<ProfileRestoreOutcome>.NeedsAttention(
                    OperationErrorCode.TrashPlanUnreadable,
                    "The Profile recovery manifest does not match the Profile being restored."),
            StorageOperationStatus.UnexpectedTarget or StorageOperationStatus.TargetCollision =>
                OperationResult<ProfileRestoreOutcome>.NeedsAttention(
                    OperationErrorCode.TrashDestinationConflict,
                    "A manifest already occupies the current Profile folder, so nothing was overwritten."),
            StorageOperationStatus.PathOutsideVault => OperationResult<ProfileRestoreOutcome>.NeedsAttention(
                OperationErrorCode.CurrentPathAmbiguous,
                "A Profile recovery location falls outside its exact managed boundary."),
            StorageOperationStatus.Cancelled => OperationResult<ProfileRestoreOutcome>.Failed(
                OperationErrorCode.TrashPhysicalMoveFailed,
                "The Profile manifest restore was cancelled before it completed."),
            _ => OperationResult<ProfileRestoreOutcome>.Failed(
                OperationErrorCode.TrashPhysicalMoveFailed,
                "The Profile manifest could not be restored. The operation remains safe to retry."),
        };

    private async Task<OperationResult<MediaRestoreOutcome>> CommitMediaRestoreAsync(
        Guid trashEntryId,
        MediaTrashState asset,
        MediaTrashPlan plan,
        MediaRestoreCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var entry = await TrashCoordinator.ReadTrashEntryAsync(connection, transaction, trashEntryId, cancellationToken)
            .ConfigureAwait(false);
        var current = await TrashCoordinator.ReadMediaTrashStateAsync(
                connection, transaction, asset.MediaId, cancellationToken).ConfigureAwait(false);
        if (entry is null || current is null)
        {
            return OperationResult<MediaRestoreOutcome>.NotFound(
                OperationErrorCode.TrashEntryNotFound,
                "That Trash record no longer exists.");
        }

        var owner = await TrashCoordinator.ReadProfileTrashStateAsync(
                connection, transaction, checkpoint.OwnerProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (owner is null || owner.IsTrashed)
        {
            return OperationResult<MediaRestoreOutcome>.Conflict(
                OperationErrorCode.RestoreOwnerUnavailable,
                "The selected destination Profile is no longer available. Choose another Profile.");
        }

        var currentTarget = PlanRestoreTarget(
            asset,
            plan,
            owner,
            checkpoint.TargetManagedRelativePath,
            checkpoint.TargetManagedFileName);
        if (currentTarget.Failure is not null
            || currentTarget.TargetManagedRelativePath != checkpoint.TargetManagedRelativePath
            || currentTarget.TargetManagedFileName != checkpoint.TargetManagedFileName)
        {
            return OperationResult<MediaRestoreOutcome>.Conflict(
                OperationErrorCode.TrashPlanStale,
                "The destination Profile changed after the physical restore checkpoint. Retry to move the verified bytes to its current legal path.");
        }

        if (current.State == MediaState.Active && entry.State == TrashEntryState.Restored)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult<MediaRestoreOutcome>.Success(
                new MediaRestoreOutcome(
                    trashEntryId,
                    asset.MediaId,
                    checkpoint.OwnerProfileId,
                    checkpoint.TargetManagedRelativePath,
                    checkpoint.TargetManagedFileName,
                    current.RowVersion),
                plan.OperationId);
        }

        if (current.State != MediaState.Trashed || current.RowVersion != asset.RowVersion)
        {
            return OperationResult<MediaRestoreOutcome>.Conflict(
                OperationErrorCode.TrashPlanStale,
                "This media item changed while it was being restored.");
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var newRowVersion = current.RowVersion + 1;

        var updatedMedia = await TrashCoordinator.ExecuteAsync(
            transaction,
            """
            UPDATE media
            SET state = 'ACTIVE',
                trashed_at_ms = NULL,
                current_managed_relative_path = $path,
                current_managed_file_name = $fileName,
                target_managed_relative_path = $path,
                target_managed_file_name = $fileName,
                path_state = 'NONE',
                reconciliation_operation_id = NULL,
                row_version = $newRowVersion
            WHERE media_id = $assetId AND row_version = $expectedRowVersion AND state = 'TRASHED';
            """,
            cancellationToken,
            ("$path", checkpoint.TargetManagedRelativePath),
            ("$fileName", checkpoint.TargetManagedFileName),
            ("$newRowVersion", newRowVersion),
            ("$assetId", DbGuid.Format(asset.MediaId)),
            ("$expectedRowVersion", current.RowVersion)).ConfigureAwait(false);
        if (updatedMedia != 1)
        {
            throw new CatalogInvariantException(
                $"Media {asset.MediaId:D} changed while it was being restored.");
        }

        var originalOwner = plan.RelationSnapshots.FirstOrDefault(
            relation => relation.RelationType == "OWNER" && relation.ProfileId == checkpoint.OwnerProfileId);
        await TrashCoordinator.ExecuteAsync(
            transaction,
            """
            INSERT INTO profile_media(
                profile_id, media_id, relation_type, provenance_key, created_at_ms, publication_import_unit_id)
            VALUES ($profileId, $assetId, 'OWNER', $provenanceKey, $createdAt, $publicationImportUnitId);
            """,
            cancellationToken,
            ("$profileId", DbGuid.Format(checkpoint.OwnerProfileId)),
            ("$assetId", DbGuid.Format(asset.MediaId)),
            ("$provenanceKey", (object?)originalOwner?.ProvenanceKey ?? DBNull.Value),
            ("$createdAt", originalOwner?.CreatedAtMilliseconds ?? now),
            ("$publicationImportUnitId", originalOwner?.PublicationImportUnitId is { } ownerPublicationUnitId
                ? DbGuid.Format(ownerPublicationUnitId)
                : DBNull.Value)).ConfigureAwait(false);

        foreach (var relation in plan.RelationSnapshots.Where(item => item.RelationType != "OWNER"))
        {
            await TrashCoordinator.ExecuteAsync(
                transaction,
                """
                INSERT OR IGNORE INTO profile_media(
                    profile_id, media_id, relation_type, provenance_key, created_at_ms, publication_import_unit_id)
                SELECT $profileId, $assetId, $relationType, $provenanceKey, $createdAt, $publicationImportUnitId
                WHERE EXISTS (
                    SELECT 1 FROM profiles WHERE profile_id = $profileId AND trashed_at_ms IS NULL
                );
                """,
                cancellationToken,
                ("$profileId", DbGuid.Format(relation.ProfileId)),
                ("$assetId", DbGuid.Format(asset.MediaId)),
                ("$relationType", relation.RelationType),
                ("$provenanceKey", (object?)relation.ProvenanceKey ?? DBNull.Value),
                ("$createdAt", relation.CreatedAtMilliseconds),
                ("$publicationImportUnitId", relation.PublicationImportUnitId.HasValue
                        ? DbGuid.Format(relation.PublicationImportUnitId.Value)
                        : DBNull.Value)).ConfigureAwait(false);

            await TrashCoordinator.ExecuteAsync(
                transaction,
                """
                UPDATE profile_media
                SET publication_import_unit_id = $publicationImportUnitId
                WHERE profile_id = $profileId
                  AND media_id = $assetId
                  AND relation_type = $relationType;
                """,
                cancellationToken,
                ("$publicationImportUnitId", relation.PublicationImportUnitId.HasValue
                        ? DbGuid.Format(relation.PublicationImportUnitId.Value)
                        : DBNull.Value),
                ("$profileId", DbGuid.Format(relation.ProfileId)),
                ("$assetId", DbGuid.Format(asset.MediaId)),
                ("$relationType", relation.RelationType)).ConfigureAwait(false);
        }

        foreach (var appearance in plan.AffectedAppearanceReferences.Where(static item => item.IsCover))
        {
            await TrashCoordinator.ExecuteAsync(
                transaction,
                """
                UPDATE profiles
                SET cover_media_id = CASE
                        WHEN cover_media_id IS NULL THEN $assetId
                        ELSE cover_media_id
                    END,
                    updated_at_ms = $now,
                    row_version = row_version + CASE
                        WHEN cover_media_id IS NULL THEN 1 ELSE 0 END
                WHERE profile_id = $profileId AND trashed_at_ms IS NULL;
                """,
                cancellationToken,
                ("$assetId", DbGuid.Format(asset.MediaId)),
                ("$now", now),
                ("$profileId", DbGuid.Format(appearance.ProfileId))).ConfigureAwait(false);
        }

        foreach (var mediaAsset in plan.MediaAssets.Where(item => item.RepairOnRestore))
        {
            var role = mediaAsset.Role;
            var kind = asset.MediaType == MediaType.Video ? "GenerateVideoMediaAssets"
                : asset.MediaType == MediaType.Model ? "GenerateModelMediaAssets" : "GenerateThumbnail";
            var lane = kind == "GenerateThumbnail" && asset.MediaType == MediaType.Image
                ? JobLane.Cpu : JobLane.Media;
            await TrashCoordinator.ExecuteAsync(transaction, """
                UPDATE media_assets SET state = 'NEEDS_REPAIR', row_version = row_version + 1,
                    updated_at_ms = $now WHERE media_id = $assetId AND role = $role;
                """, cancellationToken,
                ("$now", now), ("$assetId", DbGuid.Format(asset.MediaId)),
                ("$role", DbEnum.Format(role))).ConfigureAwait(false);
            await TrashCoordinator.ExecuteAsync(transaction, """
                INSERT INTO jobs(job_id, kind, lane, state, priority, owner_type, owner_id,
                    attempt, max_attempts, checkpoint_json, created_at_ms)
                SELECT $id, $kind, $lane, 'PENDING', $priority, 'Media', $assetId,
                    0, $maxAttempts, '{}', $now
                WHERE NOT EXISTS (SELECT 1 FROM jobs WHERE owner_type = 'Media' AND owner_id = $assetId
                    AND kind = $kind AND state IN ('PENDING','RUNNABLE','RUNNING','PAUSED','FAILED_RETRYABLE','WAITING_DEPENDENCY','WAITING_USER_INPUT'));
                """, cancellationToken,
                ("$id", DbGuid.Format(Guid.NewGuid())), ("$kind", kind),
                ("$lane", DbEnum.Format(lane)),
                ("$priority", JobPriorityPolicy.PriorityBackground),
                ("$assetId", DbGuid.Format(asset.MediaId)),
                ("$maxAttempts", JobRetryPolicy.DefaultMaxAttempts),
                ("$now", now)).ConfigureAwait(false);
        }

        foreach (var reference in plan.SelectedMediaReferences)
        {
            await TrashCoordinator.ExecuteAsync(transaction, """
                UPDATE profile_appearance
                SET cover_media_asset_id = CASE
                        WHEN $cover IS NOT NULL AND cover_media_asset_id IS NULL
                          AND EXISTS (SELECT 1 FROM media_assets ma WHERE ma.media_asset_id = $cover
                                      AND ma.state = 'READY')
                        THEN $cover ELSE cover_media_asset_id END,
                    banner_media_asset_id = CASE
                        WHEN $banner IS NOT NULL AND banner_media_asset_id IS NULL
                          AND EXISTS (SELECT 1 FROM media_assets ma WHERE ma.media_asset_id = $banner
                                      AND ma.state = 'READY')
                        THEN $banner ELSE banner_media_asset_id END,
                    figure_media_id = CASE
                        WHEN $figure IS NOT NULL AND figure_media_id IS NULL
                        THEN $figure ELSE figure_media_id END,
                    updated_at_ms = $now, row_version = row_version + 1
                WHERE profile_id = $profileId AND row_version = $expectedVersion
                  AND EXISTS (SELECT 1 FROM profiles p WHERE p.profile_id = $profileId
                              AND p.trashed_at_ms IS NULL)
                  AND EXISTS (SELECT 1 FROM profile_media pm
                              WHERE pm.profile_id = $profileId AND pm.media_id = $mediaId
                                AND pm.publication_import_unit_id IS NULL);
                """, cancellationToken,
                ("$cover", reference.CoverMediaAssetId is { } cover ? DbGuid.Format(cover) : DBNull.Value),
                ("$banner", reference.BannerMediaAssetId is { } banner ? DbGuid.Format(banner) : DBNull.Value),
                ("$figure", reference.FigureMediaId is { } figure ? DbGuid.Format(figure) : DBNull.Value),
                ("$profileId", DbGuid.Format(reference.ProfileId)),
                ("$expectedVersion", reference.AppearanceRowVersion + 1),
                ("$mediaId", DbGuid.Format(asset.MediaId)),
                ("$now", now)).ConfigureAwait(false);
        }

        await TrashCoordinator.CheckpointTrashEntryAsync(
            transaction,
            trashEntryId,
            TrashEntryState.Restored,
            entry.RecoveryRelativePath,
            completedAtMs: now,
            entry.RowVersion,
            now,
            cancellationToken,
            plan.ToJson()).ConfigureAwait(false);

        await TrashCoordinator.AppendActivityAsync(
            transaction,
            ActivityEventType.MediaRestored,
            checkpoint.OwnerProfileId,
            asset.MediaId,
            plan.OperationId,
            now,
            cancellationToken).ConfigureAwait(false);

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [asset.MediaId],
            CatalogInvalidationDomain.Media,
            newRowVersion));
        var restoredProfileIds = plan.RelationSnapshots
            .Select(relation => relation.ProfileId)
            .Concat(plan.AffectedAppearanceReferences.Select(reference => reference.ProfileId))
            .Append(checkpoint.OwnerProfileId)
            .Distinct()
            .ToArray();
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            restoredProfileIds,
            CatalogInvalidationDomain.Profile,
            0));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [asset.MediaId],
            CatalogInvalidationDomain.Trash,
            0));
        transaction.QueueInvalidation(CatalogInvalidationDomain.Health);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (plan.MediaAssets.Any(item => item.RepairOnRestore)) JobSignals.Raise();

        return OperationResult<MediaRestoreOutcome>.Success(
            new MediaRestoreOutcome(
                trashEntryId,
                asset.MediaId,
                checkpoint.OwnerProfileId,
                checkpoint.TargetManagedRelativePath,
                checkpoint.TargetManagedFileName,
                newRowVersion),
            plan.OperationId);
    }

    private static string CombineRelative(string directory, string fileName)
    {
        var trimmed = directory.TrimEnd('/', '\\');
        return trimmed.Length == 0 ? fileName : $"{trimmed}/{fileName.TrimStart('/', '\\')}";
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }

    private static OperationResult<TTarget> Propagate<TSource, TTarget>(OperationResult<TSource> source) =>
        new(source.Status, default, source.ErrorCode, source.UserMessage, source.OperationId);

    private sealed record RestoreTargetResult(
        string? TargetManagedRelativePath,
        string? TargetManagedFileName,
        OperationResult<MediaRestoreOutcome>? Failure)
    {
        public static RestoreTargetResult Accepted(string targetManagedRelativePath, string targetManagedFileName) =>
            new(targetManagedRelativePath, targetManagedFileName, null);

        public static RestoreTargetResult Rejected(OperationResult<MediaRestoreOutcome> failure) =>
            new(null, null, failure);
    }
}

public sealed record MediaRestoreOutcome(
    Guid TrashEntryId,
    Guid MediaId,
    Guid OwnerProfileId,
    string CurrentManagedRelativePath,
    string CurrentManagedFileName,
    long MediaRowVersion);

public sealed record ProfileRestoreOutcome(
    Guid TrashEntryId,
    Guid ProfileId,
    long ProfileRowVersion,
    int ReturnedOwnedMediaCount,
    int PreservedChangedOwnershipCount);
