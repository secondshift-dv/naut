using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.SystemServices.Jobs;

/// <summary>
/// Canonical durable authority for path-reconciliation jobs. The domain mutation and its job row
/// commit in the same SQLite transaction; recovery recreates only a missing deterministic job row.
/// </summary>
public static class ReconciliationJobAuthority
{
    public const string ProfileRenameKind = "ProfileRenameReconciliation";
    public const string OwnerRelocationKind = "OwnerRelocation";
    public const int Priority = 50;
    public const int MaxAttempts = 5;

    public static Task EnsureProfileRenameJobAsync(
        CatalogTransaction transaction,
        Guid operationId,
        Guid profileId,
        long createdAtMs,
        CancellationToken cancellationToken = default) =>
        InsertJobAsync(
            transaction,
            operationId,
            ProfileRenameKind,
            "Profile",
            profileId,
            createdAtMs,
            cancellationToken);

    public static Task EnsureOwnerRelocationJobAsync(
        CatalogTransaction transaction,
        Guid operationId,
        Guid assetId,
        long createdAtMs,
        CancellationToken cancellationToken = default) =>
        InsertJobAsync(
            transaction,
            operationId,
            OwnerRelocationKind,
            "Media",
            assetId,
            createdAtMs,
            cancellationToken);

    public static async Task<int> RecoverMissingJobsAsync(
        CatalogDb catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        await using var lease = await catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await catalog.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        var createdAtMs = DbTime.Format(TimeProvider.System.GetUtcNow());

        var recovered = 0;
        await using (var profileJobs = transaction.CreateCommand(
            """
            INSERT INTO jobs(
                job_id, kind, lane, state, priority, owner_type, owner_id,
                attempt, max_attempts, not_before_ms, checkpoint_json,
                created_at_ms, completed_at_ms)
            SELECT
                p.reconciliation_operation_id,
                $kind,
                'IO',
                'PENDING',
                $priority,
                'Profile',
                p.profile_id,
                0,
                $maxAttempts,
                NULL,
                '{}',
                $createdAtMs,
                NULL
            FROM profiles p
            WHERE p.trashed_at_ms IS NULL
              AND p.path_state IN ('PENDING','NEEDS_ATTENTION')
              AND p.reconciliation_operation_id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM jobs j
                  WHERE j.job_id = p.reconciliation_operation_id
              );
            """))
        {
            profileJobs.Parameters.AddWithValue("$kind", ProfileRenameKind);
            profileJobs.Parameters.AddWithValue("$priority", Priority);
            profileJobs.Parameters.AddWithValue("$maxAttempts", MaxAttempts);
            profileJobs.Parameters.AddWithValue("$createdAtMs", createdAtMs);
            recovered += await profileJobs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var assetJobs = transaction.CreateCommand(
            """
            INSERT INTO jobs(
                job_id, kind, lane, state, priority, owner_type, owner_id,
                attempt, max_attempts, not_before_ms, checkpoint_json,
                created_at_ms, completed_at_ms)
            SELECT
                a.reconciliation_operation_id,
                $kind,
                'IO',
                'PENDING',
                $priority,
                'Media',
                a.media_id,
                0,
                $maxAttempts,
                NULL,
                '{}',
                $createdAtMs,
                NULL
            FROM media a
            WHERE a.state = 'ACTIVE'
              AND a.trashed_at_ms IS NULL
              AND a.path_state IN ('PENDING','NEEDS_ATTENTION')
              AND a.reconciliation_operation_id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM jobs j
                  WHERE j.job_id = a.reconciliation_operation_id
              );
            """))
        {
            assetJobs.Parameters.AddWithValue("$kind", OwnerRelocationKind);
            assetJobs.Parameters.AddWithValue("$priority", Priority);
            assetJobs.Parameters.AddWithValue("$maxAttempts", MaxAttempts);
            assetJobs.Parameters.AddWithValue("$createdAtMs", createdAtMs);
            recovered += await assetJobs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (recovered > 0)
        {
            JobSignals.Raise();
        }

        return recovered;
    }

    private static async Task InsertJobAsync(
        CatalogTransaction transaction,
        Guid operationId,
        string kind,
        string ownerType,
        Guid ownerId,
        long createdAtMs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (operationId == Guid.Empty)
            throw new ArgumentException("Reconciliation operation id cannot be empty.", nameof(operationId));
        if (ownerId == Guid.Empty)
            throw new ArgumentException("Reconciliation owner id cannot be empty.", nameof(ownerId));

        await using var command = transaction.CreateCommand(
            """
            INSERT INTO jobs(
                job_id, kind, lane, state, priority, owner_type, owner_id,
                attempt, max_attempts, not_before_ms, checkpoint_json,
                created_at_ms, completed_at_ms)
            VALUES(
                $jobId, $kind, 'IO', 'PENDING', $priority, $ownerType, $ownerId,
                0, $maxAttempts, NULL, '{}', $createdAtMs, NULL);
            """);
        command.Parameters.AddWithValue("$jobId", DbGuid.Format(operationId));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$priority", Priority);
        command.Parameters.AddWithValue("$ownerType", ownerType);
        command.Parameters.AddWithValue("$ownerId", DbGuid.Format(ownerId));
        command.Parameters.AddWithValue("$maxAttempts", MaxAttempts);
        command.Parameters.AddWithValue("$createdAtMs", createdAtMs);

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("A reconciliation obligation must create exactly one durable job.");
    }
}
