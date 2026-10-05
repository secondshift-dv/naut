using System.Text.Json;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.SystemServices.Database.Writes;

public sealed class JobWrites
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public JobWrites(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task CreateJobAsync(
        JobDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        EnsureNonEmpty(definition.JobId, nameof(definition.JobId));
        EnsureNonEmpty(definition.OwnerId, nameof(definition.OwnerId));
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.OwnerType);
        if (definition.MaxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                "A durable job must allow at least one attempt.");
        }

        ValidateJson(definition.CheckpointJson, nameof(definition.CheckpointJson));
        definition = definition with
        {
            Kind = definition.Kind.Trim(),
            OwnerType = definition.OwnerType.Trim(),
        };

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var existing = await ReadDefinitionAsync(transaction, definition.JobId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!HasSameCreationIdentity(existing, definition))
            {
                throw new CatalogInvariantException(
                    $"Job {definition.JobId:D} already exists with different durable authority.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        definition = await ApplyFocusedImportPriorityAsync(transaction, definition, cancellationToken)
            .ConfigureAwait(false);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var completedAt = IsTerminal(definition.State) ? now : (long?)null;
        await using var insert = transaction.CreateCommand(
            """
            INSERT INTO jobs(
                job_id, kind, lane, state, priority, owner_type, owner_id,
                attempt, max_attempts, not_before_ms, checkpoint_json,
                created_at_ms, completed_at_ms)
            VALUES (
                $jobId, $kind, $lane, $state, $priority, $ownerType, $ownerId,
                0, $maxAttempts, $notBeforeMs, $checkpointJson,
                $createdAtMs, $completedAtMs);
            """);
        insert.Parameters.AddWithValue("$jobId", DbGuid.Format(definition.JobId));
        insert.Parameters.AddWithValue("$kind", definition.Kind);
        insert.Parameters.AddWithValue("$lane", DbEnum.Format(definition.Lane));
        insert.Parameters.AddWithValue("$state", DbEnum.Format(definition.State));
        insert.Parameters.AddWithValue("$priority", definition.Priority);
        insert.Parameters.AddWithValue("$ownerType", definition.OwnerType);
        insert.Parameters.AddWithValue("$ownerId", DbGuid.Format(definition.OwnerId));
        insert.Parameters.AddWithValue("$maxAttempts", definition.MaxAttempts);
        insert.Parameters.AddWithValue(
            "$notBeforeMs",
            definition.NotBeforeMilliseconds is null
                ? DBNull.Value
                : definition.NotBeforeMilliseconds.Value);
        insert.Parameters.AddWithValue("$checkpointJson", definition.CheckpointJson);
        insert.Parameters.AddWithValue("$createdAtMs", now);
        insert.Parameters.AddWithValue(
            "$completedAtMs",
            completedAt is null ? DBNull.Value : completedAt.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Neuterradise.App.SystemServices.Jobs.JobSignals.Raise();
    }

    /// <summary>
    /// Re-queues a successfully completed disposable-derivative job when its current cache artifact
    /// is missing. Only the exact expected kind/owner in SUCCEEDED may move back to PENDING.
    /// </summary>
    public async Task<bool> TryRequeueSucceededDerivedJobAsync(
        Guid jobId,
        string expectedKind,
        Guid expectedOwnerId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(jobId, nameof(jobId));
        EnsureNonEmpty(expectedOwnerId, nameof(expectedOwnerId));
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedKind);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var update = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'PENDING',
                attempt = 0,
                not_before_ms = NULL,
                completed_at_ms = NULL,
                error_code = NULL,
                error_detail_safe = NULL,
                row_version = row_version + 1
            WHERE job_id = $jobId
              AND kind = $kind
              AND owner_type = 'Media'
              AND owner_id = $ownerId
              AND state = 'SUCCEEDED';
            """);
        update.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
        update.Parameters.AddWithValue("$kind", expectedKind.Trim());
        update.Parameters.AddWithValue("$ownerId", DbGuid.Format(expectedOwnerId));
        var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (changed)
        {
            Neuterradise.App.SystemServices.Jobs.JobSignals.Raise();
        }

        return changed;
    }

    public async Task AddDependencyAsync(
        Guid jobId,
        Guid dependsOnJobId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(jobId, nameof(jobId));
        EnsureNonEmpty(dependsOnJobId, nameof(dependsOnJobId));
        if (jobId == dependsOnJobId)
        {
            throw new ArgumentException("A durable job cannot depend on itself.", nameof(dependsOnJobId));
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await EnsureJobsExistAsync(transaction, jobId, dependsOnJobId, cancellationToken)
            .ConfigureAwait(false);
        if (await WouldCreateCycleAsync(transaction, jobId, dependsOnJobId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new CatalogInvariantException(
                $"Dependency {jobId:D} -> {dependsOnJobId:D} would create a durable job cycle.");
        }

        await using var insert = transaction.CreateCommand(
            """
            INSERT OR IGNORE INTO job_dependencies(job_id, depends_on_job_id)
            VALUES ($jobId, $dependsOnJobId);
            """);
        insert.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
        insert.Parameters.AddWithValue("$dependsOnJobId", DbGuid.Format(dependsOnJobId));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Neuterradise.App.SystemServices.Jobs.JobSignals.Raise();
    }

    public async Task<bool> TryClaimAsync(
        Guid jobId,
        long expectedRowVersion,
        DateTimeOffset claimedAtUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(jobId, nameof(jobId));
        if (expectedRowVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRowVersion));
        }

        var claimedAt = DbTime.Format(claimedAtUtc);
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var update = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'RUNNING',
                attempt = attempt + 1,
                started_at_ms = $claimedAtMs,
                completed_at_ms = NULL,
                row_version = row_version + 1
            WHERE job_id = $jobId
              AND row_version = $expectedRowVersion
              AND state = 'RUNNABLE'
              AND attempt < max_attempts
              AND (not_before_ms IS NULL OR not_before_ms <= $claimedAtMs)
              AND NOT EXISTS (
                  SELECT 1
                  FROM job_dependencies dependency
                  JOIN jobs predecessor
                    ON predecessor.job_id = dependency.depends_on_job_id
                  WHERE dependency.job_id = jobs.job_id
                    AND predecessor.state <> 'SUCCEEDED'
              );
            """);
        update.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
        update.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);
        update.Parameters.AddWithValue("$claimedAtMs", claimedAt);
        var claimed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (claimed) JobSignals.RaiseStatusChanged();
        return claimed;
    }

    public async Task<bool> MarkRunnableAsync(
        Guid jobId,
        long expectedRowVersion,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(jobId, nameof(jobId));
        if (expectedRowVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRowVersion));
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var update = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'RUNNABLE',
                completed_at_ms = NULL,
                error_code = NULL,
                error_detail_safe = NULL,
                row_version = row_version + 1
            WHERE job_id = $jobId
              AND row_version = $expectedRowVersion
              AND state = 'PENDING';
            """);
        update.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
        update.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);
        var promoted = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (promoted) JobSignals.RaiseStatusChanged();
        return promoted;
    }

    public async Task<bool> UpdateRunningProgressAsync(
        Guid jobId,
        long expectedRowVersion,
        long? completed,
        long? total,
        string? stage,
        string? checkpointJson,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(jobId, nameof(jobId));
        if (expectedRowVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRowVersion));
        }

        ValidateProgress(completed, total);
        if (checkpointJson is not null)
        {
            ValidateJson(checkpointJson, nameof(checkpointJson));
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var update = transaction.CreateCommand(
            """
            UPDATE jobs
            SET progress_completed = COALESCE($completed, progress_completed),
                progress_total = COALESCE($total, progress_total),
                stage = COALESCE($stage, stage),
                checkpoint_json = COALESCE($checkpointJson, checkpoint_json)
            WHERE job_id = $jobId
              AND row_version = $expectedRowVersion
              AND state = 'RUNNING';
            """);
        update.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
        update.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);
        update.Parameters.AddWithValue("$completed", completed is null ? DBNull.Value : completed.Value);
        update.Parameters.AddWithValue("$total", total is null ? DBNull.Value : total.Value);
        update.Parameters.AddWithValue("$stage", stage is null ? DBNull.Value : stage);
        update.Parameters.AddWithValue("$checkpointJson", checkpointJson is null ? DBNull.Value : checkpointJson);
        var updated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (updated) JobSignals.RaiseStatusChanged();
        return updated;
    }

    public async Task<bool> TryInterruptForShutdownAsync(
        Guid jobId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(jobId, nameof(jobId));
        if (expectedRowVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRowVersion));
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var update = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'PENDING',
                attempt = CASE WHEN attempt > 0 THEN attempt - 1 ELSE 0 END,
                not_before_ms = NULL,
                started_at_ms = NULL,
                completed_at_ms = NULL,
                error_code = NULL,
                error_detail_safe = NULL,
                row_version = row_version + 1
            WHERE job_id = $jobId
              AND row_version = $expectedRowVersion
              AND state = 'RUNNING';
            """);
        update.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
        update.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);
        var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (changed)
        {
            JobSignals.Raise();
        }

        return changed;
    }

    public async Task<bool> TryCompleteAsync(
        Guid jobId,
        long expectedRowVersion,
        JobState terminalState,
        string? errorCode,
        string? errorDetailSafe,
        long? progressCompleted,
        long? progressTotal,
        string? stage,
        string? checkpointJson,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default,
        long? notBeforeMs = null)
    {
        EnsureNonEmpty(jobId, nameof(jobId));
        if (expectedRowVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRowVersion));
        }

        if (terminalState is not (JobState.Succeeded
            or JobState.FailedRetryable
            or JobState.FailedTerminal
            or JobState.Cancelled
            or JobState.Paused
            or JobState.Pending))
        {
            throw new ArgumentOutOfRangeException(
                nameof(terminalState),
                "A claimed job may complete only to a terminal state.");
        }

        ValidateProgress(progressCompleted, progressTotal);
        if (checkpointJson is not null)
        {
            ValidateJson(checkpointJson, nameof(checkpointJson));
        }

        var completedAt = DbTime.Format(completedAtUtc);
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var update = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = $state,
                attempt = CASE WHEN $state = 'PENDING' AND attempt > 0 THEN attempt - 1 ELSE attempt END,
                priority = CASE WHEN $state = 'PENDING' THEN COALESCE(
                    (SELECT MAX(interest.desired_priority)
                     FROM import_media_interests interest
                     JOIN import_units live ON live.import_unit_id = interest.import_unit_id
                     WHERE interest.media_id = jobs.owner_id
                       AND live.is_paused = 0
                       AND live.state NOT IN ('COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                                              'CANCELLED','FAILED_TERMINAL')),
                    50) ELSE priority END,
                progress_completed = COALESCE($progressCompleted, progress_completed),
                progress_total = COALESCE($progressTotal, progress_total),
                stage = COALESCE($stage, stage),
                checkpoint_json = COALESCE($checkpointJson, checkpoint_json),
                error_code = $errorCode,
                error_detail_safe = $errorDetailSafe,
                not_before_ms = $notBeforeMs,
                completed_at_ms = CASE WHEN $state = 'PENDING' THEN NULL ELSE $completedAtMs END,
                row_version = row_version + 1
            WHERE job_id = $jobId
              AND row_version = $expectedRowVersion
              AND state = 'RUNNING';
            """);
        update.Parameters.AddWithValue("$notBeforeMs", notBeforeMs is null ? DBNull.Value : notBeforeMs.Value);
        update.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
        update.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);
        update.Parameters.AddWithValue("$state", DbEnum.Format(terminalState));
        update.Parameters.AddWithValue(
            "$progressCompleted",
            progressCompleted is null ? DBNull.Value : progressCompleted.Value);
        update.Parameters.AddWithValue(
            "$progressTotal",
            progressTotal is null ? DBNull.Value : progressTotal.Value);
        update.Parameters.AddWithValue("$stage", stage is null ? DBNull.Value : stage);
        update.Parameters.AddWithValue("$checkpointJson", checkpointJson is null ? DBNull.Value : checkpointJson);
        update.Parameters.AddWithValue("$errorCode", errorCode is null ? DBNull.Value : errorCode);
        update.Parameters.AddWithValue("$errorDetailSafe", errorDetailSafe is null ? DBNull.Value : errorDetailSafe);
        update.Parameters.AddWithValue("$completedAtMs", completedAt);
        var completed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (completed) JobSignals.RaiseStatusChanged();
        return completed;
    }

    public Task<int> PauseAsync(
        Guid? jobId,
        string? ownerType,
        Guid? ownerId,
        CancellationToken cancellationToken = default) =>
        TransitionScopeAsync(
            """
            UPDATE jobs
            SET state = 'PAUSED',
                row_version = row_version + 1
            WHERE state IN ('PENDING','RUNNABLE','FAILED_RETRYABLE')
            """,
            jobId,
            ownerType,
            ownerId,
            cancellationToken);

    public Task<int> ResumeAsync(
        Guid? jobId,
        string? ownerType,
        Guid? ownerId,
        CancellationToken cancellationToken = default) =>
        TransitionScopeAsync(
            """
            UPDATE jobs
            SET state = 'PENDING',
                not_before_ms = NULL,
                error_code = NULL,
                error_detail_safe = NULL,
                row_version = row_version + 1
            WHERE state = 'PAUSED'
            """,
            jobId,
            ownerType,
            ownerId,
            cancellationToken);

    public Task<int> CancelIdleAsync(
        Guid? jobId,
        string? ownerType,
        Guid? ownerId,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken = default) =>
        TransitionScopeAsync(
            """
            UPDATE jobs
            SET state = 'CANCELLED',
                not_before_ms = NULL,
                completed_at_ms = $cancelledAtMs,
                row_version = row_version + 1
            WHERE state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE')
            """,
            jobId,
            ownerType,
            ownerId,
            cancellationToken,
            ("$cancelledAtMs", DbTime.Format(cancelledAtUtc)));

    public async Task<int> CancelIdleMediaForImportUnitAsync(
        Guid importUnitId,
        Guid assetId,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(importUnitId, nameof(importUnitId));
        EnsureNonEmpty(assetId, nameof(assetId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using var update = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'CANCELLED',
                not_before_ms = NULL,
                completed_at_ms = $cancelledAtMs,
                row_version = row_version + 1
            WHERE owner_type = 'Media'
              AND owner_id = $assetId
              AND state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE')
              AND NOT EXISTS (
                  SELECT 1
                  FROM import_items other
                  JOIN import_units consumer ON consumer.import_unit_id = other.import_unit_id
                  WHERE other.import_unit_id <> $unitId
                    AND (other.candidate_media_id = $assetId OR other.reused_media_id = $assetId)
                    AND consumer.state NOT IN (
                        'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                        'CANCELLED','FAILED_TERMINAL'
                    )
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM import_media_interests other
                  JOIN import_units consumer ON consumer.import_unit_id = other.import_unit_id
                  WHERE other.import_unit_id <> $unitId
                    AND other.media_id = $assetId
                    AND consumer.state NOT IN (
                        'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                        'CANCELLED','FAILED_TERMINAL'
                    )
              );
            """);
        update.Parameters.AddWithValue("$unitId", DbGuid.Format(importUnitId));
        update.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        update.Parameters.AddWithValue("$cancelledAtMs", DbTime.Format(cancelledAtUtc));
        var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (changed > 0) JobSignals.RaiseStatusChanged();
        return changed;
    }

    public async Task<bool> TryRescheduleRetryAsync(
        Guid jobId,
        long expectedRowVersion,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(jobId, nameof(jobId));
        if (expectedRowVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRowVersion));
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var update = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'RUNNABLE',
                not_before_ms = NULL,
                completed_at_ms = NULL,
                error_code = NULL,
                error_detail_safe = NULL,
                row_version = row_version + 1
            WHERE job_id = $jobId
              AND row_version = $expectedRowVersion
              AND state = 'FAILED_RETRYABLE'
              AND attempt < max_attempts
              AND (not_before_ms IS NULL OR not_before_ms <= $nowMs);
            """);
        update.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
        update.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);
        update.Parameters.AddWithValue("$nowMs", DbTime.Format(utcNow));
        var rescheduled = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (rescheduled) JobSignals.RaiseStatusChanged();
        return rescheduled;
    }

    public async Task<bool> TryExhaustRetriesAsync(
        Guid jobId,
        long expectedRowVersion,
        string errorCode,
        string? errorDetailSafe,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(jobId, nameof(jobId));
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var update = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'FAILED_TERMINAL',
                not_before_ms = NULL,
                error_code = $errorCode,
                error_detail_safe = COALESCE($errorDetailSafe, error_detail_safe),
                completed_at_ms = $nowMs,
                row_version = row_version + 1
            WHERE job_id = $jobId
              AND row_version = $expectedRowVersion
              AND state = 'FAILED_RETRYABLE'
              AND attempt >= max_attempts;
            """);
        update.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
        update.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);
        update.Parameters.AddWithValue("$errorCode", errorCode);
        update.Parameters.AddWithValue(
            "$errorDetailSafe",
            errorDetailSafe is null ? DBNull.Value : errorDetailSafe);
        update.Parameters.AddWithValue("$nowMs", DbTime.Format(utcNow));
        var exhausted = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (exhausted) JobSignals.RaiseStatusChanged();
        return exhausted;
    }

    public async Task<bool> PrioritizeJobAsync(
        Guid jobId,
        int priority,
        bool boostDependencies = true,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(jobId, nameof(jobId));
        var clamped = JobPriorityPolicy.Clamp(priority);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using var update = transaction.CreateCommand(
            """
            UPDATE jobs
            SET priority = $priority,
                row_version = row_version + 1
            WHERE job_id = $jobId
              AND state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE');
            """);
        update.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
        update.Parameters.AddWithValue("$priority", clamped);
        var updated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;

        if (boostDependencies)
        {
            await using var boost = transaction.CreateCommand(
                """
                WITH RECURSIVE deps(dep_id) AS (
                    SELECT depends_on_job_id FROM job_dependencies WHERE job_id = $jobId
                    UNION
                    SELECT d.depends_on_job_id FROM job_dependencies d JOIN deps ON d.job_id = deps.dep_id
                )
                UPDATE jobs
                SET priority = MAX(priority, $priority),
                    row_version = row_version + 1
                WHERE job_id IN (SELECT dep_id FROM deps)
                  AND state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE');
                """);
            boost.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
            boost.Parameters.AddWithValue("$priority", clamped);
            await boost.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Changes the focused import and its Media-owned job graph in the same transaction. The
    /// expected-current guard lets terminal/pause cleanup avoid clearing a newer user selection.
    /// </summary>
    public async Task<Guid?> ChangeFocusedImportUnitAsync(
        string settingKey,
        Guid? focusedImportUnitId,
        Guid? expectedCurrent,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingKey);
        if (focusedImportUnitId == Guid.Empty)
        {
            throw new ArgumentException("A focused import identifier cannot be empty.", nameof(focusedImportUnitId));
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        await using var read = transaction.CreateCommand("SELECT value_json FROM settings WHERE key = $key;");
        read.Parameters.AddWithValue("$key", settingKey.Trim());
        var currentJson = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        var current = ImportPriorityOperations.Parse(currentJson);

        if (expectedCurrent.HasValue && current != expectedCurrent)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return current;
        }

        if (focusedImportUnitId is { } requestedFocus)
        {
            await using var eligible = transaction.CreateCommand(
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM import_units
                    WHERE import_unit_id = $unitId
                      AND is_paused = 0
                      AND state IN ('INTAKE','PREPARING','READY_FOR_VERIFICATION','FAILED_RETRYABLE')
                );
                """);
            eligible.Parameters.AddWithValue("$unitId", DbGuid.Format(requestedFocus));
            var canFocus = Convert.ToInt32(
                await eligible.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) == 1;

            if (!canFocus)
            {
                if (current != requestedFocus)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return current;
                }

                focusedImportUnitId = null;
            }
        }

        if (current == focusedImportUnitId)
        {
            if (focusedImportUnitId is { } targetId)
            {
                // Refresh focused import jobs to P1. Never P0 — import work is not interactive.
                await SetImportUnitPriorityInTransactionAsync(
                    transaction,
                    targetId,
                    JobPriorityPolicy.PriorityCurrentImport,
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return current;
        }

        if (current is { } previous)
        {
            // Demote previous focused import to background. Does not cancel or fail the import.
            await SetImportUnitPriorityInTransactionAsync(
                transaction,
                previous,
                JobPriorityPolicy.PriorityBackground,
                cancellationToken).ConfigureAwait(false);
        }

        if (focusedImportUnitId is { } nextId)
        {
            // Promote newly focused import to P1. Never P0.
            await SetImportUnitPriorityInTransactionAsync(
                transaction,
                nextId,
                JobPriorityPolicy.PriorityCurrentImport,
                cancellationToken).ConfigureAwait(false);
        }

        await SettingsWrites.SetSettingInTransactionAsync(
            transaction,
            settingKey,
            JsonSerializer.Serialize(focusedImportUnitId?.ToString("D")),
            activityEntry: null,
            nowUtc: _timeProvider.GetUtcNow(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return focusedImportUnitId;
    }

    public async Task<int> SetImportUnitPriorityAsync(
        Guid importUnitId,
        int priority,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(importUnitId, nameof(importUnitId));
        var clamped = JobPriorityPolicy.Clamp(priority);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        var count = await SetImportUnitPriorityInTransactionAsync(
            transaction,
            importUnitId,
            clamped,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    public async Task RegisterImportMediaInterestAsync(
        Guid importUnitId,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(importUnitId, nameof(importUnitId));
        EnsureNonEmpty(assetId, nameof(assetId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using (var register = transaction.CreateCommand(
            """
            INSERT INTO import_media_interests(
                import_unit_id, media_id, desired_priority, created_at_ms, updated_at_ms)
            VALUES(
                $unitId,
                $assetId,
                CASE WHEN EXISTS(
                    SELECT 1 FROM settings
                    WHERE key = $focusedKey
                      AND value_json = '"' || $unitId || '"'
                ) THEN $focusedPriority ELSE $backgroundPriority END,
                $now,
                $now)
            ON CONFLICT(import_unit_id, media_id) DO UPDATE SET
                desired_priority = excluded.desired_priority,
                updated_at_ms = excluded.updated_at_ms;
            """))
        {
            register.Parameters.AddWithValue("$unitId", DbGuid.Format(importUnitId));
            register.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
            register.Parameters.AddWithValue("$focusedKey", ImportPriorityOperations.SettingKey);
            register.Parameters.AddWithValue("$focusedPriority", JobPriorityPolicy.PriorityCurrentImport);
            register.Parameters.AddWithValue("$backgroundPriority", JobPriorityPolicy.DefaultPriority);
            register.Parameters.AddWithValue("$now", now);
            await register.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await RecomputeMediaPriorityInTransactionAsync(transaction, assetId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> SetImportUnitPriorityInTransactionAsync(
        CatalogTransaction transaction,
        Guid importUnitId,
        int priority,
        CancellationToken cancellationToken)
    {
        var clamped = JobPriorityPolicy.Clamp(priority);

        await using (var interest = transaction.CreateCommand(
            """
            UPDATE import_media_interests
            SET desired_priority = $priority,
                updated_at_ms = CAST(unixepoch('subsec') * 1000 AS INTEGER)
            WHERE import_unit_id = $unitId;
            """))
        {
            interest.Parameters.AddWithValue("$unitId", DbGuid.Format(importUnitId));
            interest.Parameters.AddWithValue("$priority", clamped);
            await interest.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var update = transaction.CreateCommand(
            """
            UPDATE jobs
            SET priority = COALESCE(
                    (
                        SELECT MAX(interest.desired_priority)
                        FROM import_media_interests interest
                        JOIN import_units live ON live.import_unit_id = interest.import_unit_id
                        WHERE interest.media_id = jobs.owner_id
                          AND live.is_paused = 0
                          AND live.state NOT IN (
                              'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                              'CANCELLED','FAILED_TERMINAL'
                          )
                    ),
                    $priority
                ),
                row_version = row_version + 1
            WHERE owner_type = 'Media'
              AND owner_id IN (
                  SELECT candidate_media_id
                  FROM import_items
                  WHERE import_unit_id = $unitId
                    AND candidate_media_id IS NOT NULL
                  UNION
                  SELECT media_id
                  FROM import_media_interests
                  WHERE import_unit_id = $unitId
              )
              AND state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE');
            """);
        update.Parameters.AddWithValue("$unitId", DbGuid.Format(importUnitId));
        update.Parameters.AddWithValue("$priority", clamped);
        return await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecomputeMediaPriorityInTransactionAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var update = transaction.CreateCommand(
            """
            UPDATE jobs
            SET priority = COALESCE(
                    (
                        SELECT MAX(interest.desired_priority)
                        FROM import_media_interests interest
                        JOIN import_units live ON live.import_unit_id = interest.import_unit_id
                        WHERE interest.media_id = $assetId
                          AND live.is_paused = 0
                          AND live.state NOT IN (
                              'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                              'CANCELLED','FAILED_TERMINAL'
                          )
                    ),
                    $backgroundPriority
                ),
                row_version = row_version + 1
            WHERE owner_type = 'Media'
              AND owner_id = $assetId
              AND state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE');
            """);
        update.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        update.Parameters.AddWithValue("$backgroundPriority", JobPriorityPolicy.DefaultPriority);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JobDefinition> ApplyFocusedImportPriorityAsync(
        CatalogTransaction transaction,
        JobDefinition definition,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(definition.OwnerType, "Media", StringComparison.OrdinalIgnoreCase))
        {
            return definition;
        }

        await using var command = transaction.CreateCommand(
            """
            SELECT 1
            FROM settings setting
            WHERE setting.key = $key
              AND (
                  EXISTS (
                      SELECT 1
                      FROM import_items item
                      WHERE item.candidate_media_id = $assetId
                        AND setting.value_json = '"' || item.import_unit_id || '"'
                  )
                  OR EXISTS (
                      SELECT 1
                      FROM import_media_interests interest
                      WHERE interest.media_id = $assetId
                        AND setting.value_json = '"' || interest.import_unit_id || '"'
                  )
              )
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$key", ImportPriorityOperations.SettingKey);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(definition.OwnerId));
        var focused = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        // Newly created jobs for the focused import inherit P1, not P0. Import work is never
        // interactive — the person is not waiting on the scheduler for this job.
        return focused ? definition with { Priority = JobPriorityPolicy.PriorityCurrentImport } : definition;
    }

    public async Task<int> PrioritizeOwnerAsync(
        string ownerType,
        Guid ownerId,
        int priority,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerType);
        EnsureNonEmpty(ownerId, nameof(ownerId));
        var clamped = JobPriorityPolicy.Clamp(priority);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using var update = transaction.CreateCommand(
            """
            UPDATE jobs
            SET priority = $priority,
                row_version = row_version + 1
            WHERE owner_type = $ownerType
              AND owner_id = $ownerId
              AND state IN ('PENDING','RUNNABLE','PAUSED','FAILED_RETRYABLE');
            """);
        update.Parameters.AddWithValue("$ownerType", ownerType.Trim());
        update.Parameters.AddWithValue("$ownerId", DbGuid.Format(ownerId));
        update.Parameters.AddWithValue("$priority", clamped);
        var count = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    public async Task<bool> SkipJobFailureAsync(
        Guid jobId,
        DateTimeOffset skippedAtUtc,
        bool unblockDependents = false,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(jobId, nameof(jobId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var nowMs = DbTime.Format(skippedAtUtc);
        await using var update = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'CANCELLED',
                not_before_ms = NULL,
                error_code = 'SKIPPED_BY_USER',
                error_detail_safe = 'The failure was explicitly skipped by user/workflow request.',
                completed_at_ms = $nowMs,
                row_version = row_version + 1
            WHERE job_id = $jobId
              AND state IN ('FAILED_RETRYABLE','FAILED_TERMINAL');
            """);
        update.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
        update.Parameters.AddWithValue("$nowMs", nowMs);
        var skipped = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;

        if (skipped && unblockDependents)
        {
            await using var unblock = transaction.CreateCommand(
                """
                DELETE FROM job_dependencies
                WHERE depends_on_job_id = $jobId;
                """);
            unblock.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
            await unblock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (skipped)
        {
            await using var itemUpdate = transaction.CreateCommand(
                """
                UPDATE import_items
                SET disposition = 'SKIPPED',
                    row_version = row_version + 1
                WHERE import_item_id IN (
                    SELECT owner_id FROM jobs WHERE job_id = $jobId AND owner_type = 'IMPORT_ITEM'
                );
                """);
            itemUpdate.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
            await itemUpdate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (skipped) JobSignals.RaiseStatusChanged();
        return skipped;
    }

    public async Task<int> ClearFinishedHistoryAsync(
        string? ownerType = null,
        Guid? ownerId = null,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var filterUnit = ownerId is null
            ? string.Empty
            : "AND (import_unit_id = $ownerId OR import_session_id = $ownerId)";

        await using var updateUnits = transaction.CreateCommand(
            $"""
            UPDATE import_units
            SET hidden_from_history = 1,
                row_version = row_version + 1
            WHERE state IN ('COMPLETED','CANCELLED')
              AND hidden_from_history = 0
              {filterUnit};
            """);
        if (ownerId is not null)
        {
            updateUnits.Parameters.AddWithValue("$ownerId", DbGuid.Format(ownerId.Value));
        }
        var unitsHidden = await updateUnits.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var filterSession = ownerId is null
            ? string.Empty
            : "AND import_session_id = $ownerId";

        await using var updateSessions = transaction.CreateCommand(
            $"""
            UPDATE import_sessions
            SET hidden_from_history = 1,
                row_version = row_version + 1
            WHERE state IN ('COMPLETED','CANCELLED')
              AND hidden_from_history = 0
              {filterSession}
              AND NOT EXISTS (
                  SELECT 1 FROM import_units u
                  WHERE u.import_session_id = import_sessions.import_session_id
                    AND u.hidden_from_history = 0
              );
            """);
        if (ownerId is not null)
        {
            updateSessions.Parameters.AddWithValue("$ownerId", DbGuid.Format(ownerId.Value));
        }
        var sessionsHidden = await updateSessions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return unitsHidden + sessionsHidden;
    }

    /// <summary>
    /// Reconciles jobs that were left RUNNING by a previous process that exited. Distinguishes
    /// the durable ImportUnit lifecycle so that:
    ///
    /// - Paused imports: interrupted jobs become PAUSED (not failed).
    /// - Cancelled imports: interrupted jobs become CANCELLED (intentional control state, not failure).
    /// - Terminal-failed imports: interrupted jobs become FAILED_TERMINAL.
    /// - Active imports: interrupted jobs become PENDING for resumption from checkpoint.
    ///
    /// Does not increment retry/failure semantics solely because the process restarted.
    /// </summary>
    public async Task<int> ReconcileInterruptedRunningJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        // 1. Running jobs owned by paused ImportUnits → PAUSED.
        //    Import membership resolved through import_items → candidate_media_id.
        await using (var pausedUpdate = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'PAUSED',
                error_code = NULL,
                error_detail_safe = NULL,
                not_before_ms = NULL,
                row_version = row_version + 1
            WHERE state = 'RUNNING'
              AND owner_type = 'Media'
              AND owner_id IN (
                  SELECT DISTINCT i.candidate_media_id
                  FROM import_items i
                  JOIN import_units u ON u.import_unit_id = i.import_unit_id
                  WHERE i.candidate_media_id IS NOT NULL
                    AND u.is_paused = 1
                    AND u.state NOT IN ('COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION','CANCELLED','FAILED_TERMINAL')
                  UNION
                  SELECT interest.media_id
                  FROM import_media_interests interest
                  JOIN import_units u ON u.import_unit_id = interest.import_unit_id
                  WHERE u.is_paused = 1
                    AND u.state NOT IN ('COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION','CANCELLED','FAILED_TERMINAL'))
              AND NOT EXISTS (
                  SELECT 1
                  FROM import_media_interests other
                  JOIN import_units consumer ON consumer.import_unit_id = other.import_unit_id
                  WHERE other.media_id = jobs.owner_id
                    AND consumer.is_paused = 0
                    AND consumer.state NOT IN (
                        'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                        'CANCELLED','FAILED_TERMINAL'
                    )
              );
            """))
        {
            await pausedUpdate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 2. Running jobs owned by cancelled ImportUnits → CANCELLED.
        //    Cancelled import is an intentional control state, not a failure.
        await using (var cancelledUpdate = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'CANCELLED',
                error_code = NULL,
                error_detail_safe = NULL,
                not_before_ms = NULL,
                row_version = row_version + 1
            WHERE state = 'RUNNING'
              AND owner_type = 'Media'
              AND owner_id IN (
                  SELECT DISTINCT i.candidate_media_id
                  FROM import_items i
                  JOIN import_units u ON u.import_unit_id = i.import_unit_id
                  WHERE i.candidate_media_id IS NOT NULL
                    AND u.state = 'CANCELLED'
                  UNION
                  SELECT interest.media_id
                  FROM import_media_interests interest
                  JOIN import_units u ON u.import_unit_id = interest.import_unit_id
                  WHERE u.state = 'CANCELLED')
              AND NOT EXISTS (
                  SELECT 1
                  FROM import_media_interests other
                  JOIN import_units consumer ON consumer.import_unit_id = other.import_unit_id
                  WHERE other.media_id = jobs.owner_id
                    AND consumer.state NOT IN (
                        'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                        'CANCELLED','FAILED_TERMINAL'
                    )
              );
            """))
        {
            await cancelledUpdate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 3. Running jobs owned by terminal-failed ImportUnits → FAILED_TERMINAL.
        await using (var terminalUpdate = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'FAILED_TERMINAL',
                error_code = 'IMPORT_UNIT_TERMINATED',
                error_detail_safe = 'The owning import was terminated before the process exited.',
                not_before_ms = NULL,
                row_version = row_version + 1
            WHERE state = 'RUNNING'
              AND owner_type = 'Media'
              AND owner_id IN (
                  SELECT DISTINCT i.candidate_media_id
                  FROM import_items i
                  JOIN import_units u ON u.import_unit_id = i.import_unit_id
                  WHERE i.candidate_media_id IS NOT NULL
                    AND u.state = 'FAILED_TERMINAL'
                  UNION
                  SELECT interest.media_id
                  FROM import_media_interests interest
                  JOIN import_units u ON u.import_unit_id = interest.import_unit_id
                  WHERE u.state = 'FAILED_TERMINAL')
              AND NOT EXISTS (
                  SELECT 1
                  FROM import_media_interests other
                  JOIN import_units consumer ON consumer.import_unit_id = other.import_unit_id
                  WHERE other.media_id = jobs.owner_id
                    AND consumer.state NOT IN (
                        'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                        'CANCELLED','FAILED_TERMINAL'
                    )
              );
            """))
        {
            await terminalUpdate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 4. Remaining running jobs in active imports → PENDING (resumable from checkpoint).
        //    This covers both Media-owned import jobs and any other running jobs.
        //    The scheduler's PromotePendingAsync will re-evaluate dependencies and move eligible
        //    jobs to RUNNABLE.
        var affected = 0;
        await using (var resumeUpdate = transaction.CreateCommand(
            """
            UPDATE jobs
            SET state = 'PENDING',
                error_code = NULL,
                error_detail_safe = NULL,
                not_before_ms = NULL,
                row_version = row_version + 1
            WHERE state = 'RUNNING';
            """))
        {
            affected = await resumeUpdate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    private async Task<int> TransitionScopeAsync(
        string updatePrefix,
        Guid? jobId,
        string? ownerType,
        Guid? ownerId,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] extraParameters)
    {
        var hasOwnerScope = !string.IsNullOrWhiteSpace(ownerType) && ownerId is not null;
        if (jobId is null && !hasOwnerScope)
        {
            throw new ArgumentException(
                "A job state transition needs a scope: either a JobId or an owner type and id.",
                nameof(jobId));
        }

        if (jobId is { } id)
        {
            EnsureNonEmpty(id, nameof(jobId));
        }

        if (ownerId is { } owner && hasOwnerScope)
        {
            EnsureNonEmpty(owner, nameof(ownerId));
        }

        var sql = updatePrefix
            + (jobId is null ? string.Empty : "\n  AND job_id = $jobId")
            + (hasOwnerScope ? "\n  AND owner_type = $ownerType AND owner_id = $ownerId" : string.Empty)
            + ";";

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var update = transaction.CreateCommand(sql);
        if (jobId is { } scopedJobId)
        {
            update.Parameters.AddWithValue("$jobId", DbGuid.Format(scopedJobId));
        }

        if (hasOwnerScope)
        {
            update.Parameters.AddWithValue("$ownerType", ownerType!.Trim());
            update.Parameters.AddWithValue("$ownerId", DbGuid.Format(ownerId!.Value));
        }

        foreach (var (name, value) in extraParameters)
        {
            update.Parameters.AddWithValue(name, value);
        }

        var affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (hasOwnerScope && updatePrefix.Contains("'PAUSED'"))
        {
            if (string.Equals(ownerType, "IMPORT_UNIT", StringComparison.OrdinalIgnoreCase))
            {
                await using var pauseUnit = transaction.CreateCommand(
                    "UPDATE import_units SET is_paused = 1, row_version = row_version + 1 WHERE import_unit_id = $ownerId;");
                pauseUnit.Parameters.AddWithValue("$ownerId", DbGuid.Format(ownerId!.Value));
                await pauseUnit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(ownerType, "IMPORT_SESSION", StringComparison.OrdinalIgnoreCase))
            {
                await using var pauseSession = transaction.CreateCommand(
                    """
                    UPDATE import_sessions SET is_paused = 1, row_version = row_version + 1 WHERE import_session_id = $ownerId;
                    UPDATE import_units SET is_paused = 1, row_version = row_version + 1 WHERE import_session_id = $ownerId;
                    """);
                pauseSession.Parameters.AddWithValue("$ownerId", DbGuid.Format(ownerId!.Value));
                await pauseSession.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else if (hasOwnerScope && updatePrefix.Contains("'PENDING'"))
        {
            if (string.Equals(ownerType, "IMPORT_UNIT", StringComparison.OrdinalIgnoreCase))
            {
                await using var resumeUnit = transaction.CreateCommand(
                    "UPDATE import_units SET is_paused = 0, row_version = row_version + 1 WHERE import_unit_id = $ownerId;");
                resumeUnit.Parameters.AddWithValue("$ownerId", DbGuid.Format(ownerId!.Value));
                await resumeUnit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(ownerType, "IMPORT_SESSION", StringComparison.OrdinalIgnoreCase))
            {
                await using var resumeSession = transaction.CreateCommand(
                    """
                    UPDATE import_sessions SET is_paused = 0, row_version = row_version + 1 WHERE import_session_id = $ownerId;
                    UPDATE import_units SET is_paused = 0, row_version = row_version + 1 WHERE import_session_id = $ownerId;
                    """);
                resumeSession.Parameters.AddWithValue("$ownerId", DbGuid.Format(ownerId!.Value));
                await resumeSession.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (affected > 0) JobSignals.RaiseStatusChanged();
        return affected;
    }

    private static void ValidateProgress(long? completed, long? total)
    {
        if (completed is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completed), "Progress cannot be negative.");
        }

        if (total is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(total), "Progress total cannot be negative.");
        }

        if (completed is not null && total is not null && completed > total)
        {
            throw new ArgumentOutOfRangeException(nameof(completed), "Progress cannot exceed the aggregate total.");
        }
    }

    private static async Task<JobDefinition?> ReadDefinitionAsync(
        CatalogTransaction transaction,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT kind, lane, state, priority, owner_type, owner_id,
                   max_attempts, not_before_ms, checkpoint_json
            FROM jobs
            WHERE job_id = $jobId;
            """);
        command.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new JobDefinition(
            jobId,
            reader.GetString(0),
            DbEnum.ParseJobLane(reader.GetString(1)),
            DbEnum.ParseJobState(reader.GetString(2)),
            reader.GetInt32(3),
            reader.GetString(4),
            DbGuid.Parse(reader.GetString(5)),
            reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.GetString(8));
    }

    private static async Task EnsureJobsExistAsync(
        CatalogTransaction transaction,
        Guid jobId,
        Guid dependsOnJobId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT COUNT(*)
            FROM jobs
            WHERE job_id IN ($jobId, $dependsOnJobId);
            """);
        command.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
        command.Parameters.AddWithValue("$dependsOnJobId", DbGuid.Format(dependsOnJobId));
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (count != 2)
        {
            throw new CatalogInvariantException("Both durable jobs must exist before adding a dependency.");
        }
    }

    private static async Task<bool> WouldCreateCycleAsync(
        CatalogTransaction transaction,
        Guid jobId,
        Guid dependsOnJobId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            WITH RECURSIVE ancestors(job_id) AS (
                SELECT $dependsOnJobId
                UNION
                SELECT dependency.depends_on_job_id
                FROM job_dependencies dependency
                JOIN ancestors ON ancestors.job_id = dependency.job_id
            )
            SELECT EXISTS(SELECT 1 FROM ancestors WHERE job_id = $jobId);
            """);
        command.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));
        command.Parameters.AddWithValue("$dependsOnJobId", DbGuid.Format(dependsOnJobId));
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static bool IsTerminal(JobState state) => state is
        JobState.Succeeded or JobState.FailedTerminal or JobState.Cancelled;

    private static bool HasSameCreationIdentity(JobDefinition existing, JobDefinition requested) =>
        string.Equals(existing.Kind, requested.Kind, StringComparison.Ordinal)
        && existing.Lane == requested.Lane
        && string.Equals(existing.OwnerType, requested.OwnerType, StringComparison.Ordinal)
        && existing.OwnerId == requested.OwnerId
        && existing.MaxAttempts == requested.MaxAttempts;

    private static void ValidateJson(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        try
        {
            using var _ = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Durable job checkpoint JSON must be valid.", parameterName, exception);
        }
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }
}

public sealed record JobDefinition(
    Guid JobId,
    string Kind,
    JobLane Lane,
    JobState State,
    int Priority,
    string OwnerType,
    Guid OwnerId,
    int MaxAttempts,
    long? NotBeforeMilliseconds = null,
    string CheckpointJson = "{}");
