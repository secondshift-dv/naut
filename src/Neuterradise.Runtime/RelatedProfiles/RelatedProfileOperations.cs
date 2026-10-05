using System.Diagnostics;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.RelatedProfiles;

public sealed class RelatedProfileOperations
{
    private readonly CatalogDb _catalog;
    private readonly RelatedWrites _writes;
    private readonly ActivityWrites _activityWrites;
    private readonly TimeProvider _timeProvider;

    public RelatedProfileOperations(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _writes = new RelatedWrites(catalog);
        _activityWrites = new ActivityWrites(catalog);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CatalogDb Catalog => _catalog;

    public async Task<OperationResult> AddManualRelatedProfileAsync(
        Guid profileIdA,
        Guid profileIdB,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(AddManualRelatedProfileAsync));
        if (profileIdA == Guid.Empty || profileIdB == Guid.Empty)
        {
            return OperationResult.Validation(
                "INVALID_PROFILE_ID",
                "Profile identifier cannot be empty.");
        }

        if (profileIdA == profileIdB)
        {
            return OperationResult.Validation(
                "SELF_PAIR_NOT_ALLOWED",
                "Related Profile evidence cannot target a self-pair.");
        }

        var (low, high) = Canonicalize(profileIdA, profileIdB);
        var now = _timeProvider.GetUtcNow();
        var key = $"Manual|{DbGuid.Format(low)}|{DbGuid.Format(high)}";

        try
        {
            var evidence = new RelatedEvidencePersistence(
                key,
                low,
                high,
                RelatedProfileEvidence.Manual,
                MediaId: null,
                FaceId: null,
                now);

            await _writes.PersistEvidenceAsync(evidence, cancellationToken).ConfigureAwait(false);
            await _writes.RebuildSummaryAsync(low, high, now, cancellationToken).ConfigureAwait(false);
            await AppendActivityBestEffortAsync(
                low,
                high,
                ActivityEventType.ManualRelatedAdded,
                cancellationToken).ConfigureAwait(false);

            return OperationResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CatalogInvariantException exception)
        {
            return OperationResult.Validation("INVALID_RELATED_PROFILE", exception.Message);
        }
        catch (Exception exception) when (CatalogDb.IsDatabaseFailure(exception) || exception is InvalidOperationException)
        {
            return OperationResult.Failed("MANUAL_RELATION_FAILED", exception.Message);
        }
    }

    public async Task<OperationResult> RemoveManualRelatedProfileAsync(
        Guid profileIdA,
        Guid profileIdB,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(RemoveManualRelatedProfileAsync));
        if (profileIdA == Guid.Empty || profileIdB == Guid.Empty)
        {
            return OperationResult.Validation(
                "INVALID_PROFILE_ID",
                "Profile identifier cannot be empty.");
        }

        if (profileIdA == profileIdB)
        {
            return OperationResult.Validation(
                "SELF_PAIR_NOT_ALLOWED",
                "Related Profile evidence cannot target a self-pair.");
        }

        var (low, high) = Canonicalize(profileIdA, profileIdB);
        var now = _timeProvider.GetUtcNow();

        try
        {
            await _writes.RemoveManualEvidenceAsync(low, high, now, cancellationToken).ConfigureAwait(false);
            await AppendActivityBestEffortAsync(
                low,
                high,
                ActivityEventType.ManualRelatedRemoved,
                cancellationToken).ConfigureAwait(false);

            return OperationResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CatalogInvariantException exception)
        {
            return OperationResult.Validation("INVALID_RELATED_PROFILE", exception.Message);
        }
        catch (Exception exception) when (CatalogDb.IsDatabaseFailure(exception) || exception is InvalidOperationException)
        {
            return OperationResult.Failed("MANUAL_RELATION_FAILED", exception.Message);
        }
    }

    /// <summary>
    /// Activity is a derived chronicle of an already-durable relation mutation. Failure to append the
    /// chronicle must be observable, but must not turn a successfully persisted relation into a false
    /// mutation failure in the UI.
    /// </summary>
    private async Task AppendActivityBestEffortAsync(
        Guid low,
        Guid high,
        string eventType,
        CancellationToken cancellationToken)
    {
        try
        {
            await AppendActivityAsync(low, high, eventType, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (CatalogDb.IsDatabaseFailure(exception) || exception is InvalidOperationException)
        {
            Trace.TraceWarning(
                "Related-profile activity append failed for {0}/{1}: {2}",
                DbGuid.Format(low),
                DbGuid.Format(high),
                exception.GetType().Name);
        }
    }

    private async Task AppendActivityAsync(
        Guid low,
        Guid high,
        string eventType,
        CancellationToken cancellationToken)
    {
        var payload = $"{{\"profileIdLow\":\"{DbGuid.Format(low)}\",\"profileIdHigh\":\"{DbGuid.Format(high)}\"}}";
        var entry = new ActivityEntryPersistence(
            Guid.NewGuid(),
            eventType,
            ProfileId: low,
            MediaId: null,
            ImportUnitId: null,
            OperationId: null,
            payload,
            _timeProvider.GetUtcNow());

        await _activityWrites.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private static (Guid Low, Guid High) Canonicalize(Guid a, Guid b)
    {
        var strA = DbGuid.Format(a);
        var strB = DbGuid.Format(b);
        return string.CompareOrdinal(strA, strB) < 0 ? (a, b) : (b, a);
    }
}
