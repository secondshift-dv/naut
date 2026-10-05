using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Neuterradise.App.Presentation;

public enum PackAssetKind
{
    Image,
    Texture,
    Video,
    Font,
    Lottie,
}

public sealed record PackAssetEntry(string Id, PackAssetKind Kind, string Path, long? DeclaredBytes);

/// <summary>Resource characteristics a definition declares. The ResourceGovernor keeps authority.</summary>
public sealed record DefinitionPerformance(
    string Tier,
    bool ContinuousAnimation,
    string OffscreenPolicy,
    int MaxAnimatedLayers)
{
    public static DefinitionPerformance Static { get; } = new("utility", false, "suspend", 0);
}

/// <summary>
/// One declarative definition inside a pack. <see cref="SpecJson"/> is data only, validated by the
/// compiler for its kind. It is parsed once per compile, never per frame.
/// </summary>
public sealed record PresentationDefinition(
    string Id,
    string Kind,
    string Version,
    string Name,
    string? Description,
    IReadOnlyList<string> Tags,
    DefinitionPerformance Performance,
    string SpecJson,
    string? PreviewAccent)
{
    public DefinitionRef RefIn(string packId) => new(packId, Id);
}

public sealed record PresentationPackManifest(
    int SchemaVersion,
    string PackId,
    string Version,
    string Name,
    string? Author,
    string? Description,
    string? License,
    int MinContractVersion,
    IReadOnlyList<PackAssetEntry> Assets,
    IReadOnlyList<PresentationDefinition> Definitions);

public sealed record PackDiagnostic(string Code, string Subject, string Detail, bool IsError = true);

public sealed record PackReadResult(PresentationPackManifest? Manifest, IReadOnlyList<PackDiagnostic> Diagnostics)
{
    public bool IsValid => Manifest is not null && Diagnostics.All(d => !d.IsError);
}

public static class PackDiagnosticCodes
{
    public const string JsonInvalid = "PACK_JSON_INVALID";
    public const string FieldMissing = "PACK_FIELD_MISSING";
    public const string SchemaUnsupported = "PACK_SCHEMA_UNSUPPORTED";
    public const string ContractTooNew = "PACK_CONTRACT_TOO_NEW";
    public const string IdInvalid = "PACK_ID_INVALID";
    public const string IdReserved = "PACK_ID_RESERVED";
    public const string AlreadyInstalled = "PACK_ALREADY_INSTALLED";
    public const string DefinitionIdInvalid = "PACK_DEFINITION_ID_INVALID";
    public const string DefinitionDuplicate = "PACK_DEFINITION_DUPLICATE";
    public const string KindUnknown = "PACK_KIND_UNKNOWN";
    public const string KindRetired = "PACK_KIND_RETIRED";
    public const string MediaIdDuplicate = "PACK_ASSET_DUPLICATE";
    public const string AssetPathInvalid = "PACK_ASSET_PATH_INVALID";
    public const string AssetMissing = "PACK_ASSET_MISSING";
    public const string AssetTooLarge = "PACK_ASSET_TOO_LARGE";
    public const string AssetTypeInvalid = "PACK_ASSET_TYPE_INVALID";
    public const string AssetUnreadable = "PACK_ASSET_UNREADABLE";
    public const string ForbiddenContent = "PACK_FORBIDDEN_CONTENT";
    public const string PackTooLarge = "PACK_TOO_LARGE";
    public const string TooManyEntries = "PACK_TOO_MANY_ENTRIES";
    public const string SpecInvalid = "PACK_SPEC_INVALID";
}

/// <summary>Strict reader for <c>pack.json</c>. Unknown top-level fields are tolerated as warnings.</summary>
public static class PackManifestReader
{
    public const string ManifestFileName = "pack.json";
    public const int MaxManifestBytes = 4 * 1024 * 1024;

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    public static PackReadResult Read(string json, string source)
    {
        var diagnostics = new List<PackDiagnostic>();
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > MaxManifestBytes)
            {
                return new(null, [new(PackDiagnosticCodes.PackTooLarge, source, "The manifest exceeds 4 MiB.")]);
            }

            using var document = JsonDocument.Parse(json, Options);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new(PackDiagnosticCodes.JsonInvalid, source, "The manifest root must be an object."));
                return new(null, diagnostics);
            }

            var schema = ReadInt(root, "schemaVersion", diagnostics, source) ?? 0;
            var packId = ReadString(root, "packId", diagnostics, source, required: true) ?? string.Empty;
            var version = ReadString(root, "version", diagnostics, source, required: true) ?? "0.0.0";
            var name = ReadString(root, "name", diagnostics, source, required: true) ?? packId;
            var author = ReadString(root, "author", diagnostics, source, required: false);
            var description = ReadString(root, "description", diagnostics, source, required: false);
            var license = ReadString(root, "license", diagnostics, source, required: false);
            var minContract = ReadInt(root, "minContractVersion", diagnostics, source) ?? 1;

            foreach (var field in new[] { "assets", "definitions" })
            {
                if (!root.TryGetProperty(field, out var array) || array.ValueKind != JsonValueKind.Array)
                    diagnostics.Add(new(PackDiagnosticCodes.JsonInvalid, source, $"'{field}' must be an array."));
                else if (array.GetArrayLength() > (field == "assets" ? PackValidator.MaxAssets : PackValidator.MaxDefinitions))
                    diagnostics.Add(new(PackDiagnosticCodes.TooManyEntries, source, $"'{field}' contains too many entries."));
            }
            if (diagnostics.Any(d => d.IsError)) return new(null, diagnostics);

            var assets = new List<PackAssetEntry>();
            if (root.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in assetArray.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        diagnostics.Add(new(PackDiagnosticCodes.JsonInvalid, source, "Each asset must be an object."));
                        continue;
                    }
                    var id = ReadString(element, "id", diagnostics, source, required: true) ?? string.Empty;
                    var kindText = ReadString(element, "kind", diagnostics, source, required: true) ?? string.Empty;
                    var path = ReadString(element, "path", diagnostics, source, required: true) ?? string.Empty;
                    long? bytes = null;
                    if (element.TryGetProperty("bytes", out var b))
                    {
                        if (b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out var parsed) && parsed >= 0) bytes = parsed;
                        else diagnostics.Add(new(PackDiagnosticCodes.JsonInvalid, id, "Asset bytes must be a nonnegative integer."));
                    }
                    if (!Enum.TryParse<PackAssetKind>(kindText, ignoreCase: true, out var kind) || !Enum.IsDefined(kind) || !Enum.GetNames<PackAssetKind>().Contains(kindText, StringComparer.OrdinalIgnoreCase))
                    {
                        diagnostics.Add(new(PackDiagnosticCodes.AssetTypeInvalid, id, $"Asset kind '{kindText}' is not supported."));
                        continue;
                    }

                    assets.Add(new PackAssetEntry(id, kind, path, bytes));
                }
            }

            var definitions = new List<PresentationDefinition>();
            if (root.TryGetProperty("definitions", out var definitionArray) && definitionArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in definitionArray.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        diagnostics.Add(new(PackDiagnosticCodes.JsonInvalid, source, "Each definition must be an object."));
                        continue;
                    }
                    if (element.TryGetProperty("spec", out var definitionSpec) && definitionSpec.ValueKind != JsonValueKind.Object)
                        diagnostics.Add(new(PackDiagnosticCodes.SpecInvalid, source, "Each definition spec must be an object."));
                    if (element.TryGetProperty("tags", out var declaredTags) && (declaredTags.ValueKind != JsonValueKind.Array || declaredTags.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String)))
                        diagnostics.Add(new(PackDiagnosticCodes.JsonInvalid, source, "Definition tags must be an array of strings."));
                    if (element.TryGetProperty("performance", out var declaredPerformance) && declaredPerformance.ValueKind != JsonValueKind.Object)
                        diagnostics.Add(new(PackDiagnosticCodes.JsonInvalid, source, "Definition performance must be an object."));
                    var id = ReadString(element, "id", diagnostics, source, required: true) ?? string.Empty;
                    var kind = ReadString(element, "kind", diagnostics, source, required: true) ?? string.Empty;
                    var defVersion = ReadString(element, "version", diagnostics, source, required: false) ?? version;
                    var defName = ReadString(element, "name", diagnostics, source, required: true) ?? id;
                    var defDescription = ReadString(element, "description", diagnostics, source, required: false);
                    var accent = ReadString(element, "previewAccent", diagnostics, source, required: false);
                    var tags = element.TryGetProperty("tags", out var tagArray) && tagArray.ValueKind == JsonValueKind.Array
                        ? tagArray.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToArray()
                        : [];
                    var performance = DefinitionPerformance.Static;
                    if (element.TryGetProperty("performance", out var perf) && perf.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var field in new[] { "tier", "offscreenPolicy" })
                            if (perf.TryGetProperty(field, out var text) && text.ValueKind != JsonValueKind.String)
                                diagnostics.Add(new(PackDiagnosticCodes.JsonInvalid, id, $"'performance.{field}' must be a string."));
                        if (perf.TryGetProperty("continuousAnimation", out var animation) && animation.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            diagnostics.Add(new(PackDiagnosticCodes.JsonInvalid, id, "'performance.continuousAnimation' must be a boolean."));
                        if (perf.TryGetProperty("maxAnimatedLayers", out var declaredLayers)
                            && (declaredLayers.ValueKind != JsonValueKind.Number || !declaredLayers.TryGetInt32(out _)))
                            diagnostics.Add(new(PackDiagnosticCodes.JsonInvalid, id, "'performance.maxAnimatedLayers' must be an integer."));
                        performance = new DefinitionPerformance(
                            perf.TryGetProperty("tier", out var tier) && tier.ValueKind == JsonValueKind.String ? tier.GetString()! : "utility",
                            perf.TryGetProperty("continuousAnimation", out var continuous) && continuous.ValueKind == JsonValueKind.True,
                            perf.TryGetProperty("offscreenPolicy", out var offscreen) && offscreen.ValueKind == JsonValueKind.String ? offscreen.GetString()! : "suspend",
                            perf.TryGetProperty("maxAnimatedLayers", out var layers) && layers.ValueKind == JsonValueKind.Number && layers.TryGetInt32(out var count) ? count : 0);
                    }

                    var spec = element.TryGetProperty("spec", out var specElement) && specElement.ValueKind == JsonValueKind.Object
                        ? specElement.GetRawText()
                        : "{}";

                    definitions.Add(new PresentationDefinition(id, kind, defVersion, defName, defDescription, tags, performance, spec, accent));
                }
            }

            var manifest = new PresentationPackManifest(schema, packId, version, name, author, description, license, minContract, assets, definitions);
            return diagnostics.Any(d => d.IsError) ? new(null, diagnostics) : new(manifest, diagnostics);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            diagnostics.Add(new(PackDiagnosticCodes.JsonInvalid, source, exception.Message));
            return new(null, diagnostics);
        }
    }

    private static string? ReadString(JsonElement element, string name, List<PackDiagnostic> diagnostics, string source, bool required)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (required && string.IsNullOrWhiteSpace(text))
                diagnostics.Add(new(PackDiagnosticCodes.FieldMissing, source, $"'{name}' cannot be empty."));
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        if (element.TryGetProperty(name, out var invalid) && invalid.ValueKind != JsonValueKind.String && invalid.ValueKind != JsonValueKind.Null)
            diagnostics.Add(new(PackDiagnosticCodes.JsonInvalid, source, $"'{name}' must be a string."));
        if (required)
        {
            diagnostics.Add(new(PackDiagnosticCodes.FieldMissing, source, $"'{name}' is required."));
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, string name, List<PackDiagnostic> diagnostics, string source)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        diagnostics.Add(new(PackDiagnosticCodes.FieldMissing, source, $"'{name}' must be an integer."));
        return null;
    }
}

/// <summary>
/// Fail-closed validation of a Presentation Pack. A pack is data and assets
/// only: executables, scripts, XAML, SQL and shaders are rejected outright.
/// </summary>
public static partial class PackValidator
{
    private static readonly IReadOnlySet<string> RetiredDefinitionKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "motion-preset",
        "icon-pack",
    };

    public const int MaxDefinitions = 256;
    public const int MaxAssets = 256;
    public const int MaxFiles = 600;
    public const long MaxPackBytes = 640L * 1024 * 1024;

    public static IReadOnlyDictionary<PackAssetKind, long> MaxAssetBytes { get; } = new Dictionary<PackAssetKind, long>
    {
        [PackAssetKind.Image] = 40L * 1024 * 1024,
        [PackAssetKind.Texture] = 16L * 1024 * 1024,
        [PackAssetKind.Video] = 320L * 1024 * 1024,
        [PackAssetKind.Font] = 16L * 1024 * 1024,
        [PackAssetKind.Lottie] = 4L * 1024 * 1024,
    };

    public static IReadOnlyDictionary<PackAssetKind, string[]> AllowedExtensions { get; } = new Dictionary<PackAssetKind, string[]>
    {
        [PackAssetKind.Image] = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"],
        [PackAssetKind.Texture] = [".png", ".jpg", ".jpeg", ".webp"],
        [PackAssetKind.Video] = [".mp4", ".m4v", ".webm", ".mov"],
        [PackAssetKind.Font] = [".ttf", ".otf"],
        [PackAssetKind.Lottie] = [".json"],
    };

    /// <summary>Content that must never ship inside a pack, regardless of what the manifest claims.</summary>
    public static IReadOnlySet<string> ForbiddenExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".com", ".scr", ".msi", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".js", ".mjs", ".ts",
        ".jar", ".py", ".sh", ".xaml", ".sql", ".hlsl", ".glsl", ".fx", ".sksl", ".wasm", ".lnk", ".url", ".reg", ".cs",
    };

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && IdPattern().IsMatch(id);

    public static IReadOnlyList<PackDiagnostic> Validate(PresentationPackManifest manifest, string? packRoot, bool isBuiltIn)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var diagnostics = new List<PackDiagnostic>();

        if (manifest.SchemaVersion != PresentationContract.PackSchemaVersion)
        {
            diagnostics.Add(new(PackDiagnosticCodes.SchemaUnsupported, manifest.PackId,
                $"Pack schema {manifest.SchemaVersion} is not supported (this build reads {PresentationContract.PackSchemaVersion})."));
        }

        if (manifest.MinContractVersion > PresentationContract.Version)
        {
            diagnostics.Add(new(PackDiagnosticCodes.ContractTooNew, manifest.PackId,
                $"The pack needs Presentation Contract {manifest.MinContractVersion}; this build implements {PresentationContract.Version}."));
        }

        if (!IsValidId(manifest.PackId))
        {
            diagnostics.Add(new(PackDiagnosticCodes.IdInvalid, manifest.PackId, "Pack ids use lowercase letters, digits, '.' and '-'."));
        }

        if (!isBuiltIn && manifest.PackId.StartsWith("builtin.", StringComparison.Ordinal))
        {
            diagnostics.Add(new(PackDiagnosticCodes.IdReserved, manifest.PackId, "The 'builtin.' prefix is reserved for application packs."));
        }

        if (manifest.Definitions.Count > MaxDefinitions)
        {
            diagnostics.Add(new(PackDiagnosticCodes.TooManyEntries, manifest.PackId, $"At most {MaxDefinitions} definitions are allowed."));
        }

        if (manifest.Assets.Count > MaxAssets)
        {
            diagnostics.Add(new(PackDiagnosticCodes.TooManyEntries, manifest.PackId, $"At most {MaxAssets} assets are allowed."));
        }

        var seenDefinitions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in manifest.Definitions)
        {
            if (!IsValidId(definition.Id) || !definition.Id.StartsWith(manifest.PackId + ".", StringComparison.Ordinal))
            {
                diagnostics.Add(new(PackDiagnosticCodes.DefinitionIdInvalid, definition.Id,
                    $"Definition ids must be valid and start with the pack id '{manifest.PackId}.'."));
            }

            if (!seenDefinitions.Add(definition.Id))
            {
                diagnostics.Add(new(PackDiagnosticCodes.DefinitionDuplicate, definition.Id, "The definition id appears more than once."));
            }

            if (!DefinitionKinds.IsKnown(definition.Kind))
            {
                diagnostics.Add(RetiredDefinitionKinds.Contains(definition.Kind)
                    ? new PackDiagnostic(
                        PackDiagnosticCodes.KindRetired,
                        definition.Id,
                        $"Definition kind '{definition.Kind}' was retired and is ignored.",
                        IsError: false)
                    : new PackDiagnostic(
                        PackDiagnosticCodes.KindUnknown,
                        definition.Id,
                        $"Kind '{definition.Kind}' is not supported by this build."));
            }
        }

        List<string>? packFiles = null;
        if (packRoot is not null && Directory.Exists(packRoot))
        {
            try { packFiles = EnumerateFilesWithoutLinks(packRoot).Take(MaxFiles + 1).ToList(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                diagnostics.Add(new(PackDiagnosticCodes.AssetPathInvalid, manifest.PackId, exception.Message));
                return diagnostics;
            }
        }

        var seenAssets = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var asset in manifest.Assets)
        {
            if (!IsValidId(asset.Id) || !seenAssets.Add(asset.Id))
            {
                diagnostics.Add(new(PackDiagnosticCodes.MediaIdDuplicate, asset.Id, "Asset ids must be valid and unique."));
                continue;
            }

            if (!IsSafeRelativePath(asset.Path))
            {
                diagnostics.Add(new(PackDiagnosticCodes.AssetPathInvalid, asset.Id, "Asset paths must be relative, inside the pack, without '..'."));
                continue;
            }

            var extension = Path.GetExtension(asset.Path);
            if (!AllowedExtensions.TryGetValue(asset.Kind, out var extensions) || !extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                diagnostics.Add(new(PackDiagnosticCodes.AssetTypeInvalid, asset.Id, $"'{extension}' is not an allowed {asset.Kind} file."));
                continue;
            }

            if (packRoot is null)
            {
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(packRoot, asset.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(Path.GetFullPath(packRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new(PackDiagnosticCodes.AssetPathInvalid, asset.Id, "The asset path escapes the pack."));
                continue;
            }

            var info = new FileInfo(full);
            if (!info.Exists)
            {
                diagnostics.Add(new(PackDiagnosticCodes.AssetMissing, asset.Id, $"'{asset.Path}' is missing."));
                continue;
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                diagnostics.Add(new(PackDiagnosticCodes.AssetPathInvalid, asset.Id, "Links and reparse points are not allowed in packs."));
                continue;
            }

            totalBytes += info.Length;
            if (info.Length > MaxAssetBytes[asset.Kind] || (asset.DeclaredBytes is { } declared && info.Length > declared))
            {
                diagnostics.Add(new(PackDiagnosticCodes.AssetTooLarge, asset.Id, $"'{asset.Path}' exceeds the allowed size for {asset.Kind}."));
                continue;
            }

            if (!ProbeAsset(asset.Kind, full, out var reason))
            {
                diagnostics.Add(new(PackDiagnosticCodes.AssetUnreadable, asset.Id, reason));
            }
        }

        if (packRoot is not null && Directory.Exists(packRoot))
        {
            var files = packFiles!;
            if (files.Count > MaxFiles)
            {
                diagnostics.Add(new(PackDiagnosticCodes.TooManyEntries, manifest.PackId, $"A pack may contain at most {MaxFiles} files."));
            }

            foreach (var file in files)
            {
                if (ForbiddenExtensions.Contains(Path.GetExtension(file)))
                {
                    diagnostics.Add(new(PackDiagnosticCodes.ForbiddenContent, Path.GetFileName(file), "Executable, script, XAML, SQL and shader files are never allowed in a pack."));
                }
            }

            if (totalBytes > MaxPackBytes)
            {
                diagnostics.Add(new(PackDiagnosticCodes.PackTooLarge, manifest.PackId, "The pack's assets exceed the total size limit."));
            }
        }

        return diagnostics;
    }

internal static IEnumerable<string> EnumerateFilesWithoutLinks(string root)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        var entries = 0;
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Packs cannot contain directory links or reparse points.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > MaxFiles * 2) throw new InvalidDataException("The pack contains too many filesystem entries.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Packs cannot contain links or reparse points.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260)
        {
            return false;
        }

        if (Path.IsPathRooted(path) || path.Contains(':') || path.StartsWith('/') || path.StartsWith('\\'))
        {
            return false;
        }

        var segments = path.Replace('\\', '/').Split('/');
        return segments.All(segment => segment.Length > 0 && segment != "." && segment != ".."
            && !segment.EndsWith('.') && !segment.EndsWith(' ') && segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !IsReservedFileName(segment));
    }

    private static bool IsReservedFileName(string segment)
    {
        var stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9';
    }

    private static bool ProbeAsset(PackAssetKind kind, string fullPath, out string reason)
    {
        reason = string.Empty;
        try
        {
            switch (kind)
            {
                case PackAssetKind.Image:
                case PackAssetKind.Texture:
                    using (var codec = SKCodec.Create(fullPath))
                    {
                        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || codec.Info.Width > 16384 || codec.Info.Height > 16384)
                        {
                            reason = "The image cannot be decoded or has unsupported dimensions.";
                            return false;
                        }
                        var decoded = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                        if ((long)decoded.Width * decoded.Height * 4 > 64L * 1024 * 1024)
                        {
                            reason = "The image exceeds the 64 MiB decoded pixel limit.";
                            return false;
                        }
                        using var bitmap = new SKBitmap(decoded);
                        if (codec.GetPixels(decoded, bitmap.GetPixels()) != SKCodecResult.Success)
                        {
                            reason = "The image pixels cannot be completely decoded.";
                            return false;
                        }
                    }

                    return true;
                case PackAssetKind.Font:
                    using (var typeface = SKTypeface.FromFile(fullPath))
                    {
                        if (typeface is null || string.IsNullOrWhiteSpace(typeface.FamilyName))
                        {
                            reason = "The font file is not a readable TrueType/OpenType font.";
                            return false;
                        }
                    }

                    return true;
                case PackAssetKind.Lottie:
                    using (var stream = File.OpenRead(fullPath))
                    using (var document = JsonDocument.Parse(stream))
                    {
                        var root = document.RootElement;
                        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out _) || !root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
                        {
                            reason = "The animation is not a valid Lottie document.";
                            return false;
                        }

                        if (root.GetRawText().Contains("\"expression\"", StringComparison.OrdinalIgnoreCase) || root.GetRawText().Contains("\"x\":\"", StringComparison.Ordinal))
                        {
                            reason = "Lottie expressions are code and are not allowed.";
                            return false;
                        }
                    }

                    return true;
                case PackAssetKind.Video:
                    using (var stream = File.OpenRead(fullPath))
                    {
                        Span<byte> header = stackalloc byte[12];
                        if (stream.Read(header) < 12)
                        {
                            reason = "The video file is truncated.";
                            return false;
                        }

                        var isMp4 = header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p';
                        var isWebm = header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3;
                        if (!isMp4 && !isWebm)
                        {
                            reason = "The video container is not MP4/MOV or WebM.";
                            return false;
                        }
                    }

                    return true;
                default:
                    reason = "Unsupported asset kind.";
                    return false;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            reason = exception.Message;
            return false;
        }
    }
}
