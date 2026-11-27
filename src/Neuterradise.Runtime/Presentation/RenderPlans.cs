using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Design.Themes;

namespace Neuterradise.App.Presentation;

/// <summary>Full / Reduced / Fallback visual tiers . Every signature effect has all three.</summary>
public enum VisualTier
{
    Full,
    Reduced,
    Fallback,
}

/// <summary>
/// A compiled, immutable, renderer-ready definition. Plans hold resolved values and asset handles only:
/// no database connection, no SQL, no arbitrary Vault path, no UI object (document 01 §26).
/// </summary>
public sealed record CompiledDefinition(
    DefinitionRef Ref,
    string Kind,
    string Name,
    string? Description,
    IReadOnlyList<string> Tags,
    PackOrigin Origin,
    string? PreviewAccent,
    DefinitionPerformance Performance,
    object Plan,
    IReadOnlyList<PackDiagnostic> Diagnostics)
{
    public string? ContentIdentity { get; init; }
    public T PlanAs<T>() where T : class => (T)Plan;
}

// ------------------------------------------------------------------ appearance

/// <summary>Theme material/atmosphere personality . A theme is never palette-only.</summary>
public sealed record MaterialSpec(
    string Personality,
    string Atmosphere,
    double Grain,
    double GlassOpacity,
    double GlassBlur,
    double Specular,
    double RimLight,
    string AmbientPrimary,
    string AmbientSecondary,
    IReadOnlyList<string> FrameAffinity)
{
    public static MaterialSpec Neutral { get; } = new("grounded", "none", 0.02, 0.92, 12, 0.3, 0.3,
        "token:accent", "token:accentSecondary", []);
}

public sealed record InspectorPhonePlan(string? BrandArtwork, string Bezel, string Rim);

public sealed record ThemePlan(ThemeDefinition Theme, MaterialSpec Material, string? ArtworkPath = null, string? ArtworkIdentity = null,
    string? ImportDropzoneArtwork = null, InspectorPhonePlan? InspectorPhone = null);

public sealed record TypeStyle(string FontRole, double Size, int Weight, double LineHeight, string ColorRole, bool Trim, bool Wrap);

// ------------------------------------------------------------------ environment

public sealed record GradientStopSpec(string Color, double Offset);

/// <summary>One bounded declarative backdrop layer. The renderer interprets a fixed, approved set of kinds.</summary>
public sealed record BackdropLayer(
    string Kind,
    double Opacity = 1,
    string? Color = null,
    string? Color2 = null,
    IReadOnlyList<GradientStopSpec>? Stops = null,
    double Angle = 90,
    double CenterX = 0.5,
    double CenterY = 0.5,
    double Radius = 0.6,
    double Speed = 1,
    double Amplitude = 0,
    double Period = 20,
    double Depth = 0,
    double Blur = 0,
    double Scale = 1,
    int Count = 0,
    double SizeMin = 1,
    double SizeMax = 3,
    double Width = 0.2,
    string? AssetPath = null,
    string? Source = null,
    string Blend = "normal",
    string Direction = "up",
    string ParticleShape = "dot",
    double Drift = 0.015,
    double Twinkle = 0.45,
    string SpawnRegion = "all",
    string? AssetIdentity = null);

public sealed record BackdropVariant(IReadOnlyList<BackdropLayer> Layers, bool Static = false)
{
    public bool IsAnimated => !Static && Layers.Any(layer => layer.Kind is "particles" or "fog" or "light-sweep" or "video" or "lottie"
        || (layer.Kind == "noise" && layer.Amplitude > 0)
        || (layer.Amplitude > 0 && layer.Kind is "gradient" or "radial-glow" or "image" or "grid-field" or "wave-field"));
}

public sealed record BackdropPlan(IReadOnlyDictionary<VisualTier, BackdropVariant> Variants)
{
    public BackdropVariant For(VisualTier tier) =>
        Variants.TryGetValue(tier, out var variant) ? variant
        : Variants.TryGetValue(VisualTier.Fallback, out var fallback) ? fallback
        : Variants.Values.First();

    public static BackdropPlan Still(string canvas = "token:canvas", string glow = "token:accentSecondary")
    {
        var variant = new BackdropVariant(
        [
            new BackdropLayer("gradient", Stops: [new GradientStopSpec(canvas, 0), new GradientStopSpec(canvas, 1)]),
            new BackdropLayer("radial-glow", Opacity: 0.4, Color: glow, CenterX: 0.5, CenterY: 0, Radius: 0.9),
        ]);
        return new BackdropPlan(new Dictionary<VisualTier, BackdropVariant>
        {
            [VisualTier.Full] = variant,
            [VisualTier.Reduced] = variant,
            [VisualTier.Fallback] = variant,
        });
    }
}

// ------------------------------------------------------------------ structure

public sealed record HomeRailPlan(string Id, bool Visible, string Style, int MaxItems)
{
    public double CoverScale { get; init; } = 1;
    public string Motion { get; init; } = "static";
    public double ReelSpeed { get; init; } = 24;

    public bool CanReel(int count, double itemWidth, double gap, double viewport) =>
        Motion == "reel" && count > 1 && double.IsFinite(viewport) && viewport > 0
        && count * (itemWidth + gap) - gap > viewport + 1;

}

/// <summary>A stable logical card index and sub-card translation, independent of the loop period.</summary>
public readonly record struct HomeReelPosition(long First, double Phase)
{
    public double Offset(double pitch) => First * pitch + Phase;

    public HomeReelPosition Advance(double delta, double pitch)
    {
        var offset = Phase + delta;
        var step = (long)Math.Floor(offset / pitch);
        return new(First + step, offset - step * pitch);
    }

    public static HomeReelPosition FromOffset(double offset, double pitch) => default(HomeReelPosition).Advance(offset, pitch);

    public static int ItemIndex(long logicalIndex, int count) => (int)((logicalIndex % count + count) % count);

    public (long Start, long End) Window(int count, double pitch, double viewport, bool loop)
    {
        var visible = (int)Math.Min(count, Math.Ceiling(viewport / pitch));
        var start = First - 1;
        var end = First + visible + 2;
        return loop ? (start, end) : (Math.Max(0, start), Math.Min(count, end));
    }
}

public sealed record HomeLayoutPlan(
    string HeroPlacement,
    double HeroFraction,
    double HeroMinHeight,
    double HeroMaxHeight,
    IReadOnlyList<HomeRailPlan> Rails,
    SurfaceNode? Surface = null)
{
    public bool ViewportStage { get; init; }
}

public sealed record ArtworkRotation(double Seconds, double PivotX, double PivotY, bool Clockwise);
public sealed record ArtworkPlacement(double X, double Y, double Width, double Height,
    string Horizontal, string Vertical, int Order);
public sealed record ArtworkLayer(string Kind, string? AssetPath, ArtworkRotation? Rotation,
    ArtworkPlacement? Placement = null, ArtworkPlacement? Narrow = null, int BannerIndex = 0, string? SlotId = null)
{
    public string BannerSlotId => SlotId ?? $"banner-{BannerIndex}";
}
public sealed record ArtworkPlan(string? MaskPath, ArtworkRotation? Rotation, IReadOnlyList<ArtworkLayer> Layers,
    double? Aspect, double Breakpoint = 800);

public enum SpotlightGeometry { FullBleed, ArtworkLeft, IdentityLeft, ArtworkAbove, IdentityAbove }

public sealed record MarqueePlan(double Angle = 0, double Spacing = 0, double Inset = 0,
    bool ShowMetadata = true, string TextColor = "token:textInverse")
{
    public double FitScale(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height)) return 1;
        return Math.Clamp(Math.Min(width / (180 + 2 * Inset + 3 * Spacing), height / (ShowMetadata ? 32 : 24)), 0.25, 1);
    }
}

public sealed record SpotlightPlan(
    string Composition,
    string Motion,
    string Transition,
    double RotationSeconds,
    bool ShowCover,
    bool ShowOverview,
    string Pagination,
    double ScrimStrength, ArtworkPlan? Artwork = null, string IdentityPlacement = "legacy", SurfaceNode? Surface = null)
{
    public MarqueePlan Marquee { get; init; } = new();

    public SpotlightGeometry GeometryFor(double width, string homePlacement) => IdentityPlacement switch
    {
        "below" => SpotlightGeometry.ArtworkAbove,
        "beside" => width < 720 ? SpotlightGeometry.ArtworkAbove : SpotlightGeometry.ArtworkLeft,
        "overlay" => SpotlightGeometry.FullBleed,
        _ => LegacyGeometry(width, homePlacement),
    };

    private SpotlightGeometry LegacyGeometry(double width, string homePlacement) => Composition switch
    {
        "storefront" or "editorial" or "cinema" => SpotlightGeometry.FullBleed,
        _ => homePlacement == "split" && width >= 840 ? SpotlightGeometry.ArtworkLeft : SpotlightGeometry.FullBleed,
    };

    public double IdentityShare => Composition == "storefront" ? 0.43 : Composition == "editorial" ? 0.44 : 0.4;
}

/// <summary>Only renderer-approved virtualized primitives exist here; no layout can request unbounded realization.</summary>
public static class CollectionPrimitives
{
    public const string AdaptiveWall = "VirtualizedAdaptiveWall";
    public const string Shelves = "VirtualizedShelves";
    public const string SingleColumn = "VirtualizedSingleColumn";
    public const string Grid = "VirtualizedGrid";
    public const string UniformGrid = "VirtualizedUniformGrid";
    public const string List = "VirtualizedList";
    public const string Carousel = "VirtualizedCarousel";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AdaptiveWall,
        Shelves,
        SingleColumn,
        Grid,
        UniformGrid,
        List,
        Carousel,
    };
}

public sealed record GalleryLayoutPlan(
    string Primitive,
    double ItemMinWidth,
    double ItemMaxWidth,
    double? ItemAspect,
    double Spacing,
    double RowHeight,
    int MaxColumns,
    string Toolbar = "standard",
    IReadOnlyList<double>? HeightPattern = null, GalleryScenePlan? Scene = null);

public sealed record GalleryScenePlan(BackdropPlan? Background, BackdropPlan? Foreground,
    string? ItemOverlay, double ItemInset, string CardSizing,
    double CardRotation = 0, double CardSkew = 0, string OverlayPlacement = "front",
    bool FixedSlot = false, int CompactColumns = 0, string? ViewportArtwork = null,
    double HeaderSpace = 0, string? ActionArtwork = null, string ActionText = "#FFFF68C8",
    string ActionHoverText = "#FFFFC5EA")
{
    public (double Width, double Height) FitCard(double width, double height, double aspect)
    {
        aspect = Math.Max(0.3, aspect);
        var rotation = CardRotation * Math.PI / 180;
        var skew = Math.Tan(CardSkew * Math.PI / 180);
        var cos = Math.Cos(rotation);
        var sin = Math.Sin(rotation);
        // Unit-height transformed bounds, before uniform scale. Contain preserves the entire
        // authored Card, while crop deliberately clips it inside the owned item envelope.
        var projectedWidth = Math.Abs(cos) * aspect + Math.Abs(cos * skew - sin);
        var projectedHeight = Math.Abs(sin) * aspect + Math.Abs(sin * skew + cos);
        var scale = CardSizing == "crop"
            ? Math.Max(width / aspect, height)
            : Math.Min(width / projectedWidth, height / projectedHeight);
        return (Math.Max(0, scale * aspect), Math.Max(0, scale));
    }
}

public sealed record ProfilePaperPlan(string Background, string Ink, string Accent)
{
    public bool ShowBackdrop { get; init; }
    public double SurfaceOpacity { get; init; } = 1;
    public IReadOnlyDictionary<string, string> Colors { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string ResolveReference(string reference)
    {
        var token = reference.StartsWith("token:", StringComparison.Ordinal) ? reference[6..] : reference;
        if (Colors.TryGetValue(token, out var color)) return color;
        return token switch
        {
            "canvas" or "surface1" => Background,
            "surface2" or "surface3" or "surfaceElevated" => "#FFF2E8EE",
            "surfaceOverlay" or "surfaceSelected" or "surfaceHover" or "surfacePressed" => "#FFF1D7E5",
            "textPrimary" or "textInverse" => Ink,
            "textSecondary" or "textMuted" => "#FF604D65",
            "textDisabled" => "#FF807484",
            "textOnAccent" => "#FFFFFFFF",
            "accent" or "accentHover" or "accentPressed" or "accentSecondary" or "textLink" or "focus" or "borderSelected" or "borderInteractive" => Accent,
            "accentSoft" or "selection" or "selectionHover" => "#FFF1D7E5",
            "borderDefault" or "borderSubtle" or "borderStrong" => "#FFCCBCCB",
            "danger" or "dangerHover" or "textDanger" => "#FFBB1957",
            "warning" => "#FFB77900",
            "success" => "#FF23794C",
            "info" => "#FF256478",
            _ => reference,
        };
    }

    public string SurfaceColor(bool raised) =>
        Colors.GetValueOrDefault(raised ? "surfaceElevated" : "surface1") ?? ResolveReference("canvas");
}
public sealed record ProfileLayoutPlan(ProfileLayoutDefinition Layout, SurfaceNode? Surface = null, ArtworkPlan? BannerArtwork = null,
    ProfilePaperPlan? Paper = null)
{
    public IReadOnlyDictionary<string, ComponentVisualStyle>? Visuals { get; init; }
}

public sealed record FramePlan(CoverFrameDefinition Frame, string? OverlayAssetPath, double? CoverInsetFraction = null, string? OverlayAssetIdentity = null);

public sealed record MediaCollectionPlan(string Layout, int MaxColumns, double RowSpacing, double ColumnSpacing);

public sealed record ProfileMediaLayoutPlan(Neuterradise.App.Design.MediaLayouts.MediaLayoutDefinition Layout, string Browse = "wall",
    MediaCollectionPlan? Collection = null);

// ------------------------------------------------------------------ card composition language (v1)

/// <summary>Layout properties every composition node shares.</summary>
public sealed record NodeLayout(
    double MarginLeft = 0,
    double MarginTop = 0,
    double MarginRight = 0,
    double MarginBottom = 0,
    double? Width = null,
    double? Height = null,
    string HorizontalAlignment = "stretch",
    string VerticalAlignment = "stretch",
    int Row = 0,
    int Column = 0,
    int RowSpan = 1,
    int ColumnSpan = 1,
    double Opacity = 1,
    string? VisibleWhen = null, bool StretchHeight = false, ArtworkPlacement? CanvasPlacement = null);

/// <summary>
/// Approved declarative primitives (document 01 §14). Built-in cards are written in exactly this
/// language, so a future layout designer can emit definitions the renderer already understands.
/// </summary>
public abstract record CompositionNode(NodeLayout Layout);

public sealed record CardCanvasNode(NodeLayout Layout, IReadOnlyList<CompositionNode> Children) : CompositionNode(Layout);

public sealed record CardAssetNode(NodeLayout Layout, string? AssetPath, bool Foreground) : CompositionNode(Layout);

public sealed record StackNode(NodeLayout Layout, string Orientation, double Spacing, double Padding, string? Background, string? CornerRadius, IReadOnlyList<CompositionNode> Children) : CompositionNode(Layout);

public sealed record GridNode(NodeLayout Layout, IReadOnlyList<string> Rows, IReadOnlyList<string> Columns, double Spacing, IReadOnlyList<CompositionNode> Children) : CompositionNode(Layout);

public sealed record OverlayNode(NodeLayout Layout, IReadOnlyList<CompositionNode> Children) : CompositionNode(Layout);

public sealed record TextNode(NodeLayout Layout, string? Slot, string? TextKey, string TypeRole, string Color, int MaxLines, string TextAlignment, bool OnMedia) : CompositionNode(Layout)
{
    public string? OutlineColor { get; init; }
    public double OutlineWidth { get; init; } = 1;
}

public sealed record ImageNode(NodeLayout Layout, string Slot, string Stretch, string? CornerRadius, string Shape, bool Ambient, string Fallback = "identity") : CompositionNode(Layout);

public sealed record FrameNode(NodeLayout Layout, double Size) : CompositionNode(Layout);

public sealed record BadgeNode(NodeLayout Layout, string Slot, string Style) : CompositionNode(Layout);

public sealed record ScrimNode(NodeLayout Layout, string Direction, string From, string To) : CompositionNode(Layout);

public sealed record ShapeNode(NodeLayout Layout, string? Fill, string? Stroke, double StrokeThickness, string? CornerRadius) : CompositionNode(Layout);

public sealed record IconNode(NodeLayout Layout, string Key, double Size, string Color) : CompositionNode(Layout);

public sealed record CardPlan(
    double Aspect,
    string ArtSource,
    string Density,
    string Hover,
    string FrameMode,
    string? LegacyVariant,
    CompositionNode Root, CompositionNode? HoverRoot = null)
{
    public bool UsesBanner => HoverRoot is not null || ArtSource is "banner" or "cover-over-banner";

    public int NodeCount => Count(Root) + (HoverRoot is null ? 0 : Count(HoverRoot));

    private static int Count(CompositionNode node) => 1 + node switch
    {
        CardCanvasNode canvas => canvas.Children.Sum(Count),
        StackNode stack => stack.Children.Sum(Count),
        GridNode grid => grid.Children.Sum(Count),
        OverlayNode overlay => overlay.Children.Sum(Count),
        _ => 0,
    };
}
