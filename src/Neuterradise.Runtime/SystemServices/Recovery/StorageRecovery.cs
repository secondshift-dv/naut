using Microsoft.Data.Sqlite;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Import;
using Neuterradise.App.Import.Preparation;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Recovery;

public sealed class StorageRecovery
{
    private const string ProfileRenameSql =
        """
        SELECT p.profile_id, p.reconciliation_operation_id
        FROM profiles p
        JOIN storage_operations o ON o.operation_id = p.reconciliation_operation_id
        WHERE p.path_state IN ('PENDING', 'NEEDS_ATTENTION')
          AND o.kind = 'PROFILE_RENAME'
        ORDER BY p.profile_id;
        """;

    private const string OwnerRelocationSql =
        """
        SELECT a.media_id, a.reconciliation_operation_id
        FROM media a
        JOIN storage_operations o ON o.operation_id = a.reconciliation_operation_id
        WHERE a.path_state IN ('PENDING', 'NEEDS_ATTENTION')
          AND o.kind = 'OWNER_RELOCATION'
        ORDER BY a.media_id;
        """;

    private readonly ImportCommitCoordinator _commitCoordinator;
    private readonly CatalogDb _catalog;
    private readonly PathReconciler _pathReconciler;
    private readonly ProfileManifestWriter _manifestWriter;
    private readonly ImportSourceTimestampReconciler _timestampReconciler;
    private readonly ImportMediaPreparationCoordinator _mediaPreparation;

    public async Task<IReadOnlyList<RecoveryFinding>> RecoverMediaAssetsAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<(Guid MediaId, string Role, string MediaType, string Path,
            long ByteLength, int ContractVersion, string State, string SourceHash, string FileName, string Dependency, string Token, string AssetHash)>();
        await using (var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT aa.media_id, aa.role, a.media_type, aa.relative_path,
                       aa.byte_length, aa.contract_version, aa.state, a.sha256,
                       coalesce(a.current_managed_file_name,a.original_file_name), a.dependency_status, a.media_storage_token, aa.sha256
                FROM media_assets aa JOIN media a ON a.media_id = aa.media_id
                WHERE a.state = 'ACTIVE' AND a.trashed_at_ms IS NULL
                  AND aa.state IN ('READY','NEEDS_REPAIR')
                  AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.owner_type = 'Media'
                      AND j.owner_id = aa.media_id AND j.kind = CASE
                          WHEN a.media_type = 'VIDEO' THEN 'GenerateVideoMediaAssets'
                          WHEN a.media_type = 'MODEL' THEN 'GenerateModelMediaAssets'
                          ELSE 'GenerateThumbnail' END
                      AND j.state IN ('PENDING','RUNNABLE','RUNNING','PAUSED',
                                      'FAILED_RETRYABLE','FAILED_TERMINAL','WAITING_DEPENDENCY','WAITING_USER_INPUT'));
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                candidates.Add((DbGuid.Parse(reader.GetString(0)), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt32(5),
                    reader.GetString(6),reader.IsDBNull(7) ? "" : reader.GetString(7),reader.IsDBNull(8) ? "" : reader.GetString(8),reader.GetString(9),reader.GetString(10),reader.IsDBNull(11) ? "" : reader.GetString(11)));
        }

        var findings = new List<RecoveryFinding>();
        var queued = new HashSet<Guid>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (queued.Contains(candidate.MediaId)) continue;
            string path;
            try { path = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, candidate.Path); }
            catch (Exception exception) when (exception is ArgumentException or IOException)
            {
                findings.Add(new RecoveryFinding(Guid.NewGuid(), "Media", candidate.MediaId,
                    RecoveryOutcome.NeedsAttention, "MEDIA_ASSET_PATH_INVALID",
                    "The durable MediaAsset path is invalid and was left unchanged."));
                continue;
            }
            var validFile = candidate.State == "READY" && candidate.ContractVersion == 1 && File.Exists(path)
                && new FileInfo(path).Length == candidate.ByteLength;
            if (candidate.Role == "MODEL_RENDER")
            {
                string expectedModelPath = "";
                try { expectedModelPath = new ManagedPathPlanner().PlanMediaAsset(new MediaStorageToken(candidate.Token),MediaAssetRole.ModelRender); }
                catch (ArgumentException) { }
                if (string.IsNullOrEmpty(candidate.SourceHash) || candidate.MediaType != "MODEL" || !ModelRenderEligibility.IsEligible(candidate.FileName,candidate.Dependency)
                    || candidate.Path != expectedModelPath)
                {
                    findings.Add(new RecoveryFinding(Guid.NewGuid(),"Media",candidate.MediaId,RecoveryOutcome.NeedsAttention,
                        "MODEL_RENDER_AUTHORITY_INVALID","ModelRender source fingerprint, eligibility or canonical path is invalid; no repair was queued."));
                    continue;
                }
                if (validFile)
                {
                    try
                    {
                        var valid = await new MediaAssetFileValidator().ValidateAsync(path,MediaAssetRole.ModelRender,cancellationToken,candidate.SourceHash).ConfigureAwait(false);
                        validFile = valid.Sha256 == candidate.AssetHash && valid.ByteLength == candidate.ByteLength;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
                    { validFile = false; }
                }
            }
            if (validFile) continue;
            var kind = candidate.MediaType == "VIDEO" ? "GenerateVideoMediaAssets"
                : candidate.MediaType == "MODEL" ? "GenerateModelMediaAssets" : "GenerateThumbnail";
            var lane = kind == "GenerateThumbnail" && candidate.MediaType == "IMAGE"
                ? JobLane.Cpu : JobLane.Media;
            var jobId = Guid.NewGuid();
            if (!await CreateRecoveryAssetJobAsync(jobId,candidate.MediaId,kind,lane,cancellationToken).ConfigureAwait(false)) continue;
            queued.Add(candidate.MediaId);
            findings.Add(new RecoveryFinding(jobId, "Media", candidate.MediaId,
                RecoveryOutcome.Requeued, "MEDIA_ASSET_REPAIR_QUEUED",
                "A missing, incomplete, or obsolete MediaAsset was queued for background repair."));
        }
        await QueueSupportedModelAssetsAsync(findings, cancellationToken).ConfigureAwait(false);
        return findings;
    }

    private async Task QueueSupportedModelAssetsAsync(List<RecoveryFinding> findings, CancellationToken ct)
    {
        var mediaIds = new List<Guid>();
        await using (var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT m.media_id, coalesce(m.current_managed_file_name,m.original_file_name), m.dependency_status
                FROM media m
                WHERE m.media_type = 'MODEL' AND m.state = 'ACTIVE' AND m.trashed_at_ms IS NULL
                  AND (NOT EXISTS (SELECT 1 FROM media_assets a WHERE a.media_id = m.media_id AND a.role = 'THUMBNAIL')
                    OR NOT EXISTS (SELECT 1 FROM media_assets a WHERE a.media_id = m.media_id AND a.role = 'MODEL_RENDER'))
                  AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.owner_type = 'Media' AND j.owner_id = m.media_id
                      AND j.kind = 'GenerateModelMediaAssets' AND j.state IN ('PENDING','RUNNABLE','RUNNING','PAUSED','WAITING_DEPENDENCY','WAITING_USER_INPUT','FAILED_RETRYABLE','FAILED_TERMINAL'));
                """;
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                if (!reader.IsDBNull(1) && ModelRenderEligibility.IsEligible(reader.GetString(1),reader.GetString(2)))
                    mediaIds.Add(DbGuid.Parse(reader.GetString(0)));
        }
        foreach (var mediaId in mediaIds)
        {
            // A stable receipt makes this capability upgrade one attempt across restarts; failed jobs retain normal retry authority.
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"glb-media-assets-v1:{mediaId:D}"));
            var jobId = new Guid(hash.AsSpan(0, 16));
            await using (var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false))
            await using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT 1 FROM jobs WHERE job_id = $jobId;";
                read.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
                if (await read.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null) continue;
            }
            if (!await CreateRecoveryAssetJobAsync(jobId,mediaId,"GenerateModelMediaAssets",JobLane.Media,ct).ConfigureAwait(false)) continue;
            findings.Add(new RecoveryFinding(jobId, "Media", mediaId, RecoveryOutcome.Requeued,
                "MODEL_MEDIA_ASSETS_QUEUED", "An eligible canonical model was queued for its missing durable MediaAssets."));
        }
    }

    private async Task<bool> CreateRecoveryAssetJobAsync(Guid jobId,Guid mediaId,string kind,JobLane lane,CancellationToken ct)
    {
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var insert = transaction.CreateCommand("""
            INSERT INTO jobs(job_id,kind,lane,state,priority,owner_type,owner_id,attempt,max_attempts,checkpoint_json,created_at_ms)
            SELECT $id,$kind,$lane,'PENDING',$priority,'Media',$media,0,$attempts,'{}',$now
            WHERE NOT EXISTS (SELECT 1 FROM jobs WHERE job_id = $id)
              AND NOT EXISTS (SELECT 1 FROM jobs WHERE owner_type = 'Media' AND owner_id = $media
                AND kind = $kind AND state IN ('PENDING','RUNNABLE','RUNNING','PAUSED','FAILED_RETRYABLE','FAILED_TERMINAL','WAITING_DEPENDENCY','WAITING_USER_INPUT'));
            """);
        insert.Parameters.AddWithValue("$id",DbGuid.Format(jobId)); insert.Parameters.AddWithValue("$media",DbGuid.Format(mediaId));
        insert.Parameters.AddWithValue("$kind",kind); insert.Parameters.AddWithValue("$lane",DbEnum.Format(lane));
        insert.Parameters.AddWithValue("$priority",JobPriorityPolicy.PriorityBackground);
        insert.Parameters.AddWithValue("$attempts",JobRetryPolicy.DefaultMaxAttempts);
        insert.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var inserted = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        if (inserted != 0) JobSignals.Raise();
        return inserted != 0;
    }
    public StorageRecovery(
        ImportCommitCoordinator commitCoordinator,
        CatalogDb catalog,
        PathReconciler pathReconciler,
        ProfileManifestWriter? manifestWriter = null)
    {
        _commitCoordinator = commitCoordinator
            ?? throw new ArgumentNullException(nameof(commitCoordinator));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _pathReconciler = pathReconciler ?? throw new ArgumentNullException(nameof(pathReconciler));
        _manifestWriter = manifestWriter ?? new ProfileManifestWriter(catalog.Paths);
        _timestampReconciler = new ImportSourceTimestampReconciler(catalog);
        _mediaPreparation = new ImportMediaPreparationCoordinator(catalog, catalog.Paths);
    }

    public async Task<IReadOnlyList<RecoveryFinding>> RecoverPathReconciliationAsync(
        CancellationToken cancellationToken = default)
    {
        var findings = new List<RecoveryFinding>();

        foreach (var obligation in await ReadObligationsAsync(
                     ProfileRenameSql, "Profile", cancellationToken).ConfigureAwait(false))
        {
            findings.Add(await ReconcileAsync(
                    obligation,
                    "PROFILE_RENAME",
                    token => _pathReconciler.ReconcileProfileRenameAsync(obligation.EntityId, token),
                    _ => Task.FromResult<Guid?>(obligation.EntityId),
                    cancellationToken)
                .ConfigureAwait(false));
        }

        foreach (var obligation in await ReadObligationsAsync(
                     OwnerRelocationSql, "Media", cancellationToken).ConfigureAwait(false))
        {
            findings.Add(await ReconcileAsync(
                    obligation,
                    "OWNER_RELOCATION",
                    token => _pathReconciler.ReconcileOwnerRelocationAsync(obligation.EntityId, token),
                    token => ReadOwnerProfileIdAsync(obligation.EntityId, token),
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return findings;
    }

    public async Task<RecoveryFinding> RecoverImportCommitAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Recovery must preserve the normal lifecycle authority after canonical commit. In particular,
            // source cleanup is not allowed to advance the commit checkpoint beyond the Finalizer's
            // replay window before media preparation scheduling is durably re-established.
            var canonicalCommit = await _commitCoordinator.CommitCanonicalMediaAsync(
                    unitId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            ImportCommitResult result;
            if (canonicalCommit.LibraryCommitted)
            {
                // media preparation is the canonical post-Stage-1 authority in both normal execution and
                // restart recovery. Schedule first: deterministic jobs/capabilities make this
                // replay idempotent. A crash or exception here leaves the commit checkpoint
                // recoverable instead of letting source cleanup hide a PREPARING unit.
                await _mediaPreparation.ScheduleForCurrentPhaseAsync(
                        unitId,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await _mediaPreparation.TryTransitionToReadyAsync(unitId, cancellationToken)
                    .ConfigureAwait(false);

                // Preserve original source dates on recovery. This reconciliation is deliberately
                // after media preparation replay so an auxiliary timestamp repair cannot prevent the canonical
                // preparation graph from being recreated after restart.
                await _timestampReconciler.ReconcileAsync(unitId, cancellationToken)
                    .ConfigureAwait(false);

                // CommitCanonicalMediaAsync is idempotent once domain authority is committed. media preparation
                // is now durably scheduled, so CommitAsync may safely resume post-authority cleanup.
                result = await _commitCoordinator.CommitAsync(
                        unitId,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                result = canonicalCommit;
            }

            return result.Outcome switch
            {
                ImportCommitOutcome.Committed => Finding(
                    unitId,
                    RecoveryOutcome.Completed,
                    "IMPORT_COMMIT_RECOVERED",
                    "The persisted Import commit restored media preparation replay authority before resuming its terminal cleanup checkpoint."),
                ImportCommitOutcome.CommittedWithCleanupAttention => Finding(
                    unitId,
                    RecoveryOutcome.NeedsAttention,
                    "SOURCE_CLEANUP_RETRYABLE",
                    "Vault and media preparation authorities are valid and source cleanup remains retryable."),
                ImportCommitOutcome.Blocked => BlockedFinding(unitId, result),
                ImportCommitOutcome.Conflict => Finding(
                    unitId,
                    RecoveryOutcome.NeedsAttention,
                    "IMPORT_COMMIT_CONFLICT",
                    "The persisted Import commit encountered conflicting durable authority."),
                ImportCommitOutcome.Cancelled => Finding(
                    unitId,
                    RecoveryOutcome.Completed,
                    "IMPORT_COMMIT_CANCELLED",
                    "The cancelled Import commit is durably settled and requires no recovery."),
                _ => Finding(
                    unitId,
                    RecoveryOutcome.Fatal,
                    "IMPORT_COMMIT_UNKNOWN_OUTCOME",
                    "The persisted Import commit returned an unsupported recovery outcome."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException)
        {
            return Finding(
                unitId,
                RecoveryOutcome.Fatal,
                "IMPORT_RECOVERY_DATABASE_FAILURE",
                "The catalog failed while recovering a persisted Import commit.");
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or FormatException
            or System.Text.Json.JsonException
            or CatalogInvariantException)
        {
            return Finding(
                unitId,
                RecoveryOutcome.NeedsAttention,
                "IMPORT_RECOVERY_AMBIGUOUS",
                "Persisted Import recovery facts are incomplete or inconsistent and were left unchanged.");
        }
    }

    private async Task<RecoveryFinding> ReconcileAsync(
        PathObligation obligation,
        string operationKind,
        Func<CancellationToken, Task<StorageOperationResult>> reconcile,
        Func<CancellationToken, Task<Guid?>> resolveManifestOwner,
        CancellationToken cancellationToken)
    {
        StorageOperationResult result;
        try
        {
            result = await reconcile(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException)
        {
            return obligation.ToFinding(
                RecoveryOutcome.Fatal,
                "PATH_RECOVERY_DATABASE_FAILURE",
                "The catalog failed while resuming a persisted placement obligation.");
        }
        catch (Exception exception) when (exception is CatalogInvariantException
            or CatalogConcurrencyConflictException
            or InvalidOperationException)
        {
            return obligation.ToFinding(
                RecoveryOutcome.NeedsAttention,
                operationKind + "_AMBIGUOUS",
                "Persisted placement facts are incomplete or inconsistent and were left unchanged.");
        }

        if (result.Status == StorageOperationStatus.Success)
        {
            return await RefreshManifestAsync(
                    obligation, operationKind, resolveManifestOwner, cancellationToken)
                .ConfigureAwait(false);
        }

        return result.Status switch
        {
            StorageOperationStatus.AlreadyCompleted => obligation.ToFinding(
                RecoveryOutcome.Completed,
                operationKind + "_RECONCILED",
                "The persisted placement obligation was already at its canonical target."),
            StorageOperationStatus.Cancelled => obligation.ToFinding(
                RecoveryOutcome.NeedsAttention,
                operationKind + "_INCOMPLETE",
                "Reconciliation stopped at a safe checkpoint and remains resumable."),
            _ => obligation.ToFinding(
                RecoveryOutcome.NeedsAttention,
                operationKind + "_NEEDS_ATTENTION",
                result.SafeErrorDetail
                    ?? "Reconciliation could not continue safely from current durable facts."),
        };
    }

    private async Task<RecoveryFinding> RefreshManifestAsync(
        PathObligation obligation,
        string operationKind,
        Func<CancellationToken, Task<Guid?>> resolveManifestOwner,
        CancellationToken cancellationToken)
    {
        var converged = obligation.ToFinding(
            RecoveryOutcome.Completed,
            operationKind + "_RECONCILED",
            "The persisted placement obligation reached its canonical target.");

        var profileId = await resolveManifestOwner(cancellationToken).ConfigureAwait(false);
        if (profileId is not { } owner)
        {
            return converged;
        }

        var refresh = await _manifestWriter
            .RegenerateManifestAsync(_catalog, owner, cancellationToken)
            .ConfigureAwait(false);
        return refresh.IsSuccess
            ? converged
            : obligation.ToFinding(
                RecoveryOutcome.NeedsAttention,
                operationKind + "_MANIFEST_STALE",
                "Placement converged, but the affected manifest.json could not be refreshed.");
    }

    private async Task<Guid?> ReadOwnerProfileIdAsync(Guid assetId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT profile_id
            FROM profile_media
            WHERE media_id = $assetId AND relation_type = 'OWNER';
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        var owner = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return owner is string value ? DbGuid.Parse(value) : null;
    }

    private static RecoveryFinding Finding(
        Guid unitId,
        RecoveryOutcome outcome,
        string code,
        string detail) =>
        new(unitId, "ImportUnit", unitId, outcome, code, detail);

    private static RecoveryFinding BlockedFinding(Guid unitId, ImportCommitResult result)
    {
        var blocker = result.Blockers.FirstOrDefault();
        return Finding(
            unitId,
            RecoveryOutcome.NeedsAttention,
            blocker?.Code ?? "IMPORT_COMMIT_BLOCKED",
            blocker?.Message
                ?? "The persisted Import commit could not advance safely from current durable facts.");
    }

    private async Task<IReadOnlyList<PathObligation>> ReadObligationsAsync(
        string sql,
        string entityType,
        CancellationToken cancellationToken)
    {
        var obligations = new List<PathObligation>();
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            obligations.Add(new PathObligation(
                entityType,
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1))));
        }

        return obligations;
    }

    private sealed record PathObligation(
        string EntityType,
        Guid EntityId,
        Guid OperationId)
    {
        public RecoveryFinding ToFinding(RecoveryOutcome outcome, string code, string safeDetail) =>
            new(OperationId, EntityType, EntityId, outcome, code, safeDetail);
    }
}
