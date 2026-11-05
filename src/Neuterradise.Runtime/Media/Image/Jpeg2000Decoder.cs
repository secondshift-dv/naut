using System.Diagnostics;
using Neuterradise.App.SystemServices.MediaTools;
using SkiaSharp;

namespace Neuterradise.App.Media.Image;

/// <summary>Bounded JPEG 2000 fallback through the approved bundled FFmpeg, without a disk derivative.</summary>
public static class Jpeg2000Decoder
{
    public static bool IsSource(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[12];
        var read = file.Read(header);
        return read >= 4 && header[..4].SequenceEqual(new byte[] { 255, 79, 255, 81 })
            || read == 12 && header.SequenceEqual(new byte[] { 0, 0, 0, 12, 106, 80, 32, 32, 13, 10, 135, 10 });
    }

    public static SKBitmap? Decode(string path, int width, int? height = null, ExternalToolResolver? tools = null,
        CancellationToken ct = default) => DecodeAsync(path, width, height, tools, ct).GetAwaiter().GetResult();

    private static async Task<SKBitmap?> DecodeAsync(string path, int width, int? height,
        ExternalToolResolver? tools, CancellationToken ct)
    {
        var executable = (tools ?? ExternalToolResolver.ForProduction()).TryResolveExecutablePath(ExternalToolResolver.FfmpegToolId);
        if (executable is null) return null;
        width = Math.Clamp(width, 1, 4096);
        var maximumHeight = Math.Clamp(height ?? 4096, 1, 4096);
        var png = await BoundedMediaFrameDecoder.DecodeAsync(executable, path, width, maximumHeight, ct: ct).ConfigureAwait(false);
        if (png is null) return null;
        using var memory = new MemoryStream(png, writable: false);
        using var managed = new SKManagedStream(memory, false);
        using var codec = SKCodec.Create(managed);
        if (codec is null || codec.Info.Width > width || codec.Info.Height > maximumHeight
            || codec.Info.Width <= 0 || codec.Info.Height <= 0) return null;
        var bitmap = new SKBitmap(codec.Info.WithColorType(SKColorType.Bgra8888).WithAlphaType(SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) == SKCodecResult.Success) return bitmap;
        bitmap.Dispose();
        return null;
    }
}