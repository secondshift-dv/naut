using Neuterradise.Profiling.Protocol;
using Neuterradise.Profiling.Worker.FaceAnalysis;

namespace Neuterradise.Profiling.Worker.IdentityMatching;

public sealed class IdentityMatcher
{
    private readonly IdentityIndexCache _indexCache;

    public IdentityMatcher(IdentityIndexCache indexCache)
    {
        _indexCache = indexCache ?? throw new ArgumentNullException(nameof(indexCache));
    }

    public MatchIdentityCandidatesResult Match(MatchIdentityCandidatesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var diagnostics = new List<string>();

        if (!_indexCache.TryGet(request.EmbeddingSpaceKey, out var index) || index is null)
        {
            diagnostics.Add($"No Identity index is built for space '{request.EmbeddingSpaceKey}'; build it before matching.");
            return new MatchIdentityCandidatesResult
            {
                EmbeddingSpaceKey = request.EmbeddingSpaceKey,
                ModelId = request.ModelId,
                ModelVersion = request.ModelVersion,
                Candidates = [],
                Diagnostics = diagnostics
            };
        }

        if (!string.Equals(index.ModelId, request.ModelId, StringComparison.Ordinal)
            || !string.Equals(index.ModelVersion, request.ModelVersion, StringComparison.Ordinal))
        {
            throw new IdentitySpaceMismatchException(
                $"Probe provenance {request.ModelId}/{request.ModelVersion} is incompatible with index provenance {index.ModelId}/{index.ModelVersion} for space '{request.EmbeddingSpaceKey}'.");
        }

        if (!FaceEmbeddingValidator.TryValidate(
                request.ProbeEmbedding,
                IdentityMatchingPolicy.EmbeddingValueCount,
                request.EmbeddingSpaceKey,
                request.ModelId,
                request.ModelVersion,
                out var probeFailure))
        {
            diagnostics.Add($"probe rejected: {probeFailure}");
            return new MatchIdentityCandidatesResult
            {
                EmbeddingSpaceKey = request.EmbeddingSpaceKey,
                ModelId = request.ModelId,
                ModelVersion = request.ModelVersion,
                Candidates = [],
                Diagnostics = diagnostics
            };
        }

        var candidates = index.Rank(
            request.ProbeEmbedding,
            request.SuggestionThreshold,
            request.MaxSuggestions);

        return new MatchIdentityCandidatesResult
        {
            EmbeddingSpaceKey = request.EmbeddingSpaceKey,
            ModelId = request.ModelId,
            ModelVersion = request.ModelVersion,
            Candidates = candidates,
            Diagnostics = diagnostics
        };
    }
}
