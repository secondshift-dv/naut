using Neuterradise.App.Import.Preparation;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.Trash;

namespace Neuterradise.App.Import;

/// <summary>
/// Single authority for per-import-unit lifecycle controls: Pause, Start/Resume, Prioritize,
/// Cancel. Every user control action routes through here so that UI, scheduler, recovery, and
/// commit coordinator never independently own import lifecycle transitions.
///
/// The full control path is:
/// <code>
/// UI command → ImportUnitControlAuthority → durable import state → scheduler jobs
///   → running handler cancellation → canonical commit / media preparation checkpoint state → recovery → UI read model
/// </code>
///
/// Import membership is resolved through import_items → candidate_media_id → jobs.owner_id
/// (Media-owned jobs). Never uses global scheduler pause for an individual import.
/// </summary>
public sealed class ImportUnitControlAuthority
{
    private readonly CatalogDb _catalog;
    private readonly ImportUnitWrites _unitWrites;
    private readonly ImportPriorityOperations _priority;
    private readonly ImportFinalizer? _finalizer;
    private readonly ImportPreparationCoordinator? _preparationCoordinator;
    private readonly JobCancellationOperations _jobCancellation;
    private readonly SchedulerReads _schedulerReads;
    private readonly ImportCancellationSettlement _settlement;

    /// <summary>Maximum time to wait for running jobs to leave RUNNING state during Cancel.</summary>
    private static readonly TimeSpan CancelSettleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CancelSettlePoll = TimeSpan.FromMilliseconds(250);

    public ImportUnitControlAuthority(
        CatalogDb catalog,
        ImportFinalizer? finalizer = null,
        TimeProvider? timeProvider = null,
        JobCancellationOperations? jobCancellation = null,
        TrashCoordinator? trashCoordinator = null,
        ImportUnitWrites? unitWrites = null,
        ImportPreparationCoordinator? preparationCoordinator = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _unitWrites = unitWrites ?? new ImportUnitWrites(catalog, timeProvider);
        _priority = new ImportPriorityOperations(catalog);
        _finalizer = finalizer;
        _preparationCoordinator = preparationCoordinator;
        _jobCancellation = jobCancellation ?? new JobCancellationOperations(catalog);
        _schedulerReads = new SchedulerReads(catalog);
        _settlement = new ImportCancellationSettlement(catalog, trashCoordinator, timeProvider);
    }

    /// <summary>
    /// Pauses one import: persists the durable paused flag, transitions idle jobs to PAUSED,
    /// and signals currently running jobs to stop at a safe boundary. Running handlers exit
    /// through their cancellation token and settle as PAUSED (not failed, not cancelled).
    ///
    /// Preserves all completed work, checkpoints, and canonical authority. Idempotent.
    /// </summary>
    public async Task<bool> PauseAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(PauseAsync));
        if (unitId == Guid.Empty)
        {
            return false;
        }

        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        var paused = await _unitWrites.SetPausedAsync(unitId, paused: true, cancellationToken)
            .ConfigureAwait(false);
        if (!paused)
        {
            return false;
        }

        await _priority.ClearIfFocusedAsync(unitId, cancellationToken).ConfigureAwait(false);

        await SignalRunningJobsAsync(unitId, JobControlIntent.Pause, cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Starts or resumes one import. Unsets the durable paused flag, makes this import the focused
    /// import (P1/90), demotes the previous focused import to P3/50, and wakes the finalizer so
    /// eligible work begins immediately.
    ///
    /// Resume uses persisted state — does not recreate the import graph, does not repeat completed
    /// canonical commit or media preparation work.
    /// </summary>
    public async Task<bool> StartAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(StartAsync));
        if (unitId == Guid.Empty)
        {
            return false;
        }

        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        await _unitWrites.ResumeUnitAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (!await _priority.FocusAsync(unitId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _finalizer?.Wake(unitId);
        return true;
    }

    /// <summary>
    /// Makes a running but non-focused import the focused import. Reuses Task E P1=90 authority.
    /// Previous focus falls to P3=50. Repeated Prioritize on the already-focused import is idempotent.
    /// </summary>
    public async Task PrioritizeAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(PrioritizeAsync));
        if (unitId == Guid.Empty)
        {
            return;
        }

        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        await _priority.FocusAsync(unitId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retries one FAILED_RETRYABLE import under command admission and the per-unit mutation lease.
    /// </summary>
    public async Task<bool> RetryAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(RetryAsync));
        if (unitId == Guid.Empty)
        {
            return false;
        }

        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        if (_preparationCoordinator is not null)
        {
            var readiness = await _preparationCoordinator
                .ResolveUnitStateAsync(unitId, cancellationToken)
                .ConfigureAwait(false);
            if (readiness.IsReady)
            {
                _finalizer?.Wake(unitId);
                return true;
            }
        }

        if (!await _unitWrites.RetryUnitAsync(unitId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        JobSignals.Raise();

        if (_preparationCoordinator is not null)
        {
            await _preparationCoordinator
                .PrepareUnitAsync(unitId, cancellationToken)
                .ConfigureAwait(false);
        }

        _finalizer?.Wake(unitId);
        return true;
    }

    public async Task<bool> ClearHistoryItemAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return false;
        }

        // A terminal pre-commit failure still owns candidate/staging state. "Clear" must therefore
        // converge it through the normal cancellation authority before hiding the history row; simply
        // hiding FAILED_TERMINAL would strand durable import-owned bytes and recovery provenance.
        var summary = await _catalog.ImportReads
            .GetUnitSummaryAsync(unitId, cancellationToken)
            .ConfigureAwait(false);
        if (summary is null)
        {
            return false;
        }

        if (summary.State == ImportUnitState.FailedTerminal)
        {
            var cancellation = await CancelAsync(unitId, cancellationToken).ConfigureAwait(false);
            if (!cancellation.WasCancelled || cancellation.Result == ImportCancellationResult.RollbackPending)
            {
                return false;
            }
        }

        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(ClearHistoryItemAsync));
        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);
        return await _unitWrites
            .HideFinishedFromHistoryAsync(unitId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        // FAILED_TERMINAL cannot be bulk-hidden directly because it can still own unpublished
        // candidate/staging state. Converge those units through the same ordered rollback authority
        // used by the per-row Clear action, then bulk-hide the already-safe finished states.
        var failedTerminalUnitIds = await _catalog.ImportReads
            .ListVisibleFailedTerminalUnitIdsAsync(cancellationToken)
            .ConfigureAwait(false);

        var cleared = 0;
        foreach (var unitId in failedTerminalUnitIds)
        {
            if (await ClearHistoryItemAsync(unitId, cancellationToken).ConfigureAwait(false))
            {
                cleared++;
            }
        }

        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(ClearHistoryAsync));
        cleared += await _unitWrites
            .HideAllFinishedFromHistoryAsync(cancellationToken)
            .ConfigureAwait(false);
        return cleared;
    }

    /// <summary>
    /// Cancels one unpublished import. Cancellation is persisted first so no new import work may
    /// become eligible. Running handlers then receive the shared scheduler CTS and must leave RUNNING
    /// before canonical storage rollback is allowed. A safe-boundary timeout leaves rollback pending;
    /// it never authorizes destructive rollback while a handler may still be using the asset.
    /// </summary>
    public async Task<ImportCancellationOutcome> CancelAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(CancelAsync));
        if (unitId == Guid.Empty)
        {
            return new ImportCancellationOutcome(unitId, ImportCancellationResult.UnitNotFound);
        }

        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        // Persist CANCELLED first. This cancels idle work and makes the durable lifecycle authority
        // block future import scheduling before we wait for running handlers.
        var outcome = await _unitWrites.CancelUnitAsync(unitId, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.WasCancelled || outcome.Result == ImportCancellationResult.AlreadyCancelled)
        {
            return outcome;
        }

        await _priority.ClearIfFocusedAsync(unitId, cancellationToken).ConfigureAwait(false);

        // Running jobs share the scheduler's JobCancellationOperations instance in production.
        await SignalRunningJobsAsync(unitId, JobControlIntent.Cancel, cancellationToken)
            .ConfigureAwait(false);

        var runningSettlement = await WaitForRunningJobsToSettleAsync(unitId, cancellationToken)
            .ConfigureAwait(false);
        if (runningSettlement != RunningJobSettlement.Settled)
        {
            return outcome with { Result = ImportCancellationResult.RollbackPending };
        }

        // Settlement reconstructs every remaining step from durable catalog/Trash/commit authority.
        // The original in-memory outcome is deliberately not used to decide what rollback remains.
        var settlement = await _settlement.TrySettleAsync(unitId, cancellationToken)
            .ConfigureAwait(false);

        return settlement.Status switch
        {
            ImportCancellationSettlementStatus.Settled =>
                outcome with { Result = ImportCancellationResult.Cancelled },

            ImportCancellationSettlementStatus.NotRequired
                when outcome.Result == ImportCancellationResult.RollbackPending =>
                outcome with { Result = ImportCancellationResult.AlreadyCancelled },

            ImportCancellationSettlementStatus.NotRequired =>
                outcome with { Result = ImportCancellationResult.Cancelled },

            ImportCancellationSettlementStatus.Missing =>
                new ImportCancellationOutcome(unitId, ImportCancellationResult.UnitNotFound),

            _ => outcome with { Result = ImportCancellationResult.RollbackPending },
        };
    }

    /// <summary>
    /// Waits for all currently RUNNING jobs belonging to one ImportUnit to leave RUNNING.
    /// Caller cancellation propagates. The internal timeout is an explicit non-success result and
    /// therefore cannot accidentally authorize Trash/profile mutation.
    /// </summary>
    private async Task<RunningJobSettlement> WaitForRunningJobsToSettleAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(CancelSettleTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            while (true)
            {
                var runningCount = (await _schedulerReads
                    .GetCancellableRunningJobIdsForImportUnitAsync(unitId, linked.Token)
                    .ConfigureAwait(false)).Count;

                if (runningCount == 0)
                {
                    return RunningJobSettlement.Settled;
                }

                await Task.Delay(CancelSettlePoll, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return RunningJobSettlement.TimedOut;
        }
    }

    /// <summary>
    /// Resolves all currently RUNNING jobs belonging to one ImportUnit and signals them to stop
    /// at a safe boundary using the given control intent. Import membership is resolved through
    /// import_items → candidate_media_id → jobs.owner_id (Media-owned jobs).
    /// </summary>
    private async Task SignalRunningJobsAsync(
        Guid unitId,
        JobControlIntent intent,
        CancellationToken cancellationToken)
    {
        var runningJobIds = intent == JobControlIntent.Cancel
            ? await _schedulerReads
                .GetCancellableRunningJobIdsForImportUnitAsync(unitId, cancellationToken)
                .ConfigureAwait(false)
            : await _schedulerReads
                .GetRunningJobIdsForImportUnitAsync(unitId, cancellationToken)
                .ConfigureAwait(false);

        foreach (var jobId in runningJobIds)
        {
            _jobCancellation.SetIntent(jobId, intent);
            await _jobCancellation.RequestCancelAsync(jobId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private enum RunningJobSettlement
    {
        Settled,
        TimedOut,
    }
}
