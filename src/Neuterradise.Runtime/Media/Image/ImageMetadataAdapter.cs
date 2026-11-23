using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using SkiaSharp;
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

    public unsafe ImageMetadata ExtractFromBytes(ReadOnlySpan<byte> bytes, string? hintFormat = null)
    {
        if (bytes.IsEmpty) return ImageMetadata.Empty;

        fixed (byte* pointer = bytes)
        {
            using var stream = new UnmanagedMemoryStream(pointer, bytes.Length);
            using var data = SKData.Create((IntPtr)pointer, bytes.Length);
            return Extract(stream, hintFormat, data);
        }
    }

    private static ImageMetadata ExtractFromStream(FileStream stream, string? hintFormat)
    {
        if (stream.Length == 0) return ImageMetadata.Empty;

        // Skia's WebP decoder requires contiguous input and copies an ordinary stream.
        // A read-only mapping lets the codec inspect that input without reading it into a buffer.
        if (NeedsContiguousCodecInput(stream))
        {
            return ExtractMapped(stream, hintFormat);
        }

        return Extract(stream, hintFormat);
    }

    private static bool NeedsContiguousCodecInput(FileStream stream)
    {
        Span<byte> header = stackalloc byte[12];
        if (stream.Length < header.Length) return false;

        stream.ReadExactly(header);
        stream.Position = 0;
        return IsWebP(header) || header[4..8].SequenceEqual("ftyp"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(header) == 0x00010000;
    }

    private static unsafe ImageMetadata ExtractMapped(FileStream file, string? hintFormat)
    {
        // SKData accepts an int-sized view; the container reader still uses the full file length.
        var codecLength = (int)Math.Min(file.Length, int.MaxValue);
        using var mapping = MemoryMappedFile.CreateFromFile(file, null, 0,
            MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        using var view = mapping.CreateViewAccessor(0, codecLength, MemoryMappedFileAccess.Read);
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        try
        {
            using var data = SKData.Create((IntPtr)(pointer + view.PointerOffset), codecLength);
            return Extract(file, hintFormat, data);
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static ImageMetadata Extract(Stream stream, string? hintFormat, SKData? codecData = null)
    {
        var input = new MetadataRegion(stream, 0, stream.Length);
        var binaryResult = TryExtractViaBinary(input);
        stream.Position = 0;
        var codecResult = TryExtractViaCodec(stream, hintFormat, codecData);
        return MergeResults(codecResult, binaryResult, hintFormat);
    }

    private static ImageMetadata? TryExtractViaCodec(Stream stream, string? hintFormat, SKData? data)
    {
        try
        {
            using var codec = data is null
                ? SKCodec.Create(new SKManagedStream(stream, disposeManagedStream: false))
                : SKCodec.Create(data);
            if (codec is null)
            {
                return null;
            }

            var info = codec.Info;
            var (orientation, degrees) = codec.EncodedOrigin switch
            {
                SKEncodedOrigin.RightTop => ("Rotate 90 CW", 90),
                SKEncodedOrigin.BottomRight => ("Rotate 180", 180),
                SKEncodedOrigin.LeftBottom => ("Rotate 270 CW", 270),
                SKEncodedOrigin.TopRight => ("Mirror horizontal", 0),
                SKEncodedOrigin.BottomLeft => ("Mirror vertical", 0),
                SKEncodedOrigin.LeftTop => ("Mirror horizontal and rotate 270 CW", 270),
                SKEncodedOrigin.RightBottom => ("Mirror horizontal and rotate 90 CW", 90),
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

    private const int MaximumMetadataValueBytes = 4 * 1024 * 1024;

    private static bool IsWebP(ReadOnlySpan<byte> header) =>
        header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8);

    private static ImageMetadata? TryExtractViaBinary(MetadataRegion input)
    {
        try
        {
            Span<byte> header = stackalloc byte[12];
            var size = (int)Math.Min(input.Length, header.Length);
            if (size < 8 || !input.TryRead(0, header[..size])) return null;

            if (header[0] == 0x89 && header[1..4].SequenceEqual("PNG"u8)) return ParsePngBinary(input);
            if (header[0] == 0xFF && header[1] == 0xD8) return ParseJpegBinary(input);
            if (IsWebP(header[..size])) return ParseWebPBinary(input, BinaryPrimitives.ReadUInt32LittleEndian(header[4..]));
            if (header[..2].SequenceEqual("II"u8) || header[..2].SequenceEqual("MM"u8)) return ParseTiffBinary(input);
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static ImageMetadata? ParseWebPBinary(MetadataRegion input, uint riffLength)
    {
        ImageMetadata? metadata = null;
        var end = Math.Min(input.Length, 8L + riffLength);
        Span<byte> header = stackalloc byte[8];
        Span<byte> exifPrefix = stackalloc byte[6];
        for (long offset = 12; offset <= end - header.Length && input.TryRead(offset, header);)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var dataOffset = offset + header.Length;
            var next = dataOffset + length + (length & 1);
            if (next > end) break;

            var type = header[..4];
            if (type.SequenceEqual("EXIF"u8))
            {
                var exif = input.Slice(dataOffset, length);
                if (exif.TryRead(0, exifPrefix) && exifPrefix.SequenceEqual("Exif\0\0"u8))
                {
                    exif = exif.Slice(6, exif.Length - 6);
                }
                metadata = MergeResults(metadata, ParseTiffBinary(exif), "WEBP");
            }
            else if (type.SequenceEqual("XMP "u8))
            {
                metadata = MergeResults(metadata, ParseXmp(input.Slice(dataOffset, length)), "WEBP");
            }

            offset = next;
        }

        return metadata;
    }

    private static ImageMetadata ParsePngBinary(MetadataRegion input)
    {
        ImageMetadata? exif = null;
        int? width = null;
        int? height = null;
        int? bitDepth = null;
        string? colorSpace = null;
        var rawTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Span<byte> header = stackalloc byte[8];
        Span<byte> ihdr = stackalloc byte[13];
        for (long offset = 8; input.TryRead(offset, header);)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            var dataOffset = offset + header.Length;
            if (length < 0 || !input.Contains(dataOffset, length + 4L)) break;

            var type = header[4..];
            if (type.SequenceEqual("IHDR"u8) && length >= ihdr.Length && input.TryRead(dataOffset, ihdr))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(ihdr);
                height = BinaryPrimitives.ReadInt32BigEndian(ihdr[4..]);
                bitDepth = ihdr[8];
                colorSpace = ihdr[9] switch
                {
                    0 => "Grayscale",
                    2 => "RGB",
                    3 => "Indexed",
                    4 => "Grayscale + Alpha",
                    6 => "RGBA",
                    _ => "PNG"
                };
            }
            else if (type.SequenceEqual("eXIf"u8))
            {
                exif = ParseTiffBinary(input.Slice(dataOffset, length));
            }
            else if (type.SequenceEqual("tEXt"u8)
                && input.ReadMetadataValue(dataOffset, length) is { } text)
            {
                var separator = text.AsSpan().IndexOf((byte)0);
                if (separator > 0 && separator + 1 < text.Length)
                {
                    var key = Encoding.ASCII.GetString(text.AsSpan(0, separator));
                    rawTags[$"PNG:{key}"] = Encoding.Latin1.GetString(text.AsSpan(separator + 1));
                }
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            offset = dataOffset + length + 4;
        }

        return MergeResults(exif, new ImageMetadata(
            Width: width,
            Height: height,
            Format: "PNG",
            BitDepth: bitDepth,
            ColorSpace: colorSpace,
            RawTags: rawTags.Count > 0 ? rawTags : null), "PNG");
    }

    private static ImageMetadata ParseJpegBinary(MetadataRegion input)
    {
        int? width = null;
        int? height = null;
        ImageMetadata? exifMeta = null;

        Span<byte> markerBytes = stackalloc byte[2];
        Span<byte> dimensions = stackalloc byte[5];
        ReadOnlySpan<byte> xmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8;
        Span<byte> prefix = stackalloc byte[xmpHeader.Length];
        var offset = 2L;
        while (input.TryRead(offset, markerBytes) && markerBytes[0] == 0xFF)
        {
            var marker = markerBytes[1];
            offset += markerBytes.Length;
            while (marker == 0xFF)
            {
                if (!input.TryRead(offset++, markerBytes[..1])) break;
                marker = markerBytes[0];
            }

            if (marker is 0xD9 or 0xDA) break;
            if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue;
            if (!input.TryRead(offset, markerBytes)) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(markerBytes);
            if (length < 2 || !input.Contains(offset, length)) break;

            var payload = input.Slice(offset + 2, length - 2);
            if (marker is 0xC0 or 0xC1 or 0xC2 && payload.TryRead(0, dimensions))
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(dimensions[1..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(dimensions[3..]);
            }
            else if (marker == 0xE1)
            {
                if (payload.TryRead(0, prefix[..6]) && prefix[..6].SequenceEqual("Exif\0\0"u8))
                {
                    exifMeta = MergeResults(exifMeta, ParseTiffBinary(payload.Slice(6, payload.Length - 6)), "JPEG");
                }
                else if (payload.TryRead(0, prefix) && prefix.SequenceEqual(xmpHeader))
                {
                    exifMeta = MergeResults(exifMeta, ParseXmp(payload.Slice(prefix.Length, payload.Length - prefix.Length)), "JPEG");
                }
            }

            offset += length;
        }

        if (exifMeta is not null)
        {
            return exifMeta with
            {
                Width = exifMeta.Width ?? width,
                Height = exifMeta.Height ?? height,
                Format = "JPEG"
            };
        }

        return new ImageMetadata(Width: width, Height: height, Format: "JPEG");
    }

    private static ImageMetadata ParseXmp(MetadataRegion input)
    {
        try
        {
            if (input.ReadMetadataValue(0, input.Length) is not { } bytes) return ImageMetadata.Empty;
            using var stream = new MemoryStream(bytes, writable: false);
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

    private static ImageMetadata ParseTiffBinary(MetadataRegion input)
    {
        Span<byte> header = stackalloc byte[8];
        if (!input.TryRead(0, header)) return ImageMetadata.Empty;

        var isLittleEndian = header[..2].SequenceEqual("II"u8);
        if (!isLittleEndian && !header[..2].SequenceEqual("MM"u8)) return ImageMetadata.Empty;
        if (ReadUInt16(header[2..], isLittleEndian) != 42) return ImageMetadata.Empty;

        var firstIfdOffset = ReadUInt32(header[4..], isLittleEndian);
        if (firstIfdOffset < 8 || !input.Contains(firstIfdOffset, 2)) return ImageMetadata.Empty;

        string? make = null;
        string? model = null;
        string? software = null;
        string? orientationStr = null;
        int? orientationDeg = null;
        DateTimeOffset? embeddedDt = null;
        DateTimeOffset? dtOriginal = null;
        DateTimeOffset? dtDigitized = null;
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

        uint exifIfdOffset = 0;
        uint gpsIfdOffset = 0;
        ReadIfd(input, firstIfdOffset, isLittleEndian, (tag, type, count, valOffset) =>
        {
            switch (tag)
            {
                case 0x0100:
                    width = (int)ReadTagValueUInt(input, type, valOffset, isLittleEndian);
                    break;
                case 0x0101:
                    height = (int)ReadTagValueUInt(input, type, valOffset, isLittleEndian);
                    break;
                case 0x010F:
                    make = ReadTagValueString(input, count, valOffset, isLittleEndian);
                    if (make != null) rawTags["IFD0:Make"] = make;
                    break;
                case 0x0110:
                    model = ReadTagValueString(input, count, valOffset, isLittleEndian);
                    if (model != null) rawTags["IFD0:Model"] = model;
                    break;
                case 0x0112:
                    var oVal = (ushort)ReadTagValueUInt(input, type, valOffset, isLittleEndian);
                    (orientationStr, orientationDeg) = DecodeOrientation(oVal);
                    rawTags["IFD0:Orientation"] = oVal.ToString(CultureInfo.InvariantCulture);
                    break;
                case 0x0131:
                    software = ReadTagValueString(input, count, valOffset, isLittleEndian);
                    if (software != null) rawTags["IFD0:Software"] = software;
                    break;
                case 0x0132:
                    var dtStr = ReadTagValueString(input, count, valOffset, isLittleEndian);
                    if (dtStr != null && TryParseDateTime(dtStr, out var pdt))
                    {
                        embeddedDt = pdt;
                        rawTags["IFD0:DateTime"] = dtStr;
                    }
                    break;
                case 0x013B:
                    var artist = ReadTagValueString(input, count, valOffset, isLittleEndian);
                    if (artist != null) rawTags["IFD0:Artist"] = artist;
                    break;
                case 0x8298:
                    var copyright = ReadTagValueString(input, count, valOffset, isLittleEndian);
                    if (copyright != null) rawTags["IFD0:Copyright"] = copyright;
                    break;
                case 0x8769:
                    exifIfdOffset = ReadTagValueUInt(input, type, valOffset, isLittleEndian);
                    break;
                case 0x8825:
                    gpsIfdOffset = ReadTagValueUInt(input, type, valOffset, isLittleEndian);
                    break;
            }
        });

        if (exifIfdOffset >= 8)
        {
            ReadIfd(input, exifIfdOffset, isLittleEndian, (tag, type, count, valOffset) =>
            {
                switch (tag)
                {
                    case 0x829A:
                        if (ReadTagValueRational(input, valOffset, isLittleEndian) is { } et)
                        {
                            expTimeSec = et;
                            expTimeString = FormatExposureTime(et);
                            rawTags["EXIF:ExposureTime"] = expTimeString;
                        }
                        break;
                    case 0x829D:
                        if (ReadTagValueRational(input, valOffset, isLittleEndian) is { } fn)
                        {
                            fNumber = fn;
                            rawTags["EXIF:FNumber"] = fn.ToString(CultureInfo.InvariantCulture);
                        }
                        break;
                    case 0x8822:
                        var ep = (ushort)ReadTagValueUInt(input, type, valOffset, isLittleEndian);
                        expProgram = DecodeExposureProgram(ep);
                        rawTags["EXIF:ExposureProgram"] = expProgram;
                        break;
                    case 0x8827:
                        iso = (int)ReadTagValueUInt(input, type, valOffset, isLittleEndian);
                        rawTags["EXIF:ISO"] = iso.Value.ToString(CultureInfo.InvariantCulture);
                        break;
                    case 0x9003:
                        var dtoStr = ReadTagValueString(input, count, valOffset, isLittleEndian);
                        if (dtoStr != null && TryParseDateTime(dtoStr, out var pdto))
                        {
                            dtOriginal = pdto;
                            rawTags["EXIF:DateTimeOriginal"] = dtoStr;
                        }
                        break;
                    case 0x9004:
                        var dtdStr = ReadTagValueString(input, count, valOffset, isLittleEndian);
                        if (dtdStr != null && TryParseDateTime(dtdStr, out var pdtd))
                        {
                            dtDigitized = pdtd;
                            rawTags["EXIF:DateTimeDigitized"] = dtdStr;
                        }
                        break;
                    case 0x9204:
                        if (ReadTagValueRational(input, valOffset, isLittleEndian, signed: true) is { } eb)
                        {
                            expBias = eb;
                            rawTags["EXIF:ExposureBias"] = eb.ToString(CultureInfo.InvariantCulture);
                        }
                        break;
                    case 0x9209:
                        var flVal = (ushort)ReadTagValueUInt(input, type, valOffset, isLittleEndian);
                        flash = (flVal & 1) != 0 ? "Flash fired" : "Flash did not fire";
                        rawTags["EXIF:Flash"] = flash;
                        break;
                    case 0x920A:
                        if (ReadTagValueRational(input, valOffset, isLittleEndian) is { } fl)
                        {
                            focalLength = fl;
                            rawTags["EXIF:FocalLength"] = fl.ToString(CultureInfo.InvariantCulture);
                        }
                        break;
                    case 0xA434:
                        lens = ReadTagValueString(input, count, valOffset, isLittleEndian);
                        if (lens != null) rawTags["EXIF:LensModel"] = lens;
                        break;
                }
            });
        }

        if (gpsIfdOffset >= 8)
        {
            string? latRef = null;
            string? lonRef = null;
            double[]? latParts = null;
            double[]? lonParts = null;

            ReadIfd(input, gpsIfdOffset, isLittleEndian, (tag, type, count, valOffset) =>
            {
                switch (tag)
                {
                    case 0x0001:
                        latRef = ReadTagValueString(input, count, valOffset, isLittleEndian);
                        break;
                    case 0x0002:
                        latParts = ReadGpsCoordinate(input, count, valOffset, isLittleEndian);
                        break;
                    case 0x0003:
                        lonRef = ReadTagValueString(input, count, valOffset, isLittleEndian);
                        break;
                    case 0x0004:
                        lonParts = ReadGpsCoordinate(input, count, valOffset, isLittleEndian);
                        break;
                    case 0x0006:
                        if (ReadTagValueRational(input, valOffset, isLittleEndian) is { } altVal)
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

        var capturedAt = ImageMetadata.ResolveCaptureTime(dtOriginal, null, embeddedDt);

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
            EmbeddedCreationTime: embeddedDt,
            CapturedAt: capturedAt,
            Latitude: lat,
            Longitude: lon,
            AltitudeMeters: alt,
            Software: software,
            RawTags: rawTags.Count > 0 ? rawTags : null);
    }

    private static void ReadIfd(
        MetadataRegion input,
        uint ifdOffset,
        bool littleEndian,
        Action<ushort, ushort, uint, long> onEntry)
    {
        Span<byte> entry = stackalloc byte[12];
        if (!input.TryRead(ifdOffset, entry[..2])) return;

        var count = ReadUInt16(entry, littleEndian);
        var entryOffset = ifdOffset + 2L;
        for (var i = 0; i < count && input.TryRead(entryOffset, entry); i++)
        {
            var tag = ReadUInt16(entry, littleEndian);
            var type = ReadUInt16(entry[2..], littleEndian);
            var valueCount = ReadUInt32(entry[4..], littleEndian);
            onEntry(tag, type, valueCount, entryOffset + 8);
            entryOffset += entry.Length;
        }
    }

    private static uint ReadTagValueUInt(MetadataRegion input, ushort type, long valueOffset, bool littleEndian)
    {
        Span<byte> value = stackalloc byte[4];
        if (!input.TryRead(valueOffset, value)) return 0;

        return type switch
        {
            1 => value[0],
            3 => ReadUInt16(value, littleEndian),
            _ => ReadUInt32(value, littleEndian)
        };
    }

    private static string? ReadTagValueString(MetadataRegion input, uint count, long valueOffset, bool littleEndian)
    {
        if (count == 0 || count > MaximumMetadataValueBytes) return null;
        Span<byte> value = stackalloc byte[4];
        if (!input.TryRead(valueOffset, value)) return null;

        if (count <= 4)
        {
            var inline = value[..(int)count];
            if (IsInlineAscii(inline))
            {
                return DecodeAscii(inline);
            }
        }

        var pointer = ReadUInt32(value, littleEndian);
        return pointer >= 8 && input.ReadMetadataValue(pointer, count) is { } bytes ? DecodeAscii(bytes) : null;
    }

    private static bool IsInlineAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0 && (value < 0x20 || value > 0x7E)) return false;
        }

        return true;
    }

    private static string? DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        var text = bytes.TrimEnd((byte)0);
        return text.IsEmpty ? null : Encoding.ASCII.GetString(text).Trim();
    }

    private static double? ReadTagValueRational(
        MetadataRegion input, long valueOffset, bool littleEndian, bool signed = false)
    {
        var pointer = ReadTagValueUInt(input, 4, valueOffset, littleEndian);
        Span<byte> bytes = stackalloc byte[8];
        if (pointer < 8 || !input.TryRead(pointer, bytes)) return null;

        var numerator = ReadUInt32(bytes, littleEndian);
        var denominator = ReadUInt32(bytes[4..], littleEndian);
        if (denominator == 0) return null;
        var value = signed ? (double)unchecked((int)numerator) / unchecked((int)denominator)
            : (double)numerator / denominator;
        return Math.Round(value, 4);
    }

    private static double[]? ReadGpsCoordinate(MetadataRegion input, uint count, long valueOffset, bool littleEndian)
    {
        if (count < 3) return null;
        var pointer = ReadTagValueUInt(input, 4, valueOffset, littleEndian);
        Span<byte> bytes = stackalloc byte[24];
        if (pointer < 8 || !input.Contains(pointer, count * 8L) || !input.TryRead(pointer, bytes)) return null;

        var result = new double[3];
        for (var i = 0; i < result.Length; i++)
        {
            var numerator = ReadUInt32(bytes[(i * 8)..], littleEndian);
            var denominator = ReadUInt32(bytes[(i * 8 + 4)..], littleEndian);
            result[i] = denominator != 0 ? (double)numerator / denominator : 0.0;
        }

        return result;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private readonly struct MetadataRegion(Stream stream, long start, long length)
    {
        public long Length => length;

        public bool Contains(long offset, long size) =>
            offset >= 0 && size >= 0 && offset <= length && size <= length - offset;

        // The caller validates the chunk or segment before creating a region for nested offsets.
        public MetadataRegion Slice(long offset, long size) => new(stream, start + offset, size);

        public bool TryRead(long offset, Span<byte> bytes)
        {
            if (!Contains(offset, bytes.Length)) return false;

            var position = start + offset;
            if (stream.Position != position) stream.Position = position;
            stream.ReadExactly(bytes);
            return true;
        }

        public byte[]? ReadMetadataValue(long offset, long size)
        {
            if (size > MaximumMetadataValueBytes || !Contains(offset, size)) return null;

            var bytes = new byte[(int)size];
            return TryRead(offset, bytes) ? bytes : null;
        }
    }

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

    private static ImageMetadata MergeResults(ImageMetadata? primary, ImageMetadata? binary, string? hintFormat)
    {
        if (primary is null && binary is null)
        {
            return ImageMetadata.Empty;
        }

        if (primary is null) return binary!;
        if (binary is null) return primary;

        var dtOrig = binary.DateTimeOriginal ?? primary.DateTimeOriginal;
        var xmp = binary.XmpCaptureTime ?? primary.XmpCaptureTime;
        var emb = binary.EmbeddedCreationTime ?? primary.EmbeddedCreationTime;
        var captured = ImageMetadata.ResolveCaptureTime(dtOrig, xmp, emb);

        return new ImageMetadata(
            Width: primary.Width ?? binary.Width,
            Height: primary.Height ?? binary.Height,
            Format: primary.Format ?? binary.Format ?? hintFormat,
            Orientation: binary.Orientation ?? primary.Orientation,
            OrientationDegrees: binary.OrientationDegrees ?? primary.OrientationDegrees,
            ColorSpace: primary.ColorSpace ?? binary.ColorSpace,
            ColorProfile: primary.ColorProfile ?? binary.ColorProfile,
            BitDepth: primary.BitDepth ?? binary.BitDepth,
            CameraMake: binary.CameraMake ?? primary.CameraMake,
            CameraModel: binary.CameraModel ?? primary.CameraModel,
            LensModel: binary.LensModel ?? primary.LensModel,
            FocalLengthMm: binary.FocalLengthMm ?? primary.FocalLengthMm,
            FNumber: binary.FNumber ?? primary.FNumber,
            ExposureTimeString: binary.ExposureTimeString ?? primary.ExposureTimeString,
            ExposureTimeSeconds: binary.ExposureTimeSeconds ?? primary.ExposureTimeSeconds,
            Iso: binary.Iso ?? primary.Iso,
            ExposureProgram: binary.ExposureProgram ?? primary.ExposureProgram,
            ExposureBiasEv: binary.ExposureBiasEv ?? primary.ExposureBiasEv,
            Flash: binary.Flash ?? primary.Flash,
            DateTimeOriginal: dtOrig,
            DateTimeDigitized: binary.DateTimeDigitized ?? primary.DateTimeDigitized,
            XmpCaptureTime: xmp,
            EmbeddedCreationTime: emb,
            CapturedAt: captured,
            Latitude: binary.Latitude ?? primary.Latitude,
            Longitude: binary.Longitude ?? primary.Longitude,
            AltitudeMeters: binary.AltitudeMeters ?? primary.AltitudeMeters,
            LocationName: binary.LocationName ?? primary.LocationName,
            Software: binary.Software ?? primary.Software,
            RawTags: MergeDicts(primary.RawTags, binary.RawTags));
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
