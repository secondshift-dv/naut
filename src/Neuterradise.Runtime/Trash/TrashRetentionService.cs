using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Trash;

public static class TrashRetentionPolicy
{
    public const int RetentionDays = 30;

    public static TimeSpan Retention { get; } =
        TimeSpan.FromDays(RetentionDays);

    public static DateTimeOffset PermanentDeletionAt(DateTimeOffset trashedAtUtc) =>
        trashedAtUtc.Add(Retention);
}

public sealed record TrashRetentionFailure(
    Guid TrashEntryId,
    Guid EntityId,
    string EntityType,
    string SafeDetail);

public sealed record TrashRetentionSweepResult(
    int PurgedCount,
    IReadOnlyList<TrashRetentionFailure> Failures);

public sealed class TrashRetentionService
{
    private readonly TrashReads _reads;
    private readonly PurgeExecutor _purge;
    private readonly TimeProvider _timeProvider;

    public TrashRetentionService(
        TrashReads reads,
        PurgeExecutor purge,
        TimeProvider? timeProvider = null)
    {
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _purge = purge ?? throw new ArgumentNullException(nameof(purge));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TrashRetentionSweepResult> PurgeExpiredAsync(
        CancellationToken cancellationToken = default)
    {
        var cutoffUtc = _timeProvider.GetUtcNow()
            .Subtract(TrashRetentionPolicy.Retention);
        var expired = await _reads
            .GetExpiredTrashEntriesAsync(cutoffUtc, cancellationToken)
            .ConfigureAwait(false);
        if (expired.Count == 0)
        {
            return new TrashRetentionSweepResult(0, []);
        }

        var purged = 0;
        var failures = new List<TrashRetentionFailure>();
        foreach (var entry in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var prepared = await (entry.EntityType == TrashEntityType.Media
                    ? _purge.PreparePurgeMediaAsync(entry.TrashEntryId, cancellationToken)
                    : _purge.PreparePurgeProfileAsync(entry.TrashEntryId, cancellationToken)).ConfigureAwait(false);

                if (!prepared.IsSuccess || prepared.Value is null)
                {
                    failures.Add(Failure(
                        entry,
                        prepared.UserMessage ?? "The retention purge could not be prepared."));
                    continue;
                }

                var confirmed = await (entry.EntityType == TrashEntityType.Media
                    ? _purge.ConfirmPurgeMediaAsync(prepared.Value.PurgePlanId, true, cancellationToken)
                    : _purge.ConfirmPurgeProfileAsync(prepared.Value.PurgePlanId, true, cancellationToken)).ConfigureAwait(false);

                if (!confirmed.IsSuccess)
                {
                    failures.Add(Failure(
                        entry,
                        confirmed.UserMessage ?? "The retention purge could not be completed."));
                    continue;
                }

                purged++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(Failure(
                    entry,
                    OperationExecution.SafeMessage(exception)));
            }
        }

        return new TrashRetentionSweepResult(purged, failures);
    }

    private static TrashRetentionFailure Failure(
        TrashItemSummary entry,
        string detail) =>
        new(
            entry.TrashEntryId,
            entry.EntityId,
            entry.EntityType,
            detail);
}
