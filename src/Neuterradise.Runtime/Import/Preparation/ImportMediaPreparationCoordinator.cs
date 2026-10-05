using System.Text.Json;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Jobs.Handlers;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Import.Preparation;

/// <summary>
/// Orchestrates per-Media capability preparation from completed Candidate staging bytes and
/// final managed originals.
///
/// Effective order:
///   completed TransferOriginal or reusable ACTIVE Media
///   → seed required capabilities per asset
///   → schedule independent jobs reading Vault-owned media
///   → each job updates its capability to READY on success
///   → readiness join: all applicable capabilities terminal → ReadyForVerification
///
/// One slow asset does NOT serialize unrelated media.
/// One slow capability does NOT serialize unrelated capabilities.
/// </summary>
public sealed class ImportMediaPreparationCoordinator
{
    private readonly CatalogDb _catalog;
    private readonly CapabilityReads _capabilityReads;
    private readonly CapabilityWrites _capabilityWrites;
    private readonly ImportWrites _importWrites;
    private readonly ImportReads _importReads;
    private readonly JobWrites _jobWrites;
    private readonly VaultPaths _vaultPaths;
    private readonly TimeProvider _timeProvider;

    public ImportMediaPreparationCoordinator(
        CatalogDb catalog,
        VaultPaths vaultPaths,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(vaultPaths);
        _catalog = catalog;
        _vaultPaths = vaultPaths;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _capabilityReads = new CapabilityReads(catalog);
        _capabilityWrites = new CapabilityWrites(catalog, timeProvider);
        _importWrites = new ImportWrites(catalog, timeProvider);
        _importReads = catalog.ImportReads;
        _jobWrites = new JobWrites(catalog, timeProvider);
    }

    /// <summary>
    /// Entry point: seed capabilities and schedule media preparation jobs for all eligible media.
    /// Called before and after canonical commit DomainAuthorityCommitted. Existing terminal capabilities
    /// are not disturbed, and jobs are created idempotently by deterministic JobId.
    /// </summary>
    public async Task<MediaPreparationScheduleResult> ScheduleForCurrentPhaseAsync(
        Guid unitId,
        CancellationToken cancellationToken = default,
        Guid? onlyMediaId = null)
    {
        var unit = await _importReads.GetUnitSummaryAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
        {
            return new MediaPreparationScheduleResult(unitId, 0, 0, false);
        }
        var candidatePhase = !unit.LibraryCommitState.HasReachedDomainCommit();

        // The durable timestamp is the human-facing Stage 2 boundary: permanent MediaAssets only
        // begin after the canonical original has crossed the domain-commit boundary. Candidate
        // preparation remains Stage 1 and must not start the Stage 2 stopwatch.
        if (!candidatePhase)
        {
            await MarkPreparationStartedAsync(unitId, cancellationToken).ConfigureAwait(false);
        }

        var items = await _importReads.GetUnitItemsUnboundedAsync(unitId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        int assetsScheduled = 0;
        int jobsScheduled = 0;
        var processedMediaIds = new HashSet<Guid>();

        foreach (var item in items)
        {
            if (item.Disposition is ItemDisposition.Skipped or ItemDisposition.Invalid)
            {
                continue;
            }

            // canonical commit duplicate reuse retires the transient candidate and records the active
            // canonical asset in reused_media_id. media preparation must operate on that effective asset,
            // not the retired candidate identity.
            var effectiveMediaId = item.Disposition == ItemDisposition.Reused
                ? item.ReusedMediaId
                : item.CandidateMediaId;
            if (effectiveMediaId is not { } assetId || !processedMediaIds.Add(assetId))
            {
                continue;
            }
            if (onlyMediaId is { } selected && assetId != selected) continue;
            if (candidatePhase && item.Disposition == ItemDisposition.Reused)
            {
                // Exact duplicates already have canonical Media/People evidence; do not spend
                // another YuNet/SFace pass on identical bytes.
                continue;
            }

            await _jobWrites.RegisterImportMediaInterestAsync(unitId, assetId, cancellationToken)
                .ConfigureAwait(false);

            // Reused items resolve from the effective ACTIVE Media, not the retired Candidate.
            var mediaType = await ReadLiveMediaAssetTypeAsync(assetId, cancellationToken).ConfigureAwait(false);
            if (mediaType is null)
            {
                continue;
            }

            // Seed capabilities: deterministic per media type. All applicable capabilities get a row;
            // required ones gate readiness, optional ones are informational.
            var allCapabilities = CapabilityApplicability.GetAll(mediaType.Value);
            var eligibleModel = mediaType == MediaType.Model
                && await ModelRenderEligibility.IsEligibleAsync(_catalog, assetId, cancellationToken).ConfigureAwait(false);
            foreach (var (cap, _) in allCapabilities)
            {
                if (candidatePhase && cap is not (MediaCapability.Metadata
                    or MediaCapability.FaceDetection or MediaCapability.FaceEmbedding
                    or MediaCapability.SimilarityRelated))
                    continue;
                if (!candidatePhase && cap is MediaCapability.FaceDetection
                    or MediaCapability.FaceEmbedding or MediaCapability.SimilarityRelated)
                    continue;
                var initialState = cap == MediaCapability.ManagedOriginal
                    ? MediaCapabilityState.Ready
                    : cap == MediaCapability.ModelRender && !eligibleModel
                    ? MediaCapabilityState.NotApplicable
                    : MediaCapabilityState.Queued;
                await _capabilityWrites.UpsertCapabilityAsync(
                    assetId, cap, initialState, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // No detected face leaves FaceEmbedding NOT_APPLICABLE; a successful face job
            // promotes it to READY only when every persisted detection has an embedding.
            if (CapabilityApplicability.IsFaceEmbeddingApplicable(mediaType.Value))
            {
                await _capabilityWrites.MarkNotApplicableAsync(
                    assetId, MediaCapability.FaceEmbedding, cancellationToken).ConfigureAwait(false);
            }

            // Candidate staging or final managed bytes are the only preparation inputs.
            var vaultPath = await ReadPreparationPathAsync(assetId, cancellationToken).ConfigureAwait(false);
            if (vaultPath is null)
            {
                // Managed-path scheduling failure: the asset has no canonical vault path, so no
                // media preparation jobs can be created. Mark all non-terminal capabilities as FAILED so the
                // asset surfaces as needing attention rather than blocking readiness silently.
                // The C012A readiness denominator still includes this asset (capability rows exist).
                foreach (var (cap, _) in allCapabilities)
                {
                    if (cap == MediaCapability.ModelRender && !eligibleModel) continue;
                    await _capabilityWrites.UpsertCapabilityAsync(
                        assetId, cap, MediaCapabilityState.Failed,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            var fingerprint = await ReadMediaFingerprintAsync(assetId, cancellationToken).ConfigureAwait(false);

            // Schedule independent capability jobs. All read from canonical vault media.
            var assetJobs = await ScheduleMediaCapabilitiesAsync(
                assetId, item.ItemId, unitId, mediaType.Value, vaultPath, fingerprint,
                candidatePhase, cancellationToken).ConfigureAwait(false);

            jobsScheduled += assetJobs;
            assetsScheduled++;
        }

        // Evaluate readiness after scheduling.
        var readiness = await EvaluateReadinessAsync(unitId, cancellationToken).ConfigureAwait(false);

        return new MediaPreparationScheduleResult(unitId, assetsScheduled, jobsScheduled, readiness.AllRequiredTerminal);
    }

    /// <summary>
    /// Re-evaluate media preparation readiness. Called by progress observers and after job completion.
    /// Returns whether all media have all applicable capabilities in terminal state.
    /// </summary>
    public async Task<MediaPreparationReadiness> EvaluateReadinessAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        await ReconcilePersistedArtifactsAsync(unitId, cancellationToken).ConfigureAwait(false);
        var summary = await _capabilityReads.GetUnitCapabilitySummaryAsync(unitId, cancellationToken)
            .ConfigureAwait(false);
        var failedMediaIds = await _capabilityReads.GetUnitFailedMediaIdsAsync(unitId, cancellationToken)
            .ConfigureAwait(false);

        return new MediaPreparationReadiness(
            summary.AllRequiredTerminal,
            summary.MediasAllCapabilitiesTerminal,
            summary.TotalMedias,
            failedMediaIds,
            HasFailures: failedMediaIds.Count > 0);
    }

    /// <summary>
    /// Applies the media preparation execution join to the Import Unit. Once every Required capability has
    /// settled, the unit enters Verify. A durable Required failure is a review blocker there; it is
    /// not allowed to strand the whole import in FAILED_TERMINAL.
    /// </summary>
    public async Task<bool> TryTransitionToReadyAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        await ReconcilePersistedArtifactsAsync(unitId, cancellationToken).ConfigureAwait(false);

        var allRequiredSettled = await _capabilityReads
            .AreAllRequiredCapabilitiesSettledAsync(unitId, cancellationToken)
            .ConfigureAwait(false);
        var unit = await _importReads.GetUnitSummaryAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null || !unit.LibraryCommitState.HasReachedDomainCommit()
            || unit.State is ImportUnitState.Cancelled or ImportUnitState.FailedTerminal
            || unit.State.IsUnitCommitted())
        {
            return false;
        }

        if (!allRequiredSettled)
        {
            return false;
        }

        if (unit.State != ImportUnitState.ReadyForVerification)
        {
            await _importWrites
                .UpdateUnitStateAsync(
                    unitId,
                    ImportUnitState.ReadyForVerification,
                    expectedState: unit.State,
                    expectedRowVersion: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Reconciles READY rows against the durable MediaAsset file and catalog record. Missing or
    /// changed bytes must not leave a required capability falsely ready.
    /// </summary>
    private async Task ReconcilePersistedArtifactsAsync(Guid unitId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT a.media_id, cr.capability, cr.job_id
            FROM import_items ii
            JOIN media a ON a.media_id = CASE
                WHEN ii.disposition = 'REUSED' THEN ii.reused_media_id
                ELSE ii.candidate_media_id
            END
            JOIN media_capability_readiness cr ON cr.media_id = a.media_id
            WHERE ii.import_unit_id = $unitId
              AND ii.disposition IN ('INCLUDED', 'REUSED')
              AND cr.state = 'READY';
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var missing = new List<(Guid MediaId, MediaCapability Capability, Guid? JobId)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var assetId = DbGuid.Parse(reader.GetString(0));
            var capability = DbEnum.ParseMediaCapability(reader.GetString(1));
            var jobId = reader.IsDBNull(2) ? (Guid?)null : DbGuid.Parse(reader.GetString(2));

            if (!await IsPersistedArtifactPresentAsync(assetId, capability, cancellationToken).ConfigureAwait(false))
            {
                missing.Add((assetId, capability, jobId));
            }
        }

        foreach (var (assetId, capability, jobId) in missing)
        {
            await _capabilityWrites.UpsertCapabilityAsync(
                    assetId,
                    capability,
                    MediaCapabilityState.Failed,
                    jobId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> IsPersistedArtifactPresentAsync(
        Guid assetId, MediaCapability capability, CancellationToken ct)
    {
        if (capability is MediaCapability.ManagedOriginal or MediaCapability.Metadata
            or MediaCapability.FaceDetection or MediaCapability.FaceEmbedding
            or MediaCapability.SearchProjection or MediaCapability.SimilarityRelated)
        {
            return true;
        }

        if (capability is not (MediaCapability.Thumbnail or MediaCapability.Hover or MediaCapability.ModelRender)) return true;
        var role = capability switch { MediaCapability.Hover => MediaAssetRole.Hover,
            MediaCapability.ModelRender => MediaAssetRole.ModelRender, _ => MediaAssetRole.Thumbnail };
        var mediaAsset = await new MediaAssetReads(_catalog).GetOwnedMediaAssetAsync(assetId, role, ct).ConfigureAwait(false);
        if (mediaAsset?.State != MediaAssetState.Ready) return false;
        try
        {
            var path = _vaultPaths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, mediaAsset.RelativePath);
            var sourceHash = role == MediaAssetRole.ModelRender
                ? (await _catalog.MediaReads.GetMediaSourceAsync(assetId, ct).ConfigureAwait(false))?.Sha256 : null;
            if (role == MediaAssetRole.ModelRender && sourceHash is null) return false;
            var valid = await new MediaAssetFileValidator().ValidateAsync(path, role, ct, sourceHash).ConfigureAwait(false);
            return valid.ByteLength == mediaAsset.ByteLength && valid.Sha256 == mediaAsset.Sha256
                && valid.PixelWidth == mediaAsset.PixelWidth && valid.PixelHeight == mediaAsset.PixelHeight
                && valid.DurationMs == mediaAsset.DurationMs;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Explicit user recovery for one media item with durable Required preparation failures.
    /// Failed jobs remain immutable history. A successor identity is derived from the predecessor,
    /// making crash/retry idempotent while allowing another successor if the recovery itself fails.
    /// </summary>
    public async Task<PreparationRetryResult> RetryRequiredPreparationAsync(
        Guid unitId,
        Guid importItemId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty || importItemId == Guid.Empty)
        {
            return PreparationRetryResult.Blocked(
                "INVALID_RECOVERY_TARGET",
                "That media recovery target is no longer valid.");
        }

        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        var unit = await _importReads.GetUnitSummaryAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
        {
            return PreparationRetryResult.Blocked("UNIT_NOT_FOUND", "That import no longer exists.");
        }

        if (unit.State.IsUnitCommitted()
            || unit.State is ImportUnitState.Committing or ImportUnitState.Cancelled)
        {
            return PreparationRetryResult.Blocked(
                "RECOVERY_NOT_ALLOWED",
                "This import is no longer in a state where media preparation can be retried.");
        }

        var failureMap = await _importReads
            .ListRequiredPreparationFailuresAsync([unitId], cancellationToken)
            .ConfigureAwait(false);
        var failures = failureMap.GetValueOrDefault(unitId)?
            .Where(failure => failure.ImportItemId == importItemId)
            .ToArray() ?? [];

        if (failures.Length == 0)
        {
            return PreparationRetryResult.NotRequired();
        }

        var recoveryJobs = new List<RecoveryJob>();
        foreach (var assetFailures in failures.GroupBy(failure => new { failure.MediaId, failure.MediaType }))
        {
            var vaultPath = await ReadPreparationPathAsync(assetFailures.Key.MediaId, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(vaultPath))
            {
                return PreparationRetryResult.Blocked(
                    "CANONICAL_MEDIA_UNAVAILABLE",
                    "The Vault copy for this media is unavailable. Cancel the import or repair Vault storage before retrying.");
            }

            var fingerprint = await ReadMediaFingerprintAsync(assetFailures.Key.MediaId, cancellationToken)
                .ConfigureAwait(false);

            var normalized = new List<(ImportPreparationFailure Failure, MediaCapability Capability, string JobKind)>();
            foreach (var failure in assetFailures)
            {
                if (!Enum.TryParse<MediaCapability>(failure.Capability, ignoreCase: true, out var capability))
                {
                    return PreparationRetryResult.Blocked(
                        "UNKNOWN_PREPARATION_CAPABILITY",
                        "The failed preparation step is not recognized by this build.");
                }

                var jobKind = failure.JobKind ?? InferRecoveryJobKind(capability, failure.MediaType);
                if (string.IsNullOrWhiteSpace(jobKind))
                {
                    return PreparationRetryResult.Blocked(
                        "PREPARATION_RECOVERY_UNAVAILABLE",
                        $"Required {failure.Capability} preparation cannot be retried automatically.");
                }

                normalized.Add((failure, capability, jobKind));
            }

            foreach (var group in normalized.GroupBy(item => new { item.Failure.JobId, item.JobKind }))
            {
                var predecessorJobId = group.Key.JobId ?? DeriveJobId(assetFailures.Key.MediaId, group.Key.JobKind);
                var successorJobId = DeriveRecoveryJobId(predecessorJobId);
                var definition = CreateRecoveryJobDefinition(
                    successorJobId,
                    predecessorJobId,
                    assetFailures.Key.MediaId,
                    importItemId,
                    assetFailures.Key.MediaType,
                    vaultPath,
                    group.Key.JobKind);

                recoveryJobs.Add(new RecoveryJob(
                    definition,
                    group.Key.JobId,
                    assetFailures.Key.MediaId,
                    fingerprint,
                    group.Select(item => item.Capability).Distinct().ToArray()));
            }
        }

        if (!await _importWrites.BeginRequiredPreparationRetryAsync(unitId, cancellationToken)
                .ConfigureAwait(false))
        {
            return PreparationRetryResult.Blocked(
                "RECOVERY_STATE_CHANGED",
                "The import changed while recovery was starting. Reopen Review and try again.");
        }

        foreach (var assetId in recoveryJobs.Select(job => job.MediaId).Distinct())
        {
            await _jobWrites.RegisterImportMediaInterestAsync(unitId, assetId, cancellationToken)
                .ConfigureAwait(false);
        }

        var resetCapabilities = 0;
        var scheduledJobs = new HashSet<Guid>();
        foreach (var recovery in recoveryJobs)
        {
            await _jobWrites.CreateJobAsync(recovery.Definition, cancellationToken).ConfigureAwait(false);
            var recoveryHashJobId = CandidatePreparationPlan.DeriveHashJobId(recovery.MediaId);
            if (await JobExistsAsync(recoveryHashJobId, cancellationToken).ConfigureAwait(false))
            {
                await _jobWrites.AddDependencyAsync(
                        recovery.Definition.JobId,
                        recoveryHashJobId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var reboundAnyCapability = false;
            foreach (var capability in recovery.Capabilities)
            {
                if (await _capabilityWrites.ResetFailedCapabilityForRetryAsync(
                        recovery.MediaId,
                        capability,
                        recovery.ExpectedFailedJobId,
                        recovery.Definition.JobId,
                        recovery.SourceFingerprint,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    resetCapabilities++;
                    reboundAnyCapability = true;
                }
            }

            if (reboundAnyCapability)
            {
                await ReconcileRecoveryJobAfterBindingAsync(recovery, cancellationToken)
                    .ConfigureAwait(false);
            }

            scheduledJobs.Add(recovery.Definition.JobId);
        }

        if (resetCapabilities == 0)
        {
            await _importWrites.UpdateUnitStateAsync(
                    unitId,
                    ImportUnitState.ReadyForVerification,
                    expectedState: ImportUnitState.Preparing,
                    expectedRowVersion: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return PreparationRetryResult.NotRequired();
        }

        JobSignals.Raise();
        return PreparationRetryResult.Accepted(scheduledJobs.Count);
    }

    private async Task<int> ScheduleMediaCapabilitiesAsync(
        Guid assetId,
        Guid importItemId,
        Guid unitId,
        MediaType mediaType,
        string vaultPath,
        string? fingerprint,
        bool candidatePhase,
        CancellationToken cancellationToken)
    {
        var jobsCreated = 0;

        // Candidate jobs depend on completed transfer as well as the deterministic hash job.
        // Reused Medias retain their existing managed-byte authority.
        var hashJobId = CandidatePreparationPlan.DeriveHashJobId(assetId);
        var hashJobExists = await JobExistsAsync(hashJobId, cancellationToken).ConfigureAwait(false);
        var isCandidate = await IsCandidateAsync(assetId, cancellationToken).ConfigureAwait(false);
        var transferJobId = CandidatePreparationPlan.DeriveTransferJobId(assetId);
        if (isCandidate && !await JobExistsAsync(transferJobId, cancellationToken).ConfigureAwait(false))
        {
            throw new CatalogInvariantException(
                $"Candidate media preparation requires its durable TransferOriginal predecessor for Media {assetId:D}.");
        }

        // Metadata
        var metadataJobId = DeriveJobId(assetId, "ExtractMetadata");
        var metadataLane = mediaType == MediaType.Image ? JobLane.Cpu : JobLane.Media;
        var metadataCheckpoint = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            sourceKind = "vault",
            vaultPath,
            importItemId = importItemId.ToString("D"),
            mediaType = mediaType.ToString(),
        });
        var metadataJob = new JobDefinition(
            metadataJobId, "ExtractMetadata", metadataLane, JobState.Pending,
            JobPriorityPolicy.PriorityBackground,
            "Media", assetId, JobRetryPolicy.DefaultMaxAttempts, CheckpointJson: metadataCheckpoint);

        // Preview / Thumbnail
        var previewLane = mediaType == MediaType.Image ? JobLane.Cpu : JobLane.Media;
        var previewKind = mediaType switch
        {
            MediaType.Image => "GenerateThumbnail",
            MediaType.Video => "GenerateVideoMediaAssets",
            MediaType.Model => "GenerateModelMediaAssets",
            _ => "GenerateThumbnail",
        };
        var previewJobId = DeriveJobId(assetId, previewKind);
        var previewCheckpoint = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            sourceKind = "vault",
            vaultPath,
            importItemId = importItemId.ToString("D"),
            mediaType = mediaType.ToString(),
        });
        var previewJob = new JobDefinition(
            previewJobId, previewKind, previewLane, JobState.Pending,
            JobPriorityPolicy.PriorityVisible,
            "Media", assetId, JobRetryPolicy.DefaultMaxAttempts, CheckpointJson: previewCheckpoint);

        // Face detection
        JobDefinition? faceJob = null;
        if (mediaType is MediaType.Image or MediaType.Video)
        {
            var faceJobId = DeriveJobId(assetId, "FaceAnalysis");
            var faceCheckpoint = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                sourceKind = "vault",
                vaultPath,
                importItemId = importItemId.ToString("D"),
                mediaType = mediaType.ToString(),
            });
            faceJob = new JobDefinition(
                faceJobId, "FaceAnalysis", JobLane.Face, JobState.Pending,
                JobPriorityPolicy.PriorityBackground,
                "Media", assetId, JobRetryPolicy.DefaultMaxAttempts, CheckpointJson: faceCheckpoint);
        }

        // Candidate preparation is identity-only: metadata plus bounded People analysis.
        // MediaAsset derivation is post-commit only and never reruns FaceAnalysis.
        var allJobs = candidatePhase
            ? new List<JobDefinition> { metadataJob }
            : new List<JobDefinition> { previewJob };
        if (candidatePhase && faceJob is not null)
        {
            allJobs.Add(faceJob);
        }

        foreach (var job in allJobs)
        {
            await _jobWrites.CreateJobAsync(job, cancellationToken).ConfigureAwait(false);
            jobsCreated++;

            // Candidate preparation can read only the completed staged transfer. Reused ACTIVE
            // Medias have no Candidate transfer and retain their existing managed authority.
            if (hashJobExists)
            {
                await _jobWrites.AddDependencyAsync(job.JobId, hashJobId, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (isCandidate)
            {
                await _jobWrites.AddDependencyAsync(job.JobId, transferJobId, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (job.Kind == "FaceAnalysis" && mediaType == MediaType.Video)
                await _jobWrites.AddDependencyAsync(job.JobId, metadataJobId, cancellationToken)
                    .ConfigureAwait(false);

            // Update capability rows with the job ID. One job may satisfy multiple capabilities.
            var caps = MapJobKindToCapabilities(job.Kind, mediaType);
            foreach (var cap in caps)
            {
                if (cap == MediaCapability.ModelRender
                    && !await ModelRenderEligibility.IsEligibleAsync(_catalog, assetId, cancellationToken).ConfigureAwait(false))
                    continue;
                await _capabilityWrites.UpsertCapabilityAsync(
                    assetId, cap, MediaCapabilityState.Queued,
                    job.JobId, fingerprint, cancellationToken).ConfigureAwait(false);
            }
        }

        // FaceEmbedding: conditional applicability. Initially NOT_APPLICABLE; after FaceDetection
        // completes, the completion handler checks persisted face_detections. If faces found,
        // FaceEmbedding transitions to READY (SFace embeddings are persisted by the same worker).
        // If zero faces, it stays NOT_APPLICABLE. This is genuine conditional applicability.

        // SearchProjection: the asset row + metadata are in the DB after canonical commit commit.
        // Gallery/search queries read directly from media/profiles tables. No separate
        // projection step exists or is needed. Mark READY immediately.
        if (!candidatePhase && mediaType is MediaType.Image or MediaType.Video)
        {
            await _capabilityWrites.UpsertCapabilityAsync(
                assetId, MediaCapability.SearchProjection, MediaCapabilityState.Ready,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // SimilarityRelated: depends on face detection results + related evidence projection.
        // Only applicable to Image/Video where face-based related profiles are surfaced.
        // Left QUEUED; the completion handler transitions it after face analysis determines
        // whether similarity evidence applies. Genuine conditional applicability.
        // For Model, GetAll() does not include it so no action needed.

        return jobsCreated;
    }

    /// <summary>
    /// Closes the only completion race in manual recovery. Job creation wakes the scheduler, so a
    /// very fast successor can finish before its failed capability row has been rebound to that job.
    /// Normal completion then has no capability authority to project into. After the rebind, read the
    /// durable job state once: a terminal successor is projected immediately; a non-terminal one is
    /// left to the ordinary completion path. Either ordering therefore converges on the same state.
    /// </summary>
    private async Task ReconcileRecoveryJobAfterBindingAsync(
        RecoveryJob recovery,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM jobs WHERE job_id = $jobId;";
        command.Parameters.AddWithValue("$jobId", DbGuid.Format(recovery.Definition.JobId));
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (scalar is null or DBNull)
        {
            return;
        }

        var state = DbEnum.ParseJobState(Convert.ToString(
            scalar,
            System.Globalization.CultureInfo.InvariantCulture)!);
        var targetState = state switch
        {
            JobState.Succeeded => MediaCapabilityState.Ready,
            JobState.FailedTerminal or JobState.Cancelled => MediaCapabilityState.Failed,
            _ => (MediaCapabilityState?)null,
        };

        if (targetState is null)
        {
            return;
        }

        foreach (var capability in recovery.Capabilities)
        {
            await _capabilityWrites.UpsertCapabilityAsync(
                    recovery.MediaId,
                    capability,
                    targetState.Value,
                    recovery.Definition.JobId,
                    recovery.SourceFingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static JobDefinition CreateRecoveryJobDefinition(
        Guid jobId,
        Guid predecessorJobId,
        Guid assetId,
        Guid importItemId,
        MediaType mediaType,
        string vaultPath,
        string jobKind)
    {
        var (lane, priority) = jobKind switch
        {
            "ExtractMetadata" => (mediaType == MediaType.Image ? JobLane.Cpu : JobLane.Media, JobPriorityPolicy.PriorityBackground),
            "GenerateThumbnail" => (mediaType == MediaType.Video ? JobLane.Media : JobLane.Cpu, JobPriorityPolicy.PriorityVisible),
            "GenerateVideoMediaAssets" => (JobLane.Media, JobPriorityPolicy.PriorityVisible),
            "GenerateModelMediaAssets" => (JobLane.Media, JobPriorityPolicy.PriorityVisible),
            _ => throw new CatalogInvariantException($"Unsupported media preparation recovery job kind '{jobKind}'."),
        };

        var checkpoint = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            sourceKind = "vault",
            vaultPath,
            importItemId = importItemId.ToString("D"),
            mediaType = mediaType.ToString(),
            recovery = new
            {
                retryOfJobId = predecessorJobId.ToString("D"),
            },
        });

        return new JobDefinition(
            jobId,
            jobKind,
            lane,
            JobState.Pending,
            priority,
            "Media",
            assetId,
            JobRetryPolicy.DefaultMaxAttempts,
            CheckpointJson: checkpoint);
    }

    private static string? InferRecoveryJobKind(MediaCapability capability, MediaType mediaType) => capability switch
    {
        MediaCapability.Metadata => "ExtractMetadata",
        MediaCapability.Hover => "GenerateVideoMediaAssets",
        MediaCapability.ModelRender => "GenerateModelMediaAssets",
        MediaCapability.Thumbnail => mediaType switch
        {
            MediaType.Model => "GenerateModelMediaAssets",
            MediaType.Video => "GenerateVideoMediaAssets",
            _ => "GenerateThumbnail",
        },
        _ => null,
    };

    private static Guid DeriveRecoveryJobId(Guid predecessorJobId)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
            $"{predecessorJobId:D}:MediaPreparation:manual-recovery:v1"));
        var guidBytes = new byte[16];
        Array.Copy(bytes, guidBytes, 16);
        return new Guid(guidBytes);
    }

    /// <summary>
    /// Returns the single MediaAsset or metadata capability owned by a completed job.
    /// </summary>
    public static IReadOnlyList<MediaCapability> MapJobKindToCapabilities(string jobKind, MediaType mediaType) => jobKind switch
    {
        "ExtractMetadata" => [MediaCapability.Metadata],
        "GenerateThumbnail" => [MediaCapability.Thumbnail],
        "GenerateVideoMediaAssets" => [MediaCapability.Thumbnail, MediaCapability.Hover],
        "GenerateModelMediaAssets" => [MediaCapability.Thumbnail, MediaCapability.ModelRender],
        "FaceAnalysis" => [MediaCapability.FaceDetection],
        _ => [],
    };

    private async Task<MediaType?> ReadLiveMediaAssetTypeAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT media_type
            FROM media
            WHERE media_id = $assetId
              AND state IN ('CANDIDATE', 'ACTIVE');
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return value is null ? null : DbEnum.ParseMediaType(value);
    }

    private async Task<string?> ReadPreparationPathAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var source = await _catalog.MediaReads.GetMediaSourceAsync(assetId, cancellationToken)
            .ConfigureAwait(false);
        return source?.ResolveManagedPath(_vaultPaths);
    }

    private async Task<bool> JobExistsAsync(Guid jobId, CancellationToken ct)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM jobs WHERE job_id = $id);";
        command.Parameters.AddWithValue("$id", DbGuid.Format(jobId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1;
    }

    private async Task<bool> IsCandidateAsync(Guid assetId, CancellationToken ct)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state = 'CANDIDATE' FROM media WHERE media_id = $id;";
        command.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1;
    }

    private async Task<string?> ReadMediaFingerprintAsync(Guid assetId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256 FROM media WHERE media_id = $assetId;";
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private async Task MarkPreparationStartedAsync(Guid unitId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE import_units
            SET preparation_started_at_ms = $now
            WHERE import_unit_id = $unitId AND preparation_started_at_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// HashMedia keeps the pre-commit deterministic identity because media preparation references it as a
    /// prerequisite. Preparation jobs use their own namespace to avoid idempotency collisions.
    /// </summary>
    private sealed record RecoveryJob(
        JobDefinition Definition,
        Guid? ExpectedFailedJobId,
        Guid MediaId,
        string? SourceFingerprint,
        IReadOnlyList<MediaCapability> Capabilities);

    private static Guid DeriveJobId(Guid ownerId, string jobKind)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var identity = $"{ownerId:D}:MediaPreparation:v1:{jobKind}";
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity));
        var guidBytes = new byte[16];
        Array.Copy(bytes, guidBytes, 16);
        return new Guid(guidBytes);
    }
}

public sealed record MediaPreparationScheduleResult(
    Guid UnitId,
    int MediasScheduled,
    int JobsScheduled,
    bool AllRequiredTerminal);

public sealed record MediaPreparationReadiness(
    bool AllRequiredTerminal,
    int MediasAllCapabilitiesTerminal,
    int TotalMedias,
    IReadOnlyList<Guid> FailedMediaIds,
    bool HasFailures);

public sealed record PreparationRetryResult(
    bool IsStarted,
    int JobsScheduled,
    string? Code,
    string? UserMessage)
{
    public static PreparationRetryResult Accepted(int jobsScheduled) =>
        new(true, jobsScheduled, null, null);

    public static PreparationRetryResult NotRequired() =>
        new(false, 0, "PREPARATION_ALREADY_RECOVERED", "This media no longer has a failed Required preparation step.");

    public static PreparationRetryResult Blocked(string code, string message) =>
        new(false, 0, code, message);
}
