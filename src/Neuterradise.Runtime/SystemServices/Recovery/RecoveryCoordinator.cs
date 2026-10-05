using Microsoft.Data.Sqlite;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Import.Preparation;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Recovery;

public sealed class RecoveryCoordinator
{
    private readonly CatalogDb _catalog;
    private readonly ImportRecovery _importRecovery;
    private readonly StorageRecovery _storageRecovery;
    private readonly TrashRecovery _trashRecovery;
    private readonly JobRecovery _jobRecovery;
    private readonly StructuredDiagnostics? _diagnostics;

    public RecoveryCoordinator(
        CatalogDb catalog,
        StructuredDiagnostics? diagnostics = null,
        IVolumeIdentityProvider? volumeIdentityProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _diagnostics = diagnostics;
        volumeIdentityProvider ??= new WindowsVolumeIdentityProvider();
        var verifier = new ManagedFileVerifier();
        var assetWrites = new MediaWrites(catalog);
        var moveExecutor = new ManagedMoveExecutor(catalog.Paths, volumeIdentityProvider, verifier, assetWrites);
        var commitCoordinator = new ImportCommitCoordinator(
            catalog,
            new VerificationValidator(catalog),
            moveExecutor,
            new SourceCleanupExecutor(new ImportWrites(catalog)),
            volumeIdentityProvider,
            catalog.Paths);
        _storageRecovery = new StorageRecovery(
            commitCoordinator,
            catalog,
            new PathReconciler(catalog.Paths, catalog.ProfileWrites, assetWrites, verifier, volumeIdentityProvider));
        _importRecovery = new ImportRecovery(catalog, new ImportPreparationCoordinator(catalog), _storageRecovery);
        _trashRecovery = new TrashRecovery(catalog, moveExecutor);
        _jobRecovery = new JobRecovery(catalog);
    }

    public async Task<RecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var findings = new List<RecoveryFinding>();

        // Settle stale RUNNING rows before import cancellation recovery. No worker from the prior
        // process can still be alive. The additional cancelled-scope pass below closes owner types
        // that the generic running reconciliation cannot infer from Media membership alone.
        await RunStageAsync(
            "JOB_INTERRUPTION",
            () => ReconcileInterruptedJobsAsync(cancellationToken),
            findings,
            cancellationToken).ConfigureAwait(false);

        await RunStageAsync("IMPORT", () => _importRecovery.RecoverAsync(cancellationToken), findings, cancellationToken).ConfigureAwait(false);
        await RunStageAsync("STORAGE", () => _storageRecovery.RecoverPathReconciliationAsync(cancellationToken), findings, cancellationToken).ConfigureAwait(false);
        await RunStageAsync("TRASH", () => _trashRecovery.RecoverAsync(cancellationToken), findings, cancellationToken).ConfigureAwait(false);
        await RunStageAsync("MEDIA_ASSETS", () => _storageRecovery.RecoverMediaAssetsAsync(cancellationToken), findings, cancellationToken).ConfigureAwait(false);
        await RunStageAsync("STAGING", () => _importRecovery.RecoverStagingOwnershipAsync(cancellationToken), findings, cancellationToken).ConfigureAwait(false);
        await RunStageAsync("JOBS", () => _jobRecovery.RecoverAsync(cancellationToken), findings, cancellationToken).ConfigureAwait(false);
        return findings.Count == 0 ? RecoveryResult.NoAction : new RecoveryResult(findings.AsReadOnly());
    }

    private async Task<IReadOnlyList<RecoveryFinding>> ReconcileInterruptedJobsAsync(
        CancellationToken cancellationToken)
    {
        await new JobWrites(_catalog)
            .ReconcileInterruptedRunningJobsAsync(cancellationToken)
            .ConfigureAwait(false);

        // The generic reconciliation maps remaining RUNNING rows to PENDING. For a cancelled import,
        // no owner type may become runnable again after restart, so converge every known import scope
        // (ImportUnit, ImportItem, Media) to intentional CANCELLED before rollback recovery starts.
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE jobs
            SET state = 'CANCELLED',
                not_before_ms = NULL,
                error_code = NULL,
                error_detail_safe = NULL,
                row_version = row_version + 1
            WHERE state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE')
              AND (
                    (owner_type = 'ImportUnit'
                     AND owner_id IN (
                         SELECT import_unit_id FROM import_units WHERE state = 'CANCELLED'
                     ))
                 OR (owner_type = 'ImportItem'
                     AND owner_id IN (
                         SELECT i.import_item_id
                         FROM import_items i
                         JOIN import_units u ON u.import_unit_id = i.import_unit_id
                         WHERE u.state = 'CANCELLED'
                     ))
                 OR (owner_type = 'Media'
                     AND owner_id IN (
                         SELECT DISTINCT i.candidate_media_id
                         FROM import_items i
                         JOIN import_units u ON u.import_unit_id = i.import_unit_id
                         WHERE u.state = 'CANCELLED'
                           AND i.candidate_media_id IS NOT NULL
                     ))
              );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return [];
    }

    private async Task RunStageAsync(
        string stage,
        Func<Task<IReadOnlyList<RecoveryFinding>>> recover,
        List<RecoveryFinding> findings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _diagnostics?.WriteStateTransition("lifecycle.recovery", "RECOVERY_STAGE_STARTED", stage);
        try
        {
            findings.AddRange(await recover().ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _diagnostics?.WriteStateTransition(
                "lifecycle.recovery",
                "RECOVERY_CANCELLED",
                stage,
                DiagnosticSeverity.Warning);
            throw;
        }
        catch (SqliteException exception)
        {
            findings.Add(StageFailure(stage, RecoveryOutcome.Fatal, "RECOVERY_DATABASE_FAILURE"));
            _diagnostics?.Write(new DiagnosticEvent(
                DateTimeOffset.UtcNow,
                DiagnosticSeverity.Error,
                "lifecycle.recovery",
                "RECOVERY_DATABASE_FAILURE",
                $"{exception.GetType().Name} during {stage}"));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            findings.Add(StageFailure(stage, RecoveryOutcome.NeedsAttention, "RECOVERY_STAGE_AMBIGUOUS"));
            _diagnostics?.Write(new DiagnosticEvent(
                DateTimeOffset.UtcNow,
                DiagnosticSeverity.Warning,
                "lifecycle.recovery",
                "RECOVERY_STAGE_AMBIGUOUS",
                $"{exception.GetType().Name} during {stage}"));
        }
        finally
        {
            _diagnostics?.WriteStateTransition("lifecycle.recovery", "RECOVERY_STAGE_COMPLETED", stage);
        }
    }

    private static RecoveryFinding StageFailure(string stage, RecoveryOutcome outcome, string code) =>
        new(
            Guid.NewGuid(),
            "RecoveryStage",
            Guid.NewGuid(),
            outcome,
            code,
            $"Recovery stage {stage} could not reconcile all persisted work; unchanged facts remain for retry.");
}
