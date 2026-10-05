using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using Neuterradise.App.Import.Preparation;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Import;

public enum ImportFinalizeOutcome
{
    NotRequested,
    Waiting,
    Committed,
    NeedsAttention,
    Finished,
}

/// <summary>
/// Turns durable import intent into a library commit. Safe admission and automatic ownership happen
/// here, outside the UI, so closing the overlay or restarting the app cannot change classification.
/// </summary>
public sealed class ImportFinalizer : IAsyncDisposable
{
    public static readonly TimeSpan PendingInterval = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan AttentionBackoff = TimeSpan.FromMinutes(1);

    private readonly CatalogDb _catalog;
    private readonly ImportReads _reads;
    private readonly SchedulerReads _schedulerReads;
    private readonly VerificationOperations _operations;
    private readonly ImportPreparationCoordinator _preparation;
    private readonly ImportMediaPreparationCoordinator _mediaPreparation;
    private readonly ImportUnitSplitter _splitter;
    private readonly AutomaticImportAssignmentService _assignment;
    private readonly Func<ImportCommitCoordinator> _commitFactory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, DateTimeOffset> _attentionUntil = [];

    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    private int _disposed;

    public ImportFinalizer(
        CatalogDb catalog,
        ImportPreparationCoordinator? preparation = null,
        Func<ImportCommitCoordinator>? commitFactory = null,
        TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reads = catalog.ImportReads;
        _schedulerReads = new SchedulerReads(catalog);
        _operations = new VerificationOperations(catalog, _timeProvider);
        _preparation = preparation ?? new ImportPreparationCoordinator(catalog, _timeProvider);
        _mediaPreparation = new ImportMediaPreparationCoordinator(catalog, catalog.Paths, _timeProvider);
        _splitter = new ImportUnitSplitter(catalog, _timeProvider);
        _assignment = new AutomaticImportAssignmentService(catalog);
        _commitFactory = commitFactory ?? (() => CreateDefaultCommitCoordinator(catalog));
    }

    public event EventHandler? Changed;

    public static ImportCommitCoordinator CreateDefaultCommitCoordinator(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var paths = catalog.Paths;
        var volume = new WindowsVolumeIdentityProvider();
        var verifier = new ManagedFileVerifier();
        var moveExecutor = new ManagedMoveExecutor(paths, volume, verifier, new MediaWrites(catalog));
        var cleanupExecutor = new SourceCleanupExecutor(new ImportWrites(catalog));
        return new ImportCommitCoordinator(
            catalog,
            new VerificationValidator(catalog),
            moveExecutor,
            cleanupExecutor,
            volume,
            paths);
    }

    public void Start()
    {
        if (_loop is not null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _loop = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
    }

    public void Wake(Guid? clearAttentionFor = null)
    {
        TaskCompletionSource wake;
        lock (_sync)
        {
            if (clearAttentionFor is { } unitId)
            {
                _attentionUntil.Remove(unitId);
            }

            wake = _wake;
        }

        wake.TrySetResult();
    }

    public async Task<bool> RequestImportAsync(
        Guid unitId,
        VerificationDraftV1 draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        draft = await MaterializeProfileMovesAsync(unitId, draft, cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var model = await _operations.LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                return false;
            }

            var existing = VerificationDraftV1.FromJson(
                model.VerificationDraftJson,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            var intent = draft with
            {
                CurrentStep = Math.Max(2, draft.CurrentStep),
                ImportRequested = true,
                AttentionItemIds = existing.AttentionItemIds ?? draft.AttentionItemIds,
            };

            try
            {
                await _operations.UpdateDraftAsync(unitId, intent, model.RowVersion, cancellationToken).ConfigureAwait(false);
                Wake(unitId);
                return true;
            }
            catch (CatalogConcurrencyConflictException) when (attempt < 2)
            {
            }
        }

        return false;
    }

    private async Task<VerificationDraftV1> MaterializeProfileMovesAsync(
        Guid unitId,
        VerificationDraftV1 draft,
        CancellationToken cancellationToken)
    {
        var moveGroups = (draft.ProfileCollisionDecisions ?? [])
            .Where(static decision => decision.Action == ProfileCollisionAction.MoveToProfile
                && decision.TargetProfileId.HasValue
                && decision.TargetProfileId.Value != Guid.Empty)
            .GroupBy(static decision => decision.TargetProfileId!.Value)
            .Select(group => new
            {
                ProfileId = group.Key,
                ItemIds = group.Select(static decision => decision.ImportItemId).Distinct().ToArray(),
            })
            .ToList();
        if (moveGroups.Count == 0)
        {
            return draft;
        }

        var items = await _reads.GetUnitItemsUnboundedAsync(unitId, cancellationToken).ConfigureAwait(false);
        var movedIds = moveGroups.SelectMany(static group => group.ItemIds).ToHashSet();
        var sourceHasAcceptedItem = items.Any(item =>
            !movedIds.Contains(item.ItemId)
            && item.Disposition is ItemDisposition.Included or ItemDisposition.Reused);

        Guid? sourceTargetProfileId = null;
        if (!sourceHasAcceptedItem)
        {
            var sourceGroup = moveGroups[0];
            sourceTargetProfileId = sourceGroup.ProfileId;
            moveGroups.RemoveAt(0);
            draft = draft with
            {
                Destination = new VerificationDestinationDraft(DestinationKind.ExistingNormal, sourceGroup.ProfileId, null),
                ProfileCollisionDecisions = (draft.ProfileCollisionDecisions ?? [])
                    .Where(decision => !sourceGroup.ItemIds.Contains(decision.ImportItemId))
                    .ToArray(),
            };
        }

        foreach (var group in moveGroups)
        {
            var split = await _splitter.SplitAsync(
                unitId,
                group.ItemIds,
                $"{group.ItemIds.Length} media moved by Verify",
                cancellationToken).ConfigureAwait(false);
            var child = await _operations.LoadVerificationReadModelAsync(split.NewUnitId, cancellationToken).ConfigureAwait(false)
                ?? throw new CatalogInvariantException($"Split ImportUnit {split.NewUnitId:D} was not created.");
            var childDraft = VerificationDraftV1.FromJson(child.VerificationDraftJson, _timeProvider.GetUtcNowUnixMilliseconds()) with
            {
                CurrentStep = 5,
                Destination = new VerificationDestinationDraft(DestinationKind.ExistingNormal, group.ProfileId, null),
                Appearance = AppearanceForItems(draft.Appearance, group.ItemIds),
                DuplicateDecisions = (draft.DuplicateDecisions ?? [])
                    .Where(decision => group.ItemIds.Contains(decision.ImportItemId))
                    .ToArray(),
                ProfileCollisionDecisions = [],
                ImportRequested = true,
            };
            await _operations.UpdateDraftAsync(split.NewUnitId, childDraft, child.RowVersion, cancellationToken).ConfigureAwait(false);

            // A split child is a new durable Import Unit. Re-enter it through canonical Hash
            // admission so it cannot be stranded before background scheduling observes the move.
            // This path owns the durable INTAKE -> PREPARING handoff; readiness observers stay read-only.
            await _preparation.ScheduleAdmissionHashJobsAsync(split.NewUnitId, cancellationToken).ConfigureAwait(false);
            Wake(split.NewUnitId);
        }

        var current = await _operations.LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false)
            ?? throw new CatalogInvariantException($"ImportUnit {unitId:D} disappeared while materializing Profile moves.");
        var persisted = VerificationDraftV1.FromJson(current.VerificationDraftJson, _timeProvider.GetUtcNowUnixMilliseconds());
        return draft with
        {
            Appearance = persisted.Appearance,
            DuplicateDecisions = (draft.DuplicateDecisions ?? [])
                .Where(decision => !moveGroups.SelectMany(static group => group.ItemIds).Contains(decision.ImportItemId))
                .ToArray(),
            ProfileCollisionDecisions = (draft.ProfileCollisionDecisions ?? [])
                .Where(decision => !moveGroups.SelectMany(static group => group.ItemIds).Contains(decision.ImportItemId))
                .ToArray(),
            Destination = sourceTargetProfileId.HasValue
                ? new VerificationDestinationDraft(DestinationKind.ExistingNormal, sourceTargetProfileId.Value, null)
                : draft.Destination,
        };
    }

    private static VerificationAppearanceDraft AppearanceForItems(
        VerificationAppearanceDraft appearance,
        IReadOnlyCollection<Guid> itemIds)
    {
        var keepCover = appearance.CoverImportItemId is { } coverItemId && itemIds.Contains(coverItemId);
        var keepBanner = appearance.BannerImportItemId is { } bannerItemId && itemIds.Contains(bannerItemId);
        return appearance with
        {
            CoverMediaId = keepCover ? appearance.CoverMediaId : null,
            CoverSourceKind = keepCover ? appearance.CoverSourceKind : null,
            CoverImportItemId = keepCover ? appearance.CoverImportItemId : null,
            CoverVideoTimestampMilliseconds = keepCover ? appearance.CoverVideoTimestampMilliseconds : null,
            BannerMediaId = keepBanner ? appearance.BannerMediaId : null,
            BannerImportItemId = keepBanner ? appearance.BannerImportItemId : null,
        };
    }

    public async Task<IReadOnlyDictionary<Guid, ImportFinalizeOutcome>> RunPassAsync(
        CancellationToken cancellationToken = default)
    {
        await _passGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var measure = PerfTrace.Measure("import.finalizer.pass", 500);
            var outcomes = new Dictionary<Guid, ImportFinalizeOutcome>();
            foreach (var unitId in await ReadRequestedUnitIdsAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (_sync)
                {
                    if (_attentionUntil.TryGetValue(unitId, out var until) && until > _timeProvider.GetUtcNow())
                    {
                        outcomes[unitId] = ImportFinalizeOutcome.NeedsAttention;
                        continue;
                    }
                }

                await using var mutationLease = await _catalog.ImportUnitMutations
                    .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

                ImportFinalizeOutcome outcome;
                try
                {
                    outcome = await AdvanceAsync(unitId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Trace.TraceWarning("Import {0:D} could not be finished yet: {1}", unitId, exception);
                    await MarkFailureAsync(
                        unitId,
                        IsTransientCommitFailure(exception),
                        "IMPORT_COMMIT_EXCEPTION",
                        cancellationToken).ConfigureAwait(false);
                    outcome = ImportFinalizeOutcome.NeedsAttention;
                }

                outcomes[unitId] = outcome;
            }

            if (outcomes.Values.Any(value => value is ImportFinalizeOutcome.Committed or ImportFinalizeOutcome.NeedsAttention))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return outcomes;
        }
        finally
        {
            _passGate.Release();
        }
    }

    public async Task<ImportFinalizeOutcome> AdvanceAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        var model = await _operations.LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return ImportFinalizeOutcome.Finished;
        }

        if (model.State is ImportUnitState.Cancelled or ImportUnitState.FailedTerminal
            || model.State.IsUnitCommitted())
        {
            return ImportFinalizeOutcome.Finished;
        }

        var draft = VerificationDraftV1.FromJson(
            model.VerificationDraftJson,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        if (!draft.IsImportRequested)
        {
            return ImportFinalizeOutcome.NotRequested;
        }

        var summary = await _reads.GetUnitSummaryAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (summary is null || summary.IsPaused || model.State == ImportUnitState.Intake)
        {
            return ImportFinalizeOutcome.Waiting;
        }

        if (model.CommitOperationId is null)
        {
            // Completed Candidate transfer bytes remain in staging until required preparation
            // settles. Moving them into Profile ownership while a decoder is reading would
            // invalidate the job's input authority.
            var admission = await AdmitAutomaticallyAsync(unitId, draft, cancellationToken).ConfigureAwait(false);
            if (!admission.ReadyToCommit)
            {
                return ImportFinalizeOutcome.Waiting;
            }

            // Before commit, schedule only identity preparation. MediaAsset derivation is
            // intentionally unavailable until Profile/original authority is durable.
            await _mediaPreparation.ScheduleForCurrentPhaseAsync(unitId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (await HasActiveCandidateIdentityPreparationAsync(unitId, cancellationToken)
                    .ConfigureAwait(false))
                return ImportFinalizeOutcome.Waiting;
            if (await HasTerminalCandidateIdentityFailureAsync(unitId, cancellationToken)
                    .ConfigureAwait(false))
            {
                await MarkFailureAsync(
                    unitId,
                    retryable: true,
                    "IDENTITY_PREPARATION_FAILED",
                    cancellationToken).ConfigureAwait(false);
                return ImportFinalizeOutcome.NeedsAttention;
            }

            // Once exact-duplicate admission is durable, existing ownership/People evidence may
            // resolve the destination. Ambiguous identity remains a user decision before commit.
            await ApplyAutomaticDestinationAsync(unitId, cancellationToken).ConfigureAwait(false);
        }

        using var commitMeasure = PerfTrace.Measure("import.commit", 5000);
        // canonical commit materialization follows settled required Candidate preparation.
        var result = await _commitFactory().CommitCanonicalMediaAsync(unitId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.LibraryCommitted)
        {
            // Final managed authority permits optional enrichment and reuses settled Candidate outputs.
            try
            {
                await _mediaPreparation.ScheduleForCurrentPhaseAsync(unitId, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                // Attempt readiness join: if all applicable capabilities are already terminal,
                // transition to ReadyForVerification immediately.
                await _mediaPreparation.TryTransitionToReadyAsync(unitId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Non-fatal: canonical commit authority is already durable. media preparation can be retried.
                System.Diagnostics.Trace.TraceWarning(
                    "media preparation scheduling was not completed for {0:D}: {1}",
                    unitId, exception.GetType().Name);
            }

            lock (_sync)
            {
                _attentionUntil.Remove(unitId);
            }

            return ImportFinalizeOutcome.Committed;
        }

        if (result.Outcome == ImportCommitOutcome.Cancelled)
        {
            return ImportFinalizeOutcome.Finished;
        }

        Trace.TraceWarning(
            "Import {0:D} commit did not complete ({1}): {2}",
            unitId,
            result.Outcome,
            string.Join(" | ", result.Blockers.Select(blocker => blocker.Code)));
        var retryable = result.Outcome == ImportCommitOutcome.Conflict
            || result.Blockers.Any(blocker => blocker.Code == "PLACEMENT_RETRYABLE");
        await MarkFailureAsync(
            unitId,
            retryable,
            result.Blockers.FirstOrDefault()?.Code ?? "IMPORT_COMMIT_BLOCKED",
            cancellationToken).ConfigureAwait(false);
        return ImportFinalizeOutcome.NeedsAttention;
    }

    private async Task<bool> HasActiveCandidateIdentityPreparationAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM jobs j
                JOIN import_items ii ON ii.candidate_media_id = j.owner_id
                WHERE ii.import_unit_id = $unitId
                  AND ii.disposition = 'INCLUDED'
                  AND j.kind IN ('ExtractMetadata', 'FaceAnalysis')
                  AND j.owner_type = 'Media'
                  AND j.state NOT IN ('SUCCEEDED', 'FAILED_TERMINAL', 'CANCELLED')
            );
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private async Task<bool> HasTerminalCandidateIdentityFailureAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM jobs j
                JOIN import_items ii ON ii.candidate_media_id = j.owner_id
                WHERE ii.import_unit_id = $unitId
                  AND ii.disposition = 'INCLUDED'
                  AND j.kind IN ('ExtractMetadata', 'FaceAnalysis')
                  AND j.owner_type = 'Media'
                  AND j.state = 'FAILED_TERMINAL'
            );
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }
    private async Task ApplyAutomaticDestinationAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var assessment = await _assignment.AssessAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (!assessment.Decision.IsAuthoritative
            || assessment.Decision.ProfileId is not { } profileId
            || profileId == Guid.Empty)
        {
            if (assessment.HasAmbiguity)
            {
                Trace.TraceInformation(
                    "Import {0:D} remains unresolved: {1} ownership cluster(s), {2}/{3} item(s) have durable owner evidence.",
                    unitId,
                    assessment.Clusters.Count,
                    assessment.SupportedItemCount,
                    assessment.TotalItemCount);
            }

            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var model = await _operations.LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false);
            if (model is null || model.CommitOperationId is not null)
            {
                return;
            }

            var draft = VerificationDraftV1.FromJson(
                model.VerificationDraftJson,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            if (draft.Destination.Kind != DestinationKind.SystemUnknown)
            {
                return;
            }

            var updated = draft with
            {
                Destination = new VerificationDestinationDraft(
                    DestinationKind.ExistingNormal,
                    profileId,
                    NewProfile: null),
            };

            try
            {
                await _operations.UpdateDraftAsync(
                    unitId,
                    updated,
                    model.RowVersion,
                    cancellationToken).ConfigureAwait(false);
                Trace.TraceInformation(
                    "Import {0:D} automatically reused durable Profile ownership {1:D} for all {2} item(s).",
                    unitId,
                    profileId,
                    assessment.TotalItemCount);
                return;
            }
            catch (CatalogConcurrencyConflictException) when (attempt < 2)
            {
            }
        }
    }

    private async Task<AdmissionResult> AdmitAutomaticallyAsync(
        Guid unitId,
        VerificationDraftV1 draft,
        CancellationToken cancellationToken)
    {
        var items = await _reads.GetUnitItemsUnboundedAsync(unitId, cancellationToken).ConfigureAwait(false);
        var jobs = await _schedulerReads.GetJobsForImportUnitMediasAsync(unitId, cancellationToken).ConfigureAwait(false);
        var fingerprints = await ReadFingerprintsAsync(unitId, cancellationToken).ConfigureAwait(false);
        var matches = await _reads.ListExactDuplicateMatchesAsync(unitId, cancellationToken).ConfigureAwait(false);

        // Auto-reuse authoritative exact duplicates: if the matched asset is already ACTIVE,
        // reuse is safe and no duplicate physical authority should be created. The user can
        // still change this decision in Verify before requesting import.
        var authoritativeMatches = matches
            .Where(match => match is { MatchedMediaState: MediaState.Active, Decision: null })
            .ToList();
        foreach (var match in authoritativeMatches)
        {
            try
            {
                await _catalog.ImportWrites.ApplyDuplicateDecisionAsync(
                    match.ImportItemId,
                    ItemDisposition.Reused,
                    DuplicateDecision.Reuse,
                    match.MatchedMediaId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Auto-reuse of authoritative duplicate {0:D} was not applied: {1}",
                    match.ImportItemId,
                    exception.GetType().Name);
            }
        }

        // Re-read matches after auto-reuse to get updated decisions.
        matches = await _reads.ListExactDuplicateMatchesAsync(unitId, cancellationToken).ConfigureAwait(false);

        var hashJobs = jobs
            .Where(job => string.Equals(job.Kind, "HashMedia", StringComparison.OrdinalIgnoreCase))
            .GroupBy(job => job.OwnerId)
            .ToDictionary(group => group.Key, group => group.First());
        var transferJobs = jobs
            .Where(job => string.Equals(job.Kind, "TransferOriginal", StringComparison.Ordinal))
            .GroupBy(job => job.OwnerId)
            .ToDictionary(group => group.Key, group => group.First());

        var reusedItems = matches
            .Where(match => match.RequiresDecision)
            .Select(match => match.ImportItemId)
            .ToHashSet();
        var setAside = new List<Guid>();
        var stillPreparing = false;

        foreach (var item in items)
        {
            if (reusedItems.Contains(item.ItemId)
                || item.Disposition is ItemDisposition.Skipped or ItemDisposition.Reused)
            {
                continue;
            }

            if (item.Disposition == ItemDisposition.Invalid)
            {
                setAside.Add(item.ItemId);
                continue;
            }

            if (item.CandidateMediaId is not { } candidateId)
            {
                continue;
            }

            if (transferJobs.TryGetValue(candidateId, out var transferJob)
                && transferJob.State == JobState.Succeeded
                && fingerprints.TryGetValue(candidateId, out var sha)
                && !string.IsNullOrWhiteSpace(sha))
            {
                continue;
            }

            if (transferJobs.TryGetValue(candidateId, out transferJob)
                && transferJob.State is JobState.FailedTerminal or JobState.Cancelled
                || hashJobs.TryGetValue(candidateId, out var hashJob)
                && hashJob.State is JobState.FailedTerminal or JobState.Cancelled)
            {
                setAside.Add(item.ItemId);
            }
            else
            {
                stillPreparing = true;
            }
        }

        if (setAside.Count > 0)
        {
            await _operations.SetBatchItemDispositionsAsync(
                setAside,
                ItemDisposition.Skipped,
                cancellationToken).ConfigureAwait(false);
            await RecordAttentionAsync(unitId, setAside, cancellationToken).ConfigureAwait(false);
        }

        return new AdmissionResult(!stillPreparing);
    }

    private async Task RecordAttentionAsync(
        Guid unitId,
        IReadOnlyList<Guid> itemIds,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var model = await _operations.LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                return;
            }

            var draft = VerificationDraftV1.FromJson(
                model.VerificationDraftJson,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            var merged = (draft.AttentionItemIds ?? []).Concat(itemIds).Distinct().ToList();
            try
            {
                await _operations.UpdateDraftAsync(
                    unitId,
                    draft with { AttentionItemIds = merged },
                    model.RowVersion,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (CatalogConcurrencyConflictException) when (attempt < 2)
            {
            }
        }
    }

    private async Task MarkFailureAsync(
        Guid unitId,
        bool retryable,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (retryable)
        {
            lock (_sync)
            {
                _attentionUntil[unitId] = _timeProvider.GetUtcNow().Add(AttentionBackoff);
            }
        }
        else
        {
            lock (_sync)
            {
                _attentionUntil.Remove(unitId);
            }
        }

        try
        {
            var summary = await _reads.GetUnitSummaryAsync(unitId, cancellationToken).ConfigureAwait(false);
            if (summary is not null
                && summary.State is not (ImportUnitState.Cancelled or ImportUnitState.FailedTerminal or ImportUnitState.Committing)
                && !summary.State.IsUnitCommitted())
            {
                var terminalState = retryable
                    ? ImportUnitState.FailedRetryable
                    : ImportUnitState.FailedTerminal;
                await _catalog.ImportWrites
                    .UpdateUnitStateAsync(
                        unitId,
                        terminalState,
                        expectedState: summary.State,
                        expectedRowVersion: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!retryable)
                {
                    await _catalog.ImportWrites
                        .SettleFailedCommitOperationAsync(unitId, errorCode, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceWarning(
                "Import {0:D} attention state could not be recorded: {1}",
                unitId,
                exception.GetType().Name);
        }
    }

    private static bool IsTransientCommitFailure(Exception exception) => exception switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode is 5 or 6 or 261 or 262,
        IOException or TimeoutException => true,
        _ => false,
    };

    private async Task<IReadOnlyList<Guid>> ReadRequestedUnitIdsAsync(CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT import_unit_id, verification_draft_json
            FROM import_units
            WHERE state NOT IN ('COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION','CANCELLED','FAILED_TERMINAL')
              AND library_commit_state NOT IN ('SOURCE_CLEANUP_PENDING','SOURCE_CLEANUP_COMPLETE','TERMINAL')
              AND is_paused = 0
            ORDER BY created_at_ms, import_unit_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var unitId = DbGuid.Parse(reader.GetString(0));
            var draftJson = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(draftJson))
            {
                continue;
            }

            VerificationDraftV1? draft;
            try
            {
                draft = VerificationDraftV1.FromJson(draftJson, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            }
            catch (InvalidOperationException exception)
            {
                Trace.TraceWarning(
                    "Import {0:D} draft JSON is malformed and was skipped by the finalizer: {1}",
                    unitId,
                    exception.Message);
                continue;
            }

            if (draft.IsImportRequested)
            {
                ids.Add(unitId);
            }
        }

        return ids;
    }

    private async Task<Dictionary<Guid, string?>> ReadFingerprintsAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string?>();
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.candidate_media_id, a.sha256
            FROM import_items i
            JOIN media a ON a.media_id = i.candidate_media_id
            WHERE i.import_unit_id = $unitId;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[DbGuid.Parse(reader.GetString(0))] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        return result;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyDictionary<Guid, ImportFinalizeOutcome> outcomes;
            try
            {
                outcomes = await RunPassAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Import finalizer pass failed: {0}", exception.GetType().Name);
                outcomes = new Dictionary<Guid, ImportFinalizeOutcome>();
            }

            var interval = outcomes.Values.Any(value => value == ImportFinalizeOutcome.Waiting)
                ? PendingInterval
                : IdleInterval;

            TaskCompletionSource wake;
            lock (_sync)
            {
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wake = _wake;
            }

            try
            {
                await Task.WhenAny(wake.Task, Task.Delay(interval, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        Wake();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetime.Dispose();
    }

    private readonly record struct AdmissionResult(bool ReadyToCommit);
}
