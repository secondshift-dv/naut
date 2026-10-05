using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Neuterradise.App.Design.Themes;

namespace Neuterradise.App.Presentation;

/// <summary>Every token value for one theme + motion state, already derived.</summary>
public sealed record ResolvedTokenSet(
    string ThemeId,
    bool IsDark,
    IReadOnlyDictionary<string, ArgbColor> Colors,
    IReadOnlyDictionary<string, double> Numbers,
    IReadOnlyDictionary<string, string> Strings)
{
    public ArgbColor Color(string key) => Colors.TryGetValue(key, out var color) ? color
        : TokenAuthority.LegacyColorAliases.TryGetValue(key, out var active) && Colors.TryGetValue(active, out color) ? color : default;

    public double Number(string key, double fallback = 0) => Numbers.TryGetValue(key, out var value) ? value : fallback;

    public string Text(string key, string fallback = "") => Strings.TryGetValue(key, out var value) ? value : fallback;

    /// <summary>Resolves a colour reference ("token:x" or "#AARRGGBB"); unknown references yield transparent.</summary>
    public ArgbColor Resolve(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return default;
        }

        if (reference.StartsWith("token:", StringComparison.Ordinal))
        {
            return Color(reference[6..]);
        }

        return TokenAuthority.TryParseLiteral(reference, out var literal) ? literal : default;
    }

    public double ResolveRadius(string? reference, double fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return fallback;
        }

        if (reference.StartsWith("token:", StringComparison.Ordinal))
        {
            return Number(reference[6..], fallback);
        }

        return double.TryParse(reference, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }
}

public sealed record FoundationTokens(
    IReadOnlyDictionary<string, double> Space,
    IReadOnlyDictionary<string, double> Sizing,
    IReadOnlyDictionary<string, double> Radius,
    IReadOnlyDictionary<string, double> Opacity,
    IReadOnlyDictionary<string, ArgbColor> Scrims,
    double CardLiftScale,
    double PageTranslate,
    IReadOnlyDictionary<string, double[]> Easing,
    double FocusThickness,
    double FocusOffset,
    double FocusRadius,
    IReadOnlyList<(string Name, double Blur, double Depth, double Opacity)> Elevation,
    IReadOnlyDictionary<string, double> Density,
    string FallbackUi,
    string FallbackMono,
    IReadOnlyDictionary<string, TypeStyle> TypeStyles);

/// <summary>
/// THE canonical semantic token authority (document 01 §30). Theme JSON + foundation.tokens.json +
/// material personality → one resolved token set. The Uno resource projection, Presentation defaults
/// and the exported design CSS (via <see cref="ExportJson"/>) all derive from here; no other generator
/// re-implements this math.
/// </summary>
public static class TokenAuthority
{
    private static readonly Lazy<FoundationTokens> LazyFoundations = new(LoadFoundations);

    public static FoundationTokens Foundations => LazyFoundations.Value;

    /// <summary>Colour tokens a definition may reference as "token:name".</summary>
    public static IReadOnlySet<string> ColorTokenNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "canvas", "surface1", "surface2", "surface3", "surfaceElevated", "surfaceOverlay", "surfaceHover", "surfacePressed", "surfaceSelected",
        "textPrimary", "textSecondary", "textMuted", "textDisabled", "textInverse", "textOnAccent", "textLink",
        "textAccent", "textInfo", "textSuccess", "textWarning", "textDanger",
        "signatureGold", "signatureFill",
        "borderSubtle", "borderDefault", "borderStrong", "borderInteractive", "borderSelected", "focus",
        "accent", "accentHover", "accentPressed", "accentSecondary", "selection", "selectionHover",
        "info", "success", "warning", "danger", "dangerHover",
        "scrimSoft", "scrimMedium", "scrimStrong", "mediaOverlay", "mediaOverlayHover",
        "onMediaPrimary", "onMediaSecondary", "onHeroPrimary", "onHeroSecondary", "heroScrimStrong", "heroScrimSoft",
        "backgroundBase", "backgroundCinematic", "surfaceRaised",
        "glassFill", "glassBorder", "glassHighlight", "glassDeepFill", "glassStrip",
        "lightAmbient", "lightRim", "lightSpecular", "lightHalo", "lightBloom", "auraPrimary", "auraSecondary",
        "borderFocus", "borderEnergy",
        "chromeBackground", "chromeForeground", "chromeHover", "chromeCloseHover",
        "navHoverBackground", "navSelectedBackground", "navIndicator",
        "ambientPrimary", "ambientSecondary",
    };

    internal static IReadOnlyDictionary<string, string> LegacyColorAliases { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["backgroundBase"] = "canvas",
        ["surfaceRaised"] = "surfaceElevated",
        ["lightHalo"] = "ambientPrimary",
        ["lightBloom"] = "accent",
        ["auraPrimary"] = "ambientPrimary",
        ["auraSecondary"] = "ambientSecondary",
        ["borderFocus"] = "focus",
        ["chromeForeground"] = "textPrimary",
        ["chromeHover"] = "surfaceHover",
        ["chromeCloseHover"] = "dangerHover",
    };

    public static bool IsRadiusToken(string text) =>
        text.StartsWith("token:radius", StringComparison.Ordinal);

    public static bool IsValidColorReference(string text) =>
        text.StartsWith("token:", StringComparison.Ordinal)
            ? ColorTokenNames.Contains(text[6..])
            : TryParseLiteral(text, out _);

    public static bool TryParseLiteral(string text, out ArgbColor color)
    {
        color = default;
        if (text.Length == 7 && text[0] == '#')
        {
            return ThemeColorText.TryParse("#FF" + text[1..], out color);
        }

        return ThemeColorText.TryParse(text, out color);
    }

    /// <summary>Resolves every token for a theme. Cheap: runs on theme/accessibility change, never per frame.</summary>
    public static ResolvedTokenSet Resolve(ThemeDefinition theme, MaterialSpec material, bool reducedMotion)
    {
        ArgumentNullException.ThrowIfNull(theme);
        material ??= MaterialSpec.Neutral;
        var f = Foundations;
        var morphology = theme.EffectiveMorphology;
        var colors = new Dictionary<string, ArgbColor>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, double>(StringComparer.Ordinal);
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);

        // Theme colour roles.
        foreach (var property in typeof(ThemeColors).GetProperties())
        {
            var value = (string)property.GetValue(theme.Colors)!;
            colors[char.ToLowerInvariant(property.Name[0]) + property.Name[1..]] = ThemeColorText.Parse(value);
        }

        foreach (var (name, scrim) in f.Scrims)
        {
            colors[name] = scrim;
        }

        var canvas = colors["canvas"];
        var accent = colors["accent"];
        var accentSecondary = colors["accentSecondary"];
        var textPrimary = colors["textPrimary"];
        var isDark = theme.IsDark;
        var textBackgrounds = new[]
        {
            canvas,
            colors["surface1"],
            colors["surface2"],
            colors["surface3"],
            colors["surfaceOverlay"],
        };
        colors["textAccent"] = ReadableText(accent, textPrimary, textBackgrounds);
        colors["textInfo"] = ReadableText(colors["info"], textPrimary, textBackgrounds);
        colors["textSuccess"] = ReadableText(colors["success"], textPrimary, textBackgrounds);
        colors["textWarning"] = ReadableText(colors["warning"], textPrimary, textBackgrounds);
        colors["textDanger"] = ReadableText(colors["danger"], textPrimary, textBackgrounds);

        // Product signature accent. This is deliberately independent from the selectable theme accent;
        // it identifies the rare Customize CTA without repurposing warning/status colors.
        colors["signatureGold"] = isDark
            ? ArgbColor.FromArgb(0xFF, 0xE7, 0xC9, 0x8F)
            : ArgbColor.FromArgb(0xFF, 0x7A, 0x5A, 0x24);
        colors["signatureFill"] = ArgbColor.FromArgb(isDark ? (byte)0x26 : (byte)0x20, 0xE7, 0xC9, 0x8F);

        // semantic media / hero tokens: text over media never borrows ordinary Text.Primary.
        colors["onMediaPrimary"] = ArgbColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        colors["onMediaSecondary"] = ArgbColor.FromArgb(0xD9, 0xF2, 0xF0, 0xF6);
        colors["onHeroPrimary"] = isDark ? ArgbColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : ArgbColor.FromArgb(0xFF, 0xFF, 0xFD, 0xF8);
        colors["onHeroSecondary"] = ArgbColor.FromArgb(0xCC, 0xF2, 0xEE, 0xF8);
        var scrimBase = ArgbColor.Lerp(ArgbColor.FromArgb(0xFF, 0, 0, 0), canvas, isDark ? 0.08 : 0);
        colors["heroScrimStrong"] = scrimBase.WithAlpha(isDark ? (byte)0xE0 : (byte)0xC8);
        colors["heroScrimSoft"] = scrimBase.WithAlpha(isDark ? (byte)0x70 : (byte)0x58);

        colors["backgroundCinematic"] = ArgbColor.Lerp(canvas, accentSecondary, isDark ? 0.18 : 0.08);

        // Glass material family  — tint and opacity follow the theme's material personality.
        var panelOpacity = Math.Clamp(morphology.PanelOpacity * material.GlassOpacity, 0.5, 1);
        var glassBase = isDark ? colors["surface2"] : colors["surface1"];
        colors["glassFill"] = glassBase.WithAlpha((byte)Math.Round(255 * panelOpacity * 0.82));
        colors["glassDeepFill"] = ArgbColor.Lerp(glassBase, ArgbColor.FromArgb(0xFF, 0, 0, 0), isDark ? 0.35 : 0.08).WithAlpha((byte)Math.Round(255 * Math.Min(1, panelOpacity * 0.94)));
        colors["glassStrip"] = ArgbColor.Lerp(glassBase, canvas, 0.3).WithAlpha(0xB8);
        colors["glassBorder"] = ArgbColor.Lerp(textPrimary, accent, 0.25).WithAlpha(isDark ? (byte)0x33 : (byte)0x40);
        colors["glassHighlight"] = ArgbColor.FromArgb((byte)Math.Round(255 * (isDark ? 0.10 : 0.55) * (0.5 + material.Specular)), 0xFF, 0xFF, 0xFF);

        // Light / aura .
        var ambientPrimary = ResolveMaterialColor(material.AmbientPrimary, colors, accent);
        var ambientSecondary = ResolveMaterialColor(material.AmbientSecondary, colors, accentSecondary);
        colors["ambientPrimary"] = ambientPrimary;
        colors["ambientSecondary"] = ambientSecondary;
        colors["lightAmbient"] = ambientPrimary.WithAlpha(isDark ? (byte)0x55 : (byte)0x30);
        colors["lightRim"] = ArgbColor.Lerp(ambientPrimary, ArgbColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.45).WithAlpha((byte)Math.Round(255 * Math.Clamp(0.25 + material.RimLight * 0.6, 0, 1)));
        colors["lightSpecular"] = ArgbColor.FromArgb((byte)Math.Round(255 * Math.Clamp(material.Specular * 0.7, 0, 1)), 0xFF, 0xFF, 0xFF);

        colors["borderEnergy"] = ArgbColor.Lerp(accent, colors["focus"], 0.4);

        // Window chrome : chrome controls stay visible in every theme.
        colors["chromeBackground"] = colors["surface1"].WithAlpha(isDark ? (byte)0xE6 : (byte)0xF2);

        // Navigation treatment.
        colors["navHoverBackground"] = accent.WithAlpha(isDark ? (byte)0x14 : (byte)0x0E);
        var selectedSurface = morphology.NavigationTreatment is ThemeNavigationTreatment.SelectedSurface or ThemeNavigationTreatment.Glow;
        colors["navSelectedBackground"] = selectedSurface ? colors["surfaceSelected"] : accent.WithAlpha(isDark ? (byte)0x1F : (byte)0x14);
        colors["navIndicator"] = accent;

        // Scales.
        foreach (var (key, value) in f.Space)
        {
            numbers["space" + key] = value;
        }

        foreach (var (key, value) in f.Sizing)
        {
            numbers[key] = value;
        }

        numbers["radiusXs"] = theme.Shape.Xs;
        numbers["radiusSm"] = theme.Shape.Sm;
        numbers["radiusMd"] = theme.Shape.Md;
        numbers["radiusLg"] = theme.Shape.Lg;
        numbers["radiusXl"] = theme.Shape.Xl;
        foreach (var (key, value) in f.Radius)
        {
            numbers["radius" + char.ToUpperInvariant(key[0]) + key[1..]] = Math.Round(value * Math.Clamp(theme.Shape.Md / 8, 0.5, 2), 2);
        }

        foreach (var (key, value) in f.Opacity)
        {
            numbers["opacity" + char.ToUpperInvariant(key[0]) + key[1..]] = value;
        }

        foreach (var (key, value) in f.Density)
        {
            numbers["density" + char.ToUpperInvariant(key[0]) + key[1..]] = value;
        }

        // Morphology.
        numbers["borderWeight"] = morphology.BorderWeight;
        numbers["navIndicatorHeight"] = morphology.NavigationTreatment == ThemeNavigationTreatment.Underline ? Math.Max(2.0, morphology.BorderWeight * 2.0) : 0;
        numbers["navAccentEdgeWidth"] = morphology.NavigationTreatment == ThemeNavigationTreatment.AccentEdge ? Math.Max(2.0, morphology.BorderWeight * 2.5) : 0;
        numbers["navSelectedBorderThickness"] = morphology.NavigationTreatment == ThemeNavigationTreatment.StrongBorder ? Math.Max(1.5, morphology.BorderWeight * 1.5) : 0;
        numbers["navCornerRadius"] = Math.Round(theme.Shape.Sm * morphology.NavigationRadiusScale, 2);
        var (cardBorderScale, cardOpacity) = morphology.CardPersonality switch
        {
            ThemeCardPersonality.Flat => (1.0, 1.0),
            ThemeCardPersonality.Glass => (0.8, Math.Min(morphology.PanelOpacity, 0.86)),
            ThemeCardPersonality.Framed => (2.0, 1.0),
            _ => (1.0, 1.0),
        };
        numbers["cardBorderThickness"] = Math.Round(morphology.BorderWeight * cardBorderScale, 2);
        numbers["cardSurfaceOpacity"] = cardOpacity;
        numbers["chromeBorderThickness"] = morphology.ChromeTreatment switch
        {
            ThemeChromeTreatment.Accented => Math.Max(2.0, morphology.BorderWeight * 2.0),
            ThemeChromeTreatment.Flush => 0,
            _ => morphology.BorderWeight,
        };
        colors["chromeBorder"] = morphology.ChromeTreatment == ThemeChromeTreatment.Accented ? accent : colors["borderSubtle"];

        // Motion: theme intensity × preset scale; Reduced Motion wins outright.
        var intensity = morphology.MotionIntensity > 0 && double.IsFinite(morphology.MotionIntensity) ? morphology.MotionIntensity : 1;
        var scale = reducedMotion ? 0 : intensity;
        numbers["durationFast"] = Math.Round(theme.Motion.FastMs * scale);
        numbers["durationNormal"] = Math.Round(theme.Motion.NormalMs * scale);
        numbers["durationSlow"] = Math.Round(theme.Motion.SlowMs * scale);
        numbers["durationCinematic"] = Math.Round(theme.Motion.CinematicMs * scale);
        numbers["motionCardLiftScale"] = reducedMotion ? 1 : f.CardLiftScale;
        numbers["motionPageTranslate"] = reducedMotion ? 0 : f.PageTranslate;
        numbers["reducedMotion"] = reducedMotion ? 1 : 0;

        strings["fontDisplay"] = f.FallbackUi;
        strings["fontHeading"] = f.FallbackUi;
        strings["fontBody"] = f.FallbackUi;
        strings["fontMono"] = f.FallbackMono;

        return new ResolvedTokenSet(theme.Id, isDark, colors, numbers, strings);
    }

    /// <summary>
    /// Canonical machine-readable projection for external presentation-token consumers.
    /// only formats this JSON as CSS; it performs no token math of its own.
    /// </summary>
    public static string ExportJson(IEnumerable<(ThemeDefinition Theme, MaterialSpec Material)> themes, string fallbackThemeId)
    {
        var f = Foundations;
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["generator"] = "Neuterradise.Runtime TokenAuthority",
            ["fallbackThemeId"] = fallbackThemeId,
        };

        var foundation = new JsonObject();
        foreach (var (key, value) in f.Space) foundation[$"space-{key}"] = value;
        foreach (var (key, value) in f.Sizing) foundation[Kebab(key)] = value;
        foreach (var (key, value) in f.Radius) foundation[$"radius-{Kebab(key)}"] = value;
        foreach (var (key, value) in f.Opacity) foundation[$"opacity-{Kebab(key)}"] = value;
        foreach (var (key, value) in f.Scrims) foundation[$"color-{Kebab(key)}"] = value.ToHex();
        foundation["motion-card-lift-scale"] = f.CardLiftScale;
        foundation["motion-page-translate"] = f.PageTranslate;
        foundation["focus-width"] = f.FocusThickness;
        foundation["focus-offset"] = f.FocusOffset;
        var easing = new JsonObject();
        foreach (var (key, curve) in f.Easing) easing[key] = new JsonArray(curve.Select(v => (JsonNode)v).ToArray());
        foundation["easing"] = easing;
        root["foundations"] = foundation;

        var densityNode = new JsonObject();
        foreach (var (key, value) in f.Density) densityNode[Kebab(key)] = value;
        root["density"] = densityNode;

        var type = new JsonObject();
        foreach (var (name, style) in f.TypeStyles)
        {
            type[name] = new JsonObject
            {
                ["font"] = style.FontRole,
                ["size"] = style.Size,
                ["weight"] = style.Weight,
                ["lineHeight"] = style.LineHeight,
                ["color"] = Kebab(style.ColorRole),
                ["trim"] = style.Trim,
            };
        }

        root["typography"] = type;

        var themeNodes = new JsonObject();
        foreach (var (theme, material) in themes.OrderBy(t => t.Theme.Id, StringComparer.Ordinal))
        {
            var tokens = Resolve(theme, material, reducedMotion: false);
            var node = new JsonObject { ["name"] = theme.Name, ["isDark"] = theme.IsDark };
            var colorNode = new JsonObject();
            foreach (var (key, value) in tokens.Colors.OrderBy(p => p.Key, StringComparer.Ordinal)) colorNode[Kebab(key)] = value.ToHex();
            var numberNode = new JsonObject();
            foreach (var (key, value) in tokens.Numbers.OrderBy(p => p.Key, StringComparer.Ordinal)) numberNode[Kebab(key)] = value;
            var stringNode = new JsonObject();
            foreach (var (key, value) in tokens.Strings.OrderBy(p => p.Key, StringComparer.Ordinal)) stringNode[Kebab(key)] = value;
            node["colors"] = colorNode;
            node["numbers"] = numberNode;
            node["strings"] = stringNode;
            themeNodes[theme.Id] = node;
        }

        root["themes"] = themeNodes;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string Kebab(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('-');
            }
            else if (char.IsDigit(c) && i > 0 && char.IsLetter(name[i - 1]))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static ArgbColor ResolveMaterialColor(string reference, Dictionary<string, ArgbColor> colors, ArgbColor fallback)
    {
        if (reference.StartsWith("token:", StringComparison.Ordinal))
        {
            return colors.TryGetValue(reference[6..], out var color) ? color : fallback;
        }

        return TryParseLiteral(reference, out var literal) ? literal : fallback;
    }

    private static ArgbColor ReadableText(ArgbColor semantic, ArgbColor fallback, IReadOnlyList<ArgbColor> backgrounds)
    {
        const double targetContrast = 4.5;
        var source = semantic.WithAlpha(0xFF);
        var safeFallback = fallback.WithAlpha(0xFF);
        if (MinimumContrast(source, backgrounds) >= targetContrast)
        {
            return source;
        }

        for (var step = 1; step <= 20; step++)
        {
            var candidate = ArgbColor.Lerp(source, safeFallback, step / 20.0).WithAlpha(0xFF);
            if (MinimumContrast(candidate, backgrounds) >= targetContrast)
            {
                return candidate;
            }
        }

        return safeFallback;
    }

    private static double MinimumContrast(ArgbColor foreground, IReadOnlyList<ArgbColor> backgrounds)
    {
        var minimum = double.MaxValue;
        foreach (var background in backgrounds)
        {
            var lighter = Math.Max(foreground.Luminance, background.Luminance);
            var darker = Math.Min(foreground.Luminance, background.Luminance);
            minimum = Math.Min(minimum, (lighter + 0.05) / (darker + 0.05));
        }

        return minimum;
    }

    private static FoundationTokens LoadFoundations()
    {
        using var stream = typeof(TokenAuthority).Assembly.GetManifestResourceStream("Neuterradise.App.Presentation.Tokens.foundation.tokens.json")
            ?? throw new InvalidOperationException("foundation.tokens.json is not embedded in the runtime.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        static Dictionary<string, double> Numbers(JsonElement element) =>
            element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal);

        var scrims = root.GetProperty("scrims").EnumerateObject()
            .ToDictionary(p => p.Name, p => ThemeColorText.Parse(p.Value.GetString()!), StringComparer.Ordinal);
        var motion = root.GetProperty("motion");
        var easing = motion.GetProperty("easing").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetDouble()).ToArray(), StringComparer.Ordinal);
        var focus = root.GetProperty("focus");
        var elevation = root.GetProperty("elevation").EnumerateArray()
            .Select(e => (e.GetProperty("name").GetString()!, e.GetProperty("blur").GetDouble(), e.GetProperty("depth").GetDouble(), e.GetProperty("opacity").GetDouble()))
            .ToList();
        var density = Numbers(root.GetProperty("density"));
        var typography = root.GetProperty("typography");
        var styles = typography.GetProperty("styles").EnumerateObject().ToDictionary(
            p => p.Name,
            p => new TypeStyle(
                p.Value.GetProperty("font").GetString()!,
                p.Value.GetProperty("size").GetDouble(),
                p.Value.GetProperty("weight").GetInt32(),
                p.Value.GetProperty("lineHeight").GetDouble(),
                p.Value.GetProperty("color").GetString()!,
                p.Value.TryGetProperty("trim", out var trim) && trim.GetBoolean(),
                p.Value.TryGetProperty("wrap", out var wrap) && wrap.GetBoolean()),
            StringComparer.Ordinal);

        return new FoundationTokens(
            Numbers(root.GetProperty("space")),
            Numbers(root.GetProperty("sizing")),
            Numbers(root.GetProperty("radius")),
            Numbers(root.GetProperty("opacity")),
            scrims,
            motion.GetProperty("cardLiftScale").GetDouble(),
            motion.GetProperty("pageTranslate").GetDouble(),
            easing,
            focus.GetProperty("thickness").GetDouble(),
            focus.GetProperty("offset").GetDouble(),
            focus.GetProperty("radius").GetDouble(),
            elevation,
            density,
            typography.GetProperty("fallbackUi").GetString()!,
            typography.GetProperty("fallbackMono").GetString()!,
            styles);
    }
}
