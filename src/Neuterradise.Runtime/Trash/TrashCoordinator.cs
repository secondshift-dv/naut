using System.IO;
using Microsoft.Data.Sqlite;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.RelatedProfiles;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Trash;

public sealed class TrashCoordinator
{
    private readonly CatalogDb _catalog;
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly ManagedMoveExecutor _moveExecutor;
    private readonly MediaOperations _mediaOperations;
    private readonly ManagedNamePolicy _namePolicy;
    private readonly ProfileManifestWriter _manifestWriter;
    private readonly TimeProvider _timeProvider;

    public TrashCoordinator(
        CatalogDb catalog,
        ManagedMoveExecutor moveExecutor,
        MediaOperations mediaOperations,
        TimeProvider? timeProvider = null,
        ManagedNamePolicy? namePolicy = null,
        ProfileManifestWriter? manifestWriter = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _moveExecutor = moveExecutor ?? throw new ArgumentNullException(nameof(moveExecutor));
        _mediaOperations = mediaOperations ?? throw new ArgumentNullException(nameof(mediaOperations));
        _namePolicy = namePolicy ?? new ManagedNamePolicy();
        _manifestWriter = manifestWriter ?? new ProfileManifestWriter(catalog.Paths);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OperationResult<MediaTrashPlan>> PrepareMediaTrashAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));

        MediaTrashPlan plan;

        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var asset = await ReadMediaTrashStateAsync(connection, transaction: null, assetId, cancellationToken)
                .ConfigureAwait(false);
            if (asset is null)
            {
                return OperationResult<MediaTrashPlan>.NotFound(
                    OperationErrorCode.MediaNotFound,
                    "That media item no longer exists.");
            }

            var gate = ValidateMediaIsTrashable(asset);
            if (gate is not null)
            {
                return gate;
            }

            var existing = await ReadActiveTrashEntryAsync(
                    connection, TrashEntityType.Media, assetId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {

                var existingPlan = MediaTrashPlan.FromJson(existing.PlanJson);
                return existingPlan is null
                    ? OperationResult<MediaTrashPlan>.NeedsAttention(
                        OperationErrorCode.TrashPlanUnreadable,
                        "An earlier Trash record for this media item cannot be read and needs attention.")
                    : OperationResult<MediaTrashPlan>.Success(existingPlan);
            }

            var appearanceReferences = await ReadAffectedAppearanceReferencesAsync(
                    connection, transaction: null, assetId, cancellationToken).ConfigureAwait(false);

            string extension;
            try
            {
                extension = _namePolicy.NormalizeExtension(Path.GetExtension(asset.CurrentManagedFileName!));
            }
            catch (ArgumentException)
            {
                return OperationResult<MediaTrashPlan>.NeedsAttention(
                    OperationErrorCode.CurrentPathAmbiguous,
                    "This media item's stored filename cannot be interpreted and needs repair before it can be moved to Trash.");
            }

            IReadOnlyList<MediaTrashComponentPlan>? packageComponents = null;
            if (asset.MediaType == MediaType.Model)
            {
                var components = await _catalog.MediaWrites.GetMediaComponentsAsync(assetId, cancellationToken)
                    .ConfigureAwait(false);
                if (components.Count > 1 || (components.Count == 1 && components.Any(c => c.ComponentRole == ComponentRole.Dependency)))
                {
                    packageComponents = components.Select(c => new MediaTrashComponentPlan(
                        c.ComponentRelativePath,
                        CombineRelative(asset.CurrentManagedRelativePath!, c.ComponentRelativePath),
                        $"_trash/media/{assetId:D}/{c.ComponentRelativePath}",
                        c.ByteLength,
                        c.Sha256,
                        c.ComponentRole)).ToList();
                }
            }

            plan = new MediaTrashPlan(
                Guid.NewGuid(),
                assetId,
                asset.OwnerProfileId!.Value,
                asset.CurrentManagedRelativePath!,
                asset.CurrentManagedFileName!,
                asset.StorageToken!,
                asset.ByteLength!.Value,
                asset.Sha256!,
                asset.MediaType,
                BuildRecoveryRelativePath(assetId, extension),
                appearanceReferences,
                Guid.NewGuid(),
                _timeProvider.GetUtcNow(),
                asset.RowVersion,
                packageComponents)
            {
                MediaAssets = await ReadMediaAssetPlansAsync(connection, assetId, cancellationToken)
                    .ConfigureAwait(false),
            };
        }

        await _catalog.TrashWrites.PersistEntryAsync(
                new TrashEntryPersistence(
                    plan.TrashEntryId,
                    TrashEntityType.Media,
                    plan.MediaId,
                    TrashEntryState.Pending,
                    plan.RecoveryRelativePath,
                    plan.ToJson(),
                    plan.PreparedAtUtc,
                    plan.PreparedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);

        return OperationResult<MediaTrashPlan>.Success(plan, plan.OperationId);
    }

    public Task<OperationResult<MediaTrashOutcome>> ExecuteMediaTrashAsync(
        MediaTrashPlan plan,
        CancellationToken cancellationToken = default) =>
        ExecuteMediaTrashCoreAsync(plan, rollbackImportUnitId: null, cancellationToken);

    public Task<OperationResult<MediaTrashOutcome>> ExecuteMediaTrashAsync(
        MediaTrashPlan plan,
        Guid rollbackImportUnitId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(rollbackImportUnitId, nameof(rollbackImportUnitId));
        return ExecuteMediaTrashCoreAsync(plan, rollbackImportUnitId, cancellationToken);
    }

    private async Task<OperationResult<MediaTrashOutcome>> ExecuteMediaTrashCoreAsync(
        MediaTrashPlan plan,
        Guid? rollbackImportUnitId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var entry = await ReadTrashEntryAsync(connection, plan.TrashEntryId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return OperationResult<MediaTrashOutcome>.NotFound(
                    OperationErrorCode.TrashEntryNotFound,
                    "That Trash record no longer exists.");
            }

            var persistedPlan = MediaTrashPlan.FromJson(entry.PlanJson);
            if (entry.EntityType != TrashEntityType.Media
                || persistedPlan is null
                || persistedPlan.TrashEntryId != entry.TrashEntryId
                || persistedPlan.MediaId != entry.EntityId)
            {
                return OperationResult<MediaTrashOutcome>.NeedsAttention(
                    OperationErrorCode.TrashPlanUnreadable,
                    "The durable Trash plan is missing or does not match this media item.");
            }

            plan = persistedPlan;

            if (entry.State == TrashEntryState.Restored)
            {
                return OperationResult<MediaTrashOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "That Trash record was already reversed. Prepare the move to Trash again.");
            }

            var asset = await ReadMediaTrashStateAsync(connection, transaction: null, plan.MediaId, cancellationToken)
                .ConfigureAwait(false);
            if (asset is null)
            {
                return OperationResult<MediaTrashOutcome>.NotFound(
                    OperationErrorCode.MediaNotFound,
                    "That media item no longer exists.");
            }

            if (asset.State == MediaState.Trashed && entry.State == TrashEntryState.InTrash)
            {
                var priorOutcome = OperationResult<MediaTrashOutcome>.Success(
                    new MediaTrashOutcome(plan.TrashEntryId, plan.MediaId, plan.RecoveryRelativePath, asset.RowVersion),
                    plan.OperationId);
                return await RefreshAffectedManifestsAfterMediaTrashAsync(
                        plan, priorOutcome, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (asset.State != MediaState.Active || asset.RowVersion != plan.ExpectedMediaRowVersion)
            {
                return OperationResult<MediaTrashOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "This media item changed since the move to Trash was prepared. Reload it and try again.");
            }

            if (rollbackImportUnitId is { } rollbackUnitId
                && !await IsCancellationRollbackMediaEligibleAsync(
                        connection,
                        transaction: null,
                        rollbackUnitId,
                        plan.MediaId,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return OperationResult<MediaTrashOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "Another import or published Profile relation now requires this media item; cancellation rollback preserved it.");
            }

            if (entry.State == TrashEntryState.Pending)
            {
                await MarkEntryStateAsync(
                        plan.TrashEntryId,
                        TrashEntryState.Executing,
                        plan.RecoveryRelativePath,
                        completedAtUtc: null,
                        entry.RowVersion,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (plan.PackageComponents is { Count: > 0 } packageComponents)
        {
            foreach (var comp in packageComponents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var compMove = await _moveExecutor.ExecuteTrashMoveAsync(
                    new ManagedTrashMoveRequest(
                        plan.MediaId,
                        RelativeDirectoryOf(comp.SourceRelativePath),
                        FileNameOf(comp.SourceRelativePath),
                        comp.RecoveryRelativePath,
                        comp.ByteLength,
                        comp.Sha256),
                    cancellationToken).ConfigureAwait(false);
                if (!compMove.IsSuccess)
                {
                    return MapTrashMoveFailure<MediaTrashOutcome>(compMove);
                }
            }

            try
            {
                var pkgSourceDir = _catalog.Paths.ResolveVaultRelativePath(plan.CurrentManagedRelativePath);
                if (Directory.Exists(pkgSourceDir) && !Directory.EnumerateFileSystemEntries(pkgSourceDir).Any())
                {
                    Directory.Delete(pkgSourceDir, recursive: false);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        else
        {
            var move = await _moveExecutor.ExecuteTrashMoveAsync(
                    new ManagedTrashMoveRequest(
                        plan.MediaId,
                        plan.CurrentManagedRelativePath,
                        plan.CurrentManagedFileName,
                        plan.RecoveryRelativePath,
                        plan.ByteLength,
                        plan.Sha256),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!move.IsSuccess)
            {
                return MapTrashMoveFailure<MediaTrashOutcome>(move);
            }
        }

        var mediaAssetPlans = new List<DurableMediaAssetTrashPlan>(plan.MediaAssets.Count);
        foreach (var mediaAsset in plan.MediaAssets)
        {
            if (!mediaAsset.HasValidPaths(plan.MediaId,plan.MediaStorageToken,plan.MediaType))
                return OperationResult<MediaTrashOutcome>.NeedsAttention(
                    OperationErrorCode.TrashPlanUnreadable,
                    "The MediaAsset Trash plan is outside its exact recovery boundary.");
            if (mediaAsset.RepairOnRestore)
            {
                mediaAssetPlans.Add(mediaAsset);
                continue;
            }
            var move = await _moveExecutor.ExecuteDurableMediaAssetMoveAsync(
                mediaAsset.SourceRelativePath, VaultPathArea.MediaAssets,
                mediaAsset.RecoveryRelativePath, VaultPathArea.TrashMedias,
                mediaAsset.ByteLength, mediaAsset.Sha256, cancellationToken).ConfigureAwait(false);
            if (move.Status == StorageOperationStatus.SourceMissing)
            {
                mediaAssetPlans.Add(mediaAsset with { RepairOnRestore = true });
                continue;
            }
            if (!move.IsSuccess)
                return MapTrashMoveFailure<MediaTrashOutcome>(move);
            mediaAssetPlans.Add(mediaAsset);
        }

        plan = plan with { MediaAssets = mediaAssetPlans };

        var commit = await CommitMediaTrashTransitionAsync(
                plan,
                rollbackImportUnitId,
                cancellationToken)
            .ConfigureAwait(false);
        var outcome = commit.Outcome;

        if (outcome.IsSuccess)
        {
            return await RefreshAffectedManifestsAfterMediaTrashAsync(
                    commit.Plan, outcome, cancellationToken)
                .ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<OperationResult<MediaTrashOutcome>> RefreshAffectedManifestsAfterMediaTrashAsync(
        MediaTrashPlan plan,
        OperationResult<MediaTrashOutcome> committed,
        CancellationToken cancellationToken)
    {
        await new RelatedEvidenceProjector(_catalog, _timeProvider)
            .RefreshRelatedProjectionsForMediaAsync(plan.MediaId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var profileId in plan.AffectedAppearanceReferences
                     .Select(reference => reference.ProfileId)
                     .Distinct()
                     .Order())
        {
            var profile = await _catalog.ProfileReads.GetDetailAsync(profileId, cancellationToken)
                .ConfigureAwait(false);
            if (profile is null || profile.TrashedAtUtc.HasValue)
            {
                continue;
            }

            var refresh = await _manifestWriter.RegenerateManifestAsync(
                    _catalog, profileId, cancellationToken)
                .ConfigureAwait(false);
            if (!refresh.IsSuccess)
            {
                return OperationResult<MediaTrashOutcome>.NeedsAttention(
                    OperationErrorCode.ProfileManifestWriteFailed,
                    "The media item is safely in Trash, but an affected manifest.json could not be refreshed. Retry the Trash operation to repair it.",
                    plan.OperationId);
            }
        }

        return committed;
    }

    public async Task<OperationResult<ProfileTrashPlan>> PrepareProfileTrashAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));

        ProfileTrashPlan plan;

        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var profile = await ReadProfileTrashStateAsync(connection, transaction: null, profileId, cancellationToken)
                .ConfigureAwait(false);
            if (profile is null)
            {
                return OperationResult<ProfileTrashPlan>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "That Profile no longer exists.");
            }

            if (profile.IsTrashed)
            {
                return OperationResult<ProfileTrashPlan>.Conflict(
                    OperationErrorCode.ProfileAlreadyTrashed,
                    "That Profile is already in Trash.");
            }

            var existing = await ReadActiveTrashEntryAsync(
                    connection, TrashEntityType.Profile, profileId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var existingPlan = ProfileTrashPlan.FromJson(existing.PlanJson);
                return existingPlan is null
                    ? OperationResult<ProfileTrashPlan>.NeedsAttention(
                        OperationErrorCode.TrashPlanUnreadable,
                        "An earlier Trash record for this Profile cannot be read and needs attention.")
                    : OperationResult<ProfileTrashPlan>.Success(existingPlan);
            }

            var owned = await ReadOwnedActiveMediasAsync(connection, transaction: null, profileId, cancellationToken)
                .ConfigureAwait(false);

            plan = new ProfileTrashPlan(
                Guid.NewGuid(),
                profileId,
                profile.RowVersion,
                owned,
                Guid.NewGuid(),
                _timeProvider.GetUtcNow())
            {
                ExpectedCoverMediaId = profile.CoverMediaId,
                ExpectedBannerMediaId = profile.BannerMediaId,
                CurrentManagedRelativePath = profile.CurrentManagedRelativePath,
            };
        }

        await _catalog.TrashWrites.PersistEntryAsync(
                new TrashEntryPersistence(
                    plan.TrashEntryId,
                    TrashEntityType.Profile,
                    plan.ProfileId,
                    TrashEntryState.Pending,
                    RecoveryRelativePath: null,
                    plan.ToJson(),
                    plan.PreparedAtUtc,
                    plan.PreparedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);

        return OperationResult<ProfileTrashPlan>.Success(plan, plan.OperationId);
    }

    public async Task<OperationResult<ProfileTrashOutcome>> CommitProfileTrashAsync(
        ProfileTrashPlan plan,
        IReadOnlyList<ProfileOwnedMediaDisposition> dispositions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispositions);

        ProfileTrashValidation validation;
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var entry = await ReadTrashEntryAsync(connection, plan.TrashEntryId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return OperationResult<ProfileTrashOutcome>.NotFound(
                    OperationErrorCode.TrashEntryNotFound,
                    "That Trash record no longer exists.");
            }

            var persistedPlan = ProfileTrashPlan.FromJson(entry.PlanJson);
            if (entry.EntityType != TrashEntityType.Profile
                || persistedPlan is null
                || persistedPlan.TrashEntryId != entry.TrashEntryId
                || persistedPlan.ProfileId != entry.EntityId)
            {
                return OperationResult<ProfileTrashOutcome>.NeedsAttention(
                    OperationErrorCode.TrashPlanUnreadable,
                    "The durable Trash plan is missing or does not match this Profile.");
            }

            plan = persistedPlan;

            var profile = await ReadProfileTrashStateAsync(connection, transaction: null, plan.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (profile is null)
            {
                return OperationResult<ProfileTrashOutcome>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "That Profile no longer exists.");
            }

            if (profile.IsTrashed && entry.State == TrashEntryState.InTrash)
            {
                return OperationResult<ProfileTrashOutcome>.Success(
                    new ProfileTrashOutcome(plan.TrashEntryId, plan.ProfileId, profile.RowVersion, 0, 0),
                    plan.OperationId);
            }

            var owned = await ReadOwnedActiveMediasAsync(connection, transaction: null, plan.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            var plannedMediaIds = plan.OwnedActiveMedias.Select(asset => asset.MediaId).ToHashSet();
            if (owned.Any(asset => !plannedMediaIds.Contains(asset.MediaId)))
            {
                return OperationResult<ProfileTrashOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "This Profile acquired media after the move to Trash was prepared. Reload it and try again.");
            }

            var dispositionGate = await ValidateDispositionsAsync(
                    connection, plan, dispositions, cancellationToken).ConfigureAwait(false);
            if (dispositionGate.Failure is not null)
            {
                return dispositionGate.Failure;
            }

            if (!await ProfileAuthorityMatchesPlanAsync(
                    connection, profile, plan, dispositions, cancellationToken).ConfigureAwait(false))
            {
                return OperationResult<ProfileTrashOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "This Profile changed since the move to Trash was prepared. Reload it and try again.");
            }

            validation = dispositionGate;
        }

        plan = await EnsureProfileExecutionCheckpointAsync(plan, dispositions, cancellationToken)
            .ConfigureAwait(false);

        var trashedMedias = dispositions.Count(
            disposition => disposition.Kind == ProfileOwnedMediaDispositionKind.TrashMedia);
        var reassignedMedias = dispositions.Count - trashedMedias;

        foreach (var disposition in validation.Ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (disposition.Kind == ProfileOwnedMediaDispositionKind.TrashMedia)
            {
                var prepared = await PrepareMediaTrashAsync(disposition.MediaId, cancellationToken)
                    .ConfigureAwait(false);
                if (!prepared.IsSuccess || prepared.Value is null)
                {
                    return Propagate<MediaTrashPlan, ProfileTrashOutcome>(prepared);
                }

                var executed = await ExecuteMediaTrashAsync(prepared.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (!executed.IsSuccess)
                {
                    return Propagate<MediaTrashOutcome, ProfileTrashOutcome>(executed);
                }

                continue;
            }

            var expectedRowVersion = plan.OwnedActiveMedias
                .First(asset => asset.MediaId == disposition.MediaId)
                .RowVersion;
            var transfer = await _mediaOperations.ChangePrimaryProfileAsync(
                    new ChangePrimaryProfileRequest(
                        disposition.MediaId,
                        disposition.NewOwnerProfileId!.Value,
                        expectedRowVersion),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!transfer.IsSuccess)
            {
                return Propagate<ChangePrimaryProfileOutcome, ProfileTrashOutcome>(transfer);
            }

        }

        var profileRecovery = await MoveProfileManifestToRecoveryAsync(plan.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (!profileRecovery.IsSuccess)
        {
            return profileRecovery.Failure!;
        }

        plan = await PersistProfileManifestCheckpointAsync(
                plan,
                profileRecovery.RecoveryRelativePath!,
                cancellationToken)
            .ConfigureAwait(false);

        var committed = await CommitProfileTrashMarkerAsync(
                plan,
                profileRecovery.RecoveryRelativePath!,
                trashedMedias,
                reassignedMedias,
                cancellationToken)
            .ConfigureAwait(false);

        if (committed.IsSuccess && !string.IsNullOrWhiteSpace(plan.CurrentManagedRelativePath))
        {
            TryRemoveEmptyProfileScaffolding(plan.CurrentManagedRelativePath);
        }

        return committed;
    }

    private async Task<ProfileTrashPlan> EnsureProfileExecutionCheckpointAsync(
        ProfileTrashPlan plan,
        IReadOnlyList<ProfileOwnedMediaDisposition> dispositions,
        CancellationToken cancellationToken)
    {
        var ordered = dispositions.OrderBy(item => item.MediaId).ToArray();
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var entry = await ReadTrashEntryAsync(connection, transaction, plan.TrashEntryId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CatalogInvariantException($"TrashEntry {plan.TrashEntryId:D} disappeared.");

        var currentPlan = ProfileTrashPlan.FromJson(entry.PlanJson)
            ?? throw new CatalogInvariantException($"Profile Trash plan {plan.TrashEntryId:D} became unreadable.");
        if (entry.State == TrashEntryState.Executing)
        {
            if (currentPlan.SelectedDispositions.Count > 0
                && !currentPlan.SelectedDispositions.SequenceEqual(ordered))
            {
                throw new CatalogConcurrencyConflictException(
                    $"Profile Trash plan {plan.TrashEntryId:D} already has different durable dispositions.");
            }

            if (currentPlan.SelectedDispositions.Count > 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return currentPlan;
            }
        }
        else if (entry.State != TrashEntryState.Pending)
        {
            throw new CatalogConcurrencyConflictException(
                $"Profile Trash plan {plan.TrashEntryId:D} is not pending or executing.");
        }

        var checkpointed = currentPlan with { SelectedDispositions = ordered };
        await CheckpointTrashEntryAsync(
            transaction,
            plan.TrashEntryId,
            TrashEntryState.Executing,
            entry.RecoveryRelativePath,
            completedAtMs: null,
            entry.RowVersion,
            DbTime.Format(_timeProvider.GetUtcNow()),
            cancellationToken,
            checkpointed.ToJson()).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return checkpointed;
    }

    private async Task<ProfileTrashPlan> PersistProfileManifestCheckpointAsync(
        ProfileTrashPlan plan,
        string recoveryRelativePath,
        CancellationToken cancellationToken)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var entry = await ReadTrashEntryAsync(connection, transaction, plan.TrashEntryId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CatalogInvariantException($"TrashEntry {plan.TrashEntryId:D} disappeared.");
        var currentPlan = ProfileTrashPlan.FromJson(entry.PlanJson)
            ?? throw new CatalogInvariantException($"Profile Trash plan {plan.TrashEntryId:D} became unreadable.");

        if (entry.State != TrashEntryState.Executing)
        {
            throw new CatalogConcurrencyConflictException(
                $"Profile Trash plan {plan.TrashEntryId:D} is not executing.");
        }

        if (currentPlan.TrashCheckpoint?.RecoveryRelativePath == recoveryRelativePath)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return currentPlan;
        }

        var checkpointed = currentPlan with
        {
            TrashCheckpoint = new ProfileTrashCheckpoint(recoveryRelativePath),
        };
        await CheckpointTrashEntryAsync(
            transaction,
            plan.TrashEntryId,
            TrashEntryState.Executing,
            recoveryRelativePath,
            completedAtMs: null,
            entry.RowVersion,
            DbTime.Format(_timeProvider.GetUtcNow()),
            cancellationToken,
            checkpointed.ToJson()).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return checkpointed;
    }

    private static async Task<bool> IsCancellationRollbackMediaEligibleAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid rollbackImportUnitId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        const string commandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM import_units self
                JOIN import_items mine ON mine.import_unit_id = self.import_unit_id
                WHERE self.import_unit_id = $unitId
                  AND self.state = 'CANCELLED'
                  AND mine.candidate_media_id = $assetId
                  AND NOT EXISTS (
                      SELECT 1
                      FROM import_items other
                      JOIN import_units consumer ON consumer.import_unit_id = other.import_unit_id
                      WHERE other.import_unit_id <> $unitId
                        AND (other.candidate_media_id = $assetId OR other.reused_media_id = $assetId)
                        AND consumer.state NOT IN (
                            'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                            'CANCELLED','FAILED_TERMINAL'
                        )
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM import_media_interests interest
                      JOIN import_units consumer ON consumer.import_unit_id = interest.import_unit_id
                      WHERE interest.import_unit_id <> $unitId
                        AND interest.media_id = $assetId
                        AND consumer.state NOT IN (
                            'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                            'CANCELLED','FAILED_TERMINAL'
                        )
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM profile_media relation
                      WHERE relation.media_id = $assetId
                        AND (
                            relation.publication_import_unit_id IS NULL
                            OR relation.publication_import_unit_id <> $unitId
                        )
                  )
            );
            """;
        await using var command = transaction is null
            ? connection.CreateCommand()
            : transaction.CreateCommand(commandText);
        if (transaction is null)
        {
            command.CommandText = commandText;
        }
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(rollbackImportUnitId));
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(
            result,
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private async Task<MediaTrashCommitResult> CommitMediaTrashTransitionAsync(
        MediaTrashPlan plan,
        Guid? rollbackImportUnitId,
        CancellationToken cancellationToken)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var entry = await ReadTrashEntryAsync(connection, transaction, plan.TrashEntryId, cancellationToken)
            .ConfigureAwait(false);
        var asset = await ReadMediaTrashStateAsync(connection, transaction, plan.MediaId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null || asset is null)
        {
            return new MediaTrashCommitResult(
                OperationResult<MediaTrashOutcome>.NotFound(
                    OperationErrorCode.TrashEntryNotFound,
                    "That Trash record no longer exists."),
                plan);
        }

        if (asset.State == MediaState.Trashed && entry.State == TrashEntryState.InTrash)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new MediaTrashCommitResult(
                OperationResult<MediaTrashOutcome>.Success(
                    new MediaTrashOutcome(plan.TrashEntryId, plan.MediaId, plan.RecoveryRelativePath, asset.RowVersion),
                    plan.OperationId),
                plan);
        }

        if (asset.State != MediaState.Active || asset.RowVersion != plan.ExpectedMediaRowVersion)
        {
            return new MediaTrashCommitResult(
                OperationResult<MediaTrashOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "This media item changed while it was being moved to Trash."),
                plan);
        }

        if (rollbackImportUnitId is { } rollbackUnitId
            && !await IsCancellationRollbackMediaEligibleAsync(
                    connection,
                    transaction,
                    rollbackUnitId,
                    plan.MediaId,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return new MediaTrashCommitResult(
                OperationResult<MediaTrashOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "Cancellation rollback authority changed before the Trash commit."),
                plan);
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());

        var relationSnapshots = await ReadMediaProfileRelationSnapshotsAsync(
            connection, transaction, plan.MediaId, cancellationToken).ConfigureAwait(false);
        var affected = await ReadAffectedAppearanceReferencesAsync(
            connection, transaction, plan.MediaId, cancellationToken).ConfigureAwait(false);
        var manualCovers = await ReadSelectedMediaReferencesAsync(
            connection, plan.MediaId, cancellationToken).ConfigureAwait(false);
        var committedPlan = plan with
        {
            RelationSnapshots = relationSnapshots,
            AffectedAppearanceReferences = affected,
            SelectedMediaReferences = manualCovers,
        };

        await ExecuteAsync(transaction, """
            UPDATE profile_appearance
            SET cover_media_asset_id = CASE
                    WHEN cover_media_asset_id IN
                        (SELECT media_asset_id FROM media_assets WHERE media_id = $assetId)
                    THEN NULL ELSE cover_media_asset_id END,
                banner_media_asset_id = CASE
                    WHEN banner_media_asset_id IN
                        (SELECT media_asset_id FROM media_assets WHERE media_id = $assetId)
                    THEN NULL ELSE banner_media_asset_id END,
                figure_media_id = CASE
                    WHEN figure_media_id = $assetId
                    THEN NULL ELSE figure_media_id END,
                updated_at_ms = $now, row_version = row_version + 1
            WHERE cover_media_asset_id IN
                    (SELECT media_asset_id FROM media_assets WHERE media_id = $assetId)
               OR banner_media_asset_id IN
                    (SELECT media_asset_id FROM media_assets WHERE media_id = $assetId)
               OR figure_media_id = $assetId;
            """, cancellationToken,
            ("$assetId", DbGuid.Format(plan.MediaId)), ("$now", now)).ConfigureAwait(false);

        await ExecuteAsync(
            transaction,
            "DELETE FROM profile_media WHERE media_id = $assetId;",
            cancellationToken,
            ("$assetId", DbGuid.Format(plan.MediaId))).ConfigureAwait(false);
        foreach (var reference in affected)
        {
            await ExecuteAsync(
                transaction,
                """
                UPDATE profiles
                SET cover_media_id = CASE WHEN cover_media_id = $assetId THEN NULL ELSE cover_media_id END,
                    updated_at_ms = $now,
                    row_version = row_version + CASE WHEN cover_media_id = $assetId THEN 1 ELSE 0 END
                WHERE profile_id = $profileId;
                """,
                cancellationToken,
                ("$assetId", DbGuid.Format(plan.MediaId)),
                ("$now", now),
                ("$profileId", DbGuid.Format(reference.ProfileId))).ConfigureAwait(false);
        }

        var newMediaRowVersion = asset.RowVersion + 1;
        var updatedMedias = await ExecuteAsync(
            transaction,
            """
            UPDATE media
            SET state = 'TRASHED',
                trashed_at_ms = $now,
                current_managed_relative_path = NULL,
                current_managed_file_name = NULL,
                target_managed_relative_path = NULL,
                target_managed_file_name = NULL,
                path_state = 'NONE',
                reconciliation_operation_id = NULL,
                row_version = $newRowVersion
            WHERE media_id = $assetId AND row_version = $expectedRowVersion;
            """,
            cancellationToken,
            ("$now", now),
            ("$newRowVersion", newMediaRowVersion),
            ("$assetId", DbGuid.Format(plan.MediaId)),
            ("$expectedRowVersion", plan.ExpectedMediaRowVersion)).ConfigureAwait(false);
        if (updatedMedias != 1)
        {
            throw new CatalogInvariantException(
                $"Media {plan.MediaId:D} changed while its move to Trash was being committed.");
        }

        await CheckpointTrashEntryAsync(
            transaction,
            plan.TrashEntryId,
            TrashEntryState.InTrash,
            plan.RecoveryRelativePath,
            completedAtMs: null,
            entry.RowVersion,
            now,
            cancellationToken,
            committedPlan.ToJson()).ConfigureAwait(false);

        await AppendActivityAsync(
            transaction,
            ActivityEventType.MediaMovedToTrash,
            plan.OwnerProfileId,
            plan.MediaId,
            plan.OperationId,
            now,
            cancellationToken).ConfigureAwait(false);

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [plan.MediaId],
            CatalogInvalidationDomain.Media,
            plan.ExpectedMediaRowVersion + 1));
        var affectedRelationProfileIds = relationSnapshots
            .Select(relation => relation.ProfileId)
            .Append(plan.OwnerProfileId)
            .Distinct()
            .ToArray();
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            affectedRelationProfileIds,
            CatalogInvalidationDomain.Profile,
            0));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [plan.MediaId],
            CatalogInvalidationDomain.Trash,
            0));
        transaction.QueueInvalidation(CatalogInvalidationDomain.Health);
        // Queue APPEARANCE for profiles that lost cover/banner.
        var affectedAppearanceProfileIds = affected
            .Select(reference => reference.ProfileId)
            .Distinct()
            .ToArray();
        if (affectedAppearanceProfileIds.Length > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                affectedAppearanceProfileIds,
                CatalogInvalidationDomain.Appearance,
                0));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new MediaTrashCommitResult(
            OperationResult<MediaTrashOutcome>.Success(
                new MediaTrashOutcome(plan.TrashEntryId, plan.MediaId, plan.RecoveryRelativePath, newMediaRowVersion),
                plan.OperationId),
            committedPlan);
    }

    private async Task<OperationResult<ProfileTrashOutcome>> CommitProfileTrashMarkerAsync(
        ProfileTrashPlan plan,
        string recoveryRelativePath,
        int trashedMedias,
        int reassignedMedias,
        CancellationToken cancellationToken)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var entry = await ReadTrashEntryAsync(connection, transaction, plan.TrashEntryId, cancellationToken)
            .ConfigureAwait(false);
        var profile = await ReadProfileTrashStateAsync(connection, transaction, plan.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null || profile is null)
        {
            return OperationResult<ProfileTrashOutcome>.NotFound(
                OperationErrorCode.TrashEntryNotFound,
                "That Trash record no longer exists.");
        }

        var remaining = await ReadOwnedActiveMediasAsync(connection, transaction, plan.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (remaining.Count > 0)
        {
            return OperationResult<ProfileTrashOutcome>.Conflict(
                OperationErrorCode.TrashDispositionRequired,
                $"{remaining.Count} media item(s) this Profile owns still need a destination before it can move to Trash.");
        }

        var ownerTransferReceipts = await ReadProfileOwnerTransferReceiptsAsync(
            connection,
            transaction,
            plan.SelectedDispositions,
            cancellationToken).ConfigureAwait(false);
        if (ownerTransferReceipts is null)
        {
            return OperationResult<ProfileTrashOutcome>.Conflict(
                OperationErrorCode.TrashPlanStale,
                "A media owner changed while the Profile was moving to Trash. Reload it and try again.");
        }

        var relationSnapshots = await ReadProfileRelationSnapshotsAsync(
            connection, transaction, plan.ProfileId, cancellationToken).ConfigureAwait(false);
        var identitySnapshot = await ReadActiveIdentitySnapshotAsync(
            connection, transaction, plan.ProfileId, cancellationToken).ConfigureAwait(false);
        var committedPlan = plan with
        {
            OwnerTransferReceipts = ownerTransferReceipts,
            RelationSnapshots = relationSnapshots,
            ActiveIdentitySnapshot = identitySnapshot,
            TrashCheckpoint = new ProfileTrashCheckpoint(recoveryRelativePath),
        };

        var now = DbTime.Format(_timeProvider.GetUtcNow());

        await ExecuteAsync(
            transaction,
            "DELETE FROM profile_media WHERE profile_id = $profileId;",
            cancellationToken,
            ("$profileId", DbGuid.Format(plan.ProfileId))).ConfigureAwait(false);

        await ExecuteAsync(
            transaction,
            """
            UPDATE identities
            SET is_active = 0,
                retired_at_ms = COALESCE(retired_at_ms, $now),
                row_version = row_version + 1
            WHERE profile_id = $profileId AND is_active = 1;
            """,
            cancellationToken,
            ("$now", now),
            ("$profileId", DbGuid.Format(plan.ProfileId))).ConfigureAwait(false);

        var newProfileRowVersion = profile.RowVersion + 1;
        var updated = await ExecuteAsync(
            transaction,
            """
            UPDATE profiles
            SET trashed_at_ms = $now,
                updated_at_ms = $now,
                row_version = $newRowVersion
            WHERE profile_id = $profileId AND row_version = $expectedRowVersion AND trashed_at_ms IS NULL;
            """,
            cancellationToken,
            ("$now", now),
            ("$newRowVersion", newProfileRowVersion),
            ("$profileId", DbGuid.Format(plan.ProfileId)),
            ("$expectedRowVersion", profile.RowVersion)).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new CatalogInvariantException(
                $"Profile {plan.ProfileId:D} changed while its move to Trash was being committed.");
        }

        await CheckpointTrashEntryAsync(
            transaction,
            plan.TrashEntryId,
            TrashEntryState.InTrash,
            recoveryRelativePath,
            completedAtMs: null,
            entry.RowVersion,
            now,
            cancellationToken,
            committedPlan.ToJson()).ConfigureAwait(false);

        await AppendActivityAsync(
            transaction,
            ActivityEventType.ProfileTrashed,
            plan.ProfileId,
            assetId: null,
            plan.OperationId,
            now,
            cancellationToken).ConfigureAwait(false);

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [plan.ProfileId],
            CatalogInvalidationDomain.Profile,
            newProfileRowVersion));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [plan.ProfileId],
            CatalogInvalidationDomain.Trash,
            0));
        transaction.QueueInvalidation(CatalogInvalidationDomain.Health);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return OperationResult<ProfileTrashOutcome>.Success(
            new ProfileTrashOutcome(
                plan.TrashEntryId,
                plan.ProfileId,
                newProfileRowVersion,
                trashedMedias,
                reassignedMedias),
            plan.OperationId);
    }

    private async Task<ProfileManifestRecoveryResult> MoveProfileManifestToRecoveryAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        ProfileTrashState? profile;
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            profile = await ReadProfileTrashStateAsync(
                    connection, transaction: null, profileId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (profile is null)
        {
            return ProfileManifestRecoveryResult.Rejected(
                OperationResult<ProfileTrashOutcome>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "That Profile no longer exists."));
        }

        if (string.IsNullOrWhiteSpace(profile.CurrentManagedRelativePath))
        {
            return ProfileManifestRecoveryResult.Rejected(
                OperationResult<ProfileTrashOutcome>.NeedsAttention(
                    OperationErrorCode.ProfilePathReconciliationBlocked,
                    "That Profile has no current managed folder, so its recovery material cannot be moved safely."));
        }

        var recoveryRelativePath = $"_trash/profiles/{profileId:D}";
        var request = new ManagedProfileManifestTrashRequest(
            profileId,
            profile.CurrentManagedRelativePath,
            recoveryRelativePath);
        var recovery = await _moveExecutor.InspectProfileManifestTrashDestinationAsync(
                profileId, recoveryRelativePath, cancellationToken)
            .ConfigureAwait(false);

        if (recovery.Status == StorageOperationStatus.SourceMissing)
        {

            var regeneration = await _manifestWriter.RegenerateManifestAsync(
                    _catalog, profileId, cancellationToken)
                .ConfigureAwait(false);
            if (!regeneration.IsSuccess)
            {
                return ProfileManifestRecoveryResult.Rejected(
                    OperationResult<ProfileTrashOutcome>.NeedsAttention(
                        OperationErrorCode.ProfileManifestWriteFailed,
                        "The Profile manifest could not be prepared for recovery. The Profile remains active."));
            }

        }
        else if (!recovery.IsSuccess)
        {
            return ProfileManifestRecoveryResult.Rejected(MapProfileManifestMoveFailure(recovery));
        }

        var move = await _moveExecutor.ExecuteProfileManifestTrashMoveAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!move.IsSuccess)
        {
            return ProfileManifestRecoveryResult.Rejected(MapProfileManifestMoveFailure(move));
        }

        // The manifest is safely in recovery. Retire only empty source scaffolding;
        // deferred ownership moves or unexpected bytes must keep their directories. The same
        // conservative cleanup is retried after the IN_TRASH marker is durable.
        TryRemoveEmptyProfileScaffolding(profile.CurrentManagedRelativePath);

        return ProfileManifestRecoveryResult.Accepted(recoveryRelativePath);
    }

    private void TryRemoveEmptyProfileScaffolding(string vaultRelativePath)
    {
        try
        {
            var source = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.Profiles, vaultRelativePath);
            if (Neuterradise.App.Maintenance.RetiredProfileDirectory.IsEmptyTree(_catalog.Paths.Root, source))
            {
                Neuterradise.App.Maintenance.RetiredProfileDirectory.RemoveEmptyTree(_catalog.Paths.Root, source);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Cleanup is best effort; recovery authority and every nonempty/untrusted folder remain intact.
        }
    }

    private static async Task<IReadOnlyList<DurableMediaAssetTrashPlan>> ReadMediaAssetPlansAsync(
        SqliteConnection connection, Guid assetId, CancellationToken cancellationToken)
    {
        var plans = new List<DurableMediaAssetTrashPlan>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT aa.relative_path, aa.byte_length, aa.sha256, aa.role, a.media_storage_token FROM media_assets aa JOIN media a ON a.media_id = aa.media_id WHERE aa.media_id = $id ORDER BY aa.role;";
        command.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var relativePath = reader.GetString(0);
            var role = DbEnum.ParseMediaAssetRole(reader.GetString(3));
            var name = new ManagedPathPlanner().PlanMediaAsset(new MediaStorageToken(reader.GetString(4)),role);
            name = name[(name.LastIndexOf('/')+1)..];
            plans.Add(new DurableMediaAssetTrashPlan(relativePath,
                $"_trash/media/{assetId:D}/media-assets/{name}",
                reader.GetInt64(1), reader.GetString(2), role));
        }
        return plans;
    }

    private static async Task<IReadOnlyList<ProfileMediaTrashReference>> ReadSelectedMediaReferencesAsync(
        SqliteConnection connection, Guid mediaId, CancellationToken cancellationToken)
    {
        var references = new List<ProfileMediaTrashReference>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pa.profile_id, pa.cover_media_asset_id, pa.banner_media_asset_id, pa.row_version, pa.figure_media_id
            FROM profile_appearance pa
            WHERE pa.cover_media_asset_id IN
                    (SELECT media_asset_id FROM media_assets WHERE media_id = $id)
               OR pa.banner_media_asset_id IN
                    (SELECT media_asset_id FROM media_assets WHERE media_id = $id)
               OR pa.figure_media_id = $id;
            """;
        command.Parameters.AddWithValue("$id", DbGuid.Format(mediaId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            references.Add(new ProfileMediaTrashReference(
                DbGuid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : DbGuid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : DbGuid.Parse(reader.GetString(2)),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : DbGuid.Parse(reader.GetString(4))));
        return references;
    }

    private static OperationResult<ProfileTrashOutcome> MapProfileManifestMoveFailure(
        StorageOperationResult result) => result.Status switch
        {
            StorageOperationStatus.UnexpectedTarget or StorageOperationStatus.TargetCollision
                or StorageOperationStatus.VerificationFailed => OperationResult<ProfileTrashOutcome>.NeedsAttention(
                    OperationErrorCode.TrashDestinationConflict,
                    "The Profile recovery manifest conflicts with material already in Trash, so nothing was overwritten."),
            StorageOperationStatus.PathOutsideVault => OperationResult<ProfileTrashOutcome>.NeedsAttention(
                OperationErrorCode.CurrentPathAmbiguous,
                "The Profile manifest location falls outside its exact managed recovery boundary."),
            StorageOperationStatus.Cancelled => OperationResult<ProfileTrashOutcome>.Failed(
                OperationErrorCode.TrashPhysicalMoveFailed,
                "The Profile manifest move was cancelled before it completed."),
            _ => OperationResult<ProfileTrashOutcome>.Failed(
                OperationErrorCode.TrashPhysicalMoveFailed,
                "The Profile manifest could not be moved into recovery. The operation can be retried."),
        };

    private static OperationResult<MediaTrashPlan>? ValidateMediaIsTrashable(MediaTrashState asset)
    {
        if (asset.State != MediaState.Active)
        {
            return OperationResult<MediaTrashPlan>.Conflict(
                OperationErrorCode.MediaNotActive,
                "Only active media can be moved to Trash.");
        }

        if (asset.OwnerProfileId is null)
        {
            return OperationResult<MediaTrashPlan>.NeedsAttention(
                OperationErrorCode.MediaOwnerConflict,
                "This media item has no recorded owner and needs attention before it can be moved to Trash.");
        }

        if (asset.Sha256 is null || asset.ByteLength is null || asset.StorageToken is null)
        {
            return OperationResult<MediaTrashPlan>.NeedsAttention(
                OperationErrorCode.MediaContentMismatch,
                "This media item's content record is incomplete, so a reversible move to Trash cannot be verified.");
        }

        if (asset.CurrentManagedRelativePath is null || asset.CurrentManagedFileName is null)
        {
            return OperationResult<MediaTrashPlan>.NeedsAttention(
                OperationErrorCode.CurrentPathMissing,
                "This media item has no recorded managed file, so there is nothing to move to Trash.");
        }

        if (asset.PathState != ManagedPathState.None)
        {

            return OperationResult<MediaTrashPlan>.Conflict(
                OperationErrorCode.ProfilePathReconciliationBlocked,
                "This media item is already being moved. Wait for that to finish, then try again.");
        }

        return null;
    }

    private async Task<ProfileTrashValidation> ValidateDispositionsAsync(
        SqliteConnection connection,
        ProfileTrashPlan plan,
        IReadOnlyList<ProfileOwnedMediaDisposition> dispositions,
        CancellationToken cancellationToken)
    {
        var byMedia = new Dictionary<Guid, ProfileOwnedMediaDisposition>();
        foreach (var disposition in dispositions)
        {
            if (!byMedia.TryAdd(disposition.MediaId, disposition))
            {
                return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Validation(
                    OperationErrorCode.TrashDispositionInvalid,
                    "One media item was given more than one destination."));
            }
        }

        var planned = plan.OwnedActiveMedias.Select(asset => asset.MediaId).ToHashSet();
        var missing = planned.Where(assetId => !byMedia.ContainsKey(assetId)).ToArray();
        if (missing.Length > 0)
        {

            return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Validation(
                OperationErrorCode.TrashDispositionRequired,
                $"{missing.Length} media item(s) this Profile owns still need a destination."));
        }

        var extra = byMedia.Keys.Where(assetId => !planned.Contains(assetId)).ToArray();
        if (extra.Length > 0)
        {
            return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Validation(
                OperationErrorCode.TrashDispositionInvalid,
                "A destination was given for media this Profile does not own."));
        }

        var pending = new List<ProfileOwnedMediaDisposition>(byMedia.Count);
        foreach (var disposition in byMedia.Values)
        {
            var expectedRowVersion = plan.OwnedActiveMedias
                .First(asset => asset.MediaId == disposition.MediaId)
                .RowVersion;
            var asset = await ReadMediaTrashStateAsync(
                    connection, transaction: null, disposition.MediaId, cancellationToken)
                .ConfigureAwait(false);
            if (asset is null)
            {
                return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.NotFound(
                    OperationErrorCode.MediaNotFound,
                    "Media in this Profile Trash plan no longer exists."));
            }

            if (disposition.Kind == ProfileOwnedMediaDispositionKind.TrashMedia)
            {
                if (disposition.NewOwnerProfileId is not null)
                {
                    return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Validation(
                        OperationErrorCode.TrashDispositionInvalid,
                        "A media item moved to Trash cannot also be given a new owner."));
                }

                if (asset.State == MediaState.Trashed)
                {
                    continue;
                }

                if (asset.State != MediaState.Active
                    || asset.OwnerProfileId != plan.ProfileId
                    || asset.RowVersion != expectedRowVersion)
                {
                    return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Conflict(
                        OperationErrorCode.TrashPlanStale,
                        "Media in this Profile Trash plan changed before its Trash disposition completed."));
                }

                pending.Add(disposition);
                continue;
            }

            if (disposition.NewOwnerProfileId is not { } newOwnerId || newOwnerId == Guid.Empty)
            {
                return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Validation(
                    OperationErrorCode.TrashDispositionRequired,
                    "Changing the owner requires an explicitly chosen Profile."));
            }

            if (newOwnerId == plan.ProfileId)
            {
                return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Validation(
                    OperationErrorCode.TrashDispositionInvalid,
                    "Media cannot be given to the Profile that is moving to Trash."));
            }

            var destination = await ReadProfileTrashStateAsync(
                connection, transaction: null, newOwnerId, cancellationToken).ConfigureAwait(false);
            if (destination is null)
            {
                return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "A chosen destination Profile no longer exists."));
            }

            if (destination.IsTrashed)
            {
                return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Conflict(
                    OperationErrorCode.ProfileConflict,
                    "A chosen destination Profile is itself in Trash and cannot take ownership of media."));
            }

            if (asset.State == MediaState.Active && asset.OwnerProfileId == newOwnerId)
            {
                continue;
            }

            if (asset.State != MediaState.Active
                || asset.OwnerProfileId != plan.ProfileId
                || asset.RowVersion != expectedRowVersion)
            {
                return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "Media in this Profile Trash plan changed before its OWNER disposition completed."));
            }

            pending.Add(disposition);
        }

        var ordered = pending
            .OrderBy(disposition => disposition.Kind == ProfileOwnedMediaDispositionKind.TrashMedia ? 0 : 1)
            .ThenBy(disposition => disposition.MediaId)
            .ToArray();

        return ProfileTrashValidation.Accepted(ordered);
    }

    private static async Task<bool> ProfileAuthorityMatchesPlanAsync(
        SqliteConnection connection,
        ProfileTrashState profile,
        ProfileTrashPlan plan,
        IReadOnlyList<ProfileOwnedMediaDisposition> dispositions,
        CancellationToken cancellationToken)
    {
        var expectedCover = plan.ExpectedCoverMediaId;
        var expectedBanner = plan.ExpectedBannerMediaId;
        var expectedRowVersion = plan.ExpectedProfileRowVersion;

        foreach (var disposition in dispositions.OrderBy(item => item.MediaId))
        {
            if (expectedCover != disposition.MediaId && expectedBanner != disposition.MediaId)
            {
                continue;
            }

            var asset = await ReadMediaTrashStateAsync(
                    connection, transaction: null, disposition.MediaId, cancellationToken)
                .ConfigureAwait(false);
            var completed = disposition.Kind == ProfileOwnedMediaDispositionKind.TrashMedia
                ? asset?.State == MediaState.Trashed
                : asset?.State == MediaState.Active
                  && asset.OwnerProfileId == disposition.NewOwnerProfileId;
            if (!completed)
            {
                continue;
            }

            var clearsAppearance = disposition.Kind == ProfileOwnedMediaDispositionKind.TrashMedia
                || !await ProfileMediaRelationExistsAsync(
                        connection, plan.ProfileId, disposition.MediaId, cancellationToken)
                    .ConfigureAwait(false);
            if (!clearsAppearance)
            {
                continue;
            }

            if (expectedCover == disposition.MediaId)
            {
                expectedCover = null;
            }

            if (expectedBanner == disposition.MediaId)
            {
                expectedBanner = null;
            }

            expectedRowVersion++;
        }

        return profile.RowVersion == expectedRowVersion
            && profile.CoverMediaId == expectedCover
            && profile.BannerMediaId == expectedBanner;
    }

    private static async Task<bool> ProfileMediaRelationExistsAsync(
        SqliteConnection connection,
        Guid profileId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM profile_media WHERE profile_id = $profileId AND media_id = $assetId);";
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    internal static string BuildRecoveryRelativePath(Guid assetId, string normalizedExtension) =>
        $"_trash/media/{assetId:D}/payload.{normalizedExtension}";

    internal static async Task<MediaTrashState?> ReadMediaTrashStateAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT a.state, a.media_type, a.media_storage_token, a.sha256, a.byte_length,
                   a.current_managed_relative_path, a.current_managed_file_name,
                   a.path_state, a.row_version,
                   (SELECT profile_id FROM profile_media
                    WHERE media_id = a.media_id AND relation_type = 'OWNER') AS owner_profile_id
            FROM media a
            WHERE a.media_id = $assetId;
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new MediaTrashState(
            assetId,
            DbEnum.ParseMediaState(reader.GetString(0)),
            DbEnum.ParseMediaType(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            DbEnum.ParseManagedPathState(reader.GetString(7)),
            reader.GetInt64(8),
            reader.IsDBNull(9) ? null : DbGuid.Parse(reader.GetString(9)));
    }

    internal static async Task<ProfileTrashState?> ReadProfileTrashStateAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT p.kind, p.display_name, p.unknown_sequence, p.profile_storage_token,
                   p.current_managed_relative_path, p.trashed_at_ms, p.row_version,
                   p.cover_media_id,
                   (SELECT ma.media_id
                    FROM profile_appearance pa
                    JOIN media_assets ma ON ma.media_asset_id = pa.banner_media_asset_id
                    WHERE pa.profile_id = p.profile_id) AS banner_media_id
            FROM profiles p
            WHERE p.profile_id = $profileId;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var kind = DbEnum.ParseProfileKind(reader.GetString(0));
        var displayName = reader.IsDBNull(1) ? null : reader.GetString(1);
        var unknownSequence = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);

        return new ProfileTrashState(
            profileId,
            kind,
            displayName ?? UnknownProfileRules.FormatDerivedLabel(unknownSequence ?? 0),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            !reader.IsDBNull(5),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : DbGuid.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : DbGuid.Parse(reader.GetString(8)));
    }

    internal static async Task<IReadOnlyList<ProfileOwnedMediaSnapshot>> ReadOwnedActiveMediasAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT a.media_id, a.row_version
            FROM profile_media pa
            JOIN media a ON a.media_id = pa.media_id
            WHERE pa.profile_id = $profileId
              AND pa.relation_type = 'OWNER'
              AND a.state = 'ACTIVE'
            ORDER BY a.media_id;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        var owned = new List<ProfileOwnedMediaSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            owned.Add(new ProfileOwnedMediaSnapshot(DbGuid.Parse(reader.GetString(0)), reader.GetInt64(1)));
        }

        return owned;
    }

    internal static async Task<IReadOnlyList<MediaProfileRelationSnapshot>> ReadMediaProfileRelationSnapshotsAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT profile_id, relation_type, provenance_key, publication_import_unit_id, created_at_ms
            FROM profile_media
            WHERE media_id = $assetId
            ORDER BY profile_id, relation_type;
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var result = new List<MediaProfileRelationSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new MediaProfileRelationSnapshot(
                DbGuid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : DbGuid.Parse(reader.GetString(3)),
                reader.GetInt64(4)));
        }
        return result;
    }

    internal static async Task<IReadOnlyList<ProfileOwnerTransferReceipt>?> ReadProfileOwnerTransferReceiptsAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        IReadOnlyList<ProfileOwnedMediaDisposition> dispositions,
        CancellationToken cancellationToken)
    {
        var receipts = new List<ProfileOwnerTransferReceipt>();
        foreach (var disposition in dispositions
                     .Where(static item => item.Kind == ProfileOwnedMediaDispositionKind.ChangeOwner)
                     .OrderBy(static item => item.MediaId))
        {
            if (disposition.NewOwnerProfileId is not { } expectedOwnerProfileId)
            {
                return null;
            }

            await using var command = CreateCommand(
                connection,
                transaction,
                """
                SELECT COUNT(*), MIN(pm.profile_id), MIN(pm.created_at_ms)
                FROM profile_media pm
                JOIN media m ON m.media_id = pm.media_id
                WHERE pm.media_id = $mediaId
                  AND pm.relation_type = 'OWNER'
                  AND m.state = 'ACTIVE';
                """);
            command.Parameters.AddWithValue("$mediaId", DbGuid.Format(disposition.MediaId));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.GetInt64(0) != 1
                || reader.IsDBNull(1)
                || reader.IsDBNull(2))
            {
                return null;
            }

            var actualOwnerProfileId = DbGuid.Parse(reader.GetString(1));
            if (actualOwnerProfileId != expectedOwnerProfileId)
            {
                return null;
            }

            receipts.Add(new ProfileOwnerTransferReceipt(
                disposition.MediaId,
                actualOwnerProfileId,
                reader.GetInt64(2)));
        }

        return receipts;
    }

    internal static async Task<IReadOnlyList<ProfileRelationSnapshot>> ReadProfileRelationSnapshotsAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT media_id, relation_type, provenance_key, publication_import_unit_id, created_at_ms
            FROM profile_media
            WHERE profile_id = $profileId
            ORDER BY media_id, relation_type;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        var result = new List<ProfileRelationSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ProfileRelationSnapshot(
                DbGuid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : DbGuid.Parse(reader.GetString(3)),
                reader.GetInt64(4)));
        }
        return result;
    }

    internal static async Task<ProfileIdentitySnapshot?> ReadActiveIdentitySnapshotAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT identity_id, row_version, retired_at_ms
            FROM identities
            WHERE profile_id = $profileId AND is_active = 1
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProfileIdentitySnapshot(
            DbGuid.Parse(reader.GetString(0)),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    internal static async Task<IReadOnlyList<AffectedAppearanceReference>> ReadAffectedAppearanceReferencesAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT p.profile_id,
                   CASE WHEN p.cover_media_id = $assetId THEN $assetId ELSE NULL END AS cover_media_id,
                   CASE WHEN EXISTS (
                       SELECT 1
                       FROM profile_appearance pa
                       JOIN media_assets ma ON ma.media_asset_id = pa.banner_media_asset_id
                       WHERE pa.profile_id = p.profile_id AND ma.media_id = $assetId
                   ) THEN $assetId ELSE NULL END AS banner_media_id,
                   CASE WHEN EXISTS (
                       SELECT 1
                       FROM profile_appearance pa
                       WHERE pa.profile_id = p.profile_id AND pa.figure_media_id = $assetId
                   ) THEN $assetId ELSE NULL END AS figure_media_id
            FROM profiles p
            WHERE p.cover_media_id = $assetId
               OR EXISTS (
                   SELECT 1
                   FROM profile_appearance pa
                   JOIN media_assets ma ON ma.media_asset_id = pa.banner_media_asset_id
                   WHERE pa.profile_id = p.profile_id AND ma.media_id = $assetId
               )
               OR EXISTS (
                   SELECT 1
                   FROM profile_appearance pa
                   WHERE pa.profile_id = p.profile_id AND pa.figure_media_id = $assetId
               )
            ORDER BY p.profile_id;
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var formatted = DbGuid.Format(assetId);
        var references = new List<AffectedAppearanceReference>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var isCover = !reader.IsDBNull(1) && reader.GetString(1) == formatted;
            var isBanner = !reader.IsDBNull(2) && reader.GetString(2) == formatted;
            var isFigure = !reader.IsDBNull(3) && reader.GetString(3) == formatted;
            references.Add(new AffectedAppearanceReference(
                DbGuid.Parse(reader.GetString(0)),
                isCover,
                isBanner,
                isFigure));
        }

        return references;
    }

    internal static async Task<TrashEntryRow?> ReadTrashEntryAsync(
        SqliteConnection connection,
        Guid trashEntryId,
        CancellationToken cancellationToken) =>
        await ReadTrashEntryAsync(connection, transaction: null, trashEntryId, cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<TrashEntryRow?> ReadTrashEntryAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid trashEntryId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT entity_type, entity_id, state, recovery_relative_path, plan_json, row_version
            FROM trash_entries
            WHERE trash_entry_id = $trashEntryId;
            """);
        command.Parameters.AddWithValue("$trashEntryId", DbGuid.Format(trashEntryId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TrashEntryRow(
            trashEntryId,
            reader.GetString(0),
            DbGuid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5));
    }

    internal static async Task<TrashEntryRow?> ReadActiveTrashEntryAsync(
        SqliteConnection connection,
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT trash_entry_id, entity_type, entity_id, state, recovery_relative_path, plan_json, row_version
            FROM trash_entries
            WHERE entity_type = $entityType
              AND entity_id = $entityId
              AND state IN ('PENDING', 'EXECUTING', 'IN_TRASH')
            ORDER BY created_at_ms DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", DbGuid.Format(entityId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TrashEntryRow(
            DbGuid.Parse(reader.GetString(0)),
            reader.GetString(1),
            DbGuid.Parse(reader.GetString(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6));
    }

    private Task MarkEntryStateAsync(
        Guid trashEntryId,
        string state,
        string? recoveryRelativePath,
        DateTimeOffset? completedAtUtc,
        long expectedRowVersion,
        CancellationToken cancellationToken) =>
        _catalog.TrashWrites.UpdateEntryStateAsync(
            trashEntryId,
            state,
            recoveryRelativePath,
            completedAtUtc,
            expectedRowVersion,
            cancellationToken);

    internal static async Task CheckpointTrashEntryAsync(
        CatalogTransaction transaction,
        Guid trashEntryId,
        string state,
        string? recoveryRelativePath,
        long? completedAtMs,
        long expectedRowVersion,
        long nowMs,
        CancellationToken cancellationToken,
        string? planJson = null)
    {
        await using var command = transaction.CreateCommand(
            """
            UPDATE trash_entries
            SET state = $state,
                recovery_relative_path = $recoveryPath,
                plan_json = CASE WHEN $planJson IS NULL THEN plan_json ELSE $planJson END,
                completed_at_ms = $completedAt,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE trash_entry_id = $trashEntryId AND row_version = $expectedRowVersion;
            """);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$recoveryPath", (object?)recoveryRelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$planJson", (object?)planJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", (object?)completedAtMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", nowMs);
        command.Parameters.AddWithValue("$trashEntryId", DbGuid.Format(trashEntryId));
        command.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CatalogConcurrencyConflictException(
                $"TrashEntry {trashEntryId:D} changed while its checkpoint was being written.");
        }
    }

    internal static async Task AppendActivityAsync(
        CatalogTransaction transaction,
        string eventType,
        Guid? profileId,
        Guid? assetId,
        Guid? operationId,
        long nowMs,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            INSERT INTO activity_log(
                activity_id, event_type, profile_id, media_id, operation_id,
                payload_json, occurred_at_ms)
            VALUES ($activityId, $eventType, $profileId, $assetId, $operationId, $payload, $now);
            """);
        command.Parameters.AddWithValue("$activityId", DbGuid.Format(Guid.NewGuid()));
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$profileId", profileId is null ? DBNull.Value : DbGuid.Format(profileId.Value));
        command.Parameters.AddWithValue("$assetId", assetId is null ? DBNull.Value : DbGuid.Format(assetId.Value));
        command.Parameters.AddWithValue("$operationId", operationId is null ? DBNull.Value : DbGuid.Format(operationId.Value));
        command.Parameters.AddWithValue("$payload", "{}");
        command.Parameters.AddWithValue("$now", nowMs);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<int> ExecuteAsync(
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

    internal static SqliteCommand CreateCommand(
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

    internal static OperationResult<T> MapTrashMoveFailure<T>(StorageOperationResult result) => result.Status switch
    {
        StorageOperationStatus.SourceMissing => OperationResult<T>.NeedsAttention(
            OperationErrorCode.CurrentPathMissing,
            "The managed file for this media item was not found where it was recorded."),
        StorageOperationStatus.SourceChanged or StorageOperationStatus.VerificationFailed =>
            OperationResult<T>.NeedsAttention(
                OperationErrorCode.MediaContentMismatch,
                "The managed file does not match its recorded content, so it was left untouched."),
        StorageOperationStatus.UnexpectedTarget or StorageOperationStatus.TargetCollision =>
            OperationResult<T>.NeedsAttention(
                OperationErrorCode.TrashDestinationConflict,
                "Something already exists where this media item would be placed, so nothing was overwritten."),
        StorageOperationStatus.Cancelled => OperationResult<T>.Failed(
            OperationErrorCode.TrashPhysicalMoveFailed,
            "The move was cancelled before it completed."),
        StorageOperationStatus.PathOutsideVault => OperationResult<T>.NeedsAttention(
            OperationErrorCode.CurrentPathAmbiguous,
            "A recorded location falls outside the vault and was refused."),
        _ => OperationResult<T>.Failed(
            OperationErrorCode.TrashPhysicalMoveFailed,
            "The media file could not be moved. Nothing was deleted; the operation can be retried."),
    };

    private static OperationResult<TTarget> Propagate<TSource, TTarget>(OperationResult<TSource> source) =>
        new(source.Status, default, source.ErrorCode, source.UserMessage, source.OperationId);

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }

    private static string CombineRelative(string directory, string fileName)
    {
        var trimmed = directory.TrimEnd('/', '\\');
        return trimmed.Length == 0 ? fileName : $"{trimmed}/{fileName.TrimStart('/', '\\')}";
    }

    private static string RelativeDirectoryOf(string relativePath)
    {
        var index = relativePath.LastIndexOfAny(['/', '\\']);
        return index <= 0 ? string.Empty : relativePath[..index];
    }

    private static string FileNameOf(string relativePath)
    {
        var index = relativePath.LastIndexOfAny(['/', '\\']);
        return index < 0 ? relativePath : relativePath[(index + 1)..];
    }

    private sealed record ProfileTrashValidation(
        OperationResult<ProfileTrashOutcome>? Failure,
        IReadOnlyList<ProfileOwnedMediaDisposition> Ordered)
    {
        public static ProfileTrashValidation Rejected(OperationResult<ProfileTrashOutcome> failure) =>
            new(failure, []);

        public static ProfileTrashValidation Accepted(IReadOnlyList<ProfileOwnedMediaDisposition> ordered) =>
            new(null, ordered);
    }

    private sealed record MediaTrashCommitResult(
        OperationResult<MediaTrashOutcome> Outcome,
        MediaTrashPlan Plan);

    private sealed record ProfileManifestRecoveryResult(
        string? RecoveryRelativePath,
        OperationResult<ProfileTrashOutcome>? Failure)
    {
        public bool IsSuccess => Failure is null;

        public static ProfileManifestRecoveryResult Accepted(string recoveryRelativePath) =>
            new(recoveryRelativePath, null);

        public static ProfileManifestRecoveryResult Rejected(OperationResult<ProfileTrashOutcome> failure) =>
            new(null, failure);
    }
}

public sealed record MediaTrashOutcome(
    Guid TrashEntryId,
    Guid MediaId,
    string RecoveryRelativePath,
    long MediaRowVersion);

public sealed record ProfileTrashOutcome(
    Guid TrashEntryId,
    Guid ProfileId,
    long ProfileRowVersion,
    int TrashedMediaCount,
    int ReassignedMediaCount);

public sealed record TrashEntryRow(
    Guid TrashEntryId,
    string EntityType,
    Guid EntityId,
    string State,
    string? RecoveryRelativePath,
    string PlanJson,
    long RowVersion);

public sealed record MediaTrashState(
    Guid MediaId,
    MediaState State,
    MediaType MediaType,
    string? StorageToken,
    string? Sha256,
    long? ByteLength,
    string? CurrentManagedRelativePath,
    string? CurrentManagedFileName,
    ManagedPathState PathState,
    long RowVersion,
    Guid? OwnerProfileId);

public sealed record ProfileTrashState(
    Guid ProfileId,
    ProfileKind Kind,
    string Label,
    string? StorageToken,
    string? CurrentManagedRelativePath,
    bool IsTrashed,
    long RowVersion,
    Guid? CoverMediaId,
    Guid? BannerMediaId);
