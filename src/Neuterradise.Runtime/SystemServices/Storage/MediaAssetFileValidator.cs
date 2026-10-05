using System.Security.Cryptography;
using Neuterradise.App.Media.Video;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.MediaTools;
using SkiaSharp;

namespace Neuterradise.App.SystemServices.Storage;

public sealed record ValidatedArtifactFile(long ByteLength, string Sha256,
    int? PixelWidth, int? PixelHeight, long? DurationMs);

public sealed class MediaAssetFileValidator
{
    private readonly VideoMetadataAdapter _video;
    private readonly ExternalToolResolver _tools;
    private readonly IProcessLauncher _launcher;

    public MediaAssetFileValidator(VideoMetadataAdapter? video = null,
        ExternalToolResolver? tools = null, IProcessLauncher? launcher = null)
    {
        _tools = tools ?? ExternalToolResolver.ForProduction();
        _video = video ?? new VideoMetadataAdapter(toolResolver: _tools);
        _launcher = launcher ?? new BoundedProcessLauncher(TimeSpan.FromSeconds(30));
    }

    public async Task<ValidatedArtifactFile> ValidateAsync(string path, MediaAssetRole role,
        CancellationToken ct = default, string? sourceSha256 = null)
    {
        if (role == MediaAssetRole.ModelRender)
        {
            var file = await ModelRenderContainer.ValidateAsync(path, sourceSha256, ct).ConfigureAwait(false);
            return new(file.ByteLength, file.Sha256, null, null, null);
        }
        if (role is not (MediaAssetRole.Thumbnail or MediaAssetRole.Hover))
            throw new ArgumentOutOfRangeException(nameof(role));
        var valid = await ValidateAsync(path, role == MediaAssetRole.Thumbnail, 6000, ct)
            .ConfigureAwait(false);
        if (role == MediaAssetRole.Thumbnail
            && (valid.PixelWidth > 1024 || valid.PixelHeight > 1024))
            throw new InvalidDataException("Canonical thumbnail exceeds its 1024 pixel edge contract.");
        if (role == MediaAssetRole.Hover
            && (valid.PixelWidth > 1280 || valid.PixelHeight > 720))
            throw new InvalidDataException("Canonical hover exceeds its 1280x720 pixel contract.");
        return valid;
    }

    private async Task<ValidatedArtifactFile> ValidateAsync(string path, bool still, int maxDurationMs,
        CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0) throw new InvalidDataException("MediaAsset file is empty or missing.");

        int? width;
        int? height;
        long? duration = null;
        if (still)
        {
            await using var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous);
            using var managed = new SKManagedStream(image, disposeManagedStream: false);
            using var codec = SKCodec.Create(managed);
            if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Webp
                || codec.Info.Width <= 0 || codec.Info.Height <= 0)
                throw new InvalidDataException("Artifact must be a decodable WebP image.");
            width = codec.Info.Width;
            height = codec.Info.Height;
            using var bitmap = new SKBitmap(new SKImageInfo(width.Value, height.Value));
            if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
                throw new InvalidDataException("Artifact image pixels cannot be decoded.");
        }
        else
        {
            if (_tools.TryResolveExecutablePath(ExternalToolResolver.FfprobeToolId) is null)
                throw new InvalidDataException("Approved video stream probe is unavailable.");
            var metadata = await _video.ProbeAsync(path, ct).ConfigureAwait(false);
            duration = metadata.Duration is { } length ? (long)Math.Ceiling(length.TotalMilliseconds) : null;
            width = metadata.Width;
            height = metadata.Height;
            if (width is null or <= 0 || height is null or <= 0 || duration is null or <= 0
                || duration > maxDurationMs || metadata.AudioCodec is not null
                || !IsH264(metadata.VideoCodec)
                || metadata.Container is null || !metadata.Container.Contains("mp4", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Artifact video violates the duration, codec, audio or dimension contract.");
            var ffmpeg = _tools.TryResolveExecutablePath(ExternalToolResolver.FfmpegToolId)
                ?? throw new InvalidDataException("Approved video decoder is unavailable.");
            var result = await _launcher.RunAsync(new ProcessRunRequest(ffmpeg,
                ["-v", "error", "-i", path, "-map", "0:v:0", "-f", "null", "-"],
                Timeout: TimeSpan.FromSeconds(30)), ct).ConfigureAwait(false);
            if (result.TimedOut || result.ExitCode != 0)
                throw new InvalidDataException("Artifact video cannot be decoded.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return new ValidatedArtifactFile(info.Length, Convert.ToHexStringLower(hash), width, height, duration);
    }

    private static bool IsH264(string? codec) =>
        string.Equals(codec, "H.264 / AVC", StringComparison.OrdinalIgnoreCase)
        || string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase)
        || string.Equals(codec, "avc1", StringComparison.OrdinalIgnoreCase);
}
