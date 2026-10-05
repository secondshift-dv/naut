using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neuterradise.App.Faces;

public sealed record IdentitySampleSummary(
    Guid IdentitySampleId,
    Guid IdentityId,
    Guid ProfileId,
    Guid FaceId,
    Guid MediaId,
    EmbeddingSpaceKey EmbeddingSpace,
    string ModelId,
    string ModelVersion,
    DateTimeOffset ConfirmedAtUtc);

public sealed record FaceIdentityCandidate(
    Guid IdentityId,
    Guid? ProfileId,
    string? ProfileDisplayName,
    double Similarity,
    int Rank);

public sealed record FaceSuggestionEvidenceV1
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("embeddingSpaceKey")]
    public string EmbeddingSpaceKey { get; init; } = string.Empty;

    [JsonPropertyName("bankSignature")]
    public string BankSignature { get; init; } = string.Empty;

    [JsonPropertyName("threshold")]
    public double Threshold { get; init; }

    [JsonPropertyName("candidates")]
    public IReadOnlyList<FaceSuggestionEntryV1> Candidates { get; init; } = [];

    private static readonly JsonSerializerOptions _options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, _options);

    public static FaceSuggestionEvidenceV1? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<FaceSuggestionEvidenceV1>(json, _options);
            return parsed is null || parsed.SchemaVersion != CurrentSchemaVersion ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record FaceBoundingBox(int X, int Y, int Width, int Height)
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static FaceBoundingBox? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<FaceBoundingBox>(json, _options);
            return parsed is null || parsed.Width <= 0 || parsed.Height <= 0 ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public FaceBoundingBox WithMargin(double fraction)
    {
        var marginX = (int)Math.Round(Width * fraction);
        var marginY = (int)Math.Round(Height * fraction);
        return new FaceBoundingBox(
            Math.Max(0, X - marginX),
            Math.Max(0, Y - marginY),
            Width + (marginX * 2),
            Height + (marginY * 2));
    }
}

public sealed record FaceSuggestionEntryV1
{
    [JsonPropertyName("identityId")]
    public Guid IdentityId { get; init; }

    [JsonPropertyName("similarity")]
    public double Similarity { get; init; }
}

public static class IdentityBankSignature
{

    public const string Empty = "none";

    public static string For(string embeddingSpaceKey, IEnumerable<Guid> identitySampleIds)
    {
        ArgumentNullException.ThrowIfNull(identitySampleIds);

        var ordered = identitySampleIds
            .OrderBy(static id => id.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0)
        {
            return Empty;
        }

        var material = new StringBuilder(embeddingSpaceKey);
        foreach (var id in ordered)
        {
            material.Append(';').Append(id.ToString("D"));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }
}

public sealed record FaceReviewReadModel(
    Guid FaceId,
    Guid MediaId,
    string DetectionKey,
    string BoundingBoxJson,
    Guid? SuggestedIdentityId,
    Guid? SuggestedProfileId,
    string? SuggestedProfileDisplayName,
    Guid? ConfirmedIdentityId,
    Guid? ConfirmedProfileId,
    string? ConfirmedProfileDisplayName,
    double? Confidence,
    FaceDecisionState DecisionState,
    string ModelId,
    string ModelVersion,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long RowVersion,
    long? SampledTimestampMilliseconds = null,
    IReadOnlyList<FaceIdentityCandidate>? IdentityCandidates = null,
    string? SuggestionBankSignature = null)
{

    public IReadOnlyList<FaceIdentityCandidate> Candidates => IdentityCandidates ?? [];

    public bool IsVideoFrameDetection => SampledTimestampMilliseconds.HasValue;
}
