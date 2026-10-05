using System.IO;
using System.Text.Json;

namespace Neuterradise.App.Design.Themes;

public static class ThemeDiagnosticCodes
{
    public const string JsonInvalid = "THEME_JSON_INVALID";
    public const string RootNotObject = "THEME_ROOT_NOT_OBJECT";
    public const string UnknownField = "THEME_UNKNOWN_FIELD";
    public const string FieldMissing = "THEME_FIELD_MISSING";
    public const string FieldTypeInvalid = "THEME_FIELD_TYPE_INVALID";
    public const string SchemaVersionUnsupported = "THEME_SCHEMA_VERSION_UNSUPPORTED";
    public const string IdInvalid = "THEME_ID_INVALID";
    public const string IdReserved = "THEME_ID_RESERVED";
    public const string IdDuplicate = "THEME_ID_DUPLICATE";
    public const string NameInvalid = "THEME_NAME_INVALID";
    public const string ColorInvalid = "THEME_COLOR_INVALID";
    public const string FontFamilyInvalid = "THEME_FONT_FAMILY_INVALID";
    public const string ShapeOutOfRange = "THEME_SHAPE_OUT_OF_RANGE";
    public const string MotionOutOfRange = "THEME_MOTION_OUT_OF_RANGE";
    public const string BackgroundKindInvalid = "THEME_BACKGROUND_KIND_INVALID";
    public const string MorphologyInvalid = "THEME_MORPHOLOGY_INVALID";
    public const string FileUnreadable = "THEME_FILE_UNREADABLE";
    public const string DirectoryUnreadable = "THEME_DIRECTORY_UNREADABLE";
    public const string BuiltInUnavailable = "THEME_BUILT_IN_UNAVAILABLE";
}

public sealed record ThemeDiagnostic(string Code, string Source, string Detail);

public sealed record ThemeLoadResult(ThemeDefinition? Theme, IReadOnlyList<ThemeDiagnostic> Diagnostics)
{

    public bool IsValid => Theme is not null;
}

public sealed record ThemeCatalog(
    IReadOnlyList<ThemeDefinition> Themes,
    IReadOnlyList<ThemeDiagnostic> Diagnostics);

public sealed class ThemeLoader
{

    public const int SupportedSchemaVersion = 1;

    public const double MinimumShapeValue = 0;

    public const double MaximumShapeValue = 48;

    public const int MinimumMotionMilliseconds = 0;

    public const int MaximumMotionMilliseconds = 2000;

    public const int MaximumTextLength = 64;

    private const string BackgroundKindRadialGlow = "RadialGlow";

    private static readonly string[] RootProperties =
    [
        "schemaVersion", "id", "name", "isDark", "colors", "typography", "shape", "motion", "background",
    ];

    /// <summary>
    /// Root sections a theme may declare but is not required to. A theme that omits "morphology"
    /// keeps the neutral shipped morphology, so themes authored before morphology support still load.
    /// </summary>
    private static readonly string[] OptionalRootProperties = ["morphology"];

    private static readonly string[] MorphologyProperties =
    [
        "navigationTreatment", "cardPersonality", "chromeTreatment", "focusTreatment",
        "borderWeight", "elevationStrength", "glowStrength", "panelOpacity",
        "motionIntensity", "navigationRadiusScale",
    ];

    private static readonly string[] ColorProperties =
    [
        "canvas", "surface1", "surface2", "surface3", "surfaceElevated", "surfaceOverlay",
        "surfaceHover", "surfacePressed", "surfaceSelected",
        "textPrimary", "textSecondary", "textMuted", "textDisabled", "textInverse", "textOnAccent", "textLink",
        "borderSubtle", "borderDefault", "borderStrong", "borderInteractive", "borderSelected", "focus",
        "accent", "accentHover", "accentPressed", "accentSecondary", "selection", "selectionHover",
        "info", "success", "warning", "danger", "dangerHover",
    ];

    private static readonly string[] TypographyProperties =
    [
        "displayFamily", "headingFamily", "bodyFamily", "monoFamily",
    ];

    private static readonly string[] ShapeProperties = ["xs", "sm", "md", "lg", "xl"];

    private static readonly string[] MotionProperties = ["fastMs", "normalMs", "slowMs", "cinematicMs"];

    private static readonly string[] BackgroundProperties = ["kind"];

    private static readonly (string Id, string FileName)[] BuiltInThemeFiles =
    [
        ("abyss", "Abyss.json"),
        ("fresh", "Fresh.json"),
        ("brass", "Brass.json"),
        ("neon", "Neon.json"),
        ("ivory", "Ivory.json"),
        ("inkwell", "Inkwell.json"),
        ("pulse", "Pulse.json"),
        ("contract", "Contract.json"),
    ];

    public static IReadOnlyList<string> BuiltInThemeIds { get; } =
        BuiltInThemeFiles.Select(theme => theme.Id).ToArray();

    public static bool IsBuiltInThemeId(string? id) =>
        id is not null && BuiltInThemeIds.Contains(id, StringComparer.Ordinal);

    public ThemeLoadResult LoadBuiltInTheme(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var match = Array.Find(BuiltInThemeFiles, theme => theme.Id == id);
        if (match == default)
        {
            return new ThemeLoadResult(
                null,
                [new ThemeDiagnostic(ThemeDiagnosticCodes.BuiltInUnavailable, id, "No built-in theme has that id.")]);
        }

        var fileName = match.FileName;

        string json;

        try
        {

            // Built-in themes are embedded in the runtime assembly; they are deployment payload, never Vault data.
            var resource = typeof(ThemeLoader).Assembly.GetManifestResourceStream($"Neuterradise.App.Design.Themes.{fileName}");

            if (resource is null)
            {
                return new ThemeLoadResult(
                    null,
                    [new ThemeDiagnostic(ThemeDiagnosticCodes.BuiltInUnavailable, id, $"'{fileName}' is not embedded in the assembly.")]);
            }

            using var stream = resource;
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or InvalidOperationException)
        {
            return new ThemeLoadResult(
                null,
                [new ThemeDiagnostic(ThemeDiagnosticCodes.BuiltInUnavailable, id, ex.GetType().Name)]);
        }

        return ParseTheme(json, fileName);
    }

    public ThemeCatalog LoadBuiltInThemes()
    {
        var themes = new List<ThemeDefinition>(BuiltInThemeFiles.Length);
        var diagnostics = new List<ThemeDiagnostic>();

        foreach (var (id, _) in BuiltInThemeFiles)
        {
            var result = LoadBuiltInTheme(id);
            diagnostics.AddRange(result.Diagnostics);

            if (result.Theme is not null)
            {
                themes.Add(result.Theme);
            }
        }

        return new ThemeCatalog(themes, diagnostics);
    }

    public ThemeLoadResult ParseTheme(string json, string source)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var diagnostics = new List<ThemeDiagnostic>();
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
            diagnostics.Add(new ThemeDiagnostic(ThemeDiagnosticCodes.JsonInvalid, source, ex.Message));
            return new ThemeLoadResult(null, diagnostics);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new ThemeDiagnostic(
                    ThemeDiagnosticCodes.RootNotObject,
                    source,
                    $"A theme document must be a JSON object; found {root.ValueKind}."));

                return new ThemeLoadResult(null, diagnostics);
            }

            if (!HasExactProperties(root, RootProperties, source, "theme", diagnostics, OptionalRootProperties)
                | !TryReadInt32(root, "schemaVersion", source, diagnostics, out var schemaVersion)
                | !TryReadString(root, "id", source, diagnostics, out var id)
                | !TryReadString(root, "name", source, diagnostics, out var name)
                | !TryReadBoolean(root, "isDark", source, diagnostics, out var isDark)
                | !TryReadColors(root, source, diagnostics, out var colors)
                | !TryReadTypography(root, source, diagnostics, out var typography)
                | !TryReadShape(root, source, diagnostics, out var shape)
                | !TryReadMotion(root, source, diagnostics, out var motion)
                | !TryReadBackground(root, source, diagnostics, out var background)
                | !TryReadMorphology(root, source, diagnostics, out var morphology))
            {
                return new ThemeLoadResult(null, diagnostics);
            }

            if (schemaVersion != SupportedSchemaVersion)
            {
                diagnostics.Add(new ThemeDiagnostic(
                    ThemeDiagnosticCodes.SchemaVersionUnsupported,
                    source,
                    $"schemaVersion {schemaVersion} is not supported; this build understands {SupportedSchemaVersion}."));
            }

            if (!IsValidThemeId(id))
            {
                diagnostics.Add(new ThemeDiagnostic(
                    ThemeDiagnosticCodes.IdInvalid,
                    source,
                    "id must be 1 to 64 characters of lowercase letters, digits, or hyphen, starting with a letter or digit."));
            }

            if (!IsValidDisplayName(name))
            {
                diagnostics.Add(new ThemeDiagnostic(
                    ThemeDiagnosticCodes.NameInvalid,
                    source,
                    "name must be 1 to 64 printable characters and may not contain markup."));
            }

            if (diagnostics.Count > 0)
            {
                return new ThemeLoadResult(null, diagnostics);
            }

            var theme = new ThemeDefinition(
                schemaVersion,
                id,
                name.Trim(),
                isDark,
                colors!,
                typography!,
                shape!,
                motion!,
                background!,
                morphology);

            return new ThemeLoadResult(theme, diagnostics);
        }
    }

    private static bool HasExactProperties(
        JsonElement element,
        string[] required,
        string source,
        string scope,
        List<ThemeDiagnostic> diagnostics,
        string[]? optional = null)
    {
        var valid = true;
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (!required.Contains(property.Name, StringComparer.Ordinal)
                && !(optional?.Contains(property.Name, StringComparer.Ordinal) ?? false))
            {

                diagnostics.Add(new ThemeDiagnostic(
                    ThemeDiagnosticCodes.UnknownField,
                    source,
                    $"'{property.Name}' is not part of the schema v1 {scope} object."));

                valid = false;
                continue;
            }

            if (!present.Add(property.Name))
            {
                diagnostics.Add(new ThemeDiagnostic(
                    ThemeDiagnosticCodes.UnknownField,
                    source,
                    $"'{property.Name}' is declared more than once in the {scope} object."));

                valid = false;
            }
        }

        foreach (var name in required)
        {
            if (!present.Contains(name))
            {
                diagnostics.Add(new ThemeDiagnostic(
                    ThemeDiagnosticCodes.FieldMissing,
                    source,
                    $"'{name}' is required by the schema v1 {scope} object."));

                valid = false;
            }
        }

        return valid;
    }

    private static bool TryReadObject(
        JsonElement parent,
        string name,
        string[] required,
        string source,
        List<ThemeDiagnostic> diagnostics,
        out JsonElement value)
    {
        value = default;

        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new ThemeDiagnostic(
                ThemeDiagnosticCodes.FieldTypeInvalid,
                source,
                $"'{name}' must be a JSON object."));

            return false;
        }

        if (!HasExactProperties(element, required, source, name, diagnostics))
        {
            return false;
        }

        value = element;
        return true;
    }

    private static bool TryReadString(
        JsonElement parent,
        string name,
        string source,
        List<ThemeDiagnostic> diagnostics,
        out string value)
    {
        if (parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        diagnostics.Add(new ThemeDiagnostic(
            ThemeDiagnosticCodes.FieldTypeInvalid,
            source,
            $"'{name}' must be a JSON string."));

        value = string.Empty;
        return false;
    }

    private static bool TryReadBoolean(
        JsonElement parent,
        string name,
        string source,
        List<ThemeDiagnostic> diagnostics,
        out bool value)
    {
        if (parent.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        diagnostics.Add(new ThemeDiagnostic(
            ThemeDiagnosticCodes.FieldTypeInvalid,
            source,
            $"'{name}' must be a JSON boolean."));

        value = false;
        return false;
    }

    private static bool TryReadInt32(
        JsonElement parent,
        string name,
        string source,
        List<ThemeDiagnostic> diagnostics,
        out int value)
    {
        if (parent.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value))
        {
            return true;
        }

        diagnostics.Add(new ThemeDiagnostic(
            ThemeDiagnosticCodes.FieldTypeInvalid,
            source,
            $"'{name}' must be a whole JSON number."));

        value = 0;
        return false;
    }

    private static bool TryReadDouble(
        JsonElement parent,
        string name,
        string source,
        List<ThemeDiagnostic> diagnostics,
        out double value)
    {
        if (parent.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value)
            && double.IsFinite(value))
        {
            return true;
        }

        diagnostics.Add(new ThemeDiagnostic(
            ThemeDiagnosticCodes.FieldTypeInvalid,
            source,
            $"'{name}' must be a finite JSON number."));

        value = 0;
        return false;
    }

    private static bool TryReadColors(
        JsonElement root,
        string source,
        List<ThemeDiagnostic> diagnostics,
        out ThemeColors? colors)
    {
        colors = null;

        if (!TryReadObject(root, "colors", ColorProperties, source, diagnostics, out var element))
        {
            return false;
        }

        var values = new string[ColorProperties.Length];
        var valid = true;

        for (var index = 0; index < ColorProperties.Length; index++)
        {
            var name = ColorProperties[index];

            if (!TryReadString(element, name, source, diagnostics, out var text))
            {
                valid = false;
                continue;
            }

            if (!ThemeColorText.IsValid(text))
            {
                diagnostics.Add(new ThemeDiagnostic(
                    ThemeDiagnosticCodes.ColorInvalid,
                    source,
                    $"colors.{name} must be an #AARRGGBB value."));

                valid = false;
                continue;
            }

            values[index] = text;
        }

        if (!valid)
        {
            return false;
        }

        colors = new ThemeColors(
            values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15],
            values[16], values[17], values[18], values[19], values[20], values[21], values[22], values[23],
            values[24], values[25], values[26], values[27], values[28], values[29], values[30], values[31],
            values[32]);

        return true;
    }

    private static bool TryReadTypography(
        JsonElement root,
        string source,
        List<ThemeDiagnostic> diagnostics,
        out ThemeTypography? typography)
    {
        typography = null;

        if (!TryReadObject(root, "typography", TypographyProperties, source, diagnostics, out var element))
        {
            return false;
        }

        var values = new string[TypographyProperties.Length];
        var valid = true;

        for (var index = 0; index < TypographyProperties.Length; index++)
        {
            var name = TypographyProperties[index];

            if (!TryReadString(element, name, source, diagnostics, out var family))
            {
                valid = false;
                continue;
            }

            if (!IsSafeFontFamily(family))
            {
                diagnostics.Add(new ThemeDiagnostic(
                    ThemeDiagnosticCodes.FontFamilyInvalid,
                    source,
                    $"typography.{name} must be a plain installed family name. A WPF font URI, path, or resource "
                    + "reference is rejected because a theme may not point at arbitrary content."));

                valid = false;
                continue;
            }

            values[index] = family.Trim();
        }

        if (!valid)
        {
            return false;
        }

        typography = new ThemeTypography(values[0], values[1], values[2], values[3]);
        return true;
    }

    private static bool TryReadShape(
        JsonElement root,
        string source,
        List<ThemeDiagnostic> diagnostics,
        out ThemeShape? shape)
    {
        shape = null;

        if (!TryReadObject(root, "shape", ShapeProperties, source, diagnostics, out var element))
        {
            return false;
        }

        var values = new double[ShapeProperties.Length];
        var valid = true;

        for (var index = 0; index < ShapeProperties.Length; index++)
        {
            var name = ShapeProperties[index];

            if (!TryReadDouble(element, name, source, diagnostics, out var value))
            {
                valid = false;
                continue;
            }

            if (value < MinimumShapeValue || value > MaximumShapeValue)
            {
                diagnostics.Add(new ThemeDiagnostic(
                    ThemeDiagnosticCodes.ShapeOutOfRange,
                    source,
                    $"shape.{name} must be between {MinimumShapeValue} and {MaximumShapeValue}."));

                valid = false;
                continue;
            }

            values[index] = value;
        }

        if (!valid)
        {
            return false;
        }

        shape = new ThemeShape(values[0], values[1], values[2], values[3], values[4]);
        return true;
    }

    private static bool TryReadMotion(
        JsonElement root,
        string source,
        List<ThemeDiagnostic> diagnostics,
        out ThemeMotion? motion)
    {
        motion = null;

        if (!TryReadObject(root, "motion", MotionProperties, source, diagnostics, out var element))
        {
            return false;
        }

        var values = new int[MotionProperties.Length];
        var valid = true;

        for (var index = 0; index < MotionProperties.Length; index++)
        {
            var name = MotionProperties[index];

            if (!TryReadInt32(element, name, source, diagnostics, out var value))
            {
                valid = false;
                continue;
            }

            if (value < MinimumMotionMilliseconds || value > MaximumMotionMilliseconds)
            {
                diagnostics.Add(new ThemeDiagnostic(
                    ThemeDiagnosticCodes.MotionOutOfRange,
                    source,
                    $"motion.{name} must be between {MinimumMotionMilliseconds} and {MaximumMotionMilliseconds} milliseconds."));

                valid = false;
                continue;
            }

            values[index] = value;
        }

        if (!valid)
        {
            return false;
        }

        motion = new ThemeMotion(values[0], values[1], values[2], values[3]);
        return true;
    }

    private static bool TryReadBackground(
        JsonElement root,
        string source,
        List<ThemeDiagnostic> diagnostics,
        out ThemeBackground? background)
    {
        background = null;

        if (!TryReadObject(root, "background", BackgroundProperties, source, diagnostics, out var element))
        {
            return false;
        }

        if (!TryReadString(element, "kind", source, diagnostics, out var kind))
        {
            return false;
        }

        if (!string.Equals(kind, BackgroundKindRadialGlow, StringComparison.Ordinal))
        {
            diagnostics.Add(new ThemeDiagnostic(
                ThemeDiagnosticCodes.BackgroundKindInvalid,
                source,
                $"background.kind must be exactly '{BackgroundKindRadialGlow}'; schema v1 defines no other kind."));

            return false;
        }

        background = new ThemeBackground(ThemeBackgroundKind.RadialGlow);
        return true;
    }

    /// <summary>
    /// Reads the optional morphology block. An absent block is success with a null result
    /// (the neutral morphology then applies); a present but malformed block is a hard validation
    /// failure so a broken custom theme can never silently render as something else.
    /// </summary>
    private static bool TryReadMorphology(
        JsonElement root,
        string source,
        List<ThemeDiagnostic> diagnostics,
        out ThemeMorphology? morphology)
    {
        morphology = null;

        if (!root.TryGetProperty("morphology", out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new ThemeDiagnostic(
                ThemeDiagnosticCodes.MorphologyInvalid,
                source,
                "'morphology' must be a JSON object when it is present."));
            return false;
        }

        if (!HasExactProperties(element, MorphologyProperties, source, "morphology", diagnostics))
        {
            return false;
        }

        if (!TryReadEnum(element, "navigationTreatment", source, diagnostics, out ThemeNavigationTreatment navigation)
            | !TryReadEnum(element, "cardPersonality", source, diagnostics, out ThemeCardPersonality card)
            | !TryReadEnum(element, "chromeTreatment", source, diagnostics, out ThemeChromeTreatment chrome)
            | !TryReadEnum(element, "focusTreatment", source, diagnostics, out ThemeFocusTreatment focus)
            | !TryReadRatio(element, "borderWeight", 0.5, 4.0, source, diagnostics, out var borderWeight)
            | !TryReadRatio(element, "elevationStrength", 0.0, 2.5, source, diagnostics, out var elevation)
            | !TryReadRatio(element, "glowStrength", 0.0, 2.5, source, diagnostics, out var glow)
            | !TryReadRatio(element, "panelOpacity", 0.5, 1.0, source, diagnostics, out var panelOpacity)
            | !TryReadRatio(element, "motionIntensity", 0.0, 2.0, source, diagnostics, out var motionIntensity)
            | !TryReadRatio(element, "navigationRadiusScale", 0.0, 3.0, source, diagnostics, out var navRadius))
        {
            return false;
        }

        morphology = new ThemeMorphology(
            navigation,
            card,
            chrome,
            focus,
            borderWeight,
            elevation,
            glow,
            panelOpacity,
            motionIntensity,
            navRadius);

        return true;
    }

    private static bool TryReadEnum<TEnum>(
        JsonElement parent,
        string name,
        string source,
        List<ThemeDiagnostic> diagnostics,
        out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;

        if (!TryReadString(parent, name, source, diagnostics, out var text))
        {
            return false;
        }

        if (Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value))
        {
            return true;
        }

        diagnostics.Add(new ThemeDiagnostic(
            ThemeDiagnosticCodes.MorphologyInvalid,
            source,
            $"'{text}' is not a valid {name} value."));

        return false;
    }

    private static bool TryReadRatio(
        JsonElement parent,
        string name,
        double minimum,
        double maximum,
        string source,
        List<ThemeDiagnostic> diagnostics,
        out double value)
    {
        if (!TryReadDouble(parent, name, source, diagnostics, out value))
        {
            return false;
        }

        if (value < minimum || value > maximum)
        {
            diagnostics.Add(new ThemeDiagnostic(
                ThemeDiagnosticCodes.MorphologyInvalid,
                source,
                $"'{name}' must be between {minimum} and {maximum}."));

            return false;
        }

        return true;
    }

    private static bool IsValidThemeId(string id)
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

    private static bool IsSafeFontFamily(string family)
    {
        var trimmed = family.Trim();

        if (trimmed.Length is 0 or > MaximumTextLength)
        {
            return false;
        }

        foreach (var part in trimmed.Split(','))
        {
            var candidate = part.Trim();

            if (candidate.Length == 0)
            {
                return false;
            }

            foreach (var character in candidate)
            {
                if (char.IsControl(character))
                {
                    return false;
                }

                if (character is '#' or '/' or '\\' or ':' or '%' or '<' or '>' or '{' or '}' or '?' or '*' or '"')
                {
                    return false;
                }
            }
        }

        return true;
    }
}
