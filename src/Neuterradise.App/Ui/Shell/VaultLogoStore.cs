using System.Security.Cryptography;
using Neuterradise.App.SystemServices.Storage;
using SkiaSharp;

namespace Neuterradise.App.Ui;

/// <summary>
/// A Vault-owned visual identity. The PNG lives in the Vault's _system directory,
/// so switching, renaming, moving and backing up the whole Vault preserve its logo.
/// </summary>
internal static class VaultLogoStore
{
    private const int Side = 256;
    private const long MaxInputBytes = 8L * 1024 * 1024;
    private const int MaxDimension = 4096;
    private const long MaxPixels = 16L * 1024 * 1024;
    private const string FileName = "vault-logo.png";

    private static string LogoPath(VaultPaths paths) => Path.Combine(paths.SystemPath, FileName);

    public static ImageRef? Read(VaultPaths paths)
    {
        var path = LogoPath(paths);
        try
        {
            RootPathRules.RejectExistingReparsePoints(paths.Root, path);
            if (!File.Exists(path)) return null;
            var file = new FileInfo(path);
            if (file.Length is <= 0 or > MaxInputBytes) return null;
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            return ImageRef.FromPath(path, 96, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static void Save(VaultPaths paths, string selectedPath)
    {
        var original = new FileInfo(selectedPath);
        if (!original.Exists || original.Length is <= 0 or > MaxInputBytes)
            throw new InvalidDataException("Choose an image smaller than 8 MB.");

        using var input = original.OpenRead();
        using var codec = SKCodec.Create(input)
            ?? throw new InvalidDataException("The selected file is not a supported image.");
        var info = codec.Info;
        if (info.Width is <= 0 or > MaxDimension || info.Height is <= 0 or > MaxDimension
            || (long)info.Width * info.Height > MaxPixels)
            throw new InvalidDataException("The image dimensions exceed the supported limit.");

        using var decoded = SKBitmap.Decode(codec)
            ?? throw new InvalidDataException("The selected image could not be decoded.");
        using var surface = SKSurface.Create(new SKImageInfo(Side, Side))
            ?? throw new InvalidOperationException("Could not prepare the Vault logo.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var ratio = Math.Min((float)Side / decoded.Width, (float)Side / decoded.Height);
        var w = decoded.Width * ratio;
        var h = decoded.Height * ratio;
        canvas.DrawBitmap(decoded, new SKRect((Side - w) / 2, (Side - h) / 2,
            (Side + w) / 2, (Side + h) / 2));
        canvas.Flush();
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Could not encode the Vault logo.");

        var target = LogoPath(paths);
        RootPathRules.RejectExistingReparsePoints(paths.Root, target);
        Directory.CreateDirectory(paths.SystemPath);
        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                data.SaveTo(output);
                output.Flush(flushToDisk: true);
            }
            RootPathRules.RejectExistingReparsePoints(paths.Root, target);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static void Remove(VaultPaths paths)
    {
        var target = LogoPath(paths);
        RootPathRules.RejectExistingReparsePoints(paths.Root, target);
        if (File.Exists(target)) File.Delete(target);
    }
}