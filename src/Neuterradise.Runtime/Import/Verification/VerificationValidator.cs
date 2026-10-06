using System.Text.Json;
using Neuterradise.App.Media;
using Neuterradise.App.Faces;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.TimeAndIds;

using Neuterradise.App.Import;

namespace Neuterradise.App.Import.Verification;

public sealed class VerificationValidator
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public VerificationValidator(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public VerificationValidator(CatalogConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<VerificationBlocker> ValidateDraft(VerificationDraftV1 draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var blockers = new List<VerificationBlocker>();

        foreach (var destinationError in draft.ValidateDestination())
        {
            blockers.Add(new VerificationBlocker(null, destinationError, "The verification destination is incomplete or inconsistent."));
        }

        if (draft.SchemaVersion is < 1 or > VerificationDraftV1.CurrentSchemaVersion)
        {
            blockers.Add(new VerificationBlocker(null, "INVALID_SCHEMA_VERSION", $"Unsupported draft schema version: {draft.SchemaVersion}"));
        }

        if (draft.CurrentStep is < 1 or > 5)
        {
            blockers.Add(new VerificationBlocker(null, "INVALID_STEP", $"Step must be between 1 and 5, was {draft.CurrentStep}"));
        }

        if (draft.Appearance.CoverImportItemId.HasValue && draft.Appearance.CoverMediaId is null)
        {
            blockers.Add(new VerificationBlocker(null, "COVER_IMPORT_SELECTION_INCOMPLETE", "The selected Cover import item has no resolved asset."));
        }
        if (draft.Appearance.BannerImportItemId.HasValue && draft.Appearance.BannerMediaId is null)
        {
            blockers.Add(new VerificationBlocker(null, "BANNER_IMPORT_SELECTION_INCOMPLETE", "The selected Banner import item has no resolved asset."));
        }

        if (draft.Destination.Kind == DestinationKind.NewNormal)
        {
            if (draft.Destination.NewProfile is null || string.IsNullOrWhiteSpace(draft.Destination.NewProfile.DisplayName))
            {
                blockers.Add(new VerificationBlocker(null, "DISPLAY_NAME_REQUIRED", "Profile display name is required for New Normal destination."));
            }
            else if (draft.Destination.NewProfile.DisplayName.Trim().Length > 100)
            {
                blockers.Add(new VerificationBlocker(null, "DISPLAY_NAME_TOO_LONG", "Profile display name cannot exceed 100 characters."));
            }

            if (draft.Destination.ProfileId.HasValue)
            {
                blockers.Add(new VerificationBlocker(null, "INVALID_NEW_NORMAL_PROFILE_ID", "New Normal destination must not specify an existing ProfileId."));
            }

            if (draft.Destination.NewProfile?.Rating is < 0 or > 5)
            {
                blockers.Add(new VerificationBlocker(null, "INVALID_RATING", "Profile rating must be between 0 and 5."));
            }

            if (!ProfileRules.IsOverviewValid(draft.Destination.NewProfile?.Overview))
            {
                blockers.Add(new VerificationBlocker(
                    null,
                    "OVERVIEW_TOO_LONG",
                    $"Profile overview cannot exceed {ProfileRules.MaximumOverviewLength} characters."));
            }
        }
        else if (draft.Destination.Kind == DestinationKind.ExistingNormal)
        {
            if (!draft.Destination.ProfileId.HasValue || draft.Destination.ProfileId.Value == Guid.Empty)
            {
                blockers.Add(new VerificationBlocker(null, "PROFILE_ID_REQUIRED", "Existing profile ID is required for Existing Normal destination."));
            }

            if (draft.Destination.NewProfile is not null)
            {
                blockers.Add(new VerificationBlocker(null, "INVALID_EXISTING_NEW_PROFILE", "Existing Normal destination must not carry a NewProfile draft."));
            }
        }
        else if (draft.Destination.Kind == DestinationKind.SystemUnknown)
        {
            if (draft.Destination.ProfileId.HasValue)
            {
                blockers.Add(new VerificationBlocker(null, "INVALID_UNKNOWN_PROFILE_ID", "System Unknown destination must not specify a profile ID."));
            }

            if (draft.Destination.NewProfile is not null)
            {
                blockers.Add(new VerificationBlocker(null, "INVALID_UNKNOWN_NEW_PROFILE", "System Unknown destination must not carry a NewProfile draft."));
            }

            if (draft.Appearance.CoverMediaId.HasValue || draft.Appearance.BannerMediaId.HasValue)
            {
                blockers.Add(new VerificationBlocker(null, "APPEARANCE_NOT_SUPPORTED_FOR_UNKNOWN", "System Unknown destination does not support Cover or Banner appearance customization."));
            }
        }

        foreach (var faceDecision in draft.FaceDecisions)
        {
            if (faceDecision.FaceId == Guid.Empty)
            {
                blockers.Add(new VerificationBlocker(null, "INVALID_FACE_ID", "FaceId cannot be empty."));
            }

            if (faceDecision.Decision == VerificationFaceDecision.Reject)
            {
                if (faceDecision.TargetProfileId.HasValue || faceDecision.TargetKind.HasValue)
                {
                    blockers.Add(new VerificationBlocker(null, "INVALID_REJECT_TARGET", "Rejected face decision must not carry target information."));
                }
            }
            else if (faceDecision.Decision == VerificationFaceDecision.Confirm)
            {
                if (faceDecision.TargetKind is null)
                {
                    blockers.Add(new VerificationBlocker(null, "TARGET_KIND_REQUIRED", "Face confirmation requires a target kind (Destination or ExistingProfile)."));
                }
                else if (faceDecision.TargetKind == VerificationFaceTargetKind.Destination)
                {
                    if (faceDecision.TargetProfileId.HasValue)
                    {
                        blockers.Add(new VerificationBlocker(null, "INVALID_DESTINATION_TARGET", "Destination face confirmation must not fabricate a profile ID."));
                    }

                    if (draft.Destination.Kind == DestinationKind.SystemUnknown)
                    {
                        blockers.Add(new VerificationBlocker(null, "FACE_CONFIRMATION_TO_UNKNOWN_PROHIBITED", "Face confirmation to System Unknown destination is prohibited."));
                    }
                }
                else if (faceDecision.TargetKind == VerificationFaceTargetKind.ExistingProfile)
                {
                    if (!faceDecision.TargetProfileId.HasValue || faceDecision.TargetProfileId.Value == Guid.Empty)
                    {
                        blockers.Add(new VerificationBlocker(null, "TARGET_PROFILE_REQUIRED", "ExistingProfile face confirmation must specify a valid profile ID."));
                    }
                }
            }
        }

        foreach (var group in (draft.DuplicateDecisions ?? []).GroupBy(static decision => decision.ImportItemId))
        {
            if (group.Key == Guid.Empty || group.Count() != 1)
            {
                blockers.Add(new VerificationBlocker(group.Key == Guid.Empty ? null : group.Key, "INVALID_DUPLICATE_GATE_DECISION", "Each duplicate item must have exactly one durable gate decision."));
            }
        }

        foreach (var group in (draft.ProfileCollisionDecisions ?? []).GroupBy(static decision => decision.ImportItemId))
        {
            var decision = group.Last();
            if (group.Key == Guid.Empty || group.Count() != 1)
            {
                blockers.Add(new VerificationBlocker(group.Key == Guid.Empty ? null : group.Key, "INVALID_PROFILE_COLLISION_DECISION", "Each Profile-collision item must have exactly one durable gate decision."));
            }
            if (decision.Action == ProfileCollisionAction.MoveToProfile)
            {
                if (!decision.TargetProfileId.HasValue || decision.TargetProfileId.Value == Guid.Empty)
                {
                    blockers.Add(new VerificationBlocker(decision.ImportItemId, "MOVE_PROFILE_REQUIRED", "Move to Profile requires a target Profile."));
                }
            }
            else if (decision.TargetProfileId.HasValue)
            {
                blockers.Add(new VerificationBlocker(decision.ImportItemId, "UNEXPECTED_MOVE_PROFILE", "Only Move to Profile may carry a target Profile."));
            }
        }

        return blockers;
    }

    public async Task<IReadOnlyList<VerificationBlocker>> ValidateStepReadinessAsync(
        Guid unitId,
        int step,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return [new VerificationBlocker(null, "INVALID_UNIT_ID", "ImportUnit ID cannot be empty.")];
        }

        var blockers = new List<VerificationBlocker>();
        var importReads = new ImportReads(_connectionFactory);

        var unit = await importReads.GetVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
        {
            return [new VerificationBlocker(null, "UNIT_NOT_FOUND", $"ImportUnit {unitId:D} does not exist.")];
        }

        var draft = VerificationDraftV1.FromJson(unit.VerificationDraftJson, _timeProvider.GetUtcNowUnixMilliseconds());

        if (step >= 1)
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT i.import_item_id, i.disposition, i.duplicate_decision,
                       EXISTS (
                           SELECT 1
                           FROM media c
                           JOIN media a ON a.sha256 = c.sha256
                                        AND a.byte_length = c.byte_length
                                        AND a.media_id <> c.media_id
                                        AND a.state = 'ACTIVE'
                           WHERE c.media_id = i.candidate_media_id
                       ) AS has_active_duplicate,
                       c.dependency_status
                FROM import_items i
                LEFT JOIN media c ON c.media_id = i.candidate_media_id
                WHERE i.import_unit_id = $unitId;
                """;
            cmd.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

            int includedCount = 0;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var itemId = DbGuid.Parse(reader.GetString(0), DomainIdKind.ImportItem);
                var disposition = DbEnum.ParseItemDisposition(reader.GetString(1));
                var decision = DbEnum.ParseDuplicateDecisionOrNull(
                    reader.IsDBNull(2) ? null : reader.GetString(2));
                var hasActiveDuplicate = reader.GetInt64(3) != 0;
                var depStatus = reader.IsDBNull(4) ? MediaDependencyStatus.SelfContained : DbEnum.ParseMediaDependencyStatus(reader.GetString(4));

                if (disposition == ItemDisposition.Invalid)
                {
                    blockers.Add(new VerificationBlocker(itemId, "ITEM_INVALID", "Item cannot be admitted."));
                    continue;
                }

                if (hasActiveDuplicate && decision is null)
                {
                    blockers.Add(new VerificationBlocker(itemId, "DUPLICATE_DECISION_REQUIRED", "Item requires duplicate resolution."));
                    continue;
                }

                if (disposition is ItemDisposition.Included or ItemDisposition.Reused)
                {
                    includedCount++;

                    if (depStatus == MediaDependencyStatus.DependenciesMissing
                        && !(draft.AcknowledgedMissingDependencyItemIds?.Contains(itemId) ?? false))
                    {
                        blockers.Add(new VerificationBlocker(
                            itemId,
                            "DEPENDENCIES_MISSING",
                            "Model package has missing dependencies and must be acknowledged before verification."));
                    }

                }
            }

            if (includedCount == 0)
            {
                blockers.Add(new VerificationBlocker(null, "NO_INCLUDED_ITEMS", "At least one item must be included for verification."));
            }
        }

        if (step >= 2)
        {
            if (draft.Destination.Kind is null)
            {
                blockers.Add(new VerificationBlocker(null, "DESTINATION_REQUIRED", "Destination must be selected."));
            }
            else if (draft.Destination.Kind == DestinationKind.NewNormal)
            {
                if (draft.Destination.NewProfile is null || string.IsNullOrWhiteSpace(draft.Destination.NewProfile.DisplayName))
                {
                    blockers.Add(new VerificationBlocker(null, "DISPLAY_NAME_REQUIRED", "Profile display name is required."));
                }
                else if (draft.Destination.NewProfile.DisplayName.Trim().Length > 100)
                {
                    blockers.Add(new VerificationBlocker(null, "DISPLAY_NAME_TOO_LONG", "Profile display name cannot exceed 100 characters."));
                }

                if (draft.Destination.NewProfile?.CategoryId is { Length: > 0 } catId)
                {
                    var catValid = await CategoryExistsAsync(catId, cancellationToken).ConfigureAwait(false);
                    if (!catValid)
                    {
                        blockers.Add(new VerificationBlocker(null, "INVALID_CATEGORY", "That category no longer exists."));
                    }
                }

                if (draft.Destination.NewProfile?.TagIds is { } tagIds && tagIds.Count > 0)
                {
                    var tagsValid = await AllTagsExistAsync(tagIds, cancellationToken).ConfigureAwait(false);
                    if (!tagsValid)
                    {
                        blockers.Add(new VerificationBlocker(null, "INVALID_TAGS", "One or more specified tags do not exist."));
                    }
                }
            }
            else if (draft.Destination.Kind == DestinationKind.ExistingNormal)
            {
                if (!draft.Destination.ProfileId.HasValue || draft.Destination.ProfileId.Value == Guid.Empty)
                {
                    blockers.Add(new VerificationBlocker(null, "PROFILE_ID_REQUIRED", "Target profile ID is required."));
                }
                else
                {
                    var profileValid = await IsActiveNormalProfileAsync(draft.Destination.ProfileId.Value, cancellationToken).ConfigureAwait(false);
                    if (!profileValid)
                    {
                        blockers.Add(new VerificationBlocker(null, "INVALID_DESTINATION_PROFILE", "Target profile must exist and be an active normal profile."));
                    }
                }
            }
            else if (draft.Destination.Kind == DestinationKind.SystemUnknown)
            {
                if (draft.Destination.ProfileId.HasValue)
                {
                    blockers.Add(new VerificationBlocker(null, "INVALID_UNKNOWN_PROFILE_ID", "System Unknown destination must not specify a profile ID."));
                }
            }
        }

        if (step >= 3)
        {
            if (draft.Destination.Kind == DestinationKind.SystemUnknown)
            {
                if (draft.Appearance.CoverMediaId.HasValue || draft.Appearance.BannerMediaId.HasValue)
                {
                    blockers.Add(new VerificationBlocker(null, "APPEARANCE_NOT_SUPPORTED_FOR_UNKNOWN", "System Unknown destination does not support Cover or Banner appearance customization."));
                }
            }
            else
            {
                if (draft.Appearance.CoverMediaId is { } coverMediaId && coverMediaId != Guid.Empty)
                {
                    var coverBlocker = await ValidateCoverMediaEligibilityAsync(
                        unitId, draft.Destination, coverMediaId, draft.Appearance, cancellationToken).ConfigureAwait(false);
                    if (coverBlocker is not null)
                    {
                        blockers.Add(coverBlocker);
                    }
                }

                if (draft.Appearance.BannerMediaId is { } bannerMediaId && bannerMediaId != Guid.Empty)
                {
                    var bannerBlocker = await ValidateBannerMediaEligibilityAsync(
                        unitId,
                        draft.Destination,
                        draft.Appearance,
                        bannerMediaId,
                        cancellationToken).ConfigureAwait(false);
                    if (bannerBlocker is not null)
                    {
                        blockers.Add(bannerBlocker);
                    }
                }
            }
        }

        if (step >= 4)
        {
            foreach (var move in (draft.ProfileCollisionDecisions ?? [])
                         .Where(static decision => decision.Action == ProfileCollisionAction.MoveToProfile
                             && decision.TargetProfileId.HasValue))
            {
                if (!await IsActiveNormalProfileAsync(move.TargetProfileId!.Value, cancellationToken).ConfigureAwait(false))
                {
                    blockers.Add(new VerificationBlocker(move.ImportItemId, "INVALID_MOVE_TARGET_PROFILE", "The Profile selected for this media is no longer available."));
                }
            }

            var resolvedCollisions = (draft.ProfileCollisionDecisions ?? [])
                .Select(static decision => decision.ImportItemId)
                .ToHashSet();
            foreach (var collisionItemId in await ReadStrongCollisionItemIdsAsync(unitId, draft.Destination, cancellationToken).ConfigureAwait(false))
            {
                if (!resolvedCollisions.Contains(collisionItemId))
                {
                    blockers.Add(new VerificationBlocker(collisionItemId, "PROFILE_COLLISION_DECISION_REQUIRED", "Strong alternate-Profile evidence requires an ownership decision."));
                }
            }

            if (draft.FaceDecisions.Count > 0)
            {
                foreach (var faceDecision in draft.FaceDecisions)
                {
                    var faceBlocker = await ValidateFaceDecisionAsync(unitId, draft.Destination, faceDecision, cancellationToken).ConfigureAwait(false);
                    if (faceBlocker is not null)
                    {
                        blockers.Add(faceBlocker);
                    }
                }
            }
        }

        return blockers;
    }

    public async Task<VerificationReadinessResult> ValidateCommitReadinessAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        var blockers = new List<VerificationBlocker>();
        var warnings = new List<string>();
        var importReads = new ImportReads(_connectionFactory);
        var capabilityReads = new CapabilityReads(_connectionFactory);

        var unit = await importReads.GetVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
        {
            return new VerificationReadinessResult(false, [new VerificationBlocker(null, "UNIT_NOT_FOUND", $"ImportUnit {unitId:D} does not exist.")], warnings);
        }

        if (unit.State.IsUnitCommitted() || unit.State.IsTerminal())
        {
            blockers.Add(new VerificationBlocker(
                null,
                "TERMINAL_STATE",
                $"ImportUnit is in terminal state '{DbEnum.Format(unit.State)}'."));
        }

        var preparationMap = await importReads
            .ListRequiredPreparationFailuresAsync([unitId], cancellationToken)
            .ConfigureAwait(false);
        foreach (var failure in preparationMap.GetValueOrDefault(unitId) ?? [])
        {
            blockers.Add(new VerificationBlocker(
                failure.ImportItemId,
                "REQUIRED_PREPARATION_FAILED",
                $"{failure.SourceFileName}: required {failure.Capability} preparation failed."));
        }

        if (!await capabilityReads.AreAllRequiredCapabilitiesSettledAsync(unitId, cancellationToken)
                .ConfigureAwait(false))
        {
            blockers.Add(new VerificationBlocker(
                null,
                "REQUIRED_PREPARATION_PENDING",
                "Required media preparation is still running."));
        }

        var draft = VerificationDraftV1.FromJson(unit.VerificationDraftJson, _timeProvider.GetUtcNowUnixMilliseconds());
        var draftBlockers = ValidateDraft(draft);
        blockers.AddRange(draftBlockers);

        for (int step = 1; step <= 4; step++)
        {
            var stepBlockers = await ValidateStepReadinessAsync(unitId, step, cancellationToken).ConfigureAwait(false);
            foreach (var sb in stepBlockers)
            {
                if (!blockers.Any(b => b.Code == sb.Code && b.ImportItemId == sb.ImportItemId))
                {
                    blockers.Add(sb);
                }
            }
        }

        return new VerificationReadinessResult(blockers.Count == 0, blockers, warnings);
    }

    private async Task<IReadOnlyList<Guid>> ReadStrongCollisionItemIdsAsync(
        Guid unitId,
        VerificationDestinationDraft destination,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ii.import_item_id, fd.suggested_identity_id, fd.suggested_candidates_json, p.profile_id
            FROM import_items ii
            JOIN face_detections fd ON fd.media_id = COALESCE(ii.reused_media_id, ii.candidate_media_id)
            JOIN identities identity ON identity.identity_id = fd.suggested_identity_id
            JOIN profiles p ON p.profile_id = identity.profile_id
            WHERE ii.import_unit_id = $unitId AND ii.disposition = 'INCLUDED'
              AND p.kind = 'NORMAL' AND p.trashed_at_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var evidence = new List<(Guid ItemId, Guid ProfileId)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(2))
            {
                continue;
            }
            var identityId = DbGuid.Parse(reader.GetString(1));
            var similarity = FaceSuggestionEvidenceV1.TryParse(reader.GetString(2))?.Candidates
                .FirstOrDefault(candidate => candidate.IdentityId == identityId)?.Similarity;
            if (similarity >= ImportProfileCollisionPolicy.StrongSimilarityThreshold)
            {
                evidence.Add((DbGuid.Parse(reader.GetString(0)), DbGuid.Parse(reader.GetString(3))));
            }
        }

        var destinationId = destination.Kind == DestinationKind.ExistingNormal ? destination.ProfileId : null;
        return [.. evidence.GroupBy(static row => row.ItemId)
            .Where(group => !destinationId.HasValue || group.All(row => row.ProfileId != destinationId.Value))
            .Where(group => group.Select(static row => row.ProfileId).Distinct().Count() == 1)
            .Select(static group => group.Key)];
    }

    private static CoverVisualSourceKind ParseCoverSourceKind(
        VerificationAppearanceDraft appearance,
        MediaType mediaType) =>
        Enum.TryParse<CoverVisualSourceKind>(appearance.CoverSourceKind, ignoreCase: true, out var parsed)
            ? parsed
            : ProfileAppearanceRules.ResolveCoverSourceKind(mediaType);

    private async Task<VerificationBlocker?> ValidateCoverMediaEligibilityAsync(
        Guid unitId,
        VerificationDestinationDraft destination,
        Guid coverMediaId,
        VerificationAppearanceDraft draftAppearance,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var unitItemCmd = connection.CreateCommand();
        unitItemCmd.CommandText = """
            SELECT a.media_type
            FROM import_items ii
            JOIN media a ON (
                (ii.disposition = 'INCLUDED' AND (ii.candidate_media_id = a.media_id OR ii.reused_media_id = a.media_id))
                OR (ii.disposition = 'REUSED' AND ii.duplicate_decision = 'REUSE' AND ii.reused_media_id = a.media_id
                    AND a.state = 'ACTIVE' AND a.trashed_at_ms IS NULL))
            WHERE ii.import_unit_id = $unitId AND a.media_id = $assetId;
            """;
        unitItemCmd.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        unitItemCmd.Parameters.AddWithValue("$assetId", DbGuid.Format(coverMediaId));

        var unitMediaTypeStr = await unitItemCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (unitMediaTypeStr is not null)
        {
            var mediaType = DbEnum.ParseMediaType(unitMediaTypeStr);
            if (!ProfileAppearanceRules.IsCoverMediaTypeEligible(mediaType))
            {
                return new VerificationBlocker(null, "COVER_MEDIA_TYPE_INELIGIBLE", $"Cover asset media type '{unitMediaTypeStr}' is not eligible; a Cover must be an image or a video frame.");
            }

            if (!ProfileAppearanceRules.IsCoverVisualSourceValid(
                    mediaType,
                    ParseCoverSourceKind(draftAppearance, mediaType),
                    draftAppearance.CoverVideoTimestampMilliseconds))
            {
                return new VerificationBlocker(
                    null,
                    "COVER_VISUAL_SOURCE_INCOMPLETE",
                    "A video Cover must name the exact frame it uses, in milliseconds.");
            }
            return null;
        }

        if (destination.Kind == DestinationKind.ExistingNormal && destination.ProfileId.HasValue)
        {
            await using var profileMediaCmd = connection.CreateCommand();
            profileMediaCmd.CommandText = """
                SELECT a.media_type, a.state, a.trashed_at_ms
                FROM media a
                JOIN profile_media pa ON a.media_id = pa.media_id
                WHERE a.media_id = $assetId
                  AND pa.profile_id = $profileId
                  AND pa.publication_import_unit_id IS NULL;
                """;
            profileMediaCmd.Parameters.AddWithValue("$assetId", DbGuid.Format(coverMediaId));
            profileMediaCmd.Parameters.AddWithValue("$profileId", DbGuid.Format(destination.ProfileId.Value));

            await using var reader = await profileMediaCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var mediaTypeStr = reader.GetString(0);
                var state = DbEnum.ParseMediaState(reader.GetString(1));
                var isTrashed = !reader.IsDBNull(2);

                if (isTrashed || state != MediaState.Active)
                {
                    return new VerificationBlocker(null, "COVER_ASSET_NOT_ACTIVE", "Cover asset must be an active Vault asset.");
                }

                var mediaType = DbEnum.ParseMediaType(mediaTypeStr);
                if (!ProfileAppearanceRules.IsCoverMediaTypeEligible(mediaType))
                {
                    return new VerificationBlocker(null, "COVER_MEDIA_TYPE_INELIGIBLE", $"Cover asset media type '{mediaTypeStr}' is not eligible; a Cover must be an image or a video frame.");
                }

                if (!ProfileAppearanceRules.IsCoverVisualSourceValid(
                        mediaType,
                        ParseCoverSourceKind(draftAppearance, mediaType),
                        draftAppearance.CoverVideoTimestampMilliseconds))
                {
                    return new VerificationBlocker(
                        null,
                        "COVER_VISUAL_SOURCE_INCOMPLETE",
                        "A video Cover must name the exact frame it uses, in milliseconds.");
                }

                return null;
            }
        }

        return new VerificationBlocker(null, "INELIGIBLE_COVER_ASSET", "Selected cover asset is not an included or reused item in this import unit or an active asset related to the destination profile.");
    }

    private async Task<VerificationBlocker?> ValidateBannerMediaEligibilityAsync(
        Guid unitId,
        VerificationDestinationDraft destination,
        VerificationAppearanceDraft draftAppearance,
        Guid bannerMediaId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var unitItemCmd = connection.CreateCommand();
        unitItemCmd.CommandText = """
            SELECT a.media_type
            FROM import_items ii
            JOIN media a ON (
                (ii.disposition = 'INCLUDED' AND (ii.candidate_media_id = a.media_id OR ii.reused_media_id = a.media_id))
                OR (ii.disposition = 'REUSED' AND ii.duplicate_decision = 'REUSE' AND ii.reused_media_id = a.media_id
                    AND a.state = 'ACTIVE' AND a.trashed_at_ms IS NULL))
            WHERE ii.import_unit_id = $unitId AND a.media_id = $assetId;
            """;
        unitItemCmd.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        unitItemCmd.Parameters.AddWithValue("$assetId", DbGuid.Format(bannerMediaId));

        var unitMediaTypeStr = await unitItemCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (unitMediaTypeStr is not null)
        {
            var mediaType = DbEnum.ParseMediaType(unitMediaTypeStr);
            if (!ProfileAppearanceRules.IsBannerMediaTypeEligible(mediaType))
            {
                return new VerificationBlocker(null, "BANNER_MEDIA_TYPE_INELIGIBLE", "A Profile banner must be video media with a prepared HOVER asset.");
            }
            return null;
        }

        if (destination.Kind == DestinationKind.ExistingNormal && destination.ProfileId.HasValue)
        {
            await using var profileMediaCmd = connection.CreateCommand();
            profileMediaCmd.CommandText = """
                SELECT a.media_type, a.state, a.trashed_at_ms
                FROM media a
                JOIN profile_media pa ON a.media_id = pa.media_id
                WHERE a.media_id = $assetId
                  AND pa.profile_id = $profileId
                  AND pa.publication_import_unit_id IS NULL;
                """;
            profileMediaCmd.Parameters.AddWithValue("$assetId", DbGuid.Format(bannerMediaId));
            profileMediaCmd.Parameters.AddWithValue("$profileId", DbGuid.Format(destination.ProfileId.Value));

            await using var reader = await profileMediaCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var mediaTypeStr = reader.GetString(0);
                var state = DbEnum.ParseMediaState(reader.GetString(1));
                var isTrashed = !reader.IsDBNull(2);

                if (isTrashed || state != MediaState.Active)
                {
                    return new VerificationBlocker(null, "BANNER_ASSET_NOT_ACTIVE", "That banner is no longer in your Vault.");
                }

                var mediaType = DbEnum.ParseMediaType(mediaTypeStr);
                if (!ProfileAppearanceRules.IsBannerMediaTypeEligible(mediaType))
                {
                    return new VerificationBlocker(null, "BANNER_MEDIA_TYPE_INELIGIBLE", "A Profile banner must be video media with a prepared HOVER asset.");
                }

                return null;
            }
        }

        return new VerificationBlocker(null, "INELIGIBLE_BANNER_ASSET", "That banner is not part of this import or of the chosen profile.");
    }

    private async Task<VerificationBlocker?> ValidateFaceDecisionAsync(
        Guid unitId,
        VerificationDestinationDraft destination,
        StagedFaceDecision decision,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var faceCmd = connection.CreateCommand();
        faceCmd.CommandText = """
            SELECT fd.row_version
            FROM face_detections fd
            JOIN import_items ii ON (fd.media_id = ii.candidate_media_id OR fd.media_id = ii.reused_media_id)
            WHERE fd.face_id = $faceId AND ii.import_unit_id = $unitId AND ii.disposition = 'INCLUDED';
            """;
        faceCmd.Parameters.AddWithValue("$faceId", DbGuid.Format(decision.FaceId));
        faceCmd.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var rowVersionObj = await faceCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (rowVersionObj is null or DBNull)
        {
            return new VerificationBlocker(null, "FACE_DETECTION_NOT_FOUND", $"Face detection {decision.FaceId:D} was not found on any included candidate in this unit.");
        }

        var actualRowVersion = Convert.ToInt64(rowVersionObj);
        if (actualRowVersion != decision.ExpectedFaceRowVersion)
        {
            return new VerificationBlocker(null, "FACE_DETECTION_STALE", $"Face detection {decision.FaceId:D} is stale (expected row version {decision.ExpectedFaceRowVersion}, actual is {actualRowVersion}).");
        }

        if (decision.Decision == VerificationFaceDecision.Confirm)
        {
            if (decision.TargetKind == VerificationFaceTargetKind.Destination)
            {
                if (destination.Kind == DestinationKind.SystemUnknown)
                {
                    return new VerificationBlocker(null, "FACE_CONFIRMATION_TO_UNKNOWN_PROHIBITED", "Face confirmation to System Unknown destination is prohibited.");
                }
            }
            else if (decision.TargetKind == VerificationFaceTargetKind.ExistingProfile)
            {
                if (!decision.TargetProfileId.HasValue || decision.TargetProfileId.Value == Guid.Empty)
                {
                    return new VerificationBlocker(null, "TARGET_PROFILE_REQUIRED", "ExistingProfile face confirmation must specify a valid profile ID.");
                }

                var profileValid = await IsActiveNormalProfileAsync(decision.TargetProfileId.Value, cancellationToken).ConfigureAwait(false);
                if (!profileValid)
                {
                    return new VerificationBlocker(null, "INVALID_FACE_TARGET_PROFILE", $"Target profile {decision.TargetProfileId.Value:D} for face confirmation must exist and be an active normal profile.");
                }
            }
        }

        return null;
    }

    private async Task<bool> IsActiveNormalProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT kind FROM profiles
            WHERE profile_id = $pid AND visibility = 'PUBLISHED' AND trashed_at_ms IS NULL;
            """;
        cmd.Parameters.AddWithValue("$pid", DbGuid.Format(profileId));

        var kind = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return kind is not null && DbEnum.ParseProfileKind(kind) == ProfileKind.Normal;
    }

    private async Task<bool> CategoryExistsAsync(string categoryId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM categories WHERE category_id = $cid;";
        cmd.Parameters.AddWithValue("$cid", categoryId);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    private async Task<bool> AllTagsExistAsync(IReadOnlyList<string> tagIds, CancellationToken cancellationToken)
    {
        var distinct = tagIds.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            return true;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        // Parameterized: tag identifiers are user-authored text, never interpolated into SQL.
        var names = distinct.Select((_, index) => "$tag" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        cmd.CommandText = $"SELECT COUNT(DISTINCT tag_id) FROM tags WHERE tag_id IN ({string.Join(",", names)});";

        for (var index = 0; index < distinct.Count; index++)
        {
            cmd.Parameters.AddWithValue(names[index], distinct[index]);
        }

        var countObj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var count = Convert.ToInt32(countObj, System.Globalization.CultureInfo.InvariantCulture);
        return count == distinct.Count;
    }
}
