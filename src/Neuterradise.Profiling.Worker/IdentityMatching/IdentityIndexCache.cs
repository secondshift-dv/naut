using Neuterradise.Profiling.Protocol;
using System.Collections.Concurrent;

namespace Neuterradise.Profiling.Worker.IdentityMatching;

public sealed class IdentityIndexCache : IDisposable
{
    private readonly ConcurrentDictionary<string, IdentityIndex> _indexes = new();
    private readonly ConcurrentDictionary<string, string> _signatures = new();
    private readonly ConcurrentDictionary<string, PendingIdentityIndexTransfer> _pendingTransfers = new();

    public int LoadedIndexCount => _indexes.Count;

    public IReadOnlyList<string> LoadedSpaceKeys => _indexes.Keys.Order(StringComparer.Ordinal).ToArray();

    public bool IsSpaceLoaded(string spaceKey) => _indexes.ContainsKey(spaceKey);

    public bool TryGet(string spaceKey, out IdentityIndex? index) => _indexes.TryGetValue(spaceKey, out index);

    public IdentityIndex GetOrBuild(
        string spaceKey,
        string modelId,
        string modelVersion,
        IReadOnlyList<IdentitySampleData> samples)
    {
        var built = IdentityIndex.Build(spaceKey, modelId, modelVersion, samples, out _);
        return _indexes.AddOrUpdate(spaceKey, built, (_, _) => built);
    }

    public BuildIdentityIndexResult AppendChunk(BuildIdentityIndexRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EmbeddingSpaceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IndexSignature);
        ArgumentNullException.ThrowIfNull(request.Samples);

        if (request.ChunkIndex < 0)
        {
            throw new InvalidOperationException("Identity-index chunk index cannot be negative.");
        }

        if (request.ChunkIndex == 0
            && _indexes.TryGetValue(request.EmbeddingSpaceKey, out var cached)
            && _signatures.TryGetValue(request.EmbeddingSpaceKey, out var signature)
            && string.Equals(signature, request.IndexSignature, StringComparison.Ordinal))
        {
            return new BuildIdentityIndexResult
            {
                EmbeddingSpaceKey = cached.EmbeddingSpaceKey,
                IdentityCount = cached.IdentityCount,
                SampleCount = cached.SampleCount,
                IsComplete = true,
            };
        }

        PendingIdentityIndexTransfer transfer;
        if (request.ChunkIndex == 0)
        {
            transfer = new PendingIdentityIndexTransfer(
                request.EmbeddingSpaceKey,
                request.ModelId,
                request.ModelVersion,
                request.IndexSignature);
            _indexes.TryRemove(request.EmbeddingSpaceKey, out _);
            _signatures.TryRemove(request.EmbeddingSpaceKey, out _);
            _pendingTransfers.AddOrUpdate(request.EmbeddingSpaceKey, transfer, (_, _) => transfer);
        }
        else if (!_pendingTransfers.TryGetValue(request.EmbeddingSpaceKey, out transfer!))
        {
            throw new InvalidOperationException(
                $"Identity-index transfer for space '{request.EmbeddingSpaceKey}' was not started.");
        }

        var cumulativeSampleCount = transfer.Append(request);
        if (!request.IsFinalChunk)
        {
            return new BuildIdentityIndexResult
            {
                EmbeddingSpaceKey = request.EmbeddingSpaceKey,
                SampleCount = cumulativeSampleCount,
                IsComplete = false,
            };
        }

        try
        {
            var samples = transfer.Snapshot();
            var built = IdentityIndex.Build(
                request.EmbeddingSpaceKey,
                request.ModelId,
                request.ModelVersion,
                samples,
                out var diagnostics);

            _indexes.AddOrUpdate(request.EmbeddingSpaceKey, built, (_, _) => built);
            _signatures[request.EmbeddingSpaceKey] = request.IndexSignature;
            _pendingTransfers.TryRemove(request.EmbeddingSpaceKey, out _);

            return new BuildIdentityIndexResult
            {
                EmbeddingSpaceKey = built.EmbeddingSpaceKey,
                IdentityCount = built.IdentityCount,
                SampleCount = built.SampleCount,
                ExcludedSampleCount = samples.Count - built.SampleCount,
                IsComplete = true,
                Diagnostics = diagnostics,
            };
        }
        catch
        {
            _pendingTransfers.TryRemove(request.EmbeddingSpaceKey, out _);
            throw;
        }
    }

    public bool Invalidate(string spaceKey)
    {
        var removedIndex = _indexes.TryRemove(spaceKey, out _);
        _signatures.TryRemove(spaceKey, out _);
        var removedTransfer = _pendingTransfers.TryRemove(spaceKey, out _);
        return removedIndex || removedTransfer;
    }

    public void InvalidateAll()
    {
        _pendingTransfers.Clear();
        _indexes.Clear();
        _signatures.Clear();
    }

    public void Dispose() => InvalidateAll();

    private sealed class PendingIdentityIndexTransfer
    {
        private readonly Lock _gate = new();
        private readonly string _spaceKey;
        private readonly string _modelId;
        private readonly string _modelVersion;
        private readonly string _signature;
        private readonly List<IdentitySampleData> _samples = [];
        private int _nextChunkIndex;

        public PendingIdentityIndexTransfer(
            string spaceKey,
            string modelId,
            string modelVersion,
            string signature)
        {
            _spaceKey = spaceKey;
            _modelId = modelId;
            _modelVersion = modelVersion;
            _signature = signature;
        }

        public int Append(BuildIdentityIndexRequest request)
        {
            lock (_gate)
            {
                if (!string.Equals(request.EmbeddingSpaceKey, _spaceKey, StringComparison.Ordinal)
                    || !string.Equals(request.ModelId, _modelId, StringComparison.Ordinal)
                    || !string.Equals(request.ModelVersion, _modelVersion, StringComparison.Ordinal)
                    || !string.Equals(request.IndexSignature, _signature, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Identity-index chunk provenance does not match the active transfer.");
                }

                if (request.ChunkIndex != _nextChunkIndex)
                {
                    throw new InvalidOperationException(
                        $"Identity-index chunk {request.ChunkIndex} arrived out of order; expected {_nextChunkIndex}.");
                }

                _samples.AddRange(request.Samples);
                _nextChunkIndex++;
                return _samples.Count;
            }
        }

        public IReadOnlyList<IdentitySampleData> Snapshot()
        {
            lock (_gate)
            {
                return _samples.ToArray();
            }
        }
    }
}
