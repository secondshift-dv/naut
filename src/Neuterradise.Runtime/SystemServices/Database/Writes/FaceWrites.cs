using System.Text.Json;
using Neuterradise.App.Faces;

namespace Neuterradise.App.SystemServices.Database.Writes;

public sealed class FaceWrites
{
    private readonly CatalogDb _catalog;
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public FaceWrites(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Guid> PersistDetectionAsync(
        FaceDetectionPersistence detection,
        CancellationToken cancellationToken = default)
    {
        await PersistDetectionsAsync([detection], cancellationToken).ConfigureAwait(false);
        return detection.FaceId;
    }

    public async Task PersistDetectionsAsync(
        IReadOnlyList<FaceDetectionPersistence> detections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detections);
        foreach (var detection in detections) ValidateDetection(detection);
        if (detections.Count == 0) return;

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        foreach (var detection in detections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PersistDetectionInTransactionAsync(transaction, detection, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistDetectionInTransactionAsync(CatalogTransaction transaction,
        FaceDetectionPersistence detection, CancellationToken cancellationToken)
    {

        if (detection.SuggestedIdentityId is Guid suggestedIdentityId)
        {
            await EnsureActiveIdentityAsync(transaction, suggestedIdentityId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (detection.ConfirmedIdentityId is Guid confirmedIdentityId)
        {
            await EnsureActiveIdentityAsync(transaction, confirmedIdentityId, cancellationToken)
                .ConfigureAwait(false);
        }

        var existing = await ReadDetectionAsync(transaction, detection, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureSameDetection(existing, detection);
            await ReconcileSuggestionEvidenceAsync(transaction, existing, detection, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using var insert = transaction.CreateCommand(
            """
            INSERT INTO face_detections(
                face_id, media_id, detection_key, bounding_box_json,
                embedding, embedding_space_key, suggested_identity_id,
                confirmed_identity_id, confidence, decision_state,
                model_id, model_version, sampled_timestamp_ms,
                suggested_candidates_json, created_at_ms, updated_at_ms)
            VALUES (
                $faceId, $assetId, $detectionKey, $boundingBoxJson,
                $embedding, $embeddingSpaceKey, $suggestedIdentityId,
                $confirmedIdentityId, $confidence, $decisionState,
                $modelId, $modelVersion, $sampledTimestampMs,
                $suggestedCandidatesJson, $now, $now);
            """);
        insert.Parameters.AddWithValue("$faceId", DbGuid.Format(detection.FaceId));
        insert.Parameters.AddWithValue("$assetId", DbGuid.Format(detection.MediaId));
        insert.Parameters.AddWithValue("$detectionKey", detection.DetectionKey);
        insert.Parameters.AddWithValue("$boundingBoxJson", detection.BoundingBoxJson);
        insert.Parameters.AddWithValue("$embedding", (object?)detection.Embedding ?? DBNull.Value);
        insert.Parameters.AddWithValue(
            "$embeddingSpaceKey",
            (object?)detection.EmbeddingSpaceKey ?? DBNull.Value);
        insert.Parameters.AddWithValue(
            "$suggestedIdentityId",
            detection.SuggestedIdentityId is null
                ? DBNull.Value
                : DbGuid.Format(detection.SuggestedIdentityId.Value));
        insert.Parameters.AddWithValue(
            "$confirmedIdentityId",
            detection.ConfirmedIdentityId is null
                ? DBNull.Value
                : DbGuid.Format(detection.ConfirmedIdentityId.Value));
        insert.Parameters.AddWithValue("$confidence", (object?)detection.Confidence ?? DBNull.Value);
        insert.Parameters.AddWithValue("$decisionState", DbEnum.Format(detection.DecisionState));
        insert.Parameters.AddWithValue("$modelId", detection.ModelId);
        insert.Parameters.AddWithValue("$modelVersion", detection.ModelVersion);
        insert.Parameters.AddWithValue(
            "$sampledTimestampMs",
            (object?)detection.SampledTimestampMilliseconds ?? DBNull.Value);
        insert.Parameters.AddWithValue(
            "$suggestedCandidatesJson",
            (object?)detection.SuggestedCandidatesJson ?? DBNull.Value);
        insert.Parameters.AddWithValue("$now", now);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    public async Task<Guid> PersistIdentitySampleAsync(
        IdentitySamplePersistence sample,
        CancellationToken cancellationToken = default)
    {
        ValidateSample(sample);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await EnsureActiveIdentityAsync(transaction, sample.IdentityId, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSampleMatchesConfirmedDetectionAsync(transaction, sample, cancellationToken)
            .ConfigureAwait(false);

        var existing = await ReadSampleAsync(transaction, sample.FaceId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.IdentityId != sample.IdentityId
                || !existing.Embedding.AsSpan().SequenceEqual(sample.Embedding)
                || !string.Equals(
                    existing.EmbeddingSpaceKey,
                    sample.EmbeddingSpaceKey,
                    StringComparison.Ordinal)
                || !string.Equals(existing.ModelId, sample.ModelId, StringComparison.Ordinal)
                || !string.Equals(existing.ModelVersion, sample.ModelVersion, StringComparison.Ordinal)
                || existing.ConfirmedAtMilliseconds != DbTime.Format(sample.ConfirmedAtUtc))
            {
                throw new CatalogInvariantException(
                    $"Face {sample.FaceId:D} already has a different authoritative Identity sample.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing.IdentitySampleId;
        }

        await using var insert = transaction.CreateCommand(
            """
            INSERT INTO identity_samples(
                identity_sample_id, identity_id, face_id, embedding,
                embedding_space_key, model_id, model_version, confirmed_at_ms)
            VALUES (
                $sampleId, $identityId, $faceId, $embedding,
                $embeddingSpaceKey, $modelId, $modelVersion, $confirmedAtMs);
            """);
        insert.Parameters.AddWithValue("$sampleId", DbGuid.Format(sample.IdentitySampleId));
        insert.Parameters.AddWithValue("$identityId", DbGuid.Format(sample.IdentityId));
        insert.Parameters.AddWithValue("$faceId", DbGuid.Format(sample.FaceId));
        insert.Parameters.AddWithValue("$embedding", sample.Embedding);
        insert.Parameters.AddWithValue("$embeddingSpaceKey", sample.EmbeddingSpaceKey);
        insert.Parameters.AddWithValue("$modelId", sample.ModelId);
        insert.Parameters.AddWithValue("$modelVersion", sample.ModelVersion);
        insert.Parameters.AddWithValue("$confirmedAtMs", DbTime.Format(sample.ConfirmedAtUtc));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        IdentityBankProvider.InvalidateCatalogSpace(
            _catalog,
            EmbeddingSpaceKey.Parse(sample.EmbeddingSpaceKey));
        return sample.IdentitySampleId;
    }

    private static async Task<DetectionRow?> ReadDetectionAsync(
        CatalogTransaction transaction,
        FaceDetectionPersistence detection,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT face_id, bounding_box_json, embedding, embedding_space_key,
                   suggested_identity_id, confirmed_identity_id, confidence, decision_state,
                   sampled_timestamp_ms, suggested_candidates_json, row_version
            FROM face_detections
            WHERE media_id = $assetId
              AND detection_key = $detectionKey
              AND model_id = $modelId
              AND model_version = $modelVersion;
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(detection.MediaId));
        command.Parameters.AddWithValue("$detectionKey", detection.DetectionKey);
        command.Parameters.AddWithValue("$modelId", detection.ModelId);
        command.Parameters.AddWithValue("$modelVersion", detection.ModelVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DetectionRow(
            DbGuid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : DbGuid.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : DbGuid.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            DbEnum.ParseFaceDecisionState(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetInt64(10));
    }

    private static void EnsureSameDetection(
        DetectionRow existing,
        FaceDetectionPersistence detection)
    {
        var embeddingMatches = existing.Embedding is null
            ? detection.Embedding is null
            : detection.Embedding is not null
                && existing.Embedding.AsSpan().SequenceEqual(detection.Embedding);
        if (!string.Equals(existing.BoundingBoxJson, detection.BoundingBoxJson, StringComparison.Ordinal)
            || !embeddingMatches
            || !string.Equals(
                existing.EmbeddingSpaceKey,
                detection.EmbeddingSpaceKey,
                StringComparison.Ordinal)
            || existing.SampledTimestampMilliseconds != detection.SampledTimestampMilliseconds
            || existing.Confidence != detection.Confidence)
        {
            throw new CatalogInvariantException(
                "A deterministic face detection key already exists with different provenance.");
        }
    }

    private async Task ReconcileSuggestionEvidenceAsync(
        CatalogTransaction transaction,
        DetectionRow existing,
        FaceDetectionPersistence detection,
        CancellationToken cancellationToken)
    {
        if (existing.DecisionState is FaceDecisionState.Confirmed or FaceDecisionState.Rejected)
        {
            return;
        }

        if (existing.SuggestedIdentityId == detection.SuggestedIdentityId
            && existing.DecisionState == detection.DecisionState
            && string.Equals(
                existing.SuggestedCandidatesJson,
                detection.SuggestedCandidatesJson,
                StringComparison.Ordinal))
        {
            return;
        }

        if (detection.DecisionState is FaceDecisionState.Confirmed or FaceDecisionState.Rejected)
        {
            throw new CatalogInvariantException(
                "Face analysis may only write UNKNOWN or SUGGESTED; a decision is made through FaceDecisionOperations.");
        }

        if (detection.SuggestedIdentityId is Guid refreshed)
        {
            await EnsureActiveIdentityAsync(transaction, refreshed, cancellationToken).ConfigureAwait(false);
        }

        await using var update = transaction.CreateCommand(
            """
            UPDATE face_detections
            SET suggested_identity_id = $suggestedIdentityId,
                decision_state = $decisionState,
                suggested_candidates_json = $suggestedCandidatesJson,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE face_id = $faceId
              AND row_version = $expectedRowVersion;
            """);
        update.Parameters.AddWithValue(
            "$suggestedIdentityId",
            detection.SuggestedIdentityId is null
                ? DBNull.Value
                : DbGuid.Format(detection.SuggestedIdentityId.Value));
        update.Parameters.AddWithValue("$decisionState", DbEnum.Format(detection.DecisionState));
        update.Parameters.AddWithValue(
            "$suggestedCandidatesJson",
            (object?)detection.SuggestedCandidatesJson ?? DBNull.Value);
        update.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        update.Parameters.AddWithValue("$faceId", DbGuid.Format(existing.FaceId));
        update.Parameters.AddWithValue("$expectedRowVersion", existing.RowVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CatalogConcurrencyConflictException(
                $"Face {existing.FaceId:D} changed concurrently while refreshing analysis suggestion.");
        }
    }

    private static async Task EnsureActiveIdentityAsync(
        CatalogTransaction transaction,
        Guid identityId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            "SELECT COUNT(*) FROM identities WHERE identity_id = $identityId AND is_active = 1;");
        command.Parameters.AddWithValue("$identityId", DbGuid.Format(identityId));
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (count != 1)
        {
            throw new CatalogInvariantException(
                $"Identity {identityId:D} is not one active recognition authority.");
        }
    }

    private static async Task EnsureSampleMatchesConfirmedDetectionAsync(
        CatalogTransaction transaction,
        IdentitySamplePersistence sample,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT confirmed_identity_id, embedding, embedding_space_key
            FROM face_detections
            WHERE face_id = $faceId AND decision_state = 'CONFIRMED';
            """);
        command.Parameters.AddWithValue("$faceId", DbGuid.Format(sample.FaceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.IsDBNull(0)
            || DbGuid.Parse(reader.GetString(0)) != sample.IdentityId
            || reader.IsDBNull(1)
            || !reader.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(sample.Embedding)
            || reader.IsDBNull(2)
            || !string.Equals(reader.GetString(2), sample.EmbeddingSpaceKey, StringComparison.Ordinal)
            || !EmbeddingSpaceKey.TryParse(sample.EmbeddingSpaceKey, out var embeddingSpace)
            || !string.Equals(embeddingSpace.ModelId, sample.ModelId, StringComparison.Ordinal)
            || !string.Equals(embeddingSpace.ModelVersion, sample.ModelVersion, StringComparison.Ordinal))
        {
            throw new CatalogInvariantException(
                "An Identity sample must exactly match one confirmed FaceDetection embedding-space provenance record.");
        }
    }

    private static async Task<SampleRow?> ReadSampleAsync(
        CatalogTransaction transaction,
        Guid faceId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT identity_sample_id, identity_id, embedding, embedding_space_key,
                   model_id, model_version, confirmed_at_ms
            FROM identity_samples
            WHERE face_id = $faceId;
            """);
        command.Parameters.AddWithValue("$faceId", DbGuid.Format(faceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SampleRow(
            DbGuid.Parse(reader.GetString(0)),
            DbGuid.Parse(reader.GetString(1)),
            reader.GetFieldValue<byte[]>(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6));
    }

    private static void ValidateDetection(FaceDetectionPersistence detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        EnsureNonEmpty(detection.FaceId, nameof(detection.FaceId));
        EnsureNonEmpty(detection.MediaId, nameof(detection.MediaId));
        ArgumentException.ThrowIfNullOrWhiteSpace(detection.DetectionKey);
        ValidateJson(detection.BoundingBoxJson, nameof(detection.BoundingBoxJson));
        ArgumentException.ThrowIfNullOrWhiteSpace(detection.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(detection.ModelVersion);
        if ((detection.Embedding is null) != (detection.EmbeddingSpaceKey is null)
            || detection.Embedding is { Length: 0 })
        {
            throw new ArgumentException(
                "Embedding bytes and EmbeddingSpaceKey must be present together.",
                nameof(detection));
        }

        if (detection.EmbeddingSpaceKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(detection.EmbeddingSpaceKey);
        }

        if (detection.Confidence is double confidence && !double.IsFinite(confidence))
        {
            throw new ArgumentOutOfRangeException(nameof(detection), "Face confidence must be finite.");
        }

        if ((detection.DecisionState == FaceDecisionState.Confirmed)
            != detection.ConfirmedIdentityId.HasValue)
        {
            throw new ArgumentException(
                "Only a Confirmed FaceDetection carries a confirmed Identity.",
                nameof(detection));
        }
    }

    private static void ValidateSample(IdentitySamplePersistence sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        EnsureNonEmpty(sample.IdentitySampleId, nameof(sample.IdentitySampleId));
        EnsureNonEmpty(sample.IdentityId, nameof(sample.IdentityId));
        EnsureNonEmpty(sample.FaceId, nameof(sample.FaceId));
        ArgumentNullException.ThrowIfNull(sample.Embedding);
        if (sample.Embedding.Length == 0)
        {
            throw new ArgumentException("An Identity sample embedding cannot be empty.", nameof(sample));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sample.EmbeddingSpaceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sample.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sample.ModelVersion);
        if (!EmbeddingSpaceKey.TryParse(sample.EmbeddingSpaceKey, out var space)
            || !string.Equals(space.ModelId, sample.ModelId, StringComparison.Ordinal)
            || !string.Equals(space.ModelVersion, sample.ModelVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Identity sample model provenance must match its canonical embedding-space key.",
                nameof(sample));
        }
    }

    private static void ValidateJson(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        try
        {
            using var _ = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Face provenance JSON must be valid.", parameterName, exception);
        }
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }

    private sealed record DetectionRow(
        Guid FaceId,
        string BoundingBoxJson,
        byte[]? Embedding,
        string? EmbeddingSpaceKey,
        Guid? SuggestedIdentityId,
        Guid? ConfirmedIdentityId,
        double? Confidence,
        FaceDecisionState DecisionState,
        long? SampledTimestampMilliseconds,
        string? SuggestedCandidatesJson,
        long RowVersion);

    private sealed record SampleRow(
        Guid IdentitySampleId,
        Guid IdentityId,
        byte[] Embedding,
        string EmbeddingSpaceKey,
        string ModelId,
        string ModelVersion,
        long ConfirmedAtMilliseconds);
}

public sealed record FaceDetectionPersistence(
    Guid FaceId,
    Guid MediaId,
    string DetectionKey,
    string BoundingBoxJson,
    byte[]? Embedding,
    string? EmbeddingSpaceKey,
    Guid? SuggestedIdentityId,
    Guid? ConfirmedIdentityId,
    double? Confidence,
    FaceDecisionState DecisionState,
    string ModelId,
    string ModelVersion,
    long? SampledTimestampMilliseconds = null,
    string? SuggestedCandidatesJson = null);

public sealed record IdentitySamplePersistence(
    Guid IdentitySampleId,
    Guid IdentityId,
    Guid FaceId,
    byte[] Embedding,
    string EmbeddingSpaceKey,
    string ModelId,
    string ModelVersion,
    DateTimeOffset ConfirmedAtUtc);
