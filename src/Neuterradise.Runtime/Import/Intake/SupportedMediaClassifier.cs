using System.IO;
using Neuterradise.App.Media;

namespace Neuterradise.App.Import.Intake;

public static class SupportedMediaClassifier
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tiff", ".tif", ".avif", ".heic", ".heif", ".ico"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".m4v", ".flv", ".3gp", ".ts"
    };

    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".glb", ".gltf", ".obj", ".fbx", ".stl", ".3mf", ".dae", ".ply", ".blend"
    };

    public static IReadOnlySet<string> SupportedImageExtensions => ImageExtensions;
    public static IReadOnlySet<string> SupportedVideoExtensions => VideoExtensions;
    public static IReadOnlySet<string> SupportedModelExtensions => ModelExtensions;

    public static IEnumerable<string> AllSupportedExtensions =>
        ImageExtensions.Concat(VideoExtensions).Concat(ModelExtensions);

    public static MediaType? Classify(string? pathOrExtension)
    {
        if (string.IsNullOrWhiteSpace(pathOrExtension))
        {
            return null;
        }

        var ext = Path.GetExtension(pathOrExtension);
        if (string.IsNullOrEmpty(ext))
        {
            ext = pathOrExtension.StartsWith('.') ? pathOrExtension : "." + pathOrExtension;
        }

        if (ImageExtensions.Contains(ext))
        {
            return MediaType.Image;
        }

        if (VideoExtensions.Contains(ext))
        {
            return MediaType.Video;
        }

        if (ModelExtensions.Contains(ext))
        {
            return MediaType.Model;
        }

        return null;
    }

    public static bool TryClassify(string? pathOrExtension, out MediaType classification)
    {
        var result = Classify(pathOrExtension);
        if (result.HasValue)
        {
            classification = result.Value;
            return true;
        }

        classification = default;
        return false;
    }

    public static bool IsSupported(string? pathOrExtension) => Classify(pathOrExtension).HasValue;

    public static bool IsImage(string? pathOrExtension) => Classify(pathOrExtension) == MediaType.Image;

    public static bool IsVideo(string? pathOrExtension) => Classify(pathOrExtension) == MediaType.Video;

    public static bool IsModel(string? pathOrExtension) => Classify(pathOrExtension) == MediaType.Model;

    public static string BuildFileDialogFilter()
    {
        var allPattern = string.Join(";", AllSupportedExtensions.Select(e => $"*{e}"));
        var imgPattern = string.Join(";", ImageExtensions.Select(e => $"*{e}"));
        var vidPattern = string.Join(";", VideoExtensions.Select(e => $"*{e}"));
        var mdlPattern = string.Join(";", ModelExtensions.Select(e => $"*{e}"));

        return $"All Supported Media|{allPattern}|Images|{imgPattern}|Videos|{vidPattern}|3D Models|{mdlPattern}|All Files|*.*";
    }
}
