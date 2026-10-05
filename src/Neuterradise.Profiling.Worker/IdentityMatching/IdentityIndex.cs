using Neuterradise.Profiling.Protocol;
using Neuterradise.Profiling.Worker.FaceAnalysis;

namespace Neuterradise.Profiling.Worker.IdentityMatching;

public sealed class IdentityIndex
{
    private readonly string _spaceKey;
    private readonly string _modelId;
    private readonly string _modelVersion;
    private readonly IReadOnlyList<IndexedIdentity> _identities;

    private IdentityIndex(
        string spaceKey,
        string modelId,
        string modelVersion,
        IReadOnlyList<IndexedIdentity> identities)
    {
        _spaceKey = spaceKey;
        _modelId = modelId;
        _modelVersion = modelVersion;
        _identities = identities;
    }

    public string EmbeddingSpaceKey => _spaceKey;

    public string ModelId => _modelId;

    public string ModelVersion => _modelVersion;

    public int IdentityCount => _identities.Count;

    public int SampleCount => _identities.Sum(static identity => identity.Samples.Length);

    public IReadOnlyList<Guid> IdentityIds => _identities.Select(static identity => identity.IdentityId).ToArray();

    public static IdentityIndex Build(
        string spaceKey,
        string modelId,
        string modelVersion,
        IReadOnlyList<IdentitySampleData> samples,
        out IReadOnlyList<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var diagnosticsList = new List<string>();
        var grouped = new SortedDictionary<Guid, List<IdentitySampleData>>();

        foreach (var sample in samples.OrderBy(static s => s.IdentitySampleId))
        {

            if (!string.Equals(sample.EmbeddingSpaceKey, spaceKey, StringComparison.Ordinal))
            {
                diagnosticsList.Add(
                    $"excluded sample {sample.IdentitySampleId}: its space '{sample.EmbeddingSpaceKey}' is not the exact index space '{spaceKey}'.");
                continue;
            }

            if (!string.Equals(sample.ModelId, modelId, StringComparison.Ordinal) || !string.Equals(sample.ModelVersion, modelVersion, StringComparison.Ordinal))
            {
                diagnosticsList.Add($"excluded sample {sample.IdentitySampleId}: model provenance does not match index.");
                continue;
            }

            if (!FaceEmbeddingValidator.TryValidate(
                    sample.Embedding,
                    IdentityMatchingPolicy.EmbeddingValueCount,
                    spaceKey,
                    modelId,
                    modelVersion,
                    out var failure))
            {
                diagnosticsList.Add($"excluded sample {sample.IdentitySampleId}: {failure}");
                continue;
            }

            if (!grouped.TryGetValue(sample.IdentityId, out var identitySamples))
            {
                identitySamples = [];
                grouped[sample.IdentityId] = identitySamples;
            }

            identitySamples.Add(sample);
        }

        var identities = grouped
            .Select(static pair => new IndexedIdentity(pair.Key, pair.Value.ToArray()))
            .ToArray();

        diagnostics = diagnosticsList;

        return new IdentityIndex(spaceKey, modelId, modelVersion, identities)
        {
            Diagnostics = diagnosticsList
        };
    }

    public double? ScoreIdentity(Guid identityId, IReadOnlyList<float> probe)
    {
        var identity = _identities.FirstOrDefault(candidate => candidate.IdentityId == identityId);
        if (identity is null || identity.Samples.Length == 0)
        {
            return null;
        }

        var best = double.MinValue;
        foreach (var sample in identity.Samples)
        {
            var similarity = VideoFaceClusterer.Cosine(probe, sample.Embedding);
            if (similarity > best)
            {
                best = similarity;
            }
        }

        return best;
    }

    public IReadOnlyList<IdentityCandidate> Rank(
        IReadOnlyList<float> probe,
        double threshold,
        int maximum)
    {
        return _identities
            .Select(identity => new
            {
                identity.IdentityId,
                Score = ScoreIdentity(identity.IdentityId, probe) ?? double.NegativeInfinity
            })
            .Where(scored => !double.IsNegativeInfinity(scored.Score) && scored.Score >= threshold)
            .OrderByDescending(static scored => scored.Score)
            .ThenBy(static scored => scored.IdentityId.ToString("D"), StringComparer.Ordinal)
            .Take(maximum)
            .Select(scored => new IdentityCandidate
            {
                IdentityId = scored.IdentityId,
                Score = scored.Score,
                EmbeddingSpaceKey = _spaceKey,
                ModelId = _modelId,
                ModelVersion = _modelVersion
            })
            .ToArray();
    }

    public IReadOnlyList<string> Diagnostics { get; private init; } = [];

    private sealed record IndexedIdentity(Guid IdentityId, IdentitySampleData[] Samples);
}
