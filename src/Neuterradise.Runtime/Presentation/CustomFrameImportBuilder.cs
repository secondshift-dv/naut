using System.Security.Cryptography;
using System.Text.Json;
using Neuterradise.App.SystemServices.Cache;
using SkiaSharp;

namespace Neuterradise.App.Presentation;

/// <summary>
/// Builds a one-definition user Presentation Pack from a transparent square image.
/// The installed result uses the normal pack registry/compiler/binding pipeline; this helper owns
/// only authoring/staging and never becomes a second presentation authority.
/// </summary>
public sealed class CustomFrameImportPackage : IDisposable
{
    internal CustomFrameImportPackage(string stagingPath, string packId, string definitionId, string displayName)
    {
        StagingPath = stagingPath;
        PackId = packId;
        DefinitionId = definitionId;
        DisplayName = displayName;
    }

    public string StagingPath { get; }

    public string PackId { get; }

    public string DefinitionId { get; }

    public string DisplayName { get; }

    public DefinitionRef Definition => new(PackId, DefinitionId);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(StagingPath))
            {
                Directory.Delete(StagingPath, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class CustomFrameImportBuilder
{
    private static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".webp" };

    public const int MinimumPixels = 256;
    public const int MaximumPixels = 4096;

    public static CustomFrameImportPackage Build(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var fullSource = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSource))
        {
            throw new FileNotFoundException("The custom frame image does not exist.", fullSource);
        }

        var extension = Path.GetExtension(fullSource);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new InvalidDataException("Custom frames use a transparent square PNG or WebP image.");
        }

        ValidateArtwork(fullSource);

        byte[] hash;
        using (var stream = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            hash = SHA256.HashData(stream);
        }

        var hashText = Convert.ToHexString(hash).ToLowerInvariant();
        var packId = $"custom.frame.{hashText[..16]}";
        var definitionId = $"{packId}.frame";
        var displayName = CleanDisplayName(Path.GetFileNameWithoutExtension(fullSource));

        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            "NeuTerradise",
            "custom-frame-import",
            Guid.NewGuid().ToString("N"));
        var assetsPath = Path.Combine(stagingPath, "assets");
        Directory.CreateDirectory(assetsPath);

        var assetFileName = "frame" + extension.ToLowerInvariant();
        File.Copy(fullSource, Path.Combine(assetsPath, assetFileName), overwrite: false);

        var manifest = new
        {
            schemaVersion = PresentationContract.PackSchemaVersion,
            packId,
            version = "1.0.0",
            name = displayName,
            author = "Local user",
            description = "Custom transparent Cover frame imported in naut.",
            minContractVersion = PresentationContract.Version,
            assets = new[]
            {
                new
                {
                    id = "frame-overlay",
                    kind = nameof(PackAssetKind.Image),
                    path = $"assets/{assetFileName}",
                },
            },
            definitions = new[]
            {
                new
                {
                    id = definitionId,
                    kind = DefinitionKinds.CoverFrame,
                    version = "1.0.0",
                    name = displayName,
                    description = "Custom artwork frame.",
                    tags = new[] { "custom", "frame" },
                    performance = new
                    {
                        tier = "utility",
                        continuousAnimation = false,
                        offscreenPolicy = "suspend",
                        maxAnimatedLayers = 0,
                    },
                    spec = new
                    {
                        family = "Minimal",
                        shapes = new[] { "RoundedSquare", "Square", "Circle" },
                        scale = 1.0,
                        tint = false,
                        animated = false,
                        category = "Ornate",
                        overlayAsset = "frame-overlay",
                        coverInset = 0.0,
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(stagingPath, PackManifestReader.ManifestFileName), json);

        return new CustomFrameImportPackage(stagingPath, packId, definitionId, displayName);
    }

    private static void ValidateArtwork(string sourcePath)
    {
        using var codec = SKCodec.Create(sourcePath)
            ?? throw new InvalidDataException("The custom frame image could not be decoded.");
        var info = codec.Info;
        if (info.Width != info.Height)
        {
            throw new InvalidDataException("Custom frame artwork must be square.");
        }

        if (info.Width < MinimumPixels || info.Width > MaximumPixels)
        {
            throw new InvalidDataException(
                $"Custom frame artwork must be between {MinimumPixels}×{MinimumPixels} and {MaximumPixels}×{MaximumPixels} pixels.");
        }

        using var preview = TargetSizeDecoder.Decode(sourcePath, 512)
            ?? throw new InvalidDataException("The custom frame image could not be decoded.");
        var pixels = preview.Pixels;
        if (pixels.Length == 0)
        {
            throw new InvalidDataException("The custom frame image contains no pixels.");
        }

        var transparent = 0;
        foreach (var pixel in pixels)
        {
            if (pixel.Alpha < 240)
            {
                transparent++;
            }
        }

        var center = pixels[(preview.Height / 2 * preview.Width) + (preview.Width / 2)];
        if (center.Alpha >= 240 || transparent < pixels.Length / 20)
        {
            throw new InvalidDataException(
                "Custom frame artwork needs a transparent center so the Profile Cover remains visible.");
        }
    }

    private static string CleanDisplayName(string value)
    {
        var trimmed = string.Join(' ', value.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "Custom Frame";
        }

        return trimmed.Length <= 64 ? trimmed : trimmed[..64].TrimEnd();
    }
}

