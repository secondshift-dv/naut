using Neuterradise.App.Import.Preparation;

namespace Neuterradise.App.SystemServices.Database.Writes;

public sealed class CapabilityWrites
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public CapabilityWrites(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Upsert one capability row. Idempotent: if the row already exists with the same or later
    /// state, the write is a no-op.
    /// </summary>
    public async Task UpsertCapabilityAsync(
        Guid assetId,
        MediaCapability capability,
        MediaCapabilityState state,
        Guid? jobId = null,
        string? sourceFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO media_capability_readiness (media_id, capability, state, job_id, source_fingerprint, updated_at_ms)
            VALUES ($assetId, $capability, $state, $jobId, $fingerprint, $now)
            ON CONFLICT (media_id, capability) DO UPDATE SET
                state = CASE
                    WHEN excluded.state IN ('READY', 'NOT_APPLICABLE') THEN excluded.state
                    WHEN excluded.state = 'FAILED' AND media_capability_readiness.state NOT IN ('READY', 'NOT_APPLICABLE') THEN excluded.state
                    WHEN excluded.state = 'PROCESSING' AND media_capability_readiness.state = 'QUEUED' THEN excluded.state
                    ELSE media_capability_readiness.state
                END,
                job_id = COALESCE(excluded.job_id, media_capability_readiness.job_id),
                source_fingerprint = COALESCE(excluded.source_fingerprint, media_capability_readiness.source_fingerprint),
                updated_at_ms = excluded.updated_at_ms
            WHERE media_capability_readiness.state NOT IN ('READY', 'NOT_APPLICABLE')
               OR excluded.state IN ('READY', 'NOT_APPLICABLE');
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        command.Parameters.AddWithValue("$capability", DbEnum.Format(capability));
        command.Parameters.AddWithValue("$state", DbEnum.Format(state));
        command.Parameters.AddWithValue("$jobId", jobId.HasValue ? DbGuid.Format(jobId.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$fingerprint", sourceFingerprint ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicit user recovery transition for one failed capability. Normal upsert semantics keep
    /// FAILED sticky; only this path may point the capability at a successor job and queue it again.
    /// The expected predecessor job prevents stale recovery from replacing newer authority.
    /// </summary>
    public async Task<bool> ResetFailedCapabilityForRetryAsync(
        Guid assetId,
        MediaCapability capability,
        Guid? expectedJobId,
        Guid successorJobId,
        string? sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty) throw new ArgumentException("A stable identifier cannot be empty.", nameof(assetId));
        if (successorJobId == Guid.Empty) throw new ArgumentException("A stable identifier cannot be empty.", nameof(successorJobId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE media_capability_readiness
            SET state = 'QUEUED',
                job_id = $successorJobId,
                source_fingerprint = COALESCE($fingerprint, source_fingerprint),
                updated_at_ms = $now
            WHERE media_id = $assetId
              AND capability = $capability
              AND state = 'FAILED'
              AND (
                    (job_id IS NULL AND $expectedJobId IS NULL)
                    OR job_id = $expectedJobId
                  );
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        command.Parameters.AddWithValue("$capability", DbEnum.Format(capability));
        command.Parameters.AddWithValue("$expectedJobId", expectedJobId.HasValue ? DbGuid.Format(expectedJobId.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$successorJobId", DbGuid.Format(successorJobId));
        command.Parameters.AddWithValue("$fingerprint", sourceFingerprint ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Seed all applicable capabilities for an asset in QUEUED state.
    /// Idempotent: existing rows with terminal states are not disturbed.
    /// </summary>
    public async Task SeedCapabilitiesAsync(
        Guid assetId,
        IReadOnlyList<MediaCapability> requiredCapabilities,
        CancellationToken cancellationToken = default)
    {
        foreach (var cap in requiredCapabilities)
        {
            await UpsertCapabilityAsync(assetId, cap, MediaCapabilityState.Queued, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Transition a capability to NOT_APPLICABLE. Used for FaceEmbedding when no faces are detected.
    /// </summary>
    public async Task MarkNotApplicableAsync(
        Guid assetId,
        MediaCapability capability,
        CancellationToken cancellationToken = default)
    {
        await UpsertCapabilityAsync(assetId, capability, MediaCapabilityState.NotApplicable, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Delete all capability rows for media in a unit. Used for restart/resume cleanup only.
    /// </summary>
    public async Task DeleteUnitCapabilitiesAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM media_capability_readiness
            WHERE media_id IN (
                SELECT ii.candidate_media_id
                FROM import_items ii
                WHERE ii.import_unit_id = $unitId
                  AND ii.candidate_media_id IS NOT NULL
            );
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
