using System.Collections.Concurrent;
using Neuterradise.Profiling.Protocol;
using Neuterradise.Profiling.Worker.FaceAnalysis;
using Neuterradise.Profiling.Worker.IdentityMatching;
using Neuterradise.Profiling.Worker.StillExtraction;
using Neuterradise.Profiling.Worker.Transport;

namespace Neuterradise.Profiling.Worker.Dispatching;

public sealed class ProfilingRequestDispatcher : IDisposable
{
    private const int MaximumConcurrentLongRunningRequests = 2;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlightRequests = new();
    private readonly ConcurrentDictionary<string, Task> _inFlightTasks = new();
    private readonly SemaphoreSlim _requestSlots =
        new(MaximumConcurrentLongRunningRequests, MaximumConcurrentLongRunningRequests);
    private readonly FaceAnalyzer _faceAnalysis;
    private readonly IdentityIndexCache _indexCache;
    private readonly IdentityMatcher _candidateMatcher;
    private readonly StillExtractor _stillFrames;

    public ProfilingRequestDispatcher(
        FaceAnalyzer? faceAnalysis = null,
        IdentityIndexCache? indexCache = null,
        StillExtractor? stillFrames = null)
    {
        _indexCache = indexCache ?? new IdentityIndexCache();
        _faceAnalysis = faceAnalysis ?? CreateDefaultFaceAnalysis();
        _candidateMatcher = new IdentityMatcher(_indexCache);
        _stillFrames = stillFrames ?? new StillExtractor();
    }

    public int LoadedIndexCount => _indexCache.LoadedIndexCount;

    public void ReleaseRuntimeResources()
    {
        CancelInFlightRequests();
        _indexCache.InvalidateAll();
    }

    public void Dispose()
    {
        ReleaseRuntimeResources();
        if (_inFlightTasks.Count > 0)
        {
            Task.WhenAll(_inFlightTasks.Values.ToArray()).GetAwaiter().GetResult();
        }

        _requestSlots.Dispose();
        _indexCache.Dispose();
    }

    public async Task<int> RunAsync(ProfilingClientTransport client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        try
        {
            return await RunCoreAsync(client, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CancelAndDrainInFlightRequestsAsync().ConfigureAwait(false);
            _indexCache.InvalidateAll();
        }
    }

    private async Task<int> RunCoreAsync(ProfilingClientTransport client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ProfilingEnvelope? envelope;
            try
            {
                envelope = await client.ReadEnvelopeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {

                break;
            }

            if (envelope is null)
            {

                break;
            }

            if (envelope.ProtocolVersion != ProfilingProtocolVersion.Current)
            {
                var versionError = new ErrorPayload(
                    "PROTOCOL_VERSION_MISMATCH",
                    $"Protocol version {envelope.ProtocolVersion} is incompatible with Profiling Worker protocol version {ProfilingProtocolVersion.Current}.",
                    true);
                var versionErrorEnvelope = ProfilingEnvelope.Create(
                    ProfilingMessageType.Error,
                    versionError,
                    envelope.RequestId);
                await client.WriteEnvelopeAsync(versionErrorEnvelope, cancellationToken).ConfigureAwait(false);
                return 1;
            }

            if (envelope.MessageType == ProfilingMessageType.Shutdown)
            {
                await CancelAndDrainInFlightRequestsAsync().ConfigureAwait(false);
                return 0;
            }

            if (envelope.MessageType == ProfilingMessageType.Ping)
            {
                var ping = envelope.DeserializePayload<PingPayload>();
                var pong = new PongPayload(ping?.TimestampUtcMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var response = ProfilingEnvelope.Create(ProfilingMessageType.Pong, pong, envelope.RequestId);
                await client.WriteEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (envelope.MessageType == ProfilingMessageType.CancelRequest)
            {
                var cancel = envelope.DeserializePayload<CancelRequestPayload>();
                if (cancel is not null && _inFlightRequests.TryGetValue(cancel.TargetRequestId, out var cts))
                {
                    cts.Cancel();
                }
                continue;
            }

            if (envelope.MessageType == ProfilingMessageType.Hello)
            {
                await WriteErrorAsync(client, envelope, "UNEXPECTED_HELLO", "Hello is only valid during the initial handshake.", cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (envelope.MessageType == ProfilingMessageType.AnalyzeFaces)
            {
                await StartTrackedRequestAsync(
                        client,
                        envelope,
                        cancellationToken,
                        requestToken => HandleAnalyzeFacesAsync(client, envelope, requestToken))
                    .ConfigureAwait(false);
                continue;
            }

            if (envelope.MessageType == ProfilingMessageType.ExtractStills)
            {
                await StartTrackedRequestAsync(
                        client,
                        envelope,
                        cancellationToken,
                        requestToken => HandleExtractStillsAsync(client, envelope, requestToken))
                    .ConfigureAwait(false);
                continue;
            }

            if (envelope.MessageType == ProfilingMessageType.BuildIdentityIndex)
            {
                await HandleBuildIdentityIndexAsync(client, envelope, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (envelope.MessageType == ProfilingMessageType.MatchIdentityCandidates)
            {
                await HandleMatchIdentityCandidatesAsync(client, envelope, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (envelope.MessageType == ProfilingMessageType.ReleaseIndex)
            {
                var release = envelope.DeserializePayload<ReleaseIdentityIndexRequest>();
                if (release is not null)
                {
                    _indexCache.Invalidate(release.EmbeddingSpaceKey);
                    var released = ProfilingEnvelope.Create(
                        ProfilingMessageType.ReleaseIndex,
                        release,
                        envelope.RequestId);
                    await client.WriteEnvelopeAsync(released, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            var error = new ErrorPayload("UNKNOWN_MESSAGE_TYPE", $"Message type {envelope.MessageType} is not supported in this build.", false);
            var errorEnv = ProfilingEnvelope.Create(ProfilingMessageType.Error, error, envelope.RequestId);
            await client.WriteEnvelopeAsync(errorEnv, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task HandleAnalyzeFacesAsync(ProfilingClientTransport client, ProfilingEnvelope envelope, CancellationToken cancellationToken)
    {
        FaceAnalysisRequest? request;
        try
        {
            request = envelope.DeserializePayload<FaceAnalysisRequest>();
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", $"AnalyzeFaces payload is missing or malformed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", "AnalyzeFaces payload is missing or malformed.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var analysisTask = Task.Run(
            () => _faceAnalysis.Analyze(request, envelope.RequestId, cancellationToken),
            CancellationToken.None);
        var cancellationSignal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellationSignal);

        if (await Task.WhenAny(analysisTask, cancellationSignal.Task).ConfigureAwait(false)
            != analysisTask)
        {
            await WriteErrorAsync(
                    client,
                    envelope,
                    "CANCELLED",
                    "AnalyzeFaces request was cancelled.",
                    CancellationToken.None)
                .ConfigureAwait(false);

            try
            {
                await analysisTask.ConfigureAwait(false);
            }
            catch
            {
                // The cancellation response is already terminal for this request. Observing the
                // physical inference task here prevents an unobserved exception while the tracked
                // request slot remains occupied until native work actually unwinds.
            }

            return;
        }

        FaceAnalysisResult result;
        try
        {
            result = await analysisTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(client, envelope, "CANCELLED", "AnalyzeFaces request was cancelled.", CancellationToken.None).ConfigureAwait(false);
            return;
        }
        catch (FaceContentMismatchException ex)
        {
            await WriteErrorAsync(client, envelope, "CONTENT_MISMATCH", ex.Message, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        catch (FaceInputException ex)
        {
            await WriteErrorAsync(client, envelope, "FACE_INPUT_INVALID", ex.Message, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(client, envelope, "FACE_ANALYSIS_FAILED", $"Face analysis failed: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var response = ProfilingEnvelope.Create(ProfilingMessageType.AnalyzeFacesResult, result, envelope.RequestId);
        await client.WriteEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleExtractStillsAsync(ProfilingClientTransport client, ProfilingEnvelope envelope, CancellationToken cancellationToken)
    {
        ExtractStillsRequest? request;
        try
        {
            request = envelope.DeserializePayload<ExtractStillsRequest>();
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", $"ExtractStills payload is missing or malformed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", "ExtractStills payload is missing or malformed.", cancellationToken).ConfigureAwait(false);
            return;
        }

        ExtractStillsResult result;
        try
        {
            result = _stillFrames.Extract(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(client, envelope, "CANCELLED", "ExtractStills request was cancelled.", CancellationToken.None).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(client, envelope, "STILL_EXTRACTION_FAILED", $"Still extraction failed: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var response = ProfilingEnvelope.Create(ProfilingMessageType.ExtractStillsResult, result, envelope.RequestId);
        await client.WriteEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleBuildIdentityIndexAsync(ProfilingClientTransport client, ProfilingEnvelope envelope, CancellationToken cancellationToken)
    {
        BuildIdentityIndexRequest? request;
        try
        {
            request = envelope.DeserializePayload<BuildIdentityIndexRequest>();
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", $"BuildIdentityIndex payload is missing or malformed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", "BuildIdentityIndex payload is missing or malformed.", cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = _indexCache.AppendChunk(request);
            var response = ProfilingEnvelope.Create(
                ProfilingMessageType.BuildIdentityIndexResult,
                result,
                envelope.RequestId);
            await client.WriteEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _indexCache.Invalidate(request.EmbeddingSpaceKey);
            await WriteErrorAsync(client, envelope, "IDENTITY_INDEX_BUILD_FAILED", ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleMatchIdentityCandidatesAsync(ProfilingClientTransport client, ProfilingEnvelope envelope, CancellationToken cancellationToken)
    {
        MatchIdentityCandidatesRequest? request;
        try
        {
            request = envelope.DeserializePayload<MatchIdentityCandidatesRequest>();
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", $"MatchIdentityCandidates payload is missing or malformed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", "MatchIdentityCandidates payload is missing or malformed.", cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = _candidateMatcher.Match(request);
            var response = ProfilingEnvelope.Create(ProfilingMessageType.MatchIdentityCandidatesResult, result, envelope.RequestId);
            await client.WriteEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (IdentitySpaceMismatchException ex)
        {
            await WriteErrorAsync(client, envelope, "EMBEDDING_SPACE_MISMATCH", ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartTrackedRequestAsync(
        ProfilingClientTransport client,
        ProfilingEnvelope envelope,
        CancellationToken lifetimeToken,
        Func<CancellationToken, Task> handler)
    {
        if (!_requestSlots.Wait(0))
        {
            await WriteErrorAsync(
                    client,
                    envelope,
                    "WORKER_BUSY",
                    "The Profiling Worker is at its bounded long-running request limit.",
                    lifetimeToken)
                .ConfigureAwait(false);
            return;
        }

        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        if (!_inFlightRequests.TryAdd(envelope.RequestId, requestCts))
        {
            requestCts.Dispose();
            _requestSlots.Release();
            await WriteErrorAsync(
                    client,
                    envelope,
                    "DUPLICATE_REQUEST_ID",
                    $"RequestId '{envelope.RequestId}' is already active.",
                    lifetimeToken)
                .ConfigureAwait(false);
            return;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlightTasks[envelope.RequestId] = completion.Task;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await handler(requestCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    try
                    {
                        await WriteErrorAsync(
                                client,
                                envelope,
                                "REQUEST_FAILED",
                                $"Request failed: {ex.Message}",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    _inFlightRequests.TryRemove(envelope.RequestId, out _);
                    requestCts.Dispose();
                    _requestSlots.Release();
                    completion.TrySetResult(true);
                    _inFlightTasks.TryRemove(envelope.RequestId, out _);
                }
            },
            CancellationToken.None);
    }

    private void CancelInFlightRequests()
    {
        foreach (var inFlight in _inFlightRequests.Values)
        {
            try
            {
                inFlight.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task CancelAndDrainInFlightRequestsAsync()
    {
        CancelInFlightRequests();
        var tasks = _inFlightTasks.Values.ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private static async Task WriteErrorAsync(
        ProfilingClientTransport client,
        ProfilingEnvelope envelope,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var error = new ErrorPayload(errorCode, errorMessage, false);
        var errorEnv = ProfilingEnvelope.Create(ProfilingMessageType.Error, error, envelope.RequestId);
        await client.WriteEnvelopeAsync(errorEnv, cancellationToken).ConfigureAwait(false);
    }

    private static FaceAnalyzer CreateDefaultFaceAnalysis() =>
        new(
            new OpenCvFaceImageSource(),
            CreateYuNetAdapter,
            CreateSFaceAdapter);

    private static IFaceDetectorAdapter CreateYuNetAdapter(FaceAnalysisRequest request)
    {
        var path = ResolveModelPath(
            request.YuNetModelId,
            request.YuNetModelVersion,
            YuNetFaceDetector.ModelId,
            YuNetFaceDetector.ModelVersion,
            YuNetFaceDetector.ArtifactFileName);
        try
        {
            return new YuNetFaceDetector(path);
        }
        catch (FaceModelsUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FaceModelsUnavailableException($"YuNet model could not be loaded: {ex.Message}");
        }
    }

    private static IFaceEmbeddingAdapter CreateSFaceAdapter(FaceAnalysisRequest request)
    {
        var path = ResolveModelPath(
            request.SFaceModelId,
            request.SFaceModelVersion,
            SFaceEmbeddingExtractor.ModelId,
            SFaceEmbeddingExtractor.ModelVersion,
            SFaceEmbeddingExtractor.ArtifactFileName);
        try
        {
            return new SFaceEmbeddingExtractor(path);
        }
        catch (FaceModelsUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FaceModelsUnavailableException($"SFace model could not be loaded: {ex.Message}");
        }
    }

    private static string ResolveModelPath(
        string modelId,
        string modelVersion,
        string expectedModelId,
        string expectedModelVersion,
        string artifactFileName)
    {
        if (!string.Equals(modelId, expectedModelId, StringComparison.Ordinal)
            || !string.Equals(modelVersion, expectedModelVersion, StringComparison.Ordinal))
        {
            throw new FaceModelsUnavailableException(
                $"Model {modelId}/{modelVersion} is not provisioned in this Profiling Worker build; expected {expectedModelId}/{expectedModelVersion}.");
        }

        return WorkerRuntimeEnvironment.Current.ResolveModelPath(modelId, artifactFileName);
    }
}
