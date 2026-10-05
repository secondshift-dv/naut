using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Trash;

public sealed class PurgeExecutor
{
    private readonly CatalogDb _catalog;
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly ManagedMoveExecutor _moveExecutor;
    private readonly TimeProvider _timeProvider;

    public PurgeExecutor(
        CatalogDb catalog,
        ManagedMoveExecutor moveExecutor,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _moveExecutor = moveExecutor ?? throw new ArgumentNullException(nameof(moveExecutor));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<OperationResult<PurgePlan>> PreparePurgeMediaAsync(
        Guid trashEntryId,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(PreparePurgeMediaAsync));
        return PrepareAsync(trashEntryId, PurgeEntityType.Media, cancellationToken);
    }

    public Task<OperationResult<PurgePlan>> PreparePurgeProfileAsync(
        Guid trashEntryId,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(PreparePurgeProfileAsync));
        return PrepareAsync(trashEntryId, PurgeEntityType.Profile, cancellationToken);
    }

    public Task<OperationResult<PurgeOutcome>> ConfirmPurgeMediaAsync(
        Guid purgePlanId,
        bool irreversibleConfirmation,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(ConfirmPurgeMediaAsync));
        return ConfirmAsync(purgePlanId, PurgeEntityType.Media, irreversibleConfirmation, cancellationToken);
    }

    public Task<OperationResult<PurgeOutcome>> ConfirmPurgeProfileAsync(
        Guid purgePlanId,
        bool irreversibleConfirmation,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(ConfirmPurgeProfileAsync));
        return ConfirmAsync(purgePlanId, PurgeEntityType.Profile, irreversibleConfirmation, cancellationToken);
    }

    private async Task<OperationResult<PurgePlan>> PrepareAsync(
        Guid trashEntryId,
        string expectedEntityType,
        CancellationToken cancellationToken)
    {
        EnsureNonEmpty(trashEntryId, nameof(trashEntryId));

        PurgePlan plan;
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var trashEntry = await TrashCoordinator.ReadTrashEntryAsync(connection, trashEntryId, cancellationToken)
                .ConfigureAwait(false);
            if (trashEntry is null)
            {
                return OperationResult<PurgePlan>.NotFound(
                    OperationErrorCode.TrashEntryNotFound,
                    "That Trash record no longer exists.");
            }

            if (trashEntry.EntityType != expectedEntityType || trashEntry.State != TrashEntryState.InTrash)
            {
                return OperationResult<PurgePlan>.Conflict(
                    OperationErrorCode.PurgeStateInvalid,
                    "Only an entity that is currently in Trash can be permanently purged.");
            }

            var existing = await ReadActivePurgeEntryAsync(
                    connection, expectedEntityType, trashEntry.EntityId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                var existingPlan = PurgePlan.FromJson(existing.PlanJson);
                return existingPlan is null
                    ? OperationResult<PurgePlan>.NeedsAttention(
                        OperationErrorCode.PurgePlanUnreadable,
                        "An earlier Purge authorization cannot be read and needs attention.")
                    : OperationResult<PurgePlan>.Success(existingPlan, existingPlan.OperationId);
            }

            var entityVersion = await ReadAndValidateEntityVersionAsync(
                    connection, transaction: null, expectedEntityType, trashEntry.EntityId, cancellationToken)
                .ConfigureAwait(false);
            if (entityVersion is null)
            {
                return OperationResult<PurgePlan>.Conflict(
                    OperationErrorCode.PurgeStateInvalid,
                    "The entity is no longer in the inactive state required for Purge.");
            }

            if (expectedEntityType == PurgeEntityType.Profile)
            {
                var assignmentBlocker = await ReadProfileAssignmentPurgeBlockerAsync(
                        connection, transaction: null, trashEntry.EntityId, cancellationToken)
                    .ConfigureAwait(false);
                if (assignmentBlocker is not null)
                {
                    return OperationResult<PurgePlan>.Conflict(
                        OperationErrorCode.PurgeDependencyBlocked,
                        assignmentBlocker);
                }
            }

            if (await CountBlockingDependenciesAsync(
                    connection, transaction: null, expectedEntityType, trashEntry.EntityId, cancellationToken)
                .ConfigureAwait(false) != 0)
            {
                return OperationResult<PurgePlan>.Conflict(
                    OperationErrorCode.PurgeDependencyBlocked,
                    "Active ownership, appearance, workflow, or storage authority still requires this entity.");
            }

            IReadOnlyList<PurgePhysicalTarget> targets;
            try
            {
                targets = expectedEntityType == PurgeEntityType.Media
                    ? await BuildMediaTargetsAsync(connection, trashEntry, cancellationToken).ConfigureAwait(false)
                    : await BuildProfileTargetsAsync(trashEntry.EntityId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return OperationResult<PurgePlan>.NeedsAttention(
                    OperationErrorCode.PurgePhysicalDeleteFailed,
                    exception.Message);
            }

            var affected = await ReadAffectedDataAsync(
                    connection, transaction: null, expectedEntityType, trashEntry.EntityId, cancellationToken)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            plan = new PurgePlan(
                Guid.NewGuid(),
                expectedEntityType,
                trashEntry.EntityId,
                trashEntry.TrashEntryId,
                targets,
                affected,
                [
                    new PurgeAuthorityVersion(expectedEntityType, trashEntry.EntityId, entityVersion.Value),
                    new PurgeAuthorityVersion("TRASH_ENTRY", trashEntry.TrashEntryId, trashEntry.RowVersion),
                ],
                now,
                expectedEntityType == PurgeEntityType.Media
                    ? "This permanently and irreversibly removes the media and its recovery material."
                    : "This permanently and irreversibly removes the Profile and its recovery material.",
                Guid.NewGuid());
        }

        try
        {
            await _catalog.TrashWrites.PersistEntryAsync(
                    new TrashEntryPersistence(
                        plan.PurgePlanId,
                        OperationEntityType(plan.EntityType),
                        plan.EntityId,
                        PurgePlanState.Pending,
                        RecoveryRelativePath: null,
                        plan.ToJson(),
                        plan.PreparedAtUtc,
                        plan.PreparedAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException exception) when (
            expectedEntityType == PurgeEntityType.Profile
            && exception.SqliteErrorCode == 19)
        {
            return OperationResult<PurgePlan>.Conflict(
                OperationErrorCode.PurgeStateInvalid,
                "Profile Restore started before this Purge authorization could be persisted. Finish Restore before preparing Purge again.");
        }

        return OperationResult<PurgePlan>.Success(plan, plan.OperationId);
    }

    private async Task<OperationResult<PurgeOutcome>> ConfirmAsync(
        Guid purgePlanId,
        string expectedEntityType,
        bool irreversibleConfirmation,
        CancellationToken cancellationToken)
    {
        EnsureNonEmpty(purgePlanId, nameof(purgePlanId));
        if (!irreversibleConfirmation)
        {
            return OperationResult<PurgeOutcome>.Validation(
                OperationErrorCode.PurgeConfirmationRequired,
                "Confirm that this irreversible Purge should permanently destroy the recovery material.");
        }

        PurgePlan plan;
        TrashEntryRow purgeEntry;
        bool mayHaveDeletedCurrentTarget;
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var persistedEntry = await TrashCoordinator.ReadTrashEntryAsync(connection, purgePlanId, cancellationToken)
                .ConfigureAwait(false);
            if (persistedEntry is null)
            {
                return OperationResult<PurgeOutcome>.NotFound(
                    OperationErrorCode.PurgePlanNotFound,
                    "That Purge authorization no longer exists.");
            }
            purgeEntry = persistedEntry;
            if (purgeEntry.EntityType != OperationEntityType(expectedEntityType))
            {
                return OperationResult<PurgeOutcome>.Conflict(
                    OperationErrorCode.PurgePlanStale,
                    "That Purge authorization belongs to a different entity type.");
            }

            plan = PurgePlan.FromJson(purgeEntry.PlanJson)!;
            if (plan is null || plan.EntityType != expectedEntityType || plan.PurgePlanId != purgePlanId)
            {
                return OperationResult<PurgeOutcome>.NeedsAttention(
                    OperationErrorCode.PurgePlanUnreadable,
                    "The persisted Purge authorization cannot be read safely.");
            }

            if (purgeEntry.State == PurgePlanState.Purged)
            {
                return OperationResult<PurgeOutcome>.Success(
                    new PurgeOutcome(plan.PurgePlanId, plan.EntityType, plan.EntityId, plan.PhysicalDeletionTargets.Count),
                    plan.OperationId);
            }

            if (purgeEntry.State is not (PurgePlanState.Pending or PurgePlanState.Executing or PurgePlanState.RetryRequired))
            {
                return OperationResult<PurgeOutcome>.Conflict(
                    OperationErrorCode.PurgePlanStale,
                    "That Purge authorization is no longer active.");
            }

            var validation = await ValidatePreparedAuthorityAsync(
                    connection, transaction: null, plan, cancellationToken).ConfigureAwait(false);
            if (validation is not null)
            {
                return validation;
            }

            mayHaveDeletedCurrentTarget = purgeEntry.State == PurgePlanState.Executing;
            if (purgeEntry.State == PurgePlanState.Pending && !AllTargetsExist(plan))
            {
                return OperationResult<PurgeOutcome>.NeedsAttention(
                    OperationErrorCode.PurgePhysicalDeleteFailed,
                    "Prepared recovery material is already missing; Purge was not started.",
                    plan.OperationId);
            }
        }

        if (purgeEntry.State != PurgePlanState.Executing)
        {
            await _catalog.TrashWrites.UpdateEntryStateAsync(
                    purgePlanId,
                    PurgePlanState.Executing,
                    recoveryRelativePath: null,
                    completedAtUtc: null,
                    purgeEntry.RowVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var purgeEntryRowVersion = purgeEntry.RowVersion
            + (purgeEntry.State == PurgePlanState.Executing ? 0 : 1);
        var startingTarget = plan.CompletedPhysicalTargetCount;
        for (var targetIndex = startingTarget; targetIndex < plan.PhysicalDeletionTargets.Count; targetIndex++)
        {
            var target = plan.PhysicalDeletionTargets[targetIndex];
            var deletion = await _moveExecutor.ExecutePurgeDeleteAsync(
                    new ManagedPurgeDeleteRequest(
                        target.Area,
                        target.VaultRelativePath,
                        target.Kind,
                        target.ExpectedByteLength,
                        target.ExpectedSha256),
                    allowAlreadyMissing: mayHaveDeletedCurrentTarget && targetIndex == startingTarget,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!deletion.IsSuccess)
            {
                await MarkRetryRequiredAsync(purgePlanId, cancellationToken).ConfigureAwait(false);
                return OperationResult<PurgeOutcome>.NeedsAttention(
                    OperationErrorCode.PurgePhysicalDeleteFailed,
                    PhysicalFailureMessage(deletion.Status),
                    plan.OperationId);
            }

            plan = plan with { CompletedPhysicalTargetCount = targetIndex + 1 };
            purgeEntryRowVersion = await CheckpointProgressAsync(
                    purgePlanId,
                    plan,
                    purgeEntryRowVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            mayHaveDeletedCurrentTarget = false;
        }

        return await CommitPurgeAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationResult<PurgeOutcome>> CommitPurgeAsync(
        PurgePlan plan,
        CancellationToken cancellationToken)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var validation = await ValidatePreparedAuthorityAsync(connection, transaction, plan, cancellationToken)
            .ConfigureAwait(false);
        if (validation is not null)
        {
            return validation;
        }

        var nowMs = DbTime.Format(_timeProvider.GetUtcNow());
        if (plan.EntityType == PurgeEntityType.Media)
        {
            await DeleteMediaAuthorityAsync(transaction, plan.EntityId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DeleteProfileAuthorityAsync(transaction, plan.EntityId, cancellationToken).ConfigureAwait(false);
        }

        await MarkPurgedAsync(transaction, plan.TrashEntryId, nowMs, cancellationToken).ConfigureAwait(false);
        await MarkPurgedAsync(transaction, plan.PurgePlanId, nowMs, cancellationToken).ConfigureAwait(false);
        await TrashCoordinator.AppendActivityAsync(
                transaction,
                plan.EntityType == PurgeEntityType.Media ? ActivityEventType.MediaPurged : ActivityEventType.ProfilePurged,
                plan.EntityType == PurgeEntityType.Profile ? plan.EntityId : null,
                plan.EntityType == PurgeEntityType.Media ? plan.EntityId : null,
                plan.OperationId,
                nowMs,
                cancellationToken)
            .ConfigureAwait(false);
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [plan.EntityId],
            plan.EntityType == PurgeEntityType.Media ? CatalogInvalidationDomain.Media : CatalogInvalidationDomain.Profile,
            0));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [plan.EntityId],
            CatalogInvalidationDomain.Trash,
            0));
        transaction.QueueInvalidation(CatalogInvalidationDomain.Health);
        if (plan.EntityType == PurgeEntityType.Media)
        {
            // Media purge removes profile_media rows; affected Profile IDs are not individually
            // tracked in DependentDataAffected (which stores only aggregate counts). A global
            // PROFILE invalidation lets Gallery converge on stale membership.
            transaction.QueueInvalidation(CatalogInvalidationDomain.Profile);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return OperationResult<PurgeOutcome>.Success(
            new PurgeOutcome(plan.PurgePlanId, plan.EntityType, plan.EntityId, plan.PhysicalDeletionTargets.Count),
            plan.OperationId);
    }

    private async Task<OperationResult<PurgeOutcome>?> ValidatePreparedAuthorityAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        PurgePlan plan,
        CancellationToken cancellationToken)
    {
        var trashEntry = await ReadTrashEntryAsync(connection, transaction, plan.TrashEntryId, cancellationToken)
            .ConfigureAwait(false);
        var trashVersion = plan.PreparedAuthorityVersions.SingleOrDefault(item => item.EntityType == "TRASH_ENTRY");
        var entityVersion = plan.PreparedAuthorityVersions.SingleOrDefault(item => item.EntityType == plan.EntityType);
        if (trashEntry is null
            || trashVersion is null
            || entityVersion is null
            || trashEntry.EntityType != plan.EntityType
            || trashEntry.EntityId != plan.EntityId
            || trashEntry.State != TrashEntryState.InTrash
            || trashEntry.RowVersion != trashVersion.RowVersion)
        {
            return OperationResult<PurgeOutcome>.Conflict(
                OperationErrorCode.PurgePlanStale,
                "The Trash authority changed after this Purge was prepared. Prepare a new authorization.");
        }

        var currentEntityVersion = await ReadAndValidateEntityVersionAsync(
                connection, transaction, plan.EntityType, plan.EntityId, cancellationToken)
            .ConfigureAwait(false);
        if (currentEntityVersion != entityVersion.RowVersion)
        {
            return OperationResult<PurgeOutcome>.Conflict(
                OperationErrorCode.PurgePlanStale,
                "The entity changed after this Purge was prepared. Prepare a new authorization.");
        }

        if (plan.EntityType == PurgeEntityType.Profile)
        {
            var assignmentBlocker = await ReadProfileAssignmentPurgeBlockerAsync(
                    connection, transaction, plan.EntityId, cancellationToken)
                .ConfigureAwait(false);
            if (assignmentBlocker is not null)
            {
                return OperationResult<PurgeOutcome>.Conflict(
                    OperationErrorCode.PurgeDependencyBlocked,
                    assignmentBlocker);
            }
        }

        if (await CountBlockingDependenciesAsync(
                connection, transaction, plan.EntityType, plan.EntityId, cancellationToken)
            .ConfigureAwait(false) != 0)
        {
            return OperationResult<PurgeOutcome>.Conflict(
                OperationErrorCode.PurgeDependencyBlocked,
                "Active authority now depends on this entity, so Purge was stopped.");
        }

        var affected = await ReadAffectedDataAsync(
                connection, transaction, plan.EntityType, plan.EntityId, cancellationToken)
            .ConfigureAwait(false);
        return affected.SequenceEqual(plan.DependentDataAffected)
            ? null
            : OperationResult<PurgeOutcome>.Conflict(
                OperationErrorCode.PurgePlanStale,
                "Dependent data changed after this Purge was prepared. Prepare a new authorization.");
    }

    private async Task<IReadOnlyList<PurgePhysicalTarget>> BuildMediaTargetsAsync(
        SqliteConnection connection,
        TrashEntryRow trashEntry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(trashEntry.RecoveryRelativePath)
            || !trashEntry.RecoveryRelativePath.StartsWith(
                $"_trash/media/{trashEntry.EntityId:D}/",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Media recovery path is not the exact stable-ID Trash location.");
        }

        var plan = MediaTrashPlan.FromJson(trashEntry.PlanJson);
        if (plan?.PackageComponents is { Count: > 0 } packageComponents)
        {
            var targets = new List<PurgePhysicalTarget>();
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var comp in packageComponents)
            {
                var compAbs = ResolveTarget(VaultPathArea.TrashMedias, comp.RecoveryRelativePath);
                RootPathRules.RejectExistingReparsePoints(_catalog.Paths.Root, compAbs);
                if (!File.Exists(compAbs))
                {
                    throw new InvalidDataException($"The Media component recovery bytes for '{comp.ComponentRelativePath}' are missing, so irreversible deletion was not authorized.");
                }

                targets.Add(new PurgePhysicalTarget(
                    VaultPathArea.TrashMedias,
                    comp.RecoveryRelativePath,
                    PurgePhysicalTargetKind.File,
                    comp.ByteLength,
                    comp.Sha256));

                var dir = comp.RecoveryRelativePath[..comp.RecoveryRelativePath.LastIndexOf('/')];
                directories.Add(dir);
            }

            var rootTrashDir = $"_trash/media/{trashEntry.EntityId:D}";
            directories.Add(rootTrashDir);

            AddMediaAssetTargets(plan, targets);
            foreach (var dir in directories.OrderByDescending(d => d.Length))
            {
                targets.Add(new PurgePhysicalTarget(
                    VaultPathArea.TrashMedias,
                    dir,
                    PurgePhysicalTargetKind.EmptyDirectory));
            }

            return targets;
        }

        var absolute = ResolveTarget(VaultPathArea.TrashMedias, trashEntry.RecoveryRelativePath);
        RootPathRules.RejectExistingReparsePoints(_catalog.Paths.Root, absolute);
        if (!File.Exists(absolute))
        {
            throw new InvalidDataException("The Media recovery bytes are missing, so irreversible deletion was not authorized.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT byte_length, sha256 FROM media WHERE media_id = $id;";
        command.Parameters.AddWithValue("$id", DbGuid.Format(trashEntry.EntityId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.IsDBNull(0)
            || reader.IsDBNull(1))
        {
            throw new InvalidDataException("The Media fingerprint is incomplete, so irreversible deletion was not authorized.");
        }

        var directory = trashEntry.RecoveryRelativePath[..trashEntry.RecoveryRelativePath.LastIndexOf('/')];
        var originalTargets = new List<PurgePhysicalTarget>
        {
            new PurgePhysicalTarget(
                VaultPathArea.TrashMedias,
                trashEntry.RecoveryRelativePath,
                PurgePhysicalTargetKind.File,
                reader.GetInt64(0),
                reader.GetString(1)),
        };
        AddMediaAssetTargets(plan, originalTargets);
        originalTargets.Add(new PurgePhysicalTarget(
            VaultPathArea.TrashMedias, directory, PurgePhysicalTargetKind.EmptyDirectory));
        return originalTargets;
    }

    private void AddMediaAssetTargets(MediaTrashPlan? plan, List<PurgePhysicalTarget> targets)
    {
        if (plan is null) throw new InvalidDataException("The Media Trash plan cannot be read.");
        foreach (var mediaAsset in plan.MediaAssets)
        {
            if (!mediaAsset.HasValidPaths(plan.MediaId,plan.MediaStorageToken,plan.MediaType))
                throw new InvalidDataException("An MediaAsset Purge path is outside its exact recovery boundary.");
            if (mediaAsset.RepairOnRestore) continue;
            var absolute = ResolveTarget(VaultPathArea.TrashMedias, mediaAsset.RecoveryRelativePath);
            RootPathRules.RejectExistingReparsePoints(_catalog.Paths.Root, absolute);
            if (!File.Exists(absolute))
                throw new InvalidDataException("An MediaAsset recovery file is missing; Purge was not authorized.");
            targets.Add(new PurgePhysicalTarget(VaultPathArea.TrashMedias,
                mediaAsset.RecoveryRelativePath, PurgePhysicalTargetKind.File,
                mediaAsset.ByteLength, mediaAsset.Sha256));
        }
        if (plan.MediaAssets.Any(item => !item.RepairOnRestore))
            targets.Add(new PurgePhysicalTarget(VaultPathArea.TrashMedias,
                $"_trash/media/{plan.MediaId:D}/media-assets", PurgePhysicalTargetKind.EmptyDirectory));
    }

    private async Task<IReadOnlyList<PurgePhysicalTarget>> BuildProfileTargetsAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var relativeRoot = $"_trash/profiles/{profileId:D}";
        var absoluteRoot = ResolveTarget(VaultPathArea.TrashProfiles, relativeRoot);
        if (!Directory.Exists(absoluteRoot))
        {
            return [];
        }

        RootPathRules.RejectExistingReparsePoints(_catalog.Paths.Root, absoluteRoot);

        var targets = new List<PurgePhysicalTarget>();
        foreach (var file in Directory.EnumerateFiles(absoluteRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RootPathRules.RejectExistingReparsePoints(_catalog.Paths.Root, file);
            var relativePath = ToVaultRelative(file);
            var verifiedPath = ResolveTarget(VaultPathArea.TrashProfiles, relativePath);
            await using var stream = new FileStream(
                verifiedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            targets.Add(new PurgePhysicalTarget(
                VaultPathArea.TrashProfiles,
                relativePath,
                PurgePhysicalTargetKind.File,
                stream.Length,
                hash));
        }

        foreach (var directory in Directory.EnumerateDirectories(absoluteRoot, "*", SearchOption.AllDirectories)
                     .Append(absoluteRoot)
                     .OrderByDescending(path => path.Count(character => character is '\\' or '/'))
                     .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            RootPathRules.RejectExistingReparsePoints(_catalog.Paths.Root, directory);
            var relativePath = ToVaultRelative(directory);
            _ = ResolveTarget(VaultPathArea.TrashProfiles, relativePath);
            targets.Add(new PurgePhysicalTarget(
                VaultPathArea.TrashProfiles,
                relativePath,
                PurgePhysicalTargetKind.EmptyDirectory));
        }

        return targets;
    }

    private bool AllTargetsExist(PurgePlan plan)
    {
        foreach (var target in plan.PhysicalDeletionTargets)
        {
            string absolute;
            try
            {
                absolute = ResolveTarget(target.Area, target.VaultRelativePath);
            }
            catch (InvalidDataException)
            {
                return false;
            }

            if (target.Kind == PurgePhysicalTargetKind.File ? !File.Exists(absolute) : !Directory.Exists(absolute))
            {
                return false;
            }
        }
        return true;
    }

    private string ResolveTarget(VaultPathArea area, string relativePath)
    {
        try
        {
            return _catalog.Paths.ResolveVaultRelativePath(area, relativePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("A Purge target falls outside its exact Trash recovery boundary.", exception);
        }
    }

    private string ToVaultRelative(string absolutePath) =>
        Path.GetRelativePath(_catalog.Paths.Root, absolutePath).Replace('\\', '/');

    private static async Task<long?> ReadAndValidateEntityVersionAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        var sql = entityType == PurgeEntityType.Media
            ? "SELECT row_version FROM media WHERE media_id = $id AND state = 'TRASHED';"
            : "SELECT row_version FROM profiles WHERE profile_id = $id AND trashed_at_ms IS NOT NULL;";
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("$id", DbGuid.Format(entityId));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static async Task<string?> ReadProfileAssignmentPurgeBlockerAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT
                SUM(CASE WHEN state = 'PENDING' AND candidate_profile_id = $id THEN 1 ELSE 0 END),
                SUM(CASE WHEN state = 'ACCEPTED' AND decided_profile_id = $id THEN 1 ELSE 0 END),
                SUM(CASE WHEN state IN ('ACCEPTED','KEPT_UNKNOWN') AND candidate_profile_id = $id THEN 1 ELSE 0 END)
            FROM import_assignment_clusters
            WHERE candidate_profile_id = $id OR decided_profile_id = $id;
            """);
        command.Parameters.AddWithValue("$id", DbGuid.Format(profileId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var pendingCandidates = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
        var acceptedDecisions = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
        var terminalCandidates = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
        if (pendingCandidates == 0 && acceptedDecisions == 0 && terminalCandidates == 0)
        {
            return null;
        }

        if (acceptedDecisions > 0)
        {
            return "Purge is intentionally blocked because an ACCEPTED import-assignment decision still names this Profile. The durable decision is historical authority and is never nulled implicitly.";
        }

        if (pendingCandidates > 0)
        {
            return "Purge is blocked while pending import-assignment evidence still proposes this Profile. Resolve or recompute those assignment groups before Purge.";
        }

        return "Purge is intentionally blocked because terminal import-assignment evidence still records this Profile as a historical candidate. That history is retained rather than silently rewritten by Purge.";
    }

    private static async Task<long> CountBlockingDependenciesAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        var sql = entityType == PurgeEntityType.Media
            ? """
              SELECT
                (SELECT COUNT(*) FROM profile_media WHERE media_id = $id AND relation_type = 'OWNER') +
                (SELECT COUNT(*) FROM profiles WHERE cover_media_id = $id) +
                (SELECT COUNT(*)
                 FROM profile_appearance pa
                 JOIN media_assets ma ON ma.media_asset_id = pa.banner_media_asset_id
                 WHERE ma.media_id = $id) +
                (SELECT COUNT(*) FROM storage_operations WHERE entity_type = 'MEDIA' AND entity_id = $id
                  AND state IN ('PENDING','EXECUTING','RUNNING','NEEDS_ATTENTION')) +
                (SELECT COUNT(*) FROM jobs WHERE owner_type = 'Media' AND owner_id = $id
                  AND state IN ('PENDING','RUNNABLE','RUNNING','PAUSED','FAILED_RETRYABLE'));
              """
            : """
              SELECT
                (SELECT COUNT(*) FROM profile_media pa JOIN media a ON a.media_id = pa.media_id
                  WHERE pa.profile_id = $id AND pa.relation_type = 'OWNER' AND a.state = 'ACTIVE') +
                (SELECT COUNT(*) FROM storage_operations WHERE entity_type = 'PROFILE' AND entity_id = $id
                  AND state IN ('PENDING','EXECUTING','RUNNING','NEEDS_ATTENTION')) +
                (SELECT COUNT(*) FROM jobs WHERE owner_type = 'PROFILE' AND owner_id = $id
                  AND state IN ('PENDING','RUNNABLE','RUNNING','PAUSED','FAILED_RETRYABLE')) +
                (SELECT COUNT(*) FROM import_units WHERE destination_profile_id = $id AND completed_at_ms IS NULL) +
                (SELECT COUNT(*) FROM import_assignment_clusters
                  WHERE candidate_profile_id = $id OR decided_profile_id = $id);
              """;
        return await ReadCountAsync(connection, transaction, sql, entityId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<PurgeAffectedData>> ReadAffectedDataAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        var queries = entityType == PurgeEntityType.Media
            ? new (string Name, string Sql)[]
            {
                ("media_metadata", "SELECT COUNT(*) FROM media_metadata WHERE media_id = $id;"),
                ("face_detections", "SELECT COUNT(*) FROM face_detections WHERE media_id = $id;"),
                ("identity_samples", "SELECT COUNT(*) FROM identity_samples WHERE face_id IN (SELECT face_id FROM face_detections WHERE media_id = $id);"),
                ("import_items", "SELECT COUNT(*) FROM import_items WHERE candidate_media_id = $id OR reused_media_id = $id;"),
                ("profile_media", "SELECT COUNT(*) FROM profile_media WHERE media_id = $id;"),
                ("related_profile_evidence", "SELECT COUNT(*) FROM related_profile_evidence WHERE media_id = $id OR face_id IN (SELECT face_id FROM face_detections WHERE media_id = $id);"),
            }
            : new (string Name, string Sql)[]
            {
                ("identities", "SELECT COUNT(*) FROM identities WHERE profile_id = $id;"),
                ("identity_samples", "SELECT COUNT(*) FROM identity_samples WHERE identity_id IN (SELECT identity_id FROM identities WHERE profile_id = $id);"),
                ("import_units", "SELECT COUNT(*) FROM import_units WHERE destination_profile_id = $id;"),
                ("import_assignment_cluster_candidates", "SELECT COUNT(*) FROM import_assignment_clusters WHERE candidate_profile_id = $id;"),
                ("import_assignment_cluster_decisions", "SELECT COUNT(*) FROM import_assignment_clusters WHERE decided_profile_id = $id;"),
                ("profile_appearance", "SELECT COUNT(*) FROM profile_appearance WHERE profile_id = $id;"),
                ("profile_media", "SELECT COUNT(*) FROM profile_media WHERE profile_id = $id;"),
                ("profile_tags", "SELECT COUNT(*) FROM profile_tags WHERE profile_id = $id;"),
                ("related_profile_evidence", "SELECT COUNT(*) FROM related_profile_evidence WHERE profile_id_low = $id OR profile_id_high = $id;"),
                ("related_profile_summary", "SELECT COUNT(*) FROM related_profile_summary WHERE profile_id_low = $id OR profile_id_high = $id;"),
            };

        var result = new List<PurgeAffectedData>(queries.Length);
        foreach (var query in queries)
        {
            result.Add(new PurgeAffectedData(
                query.Name,
                await ReadCountAsync(connection, transaction, query.Sql, entityId, cancellationToken)
                    .ConfigureAwait(false)));
        }
        return result;
    }

    private static async Task DeleteMediaAuthorityAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var id = DbGuid.Format(assetId);
        await ExecuteAsync(transaction,
            "DELETE FROM related_profile_summary WHERE (profile_id_low, profile_id_high) IN (SELECT profile_id_low, profile_id_high FROM related_profile_evidence WHERE media_id = $id OR face_id IN (SELECT face_id FROM face_detections WHERE media_id = $id));",
            cancellationToken, ("$id", id)).ConfigureAwait(false);
        await ExecuteAsync(transaction,
            "DELETE FROM related_profile_evidence WHERE media_id = $id OR face_id IN (SELECT face_id FROM face_detections WHERE media_id = $id);",
            cancellationToken, ("$id", id)).ConfigureAwait(false);
        await ExecuteAsync(transaction,
            "DELETE FROM identity_samples WHERE face_id IN (SELECT face_id FROM face_detections WHERE media_id = $id);",
            cancellationToken, ("$id", id)).ConfigureAwait(false);
        await ExecuteAsync(transaction, "DELETE FROM profile_media WHERE media_id = $id;", cancellationToken, ("$id", id)).ConfigureAwait(false);
        await ExecuteAsync(transaction,
            "UPDATE import_items SET candidate_media_id = CASE WHEN candidate_media_id = $id THEN NULL ELSE candidate_media_id END, reused_media_id = CASE WHEN reused_media_id = $id THEN NULL ELSE reused_media_id END WHERE candidate_media_id = $id OR reused_media_id = $id;",
            cancellationToken, ("$id", id)).ConfigureAwait(false);
        await ExecuteAsync(
            transaction,
            "DELETE FROM media_capability_readiness WHERE media_id = $id;",
            cancellationToken,
            ("$id", id)).ConfigureAwait(false);
        await ExecuteAsync(transaction, "DELETE FROM media WHERE media_id = $id;", cancellationToken, ("$id", id)).ConfigureAwait(false);
    }

    private static async Task DeleteProfileAuthorityAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var id = DbGuid.Format(profileId);
        await ExecuteAsync(transaction, "DELETE FROM related_profile_summary WHERE profile_id_low = $id OR profile_id_high = $id;", cancellationToken, ("$id", id)).ConfigureAwait(false);
        await ExecuteAsync(transaction, "DELETE FROM related_profile_evidence WHERE profile_id_low = $id OR profile_id_high = $id;", cancellationToken, ("$id", id)).ConfigureAwait(false);
        await ExecuteAsync(transaction,
            "DELETE FROM identity_samples WHERE identity_id IN (SELECT identity_id FROM identities WHERE profile_id = $id);",
            cancellationToken, ("$id", id)).ConfigureAwait(false);
        await ExecuteAsync(transaction,
            "UPDATE face_detections SET suggested_identity_id = CASE WHEN suggested_identity_id IN (SELECT identity_id FROM identities WHERE profile_id = $id) THEN NULL ELSE suggested_identity_id END, confirmed_identity_id = CASE WHEN confirmed_identity_id IN (SELECT identity_id FROM identities WHERE profile_id = $id) THEN NULL ELSE confirmed_identity_id END, decision_state = CASE WHEN confirmed_identity_id IN (SELECT identity_id FROM identities WHERE profile_id = $id) THEN 'UNKNOWN' ELSE decision_state END WHERE suggested_identity_id IN (SELECT identity_id FROM identities WHERE profile_id = $id) OR confirmed_identity_id IN (SELECT identity_id FROM identities WHERE profile_id = $id);",
            cancellationToken, ("$id", id)).ConfigureAwait(false);
        await ExecuteAsync(transaction, "DELETE FROM profile_media WHERE profile_id = $id;", cancellationToken, ("$id", id)).ConfigureAwait(false);
        await ExecuteAsync(transaction, "UPDATE import_units SET destination_profile_id = NULL WHERE destination_profile_id = $id;", cancellationToken, ("$id", id)).ConfigureAwait(false);
        await ExecuteAsync(transaction, "DELETE FROM identities WHERE profile_id = $id;", cancellationToken, ("$id", id)).ConfigureAwait(false);
        await ExecuteAsync(transaction, "DELETE FROM profiles WHERE profile_id = $id;", cancellationToken, ("$id", id)).ConfigureAwait(false);
    }

    private async Task MarkRetryRequiredAsync(Guid purgePlanId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await TrashCoordinator.ReadTrashEntryAsync(connection, purgePlanId, cancellationToken).ConfigureAwait(false);
        if (row is null || row.State == PurgePlanState.RetryRequired)
        {
            return;
        }
        await _catalog.TrashWrites.UpdateEntryStateAsync(
                purgePlanId, PurgePlanState.RetryRequired, null, null, row.RowVersion, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<long> CheckpointProgressAsync(
        Guid purgePlanId,
        PurgePlan plan,
        long expectedRowVersion,
        CancellationToken cancellationToken)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var command = transaction.CreateCommand(
            "UPDATE trash_entries SET plan_json = $plan, updated_at_ms = $now, row_version = row_version + 1 WHERE trash_entry_id = $id AND state = 'PURGE_EXECUTING' AND row_version = $version;");
        command.Parameters.AddWithValue("$plan", plan.ToJson());
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("$id", DbGuid.Format(purgePlanId));
        command.Parameters.AddWithValue("$version", expectedRowVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CatalogConcurrencyConflictException(
                $"Purge plan {purgePlanId:D} changed while deletion progress was checkpointed.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expectedRowVersion + 1;
    }

    private static async Task MarkPurgedAsync(
        CatalogTransaction transaction,
        Guid entryId,
        long nowMs,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            transaction,
            "UPDATE trash_entries SET state = 'PURGED', completed_at_ms = $now, updated_at_ms = $now, row_version = row_version + 1 WHERE trash_entry_id = $id;",
            cancellationToken,
            ("$now", nowMs),
            ("$id", DbGuid.Format(entryId))).ConfigureAwait(false);

    private static async Task<TrashEntryRow?> ReadTrashEntryAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT entity_type, entity_id, state, recovery_relative_path, plan_json, row_version FROM trash_entries WHERE trash_entry_id = $id;");
        command.Parameters.AddWithValue("$id", DbGuid.Format(entryId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new TrashEntryRow(entryId, reader.GetString(0), DbGuid.Parse(reader.GetString(1)), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetInt64(5))
            : null;
    }

    private static async Task<TrashEntryRow?> ReadActivePurgeEntryAsync(
        SqliteConnection connection,
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT trash_entry_id, entity_type, entity_id, state, recovery_relative_path, plan_json, row_version FROM trash_entries WHERE entity_type = $type AND entity_id = $id AND state IN ('PURGE_PENDING','PURGE_EXECUTING','PURGE_RETRY_REQUIRED') ORDER BY created_at_ms DESC LIMIT 1;";
        command.Parameters.AddWithValue("$type", OperationEntityType(entityType));
        command.Parameters.AddWithValue("$id", DbGuid.Format(entityId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new TrashEntryRow(DbGuid.Parse(reader.GetString(0)), reader.GetString(1), DbGuid.Parse(reader.GetString(2)),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetInt64(6))
            : null;
    }

    private static async Task<long> ReadCountAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        string sql,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("$id", DbGuid.Format(entityId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<int> ExecuteAsync(
        CatalogTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = transaction.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        string sql)
    {
        if (transaction is not null)
        {
            return transaction.CreateCommand(sql);
        }
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static string OperationEntityType(string entityType) => entityType switch
    {
        PurgeEntityType.Media => PurgeOperationEntityType.Media,
        PurgeEntityType.Profile => PurgeOperationEntityType.Profile,
        _ => throw new ArgumentOutOfRangeException(nameof(entityType)),
    };

    private static string PhysicalFailureMessage(StorageOperationStatus status) => status switch
    {
        StorageOperationStatus.SourceChanged or StorageOperationStatus.VerificationFailed =>
            "Recovery material changed after authorization. Nothing else was deleted.",
        StorageOperationStatus.UnexpectedTarget =>
            "Unplanned recovery material is present. Purge stopped without deleting it.",
        StorageOperationStatus.PathOutsideVault =>
            "A recovery target falls outside the authorized Trash boundary.",
        _ => "The exact recovery material could not be deleted. The Purge remains available for retry.",
    };

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }
}

public sealed record PurgeOutcome(
    Guid PurgePlanId,
    string EntityType,
    Guid EntityId,
    int DeletedPhysicalTargetCount);
