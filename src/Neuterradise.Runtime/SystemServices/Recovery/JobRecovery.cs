using Microsoft.Data.Sqlite;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.SystemServices.Recovery;

public sealed class JobRecovery
{
    private const string LeasedJobsSql =
        """
        SELECT job_id, kind, lane, owner_type, owner_id, attempt, max_attempts, row_version
        FROM jobs
        WHERE state = 'RUNNING'
        ORDER BY created_at_ms, job_id;
        """;

    private const string ProvenProfileOutcomeSql =
        "SELECT path_state FROM profiles WHERE profile_id = $ownerId;";

    private const string ProvenMediaOutcomeSql =
        "SELECT path_state FROM media WHERE media_id = $ownerId;";

    private readonly CatalogDb _catalog;
    private readonly JobWrites _writes;
    private readonly TimeProvider _timeProvider;

    public JobRecovery(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _writes = new JobWrites(catalog, _timeProvider);
    }

    public async Task<IReadOnlyList<RecoveryFinding>> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var findings = new List<RecoveryFinding>();

        foreach (var job in await ReadLeasedJobsAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.Add(await ReconcileAsync(job, cancellationToken).ConfigureAwait(false));
        }

        findings.AddRange(await ReclassifyMisrecordedFailuresAsync(cancellationToken).ConfigureAwait(false));
        return findings;
    }

    /// <summary>
    /// Repairs job rows that earlier builds recorded as failures although nothing failed:
    /// <list type="bullet">
    /// <item>work still queued or "failed" for media that has since been retired (skipped, cancelled or
    /// de-duplicated) no longer applies and is cancelled;</item>
    /// <item>face analysis that was refused only because the media was still being imported is
    /// requeued so the library still gets its face hints;</item>
    /// <item>face analysis refused because the optional face models or worker were missing is an
    /// unavailable capability, not a failure.</item>
    /// </list>
    /// Genuine failures are left untouched. Idempotent, so it is safe on every start.
    /// </summary>
    public async Task<IReadOnlyList<RecoveryFinding>> ReclassifyMisrecordedFailuresAsync(
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        async Task<int> ExecuteAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var retiredOwners = await ExecuteAsync(
            """
            UPDATE jobs
            SET state = 'CANCELLED',
                not_before_ms = NULL,
                error_code = 'OWNER_RETIRED',
                error_detail_safe = 'The media left the import before this work ran.',
                row_version = row_version + 1
            WHERE owner_type = 'Media'
              AND state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE','FAILED_TERMINAL')
              AND owner_id IN (SELECT media_id FROM media WHERE state = 'RETIRED');
            """).ConfigureAwait(false);

        var requeuedFaces = await ExecuteAsync(
            """
            UPDATE jobs
            SET state = 'PENDING',
                attempt = 0,
                not_before_ms = NULL,
                error_code = NULL,
                error_detail_safe = NULL,
                completed_at_ms = NULL,
                row_version = row_version + 1
            WHERE kind = 'FaceAnalysis'
              AND state = 'FAILED_TERMINAL'
              AND error_code = 'FACE_OWNER_UNUSABLE'
              AND owner_id IN (SELECT media_id FROM media WHERE state IN ('CANDIDATE','ACTIVE'));
            """).ConfigureAwait(false);

        var unavailable = await ExecuteAsync(
            """
            UPDATE jobs
            SET state = 'CANCELLED',
                not_before_ms = NULL,
                error_code = 'CAPABILITY_UNAVAILABLE',
                row_version = row_version + 1
            WHERE kind = 'FaceAnalysis'
              AND state IN ('FAILED_RETRYABLE','FAILED_TERMINAL')
              AND error_code IN ('FACE_MODELS_UNAVAILABLE','FACE_WORKER_NOT_DEPLOYED');
            """).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var findings = new List<RecoveryFinding>();
        if (retiredOwners + requeuedFaces + unavailable > 0)
        {
            findings.Add(new RecoveryFinding(
                Guid.Empty,
                "Job",
                Guid.Empty,
                RecoveryOutcome.Completed,
                "JOB_FAILURES_RECLASSIFIED",
                $"Reclassified misrecorded job failures: {retiredOwners} no longer applicable, {requeuedFaces} face analyses requeued, {unavailable} optional capability unavailable."));
        }

        return findings;
    }

    private async Task<RecoveryFinding> ReconcileAsync(
        LeasedJob job,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        if (await IsOwnedOutcomeProvenAsync(job, cancellationToken).ConfigureAwait(false))
        {
            await _writes.TryCompleteAsync(
                    job.JobId,
                    job.RowVersion,
                    JobState.Succeeded,
                    errorCode: null,
                    errorDetailSafe: null,
                    progressCompleted: null,
                    progressTotal: null,
                    stage: null,
                    checkpointJson: null,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            return job.ToFinding(
                RecoveryOutcome.Completed,
                "JOB_OUTCOME_ALREADY_PROVEN",
                "The work this job owned is already proven complete in durable authority.");
        }

        var lostCode = job.Lane == JobLane.Face ? "WORKER_EXECUTION_LOST" : "JOB_EXECUTION_LOST";
        var lostDetail = job.Lane == JobLane.Face
            ? "A leased FACE job did not survive the restart; no Worker process is assumed alive."
            : "A leased job did not survive the restart and resumes from its persisted checkpoint.";

        if (!await _writes.TryCompleteAsync(
                job.JobId,
                job.RowVersion,
                JobState.FailedRetryable,
                lostCode,
                lostDetail,
                progressCompleted: null,
                progressTotal: null,
                stage: null,
                checkpointJson: null,
                now,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return job.ToFinding(
                RecoveryOutcome.NeedsAttention,
                "JOB_RECONCILIATION_CONFLICT",
                "A leased job changed while it was being reconciled and was left unchanged.");
        }

        if (job.Attempt < job.MaxAttempts)
        {
            return job.ToFinding(RecoveryOutcome.Requeued, lostCode, lostDetail);
        }

        await _writes.TryExhaustRetriesAsync(
                job.JobId,
                job.RowVersion + 1,
                "JOB_RETRIES_EXHAUSTED",
                "A leased job spent its final attempt before the process stopped.",
                now,
                cancellationToken)
            .ConfigureAwait(false);
        return job.ToFinding(
            RecoveryOutcome.NeedsAttention,
            "JOB_RETRIES_EXHAUSTED",
            "A leased job spent its final attempt before the process stopped and needs attention.");
    }

    private async Task<bool> IsOwnedOutcomeProvenAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        var sql = job.Kind switch
        {
            "ProfileRenameReconciliation" => ProvenProfileOutcomeSql,
            "OwnerRelocation" => ProvenMediaOutcomeSql,
            _ => null,
        };

        if (sql is null)
        {
            return false;
        }

        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$ownerId", DbGuid.Format(job.OwnerId));
        var pathState = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return pathState is string state
            && DbEnum.ParseManagedPathState(state) == ManagedPathState.None;
    }

    private async Task<IReadOnlyList<LeasedJob>> ReadLeasedJobsAsync(CancellationToken cancellationToken)
    {
        var jobs = new List<LeasedJob>();
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = LeasedJobsSql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(new LeasedJob(
                DbGuid.Parse(reader.GetString(0)),
                reader.GetString(1),
                DbEnum.ParseJobLane(reader.GetString(2)),
                reader.GetString(3),
                DbGuid.Parse(reader.GetString(4)),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt64(7)));
        }

        return jobs;
    }

    private sealed record LeasedJob(
        Guid JobId,
        string Kind,
        JobLane Lane,
        string OwnerType,
        Guid OwnerId,
        int Attempt,
        int MaxAttempts,
        long RowVersion)
    {
        public RecoveryFinding ToFinding(RecoveryOutcome outcome, string code, string safeDetail) =>
            new(JobId, "Job", JobId, outcome, code, safeDetail);
    }
}
