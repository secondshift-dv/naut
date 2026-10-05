using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.Import.Verification;

public sealed class ImportCommitCoordinator
{
    private readonly CatalogDb _catalog;
    private readonly VerificationValidator _validator;
    private readonly ManagedMoveExecutor _moveExecutor;
    private readonly SourceCleanupExecutor _cleanupExecutor;
    private readonly IVolumeIdentityProvider _volumeIdentity;
    private readonly VaultPaths _vaultPaths;
    private readonly ManagedPathPlanner _pathPlanner;
    private readonly StorageTokenAllocator _tokenAllocator;
    private readonly Action<ImportCommitCheckpoint>? _checkpointObserver;
    private readonly TimeProvider _timeProvider;

    public ImportCommitCoordinator(
        CatalogDb catalog,
        VerificationValidator validator,
        ManagedMoveExecutor moveExecutor,
        SourceCleanupExecutor cleanupExecutor,
        IVolumeIdentityProvider volumeIdentity,
        VaultPaths vaultPaths,
        Action<ImportCommitCheckpoint>? checkpointObserver = null,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _moveExecutor = moveExecutor ?? throw new ArgumentNullException(nameof(moveExecutor));
        _cleanupExecutor = cleanupExecutor ?? throw new ArgumentNullException(nameof(cleanupExecutor));
        _volumeIdentity = volumeIdentity ?? throw new ArgumentNullException(nameof(volumeIdentity));
        _vaultPaths = vaultPaths ?? throw new ArgumentNullException(nameof(vaultPaths));
        _pathPlanner = new ManagedPathPlanner(_vaultPaths.Root);
        _tokenAllocator = new StorageTokenAllocator();
        _checkpointObserver = checkpointObserver;
    }

    /// <summary>
    /// Canonical commit entry point: materializes authoritative Media without Verify/readiness gating.
    ///
    /// Effective order:
    ///   destination Profile selection/creation
    ///   → stable ProfileId / MediaId / storage token
    ///   → canonical Profile folder
    ///   → final filename/path planning
    ///   → exact duplicate resolution
    ///   → move/copy + rename into final Vault tree
    ///   → OWNER + canonical managed location commit
    ///   → canonical commit complete (MEDIA AUTHORITATIVE = YES)
    ///
    /// Verify readiness, source cleanup and publication are NOT part of canonical commit.
    /// </summary>
    public async Task<ImportCommitResult> CommitCanonicalMediaAsync(
        Guid unitId,
        long? expectedUnitRowVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            throw new ArgumentException("A stable ImportUnitId cannot be empty.", nameof(unitId));
        }

        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        var importWrites = _catalog.ImportWrites;
        var importReads = new ImportReads(_catalog);

        var op = await importWrites.ReadCommitOperationAsync(unitId, cancellationToken).ConfigureAwait(false);
        var state = op is null
            ? new CommitState()
            : CommitState.FromJson(op.CheckpointJson) with
            {
                OperationId = op.OperationId,
                Checkpoint = DbEnum.ParseImportCommitCheckpointOrDefault(op.Checkpoint),
            };

        if (op is not null && DbEnum.ParseImportUnitState(op.UnitState) == ImportUnitState.Cancelled)
        {
            return ImportCommitResult.CancelledAt(unitId, op.OperationId, state.Checkpoint);
        }

        // First call: load unit, create commit operation. No Verify readiness gate.
        if (op is null)
        {
            var unit = await importReads.GetVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false);
            if (unit is null)
            {
                return ImportCommitResult.BlockedBy(unitId, null,
                    [new VerificationBlocker(null, "UNIT_NOT_FOUND", $"ImportUnit {unitId:D} does not exist.")]);
            }

            if (expectedUnitRowVersion is long expected && unit.RowVersion != expected)
            {
                return ImportCommitResult.ConflictOn(unitId);
            }

            var items = await ReadAllUnitItemsAsync(unitId, cancellationToken).ConfigureAwait(false);

            var sourceChangeBlockers = await DetectChangedSourcesAsync(items, cancellationToken).ConfigureAwait(false);
            if (sourceChangeBlockers.Count > 0)
            {
                return ImportCommitResult.BlockedBy(unitId, null, sourceChangeBlockers);
            }

            state = new CommitState
            {
                OperationId = Guid.NewGuid(),
                Checkpoint = ImportCommitCheckpoint.DecisionValidated,
            };
            state.OperationId = await importWrites.BeginOrReadCommitOperationAsync(
                unitId, state.OperationId, state.ToJson(), cancellationToken).ConfigureAwait(false);
            NotifyCheckpoint(ImportCommitCheckpoint.DecisionValidated);
        }

        // If canonical commit already completed (resume), return immediately.
        if (state.Checkpoint >= ImportCommitCheckpoint.DomainAuthorityCommitted)
        {
            var destinationProfileId = state.DestinationProfileId
                ?? throw new InvalidOperationException("Destination was prepared without a Profile id.");
            return new ImportCommitResult(
                unitId,
                state.OperationId,
                ImportCommitOutcome.Committed,
                state.Checkpoint,
                destinationProfileId,
                state.DestinationProfileCreated,
                state.ActivatedMediaIds,
                state.ReusedMediaIds,
                state.RetiredCandidateIds,
                [],
                []);
        }

        return await ExecuteMaterializationAsync(unitId, state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Materializes canonical commit, then performs source cleanup after durable library authority exists.
    /// Verification readiness is not required for canonical materialization.
    /// </summary>
    public async Task<ImportCommitResult> CommitAsync(
        Guid unitId,
        long? expectedUnitRowVersion = null,
        CancellationToken cancellationToken = default)
    {
        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        var canonicalCommit = await CommitCanonicalMediaAsync(unitId, expectedUnitRowVersion, cancellationToken).ConfigureAwait(false);
        if (!canonicalCommit.LibraryCommitted)
        {
            return canonicalCommit;
        }

        // Cleanup is allowed only after durable library authority exists.
        return await ExecuteSourceCleanupAsync(unitId, canonicalCommit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared canonical commit execution: DestinationPrepared → PlacementPlanned → DestinationBytesVerified → DomainAuthorityCommitted.
    /// Called by <see cref="CommitCanonicalMediaAsync"/> for both fresh and resumed runs.
    /// </summary>
    private async Task<ImportCommitResult> ExecuteMaterializationAsync(
        Guid unitId,
        CommitState state,
        CancellationToken cancellationToken)
    {
        var importWrites = _catalog.ImportWrites;
        var importReads = new ImportReads(_catalog);

        var currentUnit = await importReads.GetVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ImportUnit {unitId:D} vanished mid-commit.");
        if (currentUnit.State == ImportUnitState.Cancelled)
        {
            return ImportCommitResult.CancelledAt(unitId, state.OperationId, state.Checkpoint);
        }
        var currentDraft = VerificationDraftV1.FromJson(currentUnit.VerificationDraftJson, _timeProvider.GetUtcNowUnixMilliseconds());
        var currentItems = await ReadAllUnitItemsAsync(unitId, cancellationToken).ConfigureAwait(false);

        var included = currentItems
            .Where(i => i.Disposition == ItemDisposition.Included
                && i.CandidateMediaId is not null
                && i.DuplicateDecision != DuplicateDecision.Reuse)
            .ToList();
        var dedup = currentItems
            .Where(i => i.DuplicateDecision == DuplicateDecision.Reuse)
            .ToList();
        var skipped = currentItems
            .Where(i => i.Disposition == ItemDisposition.Skipped && i.CandidateMediaId is not null)
            .ToList();

        if (state.Checkpoint < ImportCommitCheckpoint.DestinationPrepared)
        {
            if (await IsCancelledAsync(unitId, cancellationToken).ConfigureAwait(false))
                return ImportCommitResult.CancelledAt(unitId, state.OperationId, state.Checkpoint);
            try
            {
                await PrepareDestinationAsync(unitId, currentDraft, state, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return CommitBlockedResult(unitId, state,
                    [new VerificationBlocker(null, "PROVISIONING_FAILED",
                        $"Destination provisioning failed: {exception.Message}")]);
            }
            await importWrites.AdvanceCommitCheckpointAsync(
                unitId, state.OperationId, ImportCommitCheckpoint.DestinationPrepared, state.ToJson(), cancellationToken).ConfigureAwait(false);
            state.Checkpoint = ImportCommitCheckpoint.DestinationPrepared;
            NotifyCheckpoint(state.Checkpoint);
        }

        var destinationProfileId = state.DestinationProfileId
            ?? throw new InvalidOperationException("Destination was prepared without a Profile id.");

        if (state.Checkpoint < ImportCommitCheckpoint.PlacementPlanned)
        {
            if (await IsCancelledAsync(unitId, cancellationToken).ConfigureAwait(false))
                return ImportCommitResult.CancelledAt(unitId, state.OperationId, state.Checkpoint);
            foreach (var item in included)
            {
                await PlanItemPlacementAsync(
                    destinationProfileId, state, item, cancellationToken).ConfigureAwait(false);
            }

            await importWrites.AdvanceCommitCheckpointAsync(
                unitId, state.OperationId, ImportCommitCheckpoint.PlacementPlanned, state.ToJson(), cancellationToken).ConfigureAwait(false);
            state.Checkpoint = ImportCommitCheckpoint.PlacementPlanned;
            NotifyCheckpoint(state.Checkpoint);
        }

        if (state.Checkpoint < ImportCommitCheckpoint.DestinationBytesVerified)
        {
            if (await IsCancelledAsync(unitId, cancellationToken).ConfigureAwait(false))
                return ImportCommitResult.CancelledAt(unitId, state.OperationId, state.Checkpoint);
            foreach (var item in included)
            {
                var assetId = item.CandidateMediaId!.Value;
                var sameVolume = state.SameVolumeByItem.GetValueOrDefault(item.ItemId, true);

                // Original filesystem dates are part of canonical import semantics, not a preference.
                var result = sameVolume
                    ? await _moveExecutor.ExecuteAsync(assetId, preserveSourceTimestamps: true, cancellationToken).ConfigureAwait(false)
                    : await _moveExecutor.ExecuteCrossVolumeAsync(item.ItemId, preserveSourceTimestamps: true, cancellationToken).ConfigureAwait(false);

                if (!result.IsSuccess)
                {
                    var blockerCode = result.Status is StorageOperationStatus.FileLocked
                        or StorageOperationStatus.VolumeResolutionFailed
                        or StorageOperationStatus.NeedsAttention
                        ? "PLACEMENT_RETRYABLE"
                        : "PLACEMENT_FAILED";
                    return CommitBlockedResult(unitId, state,
                        [new VerificationBlocker(item.ItemId, blockerCode,
                            result.SafeErrorDetail ?? $"Managed placement did not complete: {result.Status}.")]);
                }

                if (sameVolume)
                {
                    await importWrites.AdvanceItemCleanupStateAsync(
                        item.ItemId, SourceCleanupState.DestinationVerified, cancellationToken).ConfigureAwait(false);
                }

                await _catalog.MediaWrites.UpdateAllComponentsCleanupStateAsync(
                    item.CandidateMediaId!.Value, SourceCleanupState.DestinationVerified, null, cancellationToken).ConfigureAwait(false);
            }

            await importWrites.AdvanceCommitCheckpointAsync(
                unitId, state.OperationId, ImportCommitCheckpoint.DestinationBytesVerified, state.ToJson(), cancellationToken).ConfigureAwait(false);
            state.Checkpoint = ImportCommitCheckpoint.DestinationBytesVerified;
            NotifyCheckpoint(state.Checkpoint);
        }

        // Reapply the recorded source date even when placement reused already verified bytes.
        await new ImportSourceTimestampReconciler(_catalog).ReconcileAsync(unitId, cancellationToken)
            .ConfigureAwait(false);

        if (state.Checkpoint < ImportCommitCheckpoint.DomainAuthorityCommitted)
        {
            if (await IsCancelledAsync(unitId, cancellationToken).ConfigureAwait(false))
                return ImportCommitResult.CancelledAt(unitId, state.OperationId, state.Checkpoint);
            cancellationToken.ThrowIfCancellationRequested();

            // Stale REUSE authority is a precondition for the whole domain checkpoint.
            // Reject it before activating any new Candidate so a stale duplicate decision cannot
            // leave unrelated DomainAuthorityCommitted mutations ahead of the durable checkpoint.
            foreach (var item in dedup)
            {
                if (item.CandidateMediaId is not Guid candidateId
                    || item.ReusedMediaId is not Guid reusedId
                    || !await _catalog.ImportWrites.ValidateReuseAuthorityAsync(
                            candidateId,
                            reusedId,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    return CommitBlockedResult(
                        unitId,
                        state,
                        [new VerificationBlocker(
                            item.ItemId,
                            "REUSE_AUTHORITY_STALE",
                            "The reviewed REUSE authority changed before domain commit; the Candidate was preserved.")]);
                }
            }

            foreach (var item in included)
            {
                await ActivateItemAsync(destinationProfileId, item, cancellationToken).ConfigureAwait(false);
                await importWrites.AdvanceItemCleanupStateAsync(
                    item.ItemId, SourceCleanupState.LibraryCommitted, cancellationToken).ConfigureAwait(false);
                await _catalog.MediaWrites.UpdateAllComponentsCleanupStateAsync(
                    item.CandidateMediaId!.Value, SourceCleanupState.LibraryCommitted, null, cancellationToken).ConfigureAwait(false);
                state.ActivatedMediaIds.Add(item.CandidateMediaId!.Value);
            }

            foreach (var item in dedup)
            {
                if (item.ReusedMediaId is not Guid reusedId || item.CandidateMediaId is not Guid candidateId)
                {
                    return CommitBlockedResult(
                        unitId,
                        state,
                        [new VerificationBlocker(
                            item.ItemId,
                            "REUSE_AUTHORITY_STALE",
                            "The reviewed REUSE decision no longer names both Candidate and managed Media authority.")]);
                }

                var reused = await _catalog.ImportWrites.CommitReuseAssociationAndRetirementAsync(
                        candidateId,
                        reusedId,
                        destinationProfileId,
                        unitId,
                        allowExistingOwnerOutsideDestination:
                            currentDraft.Destination.Kind == DestinationKind.SystemUnknown,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (!reused)
                {
                    return CommitBlockedResult(
                        unitId,
                        state,
                        [new VerificationBlocker(
                            item.ItemId,
                            "REUSE_AUTHORITY_STALE",
                            "The reused Media or destination authority changed before the atomic reuse commit; the Candidate was preserved.")]);
                }

                if (!state.ReusedMediaIds.Contains(reusedId))
                {
                    state.ReusedMediaIds.Add(reusedId);
                }

                if (!state.RetiredCandidateIds.Contains(candidateId))
                {
                    state.RetiredCandidateIds.Add(candidateId);
                }

                await importWrites.AdvanceItemCleanupStateAsync(
                    item.ItemId,
                    SourceCleanupState.LibraryCommitted,
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var item in skipped)
            {
                await _catalog.ImportWrites.RetireCandidateAsync(
                    item.CandidateMediaId!.Value,
                    MediaRetirementReason.Skipped,
                    cancellationToken).ConfigureAwait(false);
                state.RetiredCandidateIds.Add(item.CandidateMediaId!.Value);
            }

            // Canonical commit does not mutate published appearance. A rollback snapshot for untouched
            // presentation could overwrite a legitimate Profile edit after cancellation.
            // Final Verify appearance is applied only by ImportPublicationCoordinator.

            // Canonical commit sets the library checkpoint to DomainAuthorityCommitted but keeps the
            // import lifecycle in Preparing. Media preparation, verification, and publication all occur
            // after this point. The lifecycle advances to ReadyForVerification when required preparation
            // capabilities are complete, and to Committed only at final publication.
            await _catalog.ImportWrites.UpdateUnitStateAsync(unitId, ImportUnitState.Preparing, cancellationToken).ConfigureAwait(false);
            await importWrites.AdvanceCommitCheckpointAsync(
                unitId, state.OperationId, ImportCommitCheckpoint.DomainAuthorityCommitted, state.ToJson(), cancellationToken).ConfigureAwait(false);
            state.Checkpoint = ImportCommitCheckpoint.DomainAuthorityCommitted;
            NotifyCheckpoint(state.Checkpoint);
        }

        return new ImportCommitResult(
            unitId,
            state.OperationId,
            ImportCommitOutcome.Committed,
            state.Checkpoint,
            destinationProfileId,
            state.DestinationProfileCreated,
            state.ActivatedMediaIds,
            state.ReusedMediaIds,
            state.RetiredCandidateIds,
            [],
            []);
    }

    /// <summary>
    /// Post-canonical commit source cleanup. Called by <see cref="CommitAsync"/> after materialization.
    /// This is optional and non-blocking: domain authority is already durable.
    /// </summary>
    private async Task<ImportCommitResult> ExecuteSourceCleanupAsync(
        Guid unitId,
        ImportCommitResult canonicalCommitResult,
        CancellationToken cancellationToken)
    {
        var importWrites = _catalog.ImportWrites;
        var importReads = new ImportReads(_catalog);

        var op = await importWrites.ReadCommitOperationAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (op is null)
        {
            return canonicalCommitResult;
        }

        var state = CommitState.FromJson(op.CheckpointJson) with
        {
            OperationId = op.OperationId,
            Checkpoint = DbEnum.ParseImportCommitCheckpointOrDefault(op.Checkpoint),
        };

        var currentItems = await ReadAllUnitItemsAsync(unitId, cancellationToken).ConfigureAwait(false);
        var included = currentItems
            .Where(i => i.Disposition == ItemDisposition.Included
                && i.CandidateMediaId is not null
                && i.DuplicateDecision != DuplicateDecision.Reuse)
            .ToList();
        var dedup = currentItems
            .Where(i => i.DuplicateDecision == DuplicateDecision.Reuse)
            .ToList();

        if (state.Checkpoint < ImportCommitCheckpoint.SourceCleanupPending)
        {
            await importWrites.AdvanceCommitCheckpointAsync(
                unitId, state.OperationId, ImportCommitCheckpoint.SourceCleanupPending, state.ToJson(), cancellationToken).ConfigureAwait(false);
            state.Checkpoint = ImportCommitCheckpoint.SourceCleanupPending;
            NotifyCheckpoint(state.Checkpoint);
        }

        var attention = new List<Guid>();
        foreach (var item in included.Concat(dedup))
        {
            var cleanupItemId = item.ItemId;
            var obligation = await importWrites.ReadSourceCleanupObligationAsync(
                    cleanupItemId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (obligation is null)
            {
                attention.Add(cleanupItemId);
                continue;
            }

            if (obligation.SourceCleanupState is SourceCleanupState.SourceConsumed or SourceCleanupState.SourcePreserved)
            {
                continue;
            }

            try
            {
                var result = await _cleanupExecutor.ExecuteAsync(cleanupItemId, cancellationToken).ConfigureAwait(false);
                if (result.Status is not (StorageOperationStatus.Success or StorageOperationStatus.AlreadyCompleted))
                {
                    attention.Add(cleanupItemId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Domain authority is durable; cancellation stops optional cleanup only.
                attention.Add(cleanupItemId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                attention.Add(cleanupItemId);
            }
        }

        if (attention.Count == 0)
        {
            if (state.Checkpoint < ImportCommitCheckpoint.SourceCleanupComplete)
            {
                await importWrites.AdvanceCommitCheckpointAsync(
                    unitId, state.OperationId, ImportCommitCheckpoint.SourceCleanupComplete, state.ToJson(), cancellationToken).ConfigureAwait(false);
                state.Checkpoint = ImportCommitCheckpoint.SourceCleanupComplete;
                NotifyCheckpoint(state.Checkpoint);
            }

            await importWrites.AdvanceCommitCheckpointAsync(
                unitId, state.OperationId, ImportCommitCheckpoint.Terminal, state.ToJson(), cancellationToken).ConfigureAwait(false);
            state.Checkpoint = ImportCommitCheckpoint.Terminal;
            NotifyCheckpoint(state.Checkpoint);
        }

        var outcome = attention.Count == 0
            ? ImportCommitOutcome.Committed
            : ImportCommitOutcome.CommittedWithCleanupAttention;

        return new ImportCommitResult(
            unitId,
            state.OperationId,
            outcome,
            state.Checkpoint,
            canonicalCommitResult.DestinationProfileId,
            canonicalCommitResult.DestinationProfileCreated,
            canonicalCommitResult.ActivatedMediaIds,
            canonicalCommitResult.ReusedMediaIds,
            canonicalCommitResult.RetiredCandidateIds,
            attention,
            []);
    }

    private async Task<IReadOnlyList<ImportItemSummary>> ReadAllUnitItemsAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var operations = new VerificationOperations(_catalog, _timeProvider);
        var all = new List<ImportItemSummary>();
        var offset = 0;
        while (true)
        {
            var page = await operations.GetPagedItemsAsync(unitId, offset, pageSize, cancellationToken).ConfigureAwait(false);
            all.AddRange(page.Items);
            offset += page.Items.Count;
            if (page.Items.Count == 0 || offset >= page.TotalCount)
                return all;
        }
    }

    private async Task<bool> IsCancelledAsync(Guid unitId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state = 'CANCELLED' FROM import_units WHERE import_unit_id = $unitId;";
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private async Task PrepareDestinationAsync(
        Guid unitId,
        VerificationDraftV1 draft,
        CommitState state,
        CancellationToken cancellationToken)
    {
        var profileOps = new ProfileOperations(_catalog);
        var profileWrites = _catalog.ProfileWrites;

        if (state.DestinationProfileId is null
            || string.IsNullOrWhiteSpace(state.DestinationProfileToken))
        {
            switch (draft.Destination.Kind)
            {
                case DestinationKind.ExistingNormal:
                {
                    var profileId = state.DestinationProfileId ?? draft.Destination.ProfileId
                        ?? throw new InvalidOperationException("Existing NORMAL destination without a ProfileId.");
                    state.DestinationProfileId = profileId;
                    state.DestinationDisplayLabel = await ReadProfileLabelAsync(profileId, cancellationToken).ConfigureAwait(false);
                    state.DestinationProfileToken = await profileWrites.AssignStorageTokenAsync(
                        profileId, _tokenAllocator.CreateProfileCandidateProvider(profileId), cancellationToken).ConfigureAwait(false);
                    break;
                }

                case DestinationKind.SystemUnknown:
                {
                    var profileId = state.DestinationProfileId ?? Guid.NewGuid();
                    if (state.DestinationProfileId is null)
                    {
                        state.DestinationProfileId = profileId;
                        state.DestinationProfileCreated = true;
                        await _catalog.ImportWrites.AdvanceCommitCheckpointAsync(
                            unitId,
                            state.OperationId,
                            state.Checkpoint,
                            state.ToJson(),
                            cancellationToken).ConfigureAwait(false);
                    }

                    var created = await profileOps.CreateUnknownProfileAsync(
                        new CreateUnknownProfileCommand(profileId), cancellationToken).ConfigureAwait(false);
                    state.DestinationProfileCreated = true;
                    state.DestinationDisplayLabel = $"Unknown {ReadUnknownSequenceOrDefault(created)}";
                    state.DestinationProfileToken = await profileWrites.AssignStorageTokenAsync(
                        profileId, _tokenAllocator.CreateProfileCandidateProvider(profileId), cancellationToken).ConfigureAwait(false);
                    break;
                }

                default:
                {
                    var newProfile = draft.Destination.NewProfile
                        ?? throw new InvalidOperationException("New NORMAL destination without a NewProfileDraft.");
                    var profileId = state.DestinationProfileId ?? Guid.NewGuid();
                    var identityId = state.DestinationIdentityId ?? Guid.NewGuid();
                    if (state.DestinationProfileId is null || state.DestinationIdentityId is null)
                    {
                        state.DestinationProfileId = profileId;
                        state.DestinationIdentityId = identityId;
                        state.DestinationProfileCreated = true;
                        await _catalog.ImportWrites.AdvanceCommitCheckpointAsync(
                            unitId,
                            state.OperationId,
                            state.Checkpoint,
                            state.ToJson(),
                            cancellationToken).ConfigureAwait(false);
                    }

                    await profileOps.CreateNormalProfileAsync(
                        new CreateNormalProfileCommand(profileId, newProfile.DisplayName, identityId, Visibility: "DRAFT"),
                        cancellationToken).ConfigureAwait(false);

                    await ApplyNewProfileMetadataAsync(profileOps, profileId, newProfile, cancellationToken)
                        .ConfigureAwait(false);

                    state.DestinationProfileCreated = true;
                    state.DestinationDisplayLabel = newProfile.DisplayName;
                    state.DestinationProfileToken = await profileWrites.AssignStorageTokenAsync(
                        profileId, _tokenAllocator.CreateProfileCandidateProvider(profileId), cancellationToken).ConfigureAwait(false);
                    break;
                }
            }
        }

        state.DestinationDisplayLabel ??= await ReadProfileLabelAsync(
            state.DestinationProfileId!.Value,
            cancellationToken).ConfigureAwait(false);

        await SetUnitDestinationProfileAsync(
                unitId,
                draft.Destination.Kind,
                state.DestinationProfileId.Value,
                cancellationToken)
            .ConfigureAwait(false);

        await EnsureProfileFolderProvisionedAsync(
                unitId,
                state,
                draft.Destination.Kind,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EnsureProfileFolderProvisionedAsync(
        Guid unitId,
        CommitState state,
        DestinationKind? destinationKind,
        CancellationToken cancellationToken)
    {
        var profileId = state.DestinationProfileId
            ?? throw new InvalidOperationException("Destination provisioning has no Profile id.");
        var profileToken = new ProfileStorageToken(
            state.DestinationProfileToken
            ?? throw new InvalidOperationException("Destination provisioning has no Profile storage token."));
        var displayLabel = state.DestinationDisplayLabel ?? "Profile";

        if (string.IsNullOrWhiteSpace(state.DestinationProfileFolderRelativePath))
        {
            var alreadyPersisted = await ReadPersistedProfileFolderAsync(profileId, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(alreadyPersisted))
            {
                state.DestinationProfileFolderRelativePath = alreadyPersisted;
            }
            else
            {
                var occupied = await ReadOccupiedProfileFoldersAsync(profileId, cancellationToken)
                    .ConfigureAwait(false);
                var plan = _pathPlanner.AllocateProfilePlan(
                    profileId,
                    displayLabel,
                    profileToken,
                    candidate =>
                    {
                        var relative = candidate.ProfileFolderRelativePath.Replace('\\', '/');
                        if (occupied.Contains(relative))
                        {
                            return true;
                        }

                        var absolute = _vaultPaths.ResolveVaultRelativePath(relative);
                        return Directory.Exists(absolute);
                    });
                state.DestinationProfileFolderRelativePath = plan.ProfileFolderRelativePath;
            }
        }

        if (!state.DestinationProvisioningIntentPersisted)
        {
            state.DestinationProvisioningIntentPersisted = true;
            await _catalog.ImportWrites.AdvanceCommitCheckpointAsync(
                    unitId,
                    state.OperationId,
                    state.Checkpoint,
                    state.ToJson(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var folderRelative = state.DestinationProfileFolderRelativePath!;
        var absoluteFolder = _vaultPaths.ResolveVaultRelativePath(folderRelative);
        var provisioningFolder = absoluteFolder + $".{state.OperationId:N}.provisioning";

        var profileKind = destinationKind == DestinationKind.SystemUnknown
            ? ProfileKind.Unknown
            : ProfileKind.Normal;
        var manifest = ProfileManifestWriter.CreateManifest(
            profileId,
            profileKind,
            displayLabel,
            Path.GetFileName(folderRelative),
            state.DestinationIdentityId,
            _timeProvider.GetUtcNow());

        if (Directory.Exists(absoluteFolder))
        {
            var inspection = ProfileManifestWriter.InspectManifest(absoluteFolder);
            if (inspection.Status != ManifestStatus.Valid
                || inspection.Manifest is null
                || inspection.Manifest.ProfileId != profileId)
            {
                throw new IOException(
                    "The persisted Profile provisioning target exists without matching Profile authority.");
            }
        }
        else
        {
            Directory.CreateDirectory(provisioningFolder);

            var stagingInspection = ProfileManifestWriter.InspectManifest(provisioningFolder);
            if (stagingInspection.Status == ManifestStatus.Valid
                && stagingInspection.Manifest is not null
                && stagingInspection.Manifest.ProfileId != profileId)
            {
                throw new IOException(
                    "The operation-scoped Profile provisioning directory contains foreign authority.");
            }

            var writer = new ProfileManifestWriter(_vaultPaths);
            var manifestWrite = await writer.WriteManifestAsync(
                    provisioningFolder,
                    manifest,
                    state.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!manifestWrite.IsSuccess)
            {
                if (manifestWrite.Status == StorageOperationStatus.Cancelled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new IOException(
                    manifestWrite.SafeErrorDetail
                    ?? "The operation-scoped Profile manifest could not be durably written.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Directory.Move(provisioningFolder, absoluteFolder);
            }
            catch (IOException) when (Directory.Exists(absoluteFolder))
            {
                var winner = ProfileManifestWriter.InspectManifest(absoluteFolder);
                if (winner.Status != ManifestStatus.Valid
                    || winner.Manifest is null
                    || winner.Manifest.ProfileId != profileId)
                {
                    throw new IOException(
                        "The final Profile provisioning target was claimed by different authority.");
                }

                if (Directory.Exists(provisioningFolder))
                {
                    var ours = ProfileManifestWriter.InspectManifest(provisioningFolder);
                    if (ours.Status == ManifestStatus.Valid
                        && ours.Manifest is not null
                        && ours.Manifest.ProfileId == profileId)
                    {
                        Directory.Delete(provisioningFolder, recursive: true);
                    }
                }
            }

            var finalInspection = ProfileManifestWriter.InspectManifest(absoluteFolder);
            if (finalInspection.Status != ManifestStatus.Valid
                || finalInspection.Manifest is null
                || finalInspection.Manifest.ProfileId != profileId)
            {
                throw new IOException(
                    "Profile provisioning did not converge to the persisted destination authority.");
            }
        }

        await PersistProfileFolderPathAsync(profileId, folderRelative, cancellationToken)
            .ConfigureAwait(false);

        if (!state.DestinationProvisioningCompleted)
        {
            state.DestinationProvisioningCompleted = true;
            await _catalog.ImportWrites.AdvanceCommitCheckpointAsync(
                    unitId,
                    state.OperationId,
                    state.Checkpoint,
                    state.ToJson(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the category, tags, rating, favorite and overview the user chose while importing.
    ///
    /// A metadata failure must not lose the media: the Profile and its media are already durable at
    /// this point, so a rejected metadata write is reported and skipped rather than aborting the
    /// import the user has been waiting for. Nothing here touches Vault placement.
    /// </summary>
    private async Task ApplyNewProfileMetadataAsync(
        ProfileOperations profileOps,
        Guid profileId,
        NewProfileDraft newProfile,
        CancellationToken cancellationToken)
    {
        var hasMetadata = !string.IsNullOrWhiteSpace(newProfile.CategoryId)
            || (newProfile.TagIds?.Count ?? 0) > 0
            || newProfile.Rating.HasValue
            || newProfile.Favorite == true
            || !string.IsNullOrWhiteSpace(newProfile.Overview);

        if (!hasMetadata)
        {
            return;
        }

        try
        {
            // A freshly created Profile is at row version 1; read it back rather than assuming, so a
            // concurrent write is detected as a conflict instead of being silently overwritten.
            var current = await _catalog.ProfileReads
                .GetDetailAsync(profileId, cancellationToken)
                .ConfigureAwait(false);

            if (current is null)
            {
                return;
            }

            var result = await profileOps.UpdateProfileMetadataAsync(
                new UpdateProfileMetadataRequest(
                    profileId,
                    current.RowVersion,
                    CategoryId: newProfile.CategoryId,
                    TagIds: newProfile.TagIds,
                    Rating: newProfile.Rating,
                    IsFavorite: newProfile.Favorite ?? false,
                    Overview: newProfile.Overview),
                cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Import profile metadata was not applied: {0}", result.Error?.ErrorCode);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Import profile metadata could not be applied: {0}", exception.GetType().Name);
        }
    }

    private async Task<bool> LinkReusedMediaAsync(
        Guid destinationProfileId,
        Guid unitId,
        Guid assetId,
        DestinationKind? destinationKind,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await IsOwnedByAsync(destinationProfileId, assetId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (destinationKind == DestinationKind.SystemUnknown
                && await HasActiveOwnerOutsideProfileAsync(
                    destinationProfileId,
                    assetId,
                    cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            var media = new MediaOperations(_catalog);
            var result = await media.AddProfileMediaAssociationAsync(
                new ProfileMediaAssociationRequest(
                    destinationProfileId,
                    assetId,
                    ProfileMediaRelation.Manual,
                    $"import:{unitId:D}"),
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsOwnedByAsync(Guid profileId, Guid assetId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM profile_media WHERE profile_id = $p AND media_id = $a);";
        command.Parameters.AddWithValue("$p", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$a", DbGuid.Format(assetId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private async Task<bool> HasActiveOwnerOutsideProfileAsync(
        Guid destinationProfileId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM profile_media pa
                JOIN profiles p ON p.profile_id = pa.profile_id
                WHERE pa.media_id = $assetId
                  AND pa.relation_type = 'OWNER'
                  AND pa.profile_id <> $destinationProfileId
                  AND p.trashed_at_ms IS NULL
            );
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        command.Parameters.AddWithValue("$destinationProfileId", DbGuid.Format(destinationProfileId));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private async Task PlanItemPlacementAsync(
        Guid destinationProfileId,
        CommitState state,
        ImportItemSummary item,
        CancellationToken cancellationToken)
    {
        var assetId = item.CandidateMediaId!.Value;
        var assetToken = await _catalog.MediaWrites.GetStorageTokenAsync(
            assetId, cancellationToken).ConfigureAwait(false);

        var (sha256, byteLength) = await ReadCandidateFingerprintAsync(assetId, cancellationToken).ConfigureAwait(false);
        var mediaType = item.MediaType ?? MediaType.Image;
        var extension = Path.GetExtension(item.SourceFileName).TrimStart('.');
        if (string.IsNullOrEmpty(extension))
        {
            extension = "bin";
        }

        // The final deterministic target is persisted by MediaWrites before ManagedMoveExecutor performs I/O.
        var occupiedTargets = await ReadOccupiedPlacementTargetsAsync(assetId, cancellationToken).ConfigureAwait(false);
        var persistedTarget = await ReadPersistedPlacementTargetAsync(assetId, cancellationToken).ConfigureAwait(false);

        string relativeFolder;
        string plannedFileName;

        var components = mediaType == MediaType.Model
            ? await _catalog.MediaWrites.GetMediaComponentsAsync(assetId, cancellationToken).ConfigureAwait(false)
            : Array.Empty<MediaComponentRecord>();
        var isMultiFile = components.Count > 1 || (components.Count == 1 && components.Any(c => c.ComponentRole == ComponentRole.Dependency));

        if (persistedTarget is not null && persistedTarget.Contains('/'))
        {
            relativeFolder = persistedTarget[..persistedTarget.LastIndexOf('/')];
            plannedFileName = persistedTarget[(persistedTarget.LastIndexOf('/') + 1)..];
        }
        else if (mediaType == MediaType.Model && isMultiFile)
        {
            var primaryComp = components.FirstOrDefault(c => c.ComponentRole == ComponentRole.Primary) ?? components[0];
            var pkgPlan = _pathPlanner.AllocateModelPackagePlan(
                destinationProfileId,
                state.DestinationDisplayLabel ?? "Profile",
                new ProfileStorageToken(state.DestinationProfileToken!),
                assetId,
                new MediaStorageToken(assetToken),
                primaryComp.ComponentRelativePath,
                candidate =>
                {
                    var primary = candidate.PrimaryManagedRelativePath.Replace('\\', '/');
                    if (occupiedTargets.Contains(primary))
                    {
                        return true;
                    }

                    var packageDirectory = _vaultPaths.ResolveVaultRelativePath(
                        candidate.PackageDirectoryRelativePath);
                    return Directory.Exists(packageDirectory);
                });

            relativeFolder = pkgPlan.PackageDirectoryRelativePath;
            plannedFileName = pkgPlan.PrimaryFileName;
        }
        else
        {
            var plan = _pathPlanner.AllocateMediaPlan(
                destinationProfileId,
                state.DestinationDisplayLabel ?? "Profile",
                new ProfileStorageToken(state.DestinationProfileToken!),
                assetId,
                new MediaStorageToken(assetToken),
                mediaType,
                extension,
                candidate => occupiedTargets.Contains(candidate.ManagedFileRelativePath!)
                    || File.Exists(Path.Combine(
                        _vaultPaths.Root,
                        candidate.ManagedFileRelativePath!.Replace('/', Path.DirectorySeparatorChar))));

            relativeFolder = plan.ManagedFileRelativePath![..plan.ManagedFileRelativePath!.LastIndexOf('/')];
            plannedFileName = plan.ManagedFileName!;
        }

        await _catalog.MediaWrites.PersistCandidatePlacementPlanAsync(
            assetId,
            item.SourcePath,
            sha256,
            byteLength,
            relativeFolder,
            plannedFileName,
            state.OperationId,
            cancellationToken).ConfigureAwait(false);

        var sameVolume = IsSameVolumeAsVault(item.SourcePath);
        state.SameVolumeByItem[item.ItemId] = sameVolume;

        if (!sameVolume)
        {

            await _catalog.ImportWrites.UpdateItemSourceDetailsAsync(
                item.ItemId, byteLength, item.SourceLastWriteUtc?.ToUnixTimeMilliseconds() ?? 0, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ActivateItemAsync(
        Guid destinationProfileId,
        ImportItemSummary item,
        CancellationToken cancellationToken)
    {
        var assetId = item.CandidateMediaId!.Value;
        var (sha256, byteLength, currentRel, currentName, targetRel, targetName) =
            await ReadPlacedMediaAsync(assetId, cancellationToken).ConfigureAwait(false);

        await _catalog.MediaWrites.ActivateCandidateAsync(
            new CandidateActivationRequest(
                assetId,
                destinationProfileId,
                sha256,
                byteLength,
                currentRel,
                currentName,
                targetRel,
                targetName,
                ManagedPathState.None),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<VerificationBlocker>> DetectChangedSourcesAsync(
        IReadOnlyList<ImportItemSummary> items,
        CancellationToken cancellationToken)
    {
        var blockers = new List<VerificationBlocker>();
        foreach (var item in items)
        {
            if (item.Disposition != ItemDisposition.Included || item.CandidateMediaId is null)
            {
                continue;
            }

            if (item.DuplicateDecision == DuplicateDecision.Reuse)
            {
                continue;
            }

            var (expectedSha, _) = await ReadCandidateFingerprintAsync(item.CandidateMediaId.Value, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(expectedSha) || !File.Exists(item.SourcePath))
            {
                blockers.Add(new VerificationBlocker(item.ItemId, "SOURCE_CHANGED",
                    $"The source for '{item.SourceFileName}' is missing or unanalysed."));
                continue;
            }

            var sourceInfo = new FileInfo(item.SourcePath);
            var sourceLastWriteMs = new DateTimeOffset(sourceInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            if (item.SourceByteLength == sourceInfo.Length
                && item.SourceLastWriteUtc?.ToUnixTimeMilliseconds() == sourceLastWriteMs)
            {
                // HashMedia already established the fingerprint for this unchanged source. A
                // metadata change takes the full read below; ordinary successful imports do not
                // read the same large source a second time before materialization.
                continue;
            }

            var actualSha = await ComputeSha256Async(item.SourcePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                blockers.Add(new VerificationBlocker(item.ItemId, "SOURCE_CHANGED",
                    $"The source bytes for '{item.SourceFileName}' no longer match the reviewed Candidate."));
            }
        }

        return blockers;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private async Task<string?> ReadPersistedPlacementTargetAsync(Guid assetId, CancellationToken ct)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT target_managed_relative_path || '/' || target_managed_file_name " +
            "FROM media WHERE media_id = $id AND target_managed_relative_path IS NOT NULL AND target_managed_file_name IS NOT NULL;";
        command.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private async Task<HashSet<string>> ReadOccupiedPlacementTargetsAsync(Guid assetId, CancellationToken ct)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT target_managed_relative_path || '/' || target_managed_file_name " +
            "FROM media WHERE media_id <> $id AND target_managed_relative_path IS NOT NULL AND target_managed_file_name IS NOT NULL;";
        command.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            targets.Add(reader.GetString(0).Replace('\\', '/'));
        }

        return targets;
    }

    private async Task<string?> ReadPersistedProfileFolderAsync(Guid profileId, CancellationToken ct)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT current_managed_relative_path FROM profiles WHERE profile_id = $id;";
        command.Parameters.AddWithValue("$id", DbGuid.Format(profileId));
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private async Task<HashSet<string>> ReadOccupiedProfileFoldersAsync(Guid profileId, CancellationToken ct)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT current_managed_relative_path
            FROM profiles
            WHERE profile_id <> $id
              AND current_managed_relative_path IS NOT NULL
            UNION
            SELECT target_managed_relative_path
            FROM profiles
            WHERE profile_id <> $id
              AND target_managed_relative_path IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$id", DbGuid.Format(profileId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            paths.Add(reader.GetString(0).Replace('\\', '/'));
        }

        return paths;
    }

    private async Task<(string Sha256, long ByteLength)> ReadCandidateFingerprintAsync(Guid assetId, CancellationToken ct)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256, byte_length FROM media WHERE media_id = $id;";
        command.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return (string.Empty, 0);
        }

        return (reader.GetString(0), reader.GetInt64(1));
    }

    private async Task<(string Sha256, long ByteLength, string CurrentRel, string CurrentName, string TargetRel, string TargetName)>
        ReadPlacedMediaAsync(Guid assetId, CancellationToken ct)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sha256, byte_length,
                   current_managed_relative_path, current_managed_file_name,
                   target_managed_relative_path, target_managed_file_name
            FROM media WHERE media_id = $id;
            """;
        command.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        var target = (reader.GetString(4), reader.GetString(5));
        var current = (reader.IsDBNull(2) ? target.Item1 : reader.GetString(2),
                       reader.IsDBNull(3) ? target.Item2 : reader.GetString(3));
        return (reader.GetString(0), reader.GetInt64(1), current.Item1, current.Item2, target.Item1, target.Item2);
    }

    private async Task<string> ReadProfileLabelAsync(Guid profileId, CancellationToken ct)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT display_name, kind, unknown_sequence FROM profiles WHERE profile_id = $id;";
        command.Parameters.AddWithValue("$id", DbGuid.Format(profileId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return "Profile";
        }

        if (!reader.IsDBNull(0))
        {
            return reader.GetString(0);
        }

        return reader.IsDBNull(2) ? "Unknown" : $"Unknown {reader.GetInt64(2)}";
    }

    private async Task SetUnitDestinationProfileAsync(Guid unitId, DestinationKind? kind, Guid profileId, CancellationToken ct)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE import_units
            SET destination_kind = $kind, destination_profile_id = $profileId,
                updated_at_ms = $now, row_version = row_version + 1
            WHERE import_unit_id = $unitId;
            """;

        command.Parameters.AddWithValue("$kind", DbEnum.Format(kind ?? DestinationKind.NewNormal));
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task PersistProfileFolderPathAsync(
        Guid profileId,
        string profileFolderRelativePath,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE profiles
            SET current_managed_relative_path = $folder,
                path_state = 'NONE',
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE profile_id = $profileId
              AND (current_managed_relative_path IS NULL OR current_managed_relative_path = '');
            """;
        command.Parameters.AddWithValue("$folder", profileFolderRelativePath);
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsSameVolumeAsVault(string sourcePath)
    {
        try
        {

            var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            return sourceDirectory is not null
                && VolumeIdentity.CanUseSameVolumeMove(_volumeIdentity, sourceDirectory, _vaultPaths.ProfilesPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static long ReadUnknownSequenceOrDefault(UnknownProfileCreationResult created)
    {
        var property = created.GetType().GetProperty("UnknownSequence");
        return property?.GetValue(created) is long value ? value : 0;
    }

    private void NotifyCheckpoint(ImportCommitCheckpoint checkpoint) => _checkpointObserver?.Invoke(checkpoint);

    private ImportCommitResult CommitBlockedResult(Guid unitId, CommitState state, IReadOnlyList<VerificationBlocker> blockers) =>
        new(unitId, state.OperationId, ImportCommitOutcome.Blocked, state.Checkpoint,
            state.DestinationProfileId, state.DestinationProfileCreated,
            state.ActivatedMediaIds, state.ReusedMediaIds, state.RetiredCandidateIds, [], blockers);

    private sealed record CommitState
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        public int SchemaVersion { get; init; } = 1;

        public Guid OperationId { get; set; }

        public ImportCommitCheckpoint Checkpoint { get; set; } = ImportCommitCheckpoint.DecisionValidated;

        public Guid? DestinationProfileId { get; set; }

        public Guid? DestinationIdentityId { get; set; }

        public bool DestinationProfileCreated { get; set; }

        public string? DestinationDisplayLabel { get; set; }

        public string? DestinationProfileToken { get; set; }

        public string? DestinationProfileFolderRelativePath { get; set; }

        public bool DestinationProvisioningIntentPersisted { get; set; }

        public bool DestinationProvisioningCompleted { get; set; }

        public Dictionary<Guid, bool> SameVolumeByItem { get; init; } = [];

        public List<Guid> ActivatedMediaIds { get; init; } = [];

        public List<Guid> ReusedMediaIds { get; init; } = [];

        public List<Guid> RetiredCandidateIds { get; init; } = [];

        /// <summary>Previous cover asset ID before import applied its appearance override. Null if no previous cover.</summary>
        public Guid? PreviousCoverMediaId { get; set; }

        /// <summary>Previous banner asset ID before import applied its appearance override. Null if no previous banner.</summary>
        public Guid? PreviousBannerMediaId { get; set; }

        /// <summary>True if the pre-import appearance snapshot was captured (including null baseline).</summary>
        public bool AppearanceSnapshotCaptured { get; set; }

        /// <summary>Previous profile_appearance overrides_json before import applied its appearance. Null means default overrides.</summary>
        public string? PreviousAppearanceOverridesJson { get; set; }

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        public static CommitState FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            {
                return new CommitState();
            }

            try
            {
                return JsonSerializer.Deserialize<CommitState>(json, JsonOptions) ?? new CommitState();
            }
            catch (JsonException)
            {
                return new CommitState();
            }
        }
    }
}
