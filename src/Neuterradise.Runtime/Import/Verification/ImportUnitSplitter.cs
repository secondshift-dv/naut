using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Import.Verification;

public sealed class ImportUnitSplitter
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public ImportUnitSplitter(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ImportUnitSplitter(
        CatalogConnectionFactory connectionFactory,
        CatalogWriteCoordinator writeCoordinator,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SplitUnitResult> SplitAsync(
        Guid sourceUnitId,
        IReadOnlyList<Guid> selectedItemIds,
        string? newUnitDisplayName = null,
        CancellationToken cancellationToken = default)
    {
        if (sourceUnitId == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", nameof(sourceUnitId));
        }

        ArgumentNullException.ThrowIfNull(selectedItemIds);
        if (selectedItemIds.Count == 0)
        {
            throw new ArgumentException("Must select at least one item to split.", nameof(selectedItemIds));
        }

        var distinctSelected = new HashSet<Guid>(selectedItemIds);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using var unitCmd = transaction.CreateCommand(
            """
            SELECT import_session_id, parent_import_unit_id, source_kind, source_display_name,
                   source_path_or_reference, state, verification_draft_json, row_version
            FROM import_units
            WHERE import_unit_id = $unitId;
            """);
        unitCmd.Parameters.AddWithValue("$unitId", DbGuid.Format(sourceUnitId));

        Guid sessionId;
        string sourceKind;
        string sourceDisplayName;
        string? sourceReference;
        ImportUnitState unitState;
        string draftJson;
        long sourceRowVersion;

        await using (var reader = await unitCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new CatalogInvariantException($"ImportUnit {sourceUnitId:D} does not exist.");
            }

            sessionId = DbGuid.Parse(reader.GetString(0));
            sourceKind = reader.GetString(2);
            sourceDisplayName = reader.GetString(3);
            sourceReference = reader.IsDBNull(4) ? null : reader.GetString(4);
            unitState = DbEnum.ParseImportUnitState(reader.GetString(5));
            draftJson = reader.GetString(6);
            sourceRowVersion = reader.GetInt64(7);
        }

        if (unitState.IsUnitCommitted() || unitState.IsTerminal())
        {
            throw new CatalogInvariantException(
                $"Cannot split terminal ImportUnit {sourceUnitId:D} in state '{DbEnum.Format(unitState)}'.");
        }

        await using var itemsCmd = transaction.CreateCommand(
            """
            SELECT import_item_id, candidate_media_id, reused_media_id
            FROM import_items
            WHERE import_unit_id = $unitId;
            """);
        itemsCmd.Parameters.AddWithValue("$unitId", DbGuid.Format(sourceUnitId));

        var allItems = new List<(Guid ItemId, Guid? CandidateMediaId, Guid? ReusedMediaId)>();
        await using (var reader = await itemsCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var itemId = DbGuid.Parse(reader.GetString(0));
                var candidateId = reader.IsDBNull(1) ? (Guid?)null : DbGuid.Parse(reader.GetString(1));
                var reusedId = reader.IsDBNull(2) ? (Guid?)null : DbGuid.Parse(reader.GetString(2));
                allItems.Add((itemId, candidateId, reusedId));
            }
        }

        var allItemIds = allItems.Select(x => x.ItemId).ToHashSet();
        foreach (var selectedId in distinctSelected)
        {
            if (!allItemIds.Contains(selectedId))
            {
                throw new CatalogInvariantException(
                    $"Selected ImportItem {selectedId:D} does not belong to source ImportUnit {sourceUnitId:D}.");
            }
        }

        if (distinctSelected.Count >= allItems.Count)
        {
            throw new CatalogInvariantException(
                $"Cannot split all items from source unit. At least one item must remain in source unit {sourceUnitId:D}.");
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var newUnitId = Guid.NewGuid();
        var newDisplayName = string.IsNullOrWhiteSpace(newUnitDisplayName)
            ? $"{sourceDisplayName} (Split)"
            : newUnitDisplayName.Trim();
        // A child is a fresh verification unit. It must not inherit a parent lifecycle
        // state (especially READY/COMMITTING), otherwise it could bypass preparation.
        var initialDraftJson = VerificationDraftV1.CreateDefault(_timeProvider.GetUtcNowUnixMilliseconds()).ToJson();

        await using (var createUnitCmd = transaction.CreateCommand(
            """
            INSERT INTO import_units(
                import_unit_id, import_session_id, parent_import_unit_id,
                source_kind, source_display_name, source_path_or_reference,
                state, verification_step, verification_draft_json, verification_version,
                created_at_ms, updated_at_ms, row_version)
            VALUES (
                $unitId, $sessionId, $parentUnitId,
                $sourceKind, $sourceDisplayName, $sourceReference,
                $state, 1, $draftJson, 0,
                $now, $now, 0);
            """))
        {
            createUnitCmd.Parameters.AddWithValue("$unitId", DbGuid.Format(newUnitId));
            createUnitCmd.Parameters.AddWithValue("$sessionId", DbGuid.Format(sessionId));
            createUnitCmd.Parameters.AddWithValue("$parentUnitId", DbGuid.Format(sourceUnitId));
            createUnitCmd.Parameters.AddWithValue("$sourceKind", sourceKind);
            createUnitCmd.Parameters.AddWithValue("$sourceDisplayName", newDisplayName);
            createUnitCmd.Parameters.AddWithValue("$sourceReference", (object?)sourceReference ?? DBNull.Value);
            createUnitCmd.Parameters.AddWithValue("$state", DbEnum.Format(ImportUnitState.Intake));
            createUnitCmd.Parameters.AddWithValue("$draftJson", initialDraftJson);
            createUnitCmd.Parameters.AddWithValue("$now", now);
            await createUnitCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var movedCandidateMediaIds = new HashSet<Guid>();
        var movedReusedMediaIds = new HashSet<Guid>();

        foreach (var item in allItems)
        {
            if (distinctSelected.Contains(item.ItemId))
            {
                if (item.CandidateMediaId.HasValue) movedCandidateMediaIds.Add(item.CandidateMediaId.Value);
                if (item.ReusedMediaId.HasValue) movedReusedMediaIds.Add(item.ReusedMediaId.Value);

                await using var reassignCmd = transaction.CreateCommand(
                    """
                    UPDATE import_items
                    SET import_unit_id = $newUnitId,
                        updated_at_ms = $now,
                        row_version = row_version + 1
                    WHERE import_item_id = $itemId AND import_unit_id = $sourceUnitId;
                    """);
                reassignCmd.Parameters.AddWithValue("$newUnitId", DbGuid.Format(newUnitId));
                reassignCmd.Parameters.AddWithValue("$itemId", DbGuid.Format(item.ItemId));
                reassignCmd.Parameters.AddWithValue("$sourceUnitId", DbGuid.Format(sourceUnitId));
                reassignCmd.Parameters.AddWithValue("$now", now);
                await reassignCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var sourceDraft = VerificationDraftV1.FromJson(draftJson, _timeProvider.GetUtcNowUnixMilliseconds());
        bool draftChanged = false;

        var updatedCover = sourceDraft.Appearance.CoverMediaId;
        if (updatedCover.HasValue && (movedCandidateMediaIds.Contains(updatedCover.Value) || movedReusedMediaIds.Contains(updatedCover.Value)))
        {
            updatedCover = null;
            draftChanged = true;
        }

        var updatedBanner = sourceDraft.Appearance.BannerMediaId;
        if (updatedBanner.HasValue && (movedCandidateMediaIds.Contains(updatedBanner.Value) || movedReusedMediaIds.Contains(updatedBanner.Value)))
        {
            updatedBanner = null;
            draftChanged = true;
        }

        var movedFaceIds = new HashSet<Guid>();
        if (sourceDraft.FaceDecisions.Count > 0 && (movedCandidateMediaIds.Count > 0 || movedReusedMediaIds.Count > 0))
        {
            var movedMedias = movedCandidateMediaIds.Concat(movedReusedMediaIds).ToList();
            foreach (var assetId in movedMedias)
            {
                await using var faceCmd = transaction.CreateCommand(
                    """
                    SELECT face_id FROM face_detections WHERE media_id = $assetId;
                    """);
                faceCmd.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
                await using var reader = await faceCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    movedFaceIds.Add(DbGuid.Parse(reader.GetString(0)));
                }
            }
        }

        var updatedFaceDecisions = sourceDraft.FaceDecisions;
        if (movedFaceIds.Count > 0)
        {
            var filtered = sourceDraft.FaceDecisions
                .Where(fd => !movedFaceIds.Contains(fd.FaceId))
                .ToList();
            if (filtered.Count != sourceDraft.FaceDecisions.Count)
            {
                updatedFaceDecisions = filtered;
                draftChanged = true;
            }
        }

        var updatedDraftJson = draftJson;
        if (draftChanged)
        {
            var updatedDraft = sourceDraft with
            {
                Appearance = sourceDraft.Appearance with
                {
                    CoverMediaId = updatedCover,
                    CoverSourceKind = updatedCover is null ? null : sourceDraft.Appearance.CoverSourceKind,
                    CoverImportItemId = updatedCover is null ? null : sourceDraft.Appearance.CoverImportItemId,
                    CoverVideoTimestampMilliseconds = updatedCover is null ? null : sourceDraft.Appearance.CoverVideoTimestampMilliseconds,
                    BannerMediaId = updatedBanner,
                    BannerImportItemId = updatedBanner is null ? null : sourceDraft.Appearance.BannerImportItemId,
                },
                FaceDecisions = updatedFaceDecisions,
                UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            };
            updatedDraftJson = updatedDraft.ToJson();
        }

        await using (var updateSourceCmd = transaction.CreateCommand(
            """
            UPDATE import_units
            SET verification_draft_json = $draftJson,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_unit_id = $sourceUnitId AND row_version = $expectedRowVersion;
            """))
        {
            updateSourceCmd.Parameters.AddWithValue("$draftJson", updatedDraftJson);
            updateSourceCmd.Parameters.AddWithValue("$now", now);
            updateSourceCmd.Parameters.AddWithValue("$sourceUnitId", DbGuid.Format(sourceUnitId));
            updateSourceCmd.Parameters.AddWithValue("$expectedRowVersion", sourceRowVersion);

            var affected = await updateSourceCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                throw new CatalogConcurrencyConflictException(
                    $"ImportUnit {sourceUnitId:D} concurrency conflict during split.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var remainingCount = allItems.Count - distinctSelected.Count;
        var movedCount = distinctSelected.Count;
        return new SplitUnitResult(sourceUnitId, newUnitId, remainingCount, movedCount);
    }
}
