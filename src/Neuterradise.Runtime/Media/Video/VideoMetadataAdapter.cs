using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Neuterradise.App.SystemServices.MediaTools;

namespace Neuterradise.App.Media.Video;

public sealed record VideoMetadata(
    string? Container = null,
    long? FileSize = null,
    TimeSpan? Duration = null,
    int? Width = null,
    int? Height = null,
    string? VideoCodec = null,
    string? VideoCodecLongName = null,
    string? VideoProfile = null,
    double? FrameRate = null,
    string? FrameRateRational = null,
    long? Bitrate = null,
    string? PixelFormat = null,
    string? ColorPrimaries = null,
    string? ColorTransfer = null,
    string? ColorSpace = null,
    bool? IsHdr = null,
    string? HdrType = null,
    int? Rotation = null,
    string? AudioCodec = null,
    string? AudioCodecLongName = null,
    int? AudioSampleRate = null,
    int? AudioChannels = null,
    string? AudioChannelLayout = null,
    long? AudioBitrate = null,
    DateTimeOffset? CreationTime = null,
    DateTimeOffset? RecordedTime = null,
    DateTimeOffset? CapturedAt = null,
    double? Latitude = null,
    double? Longitude = null,
    double? AltitudeMeters = null,
    string? LocationName = null,
    string? DeviceMake = null,
    string? DeviceModel = null,
    string? Software = null,
    string? Encoder = null,
    IReadOnlyDictionary<string, string>? FormatTags = null,
    IReadOnlyDictionary<string, string>? VideoStreamTags = null,
    IReadOnlyDictionary<string, string>? AudioStreamTags = null,
    IReadOnlyList<string>? Diagnostics = null)
{
    public static VideoMetadata Empty { get; } = new();

    public static DateTimeOffset? ResolveCaptureTime(
        DateTimeOffset? recordedTime,
        DateTimeOffset? creationTime)
    {
        if (recordedTime.HasValue) return recordedTime.Value;
        if (creationTime.HasValue) return creationTime.Value;
        return null;
    }
}

public sealed class VideoMetadataAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly Regex Iso6709Regex = new(
        @"^([+-]\d{2,3}(?:\.\d+)?)([+-]\d{2,3}(?:\.\d+)?)(?:([+-]\d+(?:\.\d+)?))?/?$",
        RegexOptions.Compiled);

    private readonly IProcessLauncher _launcher;
    private readonly string? _ffprobeExecutablePath;
    private readonly ExternalToolResolver _toolResolver;

    public VideoMetadataAdapter(
        IProcessLauncher? launcher = null,
        string? ffprobeExecutablePath = null,
        ExternalToolResolver? toolResolver = null)
    {
        _launcher = launcher ?? new BoundedProcessLauncher(TimeSpan.FromSeconds(10));
        _ffprobeExecutablePath = ffprobeExecutablePath;
        _toolResolver = toolResolver ?? ExternalToolResolver.ForProduction();
    }

    public async Task<VideoMetadata> ProbeAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFilePath);

        if (!File.Exists(mediaFilePath))
        {
            return new VideoMetadata(Diagnostics: [$"Media file does not exist: {mediaFilePath}"]);
        }

        var (ffprobe, unavailable) = ResolveFfprobePath();
        if (string.IsNullOrWhiteSpace(ffprobe))
        {

            var reason = unavailable ?? "The approved ffprobe artifact could not be resolved.";
            var direct = TryProbeDirectly(mediaFilePath);
            if (direct is not null)
            {
                var diag = new List<string> { $"{reason} Extracted container metadata directly." };
                if (direct.Diagnostics != null) diag.AddRange(direct.Diagnostics);
                return direct with { Diagnostics = diag };
            }

            return new VideoMetadata(Diagnostics: [$"{reason} Direct probe is not supported for this format."]);
        }

        try
        {
            var request = new ProcessRunRequest(
                ExecutablePath: ffprobe,
                Arguments: ["-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", mediaFilePath],
                Timeout: TimeSpan.FromSeconds(10));

            var runResult = await _launcher.RunAsync(request, cancellationToken).ConfigureAwait(false);

            if (runResult.TimedOut)
            {

                var directFallback = TryProbeDirectly(mediaFilePath);
                var diags = new List<string> { "ffprobe process timed out." };
                if (directFallback is not null)
                {
                    return directFallback with { Diagnostics = diags };
                }
                return new VideoMetadata(Diagnostics: diags);
            }

            if (runResult.ExitCode != 0)
            {
                var directFallback = TryProbeDirectly(mediaFilePath);
                var diags = new List<string> { $"ffprobe failed with exit code {runResult.ExitCode}: {runResult.StandardError}" };
                if (directFallback is not null)
                {
                    return directFallback with { Diagnostics = diags };
                }
                return new VideoMetadata(Diagnostics: diags);
            }

            var parsed = ParseJson(runResult.StandardOutput);

            if (!parsed.FileSize.HasValue)
            {
                try
                {
                    var fi = new FileInfo(mediaFilePath);
                    parsed = parsed with { FileSize = fi.Length };
                }
                catch { }
            }

            return parsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var directFallback = TryProbeDirectly(mediaFilePath);
            var diags = new List<string> { $"Failed to launch ffprobe: {ex.Message}" };
            if (directFallback is not null)
            {
                return directFallback with { Diagnostics = diags };
            }
            return new VideoMetadata(Diagnostics: diags);
        }
    }

    public VideoMetadata ExtractFromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            return new VideoMetadata(Diagnostics: [$"File does not exist: {filePath}"]);
        }

        return TryProbeDirectly(filePath) ?? new VideoMetadata(Diagnostics: ["Could not extract container metadata directly."]);
    }

    public VideoMetadata ExtractFromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) return VideoMetadata.Empty;

        byte[] buffer;
        if (stream is MemoryStream ms && ms.TryGetBuffer(out var segment))
        {
            buffer = segment.ToArray();
        }
        else
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            buffer = copy.ToArray();
        }

        return ExtractFromBytes(buffer);
    }

    public VideoMetadata ExtractFromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return VideoMetadata.Empty;
        return ParseMp4Boxes(bytes.ToArray()) ?? VideoMetadata.Empty;
    }

    private VideoMetadata? TryProbeDirectly(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var result = ExtractFromStream(stream);
            if (result.Width.HasValue || result.Duration.HasValue)
            {
                var fi = new FileInfo(filePath);
                return result with { FileSize = fi.Length };
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private (string? Path, string? Diagnostic) ResolveFfprobePath()
    {
        if (!string.IsNullOrWhiteSpace(_ffprobeExecutablePath))
        {

            return Path.IsPathFullyQualified(_ffprobeExecutablePath)
                ? (_ffprobeExecutablePath, null)
                : (null, $"The configured ffprobe path '{_ffprobeExecutablePath}' is not fully qualified and was not launched.");
        }

        var resolution = _toolResolver.Resolve(ExternalToolResolver.FfprobeToolId);
        return resolution.IsUsable
            ? (resolution.ResolvedPath, null)
            : (null, resolution.Reason);
    }

    public static VideoMetadata ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new VideoMetadata(Diagnostics: ["Empty probe output."]);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? container = null;
            long? fileSize = null;
            TimeSpan? duration = null;
            long? formatBitrate = null;

            string? make = null;
            string? model = null;
            string? software = null;
            string? encoder = null;
            DateTimeOffset? creationTime = null;
            DateTimeOffset? recordedTime = null;
            double? lat = null;
            double? lon = null;
            double? alt = null;
            string? locationName = null;

            var formatTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("format", out var formatEl) && formatEl.ValueKind == JsonValueKind.Object)
            {
                if (formatEl.TryGetProperty("format_name", out var fnEl) && fnEl.ValueKind == JsonValueKind.String)
                {
                    container = CleanContainerName(fnEl.GetString());
                }

                if (formatEl.TryGetProperty("size", out var sizeEl) && TryParseLong(sizeEl, out var sz))
                {
                    fileSize = sz;
                }

                if (formatEl.TryGetProperty("duration", out var durEl) && TryParseDouble(durEl, out var durSec) && durSec > 0)
                {
                    duration = TimeSpan.FromSeconds(durSec);
                }

                if (formatEl.TryGetProperty("bit_rate", out var brEl) && TryParseLong(brEl, out var br))
                {
                    formatBitrate = br;
                }

                if (formatEl.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in tagsEl.EnumerateObject())
                    {
                        var val = prop.Value.GetString();
                        if (val is null) continue;
                        formatTags[prop.Name] = val;

                        if (string.Equals(prop.Name, "creation_time", StringComparison.OrdinalIgnoreCase) && TryParseDateTime(val, out var ct))
                        {
                            creationTime = ct;
                        }
                        else if (string.Equals(prop.Name, "com.apple.quicktime.creationdate", StringComparison.OrdinalIgnoreCase) && TryParseDateTime(val, out var rt))
                        {
                            recordedTime = rt;
                        }
                        else if ((string.Equals(prop.Name, "date", StringComparison.OrdinalIgnoreCase) || string.Equals(prop.Name, "RECORDING_TIME", StringComparison.OrdinalIgnoreCase)) && TryParseDateTime(val, out var dt))
                        {
                            recordedTime ??= dt;
                        }
                        else if (string.Equals(prop.Name, "com.apple.quicktime.make", StringComparison.OrdinalIgnoreCase) || string.Equals(prop.Name, "make", StringComparison.OrdinalIgnoreCase))
                        {
                            make = val.Trim();
                        }
                        else if (string.Equals(prop.Name, "com.apple.quicktime.model", StringComparison.OrdinalIgnoreCase) || string.Equals(prop.Name, "model", StringComparison.OrdinalIgnoreCase))
                        {
                            model = val.Trim();
                        }
                        else if (string.Equals(prop.Name, "encoder", StringComparison.OrdinalIgnoreCase) || string.Equals(prop.Name, "com.apple.quicktime.encoder", StringComparison.OrdinalIgnoreCase))
                        {
                            encoder = val.Trim();
                        }
                        else if (string.Equals(prop.Name, "software", StringComparison.OrdinalIgnoreCase) || string.Equals(prop.Name, "com.apple.quicktime.software", StringComparison.OrdinalIgnoreCase))
                        {
                            software = val.Trim();
                        }
                        else if (string.Equals(prop.Name, "com.apple.quicktime.location.ISO6709", StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryParseIso6709(val, out var pLat, out var pLon, out var pAlt))
                            {
                                lat = pLat;
                                lon = pLon;
                                alt = pAlt;
                            }
                        }
                        else if (string.Equals(prop.Name, "location", StringComparison.OrdinalIgnoreCase) || string.Equals(prop.Name, "location-eng", StringComparison.OrdinalIgnoreCase))
                        {
                            locationName = val.Trim();
                        }
                    }
                }
            }

            int? width = null;
            int? height = null;
            string? videoCodec = null;
            string? videoCodecLong = null;
            string? videoProfile = null;
            double? frameRate = null;
            string? frameRateRational = null;
            long? videoBitrate = null;
            string? pixelFormat = null;
            string? colorPrimaries = null;
            string? colorTransfer = null;
            string? colorSpace = null;
            bool? isHdr = null;
            string? hdrType = null;
            int? rotation = null;

            string? audioCodec = null;
            string? audioCodecLong = null;
            int? audioSampleRate = null;
            int? audioChannels = null;
            string? audioChannelLayout = null;
            long? audioBitrate = null;

            var videoTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var audioTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("streams", out var streamsEl) && streamsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streamsEl.EnumerateArray())
                {
                    if (stream.ValueKind != JsonValueKind.Object) continue;
                    var codecType = stream.TryGetProperty("codec_type", out var ctProp) ? ctProp.GetString() : null;

                    if (string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase) && videoCodec is null)
                    {
                        videoCodec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                        videoCodecLong = stream.TryGetProperty("codec_long_name", out var cln) ? cln.GetString() : null;
                        videoProfile = stream.TryGetProperty("profile", out var prof) ? prof.GetString() : null;

                        if (stream.TryGetProperty("width", out var wEl) && TryParseInt(wEl, out var w) && w > 0)
                        {
                            width = w;
                        }

                        if (stream.TryGetProperty("height", out var hEl) && TryParseInt(hEl, out var h) && h > 0)
                        {
                            height = h;
                        }

                        string? fpsStr = null;
                        if (stream.TryGetProperty("avg_frame_rate", out var afr) && afr.GetString() is { } afrs && afrs != "0/0")
                        {
                            fpsStr = afrs;
                        }
                        else if (stream.TryGetProperty("r_frame_rate", out var rfr) && rfr.GetString() is { } rfrs && rfrs != "0/0")
                        {
                            fpsStr = rfrs;
                        }

                        if (fpsStr != null && TryParseRationalFps(fpsStr, out var parsedFps, out var parsedRat))
                        {
                            frameRate = parsedFps;
                            frameRateRational = parsedRat;
                        }

                        if (stream.TryGetProperty("bit_rate", out var vbrEl) && TryParseLong(vbrEl, out var vbr))
                        {
                            videoBitrate = vbr;
                        }

                        pixelFormat = stream.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() : null;
                        colorPrimaries = stream.TryGetProperty("color_primaries", out var cp) ? cp.GetString() : null;
                        colorTransfer = stream.TryGetProperty("color_transfer", out var ctrans) ? ctrans.GetString() : null;
                        colorSpace = stream.TryGetProperty("color_space", out var cs) ? cs.GetString() : null;

                        if (!string.IsNullOrWhiteSpace(colorTransfer))
                        {
                            if (colorTransfer.Contains("smpte2084", StringComparison.OrdinalIgnoreCase) || colorTransfer.Contains("pq", StringComparison.OrdinalIgnoreCase))
                            {
                                isHdr = true;
                                hdrType = "HDR10 (PQ)";
                            }
                            else if (colorTransfer.Contains("arib-std-b67", StringComparison.OrdinalIgnoreCase) || colorTransfer.Contains("hlg", StringComparison.OrdinalIgnoreCase))
                            {
                                isHdr = true;
                                hdrType = "HLG";
                            }
                            else if (colorTransfer.Contains("bt709", StringComparison.OrdinalIgnoreCase))
                            {
                                isHdr = false;
                            }
                        }

                        if (stream.TryGetProperty("tags", out var stagsEl) && stagsEl.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in stagsEl.EnumerateObject())
                            {
                                var val = prop.Value.GetString();
                                if (val is null) continue;
                                videoTags[prop.Name] = val;

                                if (string.Equals(prop.Name, "rotate", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var rot))
                                {
                                    rotation = NormalizeRotation(rot);
                                }
                                else if (string.Equals(prop.Name, "creation_time", StringComparison.OrdinalIgnoreCase) && !creationTime.HasValue && TryParseDateTime(val, out var sct))
                                {
                                    creationTime = sct;
                                }
                            }
                        }

                        if (!rotation.HasValue && stream.TryGetProperty("side_data_list", out var sideDataEl) && sideDataEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var sideData in sideDataEl.EnumerateArray())
                            {
                                if (sideData.TryGetProperty("rotation", out var sRot) && TryParseInt(sRot, out var rVal))
                                {
                                    rotation = NormalizeRotation(-rVal);
                                    break;
                                }
                            }
                        }

                        if (!duration.HasValue && stream.TryGetProperty("duration", out var sDur) && TryParseDouble(sDur, out var sDurSec) && sDurSec > 0)
                        {
                            duration = TimeSpan.FromSeconds(sDurSec);
                        }
                    }
                    else if (string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase) && audioCodec is null)
                    {
                        audioCodec = stream.TryGetProperty("codec_name", out var acn) ? acn.GetString() : null;
                        audioCodecLong = stream.TryGetProperty("codec_long_name", out var acln) ? acln.GetString() : null;

                        if (stream.TryGetProperty("sample_rate", out var srEl) && TryParseInt(srEl, out var sr) && sr > 0)
                        {
                            audioSampleRate = sr;
                        }

                        if (stream.TryGetProperty("channels", out var chEl) && TryParseInt(chEl, out var ch) && ch > 0)
                        {
                            audioChannels = ch;
                        }

                        audioChannelLayout = stream.TryGetProperty("channel_layout", out var cl) ? cl.GetString() : null;

                        if (stream.TryGetProperty("bit_rate", out var abrEl) && TryParseLong(abrEl, out var abr))
                        {
                            audioBitrate = abr;
                        }

                        if (stream.TryGetProperty("tags", out var atagsEl) && atagsEl.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in atagsEl.EnumerateObject())
                            {
                                var val = prop.Value.GetString();
                                if (val != null) audioTags[prop.Name] = val;
                            }
                        }
                    }
                }
            }

            var capturedAt = VideoMetadata.ResolveCaptureTime(recordedTime, creationTime);

            return new VideoMetadata(
                Container: container,
                FileSize: fileSize,
                Duration: duration,
                Width: width,
                Height: height,
                VideoCodec: videoCodec != null ? FormatVideoCodec(videoCodec) : null,
                VideoCodecLongName: videoCodecLong,
                VideoProfile: videoProfile,
                FrameRate: frameRate,
                FrameRateRational: frameRateRational,
                Bitrate: videoBitrate ?? formatBitrate,
                PixelFormat: pixelFormat,
                ColorPrimaries: colorPrimaries,
                ColorTransfer: colorTransfer,
                ColorSpace: colorSpace,
                IsHdr: isHdr,
                HdrType: hdrType,
                Rotation: rotation,
                AudioCodec: audioCodec != null ? FormatAudioCodec(audioCodec) : null,
                AudioCodecLongName: audioCodecLong,
                AudioSampleRate: audioSampleRate,
                AudioChannels: audioChannels,
                AudioChannelLayout: audioChannelLayout,
                AudioBitrate: audioBitrate,
                CreationTime: creationTime,
                RecordedTime: recordedTime,
                CapturedAt: capturedAt,
                Latitude: lat,
                Longitude: lon,
                AltitudeMeters: alt,
                LocationName: locationName,
                DeviceMake: make,
                DeviceModel: model,
                Software: software,
                Encoder: encoder,
                FormatTags: formatTags.Count > 0 ? formatTags : null,
                VideoStreamTags: videoTags.Count > 0 ? videoTags : null,
                AudioStreamTags: audioTags.Count > 0 ? audioTags : null);
        }
        catch (JsonException ex)
        {
            return new VideoMetadata(Diagnostics: [$"Malformed ffprobe JSON: {ex.Message}"]);
        }
        catch (Exception ex)
        {
            return new VideoMetadata(Diagnostics: [$"Unexpected probe parse failure: {ex.Message}"]);
        }
    }

    private static VideoMetadata? ParseMp4Boxes(byte[] buffer)
    {
        if (buffer.Length < 16) return null;

        var offset = 0;
        var isMp4 = false;
        string? brand = null;
        int? width = null;
        int? height = null;
        TimeSpan? duration = null;
        DateTimeOffset? creationTime = null;

        while (offset + 8 <= buffer.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(buffer, offset + 4, 4);

            if (size == 0) break;
            var boxLen = size == 1 && offset + 16 <= buffer.Length
                ? (int)BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(offset + 8, 8))
                : (int)size;

            if (boxLen < 8 || offset + boxLen > buffer.Length) break;

            if (type == "ftyp" && boxLen >= 12)
            {
                isMp4 = true;
                brand = Encoding.ASCII.GetString(buffer, offset + 8, 4).Trim();
            }
            else if (type == "moov")
            {

                var moovEnd = offset + boxLen;
                var subOffset = offset + 8;
                while (subOffset + 8 <= moovEnd)
                {
                    var subSize = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(subOffset, 4));
                    var subType = Encoding.ASCII.GetString(buffer, subOffset + 4, 4);
                    if (subSize < 8 || subOffset + subSize > moovEnd) break;

                    if (subType == "mvhd" && subSize >= 28)
                    {
                        var ver = buffer[subOffset + 8];
                        if (ver == 0 && subSize >= 28)
                        {
                            var createSec = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(subOffset + 12, 4));
                            if (createSec > 0)
                            {

                                var epoch = new DateTimeOffset(1904, 1, 1, 0, 0, 0, TimeSpan.Zero);
                                creationTime = epoch.AddSeconds(createSec);
                            }

                            var timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(subOffset + 20, 4));
                            var durUnits = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(subOffset + 24, 4));
                            if (timescale > 0 && durUnits > 0)
                            {
                                duration = TimeSpan.FromSeconds((double)durUnits / timescale);
                            }
                        }
                    }
                    else if (subType == "trak")
                    {

                        var trakEnd = subOffset + subSize;
                        var tOffset = subOffset + 8;
                        while (tOffset + 8 <= trakEnd)
                        {
                            var tSize = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(tOffset, 4));
                            var tType = Encoding.ASCII.GetString(buffer, tOffset + 4, 4);
                            if (tSize < 8 || tOffset + tSize > trakEnd) break;

                            if (tType == "tkhd" && tSize >= 84)
                            {
                                var ver = buffer[tOffset + 8];
                                var widthOffset = ver == 0 ? tOffset + 80 : tOffset + 92;
                                var heightOffset = ver == 0 ? tOffset + 84 : tOffset + 96;
                                if (heightOffset + 4 <= tOffset + tSize)
                                {
                                    var wFixed = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(widthOffset, 4));
                                    var hFixed = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(heightOffset, 4));
                                    var w = (int)(wFixed >> 16);
                                    var h = (int)(hFixed >> 16);
                                    if (w > 0 && h > 0 && width is null)
                                    {
                                        width = w;
                                        height = h;
                                    }
                                }
                            }
                            tOffset += tSize;
                        }
                    }

                    subOffset += subSize;
                }
            }

            offset += boxLen;
        }

        if (!isMp4 && width is null && duration is null)
        {
            return null;
        }

        return new VideoMetadata(
            Container: brand ?? "MP4",
            Duration: duration,
            Width: width,
            Height: height,
            CreationTime: creationTime,
            CapturedAt: creationTime);
    }

    private static string CleanContainerName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "VIDEO";
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return raw.ToUpperInvariant();

        if (parts.Contains("mp4", StringComparer.OrdinalIgnoreCase) || parts.Contains("mov", StringComparer.OrdinalIgnoreCase))
        {
            return "MP4 / QuickTime";
        }
        if (parts.Contains("matroska", StringComparer.OrdinalIgnoreCase) || parts.Contains("webm", StringComparer.OrdinalIgnoreCase))
        {
            return "Matroska / WebM";
        }
        return parts[0].ToUpperInvariant();
    }

    private static string FormatVideoCodec(string codec) =>
        codec.ToLowerInvariant() switch
        {
            "h264" or "avc1" => "H.264 / AVC",
            "hevc" or "h265" or "hev1" or "hvc1" => "HEVC / H.265",
            "vp9" => "VP9",
            "vp8" => "VP8",
            "av1" => "AV1",
            "prores" => "Apple ProRes",
            _ => codec.ToUpperInvariant()
        };

    private static string FormatAudioCodec(string codec) =>
        codec.ToLowerInvariant() switch
        {
            "aac" => "AAC",
            "mp3" => "MP3",
            "opus" => "Opus",
            "vorbis" => "Vorbis",
            "flac" => "FLAC",
            "pcm_s16le" or "pcm_s24le" => "PCM",
            _ => codec.ToUpperInvariant()
        };

    private static int NormalizeRotation(int deg)
    {
        var normalized = deg % 360;
        if (normalized < 0) normalized += 360;
        return normalized;
    }

    private static bool TryParseRationalFps(string rational, out double fps, out string formatted)
    {
        fps = 0;
        formatted = rational;
        var parts = rational.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var den)
            && den > 0 && num > 0)
        {
            fps = Math.Round(num / den, 2);
            return true;
        }

        if (double.TryParse(rational, NumberStyles.Any, CultureInfo.InvariantCulture, out var direct) && direct > 0)
        {
            fps = Math.Round(direct, 2);
            formatted = $"{fps}/1";
            return true;
        }

        return false;
    }

    private static bool TryParseIso6709(string iso, out double lat, out double lon, out double? alt)
    {
        lat = 0;
        lon = 0;
        alt = null;

        var m = Iso6709Regex.Match(iso.Trim());
        if (!m.Success) return false;

        if (double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedLat)
            && double.TryParse(m.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedLon))
        {
            lat = Math.Round(parsedLat, 6);
            lon = Math.Round(parsedLon, 6);

            if (m.Groups[3].Success && double.TryParse(m.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedAlt))
            {
                alt = Math.Round(parsedAlt, 2);
            }

            return true;
        }

        return false;
    }

    private static bool TryParseDateTime(string s, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
        {
            return true;
        }

        if (DateTimeOffset.TryParseExact(s, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out result))
        {
            return true;
        }

        if (DateTimeOffset.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out result))
        {
            return true;
        }

        return false;
    }

    private static bool TryParseDouble(JsonElement el, out double val)
    {
        val = 0;
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetDouble(out val);
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out val)) return true;
        return false;
    }

    private static bool TryParseLong(JsonElement el, out long val)
    {
        val = 0;
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt64(out val);
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out val)) return true;
        return false;
    }

    private static bool TryParseInt(JsonElement el, out int val)
    {
        val = 0;
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt32(out val);
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out val)) return true;
        return false;
    }

    public static string Serialize(VideoMetadata metadata) =>
        JsonSerializer.Serialize(metadata, JsonOptions);

    public static VideoMetadata Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return VideoMetadata.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<VideoMetadata>(json, JsonOptions) ?? VideoMetadata.Empty;
        }
        catch
        {
            return VideoMetadata.Empty;
        }
    }
}
