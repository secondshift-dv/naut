namespace Neuterradise.Profiling.Protocol;

public sealed record VideoSamplePlan
{
    public VideoSamplePlan() { }

    public VideoSamplePlan(IReadOnlyList<long> sampleTimestampMilliseconds) =>
        SampleTimestampMilliseconds = sampleTimestampMilliseconds;

    public IReadOnlyList<long> SampleTimestampMilliseconds { get; init; } = [];
    public bool IsEmpty => SampleTimestampMilliseconds.Count == 0;
    public int Count => SampleTimestampMilliseconds.Count;
}

public sealed record FaceAnalysisRequest
{
    public FaceAnalysisRequest(
        Guid MediaId,
        string InputPath,
        string ExpectedSha256,
        long? ExpectedByteLength,
        string MediaType,
        string AnalysisVersion,
        string YuNetModelId,
        string YuNetModelVersion,
        string SFaceModelId,
        string SFaceModelVersion,
        string EmbeddingSpaceKey,
        VideoSamplePlan? VideoSamplePlan)
    {
        this.MediaId = MediaId;
        this.InputPath = InputPath;
        this.ExpectedSha256 = ExpectedSha256;
        this.ExpectedByteLength = ExpectedByteLength;
        this.MediaType = MediaType;
        this.AnalysisVersion = AnalysisVersion;
        this.YuNetModelId = YuNetModelId;
        this.YuNetModelVersion = YuNetModelVersion;
        this.SFaceModelId = SFaceModelId;
        this.SFaceModelVersion = SFaceModelVersion;
        this.EmbeddingSpaceKey = EmbeddingSpaceKey;
        this.VideoSamplePlan = VideoSamplePlan;
    }

    public Guid MediaId { get; init; }
    public string InputPath { get; init; } = string.Empty;
    public string ExpectedSha256 { get; init; } = string.Empty;
    public long? ExpectedByteLength { get; init; }
    public string MediaType { get; init; } = string.Empty;
    public string AnalysisVersion { get; init; } = string.Empty;
    public string YuNetModelId { get; init; } = string.Empty;
    public string YuNetModelVersion { get; init; } = string.Empty;
    public string SFaceModelId { get; init; } = string.Empty;
    public string SFaceModelVersion { get; init; } = string.Empty;
    public string EmbeddingSpaceKey { get; init; } = string.Empty;
    public VideoSamplePlan? VideoSamplePlan { get; init; }
    public bool IsVideo => string.Equals(MediaType, "Video", StringComparison.OrdinalIgnoreCase);
}

public enum FaceAnalysisAvailability
{
    Available,
    ModelsUnavailable
}

public sealed record FacePoint
{
    public FacePoint() { }
    public FacePoint(int x, int y) => (X, Y) = (x, y);
    public int X { get; init; }
    public int Y { get; init; }
}

public sealed record FaceBounds
{
    public FaceBounds() { }
    public FaceBounds(int x, int y, int width, int height) => (X, Y, Width, Height) = (x, y, width, height);
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

public sealed record DetectedFace
{
    public string DetectionKey { get; init; } = string.Empty;
    public FaceBounds Bounds { get; init; } = new(0, 0, 0, 0);
    public IReadOnlyList<FacePoint> Landmarks { get; init; } = [];
    public float Confidence { get; init; }
    public IReadOnlyList<float>? Embedding { get; init; }
    public long? SampledTimestampMilliseconds { get; init; }
}

public sealed record FaceAnalysisResult
{
    public Guid RequestId { get; init; }
    public Guid MediaId { get; init; }
    public string AnalysisVersion { get; init; } = string.Empty;
    public string YuNetModelId { get; init; } = string.Empty;
    public string YuNetModelVersion { get; init; } = string.Empty;
    public string SFaceModelId { get; init; } = string.Empty;
    public string SFaceModelVersion { get; init; } = string.Empty;
    public string EmbeddingSpaceKey { get; init; } = string.Empty;
    public FaceAnalysisAvailability Availability { get; init; }
    public IReadOnlyList<DetectedFace> DetectedFaces { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
    public int SampledFrameCount { get; init; }
}

public sealed record StillExtractionRequestItem
{
    public StillExtractionRequestItem(
        string RequestKey,
        long? TimestampMilliseconds,
        FaceBounds? Crop,
        int MaxEdgePixels)
    {
        this.RequestKey = RequestKey;
        this.TimestampMilliseconds = TimestampMilliseconds;
        this.Crop = Crop;
        this.MaxEdgePixels = MaxEdgePixels;
    }

    public string RequestKey { get; init; } = string.Empty;
    public long? TimestampMilliseconds { get; init; }
    public FaceBounds? Crop { get; init; }
    public int MaxEdgePixels { get; init; } = 512;
}

public sealed record ExtractStillsRequest
{
    public ExtractStillsRequest(
        Guid assetId,
        string inputPath,
        string expectedSha256,
        string mediaType,
        IReadOnlyList<StillExtractionRequestItem> requests)
    {
        MediaId = assetId;
        InputPath = inputPath;
        ExpectedSha256 = expectedSha256;
        MediaType = mediaType;
        Requests = requests;
    }

    public Guid MediaId { get; init; }
    public string InputPath { get; init; } = string.Empty;
    public string ExpectedSha256 { get; init; } = string.Empty;
    public string MediaType { get; init; } = string.Empty;
    public IReadOnlyList<StillExtractionRequestItem> Requests { get; init; } = [];
    public bool IsVideo => string.Equals(MediaType, "Video", StringComparison.OrdinalIgnoreCase);
}

public sealed record ExtractedStill
{
    public string RequestKey { get; init; } = string.Empty;
    public string JpegBase64 { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public long? TimestampMilliseconds { get; init; }
}

public sealed record ExtractStillsResult
{
    public Guid MediaId { get; init; }
    public IReadOnlyList<ExtractedStill> Stills { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

public static class IdentityMatchingPolicy
{
    public const int EmbeddingValueCount = 128;
}

public sealed record IdentitySampleData
{
    public IdentitySampleData(
        Guid IdentityId,
        Guid ProfileId,
        Guid IdentitySampleId,
        string EmbeddingSpaceKey,
        string ModelId,
        string ModelVersion,
        IReadOnlyList<float> Embedding)
    {
        this.IdentityId = IdentityId;
        this.ProfileId = ProfileId;
        this.IdentitySampleId = IdentitySampleId;
        this.EmbeddingSpaceKey = EmbeddingSpaceKey;
        this.ModelId = ModelId;
        this.ModelVersion = ModelVersion;
        this.Embedding = Embedding;
    }

    public Guid IdentityId { get; init; }
    public Guid ProfileId { get; init; }
    public Guid IdentitySampleId { get; init; }
    public string EmbeddingSpaceKey { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string ModelVersion { get; init; } = string.Empty;
    public IReadOnlyList<float> Embedding { get; init; } = [];
}

public sealed record BuildIdentityIndexRequest
{
    public BuildIdentityIndexRequest(
        string embeddingSpaceKey,
        string modelId,
        string modelVersion,
        string indexSignature,
        int chunkIndex,
        bool isFinalChunk,
        IReadOnlyList<IdentitySampleData> samples)
    {
        EmbeddingSpaceKey = embeddingSpaceKey;
        ModelId = modelId;
        ModelVersion = modelVersion;
        IndexSignature = indexSignature;
        ChunkIndex = chunkIndex;
        IsFinalChunk = isFinalChunk;
        Samples = samples;
    }

    public string EmbeddingSpaceKey { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string ModelVersion { get; init; } = string.Empty;
    public string IndexSignature { get; init; } = string.Empty;
    public int ChunkIndex { get; init; }
    public bool IsFinalChunk { get; init; }
    public IReadOnlyList<IdentitySampleData> Samples { get; init; } = [];
}

public sealed record BuildIdentityIndexResult
{
    public string EmbeddingSpaceKey { get; init; } = string.Empty;
    public int IdentityCount { get; init; }
    public int SampleCount { get; init; }
    public int ExcludedSampleCount { get; init; }
    public bool IsComplete { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

public sealed record MatchIdentityCandidatesRequest
{
    public MatchIdentityCandidatesRequest(
        string embeddingSpaceKey,
        string modelId,
        string modelVersion,
        IReadOnlyList<float> probeEmbedding,
        double suggestionThreshold,
        int maxSuggestions)
    {
        EmbeddingSpaceKey = embeddingSpaceKey;
        ModelId = modelId;
        ModelVersion = modelVersion;
        ProbeEmbedding = probeEmbedding;
        SuggestionThreshold = suggestionThreshold;
        MaxSuggestions = maxSuggestions;
    }

    public string EmbeddingSpaceKey { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string ModelVersion { get; init; } = string.Empty;
    public IReadOnlyList<float> ProbeEmbedding { get; init; } = [];
    public double SuggestionThreshold { get; init; }
    public int MaxSuggestions { get; init; }
}

public sealed record IdentityCandidate
{
    public Guid IdentityId { get; init; }
    public double Score { get; init; }
    public string EmbeddingSpaceKey { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string ModelVersion { get; init; } = string.Empty;
}

public sealed record MatchIdentityCandidatesResult
{
    public string EmbeddingSpaceKey { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string ModelVersion { get; init; } = string.Empty;
    public IReadOnlyList<IdentityCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

public sealed record ReleaseIdentityIndexRequest
{
    public ReleaseIdentityIndexRequest(string embeddingSpaceKey) => EmbeddingSpaceKey = embeddingSpaceKey;

    public string EmbeddingSpaceKey { get; init; } = string.Empty;
}
