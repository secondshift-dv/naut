using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Faces;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.RelatedProfiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Trash;

namespace Neuterradise.App.Import.Verification;

/// <summary>
/// Publication authority. Canonical commit owns authoritative Media bytes; media preparation owns
/// permanent MediaAssets. This is the only boundary that exposes an ImportUnit as published product state.
/// </summary>
public sealed class ImportPublicationCoordinator
{
    private readonly CatalogDb _catalog;
    private readonly VerificationOperations _verification;
    private readonly VerificationValidator _validator;
    private readonly TrashCoordinator _trash;
    private readonly TimeProvider _timeProvider;

    public ImportPublicationCoordinator(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _verification = new VerificationOperations(catalog, _timeProvider);
        _validator = new VerificationValidator(catalog, _timeProvider);
        _trash = CreateTrashCoordinator(catalog);
    }

    /// <summary>
    /// Persists the final Verify draft without changing the materialized destination, then converges
    /// the durable publication operation. Save/Continue never copies source bytes again.
    /// </summary>
    public async Task<ImportPublicationResult> PublishAsync(
        Guid unitId,
        VerificationDraftV1 finalDraft,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(PublishAsync));
        ArgumentNullException.ThrowIfNull(finalDraft);
        if (unitId == Guid.Empty)
        {
            return ImportPublicationResult.Blocked("UNIT_NOT_FOUND", "That import no longer exists.");
        }

        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var model = await _verification.LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                return ImportPublicationResult.Blocked("UNIT_NOT_FOUND", "That import no longer exists.");
            }

            var existing = VerificationDraftV1.FromJson(
                model.VerificationDraftJson,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            var durableDraft = finalDraft with
            {
                CurrentStep = 5,
                ImportRequested = true,
                AttentionItemIds = existing.AttentionItemIds ?? finalDraft.AttentionItemIds,
            };

            try
            {
                await _catalog.ImportWrites.UpdateVerificationDraftAsync(
                    unitId,
                    durableDraft.CurrentStep,
                    durableDraft.ToJson(),
                    durableDraft.SchemaVersion,
                    model.RowVersion,
                    cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (CatalogConcurrencyConflictException) when (attempt < 2)
            {
            }
        }

        return await PublishDurableAsync(unitId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Restart-safe publication. COMMITTING means Save was accepted and publication must converge;
    /// re-entry repeats only idempotent/durably-proven steps.
    /// </summary>
    public async Task<ImportPublicationResult> PublishDurableAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        var model = await _verification.LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return ImportPublicationResult.Blocked("UNIT_NOT_FOUND", "That import no longer exists.");
        }

        if (model.State.IsUnitCommitted())
        {
            return ImportPublicationResult.AlreadyPublished();
        }

        if (model.State is ImportUnitState.Cancelled or ImportUnitState.FailedTerminal)
        {
            return ImportPublicationResult.Blocked("IMPORT_NOT_PUBLISHABLE", "This import can no longer be published.");
        }

        if (model.State is not (ImportUnitState.ReadyForVerification or ImportUnitState.Committing))
        {
            return ImportPublicationResult.Blocked(
                "IMPORT_NOT_READY_FOR_PUBLICATION",
                "This import is still preparing. Review becomes publishable when preparation is complete.");
        }

        var draft = VerificationDraftV1.FromJson(
            model.VerificationDraftJson,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var destination = await ReadDurableDestinationAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (destination is null || destination.ProfileId == Guid.Empty)
        {
            return ImportPublicationResult.Pending("PUBLICATION_DESTINATION_UNAVAILABLE", "The prepared Profile destination could not be resolved yet.");
        }

        var destinationBlocker = ValidateDestinationAuthority(draft, destination);
        if (destinationBlocker is not null)
        {
            return destinationBlocker;
        }

        if (model.State == ImportUnitState.ReadyForVerification)
        {
            var readiness = await _validator.ValidateCommitReadinessAsync(unitId, cancellationToken).ConfigureAwait(false);
            if (!readiness.IsReady)
            {
                return ImportPublicationResult.Blocked(
                    readiness.Blockers.FirstOrDefault()?.Code ?? "VERIFY_BLOCKED",
                    "Verify still has unresolved decisions.");
            }

            await _catalog.ImportWrites.UpdateUnitStateAsync(
                unitId,
                ImportUnitState.Committing,
                expectedState: ImportUnitState.ReadyForVerification,
                expectedRowVersion: model.RowVersion,
                cancellationToken).ConfigureAwait(false);
        }

        // Ownership choices can change the Stage-1 delta, but the relation remains unpublished until
        // the core publication transaction clears its Unit marker.
        var collisionResult = await ApplyCollisionDecisionsAsync(
            unitId,
            destination.ProfileId,
            draft,
            cancellationToken).ConfigureAwait(false);
        if (!collisionResult.IsSuccess)
        {
            return collisionResult;
        }

        try
        {
            await ApplyCorePublicationAsync(unitId, destination, draft, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Import publication core remains pending for {0:D}: {1}",
                unitId,
                exception.GetType().Name);
            return ImportPublicationResult.Pending(
                "PUBLICATION_CORE_RETRY_REQUIRED",
                "Your Save is durable, but publication still needs recovery.");
        }

        // Face choices are deliberately after the core boundary: APPEARS/evidence/identity samples
        // must never leak into a published Profile before Save. They are idempotent on re-entry.
        var faceResult = await ApplyFaceDecisionsAsync(destination.ProfileId, draft, cancellationToken).ConfigureAwait(false);
        if (!faceResult.IsSuccess)
        {
            return faceResult;
        }

        // Related evidence is a published projection. Build it only after the core transaction has
        // exposed this unit's relations/Profile and after staged face decisions have become durable.
        // Re-entry is safe because evidence keys are deterministic and RelatedWrites is idempotent.
        try
        {
            var related = new RelatedEvidenceProjector(_catalog, _timeProvider);
            foreach (var assetId in await ReadUnitPublishedMediaIdsAsync(unitId, cancellationToken).ConfigureAwait(false))
            {
                await related.ProjectSharedMediaEvidenceForMediaAsync(assetId, cancellationToken).ConfigureAwait(false);
                await related.ProjectConfirmedFaceEvidenceForMediaAsync(assetId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Published related projection remains pending for {0:D}: {1}",
                unitId,
                exception.GetType().Name);
            return ImportPublicationResult.Pending(
                "RELATED_PUBLICATION_PENDING",
                "The Profile is published; related-media projection still needs recovery.");
        }

        ImportCommitResult cleanup;
        try
        {
            cleanup = await ImportFinalizer.CreateDefaultCommitCoordinator(_catalog)
                .CommitAsync(unitId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Published import cleanup remains pending for {0:D}: {1}",
                unitId,
                exception.GetType().Name);
            return ImportPublicationResult.Pending(
                "PUBLICATION_CLEANUP_RETRY_REQUIRED",
                "The Profile is published; source cleanup will be recovered.");
        }

        var finalState = cleanup.SourceCleanupAttentionItemIds.Count > 0
            ? ImportUnitState.CommittedWithCleanupAttention
            : cleanup.Checkpoint >= ImportCommitCheckpoint.SourceCleanupComplete
                ? ImportUnitState.Completed
                : ImportUnitState.Committed;
        await _catalog.ImportWrites.UpdateUnitStateAsync(unitId, finalState, cancellationToken).ConfigureAwait(false);

        return finalState == ImportUnitState.CommittedWithCleanupAttention
            ? ImportPublicationResult.PublishedWithAttention(destination.ProfileId)
            : ImportPublicationResult.Published(destination.ProfileId);
    }

    private async Task<ImportPublicationResult> ApplyCollisionDecisionsAsync(
        Guid unitId,
        Guid destinationProfileId,
        VerificationDraftV1 draft,
        CancellationToken cancellationToken)
    {
        foreach (var decision in draft.ProfileCollisionDecisions ?? [])
        {
            if (decision.Action == ProfileCollisionAction.KeepDestination)
            {
                continue;
            }

            var item = await ReadCollisionItemAsync(unitId, decision.ImportItemId, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                return ImportPublicationResult.Blocked("COLLISION_ITEM_NOT_FOUND", "One Verify ownership decision no longer matches this import.");
            }

            if (decision.Action == ProfileCollisionAction.Skip)
            {
                if (item.ReusedMediaId is { } reusedId)
                {
                    await DeleteImportRelationAsync(unitId, destinationProfileId, reusedId, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (item.CandidateMediaId is not { } candidateId)
                {
                    continue;
                }

                var state = await ReadMediaStateAsync(candidateId, cancellationToken).ConfigureAwait(false);
                if (state is null || state.Value.State is MediaState.Trashed or MediaState.Retired)
                {
                    continue;
                }
                if (state.Value.State != MediaState.Active)
                {
                    return ImportPublicationResult.Pending("COLLISION_SKIP_ASSET_NOT_SETTLED", "A skipped media item has not reached a safe Vault state yet.");
                }

                var plan = await _trash.PrepareMediaTrashAsync(candidateId, cancellationToken).ConfigureAwait(false);
                if (!plan.IsSuccess || plan.Value is null)
                {
                    return ImportPublicationResult.Pending("COLLISION_SKIP_TRASH_PENDING", "A skipped media item still needs recoverable Trash disposition.");
                }
                var executed = await _trash.ExecuteMediaTrashAsync(plan.Value, cancellationToken).ConfigureAwait(false);
                if (!executed.IsSuccess)
                {
                    return ImportPublicationResult.Pending("COLLISION_SKIP_TRASH_PENDING", "A skipped media item still needs recoverable Trash disposition.");
                }
                continue;
            }

            if (decision.Action == ProfileCollisionAction.MoveToProfile)
            {
                if (decision.TargetProfileId is not { } targetId || targetId == Guid.Empty)
                {
                    return ImportPublicationResult.Blocked("COLLISION_MOVE_TARGET_REQUIRED", "A moved media item needs a destination Profile.");
                }
                if (item.ReusedMediaId is not null || item.CandidateMediaId is not { } candidateId)
                {
                    return ImportPublicationResult.Blocked("COLLISION_REUSED_MOVE_UNSUPPORTED", "Existing shared media cannot have its primary owner changed by this import.");
                }

                var state = await ReadMediaStateAsync(candidateId, cancellationToken).ConfigureAwait(false);
                if (state is null || state.Value.State != MediaState.Active)
                {
                    return ImportPublicationResult.Pending("COLLISION_MOVE_ASSET_NOT_ACTIVE", "A moved media item is not ready for ownership publication yet.");
                }
                if (state.Value.OwnerProfileId == targetId)
                {
                    continue;
                }

                var moved = await new MediaOperations(_catalog).ChangePrimaryProfileAsync(
                    new ChangePrimaryProfileRequest(candidateId, targetId, state.Value.RowVersion),
                    cancellationToken).ConfigureAwait(false);
                if (!moved.IsSuccess)
                {
                    return ImportPublicationResult.Pending("COLLISION_MOVE_PENDING", "A media ownership move still needs recovery.");
                }
            }
        }

        return ImportPublicationResult.StepSucceeded();
    }

    private async Task ApplyCorePublicationAsync(
        Guid unitId,
        DurableDestination destination,
        VerificationDraftV1 draft,
        CancellationToken cancellationToken)
    {
        var initialCandidates = destination.Kind == DestinationKind.NewNormal
            ? await new ProfileMediaCandidateReads(_catalog).ReadAsync(destination.ProfileId, cancellationToken, unitId)
                .ConfigureAwait(false)
            : null;
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);
        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var profileId = destination.ProfileId;

        if (destination.Kind == DestinationKind.NewNormal)
        {
            if (initialCandidates?.Covers.Count is not > 0)
                throw new CatalogInvariantException("A new Profile requires a ready Media Thumbnail before publication.");
            var profile = draft.Destination.NewProfile
                ?? throw new CatalogInvariantException("A new Profile publication lost its final draft metadata.");
            await using (var update = transaction.CreateCommand(
                """
                UPDATE profiles
                SET display_name = $name,
                    category_id = $categoryId,
                    rating = $rating,
                    is_favorite = $favorite,
                    overview = $overview,
                    updated_at_ms = $now,
                    row_version = row_version + 1
                WHERE profile_id = $profileId
                  AND kind = 'NORMAL'
                  AND trashed_at_ms IS NULL;
                """))
            {
                update.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                update.Parameters.AddWithValue("$name", profile.DisplayName.Trim());
                update.Parameters.AddWithValue("$categoryId", (object?)profile.CategoryId ?? DBNull.Value);
                update.Parameters.AddWithValue("$rating", (object?)profile.Rating ?? DBNull.Value);
                update.Parameters.AddWithValue("$favorite", profile.Favorite == true ? 1 : 0);
                update.Parameters.AddWithValue("$overview", (object?)profile.Overview ?? DBNull.Value);
                update.Parameters.AddWithValue("$now", now);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new CatalogInvariantException("The draft Profile is no longer publishable.");
                }
            }

            await using (var clearTags = transaction.CreateCommand("DELETE FROM profile_tags WHERE profile_id = $profileId;"))
            {
                clearTags.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                await clearTags.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var tagId in (profile.TagIds ?? []).Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
            {
                await using var insert = transaction.CreateCommand(
                    "INSERT INTO profile_tags(profile_id, tag_id, created_at_ms) VALUES ($profileId, $tagId, $now);");
                insert.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                insert.Parameters.AddWithValue("$tagId", tagId.Trim());
                insert.Parameters.AddWithValue("$now", now);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // This UPDATE is the media-delta publication boundary for existing Profiles. Every public
        // profile/media read excludes rows carrying this marker.
        await using (var publishRelations = transaction.CreateCommand(
            """
            UPDATE profile_media
            SET publication_import_unit_id = NULL
            WHERE publication_import_unit_id = $unitId;
            """))
        {
            publishRelations.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await publishRelations.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ApplyAppearanceInTransactionAsync(transaction, profileId, draft.Appearance, now, cancellationToken).ConfigureAwait(false);

        if (destination.Kind == DestinationKind.NewNormal && initialCandidates is not null
            && draft.Appearance.CoverIntent == VerificationAppearanceIntent.Unchanged)
        {
            var cover = initialCandidates.Covers[0];
            var banner = initialCandidates.BannerHovers
                .OrderByDescending(static candidate => candidate.Score)
                .ThenBy(static candidate => candidate.MediaAssetId)
                .FirstOrDefault();
            await using var select = transaction.CreateCommand("""
                UPDATE profile_appearance SET cover_media_asset_id = $cover,
                    banner_media_asset_id = $banner
                WHERE profile_id = $profile AND cover_media_asset_id IS NULL;
                """);
            select.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
            select.Parameters.AddWithValue("$cover", DbGuid.Format(cover.MediaAssetId));
            select.Parameters.AddWithValue("$banner", banner is null
                ? DBNull.Value : DbGuid.Format(banner.MediaAssetId));
            await select.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using var source = transaction.CreateCommand("""
                UPDATE profiles
                SET cover_media_id = $coverMedia
                WHERE profile_id = $profile AND cover_media_id IS NULL;
                """);
            source.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
            source.Parameters.AddWithValue("$coverMedia", DbGuid.Format(cover.MediaId));
            await source.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (destination.Kind == DestinationKind.NewNormal)
        {
            await using var publishProfile = transaction.CreateCommand(
                """
                UPDATE profiles
                SET visibility = 'PUBLISHED', updated_at_ms = $now, row_version = row_version + 1
                WHERE profile_id = $profileId AND visibility = 'DRAFT';
                """);
            publishProfile.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            publishProfile.Parameters.AddWithValue("$now", now);
            await publishProfile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var mark = transaction.CreateCommand(
            """
            UPDATE import_units
            SET verification_step = 5,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_unit_id = $unitId AND state = 'COMMITTING';
            """))
        {
            mark.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            mark.Parameters.AddWithValue("$now", now);
            await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Read published asset IDs for this import unit before committing.
        var publishedMediaIds = new List<Guid>();
        await using (var readMedias = transaction.CreateCommand(
            """
            SELECT DISTINCT COALESCE(reused_media_id, candidate_media_id)
            FROM import_items
            WHERE import_unit_id = $unitId
              AND disposition IN ('INCLUDED','REUSED')
              AND COALESCE(reused_media_id, candidate_media_id) IS NOT NULL;
            """))
        {
            readMedias.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var reader = await readMedias.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    publishedMediaIds.Add(DbGuid.Parse(reader.GetString(0)));
                }
            }
        }

        // Queue public domain invalidations at the publication publication boundary.
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [profileId],
            CatalogInvalidationDomain.Profile,
            0));
        if (publishedMediaIds.Count > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                publishedMediaIds,
                CatalogInvalidationDomain.Media,
                0));
        }
        // Any Set or Clear appearance intent changes the rendered Profile and must invalidate Appearance.
        if (draft.Appearance.CoverIntent != VerificationAppearanceIntent.Unchanged
            || draft.Appearance.BannerIntent != VerificationAppearanceIntent.Unchanged)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [profileId],
                CatalogInvalidationDomain.Appearance,
                0));
        }
        transaction.QueueInvalidation(CatalogInvalidationDomain.Health);
        transaction.QueueInvalidation(CatalogInvalidationDomain.Activity);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAppearanceInTransactionAsync(
        CatalogTransaction transaction,
        Guid profileId,
        VerificationAppearanceDraft appearance,
        long now,
        CancellationToken cancellationToken)
    {
        // Cover and Banner publication follows the explicit three-state appearance intent.
        var applyCover = appearance.CoverIntent != VerificationAppearanceIntent.Unchanged;
        var applyBanner = appearance.BannerIntent != VerificationAppearanceIntent.Unchanged;
        if (!applyCover && !applyBanner)
        {
            return;
        }

        // A Set intent is invalid without an authoritative asset id.
        if (appearance.CoverIntent == VerificationAppearanceIntent.Set
            && (appearance.CoverMediaId is null || appearance.CoverMediaId.Value == Guid.Empty))
        {
            throw new CatalogInvariantException("Cover intent is SET but no valid Cover asset ID is present.");
        }
        if (appearance.BannerIntent == VerificationAppearanceIntent.Set
            && (appearance.BannerMediaId is null || appearance.BannerMediaId.Value == Guid.Empty))
        {
            throw new CatalogInvariantException("Banner intent is SET but no valid Banner asset ID is present.");
        }
        if (appearance.CoverIntent == VerificationAppearanceIntent.Set
            && appearance.CoverMediaAssetId is null)
            throw new CatalogInvariantException("A selected Cover must reference a ready Media Thumbnail.");
        if (appearance.BannerIntent == VerificationAppearanceIntent.Set
            && appearance.BannerMediaAssetId is null)
            throw new CatalogInvariantException("A selected Banner must reference a ready MediaAsset.");

        string? currentJson;
        await using (var read = transaction.CreateCommand(
            "SELECT overrides_json FROM profile_appearance WHERE profile_id = $profileId;"))
        {
            read.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            currentJson = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        var overrides = ProfileAppearanceOverrides.Parse(currentJson);
        if (appearance.CoverIntent == VerificationAppearanceIntent.Set)
        {
            overrides = overrides with
            {
                CoverSourceKind = appearance.CoverSourceKind ?? overrides.CoverSourceKind,
                CoverVideoTimestampMilliseconds = appearance.CoverVideoTimestampMilliseconds,
            };
        }
        else if (appearance.CoverIntent == VerificationAppearanceIntent.Clear)
        {
            // Clearing Cover also clears source-specific metadata that no longer has an owner.
            overrides = overrides with
            {
                CoverSourceKind = null,
                CoverVideoTimestampMilliseconds = null,
            };
        }

        // Persist the resolved appearance intent atomically with the Profile update.
        // Set: write new asset ID. Clear: write NULL. Unchanged: leave as-is.
        object coverIdValue = appearance.CoverIntent == VerificationAppearanceIntent.Set
            ? DbGuid.Format(appearance.CoverMediaId!.Value)
            : DBNull.Value;
        await using (var sources = transaction.CreateCommand(
            """
            UPDATE profiles
            SET cover_media_id = CASE WHEN $coverIntent = 1 THEN $coverId WHEN $coverIntent = 2 THEN NULL ELSE cover_media_id END,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE profile_id = $profileId AND trashed_at_ms IS NULL;
            """))
        {
            sources.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            // 0 = Unchanged, 1 = Set, 2 = Clear
            sources.Parameters.AddWithValue("$coverIntent", (int)appearance.CoverIntent);
            sources.Parameters.AddWithValue("$coverId", coverIdValue);
            sources.Parameters.AddWithValue("$now", now);
            await sources.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var upsert = transaction.CreateCommand(
            """
            INSERT INTO profile_appearance(profile_id, schema_version, layout_preset_id, overrides_json, updated_at_ms, row_version)
            VALUES ($profileId, $schemaVersion, $layoutPresetId, $overrides, $now, 1)
            ON CONFLICT(profile_id) DO UPDATE SET
                schema_version = excluded.schema_version,
                overrides_json = excluded.overrides_json,
                updated_at_ms = excluded.updated_at_ms,
                row_version = profile_appearance.row_version + 1;
            """);
        upsert.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        upsert.Parameters.AddWithValue("$schemaVersion", ProfileAppearanceOverrides.SchemaVersion);
        upsert.Parameters.AddWithValue("$layoutPresetId", ProfileLayoutResolver.FallbackPresetId);
        upsert.Parameters.AddWithValue("$overrides", overrides.ToJson());
        upsert.Parameters.AddWithValue("$now", now);
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var selection = transaction.CreateCommand("""
            UPDATE profile_appearance SET
                cover_media_asset_id = CASE WHEN $coverIntent = 1 THEN $cover
                    WHEN $coverIntent = 2 THEN NULL ELSE cover_media_asset_id END,
                banner_media_asset_id = CASE WHEN $bannerIntent = 1 THEN $banner
                    WHEN $bannerIntent = 2 THEN NULL ELSE banner_media_asset_id END
            WHERE profile_id = $profile;
            """);
        selection.Parameters.AddWithValue("$coverIntent", (int)appearance.CoverIntent);
        selection.Parameters.AddWithValue("$bannerIntent", (int)appearance.BannerIntent);
        selection.Parameters.AddWithValue("$cover", appearance.CoverMediaAssetId is { } coverArtifact
            ? DbGuid.Format(coverArtifact) : DBNull.Value);
        selection.Parameters.AddWithValue("$banner", appearance.BannerMediaAssetId is { } bannerArtifact
            ? DbGuid.Format(bannerArtifact) : DBNull.Value);
        selection.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
        await selection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImportPublicationResult> ApplyFaceDecisionsAsync(
        Guid destinationProfileId,
        VerificationDraftV1 draft,
        CancellationToken cancellationToken)
    {
        var operations = new FaceDecisionOperations(_catalog, _timeProvider);
        foreach (var decision in draft.FaceDecisions)
        {
            if (decision.Decision == VerificationFaceDecision.Reject)
            {
                var rejected = await operations.RejectFaceAsync(
                    new RejectFaceCommand(decision.FaceId, decision.ExpectedFaceRowVersion),
                    cancellationToken).ConfigureAwait(false);
                if (!rejected.IsSuccess)
                {
                    return ImportPublicationResult.Pending("FACE_PUBLICATION_PENDING", "A reviewed face decision still needs recovery.");
                }
                continue;
            }

            Guid targetProfileId;
            if (decision.TargetKind == VerificationFaceTargetKind.Destination)
            {
                targetProfileId = destinationProfileId;
            }
            else if (decision.TargetKind == VerificationFaceTargetKind.ExistingProfile
                && decision.TargetProfileId is { } existingTarget
                && existingTarget != Guid.Empty)
            {
                targetProfileId = existingTarget;
            }
            else
            {
                return ImportPublicationResult.Blocked("FACE_TARGET_REQUIRED", "A confirmed face needs a valid Profile target.");
            }

            var confirmed = await operations.ConfirmFaceAsync(
                new ConfirmFaceCommand(decision.FaceId, targetProfileId, decision.ExpectedFaceRowVersion),
                cancellationToken).ConfigureAwait(false);
            if (!confirmed.IsSuccess)
            {
                return ImportPublicationResult.Pending("FACE_PUBLICATION_PENDING", "A reviewed face decision still needs recovery.");
            }
        }

        return ImportPublicationResult.StepSucceeded();
    }

    private static ImportPublicationResult? ValidateDestinationAuthority(
        VerificationDraftV1 draft,
        DurableDestination destination)
    {
        if (draft.Destination.Kind != destination.Kind)
        {
            return ImportPublicationResult.Blocked(
                "DESTINATION_CHANGED_AFTER_COMMIT",
                "The destination Profile cannot be changed after canonical media preparation.");
        }

        if (destination.Kind == DestinationKind.ExistingNormal
            && draft.Destination.ProfileId != destination.ProfileId)
        {
            return ImportPublicationResult.Blocked(
                "DESTINATION_CHANGED_AFTER_COMMIT",
                "The destination Profile cannot be changed after canonical media preparation.");
        }

        return null;
    }

    private async Task<DurableDestination?> ReadDurableDestinationAsync(Guid unitId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT destination_kind, destination_profile_id FROM import_units WHERE import_unit_id = $unitId;";
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.IsDBNull(0)
            || reader.IsDBNull(1))
        {
            return null;
        }

        var kindText = reader.GetString(0);
        var kind = Enum.TryParse<DestinationKind>(kindText, ignoreCase: true, out var parsed)
            ? parsed
            : kindText.ToUpperInvariant() switch
            {
                "NEW_NORMAL" => DestinationKind.NewNormal,
                "EXISTING_NORMAL" => DestinationKind.ExistingNormal,
                "SYSTEM_UNKNOWN" => DestinationKind.SystemUnknown,
                _ => throw new CatalogInvariantException($"Unknown import destination kind '{kindText}'."),
            };
        return new DurableDestination(kind, DbGuid.Parse(reader.GetString(1)));
    }

    private async Task<IReadOnlyList<Guid>> ReadUnitPublishedMediaIdsAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT COALESCE(reused_media_id, candidate_media_id)
            FROM import_items
            WHERE import_unit_id = $unitId
              AND disposition IN ('INCLUDED','REUSED')
              AND COALESCE(reused_media_id, candidate_media_id) IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        var result = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(DbGuid.Parse(reader.GetString(0)));
        }
        return result;
    }

    private async Task<CollisionItem?> ReadCollisionItemAsync(
        Guid unitId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT candidate_media_id, reused_media_id
            FROM import_items
            WHERE import_unit_id = $unitId AND import_item_id = $itemId;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        command.Parameters.AddWithValue("$itemId", DbGuid.Format(itemId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return new CollisionItem(
            reader.IsDBNull(0) ? null : DbGuid.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : DbGuid.Parse(reader.GetString(1)));
    }

    private async Task<(MediaState State, Guid? OwnerProfileId, long RowVersion)?> ReadMediaStateAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT a.state,
                   (SELECT pa.profile_id FROM profile_media pa
                    WHERE pa.media_id = a.media_id AND pa.relation_type = 'OWNER' LIMIT 1),
                   a.row_version
            FROM media a
            WHERE a.media_id = $assetId;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return (
            DbEnum.ParseMediaState(reader.GetString(0)),
            reader.IsDBNull(1) ? null : DbGuid.Parse(reader.GetString(1)),
            reader.GetInt64(2));
    }

    private async Task DeleteImportRelationAsync(
        Guid unitId,
        Guid profileId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM profile_media
            WHERE profile_id = $profileId
              AND media_id = $assetId
              AND publication_import_unit_id = $unitId;
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TrashCoordinator CreateTrashCoordinator(CatalogDb catalog)
    {
        var volume = new WindowsVolumeIdentityProvider();
        var verifier = new ManagedFileVerifier();
        var moveExecutor = new ManagedMoveExecutor(catalog.Paths, volume, verifier, new MediaWrites(catalog));
        return new TrashCoordinator(catalog, moveExecutor, new MediaOperations(catalog));
    }

    private sealed record DurableDestination(DestinationKind Kind, Guid ProfileId);
    private sealed record CollisionItem(Guid? CandidateMediaId, Guid? ReusedMediaId);
}

public enum ImportPublicationStatus
{
    StepSucceeded,
    Published,
    PublishedWithAttention,
    AlreadyPublished,
    Blocked,
    PendingRecovery,
}

public sealed record ImportPublicationResult(
    ImportPublicationStatus Status,
    Guid? ProfileId,
    string? Code,
    string? UserMessage)
{
    public bool IsSuccess => Status is ImportPublicationStatus.StepSucceeded
        or ImportPublicationStatus.Published
        or ImportPublicationStatus.PublishedWithAttention
        or ImportPublicationStatus.AlreadyPublished;

    public bool IsPublished => Status is ImportPublicationStatus.Published
        or ImportPublicationStatus.PublishedWithAttention
        or ImportPublicationStatus.AlreadyPublished;

    public static ImportPublicationResult StepSucceeded() =>
        new(ImportPublicationStatus.StepSucceeded, null, null, null);

    public static ImportPublicationResult Published(Guid profileId) =>
        new(ImportPublicationStatus.Published, profileId, null, null);

    public static ImportPublicationResult PublishedWithAttention(Guid profileId) =>
        new(ImportPublicationStatus.PublishedWithAttention, profileId, "SOURCE_CLEANUP_ATTENTION", "Published; source cleanup still needs attention.");

    public static ImportPublicationResult AlreadyPublished() =>
        new(ImportPublicationStatus.AlreadyPublished, null, null, null);

    public static ImportPublicationResult Blocked(string code, string message) =>
        new(ImportPublicationStatus.Blocked, null, code, message);

    public static ImportPublicationResult Pending(string code, string message) =>
        new(ImportPublicationStatus.PendingRecovery, null, code, message);
}
