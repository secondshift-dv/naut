using System.IO;
using System.Text;
using System.Text.Json;
using Neuterradise.App.Design.CoverFrames;

namespace Neuterradise.App.Design.ProfileLayouts;

public static class ProfileLayoutDiagnosticCodes
{
    public const string JsonInvalid = "LAYOUT_JSON_INVALID";
    public const string RootNotObject = "LAYOUT_ROOT_NOT_OBJECT";
    public const string UnknownField = "LAYOUT_UNKNOWN_FIELD";
    public const string FieldMissing = "LAYOUT_FIELD_MISSING";
    public const string FieldTypeInvalid = "LAYOUT_FIELD_TYPE_INVALID";
    public const string SchemaVersionUnsupported = "LAYOUT_SCHEMA_VERSION_UNSUPPORTED";
    public const string IdInvalid = "LAYOUT_ID_INVALID";
    public const string NameInvalid = "LAYOUT_NAME_INVALID";
    public const string EnumValueUnknown = "LAYOUT_ENUM_VALUE_UNKNOWN";
    public const string ModuleIdUnknown = "LAYOUT_MODULE_ID_UNKNOWN";
    public const string ModuleDuplicate = "LAYOUT_MODULE_DUPLICATE";
    public const string ModulesEmpty = "LAYOUT_MODULES_EMPTY";
    public const string ModuleOrderOutOfRange = "LAYOUT_MODULE_ORDER_OUT_OF_RANGE";
    public const string RequestedUnavailable = "LAYOUT_REQUESTED_UNAVAILABLE";
    public const string FallbackApplied = "LAYOUT_FALLBACK_APPLIED";
}

public sealed record ProfileLayoutDiagnostic(string Code, string Source, string Detail);

public sealed record ProfileLayoutParseResult(
    ProfileLayoutDefinition? Definition,
    IReadOnlyList<ProfileLayoutDiagnostic> Diagnostics)
{

    public bool IsValid => Definition is not null;
}

public sealed record ProfileLayoutSelection(
    ProfileLayoutDefinition Definition,
    bool RequestedApplied,
    IReadOnlyList<ProfileLayoutDiagnostic> Diagnostics);

public sealed class ProfileLayoutResolver
{

    public const int SupportedSchemaVersion = 1;

    public const string FallbackPresetId = "plain";

    public const int MaximumTextLength = 64;

    public const int MinimumModuleOrder = 0;

    public const int MaximumModuleOrder = 1000;

    private static readonly string[] RootProperties =
    [
        "schemaVersion", "id", "name",
        "bannerMode", "height", "coverPlacement", "coverSize", "coverDetailLevel",
        "identityAlignment", "contentWidth", "overlay", "blur", "fade",
        "sectionDensity", "modules",
    ];

    private static readonly string[] ModuleProperties = ["id", "order", "visible", "width"];

    private static readonly (ProfileLayoutModuleId Id, string Wire)[] ModuleWireIds =
    [
        (ProfileLayoutModuleId.Overview, "overview"),
        (ProfileLayoutModuleId.Related, "related"),
        (ProfileLayoutModuleId.Faces, "faces"),
        (ProfileLayoutModuleId.RecentMedia, "recentMedia"),
        (ProfileLayoutModuleId.Media, "media"),
    ];

    private static readonly ProfileLayoutDefinition[] Presets = BuildPresets();

    public static IReadOnlyList<ProfileLayoutDefinition> BuiltInPresets { get; } = Presets;

    public static string ToWireId(ProfileLayoutModuleId id) =>
        Array.Find(ModuleWireIds, entry => entry.Id == id).Wire;

    public static bool IsBuiltInPresetId(string? id) => TryGetBuiltInPreset(id, out _);

    public static bool TryGetBuiltInPreset(string? id, out ProfileLayoutDefinition definition)
    {
        if (id is not null)
        {
            foreach (var preset in Presets)
            {
                if (string.Equals(preset.Id, id, StringComparison.Ordinal))
                {
                    definition = preset;
                    return true;
                }
            }
        }

        definition = Presets[0];
        return false;
    }

    public ProfileLayoutSelection Resolve(
        string? globalDefaultId,
        string? perProfileOverrideId,
        IReadOnlyList<ProfileLayoutDefinition>? customDefinitions = null)
    {
        var diagnostics = new List<ProfileLayoutDiagnostic>();

        if (!string.IsNullOrWhiteSpace(perProfileOverrideId))
        {
            if (TryFind(perProfileOverrideId, customDefinitions, out var overridden))
            {
                return new ProfileLayoutSelection(overridden, RequestedApplied: true, diagnostics);
            }

            diagnostics.Add(new ProfileLayoutDiagnostic(
                ProfileLayoutDiagnosticCodes.RequestedUnavailable,
                perProfileOverrideId,
                "The per-Profile layout override is missing or failed validation."));
        }

        if (!string.IsNullOrWhiteSpace(globalDefaultId))
        {
            if (TryFind(globalDefaultId, customDefinitions, out var global))
            {

                var requestedApplied = string.IsNullOrWhiteSpace(perProfileOverrideId);

                if (!requestedApplied)
                {
                    diagnostics.Add(new ProfileLayoutDiagnostic(
                        ProfileLayoutDiagnosticCodes.FallbackApplied,
                        global.Id,
                        "Fell back to the global default Profile Layout preset."));
                }

                return new ProfileLayoutSelection(global, requestedApplied, diagnostics);
            }

            diagnostics.Add(new ProfileLayoutDiagnostic(
                ProfileLayoutDiagnosticCodes.RequestedUnavailable,
                globalDefaultId,
                "The global default Profile Layout preset is missing or failed validation."));
        }

        diagnostics.Add(new ProfileLayoutDiagnostic(
            ProfileLayoutDiagnosticCodes.FallbackApplied,
            FallbackPresetId,
            "Fell back to the Plain built-in preset."));

        var applied = string.IsNullOrWhiteSpace(perProfileOverrideId)
            && string.IsNullOrWhiteSpace(globalDefaultId);

        TryGetBuiltInPreset(FallbackPresetId, out var fallback);
        return new ProfileLayoutSelection(fallback, applied, diagnostics);
    }

    public ProfileLayoutParseResult ParseDefinition(string json, string source)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var diagnostics = new List<ProfileLayoutDiagnostic>();
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
        }
        catch (JsonException ex)
        {
            diagnostics.Add(new ProfileLayoutDiagnostic(ProfileLayoutDiagnosticCodes.JsonInvalid, source, ex.Message));
            return new ProfileLayoutParseResult(null, diagnostics);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new ProfileLayoutDiagnostic(
                    ProfileLayoutDiagnosticCodes.RootNotObject,
                    source,
                    $"A layout document must be a JSON object; found {root.ValueKind}."));

                return new ProfileLayoutParseResult(null, diagnostics);
            }

            if (!HasExactProperties(root, RootProperties, source, "layout", diagnostics)
                | !TryReadInt32(root, "schemaVersion", source, diagnostics, out var schemaVersion)
                | !TryReadString(root, "id", source, diagnostics, out var id)
                | !TryReadString(root, "name", source, diagnostics, out var name)
                | !TryReadEnum<ProfileLayoutBannerMode>(root, "bannerMode", source, diagnostics, out var bannerMode)
                | !TryReadEnum<ProfileLayoutHeight>(root, "height", source, diagnostics, out var height)
                | !TryReadEnum<ProfileLayoutCoverPlacement>(root, "coverPlacement", source, diagnostics, out var coverPlacement)
                | !TryReadEnum<ProfileLayoutCoverSize>(root, "coverSize", source, diagnostics, out var coverSize)
                | !TryReadEnum<CoverFrameDetailLevel>(root, "coverDetailLevel", source, diagnostics, out var coverDetailLevel)
                | !TryReadEnum<ProfileLayoutIdentityAlignment>(root, "identityAlignment", source, diagnostics, out var identityAlignment)
                | !TryReadEnum<ProfileLayoutContentWidth>(root, "contentWidth", source, diagnostics, out var contentWidth)
                | !TryReadEnum<ProfileLayoutOverlay>(root, "overlay", source, diagnostics, out var overlay)
                | !TryReadEnum<ProfileLayoutBannerBlur>(root, "blur", source, diagnostics, out var blur)
                | !TryReadEnum<ProfileLayoutFade>(root, "fade", source, diagnostics, out var fade)
                | !TryReadEnum<ProfileLayoutSectionDensity>(root, "sectionDensity", source, diagnostics, out var density)
                | !TryReadModules(root, source, diagnostics, out var modules))
            {
                return new ProfileLayoutParseResult(null, diagnostics);
            }

            if (schemaVersion != SupportedSchemaVersion)
            {
                diagnostics.Add(new ProfileLayoutDiagnostic(
                    ProfileLayoutDiagnosticCodes.SchemaVersionUnsupported,
                    source,
                    $"schemaVersion {schemaVersion} is not supported; this build understands {SupportedSchemaVersion}."));
            }

            if (!IsValidId(id))
            {
                diagnostics.Add(new ProfileLayoutDiagnostic(
                    ProfileLayoutDiagnosticCodes.IdInvalid,
                    source,
                    "id must be 1 to 64 characters of lowercase letters, digits, or hyphen, starting with a letter or digit."));
            }

            if (!IsValidDisplayName(name))
            {
                diagnostics.Add(new ProfileLayoutDiagnostic(
                    ProfileLayoutDiagnosticCodes.NameInvalid,
                    source,
                    "name must be 1 to 64 printable characters and may not contain markup."));
            }

            if (diagnostics.Any(diagnostic => diagnostic.Code != ProfileLayoutDiagnosticCodes.ModuleIdUnknown))
            {
                return new ProfileLayoutParseResult(null, diagnostics);
            }

            var definition = new ProfileLayoutDefinition(
                schemaVersion,
                id,
                name.Trim(),
                bannerMode,
                height,
                coverPlacement,
                coverSize,
                coverDetailLevel,
                identityAlignment,
                contentWidth,
                overlay,
                blur,
                fade,
                density,
                modules!);

            return new ProfileLayoutParseResult(definition, diagnostics);
        }
    }

    public static string WriteDefinition(ProfileLayoutDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", definition.SchemaVersion);
            writer.WriteString("id", definition.Id);
            writer.WriteString("name", definition.Name);

            writer.WriteString("bannerMode", definition.BannerMode.ToString());
            writer.WriteString("height", definition.Height.ToString());
            writer.WriteString("coverPlacement", definition.CoverPlacement.ToString());
            writer.WriteString("coverSize", definition.CoverSize.ToString());
            writer.WriteString("coverDetailLevel", definition.CoverDetailLevel.ToString());
            writer.WriteString("identityAlignment", definition.IdentityAlignment.ToString());
            writer.WriteString("contentWidth", definition.ContentWidth.ToString());
            writer.WriteString("overlay", definition.Overlay.ToString());
            writer.WriteString("blur", definition.Blur.ToString());
            writer.WriteString("fade", definition.Fade.ToString());

            writer.WriteString("sectionDensity", definition.SectionDensity.ToString());

            writer.WriteStartArray("modules");

            foreach (var module in definition.Modules)
            {
                writer.WriteStartObject();
                writer.WriteString("id", ToWireId(module.Id));
                writer.WriteNumber("order", module.Order);
                writer.WriteBoolean("visible", module.Visible);
                writer.WriteString("width", module.Width.ToString());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool TryFind(
        string? id,
        IReadOnlyList<ProfileLayoutDefinition>? customDefinitions,
        out ProfileLayoutDefinition definition)
    {
        if (TryGetBuiltInPreset(id, out definition))
        {
            return true;
        }

        if (customDefinitions is not null && id is not null)
        {
            foreach (var candidate in customDefinitions)
            {
                if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
                {
                    definition = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasExactProperties(
        JsonElement element,
        string[] required,
        string source,
        string scope,
        List<ProfileLayoutDiagnostic> diagnostics)
    {
        var valid = true;
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (!required.Contains(property.Name, StringComparer.Ordinal))
            {
                diagnostics.Add(new ProfileLayoutDiagnostic(
                    ProfileLayoutDiagnosticCodes.UnknownField,
                    source,
                    $"'{property.Name}' is not part of the schema v1 {scope} object."));

                valid = false;
                continue;
            }

            if (!present.Add(property.Name))
            {
                diagnostics.Add(new ProfileLayoutDiagnostic(
                    ProfileLayoutDiagnosticCodes.UnknownField,
                    source,
                    $"'{property.Name}' is declared more than once in the {scope} object."));

                valid = false;
            }
        }

        foreach (var name in required)
        {
            if (!present.Contains(name))
            {
                diagnostics.Add(new ProfileLayoutDiagnostic(
                    ProfileLayoutDiagnosticCodes.FieldMissing,
                    source,
                    $"'{name}' is required by the schema v1 {scope} object."));

                valid = false;
            }
        }

        return valid;
    }

    private static bool TryReadModules(
        JsonElement root,
        string source,
        List<ProfileLayoutDiagnostic> diagnostics,
        out IReadOnlyList<ProfileLayoutModule>? modules)
    {
        modules = null;

        if (!root.TryGetProperty("modules", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new ProfileLayoutDiagnostic(
                ProfileLayoutDiagnosticCodes.FieldTypeInvalid,
                source,
                "'modules' must be a JSON array."));

            return false;
        }

        var parsed = new List<ProfileLayoutModule>();
        var seen = new HashSet<ProfileLayoutModuleId>();
        var valid = true;

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new ProfileLayoutDiagnostic(
                    ProfileLayoutDiagnosticCodes.FieldTypeInvalid,
                    source,
                    "Every entry in 'modules' must be a JSON object."));

                valid = false;
                continue;
            }

            if (!HasExactProperties(element, ModuleProperties, source, "module", diagnostics))
            {
                valid = false;
                continue;
            }

            if (!TryReadString(element, "id", source, diagnostics, out var wireId)
                | !TryReadInt32(element, "order", source, diagnostics, out var order)
                | !TryReadBoolean(element, "visible", source, diagnostics, out var visible)
                | !TryReadEnum<ProfileLayoutModuleWidth>(element, "width", source, diagnostics, out var width))
            {
                valid = false;
                continue;
            }

            var match = Array.FindIndex(ModuleWireIds, entry => string.Equals(entry.Wire, wireId, StringComparison.Ordinal));

            if (match < 0)
            {

                diagnostics.Add(new ProfileLayoutDiagnostic(
                    ProfileLayoutDiagnosticCodes.ModuleIdUnknown,
                    wireId,
                    $"'{wireId}' is not a module this build renders; the entry is ignored."));

                continue;
            }

            if (order < MinimumModuleOrder || order > MaximumModuleOrder)
            {
                diagnostics.Add(new ProfileLayoutDiagnostic(
                    ProfileLayoutDiagnosticCodes.ModuleOrderOutOfRange,
                    wireId,
                    $"order must be between {MinimumModuleOrder} and {MaximumModuleOrder}."));

                valid = false;
                continue;
            }

            var id = ModuleWireIds[match].Id;

            if (!seen.Add(id))
            {
                diagnostics.Add(new ProfileLayoutDiagnostic(
                    ProfileLayoutDiagnosticCodes.ModuleDuplicate,
                    wireId,
                    $"'{wireId}' is listed more than once."));

                valid = false;
                continue;
            }

            parsed.Add(new ProfileLayoutModule(id, order, visible, width));
        }

        if (parsed.Count == 0)
        {
            diagnostics.Add(new ProfileLayoutDiagnostic(
                ProfileLayoutDiagnosticCodes.ModulesEmpty,
                source,
                "A layout must list at least one module this build renders."));

            valid = false;
        }

        if (!valid)
        {
            return false;
        }
        var overviewIndex = parsed.FindIndex(module => module.Id == ProfileLayoutModuleId.Overview);
        if (overviewIndex >= 0)
        {
            parsed[overviewIndex] = parsed[overviewIndex] with { Visible = true, Width = ProfileLayoutModuleWidth.Full };
        }
        else
        {
            parsed.Add(new ProfileLayoutModule(
                ProfileLayoutModuleId.Overview,
                parsed.Count == 0 ? 10 : Math.Max(MinimumModuleOrder, parsed.Min(module => module.Order) - 10),
                true,
                ProfileLayoutModuleWidth.Full));
        }

        parsed.Sort((left, right) => left.Order.CompareTo(right.Order));
        modules = parsed;
        return true;
    }

    private static bool TryReadEnum<TEnum>(
        JsonElement parent,
        string name,
        string source,
        List<ProfileLayoutDiagnostic> diagnostics,
        out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;

        if (!TryReadString(parent, name, source, diagnostics, out var text))
        {
            return false;
        }

        if (Enum.TryParse(text, ignoreCase: false, out value) && Enum.IsDefined(value))
        {
            return true;
        }

        diagnostics.Add(new ProfileLayoutDiagnostic(
            ProfileLayoutDiagnosticCodes.EnumValueUnknown,
            source,
            $"'{text}' is not a valid {name} value."));

        value = default;
        return false;
    }

    private static bool TryReadString(
        JsonElement parent,
        string name,
        string source,
        List<ProfileLayoutDiagnostic> diagnostics,
        out string value)
    {
        if (parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        diagnostics.Add(new ProfileLayoutDiagnostic(
            ProfileLayoutDiagnosticCodes.FieldTypeInvalid,
            source,
            $"'{name}' must be a JSON string."));

        value = string.Empty;
        return false;
    }

    private static bool TryReadBoolean(
        JsonElement parent,
        string name,
        string source,
        List<ProfileLayoutDiagnostic> diagnostics,
        out bool value)
    {
        if (parent.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        diagnostics.Add(new ProfileLayoutDiagnostic(
            ProfileLayoutDiagnosticCodes.FieldTypeInvalid,
            source,
            $"'{name}' must be a JSON boolean."));

        value = false;
        return false;
    }

    private static bool TryReadInt32(
        JsonElement parent,
        string name,
        string source,
        List<ProfileLayoutDiagnostic> diagnostics,
        out int value)
    {
        if (parent.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value))
        {
            return true;
        }

        diagnostics.Add(new ProfileLayoutDiagnostic(
            ProfileLayoutDiagnosticCodes.FieldTypeInvalid,
            source,
            $"'{name}' must be a whole JSON number."));

        value = 0;
        return false;
    }

    private static bool IsValidId(string id)
    {
        if (id.Length is 0 or > MaximumTextLength)
        {
            return false;
        }

        if (!char.IsAsciiLetterLower(id[0]) && !char.IsAsciiDigit(id[0]))
        {
            return false;
        }

        foreach (var character in id)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidDisplayName(string name)
    {
        var trimmed = name.Trim();

        if (trimmed.Length is 0 or > MaximumTextLength)
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (char.IsControl(character) || character is '<' or '>' or '{' or '}')
            {
                return false;
            }
        }

        return true;
    }

    private static ProfileLayoutDefinition[] BuildPresets()
    {
        return
        [
            Preset(
                "cinematic", "Cinematic",
                    ProfileLayoutBannerMode.FullBleed, ProfileLayoutHeight.Tall,
                    ProfileLayoutCoverPlacement.BottomLeftOverlap, ProfileLayoutCoverSize.Large,
                    CoverFrameDetailLevel.Full, ProfileLayoutIdentityAlignment.Left,
                    ProfileLayoutContentWidth.Wide, ProfileLayoutOverlay.Medium,
                    ProfileLayoutBannerBlur.Off, ProfileLayoutFade.Bottom,
                ProfileLayoutSectionDensity.Comfortable,
                Modules(
                    (ProfileLayoutModuleId.Overview, 10, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Media, 20, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Faces, 30, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.Related, 40, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.RecentMedia, 50, false, ProfileLayoutModuleWidth.Full))),

            Preset(
                "cinematic-wide", "Center Stage",
                    ProfileLayoutBannerMode.FullBleed, ProfileLayoutHeight.Tall,
                    ProfileLayoutCoverPlacement.BottomCenterOverlap, ProfileLayoutCoverSize.Large,
                    CoverFrameDetailLevel.Full, ProfileLayoutIdentityAlignment.Center,
                    ProfileLayoutContentWidth.Wide, ProfileLayoutOverlay.Medium,
                    ProfileLayoutBannerBlur.Off, ProfileLayoutFade.Bottom,
                ProfileLayoutSectionDensity.Comfortable,
                Modules(
                    (ProfileLayoutModuleId.Overview, 10, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.RecentMedia, 20, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Related, 30, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.Faces, 40, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.Media, 50, true, ProfileLayoutModuleWidth.Full))),

            Preset(
                "immersive", "Immersive",
                    ProfileLayoutBannerMode.FullBleed, ProfileLayoutHeight.Tall,
                    ProfileLayoutCoverPlacement.InlineCenter, ProfileLayoutCoverSize.ExtraLarge,
                    CoverFrameDetailLevel.Full, ProfileLayoutIdentityAlignment.Center,
                    ProfileLayoutContentWidth.Wide, ProfileLayoutOverlay.Strong,
                    ProfileLayoutBannerBlur.Subtle, ProfileLayoutFade.Full,
                ProfileLayoutSectionDensity.Spacious,
                Modules(
                    (ProfileLayoutModuleId.Media, 10, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Overview, 20, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.RecentMedia, 30, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Related, 40, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.Faces, 50, true, ProfileLayoutModuleWidth.Half))),

            Preset(
                "editorial", "Editorial",
                    ProfileLayoutBannerMode.Contained, ProfileLayoutHeight.Medium,
                    ProfileLayoutCoverPlacement.Left, ProfileLayoutCoverSize.Medium,
                    CoverFrameDetailLevel.Full, ProfileLayoutIdentityAlignment.Left,
                    ProfileLayoutContentWidth.Normal, ProfileLayoutOverlay.Soft,
                    ProfileLayoutBannerBlur.Off, ProfileLayoutFade.None,
                ProfileLayoutSectionDensity.Comfortable,
                Modules(
                    (ProfileLayoutModuleId.Overview, 10, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Faces, 20, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.Related, 30, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.RecentMedia, 40, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Media, 50, true, ProfileLayoutModuleWidth.Full))),

            Preset(
                "split-left", "Split View",
                    ProfileLayoutBannerMode.Split, ProfileLayoutHeight.Medium,
                    ProfileLayoutCoverPlacement.Left, ProfileLayoutCoverSize.Large,
                    CoverFrameDetailLevel.Full, ProfileLayoutIdentityAlignment.Left,
                    ProfileLayoutContentWidth.Normal, ProfileLayoutOverlay.Soft,
                    ProfileLayoutBannerBlur.Off, ProfileLayoutFade.None,
                ProfileLayoutSectionDensity.Comfortable,
                Modules(
                    (ProfileLayoutModuleId.Overview, 10, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Related, 20, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.Faces, 30, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.RecentMedia, 40, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Media, 50, true, ProfileLayoutModuleWidth.Full))),

            Preset(
                "split-right", "Split Right",
                    ProfileLayoutBannerMode.Split, ProfileLayoutHeight.Medium,
                    ProfileLayoutCoverPlacement.Right, ProfileLayoutCoverSize.Large,
                    CoverFrameDetailLevel.Full, ProfileLayoutIdentityAlignment.Right,
                    ProfileLayoutContentWidth.Normal, ProfileLayoutOverlay.Soft,
                    ProfileLayoutBannerBlur.Off, ProfileLayoutFade.None,
                ProfileLayoutSectionDensity.Comfortable,
                Modules(
                    (ProfileLayoutModuleId.Overview, 10, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Related, 20, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.Faces, 30, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.RecentMedia, 40, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Media, 50, true, ProfileLayoutModuleWidth.Full))),

            Preset(
                "profile-focus", "Portrait Focus",
                    ProfileLayoutBannerMode.None, ProfileLayoutHeight.Low,
                    ProfileLayoutCoverPlacement.InlineLeft, ProfileLayoutCoverSize.Large,
                    CoverFrameDetailLevel.Full, ProfileLayoutIdentityAlignment.Left,
                    ProfileLayoutContentWidth.Normal, ProfileLayoutOverlay.Soft,
                    ProfileLayoutBannerBlur.Off, ProfileLayoutFade.None,
                ProfileLayoutSectionDensity.Comfortable,
                Modules(
                    (ProfileLayoutModuleId.Overview, 10, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Faces, 20, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.Related, 30, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.RecentMedia, 40, false, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Media, 50, true, ProfileLayoutModuleWidth.Full))),

            Preset(
                "showcase", "Media First",
                    ProfileLayoutBannerMode.Backdrop, ProfileLayoutHeight.Medium,
                    ProfileLayoutCoverPlacement.InlineCenter, ProfileLayoutCoverSize.Large,
                    CoverFrameDetailLevel.Full, ProfileLayoutIdentityAlignment.Center,
                    ProfileLayoutContentWidth.Wide, ProfileLayoutOverlay.Medium,
                    ProfileLayoutBannerBlur.Off, ProfileLayoutFade.Bottom,
                ProfileLayoutSectionDensity.Spacious,
                Modules(
                    (ProfileLayoutModuleId.Media, 10, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Overview, 20, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Related, 30, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.Faces, 40, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.RecentMedia, 50, true, ProfileLayoutModuleWidth.Full))),

            Preset(
                "gallery-hero", "Gallery Hero",
                    ProfileLayoutBannerMode.FullBleed, ProfileLayoutHeight.Medium,
                    ProfileLayoutCoverPlacement.BottomLeftOverlap, ProfileLayoutCoverSize.Medium,
                    CoverFrameDetailLevel.Full, ProfileLayoutIdentityAlignment.Left,
                    ProfileLayoutContentWidth.Wide, ProfileLayoutOverlay.Medium,
                    ProfileLayoutBannerBlur.Off, ProfileLayoutFade.Bottom,
                ProfileLayoutSectionDensity.Comfortable,
                Modules(
                    (ProfileLayoutModuleId.Media, 10, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.RecentMedia, 20, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Overview, 30, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Related, 40, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.Faces, 50, true, ProfileLayoutModuleWidth.Half))),

            Preset(
                "compact-hero", "Compact Hero",
                    ProfileLayoutBannerMode.Contained, ProfileLayoutHeight.Low,
                    ProfileLayoutCoverPlacement.InlineLeft, ProfileLayoutCoverSize.Small,
                    CoverFrameDetailLevel.Compact, ProfileLayoutIdentityAlignment.Left,
                    ProfileLayoutContentWidth.Normal, ProfileLayoutOverlay.Soft,
                    ProfileLayoutBannerBlur.Off, ProfileLayoutFade.None,
                ProfileLayoutSectionDensity.Compact,
                Modules(
                    (ProfileLayoutModuleId.Overview, 10, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Media, 20, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Related, 30, true, ProfileLayoutModuleWidth.Third),
                    (ProfileLayoutModuleId.Faces, 40, true, ProfileLayoutModuleWidth.Third),
                    (ProfileLayoutModuleId.RecentMedia, 50, true, ProfileLayoutModuleWidth.Third))),

            Preset(
                "minimal", "Minimal",
                    ProfileLayoutBannerMode.None, ProfileLayoutHeight.Low,
                    ProfileLayoutCoverPlacement.InlineLeft, ProfileLayoutCoverSize.Small,
                    CoverFrameDetailLevel.Hidden, ProfileLayoutIdentityAlignment.Left,
                    ProfileLayoutContentWidth.Normal, ProfileLayoutOverlay.Soft,
                    ProfileLayoutBannerBlur.Off, ProfileLayoutFade.None,
                ProfileLayoutSectionDensity.Compact,
                Modules(
                    (ProfileLayoutModuleId.Overview, 10, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Media, 20, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Related, 30, false, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.Faces, 40, false, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.RecentMedia, 50, false, ProfileLayoutModuleWidth.Full))),

            Preset(
                "prestige", "Prestige",
                    ProfileLayoutBannerMode.Backdrop, ProfileLayoutHeight.Tall,
                    ProfileLayoutCoverPlacement.BottomCenterOverlap, ProfileLayoutCoverSize.ExtraLarge,
                    CoverFrameDetailLevel.Full, ProfileLayoutIdentityAlignment.Center,
                    ProfileLayoutContentWidth.Wide, ProfileLayoutOverlay.Strong,
                    ProfileLayoutBannerBlur.Subtle, ProfileLayoutFade.Full,
                ProfileLayoutSectionDensity.Spacious,
                Modules(
                    (ProfileLayoutModuleId.Overview, 10, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Faces, 20, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.Related, 30, true, ProfileLayoutModuleWidth.Half),
                    (ProfileLayoutModuleId.RecentMedia, 40, true, ProfileLayoutModuleWidth.Full),
                    (ProfileLayoutModuleId.Media, 50, true, ProfileLayoutModuleWidth.Full))),
        ];
    }

    private static ProfileLayoutDefinition Preset(
        string id,
        string name,
        ProfileLayoutBannerMode bannerMode,
        ProfileLayoutHeight height,
        ProfileLayoutCoverPlacement coverPlacement,
        ProfileLayoutCoverSize coverSize,
        CoverFrameDetailLevel coverDetailLevel,
        ProfileLayoutIdentityAlignment identityAlignment,
        ProfileLayoutContentWidth contentWidth,
        ProfileLayoutOverlay overlay,
        ProfileLayoutBannerBlur blur,
        ProfileLayoutFade fade,
        ProfileLayoutSectionDensity density,
        IReadOnlyList<ProfileLayoutModule> modules) =>
        new(
            SupportedSchemaVersion,
            id,
            name,
            bannerMode,
            height,
            coverPlacement,
            coverSize,
            coverDetailLevel,
            identityAlignment,
            contentWidth,
            overlay,
            blur,
            fade,
            density,
            modules);

    private static ProfileLayoutModule[] Modules(
        params (ProfileLayoutModuleId Id, int Order, bool Visible, ProfileLayoutModuleWidth Width)[] modules)
    {
        var result = modules
            .Select(module => new ProfileLayoutModule(module.Id, module.Order, module.Visible, module.Width))
            .ToList();
        var overviewIndex = result.FindIndex(module => module.Id == ProfileLayoutModuleId.Overview);
        if (overviewIndex >= 0)
        {
            result[overviewIndex] = result[overviewIndex] with { Visible = true, Width = ProfileLayoutModuleWidth.Full };
        }
        else
        {
            result.Add(new ProfileLayoutModule(
                ProfileLayoutModuleId.Overview,
                result.Count == 0 ? 10 : Math.Max(MinimumModuleOrder, result.Min(module => module.Order) - 10),
                true,
                ProfileLayoutModuleWidth.Full));
        }

        result.Sort((left, right) => left.Order.CompareTo(right.Order));
        return [.. result];
    }
}
