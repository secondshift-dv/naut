using Neuterradise.App.Media.Model;

namespace Neuterradise.App.Ui.Figure;

public sealed record FigureMeshData(ModelRenderMesh Descriptor, byte[] Vertices, byte[] Indices);
public sealed record FigureTextureData(int Index, ModelRenderTexture Descriptor, byte[][] Mips);

/// <summary>Validated portable scene bytes; loading never decodes or prepares canonical GLB media.</summary>
public sealed class FigureSceneData
{
    public const long CpuBudget = 192L * 1024 * 1024;
    private FigureSceneData(ModelRenderHeader header, FigureMeshData[] meshes, FigureTextureData[] textures, int lod, long bytes)
    {
        Header = header;
        Meshes = meshes;
        Textures = textures;
        Lod = lod;
        ByteLength = bytes;
    }

    public ModelRenderHeader Header { get; }
    public FigureMeshData[] Meshes { get; }
    public FigureTextureData[] Textures { get; }
    public int Lod { get; }
    public long ByteLength { get; }

    public static async Task<FigureSceneData> LoadAsync(string nfigPath, string? expectedSourceSha256 = null,
        int preferredLod = 1, CancellationToken ct = default, int maximumTextureDimension = 2048)
    {
        if (preferredLod is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(preferredLod));
        if (maximumTextureDimension is < 1 or > 8192) throw new ArgumentOutOfRangeException(nameof(maximumTextureDimension));
        var file = await ModelRenderContainer.ValidateAsync(nfigPath, expectedSourceSha256, ct).ConfigureAwait(false);
        var selected = new List<ModelRenderMesh>();
        var group = new List<ModelRenderMesh>();
        foreach (var mesh in file.Header.Meshes)
        {
            if (mesh.Lod == 0 && group.Count != 0) SelectGroup();
            group.Add(mesh);
        }
        SelectGroup();
        void SelectGroup()
        {
            // Small primitives may have no reduced representation; retain their original geometry.
            selected.Add(group.OrderBy(mesh => Math.Abs(mesh.Lod - preferredLod)).ThenBy(mesh => mesh.Lod).First());
            group.Clear();
        }

        var meshes = new List<FigureMeshData>();
        long bytes = 0;
        foreach (var mesh in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (bytes + file.Header.Chunks[mesh.VertexChunk].Length + file.Header.Chunks[mesh.IndexChunk].Length > CpuBudget)
                throw new InvalidDataException("Figure geometry exceeds its resident allocation budget.");
            var vertices = await ModelRenderContainer.ReadChunkAsync(nfigPath, file, mesh.VertexChunk, ct).ConfigureAwait(false);
            var indices = await ModelRenderContainer.ReadChunkAsync(nfigPath, file, mesh.IndexChunk, ct).ConfigureAwait(false);
            bytes = checked(bytes + vertices.LongLength + indices.LongLength);
            meshes.Add(new(mesh, vertices, indices));
        }
        var usedTextures = selected.Select(mesh => file.Header.Materials[mesh.Material])
            .SelectMany(material => new[] { material.BaseColorTexture, material.MetallicRoughnessTexture,
                material.NormalTexture, material.OcclusionTexture, material.EmissiveTexture })
            .Where(index => index.HasValue).Select(index => index!.Value).Distinct().Order().ToArray();
        var textures = new List<FigureTextureData>();
        foreach (var index in usedTextures)
        {
            var descriptor = file.Header.Textures[index];
            var firstMip = 0;
            while ((descriptor.Width >> firstMip) > maximumTextureDimension || (descriptor.Height >> firstMip) > maximumTextureDimension)
                firstMip++;
            descriptor = descriptor with { Width = Math.Max(1, descriptor.Width >> firstMip),
                Height = Math.Max(1, descriptor.Height >> firstMip), MipChunks = descriptor.MipChunks[firstMip..] };
            var mips = new byte[descriptor.MipChunks.Length][];
            for (var mip = 0; mip < mips.Length; mip++)
            {
                if (bytes + file.Header.Chunks[descriptor.MipChunks[mip]].Length > CpuBudget)
                    throw new InvalidDataException("Figure scene exceeds its resident allocation budget.");
                mips[mip] = await ModelRenderContainer.ReadChunkAsync(nfigPath, file, descriptor.MipChunks[mip], ct).ConfigureAwait(false);
                bytes = checked(bytes + mips[mip].LongLength);
            }
            textures.Add(new(index, descriptor, mips));
        }
        return new(file.Header, meshes.ToArray(), textures.ToArray(), preferredLod, bytes);
    }
}
