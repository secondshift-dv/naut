using Neuterradise.Profiling.Protocol;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neuterradise.App.Faces;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs.Transport;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Jobs.Handlers;

public interface IFaceAnalysisJobOperation : IAuthorizedJobOperation;

public static class VideoFaceSamplePlanner
{

    public const int MaximumSampleCount = 12;

    public const int MinimumSampleCount = 2;

    public const long NominalIntervalMilliseconds = 10_000;

    public const long EndGuardMilliseconds = 200;

    public static IReadOnlyList<long> MissingDurationFallbackMilliseconds { get; } =
        [0, 1_000, 3_000, 7_000, 15_000];

    public static IReadOnlyList<long> CreatePlan(long? durationMilliseconds)
    {
        if (durationMilliseconds is not { } duration || duration <= 0)
        {
            return MissingDurationFallbackMilliseconds;
        }

        var usable = duration - Math.Min(EndGuardMilliseconds, duration / 10);
        if (usable <= 0)
        {
            return [0];
        }

        var nominal = 1 + (int)Math.Ceiling(usable / (double)NominalIntervalMilliseconds);
        var count = Math.Clamp(nominal, MinimumSampleCount, MaximumSampleCount);

        var timestamps = new List<long>(count);
        for (var index = 0; index < count; index++)
        {
            var value = (long)Math.Round(index * (double)usable / (count - 1), MidpointRounding.AwayFromZero);
            if (timestamps.Count == 0 || timestamps[^1] != value)
            {
                timestamps.Add(value);
            }
        }

        return timestamps;
    }
}

public sealed class FaceAnalysisJobHandler : AuthorizedJobHandler
{
    public FaceAnalysisJobHandler(IFaceAnalysisJobOperation operation)
        : base("FaceAnalysis", [JobLane.Face], "Media", operation, runOffCallingThread: true)
    {
    }
}

public sealed class ProfilingFaceAnalysisJobOperation : IFaceAnalysisJobOperation
{

    public const string AnalysisVersion = "face-analysis-v1";

    public const double SuggestionThreshold = 0.363;

    public const int MaximumSuggestions = 5;

    public static IReadOnlyList<long> VideoSampleTimestampsMilliseconds =>
        VideoFaceSamplePlanner.MissingDurationFallbackMilliseconds;

    private const int _checkpointSchemaVersion = 1;

    private static readonly JsonSerializerOptions _payloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private readonly MediaReads _reads;
    private readonly FaceWrites _writes;
    private readonly VaultPaths _paths;
    private readonly Func<ProfilingEnvelope, CancellationToken, Task<ProfilingEnvelope>> _sendAsync;
    private readonly TimeSpan _analysisTimeout;
    private readonly IdentityBankProvider? _identityBank;

    public ProfilingFaceAnalysisJobOperation(
        MediaReads reads,
        FaceWrites writes,
        VaultPaths paths,
        Func<ProfilingEnvelope, CancellationToken, Task<ProfilingEnvelope>> sendAsync,
        TimeSpan? analysisTimeout = null,
        IdentityBankProvider? identityBank = null)
    {
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
        _analysisTimeout = analysisTimeout ?? TimeSpan.FromMinutes(5);
        _identityBank = identityBank;
    }

    public async Task<JobExecutionResult> ExecuteAsync(
        JobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var source = await _reads.GetMediaSourceAsync(context.OwnerId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "FACE_OWNER_UNUSABLE",
                "The persisted job owner is not an Media that may carry face suggestions.");
        }

        // Face analysis is scheduled during import preparation, while the asset is still a CANDIDATE,
        // precisely so hints are ready before commit. Requiring ACTIVE here failed every such job
        // terminally -- the source of the growing failed-job count. A retired or trashed owner means
        // the work simply no longer applies: that is a cancellation, not a failure.
        if (source.IsRetired || source.State == MediaState.Trashed)
        {
            return JobExecutionResult.Cancelled("The media left the import or the Vault before faces were analyzed.");
        }

        if (source.MediaType is not (MediaType.Image or MediaType.Video))
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "FACE_MEDIA_TYPE_UNSUPPORTED",
                "FACE analysis applies to IMAGE and VIDEO only.");
        }

        if (string.IsNullOrWhiteSpace(source.Sha256))
        {

            return JobExecutionResult.Failed(
                JobFailureClassification.AmbiguousPhysicalState,
                "FACE_FINGERPRINT_MISSING",
                "The Media has no authoritative content fingerprint to key detections by.");
        }

        var readablePath = source.ResolveManagedPath(_paths);
        if (readablePath is null)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.ContentMismatch,
                "FACE_SOURCE_MISSING",
                "The canonical managed media file is not available.");
        }

        var space = EmbeddingSpaceKey.Baseline;
        var request = new FaceAnalysisRequest(
            MediaId: source.MediaId,
            InputPath: readablePath,
            ExpectedSha256: source.Sha256,
            ExpectedByteLength: source.ByteLength,
            MediaType: source.MediaType == MediaType.Video ? "Video" : "Image",
            AnalysisVersion: AnalysisVersion,
            YuNetModelId: "yunet",
            YuNetModelVersion: "2023mar",
            SFaceModelId: space.ModelId,
            SFaceModelVersion: space.ModelVersion,
            EmbeddingSpaceKey: space.Canonical,
            VideoSamplePlan: source.MediaType == MediaType.Video
                ? new VideoSamplePlan(
                    VideoFaceSamplePlanner.CreatePlan(source.DurationMilliseconds))
                : null);

        try
        {
            await context.Progress.ReportProgressAsync(0, 1, "Analyzing faces", cancellationToken)
                .ConfigureAwait(false);

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(_analysisTimeout);

            var envelope = ProfilingEnvelope.Create(ProfilingMessageType.AnalyzeFaces, request);
            var response = await _sendAsync(envelope, bounded.Token).ConfigureAwait(false);

            if (response.MessageType == ProfilingMessageType.Error)
            {
                var error = response.DeserializePayload<ErrorPayload>();
                if (string.Equals(error?.ErrorCode, "CANCELLED", StringComparison.Ordinal))
                {
                    return JobExecutionResult.Cancelled(
                        error?.ErrorMessage ?? "FACE analysis was cancelled by the Profiling Worker.");
                }

                var classification = error?.ErrorCode switch
                {
                    "CONTENT_MISMATCH" or "FACE_INPUT_INVALID" =>
                        JobFailureClassification.ContentMismatch,
                    _ when error?.IsFatal == true =>
                        JobFailureClassification.DeterministicInvalidInput,
                    _ => JobFailureClassification.WorkerDisconnected,
                };

                return JobExecutionResult.Failed(
                    classification,
                    error?.ErrorCode ?? "FACE_WORKER_ERROR",
                    error?.ErrorMessage ?? "The Profiling Worker reported an unspecified error.");
            }

            if (response.MessageType != ProfilingMessageType.AnalyzeFacesResult)
            {
                return JobExecutionResult.Failed(
                    JobFailureClassification.ProtocolIncompatible,
                    "FACE_WORKER_UNEXPECTED_MESSAGE",
                    $"The Profiling Worker answered with '{response.MessageType}'.");
            }

            var result = response.DeserializePayload<FaceAnalysisResult>();
            if (result is null)
            {
                return JobExecutionResult.Failed(
                    JobFailureClassification.ProtocolIncompatible,
                    "FACE_WORKER_RESULT_MALFORMED",
                    "The Profiling Worker result could not be read.");
            }

            return await PersistAsync(context, request, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return JobExecutionResult.Cancelled("FACE analysis reached a safe cancellation boundary.");
        }
        catch (OperationCanceledException)
        {

            return JobExecutionResult.Failed(
                JobFailureClassification.WorkerDisconnected,
                "FACE_WORKER_TIMEOUT",
                "The Profiling Worker did not answer within the bounded analysis budget.");
        }
        catch (ProfilingProtocolException exception)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.ProtocolIncompatible,
                "FACE_WORKER_PROTOCOL",
                exception.Message);
        }
        catch (ProfilingWorkerDisconnectedException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.WorkerDisconnected,
                "FACE_WORKER_DISCONNECTED",
                "The Profiling Profiling Worker connection dropped during analysis.");
        }
        catch (ProfilingWorkerProcessException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.WorkerDisconnected,
                "FACE_WORKER_UNAVAILABLE",
                "The Profiling Worker process could not be started or kept running.");
        }
        catch (FileNotFoundException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.MissingRequiredToolOrModel,
                "FACE_MODEL_MISSING",
                "Required YuNet/SFace model files are not available.");
        }
        catch (IOException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.TransientIo,
                "FACE_WORKER_IO_FAILED",
                "The Profiling Profiling Worker connection failed while analysis was in flight.");
        }
    }

    private async Task<JobExecutionResult> PersistAsync(
        JobExecutionContext context,
        FaceAnalysisRequest request,
        FaceAnalysisResult result,
        CancellationToken cancellationToken)
    {
        if (result.MediaId != request.MediaId)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.AmbiguousPhysicalState,
                "FACE_RESULT_ASSET_MISMATCH",
                "The Profiling Worker answered about a different Media than was asked about.");
        }

        if (result.Availability == FaceAnalysisAvailability.ModelsUnavailable)
        {

            await WriteCheckpointAsync(context, detections: 0, available: false, cancellationToken)
                .ConfigureAwait(false);

            return JobExecutionResult.Failed(
                JobFailureClassification.MissingRequiredToolOrModel,
                "FACE_MODELS_UNAVAILABLE",
                "Required YuNet/SFace models are not provisioned.");
        }

        if (!string.Equals(result.YuNetModelId, request.YuNetModelId, StringComparison.Ordinal)
            || !string.Equals(result.YuNetModelVersion, request.YuNetModelVersion, StringComparison.Ordinal)
            || !string.Equals(result.SFaceModelId, request.SFaceModelId, StringComparison.Ordinal)
            || !string.Equals(result.SFaceModelVersion, request.SFaceModelVersion, StringComparison.Ordinal)
            || !string.Equals(result.EmbeddingSpaceKey, request.EmbeddingSpaceKey, StringComparison.Ordinal))
        {

            return JobExecutionResult.Failed(
                JobFailureClassification.ProtocolIncompatible,
                "FACE_RESULT_PROVENANCE_MISMATCH",
                "The Profiling Worker result does not carry the requested model or embedding-space provenance.");
        }

        var suggestions = await ResolveIdentitySuggestionsAsync(request, result, cancellationToken)
            .ConfigureAwait(false);

        var detections = new List<FaceDetectionPersistence>();
        foreach (var face in result.DetectedFaces ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(face.DetectionKey) || face.Bounds is null)
            {
                continue;
            }

            byte[]? embedding = null;
            if (face.Embedding is { Count: > 0 } values)
            {
                embedding = FaceEmbedding.ToBlob([.. values]);
            }

            suggestions.TryGetValue(face.DetectionKey, out var evidence);

            var topCandidate = evidence?.Candidates.Count > 0 ? evidence.Candidates[0] : null;
            var decisionState = topCandidate is null
                ? FaceDecisionState.Unknown
                : FaceDecisionState.Suggested;

            detections.Add(new FaceDetectionPersistence(
                        FaceId: DeriveFaceId(request.MediaId, face.DetectionKey),
                        MediaId: request.MediaId,
                        DetectionKey: face.DetectionKey,
                        BoundingBoxJson: JsonSerializer.Serialize(face.Bounds, _payloadOptions),
                        Embedding: embedding,
                        EmbeddingSpaceKey: embedding is null ? null : request.EmbeddingSpaceKey,
                        SuggestedIdentityId: topCandidate?.IdentityId,
                        ConfirmedIdentityId: null,
                        Confidence: face.Confidence,
                        DecisionState: decisionState,
                        ModelId: request.YuNetModelId,
                        ModelVersion: request.YuNetModelVersion,
                        SampledTimestampMilliseconds: face.SampledTimestampMilliseconds,
                        SuggestedCandidatesJson: evidence?.ToJson()));
        }

        await _writes.PersistDetectionsAsync(detections, cancellationToken).ConfigureAwait(false);
        await WriteCheckpointAsync(context, detections.Count, available: true, cancellationToken).ConfigureAwait(false);
        await context.Progress.ReportProgressAsync(1, 1, "Analyzed", cancellationToken).ConfigureAwait(false);
        return JobExecutionResult.Succeeded;
    }

    private async Task<IReadOnlyDictionary<string, FaceSuggestionEvidenceV1>> ResolveIdentitySuggestionsAsync(
        FaceAnalysisRequest request,
        FaceAnalysisResult result,
        CancellationToken cancellationToken)
    {
        var empty = new Dictionary<string, FaceSuggestionEvidenceV1>(StringComparer.Ordinal);
        if (_identityBank is null)
        {
            return empty;
        }

        var probes = (result.DetectedFaces ?? [])
            .Where(face => !string.IsNullOrWhiteSpace(face.DetectionKey) && face.Embedding is { Count: > 0 })
            .ToArray();
        if (probes.Length == 0)
        {
            return empty;
        }

        if (!EmbeddingSpaceKey.TryParse(request.EmbeddingSpaceKey, out var space))
        {
            return empty;
        }

        IdentityBankSpace bank;
        try
        {
            bank = await _identityBank.GetSpaceAsync(space, cancellationToken).ConfigureAwait(false);
        }
        catch (EmbeddingSpaceMismatchException)
        {
            return empty;
        }

        if (bank.IdentityCount == 0)
        {
            return empty;
        }

        var samples = bank.Identities
            .SelectMany(identity => identity.Samples.Select(sample => new IdentitySampleData(
                IdentityId: identity.IdentityId,
                ProfileId: identity.ProfileId,
                IdentitySampleId: sample.IdentitySampleId,
                EmbeddingSpaceKey: space.Canonical,
                ModelId: space.ModelId,
                ModelVersion: space.ModelVersion,
                Embedding: sample.Embedding.ToArray())))
            .ToArray();
        if (samples.Length == 0)
        {
            return empty;
        }

        var signature = IdentityBankSignature.For(
            space.Canonical,
            samples.Select(static sample => sample.IdentitySampleId));

        var chunks = CreateIdentityIndexChunks(space, signature, samples);
            BuildIdentityIndexResult? completedBuild = null;
            for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var indexRequest = new BuildIdentityIndexRequest(
                    space.Canonical,
                    space.ModelId,
                    space.ModelVersion,
                    signature,
                    chunkIndex,
                    isFinalChunk: chunkIndex == chunks.Count - 1,
                    chunks[chunkIndex]);
                var envelope = ProfilingEnvelope.Create(
                    ProfilingMessageType.BuildIdentityIndex,
                    indexRequest);

                if (ProfilingProtocolSerializer.MeasurePayloadSize(envelope)
                    > ProfilingProtocolVersion.MaximumFramePayloadSize)
                {
                    throw new ProfilingProtocolException(
                        $"Identity-index chunk {chunkIndex} exceeds the protocol frame limit.");
                }

                var buildResponse = await _sendAsync(envelope, cancellationToken)
                    .ConfigureAwait(false);
                if (buildResponse.MessageType != ProfilingMessageType.BuildIdentityIndexResult)
                {
                    return empty;
                }

                var buildResult = buildResponse.DeserializePayload<BuildIdentityIndexResult>();
                if (buildResult is null)
                {
                    return empty;
                }

                if (buildResult.IsComplete)
                {
                    completedBuild = buildResult;
                    break;
                }
            }

            if (completedBuild is null)
            {
                return empty;
            }

            var suggestions = new Dictionary<string, FaceSuggestionEvidenceV1>(StringComparer.Ordinal);
            foreach (var probe in probes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var matchResponse = await _sendAsync(
                        ProfilingEnvelope.Create(
                            ProfilingMessageType.MatchIdentityCandidates,
                            new MatchIdentityCandidatesRequest(
                                space.Canonical,
                                space.ModelId,
                                space.ModelVersion,
                                probe.Embedding ?? [],
                                SuggestionThreshold,
                                MaximumSuggestions)),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (matchResponse.MessageType != ProfilingMessageType.MatchIdentityCandidatesResult)
                {
                    continue;
                }

                var matched = matchResponse.DeserializePayload<MatchIdentityCandidatesResult>();
                var ranked = (matched?.Candidates ?? [])
                    .Where(candidate => candidate.Score >= SuggestionThreshold)
                    .OrderByDescending(static candidate => candidate.Score)
                    .ThenBy(static candidate => candidate.IdentityId.ToString("D"), StringComparer.Ordinal)
                    .Take(MaximumSuggestions)
                    .Select(candidate => new FaceSuggestionEntryV1
                    {
                        IdentityId = candidate.IdentityId,
                        Similarity = candidate.Score,
                    })
                    .ToArray();
                if (ranked.Length == 0)
                {
                    continue;
                }

                suggestions[probe.DetectionKey] = new FaceSuggestionEvidenceV1
                {
                    EmbeddingSpaceKey = space.Canonical,
                    BankSignature = signature,
                    Threshold = SuggestionThreshold,
                    Candidates = ranked,
                };
            }

            return suggestions;
    }

    private static IReadOnlyList<IReadOnlyList<IdentitySampleData>> CreateIdentityIndexChunks(
        EmbeddingSpaceKey space,
        string signature,
        IReadOnlyList<IdentitySampleData> samples)
    {
        const int reserveBytes = 16 * 1024;
        var maximumPayload =
            checked((int)ProfilingProtocolVersion.MaximumFramePayloadSize) - reserveBytes;

        var emptyEnvelope = ProfilingEnvelope.Create(
            ProfilingMessageType.BuildIdentityIndex,
            new BuildIdentityIndexRequest(
                space.Canonical,
                space.ModelId,
                space.ModelVersion,
                signature,
                chunkIndex: 0,
                isFinalChunk: false,
                []));
        var fixedBytes = ProfilingProtocolSerializer.MeasurePayloadSize(emptyEnvelope);

        var chunks = new List<IReadOnlyList<IdentitySampleData>>();
        var current = new List<IdentitySampleData>();
        var currentBytes = fixedBytes;

        foreach (var sample in samples)
        {
            var sampleBytes = JsonSerializer.SerializeToUtf8Bytes(
                sample,
                ProfilingProtocolSerializer.Options).Length + 1;

            if (fixedBytes + sampleBytes > maximumPayload)
            {
                throw new ProfilingProtocolException(
                    $"Identity sample {sample.IdentitySampleId:D} cannot fit in one protocol frame.");
            }

            if (current.Count > 0 && currentBytes + sampleBytes > maximumPayload)
            {
                chunks.Add(current.ToArray());
                current = [];
                currentBytes = fixedBytes;
            }

            current.Add(sample);
            currentBytes += sampleBytes;
        }

        if (current.Count > 0)
        {
            chunks.Add(current.ToArray());
        }

        return chunks;
    }

    private static Guid DeriveFaceId(Guid assetId, string detectionKey)
    {
        var material = System.Text.Encoding.UTF8.GetBytes($"face:{assetId:D}:{detectionKey}");
        var hash = System.Security.Cryptography.SHA256.HashData(material);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static Task WriteCheckpointAsync(
        JobExecutionContext context,
        int detections,
        bool available,
        CancellationToken cancellationToken)
    {
        var checkpoint = JsonSerializer.Serialize(new
        {
            schemaVersion = _checkpointSchemaVersion,
            assetId = context.OwnerId,
            analysisVersion = AnalysisVersion,
            available,
            detections,
        });

        return context.Checkpoints.UpdateCheckpointAsync(checkpoint, cancellationToken);
    }

}
