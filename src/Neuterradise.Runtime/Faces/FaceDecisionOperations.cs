using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Faces;

public sealed class FaceDecisionOperations
{

    private const string FaceConfirmationProvenance = "face-confirmation";

    private readonly CatalogDb _catalog;
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public FaceDecisionOperations(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OperationResult<FaceDecisionOutcome>> ConfirmFaceAsync(
        ConfirmFaceCommand command,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(ConfirmFaceAsync));
        ArgumentNullException.ThrowIfNull(command);
        EnsureNonEmpty(command.FaceId, nameof(command.FaceId));
        EnsureNonEmpty(command.ProfileId, nameof(command.ProfileId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var detection = await ReadDetectionAsync(transaction, command.FaceId, cancellationToken).ConfigureAwait(false);
        if (detection is null)
        {
            return OperationResult<FaceDecisionOutcome>.NotFound(
                OperationErrorCode.FaceNotFound,
                "That face detection no longer exists.");
        }

        var asset = await ReadMediaAsync(transaction, detection.MediaId, cancellationToken).ConfigureAwait(false);
        if (asset.State != MediaState.Active || asset.IsTrashed)
        {
            return OperationResult<FaceDecisionOutcome>.Validation(
                OperationErrorCode.FaceMediaNotActive,
                "A face can only be confirmed against an active media item.");
        }

        var targetIdentityId = await ResolveActiveNormalIdentityAsync(
            transaction, command.ProfileId, cancellationToken).ConfigureAwait(false);
        if (targetIdentityId is null)
        {
            return OperationResult<FaceDecisionOutcome>.Validation(
                OperationErrorCode.FaceTargetInvalid,
                "A face can only be confirmed against an active Profile.");
        }

        if (detection.DecisionState == FaceDecisionState.Confirmed)
        {
            if (detection.ConfirmedIdentityId == targetIdentityId)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return OperationResult<FaceDecisionOutcome>.Success(
                    new FaceDecisionOutcome(
                        command.FaceId,
                        FaceDecisionState.Confirmed,
                        detection.RowVersion,
                        IdentitySampleCreated: false,
                        AppearsRelationEnsured: false));
            }

            return OperationResult<FaceDecisionOutcome>.Conflict(
                OperationErrorCode.FaceDecisionStateInvalid,
                "That face is already confirmed to someone else. Change the confirmation instead.");
        }

        if (detection.RowVersion != command.ExpectedRowVersion)
        {
            return OperationResult<FaceDecisionOutcome>.Conflict(
                OperationErrorCode.FaceConflict,
                "That face changed somewhere else. Reload it and try again.");
        }

        ValidateEmbeddingProvenance(command.FaceId, detection);

        var owner = await ReadOwnerAsync(transaction, detection.MediaId, cancellationToken).ConfigureAwait(false);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var newRowVersion = detection.RowVersion + 1;

        await SetDecisionAsync(
            transaction,
            command.FaceId,
            detection.RowVersion,
            newRowVersion,
            FaceDecisionState.Confirmed,
            targetIdentityId,
            now,
            cancellationToken).ConfigureAwait(false);

        var embeddingSpace = GetEmbeddingSpace(detection);
        var sampleCreated = false;
        if (detection.Embedding is not null)
        {
            var sampleSpace = embeddingSpace
                ?? throw new CatalogInvariantException(
                    $"Face {command.FaceId:D} carries an embedding without canonical embedding provenance.");
            await InsertIdentitySampleAsync(
                transaction,
                command.FaceId,
                targetIdentityId.Value,
                detection.Embedding,
                sampleSpace.Canonical,
                sampleSpace.ModelId,
                sampleSpace.ModelVersion,
                now,
                cancellationToken).ConfigureAwait(false);
            sampleCreated = true;
        }

        var appearsEnsured = owner.ProfileId != command.ProfileId;
        if (appearsEnsured)
        {
            await EnsureAppearsAsync(
                transaction, command.ProfileId, detection.MediaId, now, cancellationToken).ConfigureAwait(false);
        }

        var evidencePair = ResolveConfirmedFacePair(owner, command.ProfileId);
        if (evidencePair is { } pair)
        {
            await EnsureConfirmedFaceEvidenceAsync(
                transaction, pair, detection.MediaId, command.FaceId, now, cancellationToken).ConfigureAwait(false);
            await RebuildSummaryAsync(transaction, pair.Low, pair.High, now, cancellationToken).ConfigureAwait(false);
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [pair.Low, pair.High],
                CatalogInvalidationDomain.Related,
                0));
        }

        // Queue face decision invalidations.
        var profileIds = new HashSet<Guid> { detection.MediaId == Guid.Empty ? Guid.Empty : owner.ProfileId, command.ProfileId };
        profileIds.Remove(Guid.Empty);
        transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [command.FaceId], CatalogInvalidationDomain.Face, 0));
        transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [detection.MediaId], CatalogInvalidationDomain.Media, 0));
        transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [], CatalogInvalidationDomain.Health, 0));
        if (profileIds.Count > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [.. profileIds], CatalogInvalidationDomain.Profile, 0));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (sampleCreated && embeddingSpace is { } committedSpace)
        {
            IdentityBankProvider.InvalidateCatalogSpace(_catalog, committedSpace);
        }

        return OperationResult<FaceDecisionOutcome>.Success(
            new FaceDecisionOutcome(
                command.FaceId,
                FaceDecisionState.Confirmed,
                newRowVersion,
                sampleCreated,
                appearsEnsured));
    }

    public async Task<OperationResult<FaceDecisionOutcome>> RejectFaceAsync(
        RejectFaceCommand command,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(RejectFaceAsync));
        ArgumentNullException.ThrowIfNull(command);
        EnsureNonEmpty(command.FaceId, nameof(command.FaceId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var detection = await ReadDetectionAsync(transaction, command.FaceId, cancellationToken).ConfigureAwait(false);
        if (detection is null)
        {
            return OperationResult<FaceDecisionOutcome>.NotFound(
                OperationErrorCode.FaceNotFound,
                "That face detection no longer exists.");
        }

        if (detection.DecisionState == FaceDecisionState.Rejected)
        {
            // Idempotent: already rejected, no mutation.
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult<FaceDecisionOutcome>.Success(
                new FaceDecisionOutcome(
                    command.FaceId,
                    FaceDecisionState.Rejected,
                    detection.RowVersion,
                    IdentitySampleCreated: false,
                    AppearsRelationEnsured: false));
        }

        if (detection.DecisionState == FaceDecisionState.Confirmed)
        {
            return OperationResult<FaceDecisionOutcome>.Conflict(
                OperationErrorCode.FaceDecisionStateInvalid,
                "That face is already confirmed. Change the confirmation instead of rejecting it.");
        }

        if (detection.RowVersion != command.ExpectedRowVersion)
        {
            return OperationResult<FaceDecisionOutcome>.Conflict(
                OperationErrorCode.FaceConflict,
                "That face changed somewhere else. Reload it and try again.");
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var newRowVersion = detection.RowVersion + 1;

        await SetDecisionAsync(
            transaction,
            command.FaceId,
            detection.RowVersion,
            newRowVersion,
            FaceDecisionState.Rejected,
            confirmedIdentityId: null,
            now,
            cancellationToken).ConfigureAwait(false);

        // Queue face decision invalidations for real rejection.
        var owner = await ReadOwnerAsync(transaction, detection.MediaId, cancellationToken).ConfigureAwait(false);
        transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [command.FaceId], CatalogInvalidationDomain.Face, 0));
        transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [detection.MediaId], CatalogInvalidationDomain.Media, 0));
        transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [], CatalogInvalidationDomain.Health, 0));
        transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [owner.ProfileId], CatalogInvalidationDomain.Profile, 0));

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return OperationResult<FaceDecisionOutcome>.Success(
            new FaceDecisionOutcome(
                command.FaceId,
                FaceDecisionState.Rejected,
                newRowVersion,
                IdentitySampleCreated: false,
                AppearsRelationEnsured: false));
    }

    public async Task<OperationResult<FaceDecisionOutcome>> ChangeFaceConfirmationAsync(
        ChangeFaceConfirmationCommand command,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(ChangeFaceConfirmationAsync));
        ArgumentNullException.ThrowIfNull(command);
        EnsureNonEmpty(command.FaceId, nameof(command.FaceId));
        if (command.ProfileId is Guid profileId)
        {
            EnsureNonEmpty(profileId, nameof(command.ProfileId));
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var detection = await ReadDetectionAsync(transaction, command.FaceId, cancellationToken).ConfigureAwait(false);
        if (detection is null)
        {
            return OperationResult<FaceDecisionOutcome>.NotFound(
                OperationErrorCode.FaceNotFound,
                "That face detection no longer exists.");
        }

        if (detection.DecisionState != FaceDecisionState.Confirmed)
        {
            if (detection.DecisionState == FaceDecisionState.Rejected && command.ProfileId is null)
            {
                // Idempotent: already rejected and no new profile.
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return OperationResult<FaceDecisionOutcome>.Success(
                    new FaceDecisionOutcome(
                        command.FaceId,
                        FaceDecisionState.Rejected,
                        detection.RowVersion,
                        IdentitySampleCreated: false,
                        AppearsRelationEnsured: false));
            }

            return OperationResult<FaceDecisionOutcome>.Validation(
                OperationErrorCode.FaceDecisionStateInvalid,
                "Only a confirmed face can have its confirmation changed.");
        }

        var oldIdentityId = detection.ConfirmedIdentityId
            ?? throw new CatalogInvariantException($"Confirmed face {command.FaceId:D} has no confirmed Identity.");
        var oldProfileId = await ResolveProfileOfIdentityAsync(transaction, oldIdentityId, cancellationToken).ConfigureAwait(false)
            ?? throw new CatalogInvariantException($"Identity {oldIdentityId:D} has no linked Profile.");

        Guid? newIdentityId = null;
        if (command.ProfileId is Guid newProfileId)
        {
            if (newProfileId == oldProfileId)
            {
                // Idempotent: same confirmation target.
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return OperationResult<FaceDecisionOutcome>.Success(
                    new FaceDecisionOutcome(
                        command.FaceId,
                        FaceDecisionState.Confirmed,
                        detection.RowVersion,
                        IdentitySampleCreated: false,
                        AppearsRelationEnsured: false));
            }

            newIdentityId = await ResolveActiveNormalIdentityAsync(transaction, newProfileId, cancellationToken).ConfigureAwait(false);
            if (newIdentityId is null)
            {
                return OperationResult<FaceDecisionOutcome>.Validation(
                    OperationErrorCode.FaceTargetInvalid,
                    "A face can only be confirmed against an active Profile.");
            }
        }

        if (detection.RowVersion != command.ExpectedRowVersion)
        {
            return OperationResult<FaceDecisionOutcome>.Conflict(
                OperationErrorCode.FaceConflict,
                "That face changed somewhere else. Reload it and try again.");
        }

        var asset = await ReadMediaAsync(transaction, detection.MediaId, cancellationToken).ConfigureAwait(false);
        if (asset.State != MediaState.Active || asset.IsTrashed)
        {
            return OperationResult<FaceDecisionOutcome>.Validation(
                OperationErrorCode.FaceMediaNotActive,
                "A face can only be confirmed against an active media item.");
        }

        var owner = await ReadOwnerAsync(transaction, detection.MediaId, cancellationToken).ConfigureAwait(false);
        var embeddingSpace = GetEmbeddingSpace(detection);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var newRowVersion = detection.RowVersion + 1;

        var removedPairs = await RemoveConfirmedFaceEvidenceAsync(
            transaction, command.FaceId, cancellationToken).ConfigureAwait(false);
        await RemoveIdentitySampleAsync(transaction, command.FaceId, cancellationToken).ConfigureAwait(false);
        await RemoveFaceDerivedAppearsAsync(
            transaction, oldProfileId, detection.MediaId, command.FaceId, oldIdentityId, cancellationToken).ConfigureAwait(false);

        var sampleCreated = false;
        var appearsEnsured = false;
        ProfilePair? addedPair = null;

        if (newIdentityId is Guid newId)
        {
            await SetDecisionAsync(
                transaction,
                command.FaceId,
                detection.RowVersion,
                newRowVersion,
                FaceDecisionState.Confirmed,
                newId,
                now,
                cancellationToken).ConfigureAwait(false);

            if (detection.Embedding is not null)
            {
                var sampleSpace = embeddingSpace
                    ?? throw new CatalogInvariantException(
                        $"Face {command.FaceId:D} carries an embedding without canonical embedding provenance.");
                await InsertIdentitySampleAsync(
                    transaction,
                    command.FaceId,
                    newId,
                    detection.Embedding,
                    sampleSpace.Canonical,
                    sampleSpace.ModelId,
                    sampleSpace.ModelVersion,
                    now,
                    cancellationToken).ConfigureAwait(false);
                sampleCreated = true;
            }

            appearsEnsured = owner.ProfileId != command.ProfileId!.Value;
            if (appearsEnsured)
            {
                await EnsureAppearsAsync(
                    transaction, command.ProfileId!.Value, detection.MediaId, now, cancellationToken).ConfigureAwait(false);
            }

            addedPair = ResolveConfirmedFacePair(owner, command.ProfileId!.Value);
            if (addedPair is { } pair)
            {
                await EnsureConfirmedFaceEvidenceAsync(
                    transaction, pair, detection.MediaId, command.FaceId, now, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await SetDecisionAsync(
                transaction,
                command.FaceId,
                detection.RowVersion,
                newRowVersion,
                FaceDecisionState.Rejected,
                confirmedIdentityId: null,
                now,
                cancellationToken).ConfigureAwait(false);
        }

        var affectedPairs = new HashSet<ProfilePair>(removedPairs);
        if (addedPair is { } added)
        {
            affectedPairs.Add(added);
        }

        foreach (var pair in affectedPairs.OrderBy(static pair => pair.Low).ThenBy(static pair => pair.High))
        {
            await RebuildSummaryAsync(transaction, pair.Low, pair.High, now, cancellationToken).ConfigureAwait(false);
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [pair.Low, pair.High],
                CatalogInvalidationDomain.Related,
                0));
        }

        // Queue face decision invalidations.
        var affectedProfileIds = new HashSet<Guid> { owner.ProfileId, oldProfileId };
        if (command.ProfileId is Guid newPid) affectedProfileIds.Add(newPid);
        affectedProfileIds.Remove(Guid.Empty);
        transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [command.FaceId], CatalogInvalidationDomain.Face, 0));
        transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [detection.MediaId], CatalogInvalidationDomain.Media, 0));
        transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [], CatalogInvalidationDomain.Health, 0));
        if (affectedProfileIds.Count > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [.. affectedProfileIds], CatalogInvalidationDomain.Profile, 0));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (embeddingSpace is { } committedSpace)
        {
            IdentityBankProvider.InvalidateCatalogSpace(_catalog, committedSpace);
        }

        return OperationResult<FaceDecisionOutcome>.Success(
            new FaceDecisionOutcome(
                command.FaceId,
                newIdentityId is null ? FaceDecisionState.Rejected : FaceDecisionState.Confirmed,
                newRowVersion,
                sampleCreated,
                appearsEnsured));
    }

    private static EmbeddingSpaceKey? GetEmbeddingSpace(DetectionRow detection)
    {
        if (detection.Embedding is null)
        {
            return null;
        }

        if (!EmbeddingSpaceKey.TryParse(detection.EmbeddingSpaceKey, out var space))
        {
            throw new CatalogInvariantException(
                "An embedding-bearing FaceDetection must carry a canonical SFace embedding-space provenance key.");
        }

        return space;
    }

    private static async Task<DetectionRow?> ReadDetectionAsync(
        CatalogTransaction transaction,
        Guid faceId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT media_id, decision_state, confirmed_identity_id, embedding, embedding_space_key,
                   model_id, model_version, row_version
            FROM face_detections
            WHERE face_id = $faceId;
            """);
        command.Parameters.AddWithValue("$faceId", DbGuid.Format(faceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DetectionRow(
            DbGuid.Parse(reader.GetString(0)),
            DbEnum.ParseFaceDecisionState(reader.GetString(1)),
            reader.IsDBNull(2) ? null : DbGuid.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7));
    }

    private static async Task<MediaRow> ReadMediaAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT state, trashed_at_ms
            FROM media
            WHERE media_id = $assetId;
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new CatalogInvariantException($"Face detection references missing Media {assetId:D}.");
        }

        return new MediaRow(DbEnum.ParseMediaState(reader.GetString(0)), !reader.IsDBNull(1));
    }

    private static async Task<OwnerRow> ReadOwnerAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT pa.profile_id, p.kind
            FROM profile_media pa
            JOIN profiles p ON p.profile_id = pa.profile_id
            WHERE pa.media_id = $assetId AND pa.relation_type = 'OWNER';
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new CatalogInvariantException($"ACTIVE Media {assetId:D} has no OWNER.");
        }

        return new OwnerRow(DbGuid.Parse(reader.GetString(0)), DbEnum.ParseProfileKind(reader.GetString(1)));
    }

    private static async Task<Guid?> ResolveActiveNormalIdentityAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT i.identity_id
            FROM identities i
            JOIN profiles p ON p.profile_id = i.profile_id
            WHERE p.profile_id = $profileId
              AND p.kind = 'NORMAL'
              AND p.trashed_at_ms IS NULL
              AND i.is_active = 1;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string identityId ? DbGuid.Parse(identityId) : null;
    }

    private static async Task<Guid?> ResolveProfileOfIdentityAsync(
        CatalogTransaction transaction,
        Guid identityId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT profile_id
            FROM identities
            WHERE identity_id = $identityId;
            """);
        command.Parameters.AddWithValue("$identityId", DbGuid.Format(identityId));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string profileId ? DbGuid.Parse(profileId) : null;
    }

    private static void ValidateEmbeddingProvenance(Guid faceId, DetectionRow detection)
    {
        if (detection.Embedding is null)
        {
            return;
        }

        string? failure = null;
        var valid = EmbeddingSpaceKey.TryParse(detection.EmbeddingSpaceKey, out var space)
            && FaceEmbedding.TryFromBlob(detection.Embedding, space, out _, out failure);

        if (!valid)
        {
            throw new CatalogInvariantException(
                $"Face {faceId:D} carries embedding provenance that violates its space contract: {failure}.");
        }
    }

    private static async Task SetDecisionAsync(
        CatalogTransaction transaction,
        Guid faceId,
        long expectedRowVersion,
        long newRowVersion,
        FaceDecisionState decision,
        Guid? confirmedIdentityId,
        long nowMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var update = transaction.CreateCommand(
            """
            UPDATE face_detections
            SET decision_state = $decision,
                confirmed_identity_id = $confirmedIdentityId,
                updated_at_ms = $now,
                row_version = $newRowVersion
            WHERE face_id = $faceId AND row_version = $expectedRowVersion;
            """);
        update.Parameters.AddWithValue("$decision", DbEnum.Format(decision));
        update.Parameters.AddWithValue(
            "$confirmedIdentityId",
            confirmedIdentityId is null ? DBNull.Value : DbGuid.Format(confirmedIdentityId.Value));
        update.Parameters.AddWithValue("$now", nowMilliseconds);
        update.Parameters.AddWithValue("$newRowVersion", newRowVersion);
        update.Parameters.AddWithValue("$faceId", DbGuid.Format(faceId));
        update.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);

        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CatalogInvariantException($"Face {faceId:D} decision lost concurrency validation.");
        }
    }

    private static async Task InsertIdentitySampleAsync(
        CatalogTransaction transaction,
        Guid faceId,
        Guid identityId,
        byte[] embedding,
        string embeddingSpaceKey,
        string modelId,
        string modelVersion,
        long confirmedAtMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var insert = transaction.CreateCommand(
            """
            INSERT INTO identity_samples(
                identity_sample_id, identity_id, face_id, embedding,
                embedding_space_key, model_id, model_version, confirmed_at_ms)
            VALUES ($sampleId, $identityId, $faceId, $embedding,
                    $embeddingSpaceKey, $modelId, $modelVersion, $confirmedAtMs);
            """);
        insert.Parameters.AddWithValue("$sampleId", DbGuid.Format(Guid.NewGuid()));
        insert.Parameters.AddWithValue("$identityId", DbGuid.Format(identityId));
        insert.Parameters.AddWithValue("$faceId", DbGuid.Format(faceId));
        insert.Parameters.AddWithValue("$embedding", embedding);
        insert.Parameters.AddWithValue("$embeddingSpaceKey", embeddingSpaceKey);
        insert.Parameters.AddWithValue("$modelId", modelId);
        insert.Parameters.AddWithValue("$modelVersion", modelVersion);
        insert.Parameters.AddWithValue("$confirmedAtMs", confirmedAtMilliseconds);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureAppearsAsync(
        CatalogTransaction transaction,
        Guid profileId,
        Guid assetId,
        long nowMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var insert = transaction.CreateCommand(
            """
            INSERT OR IGNORE INTO profile_media(profile_id, media_id, relation_type, provenance_key, created_at_ms)
            VALUES ($profileId, $assetId, 'APPEARS', $provenanceKey, $now);
            """);
        insert.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        insert.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        insert.Parameters.AddWithValue("$provenanceKey", FaceConfirmationProvenance);
        insert.Parameters.AddWithValue("$now", nowMilliseconds);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ProfilePair? ResolveConfirmedFacePair(OwnerRow owner, Guid targetProfileId)
    {
        if (owner.Kind != ProfileKind.Normal || owner.ProfileId == targetProfileId)
        {
            return null;
        }

        return CanonicalizePair(owner.ProfileId, targetProfileId);
    }

    private static async Task EnsureConfirmedFaceEvidenceAsync(
        CatalogTransaction transaction,
        ProfilePair pair,
        Guid assetId,
        Guid faceId,
        long nowMilliseconds,
        CancellationToken cancellationToken)
    {
        var evidenceKey = ConfirmedFaceEvidenceKey(pair, assetId, faceId);
        await using var insert = transaction.CreateCommand(
            """
            INSERT OR IGNORE INTO related_profile_evidence(
                evidence_key, profile_id_low, profile_id_high, evidence_type, media_id, face_id, created_at_ms)
            VALUES ($evidenceKey, $low, $high, 'CONFIRMED_FACE', $assetId, $faceId, $now);
            """);
        insert.Parameters.AddWithValue("$evidenceKey", evidenceKey);
        insert.Parameters.AddWithValue("$low", DbGuid.Format(pair.Low));
        insert.Parameters.AddWithValue("$high", DbGuid.Format(pair.High));
        insert.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        insert.Parameters.AddWithValue("$faceId", DbGuid.Format(faceId));
        insert.Parameters.AddWithValue("$now", nowMilliseconds);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ProfilePair>> RemoveConfirmedFaceEvidenceAsync(
        CatalogTransaction transaction,
        Guid faceId,
        CancellationToken cancellationToken)
    {
        var pairs = new List<ProfilePair>();

        await using (var select = transaction.CreateCommand(
            """
            SELECT profile_id_low, profile_id_high
            FROM related_profile_evidence
            WHERE face_id = $faceId AND evidence_type = 'CONFIRMED_FACE';
            """))
        {
            select.Parameters.AddWithValue("$faceId", DbGuid.Format(faceId));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                pairs.Add(new ProfilePair(DbGuid.Parse(reader.GetString(0)), DbGuid.Parse(reader.GetString(1))));
            }
        }

        await using (var delete = transaction.CreateCommand(
            """
            DELETE FROM related_profile_evidence
            WHERE face_id = $faceId AND evidence_type = 'CONFIRMED_FACE';
            """))
        {
            delete.Parameters.AddWithValue("$faceId", DbGuid.Format(faceId));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return pairs;
    }

    private static async Task RemoveIdentitySampleAsync(
        CatalogTransaction transaction,
        Guid faceId,
        CancellationToken cancellationToken)
    {
        await using var delete = transaction.CreateCommand(
            "DELETE FROM identity_samples WHERE face_id = $faceId;");
        delete.Parameters.AddWithValue("$faceId", DbGuid.Format(faceId));
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RemoveFaceDerivedAppearsAsync(
        CatalogTransaction transaction,
        Guid profileId,
        Guid assetId,
        Guid faceId,
        Guid identityId,
        CancellationToken cancellationToken)
    {
        await using var delete = transaction.CreateCommand(
            """
            DELETE FROM profile_media
            WHERE profile_id = $profileId
              AND media_id = $assetId
              AND relation_type = 'APPEARS'
              AND provenance_key = $provenanceKey
              AND NOT EXISTS (
                  SELECT 1 FROM face_detections
                  WHERE media_id = $assetId
                    AND face_id <> $faceId
                    AND decision_state = 'CONFIRMED'
                    AND confirmed_identity_id = $identityId
              );
            """);
        delete.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        delete.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        delete.Parameters.AddWithValue("$provenanceKey", FaceConfirmationProvenance);
        delete.Parameters.AddWithValue("$faceId", DbGuid.Format(faceId));
        delete.Parameters.AddWithValue("$identityId", DbGuid.Format(identityId));
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RebuildSummaryAsync(
        CatalogTransaction transaction,
        Guid lowProfileId,
        Guid highProfileId,
        long nowMilliseconds,
        CancellationToken cancellationToken)
    {
        int sharedMediaCount;
        int confirmedFaceCount;
        bool manualRelation;
        long? lastEvidenceAt;

        await using (var aggregate = transaction.CreateCommand(
            """
            SELECT COUNT(DISTINCT CASE WHEN evidence_type = 'SHARED_ASSET' THEN media_id END),
                   COUNT(DISTINCT CASE WHEN evidence_type = 'CONFIRMED_FACE' THEN face_id END),
                   MAX(CASE WHEN evidence_type = 'MANUAL' THEN 1 ELSE 0 END),
                   MAX(created_at_ms)
            FROM related_profile_evidence
            WHERE profile_id_low = $low AND profile_id_high = $high;
            """))
        {
            aggregate.Parameters.AddWithValue("$low", DbGuid.Format(lowProfileId));
            aggregate.Parameters.AddWithValue("$high", DbGuid.Format(highProfileId));
            await using var reader = await aggregate.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            sharedMediaCount = reader.GetInt32(0);
            confirmedFaceCount = reader.GetInt32(1);
            manualRelation = !reader.IsDBNull(2) && reader.GetInt32(2) == 1;
            lastEvidenceAt = reader.IsDBNull(3) ? null : reader.GetInt64(3);
        }

        if (lastEvidenceAt is null)
        {
            await using var delete = transaction.CreateCommand(
                """
                DELETE FROM related_profile_summary
                WHERE profile_id_low = $low AND profile_id_high = $high;
                """);
            delete.Parameters.AddWithValue("$low", DbGuid.Format(lowProfileId));
            delete.Parameters.AddWithValue("$high", DbGuid.Format(highProfileId));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var rankScore = sharedMediaCount + (confirmedFaceCount * 2) + (manualRelation ? 1 : 0);
        await using var upsert = transaction.CreateCommand(
            """
            INSERT INTO related_profile_summary(
                profile_id_low, profile_id_high, shared_media_count, confirmed_face_count,
                manual_relation, last_evidence_at_ms, rank_score, updated_at_ms)
            VALUES ($low, $high, $sharedMediaCount, $confirmedFaceCount, $manualRelation, $lastEvidenceAt, $rankScore, $now)
            ON CONFLICT(profile_id_low, profile_id_high) DO UPDATE SET
                shared_media_count = excluded.shared_media_count,
                confirmed_face_count = excluded.confirmed_face_count,
                manual_relation = excluded.manual_relation,
                last_evidence_at_ms = excluded.last_evidence_at_ms,
                rank_score = excluded.rank_score,
                updated_at_ms = excluded.updated_at_ms;
            """);
        upsert.Parameters.AddWithValue("$low", DbGuid.Format(lowProfileId));
        upsert.Parameters.AddWithValue("$high", DbGuid.Format(highProfileId));
        upsert.Parameters.AddWithValue("$sharedMediaCount", sharedMediaCount);
        upsert.Parameters.AddWithValue("$confirmedFaceCount", confirmedFaceCount);
        upsert.Parameters.AddWithValue("$manualRelation", manualRelation ? 1 : 0);
        upsert.Parameters.AddWithValue("$lastEvidenceAt", lastEvidenceAt.Value);
        upsert.Parameters.AddWithValue("$rankScore", rankScore);
        upsert.Parameters.AddWithValue("$now", nowMilliseconds);
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ProfilePair CanonicalizePair(Guid first, Guid second)
    {
        var firstText = DbGuid.Format(first);
        var secondText = DbGuid.Format(second);
        return string.CompareOrdinal(firstText, secondText) < 0
            ? new ProfilePair(first, second)
            : new ProfilePair(second, first);
    }

    private static string ConfirmedFaceEvidenceKey(ProfilePair pair, Guid assetId, Guid faceId) =>
        $"ConfirmedFace|{DbGuid.Format(pair.Low)}|{DbGuid.Format(pair.High)}|{DbGuid.Format(assetId)}|{DbGuid.Format(faceId)}";

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }

    private sealed record DetectionRow(
        Guid MediaId,
        FaceDecisionState DecisionState,
        Guid? ConfirmedIdentityId,
        byte[]? Embedding,
        string? EmbeddingSpaceKey,
        string ModelId,
        string ModelVersion,
        long RowVersion);

    private sealed record MediaRow(MediaState State, bool IsTrashed);

    private sealed record OwnerRow(Guid ProfileId, ProfileKind Kind);

    private readonly record struct ProfilePair(Guid Low, Guid High);
}

public sealed record ConfirmFaceCommand(Guid FaceId, Guid ProfileId, long ExpectedRowVersion);

public sealed record RejectFaceCommand(Guid FaceId, long ExpectedRowVersion);

public sealed record ChangeFaceConfirmationCommand(Guid FaceId, Guid? ProfileId, long ExpectedRowVersion);

public sealed record FaceDecisionOutcome(
    Guid FaceId,
    FaceDecisionState DecisionState,
    long RowVersion,
    bool IdentitySampleCreated,
    bool AppearsRelationEnsured);
