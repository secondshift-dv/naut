using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;

namespace Neuterradise.App.Import.Intake;

/// <summary>
/// Initial intake admission authority. An Import Unit and its first accepted Candidates become durable
/// in one catalog transaction, so intake cannot publish an empty unit when Candidate allocation fails.
/// </summary>
internal sealed class ImportAdmissionWrites
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public ImportAdmissionWrites(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<Guid>> AdmitUnitAsync(
        ImportUnitRegistration registration,
        IReadOnlyList<CandidateAllocationRequest> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            throw new ArgumentException(
                "Initial intake admission requires at least one accepted Candidate.",
                nameof(candidates));
        }

        ValidateRegistration(registration);
        foreach (var candidate in candidates)
        {
            ValidateCandidate(registration.ImportUnitId, candidate);
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await EnsureSessionAsync(transaction, registration, now, cancellationToken).ConfigureAwait(false);
        await EnsureUnitAsync(transaction, registration, now, cancellationToken).ConfigureAwait(false);

        var allocated = new List<Guid>(candidates.Count);
        foreach (var candidate in candidates)
        {
            allocated.Add(await EnsureCandidateAsync(
                transaction,
                registration,
                candidate,
                now,
                cancellationToken).ConfigureAwait(false));
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [registration.ImportUnitId],
            CatalogInvalidationDomain.Import,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return allocated;
    }

    private static async Task EnsureSessionAsync(
        CatalogTransaction transaction,
        ImportUnitRegistration registration,
        long now,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            INSERT OR IGNORE INTO import_sessions(
                import_session_id, state, created_at_ms, updated_at_ms)
            VALUES ($sessionId, $state, $now, $now);
            """);
        command.Parameters.AddWithValue("$sessionId", DbGuid.Format(registration.ImportSessionId));
        command.Parameters.AddWithValue("$state", DbEnum.Format(registration.SessionState));
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureUnitAsync(
        CatalogTransaction transaction,
        ImportUnitRegistration registration,
        long now,
        CancellationToken cancellationToken)
    {
        await using (var command = transaction.CreateCommand(
            """
            INSERT OR IGNORE INTO import_units(
                import_unit_id, import_session_id, parent_import_unit_id,
                source_kind, source_display_name, source_path_or_reference,
                state, created_at_ms, updated_at_ms)
            VALUES (
                $unitId, $sessionId, $parentUnitId,
                $sourceKind, $sourceDisplayName, $sourceReference,
                $state, $now, $now);
            """))
        {
            command.Parameters.AddWithValue("$unitId", DbGuid.Format(registration.ImportUnitId));
            command.Parameters.AddWithValue("$sessionId", DbGuid.Format(registration.ImportSessionId));
            command.Parameters.AddWithValue(
                "$parentUnitId",
                registration.ParentImportUnitId is Guid parentUnitId ? DbGuid.Format(parentUnitId) : DBNull.Value);
            command.Parameters.AddWithValue("$sourceKind", registration.SourceKind);
            command.Parameters.AddWithValue("$sourceDisplayName", registration.SourceDisplayName);
            command.Parameters.AddWithValue(
                "$sourceReference",
                registration.SourcePathOrReference is null
                    ? DBNull.Value
                    : registration.SourcePathOrReference);
            command.Parameters.AddWithValue("$state", DbEnum.Format(registration.UnitState));
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var check = transaction.CreateCommand(
            """
            SELECT u.import_session_id, u.parent_import_unit_id,
                   u.source_kind, u.source_display_name, u.source_path_or_reference,
                   s.state, u.state
            FROM import_units u
            JOIN import_sessions s ON s.import_session_id = u.import_session_id
            WHERE u.import_unit_id = $unitId;
            """);
        check.Parameters.AddWithValue("$unitId", DbGuid.Format(registration.ImportUnitId));
        await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new CatalogInvariantException(
                $"ImportUnit {registration.ImportUnitId:D} was not durable inside intake admission.");
        }

        var parentId = reader.IsDBNull(1) ? (Guid?)null : DbGuid.Parse(reader.GetString(1));
        var sourceReference = reader.IsDBNull(4) ? null : reader.GetString(4);
        if (DbGuid.Parse(reader.GetString(0)) != registration.ImportSessionId
            || parentId != registration.ParentImportUnitId
            || !string.Equals(reader.GetString(2), registration.SourceKind, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(3), registration.SourceDisplayName, StringComparison.Ordinal)
            || !string.Equals(sourceReference, registration.SourcePathOrReference, StringComparison.Ordinal)
            || DbEnum.ParseImportSessionState(reader.GetString(5)) != registration.SessionState
            || DbEnum.ParseImportUnitState(reader.GetString(6)) != registration.UnitState)
        {
            throw new CatalogInvariantException(
                $"ImportUnit {registration.ImportUnitId:D} already exists with different source authority.");
        }
    }

    private static async Task<Guid> EnsureCandidateAsync(
        CatalogTransaction transaction,
        ImportUnitRegistration registration,
        CandidateAllocationRequest request,
        long now,
        CancellationToken cancellationToken)
    {
        await using (var existingCommand = transaction.CreateCommand(
            """
            SELECT i.import_unit_id, i.candidate_media_id,
                   i.source_path, i.source_file_name,
                   i.source_byte_length, i.source_last_write_ms,
                   a.media_type, a.media_storage_token
            FROM import_items i
            LEFT JOIN media a ON a.media_id = i.candidate_media_id
            WHERE i.import_item_id = $itemId;
            """))
        {
            existingCommand.Parameters.AddWithValue("$itemId", DbGuid.Format(request.ImportItemId));
            await using var existing = await existingCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await existing.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var candidateId = existing.IsDBNull(1) ? (Guid?)null : DbGuid.Parse(existing.GetString(1));
                if (DbGuid.Parse(existing.GetString(0)) != request.ImportUnitId
                    || candidateId != request.CandidateMediaId
                    || !string.Equals(existing.GetString(2), request.SourcePath, StringComparison.Ordinal)
                    || !string.Equals(existing.GetString(3), request.SourceFileName, StringComparison.Ordinal)
                    || (existing.IsDBNull(4) ? (long?)null : existing.GetInt64(4)) != request.SourceByteLength
                    || (existing.IsDBNull(5) ? (long?)null : existing.GetInt64(5)) != request.SourceLastWriteMilliseconds
                    || existing.IsDBNull(6)
                    || DbEnum.ParseMediaType(existing.GetString(6)) != request.MediaType
                    || existing.IsDBNull(7))
                {
                    throw new CatalogInvariantException(
                        $"ImportItem {request.ImportItemId:D} already exists with different discovery authority.");
                }

                return request.CandidateMediaId;
            }
        }

        var storageToken = await CandidateStorageToken.AllocateAsync(
            transaction, request.CandidateMediaId, cancellationToken).ConfigureAwait(false);
        await using (var asset = transaction.CreateCommand(
            """
            INSERT INTO media(
                media_id, state, media_type, media_storage_token,
                original_source_path, original_file_name,
                source_kind, source_display_name, created_at_ms)
            VALUES (
                $assetId, 'CANDIDATE', $mediaType, $storageToken,
                $sourcePath, $sourceFileName,
                $sourceKind, $sourceDisplayName, $now);
            """))
        {
            asset.Parameters.AddWithValue("$assetId", DbGuid.Format(request.CandidateMediaId));
            asset.Parameters.AddWithValue("$mediaType", DbEnum.Format(request.MediaType));
            asset.Parameters.AddWithValue("$storageToken", storageToken);
            asset.Parameters.AddWithValue("$sourcePath", request.SourcePath);
            asset.Parameters.AddWithValue("$sourceFileName", request.SourceFileName);
            asset.Parameters.AddWithValue("$sourceKind", registration.SourceKind);
            asset.Parameters.AddWithValue("$sourceDisplayName", registration.SourceDisplayName);
            asset.Parameters.AddWithValue("$now", now);
            await asset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var item = transaction.CreateCommand(
            """
            INSERT INTO import_items(
                import_item_id, import_unit_id, candidate_media_id,
                source_path, source_file_name, source_byte_length, source_last_write_ms,
                source_identity_json, cleanup_policy, created_at_ms, updated_at_ms)
            VALUES (
                $itemId, $unitId, $assetId,
                $sourcePath, $sourceFileName, $sourceByteLength, $sourceLastWriteMs,
                $identityJson, $cleanupPolicy, $now, $now);
            """))
        {
            item.Parameters.AddWithValue("$itemId", DbGuid.Format(request.ImportItemId));
            item.Parameters.AddWithValue("$unitId", DbGuid.Format(request.ImportUnitId));
            item.Parameters.AddWithValue("$assetId", DbGuid.Format(request.CandidateMediaId));
            item.Parameters.AddWithValue("$sourcePath", request.SourcePath);
            item.Parameters.AddWithValue("$sourceFileName", request.SourceFileName);
            item.Parameters.AddWithValue(
                "$sourceByteLength",
                request.SourceByteLength is long length ? length : DBNull.Value);
            item.Parameters.AddWithValue(
                "$sourceLastWriteMs",
                request.SourceLastWriteMilliseconds is long lastWrite ? lastWrite : DBNull.Value);
            item.Parameters.AddWithValue(
                "$identityJson",
                (object?)request.SourceIdentityJson ?? DBNull.Value);
            item.Parameters.AddWithValue("$cleanupPolicy", DbEnum.Format(request.CleanupPolicy));
            item.Parameters.AddWithValue("$now", now);
            await item.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return request.CandidateMediaId;
    }

    private static void ValidateRegistration(ImportUnitRegistration registration)
    {
        if (registration.ImportSessionId == Guid.Empty)
            throw new ArgumentException("Import session ID cannot be empty.", nameof(registration));
        if (registration.ImportUnitId == Guid.Empty)
            throw new ArgumentException("Import unit ID cannot be empty.", nameof(registration));
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.SourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.SourceDisplayName);
    }

    private static void ValidateCandidate(Guid unitId, CandidateAllocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ImportItemId == Guid.Empty || request.CandidateMediaId == Guid.Empty)
            throw new ArgumentException("Candidate IDs cannot be empty.", nameof(request));
        if (request.ImportUnitId != unitId)
            throw new ArgumentException("Candidate belongs to a different Import Unit.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceFileName);
        if (request.SourceByteLength is < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Source byte length cannot be negative.");
    }
}
