using Neuterradise.App.Media.Model;
using System.IO;
using System.Text;
using System.Text.Json;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.ProfileCards;
using Neuterradise.App.Design.MediaLayouts;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.Profiles;

public sealed class ProfileAppearanceOperations
{
    private readonly CatalogDb _catalog;
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public ProfileAppearanceOperations(
        CatalogDb catalog,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OperationResult<ProfileAppearanceOutcome>> SetCoverMediaAsync(
        SetCoverMediaRequest request,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetCoverMediaAsync));
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.ProfileId, nameof(request));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var gate = await ValidateProfileAsync(
            transaction, request.ProfileId, request.ExpectedProfileRowVersion, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        var coverSourceKind = CoverVisualSourceKind.Image;
        long? coverTimestamp = null;
        Guid? thumbnailMediaAssetId = null;

        if (request.CoverMediaId is { } coverMediaId)
        {
            var asset = await ReadAppearanceMediaAsync(transaction, coverMediaId, cancellationToken)
                .ConfigureAwait(false);
            var rejection = await ValidateAppearanceMediaAsync(
                transaction,
                request.ProfileId,
                coverMediaId,
                ProfileAppearanceRules.IsCoverMediaTypeEligible,
                "A Cover must be an image or a video frame that is already linked to this Profile.",
                cancellationToken).ConfigureAwait(false);
            if (rejection is not null)
            {
                return rejection;
            }

            var thumbnail = await FindReadyMediaAssetAsync(transaction, request.ProfileId,
                coverMediaId, MediaAssetRole.Thumbnail, request.CoverMediaAssetId,
                cancellationToken).ConfigureAwait(false);
            if (thumbnail is null)
                return OperationResult<ProfileAppearanceOutcome>.Validation(
                    OperationErrorCode.ProfileAppearanceInvalid,
                    "This media item's ready Thumbnail is unavailable.");
            thumbnailMediaAssetId = thumbnail.Value.Id;
            coverSourceKind = asset?.MediaType == MediaType.Video
                ? CoverVisualSourceKind.VideoFrame : CoverVisualSourceKind.Image;
            coverTimestamp = thumbnail.Value.SourceTimestampMs;

        }

        var newRowVersion = request.ExpectedProfileRowVersion + 1;
        await using (var update = transaction.CreateCommand(
            """
            UPDATE profiles
            SET cover_media_id = $coverMediaId,
                updated_at_ms = $now,
                row_version = $newRowVersion
            WHERE profile_id = $profileId AND row_version = $expectedRowVersion;
            """))
        {
            update.Parameters.AddWithValue(
                "$coverMediaId",
                request.CoverMediaId is { } id ? DbGuid.Format(id) : DBNull.Value);
            update.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
            update.Parameters.AddWithValue("$newRowVersion", newRowVersion);
            update.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
            update.Parameters.AddWithValue("$expectedRowVersion", request.ExpectedProfileRowVersion);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var storedOverrides = await ReadOverridesInTransactionAsync(
            transaction, request.ProfileId, cancellationToken).ConfigureAwait(false);
        await WriteOverridesAsync(
            transaction,
            request.ProfileId,
            storedOverrides with
            {
                CoverSourceKind = request.CoverMediaId is null ? null : coverSourceKind.ToString(),
                CoverVideoTimestampMilliseconds = coverTimestamp,
            },
            cancellationToken).ConfigureAwait(false);

        await using (var selection = transaction.CreateCommand("""
            UPDATE profile_appearance SET cover_media_asset_id = $thumbnail
            WHERE profile_id = $profile;
            """))
        {
            selection.Parameters.AddWithValue("$thumbnail", thumbnailMediaAssetId is { } id
                ? DbGuid.Format(id) : DBNull.Value);
            selection.Parameters.AddWithValue("$profile", DbGuid.Format(request.ProfileId));
            await selection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AppendAppearanceActivityAsync(transaction, request.ProfileId, "cover", DbTime.Format(_timeProvider.GetUtcNow()), cancellationToken)
            .ConfigureAwait(false);
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.ProfileId],
            CatalogInvalidationDomain.Appearance,
            newRowVersion));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return OperationResult<ProfileAppearanceOutcome>.Success(
            new ProfileAppearanceOutcome(request.ProfileId, newRowVersion));
    }

    /// <summary>
    /// Applies the Customization Center's Profile-scoped presentation inside the caller's transaction.
    /// Layout and the complete canonical appearance payload commit atomically with Presentation bindings.
    /// </summary>
    internal async Task<ProfilePresentationWriteResult> ApplyPresentationInTransactionAsync(
        ApplyProfilePresentationRequest request,
        CatalogTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transaction);
        EnsureNonEmpty(request.ProfileId, nameof(request));
        var overrides = request.Overrides ?? ProfileAppearanceOverrides.Default;
        var presetId = string.IsNullOrWhiteSpace(request.LayoutPresetId) ? null : request.LayoutPresetId.Trim();
        var invalid = ValidatePresentation(request, presetId, overrides);
        if (invalid is not null)
        {
            return new(invalid);
        }

        var gate = await ValidateProfileAsync(
            transaction, request.ProfileId, request.ExpectedProfileRowVersion, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return new(gate);
        }

        var stored = await ReadOverridesInTransactionAsync(transaction, request.ProfileId, cancellationToken).ConfigureAwait(false);
        if (request.Sources is not null)
        {
            var applied = await ApplySourcesInTransactionAsync(
                transaction, request.ProfileId, request.Sources, overrides, cancellationToken).ConfigureAwait(false);
            if (applied.Rejection is not null)
            {
                return new(applied.Rejection);
            }

            overrides = applied.Overrides;
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var newRowVersion = CatalogTransaction.NextRowVersion(request.ExpectedProfileRowVersion);
        await using (var upsert = transaction.CreateCommand(
            """
            INSERT INTO profile_appearance(
                profile_id, schema_version, layout_preset_id, overrides_json, updated_at_ms, row_version)
            VALUES ($profileId, $schemaVersion, $layoutPresetId, $overridesJson, $now, 1)
            ON CONFLICT(profile_id) DO UPDATE SET
                layout_preset_id = CASE WHEN $applyLayout = 1
                    THEN $layoutPresetId ELSE profile_appearance.layout_preset_id END,
                overrides_json = excluded.overrides_json,
                updated_at_ms = excluded.updated_at_ms,
                row_version = profile_appearance.row_version + 1;
            """))
        {
            upsert.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
            upsert.Parameters.AddWithValue("$schemaVersion", ProfileAppearanceOverrides.SchemaVersion);
            upsert.Parameters.AddWithValue("$layoutPresetId", (object?)presetId ?? ProfileLayoutResolver.FallbackPresetId);
            upsert.Parameters.AddWithValue("$overridesJson", overrides.ToJson());
            upsert.Parameters.AddWithValue("$now", now);
            upsert.Parameters.AddWithValue("$applyLayout", request.ApplyLayout ? 1 : 0);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var bump = transaction.CreateCommand(
            """
            UPDATE profiles
            SET updated_at_ms = $now, row_version = $newRowVersion
            WHERE profile_id = $profileId AND row_version = $expectedRowVersion;
            """))
        {
            bump.Parameters.AddWithValue("$now", now);
            bump.Parameters.AddWithValue("$newRowVersion", newRowVersion);
            bump.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
            bump.Parameters.AddWithValue("$expectedRowVersion", request.ExpectedProfileRowVersion);
            if (await bump.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new CatalogConcurrencyConflictException("The Profile changed while its presentation was being applied.");
            }
        }

        await AppendAppearanceActivityAsync(transaction, request.ProfileId, "presentation", now, cancellationToken)
            .ConfigureAwait(false);
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.ProfileId],
            CatalogInvalidationDomain.Appearance,
            newRowVersion));
        return new(
            OperationResult<ProfileAppearanceOutcome>.Success(
                new ProfileAppearanceOutcome(request.ProfileId, newRowVersion)));
    }

    private static OperationResult<ProfileAppearanceOutcome>? ValidatePresentation(
        ApplyProfilePresentationRequest request,
        string? presetId,
        ProfileAppearanceOverrides overrides)
    {
        if (request.ApplyLayout && presetId is not null && !ProfileLayoutResolver.IsBuiltInPresetId(presetId))
            return OperationResult<ProfileAppearanceOutcome>.Validation(OperationErrorCode.ProfileLayoutInvalid, "That Profile layout is not one of the available layouts.");
        if (overrides.ProfileCardVariantId is { } variant && !ProfileCardCatalog.IsKnownVariant(variant))
            return OperationResult<ProfileAppearanceOutcome>.Validation(OperationErrorCode.ProfileCardInvalid, $"The Profile card variant '{variant}' is not recognized.");
        if (overrides.MediaLayoutId is { } mediaLayoutId && !MediaLayoutCatalog.IsBuiltInId(mediaLayoutId))
            return OperationResult<ProfileAppearanceOutcome>.Validation(OperationErrorCode.ProfileAppearanceInvalid, "That Media Layout is not recognized.");
        var cover = CoverFrameCatalog.Resolve(overrides.ToCoverAppearanceRequest(), reduceMotion: false);
        if (overrides.CoverFrameId is not null && cover.Diagnostics.Count > 0)
            return OperationResult<ProfileAppearanceOutcome>.Validation(OperationErrorCode.ProfileAppearanceInvalid, cover.Diagnostics[0].Detail);
        if (!ProfileAppearanceRules.IsCoverCropValid(overrides.CropX)
            || !ProfileAppearanceRules.IsCoverCropValid(overrides.CropY)
            || !ProfileAppearanceRules.IsCoverZoomValid(overrides.Zoom)
            || !InRange(overrides.CoverOffsetX, ProfileAppearanceOverrides.MaximumOffset)
            || !InRange(overrides.CoverOffsetY, ProfileAppearanceOverrides.MaximumOffset)
            || !InRange(overrides.CoverRotation, ProfileAppearanceOverrides.MaximumRotation)
            || !ProfileAppearanceRules.IsBannerFocusValid(overrides.BannerFocusX)
            || !ProfileAppearanceRules.IsBannerFocusValid(overrides.BannerFocusY)
            || !ProfileAppearanceRules.IsBannerZoomValid(overrides.BannerZoom)
            || (overrides.CoverFit is { } coverFit && !ProfileAppearanceOverrides.FitModes.Contains(coverFit)))
            return OperationResult<ProfileAppearanceOutcome>.Validation(OperationErrorCode.ProfileAppearanceInvalid, "The Cover or Banner presentation is outside the supported range.");
        return null;

        static bool InRange(double value, double limit) => double.IsFinite(value) && Math.Abs(value) <= limit;
    }

    private async Task<(OperationResult<ProfileAppearanceOutcome>? Rejection, ProfileAppearanceOverrides Overrides)> ApplySourcesInTransactionAsync(
        CatalogTransaction transaction,
        Guid profileId,
        ProfileMediaSourceChange sources,
        ProfileAppearanceOverrides overrides,
        CancellationToken cancellationToken)
    {
        var result = overrides;
        if (sources.CoverChanged)
        {
            var kind = CoverVisualSourceKind.Image;
            long? timestamp = null;
            if (sources.CoverMediaId is { } coverMediaId)
            {
                var asset = await ReadAppearanceMediaAsync(transaction, coverMediaId, cancellationToken).ConfigureAwait(false);
                var rejection = await ValidateAppearanceMediaAsync(
                    transaction,
                    profileId,
                    coverMediaId,
                    ProfileAppearanceRules.IsCoverMediaTypeEligible,
                    "A Cover must be an image or a video frame that is already linked to this Profile.",
                    cancellationToken).ConfigureAwait(false);
                if (rejection is not null)
                {
                    return (rejection, overrides);
                }

                var mediaType = asset?.MediaType ?? MediaType.Image;
                kind = ProfileAppearanceRules.ResolveCoverSourceKind(mediaType);
                timestamp = kind == CoverVisualSourceKind.VideoFrame ? overrides.CoverVideoTimestampMilliseconds : null;
                if (!ProfileAppearanceRules.IsCoverVisualSourceValid(mediaType, kind, timestamp))
                {
                    return (OperationResult<ProfileAppearanceOutcome>.Validation(
                        OperationErrorCode.ProfileAppearanceInvalid,
                        kind == CoverVisualSourceKind.VideoFrame
                            ? "A video Cover must name the exact frame it uses, in milliseconds."
                            : "An image Cover carries no frame timestamp."), overrides);
                }
            }

            Guid? preparedCoverId = null;
            if (sources.CoverMediaId is { } selectedMedia)
            {
                var ready = await FindReadyMediaAssetAsync(transaction, profileId, selectedMedia,
                    MediaAssetRole.Thumbnail, sources.CoverMediaAssetId,
                    cancellationToken).ConfigureAwait(false);
                if (ready is null)
                    return (OperationResult<ProfileAppearanceOutcome>.Validation(
                        OperationErrorCode.ProfileAppearanceInvalid,
                        "This media item's ready Thumbnail is unavailable."), overrides);
                preparedCoverId = ready.Value.Id;
                timestamp = ready.Value.SourceTimestampMs;
            }

            await using (var update = transaction.CreateCommand("UPDATE profiles SET cover_media_id = $assetId WHERE profile_id = $profileId;"))
            {
                update.Parameters.AddWithValue("$assetId", sources.CoverMediaId is { } id ? DbGuid.Format(id) : DBNull.Value);
                update.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var selection = transaction.CreateCommand("""
                UPDATE profile_appearance SET cover_media_asset_id = $artifact
                WHERE profile_id = $profile;
                """))
            {
                selection.Parameters.AddWithValue("$artifact", preparedCoverId is { } id ? DbGuid.Format(id) : DBNull.Value);
                selection.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
                await selection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            result = result with
            {
                CoverSourceKind = sources.CoverMediaId is null ? null : kind.ToString(),
                CoverVideoTimestampMilliseconds = timestamp,
            };
        }

        if (sources.BannerChanged)
        {
            Guid? preparedBannerId = null;
            if (sources.BannerMediaId is { } bannerMediaId)
            {
                var rejection = await ValidateAppearanceMediaAsync(
                    transaction,
                    profileId,
                    bannerMediaId,
                    ProfileAppearanceRules.IsBannerMediaTypeEligible,
                    "A banner must be video media that already belongs to this profile.",
                    cancellationToken).ConfigureAwait(false);
                if (rejection is not null)
                {
                    return (rejection, overrides);
                }

                var ready = await FindReadyMediaAssetAsync(
                    transaction,
                    profileId,
                    bannerMediaId,
                    MediaAssetRole.Hover,
                    sources.BannerMediaAssetId,
                    cancellationToken).ConfigureAwait(false);
                if (ready is null)
                {
                    return (OperationResult<ProfileAppearanceOutcome>.Validation(
                        OperationErrorCode.ProfileAppearanceInvalid,
                        "This video's ready HOVER MP4 is unavailable."), overrides);
                }

                preparedBannerId = ready.Value.Id;
            }

            await using var selection = transaction.CreateCommand("""
                UPDATE profile_appearance SET banner_media_asset_id = $artifact
                WHERE profile_id = $profile;
                """);
            selection.Parameters.AddWithValue("$artifact", preparedBannerId is { } selectedId
                ? DbGuid.Format(selectedId) : DBNull.Value);
            selection.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
            await selection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        if (sources.FigureChanged)
        {
            if (sources.FigureMediaId is { } figure)
            {
                if (figure == Guid.Empty) return (OperationResult<ProfileAppearanceOutcome>.Validation(OperationErrorCode.AppearanceMediaInvalid,"Figure media identifier cannot be empty."),overrides);
                await using var check = transaction.CreateCommand("""
                    SELECT coalesce(m.current_managed_file_name,m.original_file_name,''),m.dependency_status
                    FROM media m JOIN profiles p ON p.profile_id = $profile
                    WHERE m.media_id = $media AND m.media_type = 'MODEL' AND m.state = 'ACTIVE' AND m.trashed_at_ms IS NULL
                      AND m.sha256 IS NOT NULL AND p.kind = 'NORMAL' AND p.trashed_at_ms IS NULL AND p.visibility = 'PUBLISHED'
                      AND EXISTS (SELECT 1 FROM profile_media pm WHERE pm.profile_id = $profile AND pm.media_id = m.media_id AND pm.publication_import_unit_id IS NULL)
                      AND EXISTS (SELECT 1 FROM media_assets a WHERE a.media_id = m.media_id AND a.role = 'MODEL_RENDER' AND a.state = 'READY' AND a.contract_version = 1)
                      AND NOT EXISTS (SELECT 1 FROM trash_entries te WHERE te.entity_type = 'MEDIA' AND te.entity_id = m.media_id AND te.state IN ('PENDING','EXECUTING'));
                    """);
                check.Parameters.AddWithValue("$profile",DbGuid.Format(profileId)); check.Parameters.AddWithValue("$media",DbGuid.Format(figure));
                await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || !ModelRenderEligibility.IsEligible(reader.GetString(0),reader.GetString(1)))
                    return (OperationResult<ProfileAppearanceOutcome>.Validation(OperationErrorCode.AppearanceMediaInvalid,"Figure requires a ready eligible GLB publicly linked to this Profile."),overrides);
            }
            await using var selection = transaction.CreateCommand("UPDATE profile_appearance SET figure_media_id = $figure WHERE profile_id = $profile;");
            selection.Parameters.AddWithValue("$profile",DbGuid.Format(profileId));
            selection.Parameters.AddWithValue("$figure",sources.FigureMediaId is { } id ? DbGuid.Format(id) : DBNull.Value);
            await selection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return (null, result);
    }

    private static async Task<ProfileAppearanceOverrides> ReadOverridesInTransactionAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            "SELECT overrides_json FROM profile_appearance WHERE profile_id = $profileId;");
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        var stored = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return stored is string json
            ? ProfileAppearanceOverrides.Parse(json)
            : ProfileAppearanceOverrides.Default;
    }

    private async Task WriteOverridesAsync(
        CatalogTransaction transaction,
        Guid profileId,
        ProfileAppearanceOverrides overrides,
        CancellationToken cancellationToken)
    {
        await using var upsert = transaction.CreateCommand(
            """
            INSERT INTO profile_appearance(
                profile_id, schema_version, layout_preset_id, overrides_json, updated_at_ms, row_version)
            VALUES ($profileId, $schemaVersion, $layoutPresetId, $overridesJson, $now, 1)
            ON CONFLICT(profile_id) DO UPDATE SET
                schema_version = excluded.schema_version,
                overrides_json = excluded.overrides_json,
                updated_at_ms = excluded.updated_at_ms,
                row_version = profile_appearance.row_version + 1;
            """);
        upsert.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        upsert.Parameters.AddWithValue("$schemaVersion", ProfileAppearanceOverrides.SchemaVersion);
        upsert.Parameters.AddWithValue("$layoutPresetId", ProfileLayoutResolver.FallbackPresetId);
        upsert.Parameters.AddWithValue("$overridesJson", overrides.ToJson());
        upsert.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OperationResult<ProfileAppearanceOutcome>?> ValidateProfileAsync(
        CatalogTransaction transaction,
        Guid profileId,
        long expectedRowVersion,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT kind, trashed_at_ms, row_version
            FROM profiles
            WHERE profile_id = $profileId;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return OperationResult<ProfileAppearanceOutcome>.NotFound(
                OperationErrorCode.ProfileNotFound,
                "That Profile no longer exists.");
        }

        var kind = DbEnum.ParseProfileKind(reader.GetString(0));
        var isTrashed = !reader.IsDBNull(1);
        var currentRowVersion = reader.GetInt64(2);

        if (kind != ProfileKind.Normal)
        {
            return OperationResult<ProfileAppearanceOutcome>.Validation(
                OperationErrorCode.ProfileNotNormal,
                "An Unknown Profile has no Cover, Banner, or appearance of its own.");
        }

        if (isTrashed)
        {
            return OperationResult<ProfileAppearanceOutcome>.Conflict(
                OperationErrorCode.ProfileConflict,
                "That Profile was moved to Trash and cannot be restyled.");
        }

        if (currentRowVersion != expectedRowVersion)
        {
            return OperationResult<ProfileAppearanceOutcome>.Conflict(
                OperationErrorCode.ProfileConflict,
                "That Profile changed somewhere else. Reload it and apply your change again.");
        }

        return null;
    }

    private static async Task<OperationResult<ProfileAppearanceOutcome>?> ValidateAppearanceMediaAsync(
        CatalogTransaction transaction,
        Guid profileId,
        Guid assetId,
        Func<MediaType, bool> isMediaTypeEligible,
        string ineligibleMessage,
        CancellationToken cancellationToken)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("Media identifier cannot be empty.", nameof(assetId));
        }

        var asset = await ReadAppearanceMediaAsync(transaction, assetId, cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            return OperationResult<ProfileAppearanceOutcome>.NotFound(
                OperationErrorCode.MediaNotFound,
                "That media item no longer exists.");
        }

        var hasRelation = await HasAnyRelationAsync(transaction, profileId, assetId, cancellationToken)
            .ConfigureAwait(false);

        if (!ProfileAppearanceRules.ValidateEligibility(
                ProfileKind.Normal,
                isProfileActive: true,
                asset.State,
                isMediaActive: !asset.IsTrashed,
                hasRelation))
        {
            return OperationResult<ProfileAppearanceOutcome>.Validation(
                OperationErrorCode.AppearanceMediaInvalid,
                "That media item is not an active item linked to this Profile.");
        }

        return isMediaTypeEligible(asset.MediaType)
            ? null
            : OperationResult<ProfileAppearanceOutcome>.Validation(
                OperationErrorCode.AppearanceMediaInvalid,
                ineligibleMessage);
    }

    private static async Task<AppearanceMedia?> ReadAppearanceMediaAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT state, media_type, trashed_at_ms
            FROM media
            WHERE media_id = $assetId;
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AppearanceMedia(
            DbEnum.ParseMediaState(reader.GetString(0)),
            DbEnum.ParseMediaType(reader.GetString(1)),
            !reader.IsDBNull(2));
    }

    private static async Task<bool> HasAnyRelationAsync(
        CatalogTransaction transaction,
        Guid profileId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT EXISTS(
                SELECT 1 FROM profile_media
                WHERE profile_id = $profileId AND media_id = $assetId
            );
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var found = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(found, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task AppendAppearanceActivityAsync(
        CatalogTransaction transaction,
        Guid profileId,
        string changeKind,
        long occurredAtMs,
        CancellationToken cancellationToken)
    {
        await using var insert = transaction.CreateCommand(
            """
            INSERT INTO activity_log(
                activity_id, event_type, profile_id, payload_json, occurred_at_ms)
            VALUES ($activityId, $eventType, $profileId, $payloadJson, $occurredAtMs);
            """);
        insert.Parameters.AddWithValue("$activityId", DbGuid.Format(Guid.NewGuid()));
        insert.Parameters.AddWithValue("$eventType", ActivityEventType.ProfileAppearanceChanged);
        insert.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        insert.Parameters.AddWithValue("$payloadJson", $"{{\"change\":\"{changeKind}\"}}");
        insert.Parameters.AddWithValue("$occurredAtMs", occurredAtMs);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(Guid Id, long? SourceTimestampMs, long? DurationMs)?>
        FindReadyMediaAssetAsync(CatalogTransaction transaction, Guid profileId, Guid mediaId,
            MediaAssetRole role, Guid? requestedId, CancellationToken ct)
    {
        await using var command = transaction.CreateCommand("""
            SELECT ma.media_asset_id, ma.source_timestamp_ms, ma.duration_ms
            FROM media_assets ma
            JOIN media m ON m.media_id = ma.media_id
            JOIN profile_media pm ON pm.media_id = m.media_id
            WHERE ma.media_id = $media AND ma.role = $role AND ma.state = 'READY'
              AND m.state = 'ACTIVE' AND pm.profile_id = $profile
              AND pm.publication_import_unit_id IS NULL
              AND ($requested IS NULL OR ma.media_asset_id = $requested)
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$media", DbGuid.Format(mediaId));
        command.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$role", DbEnum.Format(role));
        command.Parameters.AddWithValue("$requested", requestedId is { } id
            ? DbGuid.Format(id) : DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? (DbGuid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2))
            : null;
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Profile identifier cannot be empty.", parameterName);
        }
    }

    private sealed record AppearanceMedia(MediaState State, MediaType MediaType, bool IsTrashed);
}

public sealed record SetCoverMediaRequest(
    Guid ProfileId,
    Guid? CoverMediaId,
    long ExpectedProfileRowVersion,
    long? CoverVideoTimestampMilliseconds = null,
    Guid? CoverMediaAssetId = null);

public sealed record ProfileAppearanceOutcome(Guid ProfileId, long RowVersion);

internal sealed record ProfilePresentationWriteResult(OperationResult<ProfileAppearanceOutcome> Result);

/// <summary>A Profile's committed presentation, applied as one transaction.</summary>
public sealed record ApplyProfilePresentationRequest(
    Guid ProfileId,
    long ExpectedProfileRowVersion,
    bool ApplyLayout,
    string? LayoutPresetId,
    ProfileAppearanceOverrides Overrides,
    ProfileMediaSourceChange? Sources = null);

/// <summary>Cover/Banner source edits committed in the same transaction as the Profile presentation.</summary>
public sealed record ProfileMediaSourceChange(
    bool CoverChanged,
    Guid? CoverMediaId,
    bool BannerChanged,
    Guid? BannerMediaId,
    Guid? CoverMediaAssetId = null,
    Guid? BannerMediaAssetId = null,
    bool FigureChanged = false, Guid? FigureMediaId = null);

public sealed record ProfileAppearanceOverrides(
    string? CoverShape,
    string? CoverFrameId,
    double? CoverFrameScale,
    string? CoverFrameTint,
    double? CoverFrameIntensity,
    string? CoverFrameAnimation,
    bool CoverShadow,
    double CropX,
    double CropY,
    double Zoom,
    double BannerFocusX,
    double BannerFocusY,
    double BannerZoom,
    string? ProfileCardVariantId = null,
    string? CoverSourceKind = null,
    long? CoverVideoTimestampMilliseconds = null,
    string? CoverFit = null,
    double CoverOffsetX = 0,
    double CoverOffsetY = 0,
    double CoverRotation = 0,
    string? MediaLayoutId = null,
    bool ShowRecentMedia = true)
{
    // First canonical Profile appearance contract. The fresh v1 catalog has no appearance migrations.
    public const int SchemaVersion = 1;

    public const double MaximumOffset = 1;
    public const double MaximumRotation = 180;

    public static IReadOnlyList<string> FitModes { get; } = ["fill", "fit"];

    public static ProfileAppearanceOverrides Default { get; } = new(
        CoverShape: null,
        CoverFrameId: "none",
        CoverFrameScale: null,
        CoverFrameTint: null,
        CoverFrameIntensity: null,
        CoverFrameAnimation: null,
        CoverShadow: true,
        CropX: ProfileAppearanceRules.DefaultCoverCrop,
        CropY: ProfileAppearanceRules.DefaultCoverCrop,
        Zoom: ProfileAppearanceRules.MinimumCoverZoom,
        BannerFocusX: ProfileAppearanceRules.DefaultBannerFocus,
        BannerFocusY: ProfileAppearanceRules.DefaultBannerFocus,
        BannerZoom: ProfileAppearanceRules.MinimumBannerZoom,
        ProfileCardVariantId: "editorial",
        CoverSourceKind: null,
        MediaLayoutId: MediaLayoutCatalog.FallbackLayoutId,
        CoverVideoTimestampMilliseconds: null,
        ShowRecentMedia: true);

    public CoverVisualSourceKind ResolvedCoverSourceKind =>
        Enum.TryParse<CoverVisualSourceKind>(CoverSourceKind, ignoreCase: true, out var parsed)
            ? parsed
            : CoverVisualSourceKind.Image;

    public bool IsVideoFrameCover =>
        ResolvedCoverSourceKind == CoverVisualSourceKind.VideoFrame
        && CoverVideoTimestampMilliseconds is >= 0;

    public long? CoverThumbnailTimestampMilliseconds =>
        IsVideoFrameCover ? CoverVideoTimestampMilliseconds : null;

    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            WriteOptionalString(writer, "coverShape", CoverShape);
            WriteOptionalString(writer, "coverFrameId", CoverFrameId);
            WriteOptionalNumber(writer, "coverFrameScale", CoverFrameScale);
            WriteOptionalString(writer, "coverFrameTint", CoverFrameTint);
            WriteOptionalNumber(writer, "coverFrameIntensity", CoverFrameIntensity);
            WriteOptionalString(writer, "coverFrameAnimation", CoverFrameAnimation);
            writer.WriteBoolean("coverShadow", CoverShadow);
            writer.WriteNumber("cropX", CropX);
            writer.WriteNumber("cropY", CropY);
            writer.WriteNumber("zoom", Zoom);
            writer.WriteNumber("bannerFocusX", BannerFocusX);
            writer.WriteNumber("bannerFocusY", BannerFocusY);
            writer.WriteNumber("bannerZoom", BannerZoom);
            WriteOptionalString(writer, "profileCardVariantId", ProfileCardVariantId);
            WriteOptionalString(writer, "coverSourceKind", CoverSourceKind);
            if (CoverVideoTimestampMilliseconds is { } coverTimestamp)
            {
                writer.WriteNumber("coverVideoTimestampMs", coverTimestamp);
            }
            else
            {
                writer.WriteNull("coverVideoTimestampMs");
            }

            WriteOptionalString(writer, "coverFit", CoverFit);
            writer.WriteNumber("coverOffsetX", CoverOffsetX);
            writer.WriteNumber("coverOffsetY", CoverOffsetY);
            writer.WriteNumber("coverRotation", CoverRotation);
            WriteOptionalString(writer, "mediaLayoutId", MediaLayoutId);
            writer.WriteBoolean("showRecentMedia", ShowRecentMedia);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static ProfileAppearanceOverrides Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Default;
            }

            var root = document.RootElement;
            var version = (int)ReadNumber(root, "schemaVersion", SchemaVersion);
            if (version != SchemaVersion)
            {
                return Default;
            }

            return new ProfileAppearanceOverrides(
                ReadString(root, "coverShape"),
                ReadString(root, "coverFrameId") ?? Default.CoverFrameId,
                ReadOptionalNumber(root, "coverFrameScale"),
                ReadString(root, "coverFrameTint"),
                ReadOptionalNumber(root, "coverFrameIntensity"),
                ReadString(root, "coverFrameAnimation"),
                ReadBoolean(root, "coverShadow", Default.CoverShadow),
                ReadNumber(root, "cropX", Default.CropX),
                ReadNumber(root, "cropY", Default.CropY),
                ReadNumber(root, "zoom", Default.Zoom),
                ReadNumber(root, "bannerFocusX", Default.BannerFocusX),
                ReadNumber(root, "bannerFocusY", Default.BannerFocusY),
                ReadNumber(root, "bannerZoom", Default.BannerZoom),
                ReadString(root, "profileCardVariantId") ?? Default.ProfileCardVariantId,
                ReadString(root, "coverSourceKind"),
                ReadOptionalLong(root, "coverVideoTimestampMs"),
                ReadString(root, "coverFit"),
                ReadNumber(root, "coverOffsetX", 0),
                ReadNumber(root, "coverOffsetY", 0),
                ReadNumber(root, "coverRotation", 0),
                MediaLayoutCatalog.Normalize(ReadString(root, "mediaLayoutId")),
                ReadBoolean(root, "showRecentMedia", Default.ShowRecentMedia));
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public CoverAppearanceRequest ToCoverAppearanceRequest() => new(
        CoverShape,
        CoverFrameId,
        CoverFrameScale,
        CoverFrameTint,
        CoverFrameIntensity,
        CoverFrameAnimation,
        CoverShadow);

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteOptionalNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? ReadOptionalLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var value)
            ? value
            : null;

    private static double? ReadOptionalNumber(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetDouble()
            : null;

    private static double ReadNumber(JsonElement root, string name, double fallback) =>
        ReadOptionalNumber(root, name) ?? fallback;

    private static bool ReadBoolean(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : fallback;
}
