using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;

namespace Neuterradise.App.Presentation;

public sealed class PresentationAssetImportPackage(string stagingPath, DefinitionRef definition) : IDisposable
{
    public string StagingPath { get; } = stagingPath;
    public DefinitionRef Definition { get; } = definition;
    public void Dispose()
    {
        try { Directory.Delete(StagingPath, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public static class PresentationAssetImportBuilder
{
    public static PresentationAssetImportPackage Backdrop(string source) => Build(source, font: false);
    public static PresentationAssetImportPackage Font(string source) => Build(source, font: true);

    private static PresentationAssetImportPackage Build(string source, bool font)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var kind = font ? PackAssetKind.Font : PackValidator.AllowedExtensions[PackAssetKind.Video].Contains(extension) ? PackAssetKind.Video : PackAssetKind.Image;
        if (!PackValidator.AllowedExtensions[kind].Contains(extension)) throw new InvalidDataException("The presentation asset type is unsupported.");
        var info = new FileInfo(source);
        if (!info.Exists || info.Length <= 0 || info.Length > PackValidator.MaxAssetBytes[kind]) throw new InvalidDataException("The presentation asset exceeds its size limit.");
        var staging = Path.Combine(Path.GetTempPath(), "NeuTerradise", "presentation-intake", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(staging, "assets"));
        try
        {
            var file = Path.Combine(staging, "assets", "asset" + extension);
            File.Copy(source, file);
            string hash;
            using (var stream = File.OpenRead(file)) hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var pack = $"custom.{(font ? "font" : "backdrop")}.{hash[..24]}";
            var definition = new DefinitionRef(pack, pack + ".component");
            var name = Path.GetFileNameWithoutExtension(source);
            name = new string(name.Where(c => !char.IsControl(c)).Take(64).ToArray());
            object spec;
            if (font)
            {
                using var typeface = SKTypeface.FromFile(file) ?? throw new InvalidDataException("This font cannot be decoded.");
                var face = new { family = typeface.FamilyName, asset = "asset" };
                spec = new { display = face, heading = face, body = face, mono = new { family = "Consolas" } };
            }
            else
            {
                var layer = new { kind = kind == PackAssetKind.Video ? "video" : "image", asset = "asset", opacity = 1 };
                object empty = new { layers = Array.Empty<object>() };
                var still = kind == PackAssetKind.Video ? empty : new { layers = new object[] { layer } };
                spec = new { variants = new { full = new { layers = new object[] { layer } }, reduced = still, fallback = still } };
            }
            var manifest = new
            {
                schemaVersion = PresentationContract.PackSchemaVersion, packId = pack, version = "1.0.0", name,
                minContractVersion = PresentationContract.Version,
                assets = new[] { new { id = "asset", kind = kind.ToString(), path = "assets/asset" + extension } },
                definitions = new[] { new { id = definition.DefinitionId, kind = font ? DefinitionKinds.Typography : DefinitionKinds.Backdrop, name, spec } },
            };
            File.WriteAllText(Path.Combine(staging, "pack.json"), JsonSerializer.Serialize(manifest));
            return new PresentationAssetImportPackage(staging, definition);
        }
        catch
        {
            Directory.Delete(staging, recursive: true);
            throw;
        }
    }
}
