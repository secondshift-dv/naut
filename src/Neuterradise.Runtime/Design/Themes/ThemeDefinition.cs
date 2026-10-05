using System.Globalization;

namespace Neuterradise.App.Design.Themes;

public sealed record ThemeDefinition(
    int SchemaVersion,
    string Id,
    string Name,
    bool IsDark,
    ThemeColors Colors,
    ThemeTypography Typography,
    ThemeShape Shape,
    ThemeMotion Motion,
    ThemeBackground Background,
    ThemeMorphology? Morphology = null)
{
    /// <summary>
    /// Morphology the theme actually declares, or the neutral shipped default. Themes are allowed to
    /// omit the section entirely; the product then behaves exactly as it did before morphology existed.
    /// </summary>
    public ThemeMorphology EffectiveMorphology => Morphology ?? ThemeMorphology.Neutral;
}

/// <summary>
/// V1 theme vocabulary. Navigation, surface shape, card body/border, chrome and motion are projected;
/// focus, elevation and glow declarations remain readable for pack compatibility.
/// </summary>
public enum ThemeNavigationTreatment
{
    /// <summary>A weighted rule under the active destination.</summary>
    Underline,

    /// <summary>A vertical accent edge on the leading side of the active destination.</summary>
    AccentEdge,

    /// <summary>A filled selected surface behind the active destination.</summary>
    SelectedSurface,

    /// <summary>A restrained accent glow around the active destination.</summary>
    Glow,

    /// <summary>A pronounced border outlining the active destination.</summary>
    StrongBorder,
}

public enum ThemeCardPersonality
{
    Flat,
    Raised,
    Glass,
    Framed,
}

public enum ThemeChromeTreatment
{
    Flush,
    Elevated,
    Accented,
}

public enum ThemeFocusTreatment
{
    Ring,
    Underline,
    Glow,
}

public sealed record ThemeMorphology(
    ThemeNavigationTreatment NavigationTreatment,
    ThemeCardPersonality CardPersonality,
    ThemeChromeTreatment ChromeTreatment,
    ThemeFocusTreatment FocusTreatment,
    double BorderWeight,
    double ElevationStrength,
    double GlowStrength,
    double PanelOpacity,
    double MotionIntensity,
    double NavigationRadiusScale)
{
    public static ThemeMorphology Neutral { get; } = new(
        ThemeNavigationTreatment.SelectedSurface,
        ThemeCardPersonality.Raised,
        ThemeChromeTreatment.Flush,
        ThemeFocusTreatment.Ring,
        BorderWeight: 1.0,
        ElevationStrength: 1.0,
        GlowStrength: 0.0,
        PanelOpacity: 1.0,
        MotionIntensity: 1.0,
        NavigationRadiusScale: 1.0);
}

public sealed record ThemeColors(
    string Canvas,
    string Surface1,
    string Surface2,
    string Surface3,
    string SurfaceElevated,
    string SurfaceOverlay,
    string SurfaceHover,
    string SurfacePressed,
    string SurfaceSelected,
    string TextPrimary,
    string TextSecondary,
    string TextMuted,
    string TextDisabled,
    string TextInverse,
    string TextOnAccent,
    string TextLink,
    string BorderSubtle,
    string BorderDefault,
    string BorderStrong,
    string BorderInteractive,
    string BorderSelected,
    string Focus,
    string Accent,
    string AccentHover,
    string AccentPressed,
    string AccentSecondary,
    string Selection,
    string SelectionHover,
    string Info,
    string Success,
    string Warning,
    string Danger,
    string DangerHover);

public sealed record ThemeTypography(
    string DisplayFamily,
    string HeadingFamily,
    string BodyFamily,
    string MonoFamily);

public sealed record ThemeShape(
    double Xs,
    double Sm,
    double Md,
    double Lg,
    double Xl);

public sealed record ThemeMotion(
    int FastMs,
    int NormalMs,
    int SlowMs,
    int CinematicMs);

public enum ThemeBackgroundKind
{
    RadialGlow
}

public sealed record ThemeBackground(ThemeBackgroundKind Kind);

public static class ThemeColorText
{

    public const int RequiredLength = 9;

    public static bool IsValid(string? value) => TryParse(value, out _);

    public static bool TryParse(string? value, out ArgbColor color)
    {
        color = default;

        if (value is null || value.Length != RequiredLength || value[0] != '#')
        {
            return false;
        }

        Span<byte> channels = stackalloc byte[4];

        for (var channel = 0; channel < 4; channel++)
        {
            var text = value.AsSpan(1 + (channel * 2), 2);

            if (!byte.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out channels[channel]))
            {
                return false;
            }
        }

        color = ArgbColor.FromArgb(channels[0], channels[1], channels[2], channels[3]);
        return true;
    }

    public static ArgbColor Parse(string value)
    {
        if (!TryParse(value, out var color))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid #AARRGGBB theme colour. Only validated themes may be mapped to resources.",
                nameof(value));
        }

        return color;
    }
}
