using Neuterradise.App.Import;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;

namespace Neuterradise.App.SystemServices.Database.Writes;

public sealed class MediaWrites
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public MediaWrites(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> GetStorageTokenAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT media_storage_token FROM media WHERE media_id = $assetId;";
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        var token = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return !string.IsNullOrWhiteSpace(token)
            ? token
            : throw new CatalogInvariantException($"Media {assetId:D} has no admitted storage token.");
    }

    public async Task<(Guid ImportUnitId, string StorageToken, string CheckpointJson)?>
        ReadCompletedTransferAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.import_unit_id, a.media_storage_token, j.checkpoint_json
            FROM import_items i
            JOIN media a ON a.media_id = i.candidate_media_id
            JOIN jobs j ON j.owner_type = 'Media' AND j.owner_id = a.media_id
                       AND j.kind = 'TransferOriginal' AND j.state = 'SUCCEEDED'
            WHERE a.media_id = $assetId;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (DbGuid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task ActivateCandidateAsync(
        CandidateActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.MediaId, nameof(request.MediaId));
        EnsureNonEmpty(request.OwnerProfileId, nameof(request.OwnerProfileId));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CurrentManagedRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CurrentManagedFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetManagedRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetManagedFileName);
        ValidateFingerprint(request.Sha256);
        if (request.ByteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ByteLength));
        }

        ValidatePathState(request);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var asset = await ReadMediaAsync(transaction, request.MediaId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CatalogInvariantException($"Media {request.MediaId:D} does not exist.");

        if (asset.State == MediaState.Active)
        {
            if (!await ActivationMatchesAsync(transaction, request, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new CatalogInvariantException(
                    $"Media {request.MediaId:D} is already ACTIVE with different authority.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (asset.State != MediaState.Candidate)
        {
            throw new CatalogInvariantException(
                $"Only a CANDIDATE Media can be activated; {request.MediaId:D} is {asset.State}.");
        }

        if (asset.StorageToken is null)
        {
            throw new CatalogInvariantException(
                "Candidate activation requires an already-persisted Media storage token.");
        }

        if (!await ActiveProfileExistsAsync(transaction, request.OwnerProfileId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new CatalogInvariantException(
                $"Owner Profile {request.OwnerProfileId:D} does not exist or is trashed.");
        }

        if (await CountOwnersAsync(transaction, request.MediaId, cancellationToken)
                .ConfigureAwait(false) != 0)
        {
            throw new CatalogInvariantException("A CANDIDATE Media cannot already have an OWNER.");
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using (var activate = transaction.CreateCommand(
            """
            UPDATE media
            SET state = 'ACTIVE',
                sha256 = $sha256,
                byte_length = $byteLength,
                current_managed_relative_path = $currentPath,
                current_managed_file_name = $currentName,
                target_managed_relative_path = $targetPath,
                target_managed_file_name = $targetName,
                path_state = $pathState,
                reconciliation_operation_id = $operationId,
                added_to_library_at_ms = $now,
                row_version = row_version + 1
            WHERE media_id = $assetId AND state = 'CANDIDATE' AND row_version = $expectedRowVersion;
            """))
        {
            activate.Parameters.AddWithValue("$sha256", request.Sha256);
            activate.Parameters.AddWithValue("$byteLength", request.ByteLength);
            activate.Parameters.AddWithValue("$currentPath", request.CurrentManagedRelativePath);
            activate.Parameters.AddWithValue("$currentName", request.CurrentManagedFileName);
            activate.Parameters.AddWithValue("$targetPath", request.TargetManagedRelativePath);
            activate.Parameters.AddWithValue("$targetName", request.TargetManagedFileName);
            activate.Parameters.AddWithValue("$pathState", DbEnum.Format(request.PathState));
            activate.Parameters.AddWithValue(
                "$operationId",
                request.ReconciliationOperationId is Guid operationId
                    ? DbGuid.Format(operationId)
                    : DBNull.Value);
            activate.Parameters.AddWithValue("$now", now);
            activate.Parameters.AddWithValue("$assetId", DbGuid.Format(request.MediaId));
            activate.Parameters.AddWithValue("$expectedRowVersion", asset.RowVersion);
            if (await activate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new CatalogInvariantException("Candidate activation lost its expected state.");
            }
        }

        await using (var owner = transaction.CreateCommand(
            """
            INSERT INTO profile_media(profile_id, media_id, relation_type, created_at_ms)
            VALUES ($profileId, $assetId, 'OWNER', $now);
            """))
        {
            owner.Parameters.AddWithValue("$profileId", DbGuid.Format(request.OwnerProfileId));
            owner.Parameters.AddWithValue("$assetId", DbGuid.Format(request.MediaId));
            owner.Parameters.AddWithValue("$now", now);
            await owner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await CountOwnersAsync(transaction, request.MediaId, cancellationToken)
                .ConfigureAwait(false) != 1)
        {
            throw new CatalogInvariantException(
                "An ACTIVE Media must commit with exactly one OWNER.");
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.MediaId],
            CatalogInvalidationDomain.Media,
            asset.RowVersion + 1));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.OwnerProfileId],
            CatalogInvalidationDomain.Profile,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PersistCandidatePlacementPlanAsync(
        Guid assetId,
        string sourcePath,
        string sha256,
        long byteLength,
        string targetManagedRelativePath,
        string targetManagedFileName,
        Guid commitOperationId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));
        EnsureNonEmpty(commitOperationId, nameof(commitOperationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetManagedRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetManagedFileName);
        ValidateFingerprint(sha256);
        if (byteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var asset = await ReadMediaAsync(transaction, assetId, cancellationToken).ConfigureAwait(false)
            ?? throw new CatalogInvariantException($"Media {assetId:D} does not exist.");

        if (asset.State == MediaState.Active)
        {

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (asset.State != MediaState.Candidate)
        {
            throw new CatalogInvariantException(
                $"Only a CANDIDATE Media can receive a placement plan; {assetId:D} is {asset.State}.");
        }

        await using (var read = transaction.CreateCommand(
            """
            SELECT target_managed_relative_path, target_managed_file_name, path_state,
                   reconciliation_operation_id, current_managed_relative_path,
                   current_managed_file_name, sha256, byte_length
            FROM media WHERE media_id = $id;
            """))
        {
            read.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            var curTargetPath = reader.IsDBNull(0) ? null : reader.GetString(0);
            var curTargetName = reader.IsDBNull(1) ? null : reader.GetString(1);
            var curPathState = DbEnum.ParseManagedPathState(reader.GetString(2));
            var curCurrentPath = reader.IsDBNull(4) ? null : reader.GetString(4);

            var alreadyConverged = string.Equals(curTargetPath, targetManagedRelativePath, StringComparison.Ordinal)
                && string.Equals(curTargetName, targetManagedFileName, StringComparison.Ordinal)
                && (curPathState == ManagedPathState.Pending
                    || (curPathState == ManagedPathState.None && curCurrentPath is not null));
            if (alreadyConverged)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await using (var update = transaction.CreateCommand(
            """
            UPDATE media
            SET original_source_path = $sourcePath,
                sha256 = $sha256,
                byte_length = $byteLength,
                target_managed_relative_path = $targetPath,
                target_managed_file_name = $targetName,
                current_managed_relative_path = NULL,
                current_managed_file_name = NULL,
                path_state = 'PENDING',
                reconciliation_operation_id = $op,
                row_version = row_version + 1
            WHERE media_id = $id AND state = 'CANDIDATE';
            """))
        {
            update.Parameters.AddWithValue("$sourcePath", sourcePath);
            update.Parameters.AddWithValue("$sha256", sha256.Trim());
            update.Parameters.AddWithValue("$byteLength", byteLength);
            update.Parameters.AddWithValue("$targetPath", targetManagedRelativePath);
            update.Parameters.AddWithValue("$targetName", targetManagedFileName);
            update.Parameters.AddWithValue("$op", DbGuid.Format(commitOperationId));
            update.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new CatalogInvariantException(
                    $"Candidate {assetId:D} placement plan lost its expected state.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersistedSameVolumeMovePlan?> ReadSameVolumeMovePlanAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT state, original_source_path, sha256, byte_length,
                   current_managed_relative_path, current_managed_file_name,
                   target_managed_relative_path, target_managed_file_name,
                   path_state, reconciliation_operation_id
            FROM media
            WHERE media_id = $assetId;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (DbEnum.ParseMediaState(reader.GetString(0)) != MediaState.Candidate)
        {
            throw new CatalogInvariantException(
                $"Same-volume admission requires CANDIDATE Media {assetId:D}.");
        }

        if (reader.IsDBNull(1)
            || reader.IsDBNull(2)
            || reader.IsDBNull(3)
            || reader.IsDBNull(6)
            || reader.IsDBNull(7))
        {
            throw new CatalogInvariantException(
                $"Candidate Media {assetId:D} has no complete persisted Move plan.");
        }

        var sha256 = reader.GetString(2);
        ValidateFingerprint(sha256);
        var byteLength = reader.GetInt64(3);
        if (byteLength < 0)
        {
            throw new CatalogInvariantException(
                $"Candidate Media {assetId:D} has an invalid persisted byte length.");
        }

        var pathState = DbEnum.ParseManagedPathState(reader.GetString(8));
        if (pathState == ManagedPathState.NeedsAttention)
        {
            throw new CatalogInvariantException(
                $"Candidate Media {assetId:D} is already marked NEEDS_ATTENTION.");
        }

        var currentPath = reader.IsDBNull(4) ? null : reader.GetString(4);
        var currentName = reader.IsDBNull(5) ? null : reader.GetString(5);
        var targetPath = reader.GetString(6);
        var targetName = reader.GetString(7);
        var operationId = reader.IsDBNull(9) ? (Guid?)null : DbGuid.Parse(reader.GetString(9));
        if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(targetName))
        {
            throw new CatalogInvariantException(
                $"Candidate Media {assetId:D} has an invalid persisted managed target.");
        }

        if (pathState == ManagedPathState.None
            && (operationId is not null
                || !string.Equals(currentPath, targetPath, StringComparison.Ordinal)
                || !string.Equals(currentName, targetName, StringComparison.Ordinal)))
        {
            throw new CatalogInvariantException(
                $"Candidate Media {assetId:D} has NONE path state without a converged placement.");
        }

        if (pathState == ManagedPathState.Pending
            && (operationId is null || currentPath is not null || currentName is not null))
        {
            throw new CatalogInvariantException(
                $"Candidate Media {assetId:D} has an invalid pending same-volume placement.");
        }

        return new PersistedSameVolumeMovePlan(
            assetId,
            reader.GetString(1),
            byteLength,
            sha256,
            currentPath,
            currentName,
            targetPath,
            targetName,
            pathState,
            operationId);
    }

    public async Task<ManagedTargetAuthority> ResolveTargetAuthorityAsync(
        PersistedSameVolumeMovePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return await ResolveTargetAuthorityAsync(
                plan.MediaId,
                plan.TargetManagedRelativePath,
                plan.TargetManagedFileName,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ManagedTargetAuthority> ResolveTargetAuthorityAsync(
        PersistedCrossVolumeMovePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return await ResolveTargetAuthorityAsync(
                plan.MediaId,
                plan.TargetManagedRelativePath,
                plan.TargetManagedFileName,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ManagedTargetAuthority> ResolveTargetAuthorityAsync(
        Guid assetId,
        string targetManagedRelativePath,
        string targetManagedFileName,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));
        ArgumentException.ThrowIfNullOrWhiteSpace(targetManagedRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetManagedFileName);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM media
                WHERE media_id <> $assetId
                  AND (
                      (current_managed_relative_path = $targetPath
                       AND current_managed_file_name = $targetName)
                      OR
                      (target_managed_relative_path = $targetPath
                       AND target_managed_file_name = $targetName)
                  ));
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        command.Parameters.AddWithValue("$targetPath", targetManagedRelativePath);
        command.Parameters.AddWithValue("$targetName", targetManagedFileName);
        var otherExists = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
        return otherExists ? ManagedTargetAuthority.OtherMedia : ManagedTargetAuthority.SameMedia;
    }

    public async Task CheckpointSameVolumePlacementAsync(
        PersistedSameVolumeMovePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using var read = transaction.CreateCommand(
            """
            SELECT state, sha256, byte_length,
                   current_managed_relative_path, current_managed_file_name,
                   target_managed_relative_path, target_managed_file_name,
                   path_state, reconciliation_operation_id
            FROM media
            WHERE media_id = $assetId;
            """);
        read.Parameters.AddWithValue("$assetId", DbGuid.Format(plan.MediaId));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new CatalogInvariantException($"Media {plan.MediaId:D} no longer exists.");
        }

        var state = reader.GetString(0);
        var sha256 = reader.IsDBNull(1) ? null : reader.GetString(1);
        var byteLength = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
        var currentPath = reader.IsDBNull(3) ? null : reader.GetString(3);
        var currentName = reader.IsDBNull(4) ? null : reader.GetString(4);
        var targetPath = reader.IsDBNull(5) ? null : reader.GetString(5);
        var targetName = reader.IsDBNull(6) ? null : reader.GetString(6);
        var pathState = DbEnum.ParseManagedPathState(reader.GetString(7));
        var operationId = reader.IsDBNull(8) ? (Guid?)null : DbGuid.Parse(reader.GetString(8));
        await reader.DisposeAsync().ConfigureAwait(false);

        if (DbEnum.ParseMediaState(state) != MediaState.Candidate
            || !string.Equals(sha256, plan.ExpectedSha256, StringComparison.Ordinal)
            || byteLength != plan.ExpectedByteLength
            || !string.Equals(targetPath, plan.TargetManagedRelativePath, StringComparison.Ordinal)
            || !string.Equals(targetName, plan.TargetManagedFileName, StringComparison.Ordinal))
        {
            throw new CatalogInvariantException(
                $"Candidate Media {plan.MediaId:D} Move authority changed before checkpoint.");
        }

        var alreadyCheckpointed = pathState == ManagedPathState.None
            && operationId is null
            && string.Equals(currentPath, targetPath, StringComparison.Ordinal)
            && string.Equals(currentName, targetName, StringComparison.Ordinal);
        if (alreadyCheckpointed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (pathState != plan.PathState
            || operationId != plan.ReconciliationOperationId
            || currentPath is not null
            || currentName is not null)
        {
            throw new CatalogInvariantException(
                $"Candidate Media {plan.MediaId:D} current placement cannot be advanced from its persisted state.");
        }

        await using var update = transaction.CreateCommand(
            """
            UPDATE media
            SET current_managed_relative_path = target_managed_relative_path,
                current_managed_file_name = target_managed_file_name,
                path_state = 'NONE',
                reconciliation_operation_id = NULL,
                row_version = row_version + 1
            WHERE media_id = $assetId
              AND state = 'CANDIDATE'
              AND sha256 = $sha256
              AND byte_length = $byteLength
              AND target_managed_relative_path = $targetPath
              AND target_managed_file_name = $targetName;
            """);
        update.Parameters.AddWithValue("$assetId", DbGuid.Format(plan.MediaId));
        update.Parameters.AddWithValue("$sha256", plan.ExpectedSha256);
        update.Parameters.AddWithValue("$byteLength", plan.ExpectedByteLength);
        update.Parameters.AddWithValue("$targetPath", plan.TargetManagedRelativePath);
        update.Parameters.AddWithValue("$targetName", plan.TargetManagedFileName);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CatalogInvariantException(
                $"Candidate Media {plan.MediaId:D} lost its persisted Move authority.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersistedCrossVolumeMovePlan?> ReadCrossVolumeMovePlanAsync(
        Guid importItemId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(importItemId, nameof(importItemId));

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.import_unit_id, i.candidate_media_id, i.source_path,
                   i.source_byte_length, i.disposition, i.duplicate_decision,
                   i.source_cleanup_state,
                   a.state, a.original_source_path, a.sha256, a.byte_length,
                   a.current_managed_relative_path, a.current_managed_file_name,
                   a.target_managed_relative_path, a.target_managed_file_name,
                   a.path_state, a.reconciliation_operation_id
            FROM import_items i
            LEFT JOIN media a ON a.media_id = i.candidate_media_id
            WHERE i.import_item_id = $itemId;
            """;
        command.Parameters.AddWithValue("$itemId", DbGuid.Format(importItemId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (DbEnum.ParseItemDisposition(reader.GetString(4)) != ItemDisposition.Included
            || !reader.IsDBNull(5))
        {
            throw new CatalogInvariantException(
                $"Cross-volume placement requires an INCLUDED non-duplicate ImportItem {importItemId:D}.");
        }

        if (reader.IsDBNull(1) || reader.IsDBNull(7))
        {
            throw new CatalogInvariantException(
                $"ImportItem {importItemId:D} has no Candidate Media placement authority.");
        }

        var assetId = DbGuid.Parse(reader.GetString(1));
        if (DbEnum.ParseMediaState(reader.GetString(7)) != MediaState.Candidate)
        {
            throw new CatalogInvariantException(
                $"Cross-volume placement requires CANDIDATE Media {assetId:D}.");
        }

        if (reader.IsDBNull(8)
            || reader.IsDBNull(9)
            || reader.IsDBNull(10)
            || reader.IsDBNull(13)
            || reader.IsDBNull(14))
        {
            throw new CatalogInvariantException(
                $"Candidate Media {assetId:D} has no complete persisted cross-volume Move plan.");
        }

        var sourcePath = reader.GetString(2);
        var originalSourcePath = reader.GetString(8);
        if (!string.Equals(sourcePath, originalSourcePath, StringComparison.Ordinal))
        {
            throw new CatalogInvariantException(
                $"ImportItem {importItemId:D} source no longer matches Candidate provenance.");
        }

        var expectedSha256 = reader.GetString(9);
        ValidateFingerprint(expectedSha256);
        var expectedByteLength = reader.GetInt64(10);
        if (expectedByteLength < 0
            || (reader.IsDBNull(3) ? null : reader.GetInt64(3)) != expectedByteLength)
        {
            throw new CatalogInvariantException(
                $"ImportItem {importItemId:D} byte length no longer matches Candidate authority.");
        }

        var currentPath = reader.IsDBNull(11) ? null : reader.GetString(11);
        var currentName = reader.IsDBNull(12) ? null : reader.GetString(12);
        var targetPath = reader.GetString(13);
        var targetName = reader.GetString(14);
        var pathState = DbEnum.ParseManagedPathState(reader.GetString(15));
        var operationId = reader.IsDBNull(16) ? (Guid?)null : DbGuid.Parse(reader.GetString(16));
        var cleanupState = DbEnum.ParseSourceCleanupState(reader.GetString(6));

        var pending = pathState == ManagedPathState.Pending
            && currentPath is null
            && currentName is null
            && operationId is not null
            && cleanupState == SourceCleanupState.SourcePresent;
        var checkpointed = pathState == ManagedPathState.None
            && string.Equals(currentPath, targetPath, StringComparison.Ordinal)
            && string.Equals(currentName, targetName, StringComparison.Ordinal)
            && operationId is null
            && cleanupState == SourceCleanupState.DestinationVerified;
        if (!pending && !checkpointed)
        {
            throw new CatalogInvariantException(
                $"ImportItem {importItemId:D} is not in a legal cross-volume placement state.");
        }

        return new PersistedCrossVolumeMovePlan(
            importItemId,
            DbGuid.Parse(reader.GetString(0)),
            assetId,
            sourcePath,
            expectedByteLength,
            expectedSha256,
            currentPath,
            currentName,
            targetPath,
            targetName,
            pathState,
            operationId,
            cleanupState);
    }

    public async Task<PersistedOwnerRelocationPlan?> ReadOwnerRelocationPlanAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT state, media_storage_token, sha256, byte_length,
                   current_managed_relative_path, current_managed_file_name,
                   target_managed_relative_path, target_managed_file_name,
                   path_state, reconciliation_operation_id, trashed_at_ms
            FROM media
            WHERE media_id = $assetId;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PersistedOwnerRelocationPlan(
            assetId,
            DbEnum.ParseMediaState(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            DbEnum.ParseManagedPathState(reader.GetString(8)),
            reader.IsDBNull(9) ? (Guid?)null : DbGuid.Parse(reader.GetString(9)),
            IsTrashed: !reader.IsDBNull(10));
    }

    public async Task<bool> CheckpointReconciledPlacementAsync(
        Guid assetId,
        Guid operationId,
        bool completeOperation,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));
        EnsureNonEmpty(operationId, nameof(operationId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        int advanced;
        await using (var update = transaction.CreateCommand(
            """
            UPDATE media
            SET current_managed_relative_path = target_managed_relative_path,
                current_managed_file_name = target_managed_file_name,
                path_state = 'NONE',
                reconciliation_operation_id = NULL,
                row_version = row_version + 1
            WHERE media_id = $assetId
              AND reconciliation_operation_id = $operationId
              AND path_state = 'PENDING'
              AND target_managed_relative_path IS NOT NULL
              AND target_managed_file_name IS NOT NULL;
            """))
        {
            update.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
            update.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
            advanced = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (advanced != 1)
        {

            var converged = await IsConvergedAsync(transaction, assetId, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return converged;
        }

        if (completeOperation)
        {
            await using var complete = transaction.CreateCommand(
                """
                UPDATE storage_operations
                SET state = 'COMPLETED',
                    completed_at_ms = $now,
                    updated_at_ms = $now,
                    error_code = NULL,
                    error_detail_safe = NULL,
                    row_version = row_version + 1
                WHERE operation_id = $operationId;
                """);
            var now = DbTime.Format(_timeProvider.GetUtcNow());
            complete.Parameters.AddWithValue("$now", now);
            complete.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
            await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task MarkPlacementNeedsAttentionAsync(
        Guid assetId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));
        EnsureNonEmpty(operationId, nameof(operationId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using (var update = transaction.CreateCommand(
            """
            UPDATE media
            SET path_state = 'NEEDS_ATTENTION',
                row_version = row_version + 1
            WHERE media_id = $assetId
              AND reconciliation_operation_id = $operationId
              AND path_state = 'PENDING';
            """))
        {
            update.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
            update.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsConvergedAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var read = transaction.CreateCommand(
            """
            SELECT path_state, reconciliation_operation_id,
                   current_managed_relative_path, current_managed_file_name,
                   target_managed_relative_path, target_managed_file_name
            FROM media
            WHERE media_id = $assetId;
            """);
        read.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return DbEnum.ParseManagedPathState(reader.GetString(0)) == ManagedPathState.None
            && reader.IsDBNull(1)
            && !reader.IsDBNull(2)
            && !reader.IsDBNull(3)
            && string.Equals(reader.GetString(2), reader.IsDBNull(4) ? null : reader.GetString(4), StringComparison.Ordinal)
            && string.Equals(reader.GetString(3), reader.IsDBNull(5) ? null : reader.GetString(5), StringComparison.Ordinal);
    }

    public async Task CheckpointCrossVolumePlacementAsync(
        PersistedCrossVolumeMovePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using var read = transaction.CreateCommand(
            """
            SELECT a.state, a.sha256, a.byte_length,
                   a.current_managed_relative_path, a.current_managed_file_name,
                   a.target_managed_relative_path, a.target_managed_file_name,
                   a.path_state, a.reconciliation_operation_id,
                   i.import_unit_id, i.candidate_media_id, i.source_path,
                   i.disposition, i.duplicate_decision, i.source_cleanup_state
            FROM import_items i
            JOIN media a ON a.media_id = i.candidate_media_id
            WHERE i.import_item_id = $itemId;
            """);
        read.Parameters.AddWithValue("$itemId", DbGuid.Format(plan.ImportItemId));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new CatalogInvariantException(
                $"ImportItem {plan.ImportItemId:D} no longer has Candidate placement authority.");
        }

        var currentPath = reader.IsDBNull(3) ? null : reader.GetString(3);
        var currentName = reader.IsDBNull(4) ? null : reader.GetString(4);
        var targetPath = reader.IsDBNull(5) ? null : reader.GetString(5);
        var targetName = reader.IsDBNull(6) ? null : reader.GetString(6);
        var pathState = DbEnum.ParseManagedPathState(reader.GetString(7));
        var operationId = reader.IsDBNull(8) ? (Guid?)null : DbGuid.Parse(reader.GetString(8));
        var cleanupState = DbEnum.ParseSourceCleanupState(reader.GetString(14));

        var unchangedAuthority = DbEnum.ParseMediaState(reader.GetString(0)) == MediaState.Candidate
            && string.Equals(reader.GetString(1), plan.ExpectedSha256, StringComparison.Ordinal)
            && reader.GetInt64(2) == plan.ExpectedByteLength
            && string.Equals(targetPath, plan.TargetManagedRelativePath, StringComparison.Ordinal)
            && string.Equals(targetName, plan.TargetManagedFileName, StringComparison.Ordinal)
            && DbGuid.Parse(reader.GetString(9)) == plan.ImportUnitId
            && DbGuid.Parse(reader.GetString(10)) == plan.MediaId
            && string.Equals(reader.GetString(11), plan.SourcePath, StringComparison.Ordinal)
            && DbEnum.ParseItemDisposition(reader.GetString(12)) == ItemDisposition.Included
            && reader.IsDBNull(13);
        await reader.DisposeAsync().ConfigureAwait(false);
        if (!unchangedAuthority)
        {
            throw new CatalogInvariantException(
                $"ImportItem {plan.ImportItemId:D} authority changed before destination checkpoint.");
        }

        var alreadyCheckpointed = pathState == ManagedPathState.None
            && operationId is null
            && string.Equals(currentPath, targetPath, StringComparison.Ordinal)
            && string.Equals(currentName, targetName, StringComparison.Ordinal)
            && cleanupState == SourceCleanupState.DestinationVerified;
        if (alreadyCheckpointed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (pathState != plan.PathState
            || operationId != plan.ReconciliationOperationId
            || currentPath is not null
            || currentName is not null
            || cleanupState != SourceCleanupState.SourcePresent)
        {
            throw new CatalogInvariantException(
                $"ImportItem {plan.ImportItemId:D} cannot advance from its persisted placement state.");
        }

        await using (var updateMedia = transaction.CreateCommand(
            """
            UPDATE media
            SET current_managed_relative_path = target_managed_relative_path,
                current_managed_file_name = target_managed_file_name,
                path_state = 'NONE',
                reconciliation_operation_id = NULL,
                row_version = row_version + 1
            WHERE media_id = $assetId
              AND state = 'CANDIDATE'
              AND sha256 = $sha256
              AND byte_length = $byteLength
              AND target_managed_relative_path = $targetPath
              AND target_managed_file_name = $targetName;
            """))
        {
            updateMedia.Parameters.AddWithValue("$assetId", DbGuid.Format(plan.MediaId));
            updateMedia.Parameters.AddWithValue("$sha256", plan.ExpectedSha256);
            updateMedia.Parameters.AddWithValue("$byteLength", plan.ExpectedByteLength);
            updateMedia.Parameters.AddWithValue("$targetPath", plan.TargetManagedRelativePath);
            updateMedia.Parameters.AddWithValue("$targetName", plan.TargetManagedFileName);
            if (await updateMedia.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new CatalogInvariantException(
                    $"Candidate Media {plan.MediaId:D} lost its cross-volume placement authority.");
            }
        }

        await using (var updateItem = transaction.CreateCommand(
            """
            UPDATE import_items
            SET source_cleanup_state = 'DESTINATION_VERIFIED',
                source_cleanup_error = NULL,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_item_id = $itemId
              AND candidate_media_id = $assetId
              AND source_cleanup_state = 'SOURCE_PRESENT';
            """))
        {
            updateItem.Parameters.AddWithValue("$itemId", DbGuid.Format(plan.ImportItemId));
            updateItem.Parameters.AddWithValue("$assetId", DbGuid.Format(plan.MediaId));
            updateItem.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
            if (await updateItem.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new CatalogInvariantException(
                    $"ImportItem {plan.ImportItemId:D} lost its source-cleanup checkpoint authority.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MediaRow?> ReadMediaAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            "SELECT state, media_storage_token, row_version FROM media WHERE media_id = $assetId;");
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new MediaRow(DbEnum.ParseMediaState(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2))
            : null;
    }

    private static async Task<bool> ActiveProfileExistsAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT EXISTS(
                SELECT 1 FROM profiles
                WHERE profile_id = $profileId AND trashed_at_ms IS NULL);
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<long> CountOwnersAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT COUNT(*) FROM profile_media
            WHERE media_id = $assetId AND relation_type = 'OWNER';
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> ActivationMatchesAsync(
        CatalogTransaction transaction,
        CandidateActivationRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT a.sha256, a.byte_length,
                   a.current_managed_relative_path, a.current_managed_file_name,
                   a.target_managed_relative_path, a.target_managed_file_name,
                   a.path_state, a.reconciliation_operation_id,
                   pa.profile_id
            FROM media a
            LEFT JOIN profile_media pa
              ON pa.media_id = a.media_id AND pa.relation_type = 'OWNER'
            WHERE a.media_id = $assetId;
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(request.MediaId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var operationId = reader.IsDBNull(7) ? (Guid?)null : DbGuid.Parse(reader.GetString(7));
        var ownerId = reader.IsDBNull(8) ? (Guid?)null : DbGuid.Parse(reader.GetString(8));
        return string.Equals(reader.GetString(0), request.Sha256, StringComparison.Ordinal)
            && reader.GetInt64(1) == request.ByteLength
            && string.Equals(reader.GetString(2), request.CurrentManagedRelativePath, StringComparison.Ordinal)
            && string.Equals(reader.GetString(3), request.CurrentManagedFileName, StringComparison.Ordinal)
            && string.Equals(reader.GetString(4), request.TargetManagedRelativePath, StringComparison.Ordinal)
            && string.Equals(reader.GetString(5), request.TargetManagedFileName, StringComparison.Ordinal)
            && string.Equals(reader.GetString(6), DbEnum.Format(request.PathState), StringComparison.Ordinal)
            && operationId == request.ReconciliationOperationId
            && ownerId == request.OwnerProfileId;
    }

    private static void ValidatePathState(CandidateActivationRequest request)
    {
        if (request.PathState == ManagedPathState.None)
        {
            if (request.ReconciliationOperationId is not null
                || !string.Equals(
                    request.CurrentManagedRelativePath,
                    request.TargetManagedRelativePath,
                    StringComparison.Ordinal)
                || !string.Equals(
                    request.CurrentManagedFileName,
                    request.TargetManagedFileName,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "NONE path state requires converged current/target placement and no operation id.");
            }

            return;
        }

        if (request.ReconciliationOperationId is null)
        {
            throw new ArgumentException(
                "A nonterminal path state requires a reconciliation operation id.");
        }
    }

    private static void ValidateFingerprint(string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64
            || sha256.Any(character => !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The authoritative SHA-256 must be 64 lowercase hexadecimal characters.",
                nameof(sha256));
        }
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }

    /// <summary>
    /// Sets or clears the Media Favorite flag (the product contract, presented to the user as the fire mark).
    /// Returns the value actually stored, so a caller that raced another writer converges instead of
    /// leaving the UI showing an optimistic value the catalog never accepted.
    ///
    /// Only an active asset can be favorited: a trashed or retired asset must not accumulate new
    /// user intent that restore would then silently resurrect.
    /// </summary>
    public async Task<bool> SetFavoriteAsync(
        Guid assetId,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        await using (var update = transaction.CreateCommand(
            """
            UPDATE media
            SET is_favorite = $isFavorite,
                row_version = row_version + 1
            WHERE media_id = $assetId
              AND state = 'ACTIVE'
              AND is_favorite <> $isFavorite;
            """))
        {
            update.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
            update.Parameters.AddWithValue("$isFavorite", isFavorite ? 1 : 0);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        bool stored;
        await using (var read = transaction.CreateCommand(
            "SELECT is_favorite FROM media WHERE media_id = $assetId;"))
        {
            read.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
            var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (value is null || value == DBNull.Value)
            {
                throw new CatalogInvariantException($"Media {assetId:D} does not exist.");
            }

            stored = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0;
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [assetId],
            CatalogInvalidationDomain.Media,
            0));

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async Task SaveMetadataAsync(
        Guid assetId,
        int? width,
        int? height,
        int? durationMs,
        DateTimeOffset? capturedAt,
        string metadataJson,
        int schemaVersion = 1,
        CancellationToken cancellationToken = default,
        bool requirePublicActive = false)
    {
        EnsureNonEmpty(assetId, nameof(assetId));
        ArgumentNullException.ThrowIfNull(metadataJson);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var nowMs = DbTime.Format(_timeProvider.GetUtcNow());
        var capturedAtMs = capturedAt.HasValue ? (long?)DbTime.Format(capturedAt.Value) : null;

        await using var command = transaction.CreateCommand(
            """
            INSERT INTO media_metadata (
                media_id,
                metadata_schema_version,
                captured_at_ms,
                width,
                height,
                duration_ms,
                metadata_json,
                updated_at_ms
            )
            SELECT
                $assetId,
                $schemaVersion,
                $capturedAtMs,
                $width,
                $height,
                $durationMs,
                $metadataJson,
                $updatedAtMs
            WHERE $requirePublic = 0 OR EXISTS (
                SELECT 1 FROM media m
                WHERE m.media_id = $assetId AND m.state = 'ACTIVE' AND m.trashed_at_ms IS NULL
                  AND EXISTS (SELECT 1 FROM profile_media pm JOIN profiles p ON p.profile_id = pm.profile_id
                              WHERE pm.media_id = m.media_id AND pm.publication_import_unit_id IS NULL
                                AND p.trashed_at_ms IS NULL AND p.visibility = 'PUBLISHED')
                  AND NOT EXISTS (SELECT 1 FROM trash_entries te WHERE te.entity_type = 'MEDIA'
                                  AND te.entity_id = m.media_id AND te.state IN ('PENDING','EXECUTING'))
            )
            ON CONFLICT(media_id) DO UPDATE SET
                metadata_schema_version = excluded.metadata_schema_version,
                captured_at_ms = excluded.captured_at_ms,
                width = excluded.width,
                height = excluded.height,
                duration_ms = excluded.duration_ms,
                metadata_json = excluded.metadata_json,
                updated_at_ms = excluded.updated_at_ms;
            """);

        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        command.Parameters.AddWithValue("$schemaVersion", schemaVersion);
        command.Parameters.AddWithValue("$capturedAtMs", capturedAtMs.HasValue ? capturedAtMs.Value : DBNull.Value);
        command.Parameters.AddWithValue("$width", width.HasValue && width.Value > 0 ? width.Value : DBNull.Value);
        command.Parameters.AddWithValue("$height", height.HasValue && height.Value > 0 ? height.Value : DBNull.Value);
        command.Parameters.AddWithValue("$durationMs", durationMs.HasValue && durationMs.Value >= 0 ? durationMs.Value : DBNull.Value);
        command.Parameters.AddWithValue("$metadataJson", metadataJson);
        command.Parameters.AddWithValue("$updatedAtMs", nowMs);
        command.Parameters.AddWithValue("$requirePublic", requirePublicActive ? 1 : 0);

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            throw new CatalogInvariantException("Metadata refresh owner is no longer public active media.");
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [assetId],
            CatalogInvalidationDomain.Media,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SaveMetadataAsync(
        Guid assetId,
        Media.Model.ModelMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return SaveMetadataAsync(
            assetId,
            width: null,
            height: null,
            durationMs: null,
            capturedAt: null,
            metadataJson: metadata.ToJson(),
            schemaVersion: 1,
            cancellationToken: cancellationToken);
    }

    public async Task UpdateMediaPackageIdentityAsync(
        Guid assetId,
        MediaDependencyStatus dependencyStatus,
        DependencyDiscoveryState discoveryState,
        string? bundleSha256,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using (var update = transaction.CreateCommand(
            """
            UPDATE media
            SET dependency_status = $status,
                dependency_discovery_state = $discoveryState,
                bundle_sha256 = $bundleSha256,
                row_version = row_version + 1
            WHERE media_id = $id;
            """))
        {
            update.Parameters.AddWithValue("$status", DbEnum.Format(dependencyStatus));
            update.Parameters.AddWithValue("$discoveryState", DbEnum.Format(discoveryState));
            update.Parameters.AddWithValue(
                "$bundleSha256",
                string.IsNullOrWhiteSpace(bundleSha256) ? DBNull.Value : bundleSha256.Trim().ToLowerInvariant());
            update.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveMediaComponentsAsync(
        Guid assetId,
        IReadOnlyList<MediaComponentRecord> components,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));
        ArgumentNullException.ThrowIfNull(components);

        if (components.Count == 0) return;

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        foreach (var c in components)
        {
            await using var cmd = transaction.CreateCommand(
                """
                INSERT INTO media_components(
                    media_id, component_relative_path, normalized_component_path,
                    component_role, sha256, byte_length,
                    original_source_path, source_identity_json,
                    source_cleanup_state, source_cleanup_error)
                VALUES (
                    $assetId, $relPath, $normPath,
                    $role, $sha256, $length,
                    $origPath, $identityJson,
                    $cleanupState, $cleanupError)
                ON CONFLICT(media_id, component_relative_path) DO UPDATE SET
                    normalized_component_path = excluded.normalized_component_path,
                    component_role = excluded.component_role,
                    sha256 = excluded.sha256,
                    byte_length = excluded.byte_length,
                    original_source_path = coalesce(excluded.original_source_path, media_components.original_source_path),
                    source_identity_json = coalesce(excluded.source_identity_json, media_components.source_identity_json),
                    row_version = media_components.row_version + 1;
                """);
            cmd.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
            cmd.Parameters.AddWithValue("$relPath", c.ComponentRelativePath);
            cmd.Parameters.AddWithValue("$normPath", c.NormalizedComponentPath);
            cmd.Parameters.AddWithValue("$role", DbEnum.Format(c.ComponentRole));
            cmd.Parameters.AddWithValue("$sha256", c.Sha256.ToLowerInvariant());
            cmd.Parameters.AddWithValue("$length", c.ByteLength);
            cmd.Parameters.AddWithValue("$origPath", (object?)c.OriginalSourcePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$identityJson", (object?)c.SourceIdentityJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cleanupState", DbEnum.Format(c.SourceCleanupState));
            cmd.Parameters.AddWithValue("$cleanupError", (object?)c.SourceCleanupError ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateComponentCleanupStateAsync(
        Guid assetId,
        string componentRelativePath,
        SourceCleanupState newState,
        string? errorDetail = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));
        ArgumentException.ThrowIfNullOrWhiteSpace(componentRelativePath);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using var update = transaction.CreateCommand(
            """
            UPDATE media_components
            SET source_cleanup_state = $state,
                source_cleanup_error = $error,
                row_version = row_version + 1
            WHERE media_id = $id AND component_relative_path = $relPath;
            """);
        update.Parameters.AddWithValue("$state", DbEnum.Format(newState));
        update.Parameters.AddWithValue("$error", (object?)errorDetail ?? DBNull.Value);
        update.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
        update.Parameters.AddWithValue("$relPath", componentRelativePath);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAllComponentsCleanupStateAsync(
        Guid assetId,
        SourceCleanupState newState,
        string? errorDetail = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using var update = transaction.CreateCommand(
            """
            UPDATE media_components
            SET source_cleanup_state = $state,
                source_cleanup_error = $error,
                row_version = row_version + 1
            WHERE media_id = $id;
            """);
        update.Parameters.AddWithValue("$state", DbEnum.Format(newState));
        update.Parameters.AddWithValue("$error", (object?)errorDetail ?? DBNull.Value);
        update.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MediaComponentRecord>> GetMediaComponentsAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT media_id, component_relative_path, normalized_component_path,
                   component_role, sha256, byte_length,
                   original_source_path, source_identity_json,
                   source_cleanup_state, source_cleanup_error, row_version
            FROM media_components
            WHERE media_id = $assetId
            ORDER BY component_role ASC, normalized_component_path ASC;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var list = new List<MediaComponentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new MediaComponentRecord(
                DbGuid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                DbEnum.ParseComponentRole(reader.GetString(3)),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                DbEnum.ParseSourceCleanupState(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt64(10)));
        }

        return list;
    }

    private sealed record MediaRow(MediaState State, string? StorageToken, long RowVersion);
}

public sealed record MediaComponentRecord(
    Guid MediaId,
    string ComponentRelativePath,
    string NormalizedComponentPath,
    ComponentRole ComponentRole,
    string Sha256,
    long ByteLength,
    string? OriginalSourcePath = null,
    string? SourceIdentityJson = null,
    SourceCleanupState SourceCleanupState = SourceCleanupState.SourcePresent,
    string? SourceCleanupError = null,
    long RowVersion = 0);

public sealed record CandidateActivationRequest(
    Guid MediaId,
    Guid OwnerProfileId,
    string Sha256,
    long ByteLength,
    string CurrentManagedRelativePath,
    string CurrentManagedFileName,
    string TargetManagedRelativePath,
    string TargetManagedFileName,
    ManagedPathState PathState,
    Guid? ReconciliationOperationId = null);

public sealed record PersistedSameVolumeMovePlan(
    Guid MediaId,
    string SourcePath,
    long ExpectedByteLength,
    string ExpectedSha256,
    string? CurrentManagedRelativePath,
    string? CurrentManagedFileName,
    string TargetManagedRelativePath,
    string TargetManagedFileName,
    ManagedPathState PathState,
    Guid? ReconciliationOperationId);

public sealed record PersistedCrossVolumeMovePlan(
    Guid ImportItemId,
    Guid ImportUnitId,
    Guid MediaId,
    string SourcePath,
    long ExpectedByteLength,
    string ExpectedSha256,
    string? CurrentManagedRelativePath,
    string? CurrentManagedFileName,
    string TargetManagedRelativePath,
    string TargetManagedFileName,
    ManagedPathState PathState,
    Guid? ReconciliationOperationId,
    SourceCleanupState SourceCleanupState);

public sealed record PersistedOwnerRelocationPlan(
    Guid MediaId,
    MediaState State,
    string? StorageToken,
    string? ExpectedSha256,
    long? ExpectedByteLength,
    string? CurrentManagedRelativePath,
    string? CurrentManagedFileName,
    string? TargetManagedRelativePath,
    string? TargetManagedFileName,
    ManagedPathState PathState,
    Guid? ReconciliationOperationId,
    bool IsTrashed);

public enum ManagedTargetAuthority
{
    SameMedia,
    OtherMedia,
}
