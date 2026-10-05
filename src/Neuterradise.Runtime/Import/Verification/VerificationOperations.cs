using Neuterradise.App.Faces;
using Neuterradise.App.Import;
using Neuterradise.App.Import.Preparation;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Storage;
using System.IO;

namespace Neuterradise.App.Import.Verification;

public sealed class VerificationOperations
{
    private readonly CatalogDb _catalog;
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;
    private readonly ImportReads _importReads;
    private readonly ImportWrites _importWrites;

    public VerificationOperations(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _importReads = catalog.ImportReads;
        _importWrites = catalog.ImportWrites;
    }

    public Task<VerificationReadModel?> LoadVerificationReadModelAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        return _importReads.GetVerificationReadModelAsync(unitId, cancellationToken);
    }

    public async Task<long> UpdateDraftAsync(
        Guid unitId,
        VerificationDraftV1 draft,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(UpdateDraftAsync));
        if (unitId == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", nameof(unitId));
        }
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.SchemaVersion is < 1 or > VerificationDraftV1.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported VerificationDraft schema version: {draft.SchemaVersion}");
        }

        if (draft.CurrentStep is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(draft), $"Step must be between 1 and 5, was {draft.CurrentStep}");
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var updatedDraft = draft with { UpdatedAtMs = now };
        var draftJson = updatedDraft.ToJson();

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var nowMs = DbTime.Format(_timeProvider.GetUtcNow());
        string? destKind = updatedDraft.Destination.Kind.HasValue
            ? DbEnum.Format(updatedDraft.Destination.Kind.Value)
            : null;
        Guid? destProfileId = null;
        string? currentDestinationKind = null;
        Guid? currentDestinationProfileId = null;
        var destinationLocked = false;
        await using (var currentUnit = transaction.CreateCommand(
            "SELECT destination_kind, destination_profile_id, commit_operation_id, library_commit_state FROM import_units WHERE import_unit_id = $unitId;"))
        {
            currentUnit.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var currentReader = await currentUnit.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await currentReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new CatalogInvariantException($"ImportUnit {unitId:D} does not exist.");
            }

            currentDestinationKind = currentReader.IsDBNull(0) ? null : currentReader.GetString(0);
            currentDestinationProfileId = currentReader.IsDBNull(1) ? null : DbGuid.Parse(currentReader.GetString(1));
            destinationLocked = !currentReader.IsDBNull(2)
                || (!currentReader.IsDBNull(3)
                    && DbEnum.ParseImportCommitCheckpointOrDefault(currentReader.GetString(3)) >= ImportCommitCheckpoint.DomainAuthorityCommitted);
        }

        if (destinationLocked)
        {
            if (!string.Equals(currentDestinationKind, destKind, StringComparison.OrdinalIgnoreCase)
                || (updatedDraft.Destination.Kind == DestinationKind.ExistingNormal
                    && updatedDraft.Destination.ProfileId != currentDestinationProfileId))
            {
                throw new CatalogConcurrencyConflictException(
                    $"ImportUnit {unitId:D} destination is immutable after canonical commit begins.");
            }
            destProfileId = currentDestinationProfileId;
        }
        else if (updatedDraft.Destination.Kind == DestinationKind.ExistingNormal && updatedDraft.Destination.ProfileId.HasValue)
        {
            await using var checkProf = transaction.CreateCommand(
                "SELECT 1 FROM profiles WHERE profile_id = $pid AND kind = 'NORMAL' AND visibility = 'PUBLISHED' AND trashed_at_ms IS NULL;");
            checkProf.Parameters.AddWithValue("$pid", DbGuid.Format(updatedDraft.Destination.ProfileId.Value));
            var profExists = await checkProf.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (profExists is not null and not DBNull)
            {
                destProfileId = updatedDraft.Destination.ProfileId.Value;
            }
        }

        await using var command = transaction.CreateCommand(
            """
            UPDATE import_units
            SET verification_step = $step,
                verification_draft_json = $draftJson,
                verification_version = $version,
                destination_kind = $destinationKind,
                destination_profile_id = $destinationProfileId,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_unit_id = $unitId AND row_version = $expectedRowVersion;
            """);
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        command.Parameters.AddWithValue("$step", updatedDraft.CurrentStep);
        command.Parameters.AddWithValue("$draftJson", draftJson.Trim());
        command.Parameters.AddWithValue("$version", updatedDraft.SchemaVersion);
        command.Parameters.AddWithValue("$destinationKind", (object?)destKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$destinationProfileId", destProfileId.HasValue ? DbGuid.Format(destProfileId.Value) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", nowMs);
        command.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            await using var check = transaction.CreateCommand("SELECT row_version FROM import_units WHERE import_unit_id = $unitId;");
            check.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            var currentVer = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (currentVer is null or DBNull)
            {
                throw new CatalogInvariantException($"ImportUnit {unitId:D} does not exist.");
            }
            throw new CatalogConcurrencyConflictException(
                $"ImportUnit {unitId:D} concurrency conflict: expected row_version {expectedRowVersion}, found {currentVer}.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expectedRowVersion + 1;
    }

    public async Task<long> SetStepAsync(
        Guid unitId,
        int step,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetStepAsync));
        if (step is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(step), "Step must be between 1 and 5.");
        }

        var model = await LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false)
            ?? throw new CatalogInvariantException($"ImportUnit {unitId:D} does not exist.");

        var draft = VerificationDraftV1.FromJson(model.VerificationDraftJson, _timeProvider.GetUtcNowUnixMilliseconds());
        var updatedDraft = draft with { CurrentStep = step };

        return await UpdateDraftAsync(unitId, updatedDraft, expectedRowVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> SetDestinationAsync(
        Guid unitId,
        VerificationDestinationDraft destination,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetDestinationAsync));
        ArgumentNullException.ThrowIfNull(destination);
        var model = await LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false)
            ?? throw new CatalogInvariantException($"ImportUnit {unitId:D} does not exist.");

        var draft = VerificationDraftV1.FromJson(model.VerificationDraftJson, _timeProvider.GetUtcNowUnixMilliseconds());
        var updatedDraft = draft with { Destination = destination };

        return await UpdateDraftAsync(unitId, updatedDraft, expectedRowVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> SetAppearanceAsync(
        Guid unitId,
        VerificationAppearanceDraft appearance,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetAppearanceAsync));
        ArgumentNullException.ThrowIfNull(appearance);
        var model = await LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false)
            ?? throw new CatalogInvariantException($"ImportUnit {unitId:D} does not exist.");

        var draft = VerificationDraftV1.FromJson(model.VerificationDraftJson, _timeProvider.GetUtcNowUnixMilliseconds());
        var updatedDraft = draft with { Appearance = appearance };

        return await UpdateDraftAsync(unitId, updatedDraft, expectedRowVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> StageFaceDecisionAsync(
        Guid unitId,
        StagedFaceDecision decision,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(StageFaceDecisionAsync));
        ArgumentNullException.ThrowIfNull(decision);
        var model = await LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false)
            ?? throw new CatalogInvariantException($"ImportUnit {unitId:D} does not exist.");

        var draft = VerificationDraftV1.FromJson(model.VerificationDraftJson, _timeProvider.GetUtcNowUnixMilliseconds());
        var existingDecisions = draft.FaceDecisions.Where(d => d.FaceId != decision.FaceId).ToList();
        existingDecisions.Add(decision);

        var updatedDraft = draft with { FaceDecisions = existingDecisions };
        return await UpdateDraftAsync(unitId, updatedDraft, expectedRowVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> RemoveStagedFaceDecisionAsync(
        Guid unitId,
        Guid faceId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(RemoveStagedFaceDecisionAsync));
        var model = await LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false)
            ?? throw new CatalogInvariantException($"ImportUnit {unitId:D} does not exist.");

        var draft = VerificationDraftV1.FromJson(model.VerificationDraftJson, _timeProvider.GetUtcNowUnixMilliseconds());
        var filtered = draft.FaceDecisions.Where(d => d.FaceId != faceId).ToList();

        var updatedDraft = draft with { FaceDecisions = filtered };
        return await UpdateDraftAsync(unitId, updatedDraft, expectedRowVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> ClearStagedFaceDecisionsAsync(
        Guid unitId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(ClearStagedFaceDecisionsAsync));
        var model = await LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false)
            ?? throw new CatalogInvariantException($"ImportUnit {unitId:D} does not exist.");

        var draft = VerificationDraftV1.FromJson(model.VerificationDraftJson, _timeProvider.GetUtcNowUnixMilliseconds());
        var updatedDraft = draft with { FaceDecisions = [] };
        return await UpdateDraftAsync(unitId, updatedDraft, expectedRowVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VerificationAppearanceCandidates> GetAppearanceCandidatesAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return VerificationAppearanceCandidates.Empty;
        }

        var model = await LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return VerificationAppearanceCandidates.Empty;
        }

        var draft = VerificationDraftV1.FromJson(model.VerificationDraftJson, _timeProvider.GetUtcNowUnixMilliseconds());
        var destinationProfileId = draft.Destination.Kind == DestinationKind.ExistingNormal
            ? draft.Destination.ProfileId
            : null;

        var evaluationInputs = new List<AppearanceCandidateEvaluationInput>();
        var itemMetaMap = new Dictionary<Guid, (Guid? ImportItemId, bool IsFromUnit)>();
        var seenMediaIds = new HashSet<Guid>();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (var unitCmd = connection.CreateCommand())
        {
            unitCmd.CommandText = """
                SELECT COALESCE(ii.reused_media_id, ii.candidate_media_id) AS media_id,
                       ii.import_item_id,
                       ii.source_file_name,
                       a.media_type,
                       am.width AS pixel_width,
                       am.height AS pixel_height,
                       am.duration_ms AS duration_ms,
                       ii.created_at_ms,
                       EXISTS (
                           SELECT 1 FROM face_detections fd
                           WHERE fd.media_id = COALESCE(ii.reused_media_id, ii.candidate_media_id)
                       ) AS has_face_detection,
                       CASE
                           WHEN $profileId IS NOT NULL THEN EXISTS (
                               SELECT 1 FROM face_detections fd
                               WHERE fd.media_id = COALESCE(ii.reused_media_id, ii.candidate_media_id)
                                 AND fd.decision_state = 'CONFIRMED'
                                 AND fd.confirmed_identity_id IN (SELECT identity_id FROM identities WHERE profile_id = $profileId)
                           )
                           ELSE 0
                       END AS has_confirmed_face
                FROM import_items ii
                JOIN media a ON a.media_id = COALESCE(ii.reused_media_id, ii.candidate_media_id)
                LEFT JOIN media_metadata am ON am.media_id = a.media_id
                WHERE ii.import_unit_id = $unitId
                  AND ii.disposition IN ('INCLUDED','REUSED')
                ORDER BY ii.created_at_ms ASC, ii.import_item_id ASC;
                """;
            unitCmd.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            unitCmd.Parameters.AddWithValue("$profileId", destinationProfileId.HasValue ? DbGuid.Format(destinationProfileId.Value) : DBNull.Value);

            await using var reader = await unitCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var assetId = DbGuid.Parse(reader.GetString(0));
                var itemId = DbGuid.Parse(reader.GetString(1));
                var name = reader.GetString(2);
                var mediaType = DbEnum.ParseMediaType(reader.GetString(3));
                var width = reader.IsDBNull(4) ? (int?)null : Convert.ToInt32(reader.GetInt64(4));
                var height = reader.IsDBNull(5) ? (int?)null : Convert.ToInt32(reader.GetInt64(5));
                var duration = reader.IsDBNull(6) ? (int?)null : Convert.ToInt32(reader.GetInt64(6));
                var createdAt = reader.GetInt64(7);
                var hasFace = !reader.IsDBNull(8) && reader.GetInt32(8) == 1;
                var hasConfirmed = !reader.IsDBNull(9) && reader.GetInt32(9) == 1;

                if (seenMediaIds.Add(assetId))
                {
                    itemMetaMap[assetId] = (itemId, true);
                    var hasPreview = (await _catalog.MediaAssetReads.GetOwnedMediaAssetAsync(
                        assetId, MediaAssetRole.Thumbnail, cancellationToken).ConfigureAwait(false))?.State
                        == MediaAssetState.Ready;

                    evaluationInputs.Add(new AppearanceCandidateEvaluationInput(
                        MediaId: assetId,
                        MediaType: mediaType,
                        DisplayName: name,
                        PixelWidth: width,
                        PixelHeight: height,
                        DurationMs: duration,
                        HasDerivedPreview: hasPreview,
                        HasConfirmedFaceForProfile: hasConfirmed,
                        HasFaceDetection: hasFace,
                        CreatedAtMs: createdAt));
                }
            }
        }

        if (draft.Destination.Kind == DestinationKind.ExistingNormal && draft.Destination.ProfileId.HasValue)
        {
            var profId = draft.Destination.ProfileId.Value;
            await using var profileCmd = connection.CreateCommand();
            profileCmd.CommandText = """
                SELECT pa.media_id,
                       COALESCE(a.current_managed_file_name, a.sha256) AS display_name,
                       a.media_type,
                       am.width AS pixel_width,
                       am.height AS pixel_height,
                       am.duration_ms,
                       pa.created_at_ms,
                       EXISTS (
                           SELECT 1 FROM face_detections fd
                           WHERE fd.media_id = pa.media_id
                       ) AS has_face_detection,
                       EXISTS (
                           SELECT 1 FROM face_detections fd
                           WHERE fd.media_id = pa.media_id
                             AND fd.decision_state = 'CONFIRMED'
                             AND fd.confirmed_identity_id IN (SELECT identity_id FROM identities WHERE profile_id = $profileId)
                       ) AS has_confirmed_face,
                       (CASE WHEN p.cover_media_id = pa.media_id THEN 1 ELSE 0 END) AS is_existing_cover,
                       (CASE WHEN EXISTS (
                           SELECT 1
                           FROM profile_appearance appearance
                           JOIN media_assets banner_asset
                             ON banner_asset.media_asset_id = appearance.banner_media_asset_id
                           WHERE appearance.profile_id = p.profile_id
                             AND banner_asset.media_id = pa.media_id
                       ) THEN 1 ELSE 0 END) AS is_existing_banner
                FROM profile_media pa
                JOIN media a ON pa.media_id = a.media_id
                LEFT JOIN media_metadata am ON pa.media_id = am.media_id
                LEFT JOIN profiles p ON pa.profile_id = p.profile_id
                WHERE pa.profile_id = $profileId
                  AND pa.publication_import_unit_id IS NULL
                  AND a.state = 'ACTIVE' AND a.trashed_at_ms IS NULL
                ORDER BY pa.created_at_ms ASC, pa.media_id ASC;
                """;
            profileCmd.Parameters.AddWithValue("$profileId", DbGuid.Format(profId));

            await using var reader = await profileCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var assetId = DbGuid.Parse(reader.GetString(0));
                var name = reader.GetString(1);
                var mediaType = DbEnum.ParseMediaType(reader.GetString(2));
                var width = reader.IsDBNull(3) ? (int?)null : Convert.ToInt32(reader.GetInt64(3));
                var height = reader.IsDBNull(4) ? (int?)null : Convert.ToInt32(reader.GetInt64(4));
                var duration = reader.IsDBNull(5) ? (int?)null : Convert.ToInt32(reader.GetInt64(5));
                var createdAt = reader.GetInt64(6);
                var hasFace = !reader.IsDBNull(7) && reader.GetInt32(7) == 1;
                var hasConfirmed = !reader.IsDBNull(8) && reader.GetInt32(8) == 1;
                var isCover = !reader.IsDBNull(9) && reader.GetInt32(9) == 1;
                var isBanner = !reader.IsDBNull(10) && reader.GetInt32(10) == 1;

                if (seenMediaIds.Add(assetId))
                {
                    itemMetaMap[assetId] = (null, false);
                    var hasPreview = (await _catalog.MediaAssetReads.GetOwnedMediaAssetAsync(
                        assetId, MediaAssetRole.Thumbnail, cancellationToken).ConfigureAwait(false))?.State
                        == MediaAssetState.Ready;

                    evaluationInputs.Add(new AppearanceCandidateEvaluationInput(
                        MediaId: assetId,
                        MediaType: mediaType,
                        DisplayName: name,
                        PixelWidth: width,
                        PixelHeight: height,
                        DurationMs: duration,
                        HasDerivedPreview: hasPreview,
                        HasConfirmedFaceForProfile: hasConfirmed,
                        HasFaceDetection: hasFace,
                        CreatedAtMs: createdAt,
                        IsExistingCover: isCover,
                        IsExistingBanner: isBanner));
                }
            }
        }

        var faceEvidence = await _catalog.FaceReads
            .GetAppearanceFaceEvidenceAsync(
                [.. evaluationInputs.Select(static input => input.MediaId)],
                destinationProfileId,
                cancellationToken)
            .ConfigureAwait(false);
        if (faceEvidence.Count > 0)
        {
            for (var index = 0; index < evaluationInputs.Count; index++)
            {
                if (faceEvidence.TryGetValue(evaluationInputs[index].MediaId, out var faces))
                {
                    evaluationInputs[index] = evaluationInputs[index] with { FaceEvidence = faces };
                }
            }
        }

        var ranked = model.DestinationProfileId is { } profileId
            ? await new ProfileMediaCandidateReads(_catalog).ReadAsync(profileId, cancellationToken, unitId).ConfigureAwait(false)
            : new ProfileMediaCandidates([], []);
        string? Preview(ProfileMediaCandidate candidate)
        {
            try
            {
                var path = _catalog.Paths.ResolveVaultRelativePath(candidate.RelativePath);
                return File.Exists(path) ? path : null;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        var covers = ranked.Covers.Select(candidate => new VerificationCoverCandidate(
            candidate.MediaAssetId.ToString("N"), candidate.MediaId,
            itemMetaMap.GetValueOrDefault(candidate.MediaId).ImportItemId,
            candidate.DisplayName, candidate.MediaType,
            candidate.SourceTimestampMs.HasValue ? CoverVisualSourceKind.VideoFrame : CoverVisualSourceKind.Image,
            candidate.SourceTimestampMs,
            itemMetaMap.GetValueOrDefault(candidate.MediaId).IsFromUnit,
            candidate.Rank, candidate.Rank == 1,
            "Ranked Media Thumbnail", Preview(candidate))).ToArray();

        var banners = ranked.BannerHovers
            .Select(candidate => new VerificationBannerCandidate(
                candidate.MediaAssetId.ToString("N"), candidate.MediaId,
                itemMetaMap.GetValueOrDefault(candidate.MediaId).ImportItemId,
                candidate.DisplayName, candidate.MediaType,
                itemMetaMap.GetValueOrDefault(candidate.MediaId).IsFromUnit,
                candidate.Rank, candidate.Rank == 1,
                "Video banner", Preview(candidate))).ToArray();

        return new VerificationAppearanceCandidates(covers, banners);
    }

    private async Task<string?> ResolvePreviewImagePathAsync(
        Guid mediaId,
        CancellationToken cancellationToken)
    {
        if (mediaId == Guid.Empty)
        {
            return null;
        }

        var mediaAsset = await _catalog.MediaAssetReads
            .GetOwnedMediaAssetAsync(mediaId, MediaAssetRole.Thumbnail, cancellationToken)
            .ConfigureAwait(false);
        if (mediaAsset is not { State: MediaAssetState.Ready })
        {
            return null;
        }

        try
        {
            var path = _catalog.Paths.ResolveVaultRelativePath(
                VaultPathArea.MediaAssets, mediaAsset.RelativePath);
            return File.Exists(path) ? path : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<FaceReviewReadModel>> GetUnitFaceReviewsAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        return _catalog.FaceReads.GetFaceReviewsForUnitAsync(unitId, cancellationToken);
    }

    /// <summary>
    /// Records the disposition of one item. A duplicate decision is only written when the caller
    /// actually made one; the writer never invents a decision such as "NONE" for a plain include or
    /// skip.
    /// </summary>
    public async Task SetItemDispositionAsync(
        Guid importItemId,
        ItemDisposition disposition,
        DuplicateDecision? duplicateDecision = null,
        Guid? reusedMediaId = null,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetItemDispositionAsync));
        if (duplicateDecision is { } decision)
        {
            await _importWrites.ApplyDuplicateDecisionAsync(
                importItemId,
                DbEnum.DispositionFor(decision),
                decision,
                reusedMediaId,
                cancellationToken).ConfigureAwait(false);
            if (decision == DuplicateDecision.Include)
                await new ImportPreparationCoordinator(_catalog)
                    .ScheduleIncludedTransferAsync(importItemId, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (reusedMediaId.HasValue)
        {
            throw new ArgumentException(
                "A reused asset can only be recorded together with a REUSE duplicate decision.",
                nameof(reusedMediaId));
        }

        await _importWrites.UpdateItemDispositionAsync(
            importItemId,
            disposition,
            duplicateDecision: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetBatchItemDispositionsAsync(
        IReadOnlyList<Guid> importItemIds,
        ItemDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetBatchItemDispositionsAsync));
        ArgumentNullException.ThrowIfNull(importItemIds);
        if (importItemIds.Count == 0)
        {
            return;
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        foreach (var itemId in importItemIds)
        {
            await using var command = transaction.CreateCommand(
                """
                UPDATE import_items
                SET disposition = $disposition,
                    updated_at_ms = $now
                WHERE import_item_id = $itemId;
                """);
            command.Parameters.AddWithValue("$itemId", DbGuid.Format(itemId));
            command.Parameters.AddWithValue("$disposition", DbEnum.Format(disposition));
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyDuplicateDecisionAsync(
        Guid importItemId,
        DuplicateDecision decision,
        Guid? reusedMediaId = null,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(ApplyDuplicateDecisionAsync));
        await _importWrites.ApplyDuplicateDecisionAsync(
            importItemId,
            DbEnum.DispositionFor(decision),
            decision,
            reusedMediaId,
            cancellationToken).ConfigureAwait(false);
        if (decision == DuplicateDecision.Include)
            await new ImportPreparationCoordinator(_catalog)
                .ScheduleIncludedTransferAsync(importItemId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Exact-duplicate matches for the unit. The surface uses this instead of inventing
    /// a disposition, so an open decision is visible without persisting a non-canonical state.
    /// </summary>
    public Task<IReadOnlyList<ImportDuplicateMatch>> GetExactDuplicateMatchesAsync(
        Guid unitId,
        CancellationToken cancellationToken = default) =>
        _catalog.ImportReads.ListExactDuplicateMatchesAsync(unitId, cancellationToken);

    public async Task<VerificationGateProblems> GetGateProblemsAsync(
        Guid unitId,
        VerificationDraftV1 draft,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return VerificationGateProblems.Empty;
        }

        var items = await GetPagedItemsAsync(unitId, 0, 1000, cancellationToken).ConfigureAwait(false);
        var itemById = items.Items.ToDictionary(static item => item.ItemId);
        var duplicateDecisions = (draft.DuplicateDecisions ?? [])
            .Select(static decision => decision.ImportItemId)
            .ToHashSet();
        var duplicates = new List<VerificationDuplicateProblem>();
        foreach (var match in await GetExactDuplicateMatchesAsync(unitId, cancellationToken).ConfigureAwait(false))
        {
            if (!match.RequiresDecision || duplicateDecisions.Contains(match.ImportItemId)
                || !itemById.TryGetValue(match.ImportItemId, out var item))
            {
                continue;
            }

            duplicates.Add(new VerificationDuplicateProblem(
                match.ImportItemId,
                match.MatchedMediaId,
                item.SourceFileName,
                await ReadOwnerDisplayNameAsync(match.MatchedMediaId, cancellationToken).ConfigureAwait(false),
                await ResolvePreviewImagePathAsync(match.MatchedMediaId, cancellationToken).ConfigureAwait(false)));
        }

        var collisionDecisions = (draft.ProfileCollisionDecisions ?? [])
            .Select(static decision => decision.ImportItemId)
            .ToHashSet();
        var collisions = (await ReadStrongProfileCollisionsAsync(unitId, draft.Destination, cancellationToken).ConfigureAwait(false))
            .Where(problem => !collisionDecisions.Contains(problem.ImportItemId))
            .ToArray();

        var preparationMap = await _importReads
            .ListRequiredPreparationFailuresAsync([unitId], cancellationToken)
            .ConfigureAwait(false);
        var preparationFailures = preparationMap.GetValueOrDefault(unitId) ?? [];
        var preparation = preparationFailures
            .GroupBy(failure => failure.ImportItemId)
            .Select(group => new VerificationPreparationProblem(
                group.Key,
                group.First().SourceFileName,
                group.ToArray()))
            .OrderBy(problem => problem.SourceFileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var verification = await LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false);
        var pending = verification?.LibraryCommitState.HasReachedDomainCommit() == true
            && !await new CapabilityReads(_catalog)
                .AreAllRequiredCapabilitiesSettledAsync(unitId, cancellationToken)
                .ConfigureAwait(false);

        return new VerificationGateProblems(duplicates, collisions, preparation, pending);
    }

    public async Task<PreparationRetryResult> RetryRequiredPreparationAsync(
        Guid unitId,
        Guid importItemId,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(RetryRequiredPreparationAsync));
        return await new ImportMediaPreparationCoordinator(_catalog, _catalog.Paths, _timeProvider)
            .RetryRequiredPreparationAsync(unitId, importItemId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string?> ReadOwnerDisplayNameAsync(Guid assetId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.display_name
            FROM profile_media pa
            JOIN profiles p ON p.profile_id = pa.profile_id
            WHERE pa.media_id = $assetId AND pa.relation_type = 'OWNER'
              AND p.kind = 'NORMAL' AND p.trashed_at_ms IS NULL
            ORDER BY pa.created_at_ms ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private async Task<IReadOnlyList<VerificationProfileCollisionProblem>> ReadStrongProfileCollisionsAsync(
        Guid unitId,
        VerificationDestinationDraft destination,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ii.import_item_id, COALESCE(ii.reused_media_id, ii.candidate_media_id),
                   ii.source_file_name, fd.suggested_identity_id, fd.suggested_candidates_json,
                   p.profile_id, p.display_name
            FROM import_items ii
            JOIN face_detections fd ON fd.media_id = COALESCE(ii.reused_media_id, ii.candidate_media_id)
            JOIN identities identity ON identity.identity_id = fd.suggested_identity_id
            JOIN profiles p ON p.profile_id = identity.profile_id
            WHERE ii.import_unit_id = $unitId
              AND ii.disposition IN ('INCLUDED','REUSED')
              AND p.kind = 'NORMAL' AND p.trashed_at_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var evidence = new List<(Guid ItemId, Guid MediaId, string FileName, Guid ProfileId, string ProfileName, double Similarity)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(3) || reader.IsDBNull(4))
            {
                continue;
            }

            var identityId = DbGuid.Parse(reader.GetString(3));
            var parsed = FaceSuggestionEvidenceV1.TryParse(reader.GetString(4));
            var similarity = parsed?.Candidates.FirstOrDefault(candidate => candidate.IdentityId == identityId)?.Similarity;
            if (similarity is null || similarity < ImportProfileCollisionPolicy.StrongSimilarityThreshold)
            {
                continue;
            }

            evidence.Add((
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1)),
                reader.GetString(2),
                DbGuid.Parse(reader.GetString(5)),
                reader.GetString(6),
                similarity.Value));
        }

        var destinationId = destination.Kind == DestinationKind.ExistingNormal ? destination.ProfileId : null;
        var result = new List<VerificationProfileCollisionProblem>();
        foreach (var itemGroup in evidence.GroupBy(static row => row.ItemId))
        {
            var profiles = itemGroup.GroupBy(static row => row.ProfileId).ToArray();
            if (destinationId.HasValue && profiles.Any(group => group.Key == destinationId.Value))
            {
                continue;
            }

            var alternates = profiles.Where(group => !destinationId.HasValue || group.Key != destinationId.Value).ToArray();
            if (alternates.Length != 1)
            {
                continue;
            }

            var strongest = alternates[0].OrderByDescending(static row => row.Similarity).First();
            result.Add(new VerificationProfileCollisionProblem(
                strongest.ItemId,
                strongest.MediaId,
                strongest.FileName,
                strongest.ProfileId,
                strongest.ProfileName,
                strongest.Similarity,
                await ResolvePreviewImagePathAsync(strongest.MediaId, cancellationToken).ConfigureAwait(false)));
        }

        return result;
    }

    public async Task<PagedItemsResult> GetPagedItemsAsync(
        Guid unitId,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return new PagedItemsResult([], 0, offset, limit);
        }

        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 1000);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM import_items WHERE import_unit_id = $unitId;";
        countCmd.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        var countObj = await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var totalCount = Convert.ToInt32(countObj);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ii.import_item_id, ii.import_unit_id, ii.candidate_media_id,
                   ii.reused_media_id, ii.source_path, ii.source_file_name,
                   ii.source_byte_length, ii.source_last_write_ms,
                   ii.disposition, ii.duplicate_decision,
                   ii.source_cleanup_state, ii.source_cleanup_error,
                   ii.cleanup_policy, a.media_type, ii.created_at_ms, ii.updated_at_ms
            FROM import_items ii
            LEFT JOIN media a ON ii.candidate_media_id = a.media_id
            WHERE ii.import_unit_id = $unitId
            ORDER BY ii.created_at_ms ASC, ii.import_item_id ASC
            LIMIT {limit} OFFSET {offset};
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var items = new List<ImportItemSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var itemId = DbGuid.Parse(reader.GetString(0));
            var candidateMediaId = reader.IsDBNull(2) ? (Guid?)null : DbGuid.Parse(reader.GetString(2));
            var reusedMediaId = reader.IsDBNull(3) ? (Guid?)null : DbGuid.Parse(reader.GetString(3));
            var sourcePath = reader.GetString(4);
            var sourceFileName = reader.GetString(5);
            var byteLength = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6);
            var lastWrite = reader.IsDBNull(7) ? (DateTimeOffset?)null : DbTime.Parse(reader.GetInt64(7));
            var disposition = DbEnum.ParseItemDisposition(reader.GetString(8));
            var duplicateDecision = DbEnum.ParseDuplicateDecisionOrNull(
                reader.IsDBNull(9) ? null : reader.GetString(9));
            var cleanupState = DbEnum.ParseSourceCleanupState(reader.GetString(10));
            var cleanupError = reader.IsDBNull(11) ? null : reader.GetString(11);
            var cleanupPolicy = DbEnum.ParseImportCleanupPolicy(reader.GetString(12));
            var mediaType = reader.IsDBNull(13) ? null : (MediaType?)DbEnum.ParseMediaType(reader.GetString(13));
            var createdAt = DbTime.Parse(reader.GetInt64(14));
            var updatedAt = DbTime.Parse(reader.GetInt64(15));

            items.Add(new ImportItemSummary(
                itemId,
                unitId,
                candidateMediaId,
                reusedMediaId,
                sourcePath,
                sourceFileName,
                byteLength,
                lastWrite,
                disposition,
                duplicateDecision,
                cleanupState,
                cleanupError,
                mediaType,
                createdAt,
                updatedAt,
                cleanupPolicy));
        }

        return new PagedItemsResult(items, totalCount, offset, limit);
    }

    public async Task<IReadOnlyList<ProfileLookupResult>> SearchProfilesAsync(
        string? query = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(query))
        {
            cmd.CommandText = """
                SELECT p.profile_id, p.display_name, p.profile_storage_token, c.name AS category_name
                FROM profiles p
                LEFT JOIN categories c ON c.category_id = p.category_id
                WHERE p.kind = 'NORMAL' AND p.visibility = 'PUBLISHED' AND p.trashed_at_ms IS NULL
                ORDER BY p.display_name COLLATE NOCASE ASC
                LIMIT $limit;
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT p.profile_id, p.display_name, p.profile_storage_token, c.name AS category_name
                FROM profiles p
                LEFT JOIN categories c ON c.category_id = p.category_id
                WHERE p.kind = 'NORMAL'
                  AND p.visibility = 'PUBLISHED'
                  AND p.trashed_at_ms IS NULL
                  AND p.display_name LIKE $query
                ORDER BY p.display_name COLLATE NOCASE ASC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$query", $"%{query.Trim()}%");
        }
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var list = new List<ProfileLookupResult>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new ProfileLookupResult(
                DbGuid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return list;
    }

    public async Task<IReadOnlyList<VerificationFaceCandidate>> GetFaceCandidatesAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty) return [];

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT fd.face_id, fd.media_id, ii.import_item_id, ii.source_file_name, fd.row_version,
                   p.display_name AS suggested_profile_name, p.profile_id AS suggested_profile_id,
                    fd.confidence, fd.decision_state, fd.suggested_candidates_json,
                    fd.suggested_identity_id
            FROM face_detections fd
            JOIN import_items ii ON fd.media_id = COALESCE(ii.reused_media_id, ii.candidate_media_id)
             LEFT JOIN identities id ON fd.suggested_identity_id = id.identity_id AND id.is_active = 1
             LEFT JOIN profiles p ON id.profile_id = p.profile_id AND p.trashed_at_ms IS NULL
            WHERE ii.import_unit_id = $unitId
              AND ii.disposition IN ('INCLUDED','REUSED')
            ORDER BY ii.created_at_ms ASC, fd.created_at_ms ASC;
            """;
        cmd.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var list = new List<VerificationFaceCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var suggestion = FaceSuggestionEvidenceV1.TryParse(reader.IsDBNull(9) ? null : reader.GetString(9));
            var suggestedIdentityId = reader.IsDBNull(10) ? (Guid?)null : DbGuid.Parse(reader.GetString(10));
            var similarity = suggestion?.Candidates
                .FirstOrDefault(candidate => candidate.IdentityId == suggestedIdentityId)?.Similarity;
            list.Add(new VerificationFaceCandidate(
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1)),
                DbGuid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : DbGuid.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.GetString(8),
                similarity));
        }

        return list;
    }
}
