using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;
using Neuterradise.App.SystemServices.MediaTools;

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
    public static PresentationAssetImportPackage Backdrop(string source, ExternalToolResolver? tools = null) => Build(source, font: false, tools);
    public static PresentationAssetImportPackage Font(string source) => Build(source, font: true);

    private static PresentationAssetImportPackage Build(string source, bool font, ExternalToolResolver? tools = null)
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
            var assets = new List<object> { new { id = "asset", kind = kind.ToString(), path = "assets/asset" + extension } };
            if (font)
            {
                using var typeface = SKTypeface.FromFile(file) ?? throw new InvalidDataException("This font cannot be decoded.");
                var face = new { family = typeface.FamilyName, asset = "asset" };
                spec = new { display = face, heading = face, body = face, mono = new { family = "Consolas" } };
            }
            else
            {
                var layer = new { kind = kind == PackAssetKind.Video ? "video" : "image", asset = "asset", opacity = 1 };
                object stillLayer = layer;
                if (kind == PackAssetKind.Video)
                {
                    var decoder = (tools ?? ExternalToolResolver.ForProduction()).TryResolveExecutablePath(ExternalToolResolver.FfmpegToolId)
                        ?? throw new InvalidDataException("The approved video decoder is unavailable.");
                    var poster = Path.Combine(staging, "assets", "poster.png");
                    var result = new BoundedProcessLauncher().RunAsync(new ProcessRunRequest(decoder,
                        ["-nostdin", "-v", "error", "-max_alloc", "67108864", "-threads", "1",
                         "-protocol_whitelist", "file", "-format_whitelist", "mov,matroska,webm", "-i", file,
                         "-map", "0:v:0", "-an", "-sn", "-dn", "-vf", "scale='min(1280,iw)':'min(1280,ih)':force_original_aspect_ratio=decrease",
                         "-threads", "1", "-frames:v", "1", poster], Timeout: TimeSpan.FromSeconds(15), MaxCapturedCharacters: 4096))
                        .GetAwaiter().GetResult();
                    using var bitmap = File.Exists(poster) ? SKBitmap.Decode(poster) : null;
                    if (result.ExitCode != 0 || result.TimedOut || bitmap is null || bitmap.Width > 1280 || bitmap.Height > 1280)
                        throw new InvalidDataException("The video poster could not be prepared.");
                    assets.Add(new { id = "poster", kind = "Image", path = "assets/poster.png" });
                    stillLayer = new { kind = "image", asset = "poster", opacity = 1 };
                }
                var still = new { layers = new[] { stillLayer } };
                spec = new { variants = new { full = new { layers = kind == PackAssetKind.Video ? new[] { stillLayer, layer } : new object[] { layer } }, reduced = still, fallback = still } };
            }
            var manifest = new
            {
                schemaVersion = PresentationContract.PackSchemaVersion, packId = pack, version = "1.0.0", name,
                minContractVersion = PresentationContract.Version,
                assets,
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
