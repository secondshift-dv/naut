using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace Neuterradise.App.Media.Image;

public sealed record ImageMetadata(
    int? Width = null,
    int? Height = null,
    string? Format = null,
    string? Orientation = null,
    int? OrientationDegrees = null,
    string? ColorSpace = null,
    string? ColorProfile = null,
    int? BitDepth = null,
    string? CameraMake = null,
    string? CameraModel = null,
    string? LensModel = null,
    double? FocalLengthMm = null,
    double? FNumber = null,
    string? ExposureTimeString = null,
    double? ExposureTimeSeconds = null,
    int? Iso = null,
    string? ExposureProgram = null,
    double? ExposureBiasEv = null,
    string? Flash = null,
    DateTimeOffset? DateTimeOriginal = null,
    DateTimeOffset? DateTimeDigitized = null,
    DateTimeOffset? XmpCaptureTime = null,
    DateTimeOffset? EmbeddedCreationTime = null,
    DateTimeOffset? CapturedAt = null,
    double? Latitude = null,
    double? Longitude = null,
    double? AltitudeMeters = null,
    string? LocationName = null,
    string? Software = null,
    IReadOnlyDictionary<string, string>? RawTags = null,
    IReadOnlyList<string>? Diagnostics = null)
{
    public static ImageMetadata Empty { get; } = new();

    public static DateTimeOffset? ResolveCaptureTime(
        DateTimeOffset? dateTimeOriginal,
        DateTimeOffset? xmpCaptureTime,
        DateTimeOffset? embeddedCreationTime)
    {
        if (dateTimeOriginal.HasValue) return dateTimeOriginal.Value;
        if (xmpCaptureTime.HasValue) return xmpCaptureTime.Value;
        if (embeddedCreationTime.HasValue) return embeddedCreationTime.Value;
        return null;
    }
}

public sealed class ImageMetadataAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public ImageMetadata ExtractFromFile(string filePath, string? formatHint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            return new ImageMetadata(Diagnostics: [$"File does not exist: {filePath}"]);
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var extension = (formatHint ?? Path.GetExtension(filePath)).TrimStart('.').ToUpperInvariant();
            return ExtractFromStream(stream, extension);
        }
        catch (Exception ex)
        {
            return new ImageMetadata(Diagnostics: [$"Failed to read image file: {ex.Message}"]);
        }
    }

    public ImageMetadata ExtractFromBytes(ReadOnlySpan<byte> bytes, string? hintFormat = null)
    {
        if (bytes.IsEmpty)
        {
            return ImageMetadata.Empty;
        }

        using var memoryStream = new MemoryStream(bytes.ToArray());
        return ExtractFromStream(memoryStream, hintFormat);
    }

    public ImageMetadata ExtractFromStream(Stream stream, string? hintFormat = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            return ImageMetadata.Empty;
        }

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

        if (buffer.Length == 0)
        {
            return ImageMetadata.Empty;
        }

        var wpfResult = TryExtractViaCodec(buffer, hintFormat);

        var binaryResult = TryExtractViaBinary(buffer, hintFormat);

        return MergeResults(wpfResult, binaryResult, hintFormat);
    }

    /// <summary>
    /// Dimensions, format and pixel depth from the image codec. Camera/lens/time/GPS facts come from the
    /// binary EXIF/XMP parser, which reads the metadata segments directly and is merged over this.
    /// </summary>
    private static ImageMetadata? TryExtractViaCodec(byte[] buffer, string? hintFormat)
    {
        try
        {
            using var data = SkiaSharp.SKData.CreateCopy(buffer);
            using var codec = SkiaSharp.SKCodec.Create(data);
            if (codec is null)
            {
                return null;
            }

            var info = codec.Info;
            var (orientation, degrees) = codec.EncodedOrigin switch
            {
                SkiaSharp.SKEncodedOrigin.RightTop => ("Rotate 90 CW", 90),
                SkiaSharp.SKEncodedOrigin.BottomRight => ("Rotate 180", 180),
                SkiaSharp.SKEncodedOrigin.LeftBottom => ("Rotate 270 CW", 270),
                SkiaSharp.SKEncodedOrigin.TopRight => ("Mirror horizontal", 0),
                SkiaSharp.SKEncodedOrigin.BottomLeft => ("Mirror vertical", 0),
                SkiaSharp.SKEncodedOrigin.LeftTop => ("Mirror horizontal and rotate 270 CW", 270),
                SkiaSharp.SKEncodedOrigin.RightBottom => ("Mirror horizontal and rotate 90 CW", 90),
                _ => ((string?)null, (int?)null),
            };

            return new ImageMetadata(
                Width: info.Width,
                Height: info.Height,
                Format: hintFormat ?? codec.EncodedFormat.ToString().ToUpperInvariant(),
                Orientation: orientation,
                OrientationDegrees: degrees,
                ColorSpace: info.ColorSpace is { IsSrgb: true } ? "sRGB" : info.ColorType.ToString(),
                BitDepth: info.BitsPerPixel);
        }
        catch
        {
            return null;
        }
    }

    private static ImageMetadata? TryExtractViaBinary(byte[] buffer, string? hintFormat)
    {
        try
        {
            if (buffer.Length < 8) return null;

            if (buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47)
            {
                return ParsePngBinary(buffer);
            }

            if (buffer[0] == 0xFF && buffer[1] == 0xD8)
            {
                return ParseJpegBinary(buffer);
            }

            if (buffer.Length >= 12 && buffer.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && buffer.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            {
                ImageMetadata? metadata = null;
                var offset = 12;
                while (offset <= buffer.Length - 8)
                {
                    var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4, 4));
                    if (length > (uint)(buffer.Length - offset - 8)) break;
                    var dataOffset = offset + 8;
                    var dataLength = (int)length;
                    if (buffer.AsSpan(offset, 4).SequenceEqual("EXIF"u8))
                    {
                        if (buffer.AsSpan(dataOffset, dataLength).StartsWith("Exif\0\0"u8))
                        {
                            dataOffset += 6;
                            dataLength -= 6;
                        }
                        metadata = MergeResults(metadata, ParseTiffBinary(buffer, dataOffset, dataLength), "WEBP");
                    }
                    else if (buffer.AsSpan(offset, 4).SequenceEqual("XMP "u8))
                        metadata = MergeResults(metadata, ParseXmp(buffer.AsSpan(dataOffset, dataLength)), "WEBP");
                    var next = (long)offset + 8 + length + (length & 1);
                    if (next > buffer.Length) break;
                    offset = (int)next;
                }
                return metadata;
            }

            if ((buffer[0] == 0x49 && buffer[1] == 0x49) || (buffer[0] == 0x4D && buffer[1] == 0x4D))
            {
                return ParseTiffBinary(buffer, 0, buffer.Length);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static ImageMetadata ParsePngBinary(byte[] buffer)
    {
        ImageMetadata? exif = null;
        int? width = null;
        int? height = null;
        int? bitDepth = null;
        string? colorSpace = null;
        var rawTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var offset = 8;
        while (offset + 8 <= buffer.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset, 4));
            if (length < 0 || offset + 12 + length > buffer.Length) break;

            var chunkType = Encoding.ASCII.GetString(buffer, offset + 4, 4);
            if (chunkType == "IHDR" && length >= 13)
            {
                width = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset + 8, 4));
                height = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset + 12, 4));
                bitDepth = buffer[offset + 16];
                var colorType = buffer[offset + 17];
                colorSpace = colorType switch
                {
                    0 => "Grayscale",
                    2 => "RGB",
                    3 => "Indexed",
                    4 => "Grayscale + Alpha",
                    6 => "RGBA",
                    _ => "PNG"
                };
            }
            else if (chunkType == "eXIf")
            {
                exif = ParseTiffBinary(buffer, offset + 8, length);
            }
            else if (chunkType is "tEXt" or "iTXt" or "zTXt")
            {
                var chunkBytes = buffer.AsSpan(offset + 8, length);
                var nullIdx = chunkBytes.IndexOf((byte)0);
                if (nullIdx > 0)
                {
                    var key = Encoding.ASCII.GetString(chunkBytes[..nullIdx]);
                    if (chunkType == "tEXt" && nullIdx + 1 < chunkBytes.Length)
                    {
                        var val = Encoding.Latin1.GetString(chunkBytes[(nullIdx + 1)..]);
                        rawTags[$"PNG:{key}"] = val;
                    }
                }
            }
            else if (chunkType == "IEND")
            {
                break;
            }

            offset += 12 + length;
        }

        return MergeResults(exif, new ImageMetadata(
            Width: width,
            Height: height,
            Format: "PNG",
            BitDepth: bitDepth,
            ColorSpace: colorSpace,
            RawTags: rawTags.Count > 0 ? rawTags : null), "PNG");
    }

    private static ImageMetadata ParseJpegBinary(byte[] buffer)
    {
        int? width = null;
        int? height = null;
        ImageMetadata? exifMeta = null;
        var rawTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var offset = 2;
        while (offset + 4 <= buffer.Length)
        {
            if (buffer[offset] != 0xFF) break;
            var marker = buffer[offset + 1];
            if (marker is 0xD9 or 0xDA) break;

            var length = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 2, 2));
            if (offset + 2 + length > buffer.Length) break;

            if (marker is 0xC0 or 0xC1 or 0xC2 && length >= 7)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 5, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 7, 2));
            }

            else if (marker == 0xE1 && length >= 8)
            {
                var app1Header = Encoding.ASCII.GetString(buffer, offset + 4, 6);
                if (app1Header.StartsWith("Exif\0\0", StringComparison.Ordinal))
                {
                    var tiffStart = offset + 10;
                    var tiffLength = length - 8;
                    exifMeta = MergeResults(exifMeta, ParseTiffBinary(buffer, tiffStart, tiffLength), "JPEG");
                }
                else if (buffer.AsSpan(offset + 4, length - 2).StartsWith("http://ns.adobe.com/xap/1.0/\0"u8))
                {
                    var headerLength = "http://ns.adobe.com/xap/1.0/\0"u8.Length;
                    var xmp = ParseXmp(buffer.AsSpan(offset + 4 + headerLength, length - 2 - headerLength));
                    exifMeta = MergeResults(exifMeta, xmp, "JPEG");
                }
            }

            offset += 2 + length;
        }

        if (exifMeta is not null)
        {
            return exifMeta with
            {
                Width = exifMeta.Width ?? width,
                Height = exifMeta.Height ?? height,
                Format = "JPEG",
                RawTags = MergeDicts(exifMeta.RawTags, rawTags)
            };
        }

        return new ImageMetadata(
            Width: width,
            Height: height,
            Format: "JPEG",
            RawTags: rawTags.Count > 0 ? rawTags : null);
    }

    private static ImageMetadata ParseXmp(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes.ToArray());
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1_048_576,
            });
            var document = XDocument.Load(reader);
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                    Add(attribute.Name, attribute.Value);
                if (!element.HasElements) Add(element.Name, element.Value);
                else if (element.Name.LocalName is "creator" or "rights") Add(element.Name, element.Value.Trim());
            }
            void Add(XName name, string value)
            {
                var prefix = name.NamespaceName switch
                {
                    "http://purl.org/dc/elements/1.1/" => "dc:",
                    "http://ns.adobe.com/xap/1.0/" => "XMP:",
                    "http://ns.adobe.com/exif/1.0/" => "EXIF:",
                    _ => string.Empty,
                };
                if (prefix.Length > 0 && !string.IsNullOrWhiteSpace(value)) tags[prefix + name.LocalName] = value;
            }
            var date = tags.GetValueOrDefault("EXIF:DateTimeOriginal") ?? tags.GetValueOrDefault("XMP:CreateDate");
            DateTimeOffset? capture = date is not null && TryParseDateTime(date, out var parsed) ? parsed : null;
            return new ImageMetadata(XmpCaptureTime: capture, CapturedAt: capture, RawTags: tags);
        }
        catch (XmlException)
        {
            return ImageMetadata.Empty;
        }
    }

    private static ImageMetadata ParseTiffBinary(byte[] buffer, int tiffStart, int tiffLength)
    {
        if (tiffLength < 8 || tiffStart + 8 > buffer.Length)
        {
            return ImageMetadata.Empty;
        }

        bool isLittleEndian;
        if (buffer[tiffStart] == 0x49 && buffer[tiffStart + 1] == 0x49)
        {
            isLittleEndian = true;
        }
        else if (buffer[tiffStart] == 0x4D && buffer[tiffStart + 1] == 0x4D)
        {
            isLittleEndian = false;
        }
        else
        {
            return ImageMetadata.Empty;
        }

        var magic = ReadUInt16(buffer, tiffStart + 2, isLittleEndian);
        if (magic != 42)
        {
            return ImageMetadata.Empty;
        }

        var firstIfdOffset = (int)ReadUInt32(buffer, tiffStart + 4, isLittleEndian);
        if (firstIfdOffset < 8 || tiffStart + firstIfdOffset >= buffer.Length)
        {
            return ImageMetadata.Empty;
        }

        string? make = null;
        string? model = null;
        string? software = null;
        string? orientationStr = null;
        int? orientationDeg = null;
        DateTimeOffset? embeddedDt = null;
        DateTimeOffset? dtOriginal = null;
        DateTimeOffset? dtDigitized = null;
        DateTimeOffset? xmpCapture = null;
        double? fNumber = null;
        double? focalLength = null;
        double? expTimeSec = null;
        string? expTimeString = null;
        int? iso = null;
        string? expProgram = null;
        double? expBias = null;
        string? flash = null;
        string? lens = null;
        double? lat = null;
        double? lon = null;
        double? alt = null;
        int? width = null;
        int? height = null;

        var rawTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int exifIfdOffset = 0;
        int gpsIfdOffset = 0;
        ReadIfd(buffer, tiffStart, firstIfdOffset, isLittleEndian, (tag, type, count, valOffset) =>
        {
            switch (tag)
            {
                case 0x0100:
                    width = (int)ReadTagValueUInt(buffer, tiffStart, type, valOffset, isLittleEndian);
                    break;
                case 0x0101:
                    height = (int)ReadTagValueUInt(buffer, tiffStart, type, valOffset, isLittleEndian);
                    break;
                case 0x010F:
                    make = ReadTagValueString(buffer, tiffStart, count, valOffset);
                    if (make != null) rawTags["IFD0:Make"] = make;
                    break;
                case 0x0110:
                    model = ReadTagValueString(buffer, tiffStart, count, valOffset);
                    if (model != null) rawTags["IFD0:Model"] = model;
                    break;
                case 0x0112:
                    var oVal = (ushort)ReadTagValueUInt(buffer, tiffStart, type, valOffset, isLittleEndian);
                    (orientationStr, orientationDeg) = DecodeOrientation(oVal);
                    rawTags["IFD0:Orientation"] = oVal.ToString(CultureInfo.InvariantCulture);
                    break;
                case 0x0131:
                    software = ReadTagValueString(buffer, tiffStart, count, valOffset);
                    if (software != null) rawTags["IFD0:Software"] = software;
                    break;
                case 0x0132:
                    var dtStr = ReadTagValueString(buffer, tiffStart, count, valOffset);
                    if (dtStr != null && TryParseDateTime(dtStr, out var pdt))
                    {
                        embeddedDt = pdt;
                        rawTags["IFD0:DateTime"] = dtStr;
                    }
                    break;
                case 0x013B:
                    var artist = ReadTagValueString(buffer, tiffStart, count, valOffset);
                    if (artist != null) rawTags["IFD0:Artist"] = artist;
                    break;
                case 0x8298:
                    var copyright = ReadTagValueString(buffer, tiffStart, count, valOffset);
                    if (copyright != null) rawTags["IFD0:Copyright"] = copyright;
                    break;
                case 0x8769:
                    exifIfdOffset = (int)ReadTagValueUInt(buffer, tiffStart, type, valOffset, isLittleEndian);
                    break;
                case 0x8825:
                    gpsIfdOffset = (int)ReadTagValueUInt(buffer, tiffStart, type, valOffset, isLittleEndian);
                    break;
            }
        });

        if (exifIfdOffset > 0 && tiffStart + exifIfdOffset < buffer.Length)
        {
            ReadIfd(buffer, tiffStart, exifIfdOffset, isLittleEndian, (tag, type, count, valOffset) =>
            {
                switch (tag)
                {
                    case 0x829A:
                        if (ReadTagValueRational(buffer, tiffStart, valOffset, isLittleEndian) is { } et)
                        {
                            expTimeSec = et;
                            expTimeString = FormatExposureTime(et);
                            rawTags["EXIF:ExposureTime"] = expTimeString;
                        }
                        break;
                    case 0x829D:
                        if (ReadTagValueRational(buffer, tiffStart, valOffset, isLittleEndian) is { } fn)
                        {
                            fNumber = fn;
                            rawTags["EXIF:FNumber"] = fn.ToString(CultureInfo.InvariantCulture);
                        }
                        break;
                    case 0x8822:
                        var ep = (ushort)ReadTagValueUInt(buffer, tiffStart, type, valOffset, isLittleEndian);
                        expProgram = DecodeExposureProgram(ep);
                        rawTags["EXIF:ExposureProgram"] = expProgram;
                        break;
                    case 0x8827:
                        iso = (int)ReadTagValueUInt(buffer, tiffStart, type, valOffset, isLittleEndian);
                        rawTags["EXIF:ISO"] = iso.Value.ToString(CultureInfo.InvariantCulture);
                        break;
                    case 0x9003:
                        var dtoStr = ReadTagValueString(buffer, tiffStart, count, valOffset);
                        if (dtoStr != null && TryParseDateTime(dtoStr, out var pdto))
                        {
                            dtOriginal = pdto;
                            rawTags["EXIF:DateTimeOriginal"] = dtoStr;
                        }
                        break;
                    case 0x9004:
                        var dtdStr = ReadTagValueString(buffer, tiffStart, count, valOffset);
                        if (dtdStr != null && TryParseDateTime(dtdStr, out var pdtd))
                        {
                            dtDigitized = pdtd;
                            rawTags["EXIF:DateTimeDigitized"] = dtdStr;
                        }
                        break;
                    case 0x9204:
                        if (ReadTagValueSignedRational(buffer, tiffStart, valOffset, isLittleEndian) is { } eb)
                        {
                            expBias = eb;
                            rawTags["EXIF:ExposureBias"] = eb.ToString(CultureInfo.InvariantCulture);
                        }
                        break;
                    case 0x9209:
                        var flVal = (ushort)ReadTagValueUInt(buffer, tiffStart, type, valOffset, isLittleEndian);
                        flash = (flVal & 1) != 0 ? "Flash fired" : "Flash did not fire";
                        rawTags["EXIF:Flash"] = flash;
                        break;
                    case 0x920A:
                        if (ReadTagValueRational(buffer, tiffStart, valOffset, isLittleEndian) is { } fl)
                        {
                            focalLength = fl;
                            rawTags["EXIF:FocalLength"] = fl.ToString(CultureInfo.InvariantCulture);
                        }
                        break;
                    case 0xA434:
                        lens = ReadTagValueString(buffer, tiffStart, count, valOffset);
                        if (lens != null) rawTags["EXIF:LensModel"] = lens;
                        break;
                }
            });
        }

        if (gpsIfdOffset > 0 && tiffStart + gpsIfdOffset < buffer.Length)
        {
            string? latRef = null;
            string? lonRef = null;
            double[]? latParts = null;
            double[]? lonParts = null;

            ReadIfd(buffer, tiffStart, gpsIfdOffset, isLittleEndian, (tag, type, count, valOffset) =>
            {
                switch (tag)
                {
                    case 0x0001:
                        latRef = ReadTagValueString(buffer, tiffStart, count, valOffset);
                        break;
                    case 0x0002:
                        latParts = ReadTagValueRationalArray(buffer, tiffStart, count, valOffset, isLittleEndian);
                        break;
                    case 0x0003:
                        lonRef = ReadTagValueString(buffer, tiffStart, count, valOffset);
                        break;
                    case 0x0004:
                        lonParts = ReadTagValueRationalArray(buffer, tiffStart, count, valOffset, isLittleEndian);
                        break;
                    case 0x0006:
                        if (ReadTagValueRational(buffer, tiffStart, valOffset, isLittleEndian) is { } altVal)
                        {
                            alt = altVal;
                        }
                        break;
                }
            });

            if (latParts is { Length: >= 3 })
            {
                var d = latParts[0] + latParts[1] / 60.0 + latParts[2] / 3600.0;
                if (string.Equals(latRef, "S", StringComparison.OrdinalIgnoreCase)) d = -d;
                lat = Math.Round(d, 6);
            }

            if (lonParts is { Length: >= 3 })
            {
                var d = lonParts[0] + lonParts[1] / 60.0 + lonParts[2] / 3600.0;
                if (string.Equals(lonRef, "W", StringComparison.OrdinalIgnoreCase)) d = -d;
                lon = Math.Round(d, 6);
            }
        }

        var capturedAt = ImageMetadata.ResolveCaptureTime(dtOriginal, xmpCapture, embeddedDt);

        return new ImageMetadata(
            Width: width,
            Height: height,
            Orientation: orientationStr,
            OrientationDegrees: orientationDeg,
            CameraMake: make,
            CameraModel: model,
            LensModel: lens,
            FocalLengthMm: focalLength,
            FNumber: fNumber,
            ExposureTimeString: expTimeString,
            ExposureTimeSeconds: expTimeSec,
            Iso: iso,
            ExposureProgram: expProgram,
            ExposureBiasEv: expBias,
            Flash: flash,
            DateTimeOriginal: dtOriginal,
            DateTimeDigitized: dtDigitized,
            XmpCaptureTime: xmpCapture,
            EmbeddedCreationTime: embeddedDt,
            CapturedAt: capturedAt,
            Latitude: lat,
            Longitude: lon,
            AltitudeMeters: alt,
            Software: software,
            RawTags: rawTags.Count > 0 ? rawTags : null);
    }

    private static void ReadIfd(
        byte[] buffer,
        int tiffStart,
        int ifdOffset,
        bool isLittleEndian,
        Action<ushort, ushort, uint, int> onEntry)
    {
        var dirOffset = tiffStart + ifdOffset;
        if (dirOffset + 2 > buffer.Length) return;

        var count = ReadUInt16(buffer, dirOffset, isLittleEndian);
        var entryOffset = dirOffset + 2;

        for (var i = 0; i < count; i++)
        {
            if (entryOffset + 12 > buffer.Length) break;

            var tag = ReadUInt16(buffer, entryOffset, isLittleEndian);
            var type = ReadUInt16(buffer, entryOffset + 2, isLittleEndian);
            var valCount = ReadUInt32(buffer, entryOffset + 4, isLittleEndian);
            var valOffset = entryOffset + 8;

            onEntry(tag, type, valCount, valOffset);
            entryOffset += 12;
        }
    }

    private static uint ReadTagValueUInt(byte[] buffer, int tiffStart, ushort type, int valOffset, bool isLittleEndian)
    {
        if (valOffset + 4 > buffer.Length) return 0;
        return type switch
        {
            1 => buffer[valOffset],
            3 => ReadUInt16(buffer, valOffset, isLittleEndian),
            4 => ReadUInt32(buffer, valOffset, isLittleEndian),
            _ => ReadUInt32(buffer, valOffset, isLittleEndian)
        };
    }

    private static string CleanCodecName(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return "IMAGE";
        var cleaned = friendlyName;
        if (cleaned.EndsWith(" Decoder", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^8].Trim();
        }
        else if (cleaned.EndsWith(" Encoder", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^8].Trim();
        }
        return cleaned.ToUpperInvariant();
    }

    private static string? ReadTagValueString(byte[] buffer, int tiffStart, uint count, int valOffset)
    {
        if (count == 0) return null;
        int dataOffset;
        if (count <= 4)
        {
            var isInlineAscii = true;
            for (var i = 0; i < count; i++)
            {
                var b = buffer[valOffset + i];
                if (b != 0 && (b < 0x20 || b > 0x7E)) { isInlineAscii = false; break; }
            }

            if (isInlineAscii)
            {
                dataOffset = valOffset;
            }
            else
            {
                var ptr = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(valOffset, 4));
                dataOffset = tiffStart + ptr;
            }
        }
        else
        {
            var ptr = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(valOffset, 4));
            dataOffset = tiffStart + ptr;
        }

        if (dataOffset < 0 || dataOffset + count > buffer.Length) return null;

        var length = (int)count;
        while (length > 0 && buffer[dataOffset + length - 1] == 0) length--;
        return length > 0 ? Encoding.ASCII.GetString(buffer, dataOffset, length).Trim() : null;
    }

    private static double? ReadTagValueRational(byte[] buffer, int tiffStart, int valOffset, bool isLittleEndian)
    {
        if (valOffset + 4 > buffer.Length) return null;
        var ptr = (int)ReadUInt32(buffer, valOffset, isLittleEndian);
        var absOffset = tiffStart + ptr;
        if (absOffset < 0 || absOffset + 8 > buffer.Length) return null;

        var num = ReadUInt32(buffer, absOffset, isLittleEndian);
        var den = ReadUInt32(buffer, absOffset + 4, isLittleEndian);
        if (den == 0) return null;
        return Math.Round((double)num / den, 4);
    }

    private static double? ReadTagValueSignedRational(byte[] buffer, int tiffStart, int valOffset, bool isLittleEndian)
    {
        if (valOffset + 4 > buffer.Length) return null;
        var ptr = (int)ReadUInt32(buffer, valOffset, isLittleEndian);
        var absOffset = tiffStart + ptr;
        if (absOffset < 0 || absOffset + 8 > buffer.Length) return null;

        var num = ReadInt32(buffer, absOffset, isLittleEndian);
        var den = ReadInt32(buffer, absOffset + 4, isLittleEndian);
        if (den == 0) return null;
        return Math.Round((double)num / den, 4);
    }

    private static double[]? ReadTagValueRationalArray(byte[] buffer, int tiffStart, uint count, int valOffset, bool isLittleEndian)
    {
        if (count == 0) return null;
        var ptr = (int)ReadUInt32(buffer, valOffset, isLittleEndian);
        var absOffset = tiffStart + ptr;
        if (absOffset < 0 || absOffset + (count * 8) > buffer.Length) return null;

        var result = new double[count];
        for (var i = 0; i < count; i++)
        {
            var num = ReadUInt32(buffer, absOffset + i * 8, isLittleEndian);
            var den = ReadUInt32(buffer, absOffset + i * 8 + 4, isLittleEndian);
            result[i] = den != 0 ? (double)num / den : 0.0;
        }

        return result;
    }

    private static ushort ReadUInt16(byte[] buffer, int offset, bool isLittleEndian) =>
        isLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));

    private static uint ReadUInt32(byte[] buffer, int offset, bool isLittleEndian) =>
        isLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));

    private static int ReadInt32(byte[] buffer, int offset, bool isLittleEndian) =>
        isLittleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4))
            : BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset, 4));

    private static (string Label, int Degrees) DecodeOrientation(ushort val) =>
        val switch
        {
            1 => ("Normal (0\u00B0)", 0),
            3 => ("Rotate 180\u00B0", 180),
            6 => ("Rotate 90\u00B0 CW", 90),
            8 => ("Rotate 270\u00B0 CW", 270),
            _ => ($"Orientation {val}", 0)
        };

    private static string DecodeExposureProgram(ushort val) =>
        val switch
        {
            1 => "Manual",
            2 => "Normal / Program AE",
            3 => "Aperture priority",
            4 => "Shutter priority",
            5 => "Creative",
            6 => "Action",
            7 => "Portrait",
            8 => "Landscape",
            _ => $"Program {val}"
        };

    private static string FormatExposureTime(double seconds)
    {
        if (seconds <= 0) return "—";
        if (seconds >= 1.0) return $"{seconds:0.##} s";
        var denom = (int)Math.Round(1.0 / seconds);
        return denom > 0 ? $"1/{denom} s" : $"{seconds:0.####} s";
    }

    private static bool TryParseDateTime(string s, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        if (DateTimeOffset.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out result))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
        {
            return true;
        }

        return false;
    }

    private static bool TryParseDouble(object obj, out double val)
    {
        val = 0;
        if (obj is double d) { val = d; return true; }
        if (obj is float f) { val = f; return true; }
        if (obj is int i) { val = i; return true; }
        if (obj is long l) { val = l; return true; }
        if (obj is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            val = parsed;
            return true;
        }
        return false;
    }

    private static bool TryParseInt(object obj, out int val)
    {
        val = 0;
        if (obj is int i) { val = i; return true; }
        if (obj is ushort u) { val = u; return true; }
        if (obj is uint ui) { val = (int)ui; return true; }
        if (obj is string s && int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            val = parsed;
            return true;
        }
        return false;
    }


    private static ImageMetadata MergeResults(ImageMetadata? wpf, ImageMetadata? binary, string? hintFormat)
    {
        if (wpf is null && binary is null)
        {
            return ImageMetadata.Empty;
        }

        if (wpf is null) return binary!;
        if (binary is null) return wpf;

        var dtOrig = binary.DateTimeOriginal ?? wpf.DateTimeOriginal;
        var xmp = binary.XmpCaptureTime ?? wpf.XmpCaptureTime;
        var emb = binary.EmbeddedCreationTime ?? wpf.EmbeddedCreationTime;
        var captured = ImageMetadata.ResolveCaptureTime(dtOrig, xmp, emb);

        return new ImageMetadata(
            Width: wpf.Width ?? binary.Width,
            Height: wpf.Height ?? binary.Height,
            Format: wpf.Format ?? binary.Format ?? hintFormat,
            Orientation: binary.Orientation ?? wpf.Orientation,
            OrientationDegrees: binary.OrientationDegrees ?? wpf.OrientationDegrees,
            ColorSpace: wpf.ColorSpace ?? binary.ColorSpace,
            ColorProfile: wpf.ColorProfile ?? binary.ColorProfile,
            BitDepth: wpf.BitDepth ?? binary.BitDepth,
            CameraMake: binary.CameraMake ?? wpf.CameraMake,
            CameraModel: binary.CameraModel ?? wpf.CameraModel,
            LensModel: binary.LensModel ?? wpf.LensModel,
            FocalLengthMm: binary.FocalLengthMm ?? wpf.FocalLengthMm,
            FNumber: binary.FNumber ?? wpf.FNumber,
            ExposureTimeString: binary.ExposureTimeString ?? wpf.ExposureTimeString,
            ExposureTimeSeconds: binary.ExposureTimeSeconds ?? wpf.ExposureTimeSeconds,
            Iso: binary.Iso ?? wpf.Iso,
            ExposureProgram: binary.ExposureProgram ?? wpf.ExposureProgram,
            ExposureBiasEv: binary.ExposureBiasEv ?? wpf.ExposureBiasEv,
            Flash: binary.Flash ?? wpf.Flash,
            DateTimeOriginal: dtOrig,
            DateTimeDigitized: binary.DateTimeDigitized ?? wpf.DateTimeDigitized,
            XmpCaptureTime: xmp,
            EmbeddedCreationTime: emb,
            CapturedAt: captured,
            Latitude: binary.Latitude ?? wpf.Latitude,
            Longitude: binary.Longitude ?? wpf.Longitude,
            AltitudeMeters: binary.AltitudeMeters ?? wpf.AltitudeMeters,
            LocationName: binary.LocationName ?? wpf.LocationName,
            Software: binary.Software ?? wpf.Software,
            RawTags: MergeDicts(wpf.RawTags, binary.RawTags));
    }

    private static IReadOnlyDictionary<string, string>? MergeDicts(
        IReadOnlyDictionary<string, string>? d1,
        IReadOnlyDictionary<string, string>? d2)
    {
        if (d1 is null && d2 is null) return null;
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (d1 is not null)
        {
            foreach (var (k, v) in d1) merged[k] = v;
        }
        if (d2 is not null)
        {
            foreach (var (k, v) in d2) merged[k] = v;
        }
        return merged;
    }

    public static string Serialize(ImageMetadata metadata) =>
        JsonSerializer.Serialize(metadata, JsonOptions);

    public static ImageMetadata Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ImageMetadata.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<ImageMetadata>(json, JsonOptions) ?? ImageMetadata.Empty;
        }
        catch
        {
            return ImageMetadata.Empty;
        }
    }
}
