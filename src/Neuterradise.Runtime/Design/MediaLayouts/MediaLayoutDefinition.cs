namespace Neuterradise.App.Design.MediaLayouts;

public enum MediaMetadataPlacement
{
    HoverOverlay,
    PersistentOverlay,
    Below,
    Side,
    Hidden,
}

public enum MediaFrameStyle
{
    None,
    Hairline,
    Gallery,
    Double,
    Luminous,
    Neon,
}

/// <summary>
/// One Profile-owned media-card composition. The card tree is fixed; this definition changes only
/// geometry, visibility and styling. It never owns media bytes, queries, paging or hover-video lifetime.
/// </summary>
public sealed record MediaLayoutDefinition(
    string Id,
    string DisplayName,
    double CardWidth,
    double Aspect,
    double MediaWidthFraction,
    string Fit,
    MediaMetadataPlacement MetadataPlacement,
    IReadOnlyList<string> MetadataFields,
    int MetadataLines,
    MediaFrameStyle FrameStyle,
    double FrameRadius,
    double FrameThickness,
    string FrameColor,
    string SelectedFrameColor,
    double FrameGlow,
    string HoverStyle,
    bool ShowTypeBadge)
{
    public double MediaWidth => CardWidth * MediaWidthFraction;

    public double CardHeight => MetadataPlacement switch
    {
        MediaMetadataPlacement.Side => Math.Max(150, MediaWidth / Math.Max(0.5, Aspect)),
        MediaMetadataPlacement.Below => (CardWidth / Math.Max(0.5, Aspect)) + 48,
        _ => CardWidth / Math.Max(0.5, Aspect),
    };
}

public static class MediaLayoutCatalog
{
    public const string FallbackLayoutId = "mosaic";

    public static IReadOnlySet<string> BuiltInIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "mosaic", "editorial-grid", "workbench",
    };

    public static IReadOnlySet<string> CompatibleLayoutIds { get; } =
        new HashSet<string>(BuiltInIds.Concat(
        [
            "immersive", "clean", "showcase", "wall", "editorial", "compact", "cinematic", "reel",
        ]), StringComparer.Ordinal);

    public static bool IsBuiltInId(string? id) =>
        id is not null && CompatibleLayoutIds.Contains(id);

    // Old durable values are translated to the redesigned layouts; old layouts are not rendered.
    public static string Normalize(string? id) => id switch
    {
        "immersive" or "clean" or "showcase" or "wall" => "mosaic",
        "editorial" or "compact" => "workbench",
        "cinematic" or "reel" => "mosaic",
        _ => IsBuiltInId(id) ? id! : FallbackLayoutId,
    };
}
