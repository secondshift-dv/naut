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
using System.Text.Json;

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

    public Task<long> UpdateDraftAsync(
        Guid unitId,
        VerificationDraftV1 draft,
        long expectedRowVersion,
        CancellationToken cancellationToken = default) =>
        UpdateDraftCoreAsync(unitId, draft, expectedRowVersion, null, cancellationToken);

    internal async Task<long> UpdateReviewDraftAsync(
        Guid unitId,
        VerificationDraftV1 draft,
        long expectedRowVersion,
        IReadOnlyList<ReviewItemMutation> changes,
        CancellationToken cancellationToken)
    {
        await using var unitLease = await _catalog.ImportUnitMutations.EnterAsync(unitId, cancellationToken).ConfigureAwait(false);
        return await UpdateDraftCoreAsync(unitId, draft, expectedRowVersion, changes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> UpdateDraftCoreAsync(
        Guid unitId,
        VerificationDraftV1 draft,
        long expectedRowVersion,
        IReadOnlyList<ReviewItemMutation>? reviewChanges,
        CancellationToken cancellationToken)
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
            "SELECT destination_kind, destination_profile_id, commit_operation_id, library_commit_state, state FROM import_units WHERE import_unit_id = $unitId;"))
        {
            currentUnit.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var currentReader = await currentUnit.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await currentReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new CatalogInvariantException($"ImportUnit {unitId:D} does not exist.");
            }

            if (reviewChanges is not null
                && !DbEnum.ParseImportUnitState(currentReader.GetString(4)).CanCancelUnpublished())
                throw new CatalogConcurrencyConflictException("The import is no longer available for Review.");

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

        if (reviewChanges is { Count: > 0 })
        {
            // The dispositions and the Review draft commit together; a crash cannot leave a
            // newly skipped item with an obsolete appearance or ownership decision.
            await using var review = transaction.CreateCommand(
                $"""
                WITH changes AS (
                    SELECT json_extract(value, '$.id') AS id,
                           json_extract(value, '$.decision') AS decision,
                           json_extract(value, '$.reuse') AS reuse
                    FROM json_each($changes)
                )
                UPDATE import_items AS item
                SET disposition = CASE WHEN change.decision = 'REUSE' THEN 'REUSED' ELSE 'SKIPPED' END,
                    duplicate_decision = CASE WHEN change.decision = 'REUSE' THEN 'REUSE'
                        WHEN change.decision = 'SKIP' OR item.duplicate_decision IS NOT NULL THEN 'SKIP' ELSE NULL END,
                    reused_media_id = change.reuse,
                    updated_at_ms = $now
                FROM changes AS change
                WHERE item.import_item_id = change.id AND item.import_unit_id = $unitId
                  AND item.disposition IN ('INCLUDED','REUSED')
                  AND (change.decision IS NOT 'REUSE'
                       OR (item.duplicate_decision IS NULL AND EXISTS (
                           SELECT 1 FROM media c JOIN media a ON {ImportReads.ExactMediaIdentitySql}
                               AND ({ImportReads.PublishedReuseAuthoritySql})
                           WHERE c.media_id = item.candidate_media_id AND a.media_id = change.reuse AND a.media_id <> c.media_id
                       )));
                """);
            review.Parameters.AddWithValue("$changes", JsonSerializer.Serialize(reviewChanges.Select(change => new
            {
                id = DbGuid.Format(change.ItemId),
                decision = change.Decision is { } decision ? DbEnum.Format(decision) : null,
                reuse = change.ReusedMediaId is { } mediaId ? DbGuid.Format(mediaId) : null,
            })));
            review.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            review.Parameters.AddWithValue("$now", nowMs);
            if (await review.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != reviewChanges.Count)
                throw new CatalogConcurrencyConflictException("One or more Review items have changed. Refresh and try again.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expectedRowVersion + 1;
    }

    internal sealed record ReviewItemMutation(Guid ItemId, DuplicateDecision? Decision, Guid? ReusedMediaId = null);

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
        var itemMetaMap = new Dictionary<Guid, (Guid? ImportItemId, bool IsFromUnit)>();
        var excludedMedia = new HashSet<Guid>();
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COALESCE(reused_media_id,candidate_media_id),import_item_id FROM import_items
                WHERE import_unit_id=$unit AND disposition IN ('INCLUDED','REUSED') ORDER BY created_at_ms,import_item_id;
                SELECT media_id FROM profile_media WHERE publication_import_unit_id=$unit;
                """;
            command.Parameters.AddWithValue("$unit", DbGuid.Format(unitId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                if (!reader.IsDBNull(0)) itemMetaMap.TryAdd(DbGuid.Parse(reader.GetString(0)), (DbGuid.Parse(reader.GetString(1)), true));
            if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var mediaId = DbGuid.Parse(reader.GetString(0));
                    if (!itemMetaMap.ContainsKey(mediaId)) excludedMedia.Add(mediaId);
                }
            }
        }
        var ranked = model.DestinationProfileId is { } profileId
            ? await new ProfileMediaCandidateReads(_catalog).ReadAsync(profileId, cancellationToken, unitId, includePendingBanner: true).ConfigureAwait(false)
            : new ProfileMediaCandidates([], []);
        string? Preview(ProfileMediaCandidate candidate)
        {
            try
            {
                var path = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, candidate.RelativePath);
                return File.Exists(path) ? path : null;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        var covers = ranked.Covers.Where(candidate => !excludedMedia.Contains(candidate.MediaId)).Select(candidate => new VerificationCoverCandidate(
            candidate.MediaAssetId.ToString("N"), candidate.MediaId,
            itemMetaMap.GetValueOrDefault(candidate.MediaId).ImportItemId,
            candidate.DisplayName, candidate.MediaType,
            candidate.SourceTimestampMs.HasValue ? CoverVisualSourceKind.VideoFrame : CoverVisualSourceKind.Image,
            candidate.SourceTimestampMs,
            itemMetaMap.GetValueOrDefault(candidate.MediaId).IsFromUnit,
            candidate.Rank, candidate.Rank == 1,
            "Ranked Media Thumbnail", Preview(candidate))).ToArray();

        var banners = new List<VerificationBannerCandidate>();
        foreach (var candidate in ranked.BannerHovers.Where(candidate => !excludedMedia.Contains(candidate.MediaId)))
        {
            var ready = model.DestinationProfileId is { } target && await new ProfileBannerOperations(_catalog)
                .ResolveRenditionAsync(target, candidate.MediaId, unitId, cancellationToken).ConfigureAwait(false) is not null;
            banners.Add(new VerificationBannerCandidate(candidate.MediaAssetId.ToString("N"), candidate.MediaId,
                itemMetaMap.GetValueOrDefault(candidate.MediaId).ImportItemId, candidate.DisplayName, candidate.MediaType,
                itemMetaMap.GetValueOrDefault(candidate.MediaId).IsFromUnit, candidate.Rank, candidate.Rank == 1,
                null, null, ready));
        }

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

    public Task SetBatchItemDispositionsAsync(
        IReadOnlyList<Guid> importItemIds,
        ItemDisposition disposition,
        CancellationToken cancellationToken = default) =>
        SetBatchItemDispositionsAsync(importItemIds, disposition, decision: null, cancellationToken);

    internal Task SkipDuplicateItemsAsync(IReadOnlyList<Guid> importItemIds, CancellationToken cancellationToken) =>
        SetBatchItemDispositionsAsync(importItemIds, ItemDisposition.Skipped, DuplicateDecision.Skip, cancellationToken);

    private async Task SetBatchItemDispositionsAsync(
        IReadOnlyList<Guid> importItemIds, ItemDisposition disposition, DuplicateDecision? decision,
        CancellationToken cancellationToken)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetBatchItemDispositionsAsync));
        ArgumentNullException.ThrowIfNull(importItemIds);
        if (importItemIds.Count == 0) return;

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var command = transaction.CreateCommand(
            """
            UPDATE import_items
            SET disposition = $disposition,
                duplicate_decision = COALESCE($decision, duplicate_decision),
                reused_media_id = CASE WHEN $decision = 'SKIP' THEN NULL ELSE reused_media_id END,
                updated_at_ms = $now
            WHERE import_item_id IN (SELECT value FROM json_each($ids))
              AND ($decision IS NULL OR (disposition = 'INCLUDED' AND duplicate_decision IS NULL));
            """);
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(importItemIds.Select(DbGuid.Format)));
        command.Parameters.AddWithValue("$disposition", DbEnum.Format(disposition));
        command.Parameters.AddWithValue("$decision", decision is { } value ? DbEnum.Format(value) : DBNull.Value);
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    public Task<VerificationGateProblems> GetGateProblemsAsync(
        Guid unitId,
        VerificationDraftV1 draft,
        CancellationToken cancellationToken = default) =>
        GetGateProblemsAsync(unitId, draft, matches: null, cancellationToken);

    internal async Task<VerificationGateProblems> GetGateProblemsAsync(
        Guid unitId,
        VerificationDraftV1 draft,
        IReadOnlyList<ImportDuplicateMatch>? matches,
        CancellationToken cancellationToken)
    {
        if (unitId == Guid.Empty)
        {
            return VerificationGateProblems.Empty;
        }

        var duplicateDecisions = (draft.DuplicateDecisions ?? [])
            .Select(static decision => decision.ImportItemId)
            .ToHashSet();
        matches ??= await GetExactDuplicateMatchesAsync(unitId, cancellationToken).ConfigureAwait(false);
        var unresolved = matches
            .Where(match => match.RequiresDecision && !duplicateDecisions.Contains(match.ImportItemId))
            .DistinctBy(static match => match.ImportItemId)
            .ToArray();
        var details = await ReadDuplicateDetailsAsync(unresolved, cancellationToken).ConfigureAwait(false);
        var duplicates = unresolved.Select(match =>
        {
            var detail = details.GetValueOrDefault(match.MatchedMediaId);
            return new VerificationDuplicateProblem(
                match.ImportItemId,
                match.MatchedMediaId,
                match.SourceFileName,
                detail.OwnerName,
                detail.PreviewPath);
        }).ToArray();

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
            .ToDictionary(static problem => problem.ImportItemId);
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT item.import_item_id, item.source_file_name
                FROM import_items item JOIN media asset ON asset.media_id = item.candidate_media_id
                WHERE item.import_unit_id = $unit AND item.disposition IN ('INCLUDED','REUSED')
                  AND asset.dependency_status = 'DEPENDENCIES_MISSING';
                """;
            command.Parameters.AddWithValue("$unit", DbGuid.Format(unitId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var itemId = DbGuid.Parse(reader.GetString(0));
                if (draft.AcknowledgedMissingDependencyItemIds?.Contains(itemId) == true)
                    continue;
                preparation[itemId] = preparation.TryGetValue(itemId, out var problem)
                    ? problem with { MissingDependencies = true }
                    : new VerificationPreparationProblem(itemId, reader.GetString(1), [], MissingDependencies: true);
            }
        }
        var verification = await LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false);
        var pending = verification is null
            || verification.State is not (ImportUnitState.ReadyForVerification or ImportUnitState.Committing)
            || !await new CapabilityReads(_catalog)
                .AreAllRequiredCapabilitiesSettledAsync(unitId, cancellationToken)
                .ConfigureAwait(false);

        return new VerificationGateProblems(duplicates, collisions,
            preparation.Values.OrderBy(static problem => problem.SourceFileName, StringComparer.CurrentCultureIgnoreCase).ToArray(), pending);
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

    private async Task<Dictionary<Guid, (string? OwnerName, string? PreviewPath)>> ReadDuplicateDetailsAsync(
        IReadOnlyList<ImportDuplicateMatch> matches,
        CancellationToken cancellationToken)
    {
        var details = new Dictionary<Guid, (string?, string?)>();
        if (matches.Count == 0) return details;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT asset.media_id, owner.display_name, thumbnail.relative_path
            FROM json_each($assetIds) ids
            JOIN media asset ON asset.media_id = ids.value AND asset.state = 'ACTIVE'
            LEFT JOIN profile_media ownership ON ownership.media_id = asset.media_id
              AND ownership.relation_type = 'OWNER' AND ownership.publication_import_unit_id IS NULL
            LEFT JOIN profiles owner ON owner.profile_id = ownership.profile_id
              AND owner.kind = 'NORMAL' AND owner.trashed_at_ms IS NULL
            LEFT JOIN media_assets thumbnail ON thumbnail.media_id = asset.media_id
              AND thumbnail.role = 'THUMBNAIL' AND thumbnail.state = 'READY';
            """;
        command.Parameters.AddWithValue("$assetIds", JsonSerializer.Serialize(
            matches.Select(static match => DbGuid.Format(match.MatchedMediaId)).Distinct()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string? preview = null;
            if (!reader.IsDBNull(2))
            {
                try
                {
                    preview = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, reader.GetString(2));
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    // A stale thumbnail leaves the review card usable.
                }
            }

            details[DbGuid.Parse(reader.GetString(0))] = (reader.IsDBNull(1) ? null : reader.GetString(1), preview);
        }

        return details;
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
                   p.profile_id, p.display_name, fd.embedding_space_key
            FROM import_items ii
            JOIN face_detections fd ON fd.media_id = COALESCE(ii.reused_media_id, ii.candidate_media_id)
            JOIN identities identity ON identity.identity_id = fd.suggested_identity_id AND identity.is_active = 1
            JOIN profiles p ON p.profile_id = identity.profile_id
            WHERE ii.import_unit_id = $unitId
              AND ii.disposition IN ('INCLUDED','REUSED')
              AND fd.decision_state = 'SUGGESTED'
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
            var recommendation = await AutomaticMediaAssignmentPolicy.GetStrongFaceSuggestionAsync(
                _catalog, parsed, reader.IsDBNull(7) ? null : reader.GetString(7),
                identityId, DbGuid.Parse(reader.GetString(5)), cancellationToken).ConfigureAwait(false);
            if (recommendation is null)
            {
                continue;
            }

            evidence.Add((
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1)),
                reader.GetString(2),
                DbGuid.Parse(reader.GetString(5)),
                reader.GetString(6),
                recommendation.Similarity));
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

    internal async Task<string?> ReadActiveDestinationNameAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT display_name FROM profiles
            WHERE profile_id = $id AND kind = 'NORMAL' AND visibility = 'PUBLISHED' AND trashed_at_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$id", DbGuid.Format(profileId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
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
            var recommendation = suggestion?.GetRecommendation();
            var showSuggestion = reader.GetString(8) == "SUGGESTED"
                && recommendation is not null && recommendation.IdentityId == suggestedIdentityId;
            list.Add(new VerificationFaceCandidate(
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1)),
                DbGuid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt64(4),
                !showSuggestion || reader.IsDBNull(5) ? null : reader.GetString(5),
                !showSuggestion || reader.IsDBNull(6) ? null : DbGuid.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.GetString(8),
                showSuggestion ? recommendation!.Similarity : null));
        }

        return list;
    }
}
