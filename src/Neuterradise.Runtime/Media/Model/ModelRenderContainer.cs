using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Neuterradise.App.Media.Model;

public sealed record ModelRenderChunk(string Kind, long Offset, int Length, string Sha256);
public sealed record ModelRenderMesh(int Vertices, int Indices, int VertexChunk, int IndexChunk,
    int Material, int Lod);
public sealed record ModelRenderTexture(int Width, int Height, bool Srgb, int[] MipChunks,
    int WrapS = 10497, int WrapT = 10497, int MinFilter = 9987, int MagFilter = 9729);
public sealed record ModelRenderMaterial(float[] BaseColor, float Metallic, float Roughness,
    float[] Emissive, int? BaseColorTexture = null, int? MetallicRoughnessTexture = null,
    int? NormalTexture = null, int? OcclusionTexture = null, int? EmissiveTexture = null,
    float NormalScale = 1, float OcclusionStrength = 1, string AlphaMode = "OPAQUE",
    float AlphaCutoff = 0.5f, bool DoubleSided = false);
public sealed record ModelRenderHeader(int ContainerVersion, int ContractVersion, string SourceSha256,
    float[] Minimum, float[] Maximum, float[] GroundOffset, float NormalizationScale,
    ModelRenderMesh[] Meshes, ModelRenderMaterial[] Materials, ModelRenderTexture[] Textures,
    ModelRenderChunk[] Chunks);
public sealed record ModelRenderFile(ModelRenderHeader Header, long PayloadOffset, long ByteLength, string Sha256);

/// <summary>Portable NFIG: bounded JSON descriptors and contiguous, individually hashed binary chunks.</summary>
public static class ModelRenderContainer
{
    public const int ContractVersion = 1;
    public const long MaxBytes = 768L * 1024 * 1024;
    public const int MaxHeaderBytes = 1024 * 1024;
    public const int VertexStride = 48; // position, normal, UV, tangent including handedness
    private const uint Magic = 0x4749464e;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static async Task WriteAsync(string path, ModelRenderHeader header,
        IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct = default)
    {
        if (payloads.Count is < 1 or > 4096) throw Invalid("chunk count");
        if (header.Chunks is null || header.Chunks.Length != payloads.Count) throw Invalid("chunk descriptors");
        long offset = 0;
        var chunks = new ModelRenderChunk[payloads.Count];
        for (var i = 0; i < chunks.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var payload = payloads[i];
            if (payload.IsEmpty || payload.Length > MaxBytes - offset) throw Invalid("payload budget");
            if (header.Chunks[i] is null) throw Invalid("chunk descriptor");
            chunks[i] = new(header.Chunks[i].Kind, offset, payload.Length,
                Convert.ToHexStringLower(SHA256.HashData(payload.Span)));
            offset += payload.Length;
        }
        header = header with { Chunks = chunks };
        ValidateHeader(header, offset);
        var json = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        if (json.Length > MaxHeaderBytes || offset > MaxBytes - json.Length - 12) throw Invalid("file budget");
        var prefix = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), ContractVersion);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(8), json.Length);
        var created = false;
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous))
            {
                created = true;
                await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
                await stream.WriteAsync(json, ct).ConfigureAwait(false);
                foreach (var payload in payloads) await stream.WriteAsync(payload, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            await ValidateAsync(path, header.SourceSha256, ct).ConfigureAwait(false);
        }
        catch
        {
            // Only bytes created by this operation may be removed on failed validation.
            if (created) File.Delete(path);
            throw;
        }
    }

    public static async Task<ModelRenderFile> ValidateAsync(string path, string? expectedSourceSha256 = null,
        CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 12 or > MaxBytes) throw Invalid("file length");
        var prefix = new byte[12];
        await ReadExactlyAsync(stream, prefix, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8));
        if (BinaryPrimitives.ReadUInt32LittleEndian(prefix) != Magic
            || BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4)) != ContractVersion
            || length is < 2 or > MaxHeaderBytes || length > stream.Length - 12) throw Invalid("header");
        var json = new byte[length];
        await ReadExactlyAsync(stream, json, ct).ConfigureAwait(false);
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        fileHash.AppendData(prefix); fileHash.AppendData(json);
        ModelRenderHeader header;
        try { header = JsonSerializer.Deserialize<ModelRenderHeader>(json, JsonOptions) ?? throw Invalid("empty header"); }
        catch (JsonException ex) { throw new InvalidDataException("Malformed NFIG descriptors.", ex); }
        var payloadOffset = stream.Position;
        ValidateHeader(header, stream.Length - payloadOffset);
        if (expectedSourceSha256 is not null && header.SourceSha256 != expectedSourceSha256)
            throw Invalid("source provenance");
        var buffer = new byte[65536];
        for (var index = 0; index < header.Chunks.Length; index++)
        {
            var chunk = header.Chunks[index];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var remaining = chunk.Length;
            while (remaining > 0)
            {
                var count = Math.Min(remaining, buffer.Length);
                // Geometry values are validated in aligned blocks without allocating a full scene.
                if (chunk.Kind == "VERTICES" && count < remaining) count -= count % VertexStride;
                await ReadExactlyAsync(stream, buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);
                fileHash.AppendData(buffer, 0, count);
                ValidateValues(header, index, buffer.AsSpan(0, count));
                remaining -= count;
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != chunk.Sha256) throw Invalid("chunk checksum");
        }
        return new(header, payloadOffset, stream.Length, Convert.ToHexStringLower(fileHash.GetHashAndReset()));
    }

    public static async Task<byte[]> ReadChunkAsync(string path, ModelRenderFile file, int index,
        CancellationToken ct = default)
    {
        if (file.PayloadOffset is < 14 or > MaxHeaderBytes + 12 || file.ByteLength > MaxBytes)
            throw Invalid("file descriptor");
        ValidateHeader(file.Header, file.ByteLength - file.PayloadOffset);
        if ((uint)index >= file.Header.Chunks.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var chunk = file.Header.Chunks[index];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous);
        if (stream.Length != file.ByteLength) throw Invalid("changed file");
        stream.Position = file.PayloadOffset + chunk.Offset;
        var bytes = new byte[chunk.Length];
        await ReadExactlyAsync(stream, bytes, ct).ConfigureAwait(false);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != chunk.Sha256) throw Invalid("changed chunk");
        ValidateValues(file.Header, index, bytes);
        return bytes;
    }

    private static void ValidateHeader(ModelRenderHeader h, long payloadLength)
    {
        if (h.ContainerVersion != 1 || h.ContractVersion != 1 || !IsHash(h.SourceSha256)) throw Invalid("contract");
        Floats(h.Minimum, 3); Floats(h.Maximum, 3); Floats(h.GroundOffset, 3);
        if (!float.IsFinite(h.NormalizationScale) || h.NormalizationScale is <= 0 or > 1_000_000) throw Invalid("normalization");
        for (var i = 0; i < 3; i++) if (h.Minimum[i] > h.Maximum[i]) throw Invalid("bounds");
        if (Enumerable.Range(0, 3).All(i => h.Minimum[i] == h.Maximum[i])) throw Invalid("empty bounds");
        if (h.Meshes is null || h.Meshes.Length is < 1 or > 1024
            || h.Materials is null || h.Materials.Length is < 1 or > 256
            || h.Textures is null || h.Textures.Length > 128
            || h.Chunks is null || h.Chunks.Length is < 2 or > 4096) throw Invalid("descriptor budgets");
        long offset = 0;
        foreach (var chunk in h.Chunks)
        {
            if (chunk is null || chunk.Kind is not ("VERTICES" or "INDICES" or "RGBA8")
                || chunk.Offset != offset || chunk.Length <= 0 || chunk.Length > payloadLength - offset
                || !IsHash(chunk.Sha256)) throw Invalid("chunk range");
            offset += chunk.Length;
        }
        if (offset != payloadLength) throw Invalid("unclaimed payload");
        long vertices = 0, indices = 0, textureBytes = 0;
        var used = new HashSet<int>();
        foreach (var mesh in h.Meshes)
        {
            if (mesh is null || mesh.Vertices is < 3 or > 4_000_000 || mesh.Indices is < 3 or > 12_000_000
                || mesh.Indices % 3 != 0 || (uint)mesh.Material >= h.Materials.Length || mesh.Lod is < 0 or > 3)
                throw Invalid("mesh");
            Chunk(mesh.VertexChunk, "VERTICES", (long)mesh.Vertices * VertexStride);
            Chunk(mesh.IndexChunk, "INDICES", (long)mesh.Indices * 4);
            vertices += mesh.Vertices; indices += mesh.Indices;
        }
        if (vertices > 8_000_000 || indices > 24_000_000 || !h.Meshes.Any(m => m.Lod == 0)) throw Invalid("geometry budget");
        if (h.Textures.Any(t => t is null)) throw Invalid("texture descriptor");
        foreach (var material in h.Materials)
        {
            if (material is null) throw Invalid("material");
            Floats(material.BaseColor, 4, unit: true); Floats(material.Emissive, 3);
            Unit(material.Metallic); Unit(material.Roughness); Unit(material.OcclusionStrength); Unit(material.AlphaCutoff);
            if (!float.IsFinite(material.NormalScale) || Math.Abs(material.NormalScale) > 16
                || material.Emissive.Any(x => x < 0 || x > 64)
                || material.AlphaMode is not ("OPAQUE" or "MASK" or "BLEND")) throw Invalid("material factors");
            foreach (var t in new[] { material.BaseColorTexture, material.MetallicRoughnessTexture,
                material.NormalTexture, material.OcclusionTexture, material.EmissiveTexture })
                if (t is { } id && (uint)id >= h.Textures.Length) throw Invalid("texture reference");
            foreach (var t in new[] { material.BaseColorTexture, material.EmissiveTexture })
                if (t is { } id && !h.Textures[id].Srgb) throw Invalid("color texture space");
            foreach (var t in new[] { material.NormalTexture, material.MetallicRoughnessTexture, material.OcclusionTexture })
                if (t is { } id && h.Textures[id].Srgb) throw Invalid("linear texture space");
        }
        foreach (var texture in h.Textures)
        {
            if (texture is null || texture.Width is < 1 or > 8192 || texture.Height is < 1 or > 8192
                || texture.MipChunks is null || texture.MipChunks.Length is < 1 or > 14) throw Invalid("texture");
            if (texture.WrapS is not (33071 or 33648 or 10497) || texture.WrapT is not (33071 or 33648 or 10497)
                || texture.MinFilter is not (9728 or 9729 or 9984 or 9985 or 9986 or 9987)
                || texture.MagFilter is not (9728 or 9729)) throw Invalid("sampler");
            var width = texture.Width; var height = texture.Height;
            for (var i = 0; i < texture.MipChunks.Length; i++)
            {
                var bytes = (long)width * height * 4;
                Chunk(texture.MipChunks[i], "RGBA8", bytes); textureBytes += bytes;
                if (width == 1 && height == 1 && i != texture.MipChunks.Length - 1) throw Invalid("extra mip");
                width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
            }
            var expectedMips = 1 + (int)Math.Floor(Math.Log2(Math.Max(texture.Width, texture.Height)));
            if (texture.MipChunks.Length != expectedMips) throw Invalid("incomplete mip chain");
        }
        if (textureBytes > 512L * 1024 * 1024 || used.Count != h.Chunks.Length) throw Invalid("payload ownership");

        void Chunk(int index, string kind, long length)
        {
            if ((uint)index >= h.Chunks.Length || h.Chunks[index].Kind != kind
                || h.Chunks[index].Length != length || !used.Add(index)) throw Invalid("chunk binding");
        }
    }

    private static void ValidateValues(ModelRenderHeader h, int index, ReadOnlySpan<byte> bytes)
    {
        var kind = h.Chunks[index].Kind;
        if (kind == "RGBA8") return;
        var mesh = h.Meshes.First(m => kind == "VERTICES" ? m.VertexChunk == index : m.IndexChunk == index);
        if (kind == "INDICES")
        {
            for (var offset = 0; offset < bytes.Length; offset += 4)
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]) >= mesh.Vertices) throw Invalid("index value");
            return;
        }
        for (var offset = 0; offset < bytes.Length; offset += 4)
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
            if (!float.IsFinite(value) || Math.Abs(value) > 1_000_000_000) throw Invalid("vertex value");
            var field = offset / 4 % 12;
            if (field < 3 && (value < h.Minimum[field] - 0.001f || value > h.Maximum[field] + 0.001f))
                throw Invalid("vertex bounds");
            if ((field is >= 3 and <= 5 or >= 8 and <= 10) && Math.Abs(value) > 1.001f)
                throw Invalid("direction component");
            if (field == 11 && value is not (-1 or 1)) throw Invalid("tangent handedness");
        }
        for (var offset = 0; offset < bytes.Length; offset += VertexStride)
        {
            var n = new System.Numerics.Vector3(BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 12)..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 16)..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 20)..]));
            var t = new System.Numerics.Vector3(BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 32)..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 36)..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 40)..]));
            if (Math.Abs(n.LengthSquared() - 1) > 0.01f || Math.Abs(t.LengthSquared() - 1) > 0.01f
                || Math.Abs(System.Numerics.Vector3.Dot(n, t)) > 0.01f) throw Invalid("tangent frame");
        }
    }

    private static void Floats(float[]? values, int count, bool unit = false)
    {
        if (values is null || values.Length != count || values.Any(x => !float.IsFinite(x) || Math.Abs(x) > 1_000_000_000))
            throw Invalid("numeric metadata");
        if (unit) foreach (var value in values) Unit(value);
    }
    private static void Unit(float value) { if (!float.IsFinite(value) || value is < 0 or > 1) throw Invalid("unit factor"); }
    private static bool IsHash(string? hash) => hash is { Length: 64 }
        && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static InvalidDataException Invalid(string reason) => new($"Invalid NFIG {reason}.");
    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> bytes, CancellationToken ct)
    {
        try { await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated NFIG payload.", ex); }
    }
}
