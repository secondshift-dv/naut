using System.Security.Cryptography;
using System.Text.Json;
using SharpAssimp;

// Native parsers run in a disposable process; all reads come from hash-verified manifest bytes.
try
{
    if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]))
        throw new ArgumentException("Expected an absolute input manifest and output GLB path.");
    if (new FileInfo(args[0]).Length > 256 * 1024) throw new InvalidDataException("Model manifest is too large.");
    var input = JsonSerializer.Deserialize<ModelInput>(File.ReadAllText(args[0]))
        ?? throw new InvalidDataException("Model manifest is missing.");
    if (input.Files.Length is < 1 or > 256) throw new InvalidDataException("Model component budget exceeded.");
    var virtualRoot = Path.Combine(Path.GetDirectoryName(args[0])!, "input");
    var io = new VerifiedModelIO(virtualRoot, input.Files);
    var native = Path.Combine(AppContext.BaseDirectory, "assimp.dll");
    if (!File.Exists(native)
        || Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(native))) != "253e2b63978a3ad42cc32ce7659dc078e9ccc64392dfa42e7ae3d08322922ec1")
        throw new InvalidDataException("The approved Assimp native library is missing or changed.");
    SharpAssimp.Unmanaged.AssimpLibrary.Instance.LoadLibrary(native);
    var primary = io.Resolve(input.Primary);
    var extension = Path.GetExtension(primary).ToLowerInvariant();
    if (extension is not (".fbx" or ".obj" or ".stl" or ".3mf"))
        throw new NotSupportedException("This worker only converts FBX, OBJ, STL and 3MF.");
    if (extension == ".3mf")
    {
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(io.ReadTexture(primary), false));
        if (archive.Entries.Count > 1024 || archive.Entries.Sum(entry => entry.Length) > 256 * 1024 * 1024)
            throw new InvalidDataException("3MF expanded package budget exceeded.");
    }
    using var nativeIO = new Utf8ModelIO(io.ReadTexture);
    var library = SharpAssimp.Unmanaged.AssimpLibrary.Instance;
    var pointer = library.ImportFile(primary, PostProcessSteps.Triangulate
        | PostProcessSteps.GenerateSmoothNormals | PostProcessSteps.CalculateTangentSpace
        | PostProcessSteps.PreTransformVertices | PostProcessSteps.ValidateDataStructure, nativeIO.Pointer, IntPtr.Zero);
    if (pointer == IntPtr.Zero) throw new InvalidDataException("Native model parser rejected the declared package.");
    Scene scene;
    try { scene = Scene.FromUnmanagedScene(pointer) ?? throw new InvalidDataException("Model scene is missing."); }
    finally { library.ReleaseImport(pointer); }
    if (scene is null || scene.MeshCount is < 1 or > 256
        || scene.Meshes.Sum(mesh => (long)mesh.VertexCount) > 2_000_000
        || scene.Meshes.Sum(mesh => (long)mesh.FaceCount) > 4_000_000
        || scene.MaterialCount > 256 || scene.TextureCount > 128)
        throw new InvalidDataException("Model scene is empty or exceeds the Figure budget.");
    var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    long textureBytes = scene.Textures.OfType<EmbeddedTexture>().Sum(texture => (long)texture.CompressedDataSize);
    foreach (var material in scene.Materials.OfType<Material>())
    {
        foreach (var original in material.GetAllMaterialTextures().ToArray())
        {
            var slot = original;
            if (slot.FilePath.StartsWith('*')) continue;
            if (!textures.TryGetValue(slot.FilePath, out var embedded))
            {
                var bytes = io.ReadTexture(slot.FilePath);
                textureBytes = checked(textureBytes + bytes.Length);
                if (textureBytes > 128 * 1024 * 1024 || scene.TextureCount >= 128)
                    throw new InvalidDataException("Model texture budget exceeded.");
                var hint = Path.GetExtension(slot.FilePath).TrimStart('.').ToLowerInvariant();
                if (hint is not ("png" or "jpg" or "jpeg"))
                    throw new NotSupportedException("Figure textures must be PNG or JPEG.");
                embedded = "*" + scene.TextureCount;
                scene.Textures.Add(new EmbeddedTexture(hint == "jpeg" ? "jpg" : hint, bytes, slot.FilePath));
                textures.Add(slot.FilePath, embedded);
            }
            slot.FilePath = embedded;
            material.AddMaterialTexture(in slot, true);
        }
    }
    // Figure is a static display derivative. Original rig/animation remains in the original asset.
    scene.Animations.Clear();
    using var exporter = new AssimpContext();
    var blob = exporter.ExportToBlob(scene, "glb2");
    var data = blob?.Data;
    if (data is null || data.Length is < 20 or > 256 * 1024 * 1024 || blob?.NextBlob is not null)
        throw new InvalidDataException("Conversion did not produce one bounded GLB.");
    File.WriteAllBytes(args[1], data);
    Console.WriteLine("MODEL_CONVERSION=PASS");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
    return 1;
}

sealed record ModelInput(string Primary, ModelFile[] Files);
sealed record ModelFile(string Name, string Path, string Sha256);

sealed class VerifiedModelIO
{
    private readonly string _root;
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public VerifiedModelIO(string root, ModelFile[] files)
    {
        _root = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (var file in files)
        {
            var info = new FileInfo(file.Path);
            if (!Path.IsPathFullyQualified(file.Path) || info.Length is < 1 or > 256 * 1024 * 1024
                || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Invalid model component.");
            total = checked(total + info.Length);
            if (total > 256 * 1024 * 1024) throw new InvalidDataException("Model package byte budget exceeded.");
            var bytes = File.ReadAllBytes(file.Path);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256)
                throw new InvalidDataException("Model component hash changed.");
            if (!_files.TryAdd(Resolve(file.Name), bytes))
                throw new InvalidDataException("Duplicate model component.");
        }
    }

    public string Resolve(string path)
    {
        var resolved = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(_root, path));
        if (!resolved.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model dependency escapes the declared package.");
        return resolved;
    }

    public byte[] ReadTexture(string path)
    {
        if (!_files.TryGetValue(Resolve(path), out var bytes))
            throw new InvalidDataException("Model texture is missing from the declared package.");
        return bytes;
    }

}
