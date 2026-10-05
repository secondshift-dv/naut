using System.Diagnostics;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Trash;

namespace Neuterradise.App.Import;

/// <summary>
/// Canonical authority for completing a cancellation that has already been durably persisted.
/// Every settlement attempt reconstructs remaining work from durable catalog/Trash/commit state;
/// it never depends on the in-memory outcome returned by the original Cancel invocation.
/// </summary>
internal sealed class ImportCancellationSettlement
{
    private readonly CatalogDb _catalog;
    private readonly ImportUnitWrites _unitWrites;
    private readonly SchedulerReads _schedulerReads;
    private readonly TrashCoordinator _trashCoordinator;

    public ImportCancellationSettlement(
        CatalogDb catalog,
        TrashCoordinator? trashCoordinator = null,
        TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _unitWrites = new ImportUnitWrites(catalog, timeProvider);
        _schedulerReads = new SchedulerReads(catalog);
        _trashCoordinator = trashCoordinator ?? CreateDefaultTrashCoordinator(catalog);
    }

    public async Task<ImportCancellationSettlementResult> TrySettleAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return new ImportCancellationSettlementResult(
                ImportCancellationSettlementStatus.Missing,
                "IMPORT_UNIT_NOT_FOUND");
        }

        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        var durable = await ReadDurableStateAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (durable is null)
        {
            return new ImportCancellationSettlementResult(
                ImportCancellationSettlementStatus.Missing,
                "IMPORT_UNIT_NOT_FOUND");
        }

        if (durable.State != ImportUnitState.Cancelled)
        {
            return new ImportCancellationSettlementResult(
                ImportCancellationSettlementStatus.NotRequired,
                "IMPORT_NOT_CANCELLED");
        }

        if (durable.RollbackSettled == true)
        {
            return new ImportCancellationSettlementResult(
                ImportCancellationSettlementStatus.Settled,
                "CANCEL_ROLLBACK_ALREADY_SETTLED");
        }

        // NULL means this cancellation never crossed canonical commit and therefore has no canonical
        // post-commit delta to dispose. It is already settled once its jobs are no longer running.
        if (durable.RollbackSettled is null)
        {
            var runningPreCommit = (await _schedulerReads
                .GetCancellableRunningJobIdsForImportUnitAsync(unitId, cancellationToken)
                .ConfigureAwait(false)).Count;
            return runningPreCommit == 0
                ? new ImportCancellationSettlementResult(
                    ImportCancellationSettlementStatus.NotRequired,
                    "CANCEL_PRE_COMMIT_SETTLED")
                : new ImportCancellationSettlementResult(
                    ImportCancellationSettlementStatus.PendingSafeBoundary,
                    "CANCEL_RUNNING_JOBS_NOT_SETTLED");
        }

        // rollback_settled = 0 is a durable promise that post-commit rollback still needs
        // convergence. Never mutate canonical storage while a handler is still RUNNING.
        var running = (await _schedulerReads
            .GetCancellableRunningJobIdsForImportUnitAsync(unitId, cancellationToken)
            .ConfigureAwait(false)).Count;
        if (running > 0)
        {
            return new ImportCancellationSettlementResult(
                ImportCancellationSettlementStatus.PendingSafeBoundary,
                "CANCEL_RUNNING_JOBS_NOT_SETTLED");
        }

        var snapshot = await _unitWrites.ReadCommitSnapshotAsync(unitId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null || snapshot.DestinationProfileId is null || snapshot.DestinationProfileId == Guid.Empty)
        {
            return new ImportCancellationSettlementResult(
                ImportCancellationSettlementStatus.PendingRetry,
                "CANCEL_COMMIT_SNAPSHOT_UNAVAILABLE");
        }

        try
        {
            // Media Trash is always settled before DB delta cleanup. Re-entry only returns media
            // that are still ACTIVE, so already-completed Trash transitions are naturally skipped.
            foreach (var assetId in await ReadExclusiveActiveMediaIdsAsync(unitId, cancellationToken).ConfigureAwait(false))
            {
                var reserved = await _unitWrites.TryReserveCancellationMediaRollbackAsync(
                        unitId,
                        assetId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!reserved)
                {
                    // Another live import or durable published relation won the race. This Media is
                    // preserved; cancellation only removes this unit's unpublished delta.
                    continue;
                }

                var plan = await _trashCoordinator.PrepareMediaTrashAsync(assetId, cancellationToken)
                    .ConfigureAwait(false);
                if (!plan.IsSuccess || plan.Value is null)
                {
                    return new ImportCancellationSettlementResult(
                        ImportCancellationSettlementStatus.PendingRetry,
                        "CANCEL_ASSET_TRASH_PREPARE_PENDING");
                }

                var executed = await _trashCoordinator.ExecuteMediaTrashAsync(
                        plan.Value,
                        unitId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!executed.IsSuccess)
                {
                    return new ImportCancellationSettlementResult(
                        ImportCancellationSettlementStatus.PendingRetry,
                        "CANCEL_ASSET_TRASH_EXECUTE_PENDING");
                }
            }

            // DB-only delta cleanup is idempotent and reads the durable commit snapshot on every
            // attempt, including null-baseline appearance restoration.
            await _unitWrites.RollbackCommittedMediaDeltaAsync(
                    unitId,
                    snapshot.DestinationProfileId,
                    snapshot,
                    cancellationToken)
                .ConfigureAwait(false);

            if (snapshot.DestinationProfileCreated)
            {
                var profileSettled = await SettleDraftProfileTrashAsync(
                        snapshot.DestinationProfileId.Value,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!profileSettled)
                {
                    return new ImportCancellationSettlementResult(
                        ImportCancellationSettlementStatus.PendingRetry,
                        "CANCEL_PROFILE_TRASH_PENDING");
                }
            }

            // This is the only success path that writes rollback_settled=1.
            await _unitWrites.MarkRollbackSettledAsync(unitId, cancellationToken).ConfigureAwait(false);
            return new ImportCancellationSettlementResult(
                ImportCancellationSettlementStatus.Settled,
                "CANCEL_ROLLBACK_SETTLED");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "Import cancel settlement remains pending for unit {0:D}: {1}",
                unitId,
                exception.GetType().Name);
            return new ImportCancellationSettlementResult(
                ImportCancellationSettlementStatus.PendingRetry,
                "CANCEL_ROLLBACK_RETRY_REQUIRED");
        }
    }

    private async Task<bool> SettleDraftProfileTrashAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        if (await IsProfileTrashedAsync(profileId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var plan = await _trashCoordinator.PrepareProfileTrashAsync(profileId, cancellationToken)
            .ConfigureAwait(false);
        if (!plan.IsSuccess || plan.Value is null)
        {
            // A concurrent/replayed completion is accepted only when durable profile authority
            // proves that the Profile is already in Trash.
            return await IsProfileTrashedAsync(profileId, cancellationToken).ConfigureAwait(false);
        }

        var dispositions = plan.Value.OwnedActiveMedias
            .Select(asset => new ProfileOwnedMediaDisposition(
                asset.MediaId,
                ProfileOwnedMediaDispositionKind.TrashMedia,
                NewOwnerProfileId: null))
            .ToList();

        var result = await _trashCoordinator.CommitProfileTrashAsync(
                plan.Value,
                dispositions,
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            || await IsProfileTrashedAsync(profileId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Guid>> ReadExclusiveActiveMediaIdsAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        var assetIds = new List<Guid>();
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT item.candidate_media_id
            FROM import_items item
            JOIN media asset ON asset.media_id = item.candidate_media_id
            JOIN import_units self ON self.import_unit_id = item.import_unit_id
            WHERE item.import_unit_id = $unitId
              AND item.candidate_media_id IS NOT NULL
              AND asset.state = 'ACTIVE'
              AND NOT EXISTS (
                  SELECT 1
                  FROM import_items other
                  JOIN import_units consumer ON consumer.import_unit_id = other.import_unit_id
                  WHERE other.import_unit_id <> $unitId
                    AND (other.candidate_media_id = item.candidate_media_id
                         OR other.reused_media_id = item.candidate_media_id)
                    AND consumer.state NOT IN (
                        'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                        'CANCELLED','FAILED_TERMINAL'
                    )
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM import_media_interests interest
                  JOIN import_units consumer ON consumer.import_unit_id = interest.import_unit_id
                  WHERE interest.media_id = item.candidate_media_id
                    AND interest.import_unit_id <> $unitId
                    AND consumer.state NOT IN (
                        'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                        'CANCELLED','FAILED_TERMINAL'
                    )
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM profile_media relation
                  WHERE relation.media_id = item.candidate_media_id
                    AND (
                        relation.publication_import_unit_id IS NULL
                        OR relation.publication_import_unit_id <> $unitId
                    )
              )
            ORDER BY item.candidate_media_id;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            assetIds.Add(DbGuid.Parse(reader.GetString(0)));
        }

        return assetIds;
    }

    private async Task<bool> IsProfileTrashedAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CASE WHEN trashed_at_ms IS NOT NULL THEN 1 ELSE 0 END
            FROM profiles
            WHERE profile_id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is not null
            && value is not DBNull
            && Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private async Task<DurableCancellationState?> ReadDurableStateAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT state, rollback_settled
            FROM import_units
            WHERE import_unit_id = $unitId;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DurableCancellationState(
            DbEnum.ParseImportUnitState(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetInt32(1) == 1);
    }

    private static TrashCoordinator CreateDefaultTrashCoordinator(CatalogDb catalog)
    {
        var volume = new WindowsVolumeIdentityProvider();
        var verifier = new ManagedFileVerifier();
        var moveExecutor = new ManagedMoveExecutor(
            catalog.Paths,
            volume,
            verifier,
            new MediaWrites(catalog));
        return new TrashCoordinator(catalog, moveExecutor, new MediaOperations(catalog));
    }

    private sealed record DurableCancellationState(
        ImportUnitState State,
        bool? RollbackSettled);
}

internal enum ImportCancellationSettlementStatus
{
    Missing,
    NotRequired,
    Settled,
    PendingSafeBoundary,
    PendingRetry,
}

internal sealed record ImportCancellationSettlementResult(
    ImportCancellationSettlementStatus Status,
    string Code)
{
    public bool IsSettled => Status is ImportCancellationSettlementStatus.Settled
        or ImportCancellationSettlementStatus.NotRequired;
}
