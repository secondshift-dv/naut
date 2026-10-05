using System.Globalization;
using Neuterradise.App.Media.Image;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Media.Video;

namespace Neuterradise.App.Media;

public static class MediaCluesBuilder
{
    public const string MissingValue = "\u2014";

    public static MediaClues Build(
        string? who = null,
        string? where = null,
        string? when = null,
        string? how = null)
    {
        return new MediaClues(
            Who: FormatValue(who),
            Where: FormatValue(where),
            When: FormatValue(when),
            How: FormatValue(how));
    }

    public static MediaClues BuildForImage(
        ImageMetadata? metadata,
        string? who = null,
        string? fallbackWhere = null)
    {
        var whoVal = FormatValue(who);

        string whereVal;
        if (!string.IsNullOrWhiteSpace(metadata?.LocationName))
        {
            whereVal = metadata.LocationName.Trim();
        }
        else if (metadata?.Latitude.HasValue == true && metadata?.Longitude.HasValue == true)
        {
            whereVal = FormatCoordinates(metadata.Latitude.Value, metadata.Longitude.Value);
        }
        else if (!string.IsNullOrWhiteSpace(fallbackWhere))
        {
            whereVal = fallbackWhere.Trim();
        }
        else
        {
            whereVal = MissingValue;
        }

        string whenVal;
        if (metadata?.CapturedAt.HasValue == true)
        {
            whenVal = metadata.CapturedAt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else
        {
            whenVal = MissingValue;
        }

        var howVal = BuildImageHowSummary(metadata);

        return Build(whoVal, whereVal, whenVal, howVal);
    }

    public static MediaClues BuildForVideo(
        VideoMetadata? metadata,
        string? who = null,
        string? fallbackWhere = null)
    {
        var whoVal = FormatValue(who);

        string whereVal;
        if (!string.IsNullOrWhiteSpace(metadata?.LocationName))
        {
            whereVal = metadata.LocationName.Trim();
        }
        else if (metadata?.Latitude.HasValue == true && metadata?.Longitude.HasValue == true)
        {
            whereVal = FormatCoordinates(metadata.Latitude.Value, metadata.Longitude.Value);
        }
        else if (!string.IsNullOrWhiteSpace(fallbackWhere))
        {
            whereVal = fallbackWhere.Trim();
        }
        else
        {
            whereVal = MissingValue;
        }

        string whenVal;
        if (metadata?.CapturedAt.HasValue == true)
        {
            whenVal = metadata.CapturedAt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else
        {
            whenVal = MissingValue;
        }

        var howVal = BuildVideoHowSummary(metadata);

        return Build(whoVal, whereVal, whenVal, howVal);
    }

    public static string BuildImageHowSummary(ImageMetadata? metadata)
    {
        if (metadata is null) return MissingValue;

        var parts = new List<string>();

        string? device = null;
        if (!string.IsNullOrWhiteSpace(metadata.CameraModel))
        {
            if (!string.IsNullOrWhiteSpace(metadata.CameraMake) &&
                !metadata.CameraModel.StartsWith(metadata.CameraMake, StringComparison.OrdinalIgnoreCase))
            {
                device = $"{metadata.CameraMake.Trim()} {metadata.CameraModel.Trim()}";
            }
            else
            {
                device = metadata.CameraModel.Trim();
            }
        }
        else if (!string.IsNullOrWhiteSpace(metadata.CameraMake))
        {
            device = metadata.CameraMake.Trim();
        }

        if (!string.IsNullOrWhiteSpace(device))
        {
            parts.Add(device);
        }

        if (metadata.FocalLengthMm.HasValue && metadata.FocalLengthMm.Value > 0)
        {
            parts.Add($"{metadata.FocalLengthMm.Value.ToString("0.#", CultureInfo.InvariantCulture)} mm");
        }

        if (metadata.FNumber.HasValue && metadata.FNumber.Value > 0)
        {
            parts.Add($"f/{metadata.FNumber.Value.ToString("0.#", CultureInfo.InvariantCulture)}");
        }

        if (parts.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(metadata.ExposureTimeString))
            {
                parts.Add(metadata.ExposureTimeString);
            }

            if (metadata.Iso.HasValue && metadata.Iso.Value > 0)
            {
                parts.Add($"ISO {metadata.Iso.Value}");
            }
        }

        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(metadata.Software))
        {
            parts.Add(metadata.Software.Trim());
        }

        return parts.Count > 0 ? string.Join(" \u00B7 ", parts) : MissingValue;
    }

    public static string BuildVideoHowSummary(VideoMetadata? metadata)
    {
        if (metadata is null) return MissingValue;

        var parts = new List<string>();

        string? device = null;
        if (!string.IsNullOrWhiteSpace(metadata.DeviceModel))
        {
            device = metadata.DeviceModel.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(metadata.DeviceMake))
        {
            device = metadata.DeviceMake.Trim();
        }

        if (!string.IsNullOrWhiteSpace(device))
        {
            parts.Add(device);
        }

        if (metadata.Width.HasValue && metadata.Height.HasValue && metadata.Width.Value > 0 && metadata.Height.Value > 0)
        {
            parts.Add($"{metadata.Width.Value}\u00D7{metadata.Height.Value}");
        }

        if (metadata.FrameRate.HasValue && metadata.FrameRate.Value > 0)
        {
            parts.Add($"{Math.Round(metadata.FrameRate.Value):0} fps");
        }

        if (!string.IsNullOrWhiteSpace(metadata.VideoCodec))
        {
            parts.Add(metadata.VideoCodec.Trim());
        }

        return parts.Count > 0 ? string.Join(" \u00B7 ", parts) : MissingValue;
    }

    public static MediaClues BuildForModel(
        ModelMetadata? metadata,
        string? who = null,
        string? fallbackWhere = null,
        string? fallbackFormat = null,
        long? fileSizeBytes = null)
    {
        var whoVal = FormatValue(who);
        var whereVal = !string.IsNullOrWhiteSpace(fallbackWhere) ? fallbackWhere.Trim() : MissingValue;
        var whenVal = MissingValue;
        var howVal = BuildModelHowSummary(metadata, fallbackFormat, fileSizeBytes);

        return new MediaClues(whoVal, whereVal, whenVal, howVal);
    }

    public static string BuildModelHowSummary(
        ModelMetadata? metadata,
        string? fallbackFormat = null,
        long? fileSizeBytes = null)
    {
        if (metadata is null)
        {
            var parts = new List<string> { "3D Model" };
            if (!string.IsNullOrWhiteSpace(fallbackFormat)) parts.Add(fallbackFormat.ToUpperInvariant());
            if (fileSizeBytes.HasValue && fileSizeBytes.Value > 0) parts.Add(FormatByteLength(fileSizeBytes.Value));
            return parts.Count > 1 ? string.Join(" \u00B7 ", parts) : MissingValue;
        }

        var tokens = new List<string>();
        var format = metadata.Format ?? fallbackFormat;
        if (!string.IsNullOrWhiteSpace(format))
        {
            tokens.Add(format.ToUpperInvariant());
        }
        else
        {
            tokens.Add("3D Model");
        }

        if (metadata.MeshCount.HasValue && metadata.MeshCount.Value > 0)
        {
            tokens.Add(metadata.MeshCount.Value == 1 ? "1 Mesh" : $"{metadata.MeshCount.Value} Meshes");
        }

        if (metadata.TriangleCount.HasValue && metadata.TriangleCount.Value > 0)
        {
            tokens.Add(metadata.TriangleCount.Value.ToString("N0", CultureInfo.InvariantCulture) + " Triangles");
        }
        else if (metadata.VertexCount.HasValue && metadata.VertexCount.Value > 0)
        {
            tokens.Add(metadata.VertexCount.Value.ToString("N0", CultureInfo.InvariantCulture) + " Vertices");
        }

        if (metadata.MaterialCount.HasValue && metadata.MaterialCount.Value > 0)
        {
            tokens.Add(metadata.MaterialCount.Value == 1 ? "1 Material" : $"{metadata.MaterialCount.Value} Materials");
        }

        if (tokens.Count == 1 && fileSizeBytes.HasValue && fileSizeBytes.Value > 0)
        {
            tokens.Add(FormatByteLength(fileSizeBytes.Value));
        }

        return tokens.Count > 0 ? string.Join(" \u00B7 ", tokens) : MissingValue;
    }

    private static string FormatByteLength(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.#} KB", bytes / 1024.0),
            < 1024 * 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.#} MB", bytes / (1024.0 * 1024.0)),
            _ => string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB", bytes / (1024.0 * 1024.0 * 1024.0))
        };
    }

    public static string FormatCoordinates(double latitude, double longitude)
    {
        var latDir = latitude >= 0 ? "N" : "S";
        var lonDir = longitude >= 0 ? "E" : "W";
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:F4}\u00B0 {1}, {2:F4}\u00B0 {3}",
            Math.Abs(latitude),
            latDir,
            Math.Abs(longitude),
            lonDir);
    }

    private static string FormatValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? MissingValue : value.Trim();
}
