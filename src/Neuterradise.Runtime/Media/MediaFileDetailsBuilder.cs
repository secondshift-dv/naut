using System.Globalization;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Media;

public static class MediaFileDetailsBuilder
{
    public static IReadOnlyList<MediaFileDetailGroup> BuildGroups(
        MediaDetailReadModel model,
        int? width = null,
        int? height = null,
        int? durationMs = null,
        DateTimeOffset? createdAt = null,
        Image.ImageMetadata? imageMetadata = null,
        Video.VideoMetadata? videoMetadata = null,
        Model.ModelMetadata? modelMetadata = null,
        IReadOnlyDictionary<string, string>? rawMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        var groups = new List<MediaFileDetailGroup>();

        var sourceRows = new List<MediaFileDetailRow>();
        if (!string.IsNullOrWhiteSpace(model.ImportSourcePath))
        {
            sourceRows.Add(new("Import source path", model.ImportSourcePath));
        }
        if (!string.IsNullOrWhiteSpace(model.ImportSourceName))
        {
            sourceRows.Add(new("Import source name", model.ImportSourceName));
        }
        if (sourceRows.Count > 0)
        {
            groups.Add(new MediaFileDetailGroup("Source Evidence", sourceRows));
        }

        var formatStr = model.MediaType switch
        {
            MediaType.Image => imageMetadata?.Format ?? model.MediaType.ToString().ToUpperInvariant(),
            MediaType.Video => videoMetadata?.Container ?? model.MediaType.ToString().ToUpperInvariant(),
            MediaType.Model => modelMetadata?.Format ?? model.MediaType.ToString().ToUpperInvariant(),
            _ => model.MediaType.ToString().ToUpperInvariant()
        };

        var fileRows = new List<MediaFileDetailRow>
        {
            new("File Name", model.FileName),
            new("Original Name", model.OriginalName),
            new("Format", formatStr),
            new("File Size", FormatByteLength(model.ByteLength)),
        };
        groups.Add(new MediaFileDetailGroup("File", fileRows));

        var effWidth = width ?? imageMetadata?.Width ?? videoMetadata?.Width;
        var effHeight = height ?? imageMetadata?.Height ?? videoMetadata?.Height;
        var effDuration = durationMs.HasValue
            ? TimeSpan.FromMilliseconds(durationMs.Value)
            : videoMetadata?.Duration;

        if (model.MediaType == MediaType.Image)
        {

            var imgRows = new List<MediaFileDetailRow>();
            if (effWidth.HasValue && effHeight.HasValue && effWidth > 0 && effHeight > 0)
            {
                imgRows.Add(new("Dimensions", $"{effWidth.Value} \u00D7 {effHeight.Value}"));
                imgRows.Add(new("Aspect Ratio", FormatAspectRatio(effWidth.Value, effHeight.Value)));
            }
            imgRows.Add(new("Orientation", imageMetadata?.Orientation ?? (imageMetadata?.OrientationDegrees.HasValue == true ? $"{imageMetadata.OrientationDegrees.Value}\u00B0" : "\u2014")));
            imgRows.Add(new("Bit Depth", imageMetadata?.BitDepth.HasValue == true ? $"{imageMetadata.BitDepth.Value} bpp" : "\u2014"));
            groups.Add(new MediaFileDetailGroup("Image", imgRows));

            var colorRows = new List<MediaFileDetailRow>
            {
                new("Color Space", imageMetadata?.ColorSpace ?? "\u2014"),
                new("Color Profile", imageMetadata?.ColorProfile ?? "\u2014")
            };
            groups.Add(new MediaFileDetailGroup("Color", colorRows));

            var camRows = new List<MediaFileDetailRow>
            {
                new("Camera Make", imageMetadata?.CameraMake ?? "\u2014"),
                new("Camera Model", imageMetadata?.CameraModel ?? "\u2014"),
                new("Lens", imageMetadata?.LensModel ?? "\u2014"),
                new("Focal Length", imageMetadata?.FocalLengthMm.HasValue == true ? $"{imageMetadata.FocalLengthMm.Value.ToString("0.#", CultureInfo.InvariantCulture)} mm" : "\u2014"),
                new("Aperture", imageMetadata?.FNumber.HasValue == true ? $"f/{imageMetadata.FNumber.Value.ToString("0.#", CultureInfo.InvariantCulture)}" : "\u2014"),
                new("Shutter Speed", imageMetadata?.ExposureTimeString ?? (imageMetadata?.ExposureTimeSeconds.HasValue == true ? $"{imageMetadata.ExposureTimeSeconds.Value.ToString("0.######", CultureInfo.InvariantCulture)} s" : "\u2014")),
                new("ISO", imageMetadata?.Iso.HasValue == true ? $"{imageMetadata.Iso.Value}" : "\u2014"),
                new("Exposure Program", imageMetadata?.ExposureProgram ?? "\u2014"),
                new("Exposure Bias", imageMetadata?.ExposureBiasEv.HasValue == true ? $"{imageMetadata.ExposureBiasEv.Value.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)} EV" : "\u2014"),
                new("Flash", imageMetadata?.Flash ?? "\u2014")
            };
            groups.Add(new MediaFileDetailGroup("Camera", camRows));

            var captureLocationRows = new List<MediaFileDetailRow>();
            if (!string.IsNullOrWhiteSpace(imageMetadata?.LocationName))
                captureLocationRows.Add(new("Location", imageMetadata.LocationName));
            if (imageMetadata?.Latitude is { } latitude && imageMetadata.Longitude is { } longitude)
                captureLocationRows.Add(new("GPS", FormattableString.Invariant($"{latitude:F6}, {longitude:F6}")));
            if (captureLocationRows.Count > 0)
                groups.Add(new MediaFileDetailGroup("Capture Location", captureLocationRows));
            if (!string.IsNullOrWhiteSpace(imageMetadata?.Software))
                groups.Add(new MediaFileDetailGroup("Metadata", [new("Software", imageMetadata.Software)]));

            var dateRows = new List<MediaFileDetailRow>
            {
                new("Captured", imageMetadata?.CapturedAt.HasValue == true ? imageMetadata.CapturedAt.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "\u2014"),
                new("Created", createdAt.HasValue ? createdAt.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "\u2014"),
                new("Added to Vault", model.AddedToLibraryAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))
            };
            groups.Add(new MediaFileDetailGroup("Dates", dateRows));
        }
        else if (model.MediaType == MediaType.Video)
        {

            var vidRows = new List<MediaFileDetailRow>();
            if (effWidth.HasValue && effHeight.HasValue && effWidth > 0 && effHeight > 0)
            {
                vidRows.Add(new("Dimensions", $"{effWidth.Value} \u00D7 {effHeight.Value}"));
                vidRows.Add(new("Aspect Ratio", FormatAspectRatio(effWidth.Value, effHeight.Value)));
            }

            if (effDuration.HasValue && effDuration.Value > TimeSpan.Zero)
            {
                vidRows.Add(new("Duration", effDuration.Value.TotalHours >= 1
                    ? effDuration.Value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                    : effDuration.Value.ToString(@"mm\:ss", CultureInfo.InvariantCulture)));
            }

            vidRows.Add(new("Video Codec", videoMetadata?.VideoCodecLongName ?? videoMetadata?.VideoCodec ?? "\u2014"));
            vidRows.Add(new("Profile", videoMetadata?.VideoProfile ?? "\u2014"));
            vidRows.Add(new("Frame Rate", videoMetadata?.FrameRate.HasValue == true
                ? (videoMetadata.FrameRateRational != null ? $"{videoMetadata.FrameRate.Value.ToString("0.##", CultureInfo.InvariantCulture)} fps ({videoMetadata.FrameRateRational})" : $"{videoMetadata.FrameRate.Value.ToString("0.##", CultureInfo.InvariantCulture)} fps")
                : "\u2014"));
            vidRows.Add(new("Bitrate", videoMetadata?.Bitrate.HasValue == true ? FormatBitrate(videoMetadata.Bitrate.Value) : "\u2014"));
            vidRows.Add(new("Pixel Format", videoMetadata?.PixelFormat ?? "\u2014"));
            groups.Add(new MediaFileDetailGroup("Video", vidRows));

            var audRows = new List<MediaFileDetailRow>
            {
                new("Audio Codec", videoMetadata?.AudioCodecLongName ?? videoMetadata?.AudioCodec ?? "\u2014"),
                new("Channels", videoMetadata?.AudioChannels.HasValue == true
                    ? (videoMetadata.AudioChannelLayout != null ? $"{videoMetadata.AudioChannels.Value} ({videoMetadata.AudioChannelLayout})" : $"{videoMetadata.AudioChannels.Value}")
                    : "\u2014"),
                new("Sample Rate", videoMetadata?.AudioSampleRate.HasValue == true ? $"{videoMetadata.AudioSampleRate.Value.ToString("N0", CultureInfo.InvariantCulture)} Hz" : "\u2014"),
                new("Audio Bitrate", videoMetadata?.AudioBitrate.HasValue == true ? FormatBitrate(videoMetadata.AudioBitrate.Value) : "\u2014")
            };
            groups.Add(new MediaFileDetailGroup("Audio", audRows));

            var colorRows = new List<MediaFileDetailRow>
            {
                new("Color Primaries", videoMetadata?.ColorPrimaries ?? "\u2014"),
                new("Color Transfer", videoMetadata?.ColorTransfer ?? "\u2014"),
                new("Color Space", videoMetadata?.ColorSpace ?? "\u2014"),
                new("HDR", videoMetadata?.IsHdr.HasValue == true
                    ? (videoMetadata.IsHdr.Value ? (videoMetadata.HdrType ?? "Yes") : "No (SDR)")
                    : "\u2014")
            };
            groups.Add(new MediaFileDetailGroup("Color", colorRows));

            var metaRows = new List<MediaFileDetailRow>
            {
                new("Container", videoMetadata?.Container ?? "\u2014"),
                new("Rotation", videoMetadata?.Rotation.HasValue == true ? $"{videoMetadata.Rotation.Value}\u00B0" : "\u2014"),
                new("Device Make", videoMetadata?.DeviceMake ?? "\u2014"),
                new("Device Model", videoMetadata?.DeviceModel ?? "\u2014"),
                new("Software", videoMetadata?.Software ?? "\u2014"),
                new("Encoder", videoMetadata?.Encoder ?? "\u2014"),
                new("Location", !string.IsNullOrWhiteSpace(videoMetadata?.LocationName)
                    ? videoMetadata.LocationName
                    : (videoMetadata?.Latitude.HasValue == true && videoMetadata?.Longitude.HasValue == true
                        ? $"{videoMetadata.Latitude.Value:F4}, {videoMetadata.Longitude.Value:F4}"
                        : "\u2014"))
            };
            groups.Add(new MediaFileDetailGroup("Metadata", metaRows));

            var dateRows = new List<MediaFileDetailRow>
            {
                new("Recorded", videoMetadata?.CapturedAt.HasValue == true ? videoMetadata.CapturedAt.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "\u2014"),
                new("Created", createdAt.HasValue ? createdAt.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "\u2014"),
                new("Added to Vault", model.AddedToLibraryAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))
            };
            groups.Add(new MediaFileDetailGroup("Dates", dateRows));
        }
        else if (model.MediaType == MediaType.Model)
        {

            var modelRows = new List<MediaFileDetailRow>
            {
                new("Format", modelMetadata?.Format ?? model.MediaType.ToString().ToUpperInvariant()),
                new("Meshes", modelMetadata?.MeshCount.HasValue == true ? modelMetadata.MeshCount.Value.ToString() : "\u2014"),
                new("Vertices", modelMetadata?.VertexCount.HasValue == true ? modelMetadata.VertexCount.Value.ToString("N0", CultureInfo.InvariantCulture) : "\u2014"),
                new("Triangles", modelMetadata?.TriangleCount.HasValue == true ? modelMetadata.TriangleCount.Value.ToString("N0", CultureInfo.InvariantCulture) : "\u2014"),
                new("Materials", modelMetadata?.MaterialCount.HasValue == true ? modelMetadata.MaterialCount.Value.ToString() : "\u2014"),
                new("Textures", modelMetadata?.TextureCount.HasValue == true ? modelMetadata.TextureCount.Value.ToString() : "\u2014"),
                new("Animations", modelMetadata?.AnimationCount.HasValue == true ? modelMetadata.AnimationCount.Value.ToString() : "\u2014"),
                new("Cameras", modelMetadata?.CameraCount.HasValue == true ? modelMetadata.CameraCount.Value.ToString() : "\u2014"),
                new("Lights", modelMetadata?.LightCount.HasValue == true ? modelMetadata.LightCount.Value.ToString() : "\u2014"),
                new("Bounds", modelMetadata?.Bounds ?? "\u2014"),
                new("Units", modelMetadata?.Units ?? "\u2014"),
                new("Adapter", modelMetadata?.AdapterId ?? "\u2014"),
                new("Adapter Version", modelMetadata?.AdapterVersion ?? "\u2014"),
                new("Adapter Status", modelMetadata?.Status.ToString() ?? "\u2014")
            };

            if (model.DependencyStatus != MediaDependencyStatus.SelfContained)
            {
                modelRows.Add(new("Dependency Status", model.DependencyStatus.ToString()));
            }

            if (!string.IsNullOrWhiteSpace(model.BundleSha256))
            {
                modelRows.Add(new("Bundle SHA256", model.BundleSha256));
            }

            groups.Add(new MediaFileDetailGroup("3D Model", modelRows));

            var dateRows = new List<MediaFileDetailRow>
            {
                new("Created", createdAt.HasValue ? createdAt.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "\u2014"),
                new("Added to Vault", model.AddedToLibraryAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))
            };
            groups.Add(new MediaFileDetailGroup("Dates", dateRows));
        }
        else
        {

            var specRows = new List<MediaFileDetailRow>();
            if (effWidth.HasValue && effHeight.HasValue && effWidth > 0 && effHeight > 0)
            {
                specRows.Add(new("Dimensions", $"{effWidth.Value} \u00D7 {effHeight.Value}"));
            }

            if (durationMs.HasValue && durationMs > 0)
            {
                var timeSpan = TimeSpan.FromMilliseconds(durationMs.Value);
                specRows.Add(new("Duration", timeSpan.TotalHours >= 1
                    ? timeSpan.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                    : timeSpan.ToString(@"mm\:ss", CultureInfo.InvariantCulture)));
            }

            if (specRows.Count > 0)
            {
                groups.Add(new MediaFileDetailGroup("Details", specRows));
            }
        }

        var locationRows = new List<MediaFileDetailRow>
        {
            new("Vault Location (current)", string.IsNullOrWhiteSpace(model.CurrentLibraryLocation) ? "\u2014" : model.CurrentLibraryLocation),
        };

        if (model.ReconciliationState != ManagedPathState.None || !string.IsNullOrWhiteSpace(model.TargetLibraryLocation))
        {
            locationRows.Add(new("Pending Target Location", model.TargetLibraryLocation ?? "Pending reconciliation"));
            locationRows.Add(new("Reconciliation State", model.ReconciliationState.ToString()));
        }

        locationRows.Add(new("Status", model.Status.ToString()));
        locationRows.Add(new("Added to Vault", model.AddedToLibraryAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)));

        if (createdAt.HasValue)
        {
            locationRows.Add(new("Created", createdAt.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)));
        }

        groups.Add(new MediaFileDetailGroup("Vault Location", locationRows));

        var systemRows = new List<MediaFileDetailRow>
        {
            new("File Fingerprint", string.IsNullOrWhiteSpace(model.FileFingerprint) ? "\u2014" : model.FileFingerprint),
            new("Media ID", model.MediaId.ToString("D")),
        };
        groups.Add(new MediaFileDetailGroup("System Details", systemRows));

        var rawDict = rawMetadata ?? imageMetadata?.RawTags ?? modelMetadata?.RawProperties;
        IEnumerable<KeyValuePair<string, string>> rawEntries = rawDict is null ? [] : rawDict;
        if (rawDict is null && videoMetadata is not null)
        {
            rawEntries = (videoMetadata.FormatTags?.AsEnumerable() ?? [])
                .Concat(videoMetadata.VideoStreamTags?.AsEnumerable() ?? [])
                .Concat(videoMetadata.AudioStreamTags?.AsEnumerable() ?? []);
        }
        var rawRows = rawEntries
            .DistinctBy(pair => $"{pair.Key}\u001f{pair.Value}", StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Select(pair => new MediaFileDetailRow(pair.Key, pair.Value))
            .ToList();
        if (rawRows.Count > 0)
        {
            groups.Add(new MediaFileDetailGroup("Raw Metadata", rawRows));
        }

        return groups;
    }

    private static string FormatAspectRatio(int width, int height)
    {
        if (width <= 0 || height <= 0) return "\u2014";
        var gcd = Gcd(width, height);
        var rw = width / gcd;
        var rh = height / gcd;

        if (rw <= 32 && rh <= 32)
        {
            return $"{rw}:{rh}";
        }

        var ratio = (double)width / height;
        return $"{ratio:F2}:1";
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            var t = b;
            b = a % b;
            a = t;
        }
        return a;
    }

    public static string FormatBitrate(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "0 bps";
        if (bitsPerSecond >= 1_000_000)
        {
            return $"{bitsPerSecond / 1_000_000.0:0.#} Mbps";
        }
        if (bitsPerSecond >= 1_000)
        {
            return $"{bitsPerSecond / 1_000.0:0} kbps";
        }
        return $"{bitsPerSecond} bps";
    }

    public static string FormatByteLength(long bytes)
    {
        if (bytes <= 0) return "0 B";

        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int suffixIndex = 0;
        double displaySize = bytes;

        while (displaySize >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            displaySize /= 1024;
            suffixIndex++;
        }

        return suffixIndex == 0
            ? $"{bytes:N0} B"
            : $"{displaySize:N1} {suffixes[suffixIndex]} ({bytes:N0} bytes)";
    }
}
