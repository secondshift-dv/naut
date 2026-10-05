using System.Globalization;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed class SchedulerReads
{
    private readonly CatalogConnectionFactory _connectionFactory;

    public SchedulerReads(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
    }

    public SchedulerReads(CatalogConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT job_id, kind, lane, state, priority, owner_type, owner_id,
                   attempt, max_attempts, not_before_ms, progress_completed, progress_total,
                   stage, checkpoint_json, error_code, error_detail_safe,
                   created_at_ms, started_at_ms, completed_at_ms, row_version
            FROM jobs
            WHERE job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadJobRecord(reader)
            : null;
    }

    public Task<JobRecord?> GetJobRecordAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        GetJobAsync(jobId, cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetRunnableJobIdsAsync(
        JobLane lane,
        int limit,
        long nowMs,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Tier ceiling caps effective priority so aged work never crosses a semantic class boundary.
        // P0(≥95)→100, P1(≥80)→94, P2(≥60)→79, P3(≥40)→59, maintenance(<40)→39.
        command.CommandText =
            """
            SELECT jobs.job_id
            FROM jobs
            WHERE jobs.state = 'RUNNABLE'
              AND jobs.lane = $lane
              AND (jobs.not_before_ms IS NULL OR jobs.not_before_ms <= $nowMs)
              AND NOT EXISTS (
                  SELECT 1
                  FROM job_dependencies dependency
                  JOIN jobs predecessor
                    ON predecessor.job_id = dependency.depends_on_job_id
                  WHERE dependency.job_id = jobs.job_id
                    AND predecessor.state <> 'SUCCEEDED')
            ORDER BY MIN(
                         CASE
                             WHEN jobs.priority >= 95 THEN 100
                             WHEN jobs.priority >= 80 THEN 94
                             WHEN jobs.priority >= 60 THEN 79
                             WHEN jobs.priority >= 40 THEN 59
                             ELSE 39
                         END,
                         jobs.priority
                     ) DESC,
                     jobs.not_before_ms,
                     jobs.created_at_ms,
                     jobs.job_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$lane", DbEnum.Format(lane));
        command.Parameters.AddWithValue("$nowMs", nowMs);
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadGuidListAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> GetRunnableJobIdsAsync(
        JobLane lane,
        int limit,
        long nowMs,
        JobPriorityAging aging,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aging);
        if (!aging.IsEnabled)
        {
            return await GetRunnableJobIdsAsync(lane, limit, nowMs, cancellationToken).ConfigureAwait(false);
        }

        ValidateLimit(limit);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Tier ceiling prevents aged lower-priority work from entering a higher tier.
        // P0(≥95)→100, P1(≥80)→94, P2(≥60)→79, P3(≥40)→59, maintenance(<40)→39.
        command.CommandText =
            """
            SELECT jobs.job_id
            FROM jobs
            WHERE jobs.state = 'RUNNABLE'
              AND jobs.lane = $lane
              AND (jobs.not_before_ms IS NULL OR jobs.not_before_ms <= $nowMs)
              AND NOT EXISTS (
                  SELECT 1
                  FROM job_dependencies dependency
                  JOIN jobs predecessor
                    ON predecessor.job_id = dependency.depends_on_job_id
                  WHERE dependency.job_id = jobs.job_id
                    AND predecessor.state <> 'SUCCEEDED')
            ORDER BY MIN(
                         CASE
                             WHEN jobs.priority >= 95 THEN 100
                             WHEN jobs.priority >= 80 THEN 94
                             WHEN jobs.priority >= 60 THEN 79
                             WHEN jobs.priority >= 40 THEN 59
                             ELSE 39
                         END,
                         jobs.priority
                           + ((MAX(0, $nowMs - jobs.created_at_ms) / $agingIntervalMs) * $agingStep)
                     ) DESC,
                     jobs.created_at_ms,
                     jobs.job_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$lane", DbEnum.Format(lane));
        command.Parameters.AddWithValue("$nowMs", nowMs);
        command.Parameters.AddWithValue("$agingIntervalMs", aging.IntervalMilliseconds);
        command.Parameters.AddWithValue("$agingStep", aging.Step);
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadGuidListAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the oldest runnable job in <paramref name="lane"/> whose <em>base</em> priority
    /// places it in a semantic tier strictly lower (higher enum value) than
    /// <paramref name="belowTier"/>. Uses a CASE-based tier classifier on the durable
    /// <c>priority</c> column so that same-tier jobs are always excluded regardless of aging.
    /// Returns at most one job.
    /// </summary>
    public async Task<Guid?> GetOldestLowerTierCandidateAsync(
        JobLane lane,
        FairnessTier belowTier,
        long nowMs,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Classify each job's base priority into a tier and select only those whose tier is
        // strictly lower (higher enum value) than the dispatched tier. This guarantees the
        // candidate is never from the same semantic class.
        command.CommandText =
            """
            SELECT job_id
            FROM jobs
            WHERE state = 'RUNNABLE'
              AND lane = $lane
              AND (not_before_ms IS NULL OR not_before_ms <= $nowMs)
              AND CASE
                      WHEN priority >= 95 THEN 0
                      WHEN priority >= 80 THEN 1
                      WHEN priority >= 60 THEN 2
                      WHEN priority >= 40 THEN 3
                      ELSE 4
                  END > $belowTier
              AND NOT EXISTS (
                  SELECT 1
                  FROM job_dependencies dependency
                  JOIN jobs predecessor
                    ON predecessor.job_id = dependency.depends_on_job_id
                  WHERE dependency.job_id = jobs.job_id
                    AND predecessor.state <> 'SUCCEEDED')
            ORDER BY created_at_ms ASC, job_id ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$lane", DbEnum.Format(lane));
        command.Parameters.AddWithValue("$nowMs", nowMs);
        command.Parameters.AddWithValue("$belowTier", (int)belowTier);

        var ids = await ReadGuidListAsync(command, cancellationToken).ConfigureAwait(false);
        return ids.Count > 0 ? ids[0] : null;
    }

    public async Task<IReadOnlyList<Guid>> GetRetryableJobIdsAsync(
        JobLane lane,
        int limit,
        long nowMs,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT job_id
            FROM jobs
            WHERE state = 'FAILED_RETRYABLE'
              AND lane = $lane
              AND attempt < max_attempts
              AND (not_before_ms IS NULL OR not_before_ms <= $nowMs)
            ORDER BY not_before_ms, created_at_ms, job_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$lane", DbEnum.Format(lane));
        command.Parameters.AddWithValue("$nowMs", nowMs);
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadGuidListAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> GetExhaustedRetryJobIdsAsync(
        JobLane lane,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT job_id
            FROM jobs
            WHERE state = 'FAILED_RETRYABLE'
              AND lane = $lane
              AND attempt >= max_attempts
            ORDER BY created_at_ms, job_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$lane", DbEnum.Format(lane));
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadGuidListAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> GetNonTerminalJobIdsForOwnerAsync(
        string ownerType,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerType);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT job_id
            FROM jobs
            WHERE owner_type = $ownerType
              AND owner_id = $ownerId
              AND state IN ('PENDING','RUNNABLE','RUNNING','PAUSED','FAILED_RETRYABLE')
            ORDER BY created_at_ms, job_id;
            """;
        command.Parameters.AddWithValue("$ownerType", ownerType.Trim());
        command.Parameters.AddWithValue("$ownerId", DbGuid.Format(ownerId));

        return await ReadGuidListAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns job IDs of all currently RUNNING jobs belonging to one ImportUnit, resolved through
    /// the import_items → candidate_media_id relationship (Media-owned jobs).
    /// Used by ImportUnitControlAuthority to signal running handlers during Pause/Cancel.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetRunningJobIdsForImportUnitAsync(
        Guid importUnitId,
        CancellationToken cancellationToken = default)
    {
        if (importUnitId == Guid.Empty) return [];
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT j.job_id
            FROM import_items item
            JOIN jobs j ON j.owner_type = 'Media' AND j.owner_id = item.candidate_media_id
            WHERE item.import_unit_id = $unitId
              AND item.candidate_media_id IS NOT NULL
              AND j.state = 'RUNNING'
              AND NOT EXISTS (
                  SELECT 1
                  FROM import_media_interests other
                  JOIN import_units consumer ON consumer.import_unit_id = other.import_unit_id
                  WHERE other.media_id = j.owner_id
                    AND other.import_unit_id <> $unitId
                    AND consumer.is_paused = 0
                    AND consumer.state NOT IN (
                        'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                        'CANCELLED','FAILED_TERMINAL'
                    )
              )
            UNION
            SELECT j.job_id
            FROM import_media_interests mine
            JOIN jobs j ON j.owner_type = 'Media' AND j.owner_id = mine.media_id
            WHERE mine.import_unit_id = $unitId
              AND j.state = 'RUNNING'
              AND NOT EXISTS (
                  SELECT 1
                  FROM import_media_interests other
                  JOIN import_units consumer ON consumer.import_unit_id = other.import_unit_id
                  WHERE other.media_id = mine.media_id
                    AND other.import_unit_id <> $unitId
                    AND consumer.is_paused = 0
                    AND consumer.state NOT IN (
                        'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                        'CANCELLED','FAILED_TERMINAL'
                    )
              )
            UNION
            SELECT job_id
            FROM jobs
            WHERE owner_type = 'ImportUnit'
              AND owner_id = $unitId
              AND state = 'RUNNING';
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(importUnitId));
        return await ReadGuidListAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> GetCancellableRunningJobIdsForImportUnitAsync(
        Guid importUnitId,
        CancellationToken cancellationToken = default)
    {
        if (importUnitId == Guid.Empty) return [];

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH unit_assets(media_id) AS (
                SELECT candidate_media_id
                FROM import_items
                WHERE import_unit_id = $unitId
                  AND candidate_media_id IS NOT NULL
                UNION
                SELECT media_id
                FROM import_media_interests
                WHERE import_unit_id = $unitId
            )
            SELECT j.job_id
            FROM unit_assets mine
            JOIN jobs j ON j.owner_type = 'Media' AND j.owner_id = mine.media_id
            WHERE j.state = 'RUNNING'
              AND NOT EXISTS (
                  SELECT 1
                  FROM import_items other
                  JOIN import_units consumer ON consumer.import_unit_id = other.import_unit_id
                  WHERE other.import_unit_id <> $unitId
                    AND (other.candidate_media_id = mine.media_id OR other.reused_media_id = mine.media_id)
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
                    AND other.media_id = mine.media_id
                    AND consumer.state NOT IN (
                        'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                        'CANCELLED','FAILED_TERMINAL'
                    )
              )
            UNION
            SELECT job_id
            FROM jobs
            WHERE owner_type = 'ImportUnit'
              AND owner_id = $unitId
              AND state = 'RUNNING';
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(importUnitId));
        return await ReadGuidListAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountRunningJobsForImportUnitAsync(
        Guid importUnitId,
        CancellationToken cancellationToken = default)
    {
        if (importUnitId == Guid.Empty) return 0;
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM (
                SELECT j.job_id
                FROM import_items item
                JOIN jobs j ON j.owner_type = 'Media' AND j.owner_id = item.candidate_media_id
                WHERE item.import_unit_id = $unitId
                  AND item.candidate_media_id IS NOT NULL
                  AND j.state = 'RUNNING'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM import_media_interests other
                      JOIN import_units consumer ON consumer.import_unit_id = other.import_unit_id
                      WHERE other.media_id = j.owner_id
                        AND other.import_unit_id <> $unitId
                        AND consumer.is_paused = 0
                        AND consumer.state NOT IN (
                            'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                            'CANCELLED','FAILED_TERMINAL'
                        )
                  )
                UNION
                SELECT j.job_id
                FROM import_media_interests mine
                JOIN jobs j ON j.owner_type = 'Media' AND j.owner_id = mine.media_id
                WHERE mine.import_unit_id = $unitId
                  AND j.state = 'RUNNING'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM import_media_interests other
                      JOIN import_units consumer ON consumer.import_unit_id = other.import_unit_id
                      WHERE other.media_id = mine.media_id
                        AND other.import_unit_id <> $unitId
                        AND consumer.is_paused = 0
                        AND consumer.state NOT IN (
                            'COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION',
                            'CANCELLED','FAILED_TERMINAL'
                        )
                  )
                UNION
                SELECT job_id
                FROM jobs
                WHERE owner_type = 'ImportUnit'
                  AND owner_id = $unitId
                  AND state = 'RUNNING'
            );
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(importUnitId));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<Guid>> GetPendingJobIdsAsync(
        JobLane lane,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT job_id
            FROM jobs
            WHERE state = 'PENDING' AND lane = $lane
            ORDER BY priority DESC, created_at_ms, job_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$lane", DbEnum.Format(lane));
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadGuidListAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JobDependencyFact>> GetDependenciesAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT dependency.job_id, dependency.depends_on_job_id, predecessor.state
            FROM job_dependencies dependency
            JOIN jobs predecessor ON predecessor.job_id = dependency.depends_on_job_id
            WHERE dependency.job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));

        var facts = new List<JobDependencyFact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            facts.Add(new JobDependencyFact(
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1)),
                DbEnum.ParseJobState(reader.GetString(2))));
        }

        return facts;
    }

    public async Task<IReadOnlyList<Guid>> GetDirectDependentsAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT job_id
            FROM job_dependencies
            WHERE depends_on_job_id = $jobId
            ORDER BY job_id;
            """;
        command.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));

        return await ReadGuidListAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountNonterminalJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM jobs
            WHERE state IN ('PENDING','RUNNABLE','RUNNING','PAUSED','FAILED_RETRYABLE');
            """;

        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyDictionary<JobLane, long>> GetOldestRunnableCreatedAtByLaneAsync(
        long nowMs,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT lane, MIN(created_at_ms)
            FROM jobs
            WHERE state = 'RUNNABLE'
              AND (not_before_ms IS NULL OR not_before_ms <= $nowMs)
            GROUP BY lane;
            """;
        command.Parameters.AddWithValue("$nowMs", nowMs);

        var oldest = new Dictionary<JobLane, long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(1))
            {
                continue;
            }

            oldest[DbEnum.ParseJobLane(reader.GetString(0))] = reader.GetInt64(1);
        }

        return oldest;
    }

    public async Task<IReadOnlyDictionary<JobLane, IReadOnlyDictionary<JobState, long>>>
        GetStateCountsByLaneAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT lane, state, COUNT(*)
            FROM jobs
            GROUP BY lane, state;
            """;

        var counts = new Dictionary<JobLane, Dictionary<JobState, long>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var lane = DbEnum.ParseJobLane(reader.GetString(0));
            var state = DbEnum.ParseJobState(reader.GetString(1));
            var count = reader.GetInt64(2);
            if (!counts.TryGetValue(lane, out var laneCounts))
            {
                laneCounts = [];
                counts.Add(lane, laneCounts);
            }

            laneCounts[state] = count;
        }

        return counts.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<JobState, long>)pair.Value);
    }

    public async Task<bool> HasDispatchableWorkAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM jobs
                WHERE state IN ('RUNNABLE', 'PENDING', 'RUNNING', 'FAILED_RETRYABLE'));
            """;

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    public async Task<SchedulerUnitSummary> GetUnitSummaryAsync(
        string ownerType,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerType);
        if (ownerId == Guid.Empty)
        {
            return new SchedulerUnitSummary(ownerType, ownerId, 0, 0, 0, 0, 0, 0, 0, 0, null);
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SUM(CASE WHEN state = 'PENDING' THEN 1 ELSE 0 END) AS pending_count,
                   SUM(CASE WHEN state = 'RUNNING' THEN 1 ELSE 0 END) AS running_count,
                   SUM(CASE WHEN state = 'SUCCEEDED' THEN 1 ELSE 0 END) AS succeeded_count,
                   SUM(CASE WHEN state = 'FAILED_RETRYABLE' THEN 1 ELSE 0 END) AS failed_retryable_count,
                   SUM(CASE WHEN state = 'FAILED_TERMINAL' THEN 1 ELSE 0 END) AS failed_terminal_count,
                   SUM(CASE WHEN state = 'CANCELLED' THEN 1 ELSE 0 END) AS cancelled_count,
                   COALESCE(SUM(progress_completed), 0) AS progress_completed,
                   COALESCE(SUM(progress_total), 0) AS progress_total
            FROM jobs
            WHERE owner_type = $ownerType AND owner_id = $ownerId;
            """;
        command.Parameters.AddWithValue("$ownerType", ownerType.Trim());
        command.Parameters.AddWithValue("$ownerId", DbGuid.Format(ownerId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SchedulerUnitSummary(ownerType, ownerId, 0, 0, 0, 0, 0, 0, 0, 0, null);
        }

        return new SchedulerUnitSummary(
            ownerType,
            ownerId,
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            null);
    }

    /// <summary>
    /// Aggregates the background work belonging to one import.
    ///
    /// Import jobs are owned by the candidate <em>asset</em>, not by the import unit, so asking the
    /// scheduler for "ImportUnit" work returns nothing. That is why per-import progress read as
    /// permanently indeterminate. This walks the unit's items to their candidate media and sums the
    /// real job rows, which is what a person sees as "this import's progress".
    /// </summary>
    public async Task<SchedulerUnitSummary> GetImportUnitWorkSummaryAsync(
        Guid importUnitId,
        CancellationToken cancellationToken = default)
    {
        if (importUnitId == Guid.Empty)
        {
            return new SchedulerUnitSummary("ImportUnit", importUnitId, 0, 0, 0, 0, 0, 0, 0, 0, null);
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SUM(CASE WHEN j.state = 'PENDING' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN j.state = 'RUNNING' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN j.state = 'SUCCEEDED' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN j.state = 'FAILED_RETRYABLE' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN j.state = 'FAILED_TERMINAL' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN j.state = 'CANCELLED' THEN 1 ELSE 0 END)
            FROM import_items i
            JOIN jobs j ON j.owner_type = 'Media' AND j.owner_id = i.candidate_media_id
            WHERE i.import_unit_id = $unitId
              AND i.candidate_media_id IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(importUnitId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SchedulerUnitSummary("ImportUnit", importUnitId, 0, 0, 0, 0, 0, 0, 0, 0, null);
        }

        static int Read(Microsoft.Data.Sqlite.SqliteDataReader reader, int index) =>
            reader.IsDBNull(index) ? 0 : reader.GetInt32(index);

        var pending = Read(reader, 0);
        var running = Read(reader, 1);
        var succeeded = Read(reader, 2);
        var failedRetryable = Read(reader, 3);
        var failedTerminal = Read(reader, 4);
        var cancelled = Read(reader, 5);

        // Progress is expressed as finished-out-of-total so the bar reflects the whole import rather
        // than the byte progress of whichever single job happens to be running.
        var total = pending + running + succeeded + failedRetryable + failedTerminal + cancelled;
        var finished = succeeded + failedTerminal + cancelled;

        return new SchedulerUnitSummary(
            "ImportUnit",
            importUnitId,
            pending,
            running,
            succeeded,
            failedRetryable,
            failedTerminal,
            cancelled,
            finished,
            total,
            null);
    }

    public async Task<IReadOnlyList<JobRecord>> GetJobsForOwnerAsync(
        string ownerType,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerType);
        if (ownerId == Guid.Empty)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, kind, lane, state, priority, owner_type, owner_id,
                   attempt, max_attempts, not_before_ms, progress_completed, progress_total,
                   stage, checkpoint_json, error_code, error_detail_safe,
                   created_at_ms, started_at_ms, completed_at_ms, row_version
            FROM jobs
            WHERE owner_type = $ownerType AND owner_id = $ownerId
            ORDER BY created_at_ms ASC, job_id ASC;
            """;
        command.Parameters.AddWithValue("$ownerType", ownerType.Trim());
        command.Parameters.AddWithValue("$ownerId", DbGuid.Format(ownerId));

        var list = new List<JobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadJobRecord(reader));
        }

        return list;
    }

    /// <summary>
    /// Every job owned by any candidate/reused asset of one import unit, in one query. Readiness used
    /// to issue one query per item, which made each re-evaluation of a large import O(items) round trips.
    /// </summary>
    public async Task<IReadOnlyList<JobRecord>> GetJobsForImportUnitMediasAsync(
        Guid importUnitId,
        CancellationToken cancellationToken = default)
    {
        if (importUnitId == Guid.Empty)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.job_id, j.kind, j.lane, j.state, j.priority, j.owner_type, j.owner_id,
                   j.attempt, j.max_attempts, j.not_before_ms, j.progress_completed, j.progress_total,
                   j.stage, j.checkpoint_json, j.error_code, j.error_detail_safe,
                   j.created_at_ms, j.started_at_ms, j.completed_at_ms, j.row_version
            FROM import_items i
            JOIN jobs j ON j.owner_type = 'Media' AND j.owner_id = i.candidate_media_id
            WHERE i.import_unit_id = $unitId
              AND i.candidate_media_id IS NOT NULL
            ORDER BY j.created_at_ms ASC, j.job_id ASC;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(importUnitId));

        var list = new List<JobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadJobRecord(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<Guid>> GetYieldableTransfersAsync(
        Guid focusedUnitId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT running.job_id
            FROM jobs focused
            JOIN import_items fi ON fi.candidate_media_id = focused.owner_id
            JOIN import_units fu ON fu.import_unit_id = fi.import_unit_id
            JOIN jobs running ON running.kind = 'TransferOriginal'
                             AND running.lane = 'IO' AND running.state = 'RUNNING'
            JOIN import_items ri ON ri.candidate_media_id = running.owner_id
            WHERE fi.import_unit_id = $focusedUnitId
              AND fu.is_paused = 0
              AND focused.kind = 'TransferOriginal'
              AND focused.state IN ('PENDING','RUNNABLE')
              AND focused.priority >= 80
              AND ri.import_unit_id <> $focusedUnitId
              AND NOT EXISTS (SELECT 1 FROM import_media_interests shared
                              WHERE shared.media_id = running.owner_id
                                AND shared.import_unit_id = $focusedUnitId)
              AND running.job_id <> focused.job_id;
            """;
        command.Parameters.AddWithValue("$focusedUnitId", DbGuid.Format(focusedUnitId));
        return await ReadGuidListAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Guid>> ReadGuidListAsync(
        Microsoft.Data.Sqlite.SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(DbGuid.Parse(reader.GetString(0)));
        }

        return ids;
    }

    private static JobRecord ReadJobRecord(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new JobRecord(
            DbGuid.Parse(reader.GetString(0)),
            reader.GetString(1),
            DbEnum.ParseJobLane(reader.GetString(2)),
            DbEnum.ParseJobState(reader.GetString(3)),
            reader.GetInt32(4),
            reader.GetString(5),
            DbGuid.Parse(reader.GetString(6)),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.IsDBNull(10) ? 0L : reader.GetInt64(10),
            reader.IsDBNull(11) ? 0L : reader.GetInt64(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.GetInt64(16),
            reader.IsDBNull(17) ? null : reader.GetInt64(17),
            reader.IsDBNull(18) ? null : reader.GetInt64(18),
            reader.GetInt64(19));
    }

    private static void ValidateLimit(int limit)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A dispatch batch must be positive.");
        }
    }
}

public sealed record JobDependencyFact(Guid JobId, Guid DependsOnJobId, JobState PredecessorState);
