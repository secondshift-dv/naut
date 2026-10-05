using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.Import.Preparation;

/// <summary>
/// Handles media preparation job completion and durable terminal reconciliation: maps job outcomes to
/// capability state, closes failed/cancelled dependency graphs, then re-evaluates unit readiness.
/// </summary>
public sealed class ImportMediaPreparationCompletionHandler
{
    private readonly CatalogDb _catalog;
    private readonly CapabilityWrites _capabilityWrites;
    private readonly ImportMediaPreparationCoordinator _coordinator;
    private readonly SchedulerReads _schedulerReads;
    private readonly JobWrites _jobWrites;
    private readonly MediaAssetReads _mediaAssetReads;

    public ImportMediaPreparationCompletionHandler(
        CatalogDb catalog,
        ImportMediaPreparationCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(coordinator);
        _catalog = catalog;
        _capabilityWrites = new CapabilityWrites(catalog);
        _coordinator = coordinator;
        _schedulerReads = new SchedulerReads(catalog);
        _jobWrites = new JobWrites(catalog);
        _mediaAssetReads = new MediaAssetReads(catalog);
    }

    /// <summary>
    /// Called by the scheduler after normal terminal completion. Updates every capability covered by
    /// the job. Terminal non-success also closes dependent jobs immediately; the reconciliation loop
    /// remains the catch-all for idle cancellation, retry exhaustion, and restart recovery.
    /// </summary>
    public async Task HandleCompletionAsync(Guid jobId, JobExecutionResult result)
    {
        var jobInfo = await FindJobInfoAsync(jobId).ConfigureAwait(false);
        if (jobInfo is null)
        {
            return;
        }

        var (assetId, jobKind, mediaType) = jobInfo.Value;
        var capabilities = await ResolveCapabilitiesAsync(jobId, jobKind, mediaType).ConfigureAwait(false);
        if (capabilities.Count == 0)
        {
            return;
        }

        var targetState = result.IsSucceeded
            ? MediaCapabilityState.Ready
            : MediaCapabilityState.Failed;

        foreach (var capability in capabilities)
        {
            var state = targetState;
            if (result.IsSucceeded && mediaType == MediaType.Model
                && jobKind == "GenerateModelMediaAssets" && capability == MediaCapability.Thumbnail
                && await _mediaAssetReads.GetOwnedMediaAssetAsync(assetId, MediaAssetRole.Thumbnail)
                    .ConfigureAwait(false) is null)
                state = MediaCapabilityState.NotApplicable;
            await _capabilityWrites.UpsertCapabilityAsync(
                assetId, capability, state, jobId)
                .ConfigureAwait(false);
        }

        if (string.Equals(jobKind, "FaceAnalysis", StringComparison.OrdinalIgnoreCase))
        {
            if (result.IsSucceeded)
            {
                await ReconcileSuccessfulFaceAnalysisAsync(assetId, jobId).ConfigureAwait(false);
            }
            else
            {
                await ReconcileUnsuccessfulFaceAnalysisAsync(assetId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        var affectedMediaIds = new HashSet<Guid> { assetId };
        if (!result.IsSucceeded)
        {
            await CascadeDependentJobsAsync(
                    jobId,
                    new HashSet<Guid> { jobId },
                    affectedMediaIds,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        await TryReadinessForMediasAsync(affectedMediaIds, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MediaCapability>> ResolveCapabilitiesAsync(
        Guid jobId,
        string jobKind,
        MediaType mediaType)
    {
        var capabilities = ImportMediaPreparationCoordinator.MapJobKindToCapabilities(jobKind, mediaType);
        if (capabilities.Count > 0)
        {
            if (capabilities.Contains(MediaCapability.ModelRender))
            {
                var info = await FindJobInfoAsync(jobId).ConfigureAwait(false);
                if (info is null) return [];
                if (!await ModelRenderEligibility.IsEligibleAsync(_catalog, info.Value.MediaId).ConfigureAwait(false))
                    return capabilities.Where(c => c != MediaCapability.ModelRender).ToArray();
            }
            return capabilities;
        }

        var legacyCap = await FindCapabilityForJobAsync(jobId).ConfigureAwait(false);
        return legacyCap.HasValue ? [legacyCap.Value] : [];
    }

    private async Task ReconcileSuccessfulFaceAnalysisAsync(Guid assetId, Guid jobId)
    {
        var (faceCount, embeddedCount) = await CountFaceEmbeddingsAsync(assetId).ConfigureAwait(false);
        if (faceCount == 0)
        {
            await _capabilityWrites.UpsertCapabilityAsync(
                    assetId, MediaCapability.FaceEmbedding, MediaCapabilityState.NotApplicable)
                .ConfigureAwait(false);
        }
        else if (embeddedCount >= faceCount)
        {
            await _capabilityWrites.UpsertCapabilityAsync(
                    assetId, MediaCapability.FaceEmbedding, MediaCapabilityState.Ready, jobId)
                .ConfigureAwait(false);
        }
        else
        {
            await _capabilityWrites.UpsertCapabilityAsync(
                    assetId, MediaCapability.FaceEmbedding, MediaCapabilityState.Failed, jobId)
                .ConfigureAwait(false);
        }

        var hasRelatedEvidence = await HasRelatedEvidenceAsync(assetId).ConfigureAwait(false);
        await _capabilityWrites.UpsertCapabilityAsync(
                assetId,
                MediaCapability.SimilarityRelated,
                hasRelatedEvidence ? MediaCapabilityState.Ready : MediaCapabilityState.NotApplicable)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A failed or cancelled FaceAnalysis cannot produce new embeddings. Conditional capabilities
    /// must still become terminal so cancellation cannot strand SimilarityRelated in QUEUED.
    /// Existing durable related evidence remains authoritative if it already exists.
    /// </summary>
    private async Task ReconcileUnsuccessfulFaceAnalysisAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await _capabilityWrites.UpsertCapabilityAsync(
                assetId,
                MediaCapability.FaceEmbedding,
                MediaCapabilityState.NotApplicable,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var hasRelatedEvidence = await HasRelatedEvidenceAsync(assetId, cancellationToken).ConfigureAwait(false);
        await _capabilityWrites.UpsertCapabilityAsync(
                assetId,
                MediaCapability.SimilarityRelated,
                hasRelatedEvidence ? MediaCapabilityState.Ready : MediaCapabilityState.NotApplicable,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(Guid MediaId, string JobKind, MediaType MediaType)?> FindJobInfoAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT cr.media_id, j.kind, COALESCE(a.media_type, 'Image')
            FROM media_capability_readiness cr
            JOIN jobs j ON j.job_id = cr.job_id
            LEFT JOIN media a ON a.media_id = cr.media_id
            WHERE cr.job_id = $jobId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (
            DbGuid.Parse(reader.GetString(0)),
            reader.GetString(1),
            DbEnum.ParseMediaType(reader.GetString(2)));
    }

    private async Task<MediaCapability?> FindCapabilityForJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT capability
            FROM media_capability_readiness
            WHERE job_id = $jobId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return DbEnum.ParseMediaCapability(reader.GetString(0));
    }

    private async Task<IReadOnlyList<Guid>> FindUnitsForMediaAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT import_unit_id
            FROM import_items
            WHERE disposition IN ('INCLUDED', 'REUSED')
              AND (candidate_media_id = $assetId OR reused_media_id = $assetId);
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(DbGuid.Parse(reader.GetString(0)));
        }

        return ids;
    }

    private async Task<(int FaceCount, int EmbeddedCount)> CountFaceEmbeddingsAsync(Guid assetId)
    {
        await using var connection = await _catalog.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) AS face_count, COUNT(embedding) AS embedded_count
            FROM face_detections
            WHERE media_id = $assetId;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return (0, 0);
        }

        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private async Task<bool> HasRelatedEvidenceAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM related_profile_evidence WHERE media_id = $assetId);";
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// Reconciles durable terminal non-success jobs into capability truth. The query includes jobs
    /// whose own capability is already FAILED when they still have non-terminal dependents, which
    /// is required for dependency closure after a normal terminal failure.
    /// </summary>
    public async Task ReconcileTerminalCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT cr.media_id, j.kind, j.job_id, COALESCE(a.media_type, 'Image')
            FROM media_capability_readiness cr
            JOIN jobs j ON j.job_id = cr.job_id
            LEFT JOIN media a ON a.media_id = cr.media_id
            WHERE j.state IN ('FAILED_TERMINAL', 'CANCELLED')
              AND (
                    cr.state NOT IN ('READY', 'NOT_APPLICABLE', 'FAILED')
                    OR EXISTS (
                        SELECT 1
                        FROM job_dependencies d
                        JOIN jobs dependent ON dependent.job_id = d.job_id
                        WHERE d.depends_on_job_id = j.job_id
                          AND dependent.state NOT IN ('SUCCEEDED', 'FAILED_TERMINAL', 'CANCELLED')
                    )
                    OR (
                        j.kind = 'FaceAnalysis'
                        AND EXISTS (
                            SELECT 1
                            FROM media_capability_readiness conditional
                            WHERE conditional.media_id = cr.media_id
                              AND conditional.capability IN ('FaceEmbedding', 'SimilarityRelated')
                              AND conditional.state NOT IN ('READY', 'NOT_APPLICABLE', 'FAILED')
                        )
                    )
              );
            """;

        var terminalJobs = new List<(Guid MediaId, string JobKind, Guid JobId, MediaType MediaType)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                terminalJobs.Add((
                    DbGuid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    DbGuid.Parse(reader.GetString(2)),
                    DbEnum.ParseMediaType(reader.GetString(3))));
            }
        }

        var affectedMediaIds = new HashSet<Guid>();
        foreach (var (assetId, jobKind, jobId, mediaType) in terminalJobs)
        {
            affectedMediaIds.Add(assetId);

            var capabilities = await ResolveCapabilitiesAsync(jobId, jobKind, mediaType).ConfigureAwait(false);
            foreach (var capability in capabilities)
            {
                await _capabilityWrites.UpsertCapabilityAsync(
                        assetId,
                        capability,
                        MediaCapabilityState.Failed,
                        jobId,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.Equals(jobKind, "FaceAnalysis", StringComparison.OrdinalIgnoreCase))
            {
                await ReconcileUnsuccessfulFaceAnalysisAsync(assetId, cancellationToken).ConfigureAwait(false);
            }

            await CascadeDependentJobsAsync(
                    jobId,
                    new HashSet<Guid> { jobId },
                    affectedMediaIds,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await TryReadinessForMediasAsync(affectedMediaIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A terminal failed/cancelled predecessor makes non-success-dependent work impossible. Close
    /// each idle dependent durably as CANCELLED before projecting its capability to FAILED, then
    /// recurse so no durable job remains PENDING/RUNNABLE/PAUSED/FAILED_RETRYABLE forever.
    /// </summary>
    private async Task CascadeDependentJobsAsync(
        Guid terminalPredecessorJobId,
        HashSet<Guid> visited,
        HashSet<Guid> affectedMediaIds,
        CancellationToken cancellationToken)
    {
        var dependents = await _schedulerReads
            .GetDirectDependentsAsync(terminalPredecessorJobId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var dependentId in dependents)
        {
            if (!visited.Add(dependentId))
            {
                continue;
            }

            var dependent = await _schedulerReads.GetJobAsync(dependentId, cancellationToken)
                .ConfigureAwait(false);
            if (dependent is null || dependent.State == JobState.Succeeded)
            {
                continue;
            }

            if (dependent.State is not (JobState.FailedTerminal or JobState.Cancelled))
            {
                await _jobWrites.CancelIdleAsync(
                        dependentId,
                        null,
                        null,
                        TimeProvider.System.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);

                dependent = await _schedulerReads.GetJobAsync(dependentId, cancellationToken)
                    .ConfigureAwait(false);
            }

            // A RUNNING dependent should be unreachable because dispatch requires all predecessors
            // SUCCEEDED. If a race ever produces one, do not falsify terminal truth; a later pass
            // will reconcile it after the scheduler settles the durable job state.
            if (dependent is null || dependent.State is not (JobState.FailedTerminal or JobState.Cancelled))
            {
                continue;
            }

            var depInfo = await FindJobInfoAsync(dependentId, cancellationToken).ConfigureAwait(false);
            if (depInfo is not null)
            {
                var (depMediaId, depJobKind, depMediaType) = depInfo.Value;
                affectedMediaIds.Add(depMediaId);
                var depCaps = await ResolveCapabilitiesAsync(dependentId, depJobKind, depMediaType)
                    .ConfigureAwait(false);
                foreach (var cap in depCaps)
                {
                    await _capabilityWrites.UpsertCapabilityAsync(
                            depMediaId,
                            cap,
                            MediaCapabilityState.Failed,
                            dependentId,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                if (string.Equals(depJobKind, "FaceAnalysis", StringComparison.OrdinalIgnoreCase))
                {
                    await ReconcileUnsuccessfulFaceAnalysisAsync(depMediaId, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            await CascadeDependentJobsAsync(dependentId, visited, affectedMediaIds, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task TryReadinessForMediasAsync(
        IEnumerable<Guid> assetIds,
        CancellationToken cancellationToken)
    {
        var affectedUnitIds = new HashSet<Guid>();
        foreach (var assetId in assetIds)
        {
            var unitIds = await FindUnitsForMediaAsync(assetId, cancellationToken).ConfigureAwait(false);
            foreach (var unitId in unitIds)
            {
                affectedUnitIds.Add(unitId);
            }
        }

        foreach (var unitId in affectedUnitIds)
        {
            try
            {
                await _coordinator.TryTransitionToReadyAsync(unitId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Readiness is recoverable/idempotent and will be retried by the bounded scheduler
                // reconciliation pass or a later job completion.
            }
        }
    }
}
