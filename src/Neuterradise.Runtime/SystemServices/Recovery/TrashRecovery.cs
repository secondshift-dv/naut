using Microsoft.Data.Sqlite;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Trash;

namespace Neuterradise.App.SystemServices.Recovery;

public sealed class TrashRecovery
{
    private const string NonterminalEntriesSql =
        """
        SELECT trash_entry_id, entity_type, entity_id, state, plan_json
        FROM trash_entries
        WHERE state IN (
            'EXECUTING','IN_TRASH',
            'RESTORE_EXECUTING','RESTORE_FINALIZING',
            'PURGE_EXECUTING','PURGE_RETRY_REQUIRED'
        )
        ORDER BY created_at_ms, trash_entry_id;
        """;

    private readonly CatalogDb _catalog;
    private readonly TrashCoordinator _trash;
    private readonly RestoreExecutor _restore;
    private readonly PurgeExecutor _purge;
    private readonly ProfileTrashRetentionService _retention;

    public TrashRecovery(CatalogDb catalog, ManagedMoveExecutor moveExecutor)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(moveExecutor);
        _catalog = catalog;
        _trash = new TrashCoordinator(catalog, moveExecutor, new MediaOperations(catalog));
        _restore = new RestoreExecutor(catalog, moveExecutor);
        _purge = new PurgeExecutor(catalog, moveExecutor);
        _retention = new ProfileTrashRetentionService(new TrashReads(catalog), _purge);
    }

    public async Task<IReadOnlyList<RecoveryFinding>> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var findings = new List<RecoveryFinding>();

        foreach (var entry in await ReadNonterminalEntriesAsync(cancellationToken).ConfigureAwait(false))
        {
            var finding = await RecoverEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            if (finding is not null)
            {
                findings.Add(finding);
            }
        }

        var retention = await _retention
            .PurgeExpiredProfilesAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var failure in retention.Failures)
        {
            findings.Add(new RecoveryFinding(
                failure.TrashEntryId,
                "Profile",
                failure.ProfileId,
                RecoveryOutcome.NeedsAttention,
                "TRASH_RETENTION_PURGE_FAILED",
                failure.SafeDetail));
        }

        return findings;
    }

    private async Task<RecoveryFinding?> RecoverEntryAsync(
        TrashEntryFacts entry,
        CancellationToken cancellationToken)
    {
        try
        {
            return entry.State switch
            {
                TrashEntryState.Executing when entry.EntityType == TrashEntityType.Media =>
                    await ResumeMediaTrashAsync(entry, cancellationToken).ConfigureAwait(false),
                TrashEntryState.Executing when entry.EntityType == TrashEntityType.Profile =>
                    await ResumeProfileTrashAsync(entry, cancellationToken).ConfigureAwait(false),
                TrashEntryState.InTrash when entry.EntityType == TrashEntityType.Media =>
                    await ResumeMediaRestoreAsync(entry, cancellationToken).ConfigureAwait(false),
                TrashEntryState.InTrash when entry.EntityType == TrashEntityType.Profile =>
                    await ResumeProfileRestoreAsync(entry, cancellationToken).ConfigureAwait(false),
                TrashEntryState.RestoreExecuting when entry.EntityType == TrashEntityType.Profile =>
                    await ResumeProfileRestoreAsync(entry, cancellationToken).ConfigureAwait(false),
                TrashEntryState.RestoreFinalizing when entry.EntityType == TrashEntityType.Profile =>
                    await ResumeProfileRestoreAsync(entry, cancellationToken).ConfigureAwait(false),
                PurgePlanState.Executing => await ResumePurgeAsync(entry, cancellationToken)
                    .ConfigureAwait(false),
                PurgePlanState.RetryRequired => RetryablePurgeFinding(entry),
                _ => null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException)
        {
            return entry.ToFinding(
                operationId: null,
                RecoveryOutcome.Fatal,
                "TRASH_RECOVERY_DATABASE_FAILURE",
                "The catalog failed while resuming a persisted Trash operation.");
        }
        catch (Exception exception) when (exception is CatalogInvariantException
            or CatalogConcurrencyConflictException
            or InvalidOperationException)
        {
            return entry.ToFinding(
                operationId: null,
                RecoveryOutcome.NeedsAttention,
                "TRASH_RECOVERY_AMBIGUOUS",
                "Persisted Trash facts are incomplete or inconsistent and were left unchanged.");
        }
    }

    private async Task<RecoveryFinding> ResumeMediaTrashAsync(
        TrashEntryFacts entry,
        CancellationToken cancellationToken)
    {
        var plan = MediaTrashPlan.FromJson(entry.PlanJson);
        if (plan is null)
        {
            return UnreadablePlanFinding(entry);
        }

        var result = await _trash.ExecuteMediaTrashAsync(plan, cancellationToken).ConfigureAwait(false);
        return entry.ToFinding(
            plan.OperationId,
            result.IsSuccess ? RecoveryOutcome.Completed : RecoveryOutcome.NeedsAttention,
            result.IsSuccess ? "TRASH_MOVE_RECOVERED" : "TRASH_MOVE_NEEDS_ATTENTION",
            result.IsSuccess
                ? "The authorized Trash move reached its durable TRASHED checkpoint."
                : SafeDetail(result, "The authorized Trash move could not be completed safely."));
    }

    private async Task<RecoveryFinding> ResumeProfileTrashAsync(
        TrashEntryFacts entry,
        CancellationToken cancellationToken)
    {
        var plan = ProfileTrashPlan.FromJson(entry.PlanJson);
        if (plan is null)
        {
            return UnreadablePlanFinding(entry);
        }

        if (plan.OwnedActiveMedias.Count > 0 && plan.SelectedDispositions.Count == 0)
        {
            return entry.ToFinding(
                plan.OperationId,
                RecoveryOutcome.NeedsAttention,
                "PROFILE_TRASH_DISPOSITION_MISSING",
                "The interrupted Profile Trash predates durable disposition authority and needs attention.");
        }

        var result = await _trash.CommitProfileTrashAsync(
                plan,
                plan.SelectedDispositions,
                cancellationToken)
            .ConfigureAwait(false);
        return entry.ToFinding(
            plan.OperationId,
            result.IsSuccess ? RecoveryOutcome.Completed : RecoveryOutcome.NeedsAttention,
            result.IsSuccess ? "PROFILE_TRASH_RECOVERED" : "PROFILE_TRASH_NEEDS_ATTENTION",
            result.IsSuccess
                ? "The interrupted Profile Trash converged to its durable terminal marker."
                : SafeDetail(result, "The interrupted Profile Trash could not be completed safely."));
    }

    private async Task<RecoveryFinding?> ResumeMediaRestoreAsync(
        TrashEntryFacts entry,
        CancellationToken cancellationToken)
    {
        var plan = MediaTrashPlan.FromJson(entry.PlanJson);
        if (plan is null)
        {
            return UnreadablePlanFinding(entry);
        }

        if (plan.RestoreCheckpoint is null)
        {
            return null;
        }

        var result = await _restore.RestoreMediaAsync(
                entry.TrashEntryId,
                targetOwnerProfileId: null,
                cancellationToken)
            .ConfigureAwait(false);
        return entry.ToFinding(
            plan.OperationId,
            result.IsSuccess ? RecoveryOutcome.Completed : RecoveryOutcome.NeedsAttention,
            result.IsSuccess ? "RESTORE_MOVE_RECOVERED" : "RESTORE_MOVE_NEEDS_ATTENTION",
            result.IsSuccess
                ? "The interrupted Restore reached current active authority from its durable checkpoint."
                : SafeDetail(result, "The interrupted Restore could not be completed safely."));
    }

    private async Task<RecoveryFinding?> ResumeProfileRestoreAsync(
        TrashEntryFacts entry,
        CancellationToken cancellationToken)
    {
        var plan = ProfileTrashPlan.FromJson(entry.PlanJson);
        if (plan is null)
        {
            return UnreadablePlanFinding(entry);
        }
        if (plan.RestoreCheckpoint is null)
        {
            return null;
        }

        var result = await _restore.RestoreProfileAsync(entry.TrashEntryId, cancellationToken)
            .ConfigureAwait(false);
        return entry.ToFinding(
            plan.OperationId,
            result.IsSuccess ? RecoveryOutcome.Completed : RecoveryOutcome.NeedsAttention,
            result.IsSuccess ? "PROFILE_RESTORE_RECOVERED" : "PROFILE_RESTORE_NEEDS_ATTENTION",
            result.IsSuccess
                ? "The interrupted Profile Restore converged from its durable checkpoint."
                : SafeDetail(result, "The interrupted Profile Restore could not be completed safely."));
    }

    private async Task<RecoveryFinding> ResumePurgeAsync(
        TrashEntryFacts entry,
        CancellationToken cancellationToken)
    {
        var plan = PurgePlan.FromJson(entry.PlanJson);
        if (plan is null)
        {
            return UnreadablePlanFinding(entry);
        }

        var result = plan.EntityType == PurgeEntityType.Media
            ? await _purge.ConfirmPurgeMediaAsync(
                    entry.TrashEntryId, irreversibleConfirmation: true, cancellationToken)
                .ConfigureAwait(false)
            : await _purge.ConfirmPurgeProfileAsync(
                    entry.TrashEntryId, irreversibleConfirmation: true, cancellationToken)
                .ConfigureAwait(false);

        return entry.ToFinding(
            plan.OperationId,
            result.IsSuccess ? RecoveryOutcome.Completed : RecoveryOutcome.NeedsAttention,
            result.IsSuccess ? "PURGE_DELETE_RESUMED" : "PURGE_DELETE_RETRYABLE",
            result.IsSuccess
                ? "The authorized Purge finished deleting exactly the material it had authorized."
                : SafeDetail(result, "The authorized Purge remains retryable and deleted nothing more."));
    }

    private static RecoveryFinding RetryablePurgeFinding(TrashEntryFacts entry) =>
        entry.ToFinding(
            PurgePlan.FromJson(entry.PlanJson)?.OperationId,
            RecoveryOutcome.NeedsAttention,
            "PURGE_DELETE_RETRYABLE",
            "An authorized Purge stopped on a deletion failure and remains explicitly retryable.");

    private static RecoveryFinding UnreadablePlanFinding(TrashEntryFacts entry) =>
        entry.ToFinding(
            operationId: null,
            RecoveryOutcome.NeedsAttention,
            "TRASH_PLAN_UNREADABLE",
            "The durable Trash plan cannot be read, so nothing was moved or deleted.");

    private static string SafeDetail<T>(OperationResult<T> result, string fallback) =>
        result.UserMessage ?? fallback;

    private async Task<IReadOnlyList<TrashEntryFacts>> ReadNonterminalEntriesAsync(
        CancellationToken cancellationToken)
    {
        var entries = new List<TrashEntryFacts>();
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = NonterminalEntriesSql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new TrashEntryFacts(
                DbGuid.Parse(reader.GetString(0)),
                reader.GetString(1),
                DbGuid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return entries;
    }

    private sealed record TrashEntryFacts(
        Guid TrashEntryId,
        string EntityType,
        Guid EntityId,
        string State,
        string PlanJson)
    {
        public RecoveryFinding ToFinding(
            Guid? operationId,
            RecoveryOutcome outcome,
            string code,
            string safeDetail) =>
            new(
                operationId ?? TrashEntryId,
                EntityType is TrashEntityType.Profile or PurgeOperationEntityType.Profile
                    ? "Profile"
                    : "Media",
                EntityId,
                outcome,
                code,
                safeDetail);
    }
}
