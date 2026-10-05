using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Trash;

public static class ProfileTrashRetentionPolicy
{
    public const int ProfileRetentionDays = 30;

    public static TimeSpan ProfileRetention { get; } =
        TimeSpan.FromDays(ProfileRetentionDays);

    public static DateTimeOffset ProfilePermanentDeletionAt(DateTimeOffset trashedAtUtc) =>
        trashedAtUtc.Add(ProfileRetention);
}

public sealed record ProfileTrashRetentionFailure(
    Guid TrashEntryId,
    Guid ProfileId,
    string SafeDetail);

public sealed record ProfileTrashRetentionSweepResult(
    int PurgedCount,
    IReadOnlyList<ProfileTrashRetentionFailure> Failures);

public sealed class ProfileTrashRetentionService
{
    private readonly TrashReads _reads;
    private readonly PurgeExecutor _purge;
    private readonly TimeProvider _timeProvider;

    public ProfileTrashRetentionService(
        TrashReads reads,
        PurgeExecutor purge,
        TimeProvider? timeProvider = null)
    {
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _purge = purge ?? throw new ArgumentNullException(nameof(purge));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProfileTrashRetentionSweepResult> PurgeExpiredProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        var cutoffUtc = _timeProvider.GetUtcNow()
            .Subtract(ProfileTrashRetentionPolicy.ProfileRetention);
        var expired = await _reads
            .GetExpiredProfileTrashEntriesAsync(cutoffUtc, cancellationToken)
            .ConfigureAwait(false);
        if (expired.Count == 0)
        {
            return new ProfileTrashRetentionSweepResult(0, []);
        }

        var purged = 0;
        var failures = new List<ProfileTrashRetentionFailure>();
        foreach (var entry in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var prepared = await _purge.PreparePurgeProfileAsync(
                    entry.TrashEntryId,
                    cancellationToken).ConfigureAwait(false);

                if (!prepared.IsSuccess || prepared.Value is null)
                {
                    failures.Add(Failure(
                        entry,
                        prepared.UserMessage ?? "The retention purge could not be prepared."));
                    continue;
                }

                var confirmed = await _purge.ConfirmPurgeProfileAsync(
                    prepared.Value.PurgePlanId,
                    irreversibleConfirmation: true,
                    cancellationToken).ConfigureAwait(false);

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

        return new ProfileTrashRetentionSweepResult(purged, failures);
    }

    private static ProfileTrashRetentionFailure Failure(
        TrashItemSummary entry,
        string detail) =>
        new(
            entry.TrashEntryId,
            entry.EntityId,
            detail);
}
