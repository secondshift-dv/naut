using System.IO;
using Neuterradise.App.Import;
using Neuterradise.App.Import.Preparation;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Maintenance;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Recovery;

public sealed class ImportRecovery
{
    private const string TerminalStagingOwnersSql =
        """
        SELECT import_unit_id
        FROM import_units
        WHERE state IN ('CANCELLED', 'COMPLETED', 'FAILED_TERMINAL')
           OR library_commit_state = 'TERMINAL';
        """;

    private const string KnownUnitSql =
        "SELECT EXISTS(SELECT 1 FROM import_units WHERE import_unit_id = $unitId);";

    private readonly CatalogDb _catalog;
    private readonly ImportPreparationCoordinator _preparation;
    private readonly StorageRecovery _storageRecovery;
    private readonly ImportCancellationSettlement _cancellationSettlement;
    private readonly ImportPublicationCoordinator _publication;

    public ImportRecovery(
        CatalogDb catalog,
        ImportPreparationCoordinator processing,
        StorageRecovery storageRecovery)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _preparation = processing ?? throw new ArgumentNullException(nameof(processing));
        _storageRecovery = storageRecovery ?? throw new ArgumentNullException(nameof(storageRecovery));
        _cancellationSettlement = new ImportCancellationSettlement(catalog);
        _publication = new ImportPublicationCoordinator(catalog);
    }

    public async Task<IReadOnlyList<RecoveryFinding>> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var findings = new List<RecoveryFinding>();

        // Cancelled imports with rollback_settled=0 are not terminally complete. Reconcile them
        // before considering ordinary import work. RecoveryCoordinator settles stale RUNNING jobs
        // before entering this stage, so canonical rollback never races a dead process lease.
        findings.AddRange(await RecoverPendingCancellationsAsync(cancellationToken).ConfigureAwait(false));
        findings.AddRange(await RecoverPendingPublicationsAsync(cancellationToken).ConfigureAwait(false));

        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT import_unit_id, commit_operation_id
            FROM import_units
            WHERE ((state IN ('INTAKE','PREPARING') AND commit_operation_id IS NULL)
               OR (state NOT IN ('CANCELLED','FAILED_TERMINAL','COMMITTING')
                   AND commit_operation_id IS NOT NULL
                   AND (library_commit_state <> 'TERMINAL' OR state = 'PREPARING')))
              AND is_paused = 0
            ORDER BY import_unit_id;
            """;

        var units = new List<RecoverableImportUnit>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                units.Add(new RecoverableImportUnit(
                    DbGuid.Parse(reader.GetString(0), DomainIdKind.ImportUnit),
                    reader.IsDBNull(1) ? null : DbGuid.Parse(reader.GetString(1), DomainIdKind.ImportSession)));
            }
        }

        foreach (var unit in units)
        {
            await using var mutationLease = await _catalog.ImportUnitMutations
                .EnterAsync(unit.UnitId, cancellationToken).ConfigureAwait(false);

            if (unit.CommitOperationId is not null)
            {
                // A PREPARING unit remains recoverable even when library_commit_state is TERMINAL.
                // StorageRecovery re-establishes idempotent media preparation scheduling before terminal
                // source-cleanup state is accepted.
                findings.Add(await _storageRecovery.RecoverImportCommitAsync(
                    unit.UnitId,
                    cancellationToken).ConfigureAwait(false));
                continue;
            }

            // Recovery uses the same pre-Stage-1 admission authority for duplicate investigation
            // and staged transfer. media preparation and lifecycle advancement remain with their coordinators.
            await _preparation.ScheduleAdmissionHashJobsAsync(unit.UnitId, cancellationToken)
                .ConfigureAwait(false);

            findings.Add(new RecoveryFinding(
                unit.UnitId,
                "ImportUnit",
                unit.UnitId,
                RecoveryOutcome.Requeued,
                "CANONICAL_ADMISSION_RECONCILED",
                "Pre-Stage-1 duplicate and transfer work was reconciled; lifecycle advancement remains owned by the Import Finalizer."));
        }

        return findings;
    }

    private async Task<IReadOnlyList<RecoveryFinding>> RecoverPendingCancellationsAsync(
        CancellationToken cancellationToken)
    {
        var unitIds = new List<Guid>();
        await using (var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT import_unit_id
                FROM import_units
                WHERE state = 'CANCELLED'
                  AND rollback_settled = 0
                ORDER BY import_unit_id;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                unitIds.Add(DbGuid.Parse(reader.GetString(0), DomainIdKind.ImportUnit));
            }
        }

        var findings = new List<RecoveryFinding>();
        foreach (var unitId in unitIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _cancellationSettlement.TrySettleAsync(unitId, cancellationToken)
                .ConfigureAwait(false);

            switch (result.Status)
            {
                case ImportCancellationSettlementStatus.Settled:
                case ImportCancellationSettlementStatus.NotRequired:
                    findings.Add(new RecoveryFinding(
                        unitId,
                        "ImportUnit",
                        unitId,
                        RecoveryOutcome.Completed,
                        "CANCEL_ROLLBACK_RECOVERED",
                        "An interrupted import cancellation finished its durable rollback without restarting import work."));
                    break;

                case ImportCancellationSettlementStatus.PendingSafeBoundary:
                    findings.Add(new RecoveryFinding(
                        unitId,
                        "ImportUnit",
                        unitId,
                        RecoveryOutcome.NeedsAttention,
                        result.Code,
                        "Cancellation rollback remains pending because import-owned work has not yet reached a safe boundary."));
                    break;

                case ImportCancellationSettlementStatus.PendingRetry:
                    findings.Add(new RecoveryFinding(
                        unitId,
                        "ImportUnit",
                        unitId,
                        RecoveryOutcome.NeedsAttention,
                        result.Code,
                        "Cancellation remains durable and safe, but one rollback step still needs retry."));
                    break;

                case ImportCancellationSettlementStatus.Missing:
                    findings.Add(new RecoveryFinding(
                        unitId,
                        "ImportUnit",
                        unitId,
                        RecoveryOutcome.NeedsAttention,
                        result.Code,
                        "A pending cancellation referenced an Import unit that could no longer be resolved."));
                    break;
            }
        }

        return findings;
    }

    private async Task<IReadOnlyList<RecoveryFinding>> RecoverPendingPublicationsAsync(
        CancellationToken cancellationToken)
    {
        var unitIds = new List<Guid>();
        await using (var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT import_unit_id
                FROM import_units
                WHERE state = 'COMMITTING'
                ORDER BY import_unit_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                unitIds.Add(DbGuid.Parse(reader.GetString(0), DomainIdKind.ImportUnit));
            }
        }

        var findings = new List<RecoveryFinding>();
        foreach (var unitId in unitIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _publication.PublishDurableAsync(unitId, cancellationToken).ConfigureAwait(false);
            findings.Add(new RecoveryFinding(
                unitId,
                "ImportUnit",
                unitId,
                result.IsPublished ? RecoveryOutcome.Completed : RecoveryOutcome.NeedsAttention,
                result.IsPublished ? "IMPORT_PUBLICATION_RECOVERED" : result.Code ?? "IMPORT_PUBLICATION_PENDING",
                result.IsPublished
                    ? "An interrupted Save finished publication without repeating canonical commit or media preparation."
                    : "The Save intent is durable, but publication still needs recovery."));
        }

        return findings;
    }

    public async Task<IReadOnlyList<RecoveryFinding>> RecoverStagingOwnershipAsync(
        CancellationToken cancellationToken = default)
    {
        var stagingRoot = _catalog.Paths.StagingImportsPath;
        if (!Directory.Exists(stagingRoot))
        {
            return [];
        }

        var terminalOwners = await ReadTerminalStagingOwnersAsync(cancellationToken).ConfigureAwait(false);
        var findings = new List<RecoveryFinding>();

        foreach (var entry in Directory.EnumerateFileSystemEntries(stagingRoot).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(entry);
            if (!DomainId.TryParse(name, out var unitId))
            {
                findings.Add(AmbiguousStagingFinding(
                    Guid.Empty,
                    "A staging entry does not name an Import unit and was left untouched for Vault Health."));
                continue;
            }

            if (terminalOwners.Contains(unitId))
            {
                findings.Add(ReleaseStaging(entry, unitId));
                continue;
            }

            if (!await IsKnownUnitAsync(unitId, cancellationToken).ConfigureAwait(false))
            {
                findings.Add(AmbiguousStagingFinding(
                    unitId,
                    "A staging entry names no known Import unit and was left untouched for Vault Health."));
            }
        }

        return findings;
    }

    private static RecoveryFinding ReleaseStaging(string entry, Guid unitId)
    {
        try
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }

            return new RecoveryFinding(
                unitId,
                "ImportUnit",
                unitId,
                RecoveryOutcome.Completed,
                "STAGING_TERMINAL_RELEASED",
                "Staging owned by a durably terminal Import unit was released.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new RecoveryFinding(
                unitId,
                "ImportUnit",
                unitId,
                RecoveryOutcome.NeedsAttention,
                "STAGING_RELEASE_FAILED",
                "Staging owned by a terminal Import unit could not be released and remains safe to retry.");
        }
    }

    private static RecoveryFinding AmbiguousStagingFinding(Guid unitId, string safeDetail) =>
        new(
            unitId,
            "ImportStaging",
            unitId,
            RecoveryOutcome.NeedsAttention,
            HealthFindingCode.StagingOrphanAmbiguous,
            safeDetail);

    private async Task<HashSet<Guid>> ReadTerminalStagingOwnersAsync(CancellationToken cancellationToken)
    {
        var owners = new HashSet<Guid>();
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = TerminalStagingOwnersSql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            owners.Add(DbGuid.Parse(reader.GetString(0)));
        }

        return owners;
    }

    private async Task<bool> IsKnownUnitAsync(Guid unitId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = KnownUnitSql;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private sealed record RecoverableImportUnit(
        Guid UnitId,
        Guid? CommitOperationId);
}
