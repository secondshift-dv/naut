using Neuterradise.Profiling.Protocol;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Neuterradise.Profiling.Worker.FaceAnalysis;

public static class FaceDetectionKey
{
    public static string From(
        string assetSha256,
        FaceBounds bounds,
        string detectorModelId,
        string detectorModelVersion,
        long? sampledTimestampMilliseconds = null)
    {
        var timestampSegment = sampledTimestampMilliseconds is { } timestamp
            ? string.Create(CultureInfo.InvariantCulture, $"sampledAtMs={timestamp};")
            : string.Empty;
        var material = string.Create(
            CultureInfo.InvariantCulture,
            $"asset={assetSha256};{timestampSegment}bounds={bounds.X},{bounds.Y},{bounds.Width},{bounds.Height};model={detectorModelId}|{detectorModelVersion}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public interface IFaceImage : IDisposable
{
    int Width { get; }

    int Height { get; }
}

public interface IFaceImageSource : IDisposable
{
    IFaceImage LoadImage(string path);

    IReadOnlyList<FaceFrameSample> LoadVideoSamples(string path, VideoSamplePlan plan);
}

public sealed record FaceFrameSample(long TimestampMilliseconds, IFaceImage Frame);

public sealed class FaceModelsUnavailableException : Exception
{
    public FaceModelsUnavailableException(string message) : base(message) { }
}

public class FaceInputException : Exception
{
    public FaceInputException(string message) : base(message) { }
}

public sealed class FaceContentMismatchException : FaceInputException
{
    public FaceContentMismatchException(string message) : base(message) { }
}

public static class FaceEmbeddingValidator
{
    public static bool TryValidate(
        IReadOnlyList<float> values,
        int expectedValueCount,
        string embeddingSpaceKey,
        string modelId,
        string modelVersion,
        out string failure)
    {
        if (values.Count != expectedValueCount)
        {
            failure = $"embedding has {values.Count} values; expected exactly {expectedValueCount} for {embeddingSpaceKey}.";
            return false;
        }

        for (var index = 0; index < values.Count; index++)
        {
            if (float.IsNaN(values[index]) || float.IsInfinity(values[index]))
            {
                failure = $"embedding contains a non-finite value at index {index}; no NaN or infinity is allowed.";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(modelVersion))
        {
            failure = "embedding model id/version provenance is missing.";
            return false;
        }

        failure = string.Empty;
        return true;
    }
}

public sealed class FaceAnalyzer
{
    private readonly IFaceImageSource _imageSource;
    private readonly Func<FaceAnalysisRequest, IFaceDetectorAdapter> _detectorFactory;
    private readonly Func<FaceAnalysisRequest, IFaceEmbeddingAdapter> _embedderFactory;

    public FaceAnalyzer(
        IFaceImageSource imageSource,
        Func<FaceAnalysisRequest, IFaceDetectorAdapter> detectorFactory,
        Func<FaceAnalysisRequest, IFaceEmbeddingAdapter> embedderFactory)
    {
        _imageSource = imageSource ?? throw new ArgumentNullException(nameof(imageSource));
        _detectorFactory = detectorFactory ?? throw new ArgumentNullException(nameof(detectorFactory));
        _embedderFactory = embedderFactory ?? throw new ArgumentNullException(nameof(embedderFactory));
    }

    public FaceAnalysisResult Analyze(
        FaceAnalysisRequest request,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var diagnostics = new List<string>();
        using var inputLease = VerifiedFaceInputLease.Open(
            request.InputPath,
            request.ExpectedSha256,
            request.ExpectedByteLength,
            cancellationToken);

        IFaceDetectorAdapter? detector = null;
        try
        {
            detector = _detectorFactory(request);
        }
        catch (FaceModelsUnavailableException ex)
        {
            diagnostics.Add(ex.Message);
            return BuildResult(request, requestId, FaceAnalysisAvailability.ModelsUnavailable, [], 0, diagnostics);
        }

        using var scope = new AnalysisScope(_imageSource, detector);

        try
        {
            if (request.IsVideo)
            {
                return AnalyzeVideo(request, requestId, detector, _embedderFactory, scope.ImageSource, diagnostics, cancellationToken);
            }

            return AnalyzeImage(request, requestId, detector, _embedderFactory, scope.ImageSource, diagnostics, cancellationToken);
        }
        catch (FaceModelsUnavailableException ex)
        {
            diagnostics.Add(ex.Message);
            return BuildResult(request, requestId, FaceAnalysisAvailability.ModelsUnavailable, [], 0, diagnostics);
        }
    }

    private static FaceAnalysisResult AnalyzeImage(
        FaceAnalysisRequest request,
        string requestId,
        IFaceDetectorAdapter detector,
        Func<FaceAnalysisRequest, IFaceEmbeddingAdapter> embedderFactory,
        IFaceImageSource imageSource,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var image = imageSource.LoadImage(request.InputPath);
        var detections = detector.Detect(image);
        var faces = new List<DetectedFace>(detections.Count);
        using var embedder = detections.Count > 0 ? embedderFactory(request) : null;

        foreach (var detection in detections.OrderBy(static d => d.Confidence).ThenBy(static d => d.Bounds.X).ThenBy(static d => d.Bounds.Y))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<float>? embedding = null;
            if (embedder?.IsAvailable == true)
            {
                embedding = TryEmbed(request, embedder, image, detection, diagnostics);
            }

            faces.Add(new DetectedFace
            {
                DetectionKey = FaceDetectionKey.From(
                    request.ExpectedSha256,
                    detection.Bounds,
                    request.YuNetModelId,
                    request.YuNetModelVersion),
                Bounds = detection.Bounds,
                Landmarks = detection.Landmarks,
                Confidence = detection.Confidence,
                Embedding = embedding,
                SampledTimestampMilliseconds = null
            });
        }

        return BuildResult(request, requestId, FaceAnalysisAvailability.Available, faces, 0, diagnostics);
    }

    public static VideoSamplePlan CreateVideoSamplePlan(long? durationMilliseconds)
    {
        var duration = durationMilliseconds.GetValueOrDefault();
        if (duration < 2_000 || duration > 12_000)
        {
            return new VideoSamplePlan { SampleTimestampMilliseconds = [0, 1_000, 3_000, 7_000, 15_000] };
        }

        var endGuard = Math.Min(200, duration / 10);
        var last = Math.Max(0, duration - endGuard);
        var count = duration < 5_000 ? 3 : duration < 9_000 ? 5 : 7;
        var timestamps = Enumerable.Range(0, count)
            .Select(index => count == 1 ? 0 : (long)Math.Round(last * index / (double)(count - 1)))
            .Distinct()
            .ToArray();
        return new VideoSamplePlan { SampleTimestampMilliseconds = timestamps };
    }

    private static FaceAnalysisResult AnalyzeVideo(
        FaceAnalysisRequest request,
        string requestId,
        IFaceDetectorAdapter detector,
        Func<FaceAnalysisRequest, IFaceEmbeddingAdapter> embedderFactory,
        IFaceImageSource imageSource,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var plan = request.VideoSamplePlan;
        if (plan is null || plan.IsEmpty)
        {
            throw new FaceInputException(
                "VIDEO analysis requires a deterministic VideoSamplePlan with at least one timestamp.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var samples = imageSource.LoadVideoSamples(request.InputPath, plan);
        var raw = new List<(long TimestampMs, DetectedFace Face)>();
        IFaceEmbeddingAdapter? embedder = null;

        try
        {
            foreach (var sample in samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = sample.Frame;
                var detections = detector.Detect(frame);
                if (detections.Count > 0)
                {
                    embedder ??= embedderFactory(request);
                }
                foreach (var detection in detections)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var embedding = embedder?.IsAvailable == true
                        ? TryEmbed(request, embedder, frame, detection, diagnostics) : null;
                    raw.Add((sample.TimestampMilliseconds, new DetectedFace
                    {
                        DetectionKey = FaceDetectionKey.From(
                            request.ExpectedSha256, detection.Bounds, request.YuNetModelId,
                            request.YuNetModelVersion, sample.TimestampMilliseconds),
                        Bounds = detection.Bounds,
                        Landmarks = detection.Landmarks,
                        Confidence = detection.Confidence,
                        Embedding = embedding,
                        SampledTimestampMilliseconds = sample.TimestampMilliseconds
                    }));
                }
            }
        }
        finally
        {
            embedder?.Dispose();
            foreach (var sample in samples)
            {
                sample.Frame.Dispose();
            }
        }

        var clustered = VideoFaceClusterer.Cluster(raw, VideoFaceClusterer.CosineThreshold);

        return BuildResult(request, requestId, FaceAnalysisAvailability.Available, clustered, samples.Count, diagnostics);
    }

    private static IReadOnlyList<float>? TryEmbed(
        FaceAnalysisRequest request,
        IFaceEmbeddingAdapter embedder,
        IFaceImage image,
        FaceDetection detection,
        List<string> diagnostics)
    {
        var raw = embedder.ExtractEmbedding(image, detection);
        if (raw.Count == 0)
        {
            return null;
        }

        if (!FaceEmbeddingValidator.TryValidate(
                raw,
                SFaceEmbeddingExtractor.EmbeddingValueCount,
                request.EmbeddingSpaceKey,
                request.SFaceModelId,
                request.SFaceModelVersion,
                out var failure))
        {
            diagnostics.Add($"excluded a detection: {failure}");
            return null;
        }

        return raw;
    }

    private static FaceAnalysisResult BuildResult(
        FaceAnalysisRequest request,
        string requestId,
        FaceAnalysisAvailability availability,
        IReadOnlyList<DetectedFace> faces,
        int sampledFrameCount,
        List<string> diagnostics) =>
        new()
        {
            RequestId = Guid.TryParse(requestId, out var parsed) ? parsed : Guid.Empty,
            MediaId = request.MediaId,
            AnalysisVersion = request.AnalysisVersion,
            YuNetModelId = request.YuNetModelId,
            YuNetModelVersion = request.YuNetModelVersion,
            SFaceModelId = request.SFaceModelId,
            SFaceModelVersion = request.SFaceModelVersion,
            EmbeddingSpaceKey = request.EmbeddingSpaceKey,
            Availability = availability,
            DetectedFaces = faces,
            Diagnostics = diagnostics,
            SampledFrameCount = sampledFrameCount
        };

    private sealed class VerifiedFaceInputLease : IDisposable
    {
        private const int OpenBufferSize = 64 * 1024;
        private readonly FileStream _stream;

        private VerifiedFaceInputLease(FileStream stream)
        {
            _stream = stream;
        }

        public static VerifiedFaceInputLease Open(
            string path,
            string expectedSha256,
            long? expectedByteLength,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (string.IsNullOrWhiteSpace(expectedSha256)
                || expectedSha256.Length != 64
                || expectedSha256.Any(static character => !Uri.IsHexDigit(character)))
            {
                throw new FaceInputException(
                    "The expected SHA-256 fingerprint is missing or malformed.");
            }

            FileStream stream;
            try
            {
                stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    OpenBufferSize,
                    FileOptions.RandomAccess);
            }
            catch (FileNotFoundException)
            {
                throw new FaceInputException("The analyzed media file no longer exists.");
            }
            catch (DirectoryNotFoundException)
            {
                throw new FaceInputException("The analyzed media file path no longer exists.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new FaceInputException("The analyzed media file cannot be read.");
            }
            catch (IOException)
            {
                throw new FaceInputException("The analyzed media file could not be opened safely.");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (expectedByteLength is null or < 0)
                {
                    throw new FaceInputException(
                        "The authoritative Media byte length is missing.");
                }

                if (stream.Length != expectedByteLength.Value)
                {
                    throw new FaceContentMismatchException(
                        "The analyzed media length does not match the authoritative Media record.");
                }

                // Transfer already computed and persisted the canonical SHA-256 while copying.
                // Holding this stream with FileShare.Read prevents mutation during analysis, so
                // re-hashing the entire image/video here would only duplicate expensive I/O.
                return new VerifiedFaceInputLease(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public void Dispose() => _stream.Dispose();
    }

    private sealed class AnalysisScope : IDisposable
    {
        public IFaceImageSource ImageSource { get; }

        private readonly IFaceDetectorAdapter _detector;

        public AnalysisScope(IFaceImageSource imageSource, IFaceDetectorAdapter detector)
        {
            ImageSource = imageSource;
            _detector = detector;
        }

        public void Dispose()
        {
            ImageSource.Dispose();
            _detector.Dispose();
        }
    }
}

public static class VideoFaceClusterer
{
    public const double CosineThreshold = 0.4;

    public static IReadOnlyList<DetectedFace> Cluster(
        IReadOnlyList<(long TimestampMs, DetectedFace Face)> detections,
        double threshold)
    {
        if (detections.Count == 0)
        {
            return [];
        }

        var representatives = new List<DetectedFace>();
        foreach (var (timestamp, face) in detections)
        {
            DetectedFace? matchingCluster = null;
            double bestSimilarity = double.MinValue;

            foreach (var candidate in representatives)
            {
                if (face.Embedding is null || candidate.Embedding is null)
                {
                    continue;
                }

                var similarity = Cosine(face.Embedding, candidate.Embedding);
                if (similarity > threshold && similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    matchingCluster = candidate;
                }
            }

            if (matchingCluster is null)
            {
                representatives.Add(face);
                continue;
            }

            if (face.Confidence > matchingCluster.Confidence)
            {
                var index = representatives.IndexOf(matchingCluster);
                representatives[index] = face;
            }
        }

        return representatives
            .OrderBy(static face => face.SampledTimestampMilliseconds ?? 0)
            .ThenBy(static face => face.Bounds.X)
            .ThenBy(static face => face.Bounds.Y)
            .ToArray();
    }

    public static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }

        if (leftNorm == 0 || rightNorm == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }
}
