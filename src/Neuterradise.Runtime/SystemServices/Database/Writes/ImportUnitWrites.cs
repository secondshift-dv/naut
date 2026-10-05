using Neuterradise.App.Import;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.SystemServices.Database.Writes;

public sealed class ImportUnitWrites
{
    private const string ClearableHistoryPredicate = """
        (
            state = 'CANCELLED'
            OR (
                state IN ('COMMITTED','COMPLETED')
                AND NOT EXISTS (
                    SELECT 1 FROM import_items item
                    WHERE item.import_unit_id = import_units.import_unit_id
                      AND item.disposition = 'INVALID'
                )
                AND NOT EXISTS (
                    SELECT 1 FROM import_items item
                    WHERE item.import_unit_id = import_units.import_unit_id
                      AND item.source_cleanup_state IN ('SOURCE_DELETE_FAILED','SOURCE_CHANGED')
                )
                AND COALESCE(json_array_length(json_extract(
                    CASE WHEN json_valid(verification_draft_json) = 1
                         THEN verification_draft_json ELSE '{}' END,
                    '$.attentionItemIds')), 0) = 0
            )
        )
        """;

    private readonly CatalogDb _catalog;
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public ImportUnitWrites(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ImportCancellationOutcome> CancelUnitAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty) return new ImportCancellationOutcome(unitId, ImportCancellationResult.UnitNotFound);
        var state = await ReadCancellationStateAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (state is null) return new ImportCancellationOutcome(unitId, ImportCancellationResult.UnitNotFound);
        // Already cancelled: check rollback settlement.
        if (state.UnitState == ImportUnitState.Cancelled)
        {
            if (state.RollbackSettled == true)
                return new ImportCancellationOutcome(unitId, ImportCancellationResult.AlreadyCancelled);
            // Rollback not yet settled — caller must continue rollback operations.
            return new ImportCancellationOutcome(
                unitId,
                ImportCancellationResult.RollbackPending,
                MediasForTrashDisposition: GetExclusiveActiveMediaIds(state),
                DestinationProfileCreated: false);
        }
        // Truly published imports cannot be cancelled. The boundary is the ImportUnit lifecycle
        // state (Committed/Completed), not the DomainAuthorityCommitted storage checkpoint.
        // Canonical Media may already exist while media preparation and verification are still pre-publication.
        if (!state.UnitState.CanCancelUnpublished())
            return new ImportCancellationOutcome(unitId, ImportCancellationResult.RefusedAlreadyCommitted);
        var now = _timeProvider.GetUtcNow();
        var hasCommittedMediaDelta = state.LibraryCommitState.HasReachedDomainCommit();
        if (!await PersistCancellationAsync(unitId, now, hasCommittedMediaDelta, cancellationToken).ConfigureAwait(false))
            return new ImportCancellationOutcome(unitId, ImportCancellationResult.RefusedAlreadyCommitted);

        var jobWrites = new JobWrites(_catalog, _timeProvider);
        var importWrites = new ImportWrites(_catalog, _timeProvider);
        var cancelledJobs = 0;
        foreach (var scope in state.JobScopes)
        {
            cancelledJobs += string.Equals(scope.OwnerType, "Media", StringComparison.Ordinal)
                ? await jobWrites.CancelIdleMediaForImportUnitAsync(
                        unitId,
                        scope.OwnerId,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await jobWrites.CancelIdleAsync(
                        null,
                        scope.OwnerType,
                        scope.OwnerId,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        var runningJobs = await CountRunningScopedJobsAsync(state.JobScopes, cancellationToken).ConfigureAwait(false);

        // Pre-commit: retire unadmitted candidate media.
        var retiredCandidates = 0;
        foreach (var candidateMediaId in state.UnadmittedCandidateMediaIds)
        {
            await importWrites.RetireCandidateAsync(candidateMediaId, MediaRetirementReason.Cancelled, cancellationToken).ConfigureAwait(false);
            retiredCandidates++;
        }

        // Post-commit: read commit state for the caller to orchestrate external rollback
        // (Trash operations, Profile disposition). DB delta rollback is NOT done here —
        // the caller handles the full ordered rollback to ensure Trash plans are prepared
        // before OWNER relations are removed.
        List<Guid> mediasForTrash = [];
        var destinationProfileCreated = false;
        if (hasCommittedMediaDelta)
        {
            mediasForTrash = GetExclusiveActiveMediaIds(state);
            var commitOp = await importWrites.ReadCommitOperationAsync(unitId, cancellationToken).ConfigureAwait(false);
            if (commitOp?.CheckpointJson is { } json)
            {
                try
                {
                    var cs = System.Text.Json.JsonSerializer.Deserialize<CommitStateProbe>(json,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (cs is not null)
                    {
                        destinationProfileCreated = cs.DestinationProfileCreated == true;
                    }
                }
                catch
                {
                    // Best-effort: if JSON is malformed, treat as not created.
                }
            }
        }

        return new ImportCancellationOutcome(
            unitId,
            ImportCancellationResult.Cancelled,
            cancelledJobs,
            retiredCandidates,
            runningJobs,
            mediasForTrash,
            destinationProfileCreated);
    }

    /// <summary>Minimal probe for CommitState JSON to read rollback-relevant fields.</summary>
    private sealed record CommitStateProbe(
        bool? DestinationProfileCreated,
        Guid? DestinationProfileId,
        Guid? PreviousCoverMediaId,
        Guid? PreviousBannerMediaId,
        bool? AppearanceSnapshotCaptured,
        string? PreviousAppearanceOverridesJson);

    /// <summary>
    /// Pauses or resumes one import. The unit's own flag is what the activity surface and the import
    /// finalizer honour; the queued background work for its media (owned by the media, not by the
    /// unit) is paused or resumed with it. Pausing only jobs owned by "ImportUnit" changed nothing,
    /// which is why Pause appeared to do nothing.
    /// </summary>
    public async Task<bool> SetPausedAsync(Guid unitId, bool paused, CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty) return false;
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);
        var now = DbTime.Format(_timeProvider.GetUtcNow());

        int changed;
        // Pause remains valid after canonical Media commit while preparation/verification are unpublished.
        // The boundary is the ImportUnit lifecycle state (Committed/Completed/Committing), not the
        // DomainAuthorityCommitted storage checkpoint.
        await using (var unit = transaction.CreateCommand("""
            UPDATE import_units
            SET is_paused = $paused, updated_at_ms = $now, row_version = row_version + 1
            WHERE import_unit_id = $unitId
              AND is_paused <> $paused
              AND state NOT IN ('COMMITTING','COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION','CANCELLED','FAILED_TERMINAL');
            """))
        {
            unit.Parameters.AddWithValue("$paused", paused ? 1 : 0);
            unit.Parameters.AddWithValue("$now", now);
            unit.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            changed = await unit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Job state is subordinate to the unit transition. If the lifecycle predicate is stale,
        // the command is a complete no-op and cannot mutate Media jobs.
        if (changed > 0)
        {
            await using (var jobs = transaction.CreateCommand(paused
                ? """
                  UPDATE jobs
                  SET state = 'PAUSED',
                      row_version = row_version + 1
                  WHERE owner_type = 'Media'
                    AND state IN ('PENDING','RUNNABLE','FAILED_RETRYABLE')
                    AND owner_id IN (
                        SELECT candidate_media_id
                        FROM import_items
                        WHERE import_unit_id = $unitId
                          AND candidate_media_id IS NOT NULL
                        UNION
                        SELECT media_id
                        FROM import_media_interests
                        WHERE import_unit_id = $unitId
                    )
                    AND NOT EXISTS (
                        SELECT 1
                        FROM import_media_interests other
                        JOIN import_units consumer ON consumer.import_unit_id = other.import_unit_id
                        WHERE other.media_id = jobs.owner_id
                          AND other.import_unit_id <> $unitId
                          AND consumer.is_paused = 0
                          AND consumer.state NOT IN (
                              'COMMITTING','COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                              'CANCELLED','FAILED_TERMINAL'
                          )
                    );
                  """
                : """
                  UPDATE jobs
                  SET state = 'PENDING',
                      not_before_ms = NULL,
                      row_version = row_version + 1
                  WHERE owner_type = 'Media'
                    AND state = 'PAUSED'
                    AND owner_id IN (
                        SELECT candidate_media_id
                        FROM import_items
                        WHERE import_unit_id = $unitId
                          AND candidate_media_id IS NOT NULL
                        UNION
                        SELECT media_id
                        FROM import_media_interests
                        WHERE import_unit_id = $unitId
                    );
                  """))
            {
                jobs.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
                await jobs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

                transaction.QueueInvalidation(new CatalogInvalidation(
                    Guid.Empty,
                    [unitId],
                    CatalogInvalidationDomain.Import,
                    0));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed > 0;
    }

    /// <summary>
    /// Resumes a paused import: clears the durable paused flag and transitions Media-owned
    /// paused jobs back to PENDING. The scheduler's dependency evaluation moves eligible jobs to
    /// RUNNABLE when prerequisites are satisfied.
    ///
    /// Does not recreate the import graph. Preserves all completed canonical commit / media preparation work and checkpoints.
    /// </summary>
    public async Task<bool> ResumeUnitAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty) return false;
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);
        var now = DbTime.Format(_timeProvider.GetUtcNow());

        int changed;
        // Resume is valid whenever the import lifecycle permits it — same boundary as Pause.
        await using (var unit = transaction.CreateCommand("""
            UPDATE import_units
            SET is_paused = 0, updated_at_ms = $now, row_version = row_version + 1
            WHERE import_unit_id = $unitId
              AND is_paused = 1
              AND state NOT IN ('COMMITTING','COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION','CANCELLED','FAILED_TERMINAL');
            """))
        {
            unit.Parameters.AddWithValue("$now", now);
            unit.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            changed = await unit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (changed > 0)
        {
            await using (var jobs = transaction.CreateCommand("""
                UPDATE jobs
                SET state = 'PENDING',
                    not_before_ms = NULL,
                    row_version = row_version + 1
                WHERE owner_type = 'Media'
                  AND state = 'PAUSED'
                  AND owner_id IN (
                      SELECT candidate_media_id
                      FROM import_items
                      WHERE import_unit_id = $unitId
                        AND candidate_media_id IS NOT NULL
                      UNION
                      SELECT media_id
                      FROM import_media_interests
                      WHERE import_unit_id = $unitId
                  );
                """))
            {
                jobs.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
                await jobs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

                transaction.QueueInvalidation(new CatalogInvalidation(
                    Guid.Empty,
                    [unitId],
                    CatalogInvalidationDomain.Import,
                    0));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed > 0;
    }

    public async Task<bool> RetryUnitAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty) return false;

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        // Retry never rewinds the lifecycle. FAILED_RETRYABLE resumes in PREPARING;
        // the durable commit checkpoint decides which unfinished jobs must be re-armed.
        string? libraryCommitState;
        await using (var read = transaction.CreateCommand("""
            SELECT library_commit_state
            FROM import_units
            WHERE import_unit_id = $unitId
              AND state = 'FAILED_RETRYABLE';
            """))
        {
            read.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            libraryCommitState = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        if (libraryCommitState is null)
        {
            // Disposal of this uncommitted CatalogTransaction is the canonical rollback path.
            return false;
        }

        var checkpoint = DbEnum.ParseImportCommitCheckpointOrDefault(libraryCommitState);
        var targetState = ImportUnitState.Preparing;
        if (!ImportUnitState.FailedRetryable.CanTransitionTo(targetState))
            throw new CatalogInvariantException("FAILED_RETRYABLE must resume through PREPARING.");
        var now = DbTime.Format(_timeProvider.GetUtcNow());

        await using var command = transaction.CreateCommand("""
            UPDATE import_units
            SET state = $state,
                completed_at_ms = NULL,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_unit_id = $unitId
              AND state = 'FAILED_RETRYABLE';
            """);
        command.Parameters.AddWithValue("$state", DbEnum.Format(targetState));
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        command.Parameters.AddWithValue("$now", now);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (changed && !checkpoint.HasReachedDomainCommit())
        {
            // Retry the failed admission job in the same transaction as FAILED_RETRYABLE -> PREPARING.
            // JobWrites.ResumeAsync accepts PAUSED only, so failed admission work is re-armed here.
            await using var jobs = transaction.CreateCommand("""
                UPDATE jobs
                SET state = 'PENDING',
                    not_before_ms = NULL,
                    completed_at_ms = NULL,
                    error_code = NULL,
                    error_detail_safe = NULL,
                    row_version = row_version + 1
                WHERE kind = 'HashMedia'
                  AND owner_type = 'Media'
                  AND state = 'FAILED_RETRYABLE'
                  AND owner_id IN (
                      SELECT candidate_media_id
                      FROM import_items
                      WHERE import_unit_id = $unitId
                        AND candidate_media_id IS NOT NULL
                      UNION
                      SELECT media_id
                      FROM import_media_interests
                      WHERE import_unit_id = $unitId
                  );
                """);
            jobs.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await jobs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (changed)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [unitId],
                CatalogInvalidationDomain.Import,
                0));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// Hides one settled import from product history. This changes only presentation visibility;
    /// the import unit, items, media, profiles and managed bytes remain authoritative and intact.
    /// </summary>
    public async Task<bool> HideFinishedFromHistoryAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return false;
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);
        await using var command = transaction.CreateCommand(
            $"""
            UPDATE import_units
            SET hidden_from_history = 1,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_unit_id = $unitId
              AND hidden_from_history = 0
              AND {ClearableHistoryPredicate};
            """);
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (changed)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [unitId],
                CatalogInvalidationDomain.Import,
                0));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <summary>Hides every currently clearable finished import with one idempotent bulk write.</summary>
    public async Task<int> HideAllFinishedFromHistoryAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);
        await using var command = transaction.CreateCommand(
            $"""
            UPDATE import_units
            SET hidden_from_history = 1,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE hidden_from_history = 0
              AND {ClearableHistoryPredicate};
            """);
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed > 0)
        {
            transaction.QueueInvalidation(CatalogInvalidationDomain.Import);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// Post-commit DB delta rollback. Called AFTER external Trash operations have settled, so
    /// OWNER relations for exclusively-imported media are already handled by Trash.
    /// This method handles only the remaining DB-only cleanup:
    ///
    /// - Reused MANUAL associations with provenance "import:{unitId}".
    /// - Draft presentation overrides on import_items.
    /// - Previous Profile appearance restored for existing Profiles.
    ///
    /// Does NOT delete OWNER relations — those are handled by the Trash authority which
    /// requires them to be intact when preparing Trash plans.
    /// </summary>
    internal async Task RollbackCommittedMediaDeltaAsync(
        Guid unitId,
        Guid? destinationProfileId,
        CommitStateSnapshot? commitSnapshot,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. Remove reused MANUAL associations introduced by this import.
        //    Provenance "import:{unitId}" targets exactly the relation attributable to this
        //    ImportUnit. Does not delete pre-existing MANUAL/APPEARS relations or associations
        //    created by another import/user action.
        await using (var removeImportRelations = connection.CreateCommand())
        {
            removeImportRelations.CommandText =
                """
                DELETE FROM profile_media
                WHERE provenance_key = $provenanceKey;
                """;
            removeImportRelations.Parameters.AddWithValue("$provenanceKey", $"import:{unitId:D}");
            await removeImportRelations.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 2. Clear draft presentation overrides on import_items.
        await using (var clearAppearance = connection.CreateCommand())
        {
            clearAppearance.CommandText =
                """
                UPDATE import_items
                SET cover_media_id = NULL, banner_media_id = NULL,
                    row_version = row_version + 1
                WHERE import_unit_id = $unitId;
                """;
            clearAppearance.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await clearAppearance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 3. Restore previous Profile appearance for existing Profiles.
        //    Restores when AppearanceSnapshotCaptured is true, including null baseline
        //    (cover=NULL, banner=NULL before import).
        //    For new Profiles, the Profile will be trashed by the caller.
        if (destinationProfileId is { } profileId
            && commitSnapshot is { DestinationProfileCreated: false, AppearanceSnapshotCaptured: true })
        {
            await using (var restoreCover = connection.CreateCommand())
            {
                restoreCover.CommandText =
                    """
                    UPDATE profiles
                    SET cover_media_id = $coverId,
                        row_version = row_version + 1
                    WHERE profile_id = $profileId;
                    """;
                restoreCover.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                restoreCover.Parameters.AddWithValue("$coverId",
                    commitSnapshot.PreviousCoverMediaId is { } cc ? DbGuid.Format(cc) : DBNull.Value);
                await restoreCover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var restoreBanner = connection.CreateCommand())
            {
                restoreBanner.CommandText =
                    """
                    UPDATE profile_appearance
                    SET banner_media_asset_id = CASE
                            WHEN $bannerId IS NULL THEN NULL
                            ELSE (
                                SELECT media_asset_id
                                FROM media_assets
                                WHERE media_id = $bannerId
                                  AND role = 'HOVER'
                                  AND state = 'READY'
                                LIMIT 1
                            )
                        END,
                        row_version = row_version + 1
                    WHERE profile_id = $profileId;
                    """;
                restoreBanner.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                restoreBanner.Parameters.AddWithValue("$bannerId",
                    commitSnapshot.PreviousBannerMediaId is { } bb ? DbGuid.Format(bb) : DBNull.Value);
                await restoreBanner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Restore previous appearance overrides JSON (cover source kind, banner source kind,
            // video timestamps, clip start/duration, etc.).
            if (commitSnapshot.PreviousAppearanceOverridesJson is { } overridesJson)
            {
                await using (var restoreOverrides = connection.CreateCommand())
                {
                    restoreOverrides.CommandText =
                        """
                        UPDATE profile_appearance
                        SET overrides_json = $overridesJson,
                            updated_at_ms = $now,
                            row_version = row_version + 1
                        WHERE profile_id = $profileId;
                        """;
                    restoreOverrides.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                    restoreOverrides.Parameters.AddWithValue("$overridesJson", overridesJson);
                    restoreOverrides.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
                    await restoreOverrides.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    internal async Task<bool> TryReserveCancellationMediaRollbackAsync(
        Guid unitId,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty || assetId == Guid.Empty)
        {
            return false;
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using (var existing = transaction.CreateCommand(
            """
            SELECT 1
            FROM import_cancel_media_reservations
            WHERE import_unit_id = $unitId
              AND media_id = $assetId;
            """))
        {
            existing.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            existing.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
            if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
        }

        await using var reserve = transaction.CreateCommand(
            """
            INSERT INTO import_cancel_media_reservations(
                import_unit_id, media_id, created_at_ms)
            SELECT $unitId, $assetId, $now
            WHERE EXISTS (
                SELECT 1
                FROM import_units self
                JOIN import_items mine ON mine.import_unit_id = self.import_unit_id
                JOIN media asset ON asset.media_id = mine.candidate_media_id
                WHERE self.import_unit_id = $unitId
                  AND self.state = 'CANCELLED'
                  AND mine.candidate_media_id = $assetId
                  AND asset.state = 'ACTIVE'
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
            """);
        reserve.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        reserve.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        reserve.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        var reserved = await reserve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reserved;
    }

    /// <summary>Marks a cancelled import's rollback as fully settled.</summary>
    internal async Task MarkRollbackSettledAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using (var update = transaction.CreateCommand(
            """
            UPDATE import_units
            SET rollback_settled = 1,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_unit_id = $unitId
              AND state = 'CANCELLED';
            """))
        {
            update.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            update.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Cancellation keeps interest rows until running shared work has reached a safe boundary.
        // Once rollback is settled, the cancelled unit has no remaining scheduling interest.
        await using (var reprioritize = transaction.CreateCommand(
            """
            UPDATE jobs
            SET priority = COALESCE(
                    (
                        SELECT MAX(interest.desired_priority)
                        FROM import_media_interests interest
                        JOIN import_units consumer ON consumer.import_unit_id = interest.import_unit_id
                        WHERE interest.media_id = jobs.owner_id
                          AND interest.import_unit_id <> $unitId
                          AND consumer.is_paused = 0
                          AND consumer.state NOT IN (
                              'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                              'CANCELLED','FAILED_TERMINAL'
                          )
                    ),
                    $backgroundPriority
                ),
                row_version = row_version + 1
            WHERE owner_type = 'Media'
              AND owner_id IN (
                  SELECT media_id
                  FROM import_media_interests
                  WHERE import_unit_id = $unitId
              )
              AND state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE');
            """))
        {
            reprioritize.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            reprioritize.Parameters.AddWithValue(
                "$backgroundPriority",
                JobPriorityPolicy.DefaultPriority);
            await reprioritize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var releaseReservation = transaction.CreateCommand(
            "DELETE FROM import_cancel_media_reservations WHERE import_unit_id = $unitId;"))
        {
            releaseReservation.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await releaseReservation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var release = transaction.CreateCommand(
            "DELETE FROM import_media_interests WHERE import_unit_id = $unitId;"))
        {
            release.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the CommitState snapshot needed for appearance rollback.</summary>
    internal async Task<CommitStateSnapshot?> ReadCommitSnapshotAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var importWrites = new ImportWrites(_catalog, _timeProvider);
        var commitOp = await importWrites.ReadCommitOperationAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (commitOp?.CheckpointJson is not { } json) return null;
        try
        {
            var cs = System.Text.Json.JsonSerializer.Deserialize<CommitStateProbe>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return cs is null ? null : new CommitStateSnapshot(
                cs.DestinationProfileCreated == true,
                cs.DestinationProfileId,
                cs.PreviousCoverMediaId,
                cs.PreviousBannerMediaId,
                cs.AppearanceSnapshotCaptured == true,
                cs.PreviousAppearanceOverridesJson);
        }
        catch
        {
            return null;
        }
    }

    private static List<Guid> GetExclusiveActiveMediaIds(ImportCancellationState state) =>
        state.ActivatedMediaIds
            .Where(id => !state.ReusedMediaIds.Contains(id))
            .ToList();

    /// <summary>Snapshot of CommitState fields needed for appearance rollback.</summary>
    internal sealed record CommitStateSnapshot(
        bool DestinationProfileCreated,
        Guid? DestinationProfileId,
        Guid? PreviousCoverMediaId,
        Guid? PreviousBannerMediaId,
        bool AppearanceSnapshotCaptured,
        string? PreviousAppearanceOverridesJson);

    private async Task<ImportCancellationState?> ReadCancellationStateAsync(Guid unitId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string unitState;
        string libraryCommitState;
        Guid? destinationProfileId = null;
        bool? rollbackSettled = null;
        await using (var unitCommand = connection.CreateCommand())
        {
            unitCommand.CommandText = "SELECT state, library_commit_state, destination_profile_id, rollback_settled FROM import_units WHERE import_unit_id = $unitId;";
            unitCommand.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var unitReader = await unitCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await unitReader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            unitState = unitReader.GetString(0);
            libraryCommitState = unitReader.GetString(1);
            if (!unitReader.IsDBNull(2)) destinationProfileId = DbGuid.Parse(unitReader.GetString(2));
            if (!unitReader.IsDBNull(3)) rollbackSettled = unitReader.GetInt32(3) == 1;
        }
        var unadmitted = new List<Guid>();
        var activated = new List<Guid>();
        var reused = new List<Guid>();
        var scopes = new List<ImportJobScope>();
        await using (var itemCommand = connection.CreateCommand())
        {
            itemCommand.CommandText = """
                SELECT i.import_item_id, i.candidate_media_id, a.state, i.reused_media_id
                FROM import_items i LEFT JOIN media a ON a.media_id = i.candidate_media_id
                WHERE i.import_unit_id = $unitId ORDER BY i.import_item_id;
                """;
            itemCommand.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var itemReader = await itemCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await itemReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var itemId = DbGuid.Parse(itemReader.GetString(0));
                scopes.Add(new ImportJobScope("ImportItem", itemId));
                if (itemReader.IsDBNull(1)) continue;
                var candidateMediaId = DbGuid.Parse(itemReader.GetString(1));
                scopes.Add(new ImportJobScope("Media", candidateMediaId));
                if (!itemReader.IsDBNull(2))
                {
                    var assetState = DbEnum.ParseMediaState(itemReader.GetString(2));
                    if (assetState == MediaState.Candidate) unadmitted.Add(candidateMediaId);
                    if (assetState == MediaState.Active) activated.Add(candidateMediaId);
                }
                if (!itemReader.IsDBNull(3))
                {
                    var reusedMediaId = DbGuid.Parse(itemReader.GetString(3));
                    reused.Add(reusedMediaId);
                    scopes.Add(new ImportJobScope("Media", reusedMediaId));
                }
            }
        }
        scopes.Add(new ImportJobScope("ImportUnit", unitId));
        return new ImportCancellationState(
            DbEnum.ParseImportUnitState(unitState),
            DbEnum.ParseImportCommitCheckpointOrDefault(libraryCommitState),
            unadmitted,
            activated,
            reused,
            scopes,
            destinationProfileId,
            rollbackSettled);
    }

    private async Task<int> CountRunningScopedJobsAsync(IReadOnlyList<ImportJobScope> scopes, CancellationToken cancellationToken)
    {
        if (scopes.Count == 0) return 0;
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var running = 0;
        foreach (var scope in scopes)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM jobs WHERE owner_type = $ownerType AND owner_id = $ownerId AND state = 'RUNNING';";
            command.Parameters.AddWithValue("$ownerType", scope.OwnerType);
            command.Parameters.AddWithValue("$ownerId", DbGuid.Format(scope.OwnerId));
            running += Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }
        return running;
    }

    private async Task<bool> PersistCancellationAsync(Guid unitId, DateTimeOffset now, bool hasCommittedMediaDelta, CancellationToken cancellationToken)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);
        var formattedUnitId = DbGuid.Format(unitId);
        var formattedNow = DbTime.Format(now);
        int changed;
        // Cancel is accepted while the import is still pre-publication, even after canonical Media
        // bytes are authoritative in the Vault. The boundary is the ImportUnit lifecycle state
        // (Committed/Completed/Committing), not the DomainAuthorityCommitted storage checkpoint.
        // Post-commit cancel rolls back only the unpublished import delta; canonical bytes remain
        // governed by storage/trash authority. rollback_settled tracks external rollback completion.
        await using (var command = transaction.CreateCommand(hasCommittedMediaDelta
            ? """
              UPDATE import_units
              SET state = 'CANCELLED', completed_at_ms = COALESCE(completed_at_ms, $now),
                  rollback_settled = 0, updated_at_ms = $now, row_version = row_version + 1
              WHERE import_unit_id = $unitId
                AND state NOT IN ('COMMITTING','COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION','CANCELLED');
              """
            : """
              UPDATE import_units
              SET state = 'CANCELLED', completed_at_ms = COALESCE(completed_at_ms, $now),
                  updated_at_ms = $now, row_version = row_version + 1
              WHERE import_unit_id = $unitId
                AND state NOT IN ('COMMITTING','COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION','CANCELLED');
              """))
        {
            command.Parameters.AddWithValue("$unitId", formattedUnitId);
            command.Parameters.AddWithValue("$now", formattedNow);
            changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var settleOperation = transaction.CreateCommand("""
            UPDATE storage_operations
            SET state = 'CANCELLED', completed_at_ms = COALESCE(completed_at_ms, $now),
                error_code = NULL, error_detail_safe = NULL,
                updated_at_ms = $now, row_version = row_version + 1
            WHERE operation_id = (
                SELECT commit_operation_id FROM import_units WHERE import_unit_id = $unitId)
              AND state NOT IN ('COMPLETED','FAILED','CANCELLED')
              AND EXISTS (
                  SELECT 1 FROM import_units
                  WHERE import_unit_id = $unitId AND state = 'CANCELLED');
            """))
        {
            settleOperation.Parameters.AddWithValue("$unitId", formattedUnitId);
            settleOperation.Parameters.AddWithValue("$now", formattedNow);
            await settleOperation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (changed == 1)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [unitId],
                CatalogInvalidationDomain.Import,
                0));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1;
    }

    private sealed record ImportCancellationState(
        ImportUnitState UnitState,
        ImportCommitCheckpoint LibraryCommitState,
        IReadOnlyList<Guid> UnadmittedCandidateMediaIds,
        IReadOnlyList<Guid> ActivatedMediaIds,
        IReadOnlyList<Guid> ReusedMediaIds,
        IReadOnlyList<ImportJobScope> JobScopes,
        Guid? DestinationProfileId = null,
        bool? RollbackSettled = null);
    private sealed record ImportJobScope(string OwnerType, Guid OwnerId);
}
