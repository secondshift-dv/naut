using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Neuterradise.App.Presentation;
using SkiaSharp;

var repository = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "packs/catalog.json")));
if (catalog.RootElement.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported collection schema.");
var ids = new HashSet<string>(StringComparer.Ordinal);
var slugs = new HashSet<string>(StringComparer.Ordinal);
var downloads = new List<object>();
foreach (var entry in catalog.RootElement.GetProperty("packs").EnumerateArray())
{
    var slug = entry.GetProperty("slug").GetString()!;
    if (!PackValidator.IsValidId(slug) || slug.Contains('.') || !slugs.Add(slug)) throw new InvalidDataException("Invalid or duplicate slug.");
    var root = Path.Combine(repository, "packs", slug);
    var manifestRead = PackManifestReader.Read(File.ReadAllText(Path.Combine(root, "pack.json")), slug);
    var manifest = manifestRead.Manifest ?? throw new InvalidDataException($"Invalid manifest: {slug}");
    var diagnostics = manifestRead.Diagnostics.Concat(PackValidator.Validate(manifest, root, false))
        .Concat(PresentationCompiler.ValidateDefinitions(manifest)).ToArray();
    foreach (var error in diagnostics.Where(d => d.IsError)) Console.Error.WriteLine(error);
    if (diagnostics.Any(d => d.IsError)) throw new InvalidDataException($"Pack validation failed: {slug}");
    if (!ids.Add(manifest.PackId) || manifest.PackId != entry.GetProperty("packId").GetString()
        || manifest.Version != entry.GetProperty("version").GetString() || !Version.TryParse(manifest.Version, out _))
        throw new InvalidDataException($"Catalog identity/version mismatch: {slug}");
    foreach (var required in new[] { "LICENSE.txt", "PROVENANCE.txt" })
        if (!File.Exists(Path.Combine(root, required)) || new FileInfo(Path.Combine(root, required)).Length == 0)
            throw new InvalidDataException($"Missing {required}: {slug}");
    if (!File.Exists(Path.Combine(root, "README.md")) && !File.Exists(Path.Combine(root, "README.txt")))
        throw new InvalidDataException($"Missing installation guide: {slug}");
    var preview = entry.GetProperty("preview").GetString();
    if (preview != "preview.png") throw new InvalidDataException("The collection preview must be preview.png.");
    using var bitmap = SKBitmap.Decode(Path.Combine(root, preview));
    if (bitmap is null || bitmap.Width > 4096 || bitmap.Height > 4096) throw new InvalidDataException($"Invalid preview: {slug}");
    var archive = Path.Combine(output, $"{slug}-{manifest.Version}.ntpack");
    using (var zip = new ZipArchive(File.Create(archive), ZipArchiveMode.Create))
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var item = zip.CreateEntry(Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'), CompressionLevel.Optimal);
            item.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var target = item.Open();
            using var source = File.OpenRead(file);
            source.CopyTo(target);
        }
    }
    var sandbox = Path.Combine(output, ".validation", Guid.NewGuid().ToString("N"));
    var store = new PresentationPackStore(Path.Combine(sandbox, "packs"), null);
    var install = await store.InstallAsync(archive, false);
    if (!install.Succeeded || install.Pack is null) throw new InvalidDataException($"Install failed: {slug}: {install.Message}");
    var compiler = new PresentationCompiler(store);
    foreach (var definition in manifest.Definitions)
        if (compiler.Compile(install.Pack, definition) is not { } plan || plan.Diagnostics.Any(d => d.IsError))
            throw new InvalidDataException($"Resolved compile failed: {definition.Id}");
    var replace = await store.InstallAsync(archive, true);
    if (!replace.Succeeded) throw new InvalidDataException($"Replacement failed: {slug}");
    var reload = new PresentationPackStore(Path.Combine(sandbox, "packs"), null);
    var loaded = reload.LoadAll().Single(p => p.PackId == manifest.PackId);
    if (!loaded.IsUsable || loaded.ContentHash != replace.Pack!.ContentHash) throw new InvalidDataException("Reload mismatch.");
    var exported = Path.Combine(sandbox, "export.ntpack");
    await reload.ExportAsync(manifest.PackId, exported);
    var roundtrip = await new PresentationPackStore(Path.Combine(sandbox, "roundtrip"), null).InstallAsync(exported, false);
    if (!roundtrip.Succeeded || roundtrip.Pack?.ContentHash != loaded.ContentHash) throw new InvalidDataException("Export roundtrip mismatch.");
    using var input = File.OpenRead(archive);
    var sha = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    downloads.Add(new { slug, packId = manifest.PackId, version = manifest.Version, file = Path.GetFileName(archive), sha256 = sha, bytes = new FileInfo(archive).Length });
    Console.WriteLine($"PACK={manifest.PackId}; VERSION={manifest.Version}; DEFINITIONS={manifest.Definitions.Count}; ASSETS={manifest.Assets.Count}; PASS");
}
if (downloads.Count == 0) throw new InvalidDataException("The collection is empty.");
File.WriteAllText(Path.Combine(output, "checksums.json"), JsonSerializer.Serialize(new { schemaVersion = 1, packs = downloads }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"CURATED_PACKS=PASS ({downloads.Count})");
