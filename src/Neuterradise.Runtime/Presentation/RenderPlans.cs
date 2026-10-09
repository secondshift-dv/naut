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

public sealed record ThemePlan(ThemeDefinition Theme, MaterialSpec Material);

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
    string ParticleShape = "dot");

public sealed record BackdropVariant(IReadOnlyList<BackdropLayer> Layers, bool Static = false)
{
    public bool IsAnimated => !Static && Layers.Any(layer => layer.Kind is "particles" or "fog" or "light-sweep" or "video" or "lottie"
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

public sealed record HomeRailPlan(string Id, bool Visible, string Style, int MaxItems);

public sealed record HomeLayoutPlan(
    string HeroPlacement,
    double HeroFraction,
    double HeroMinHeight,
    double HeroMaxHeight,
    IReadOnlyList<HomeRailPlan> Rails,
    SurfaceNode? Surface = null);

public sealed record SpotlightPlan(
    string Composition,
    string Motion,
    string Transition,
    double RotationSeconds,
    bool ShowCover,
    bool ShowOverview,
    string Pagination,
    double ScrimStrength);

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
    IReadOnlyList<double>? HeightPattern = null);

public sealed record ProfileLayoutPlan(ProfileLayoutDefinition Layout, SurfaceNode? Surface = null);

public sealed record FramePlan(CoverFrameDefinition Frame, string? OverlayAssetPath, double? CoverInsetFraction = null);

public sealed record ProfileMediaLayoutPlan(Neuterradise.App.Design.MediaLayouts.MediaLayoutDefinition Layout, string Browse = "wall");

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
    string? VisibleWhen = null);

/// <summary>
/// Approved declarative primitives (document 01 §14). Built-in cards are written in exactly this
/// language, so a future layout designer can emit definitions the renderer already understands.
/// </summary>
public abstract record CompositionNode(NodeLayout Layout);

public sealed record StackNode(NodeLayout Layout, string Orientation, double Spacing, double Padding, string? Background, string? CornerRadius, IReadOnlyList<CompositionNode> Children) : CompositionNode(Layout);

public sealed record GridNode(NodeLayout Layout, IReadOnlyList<string> Rows, IReadOnlyList<string> Columns, double Spacing, IReadOnlyList<CompositionNode> Children) : CompositionNode(Layout);

public sealed record OverlayNode(NodeLayout Layout, IReadOnlyList<CompositionNode> Children) : CompositionNode(Layout);

public sealed record TextNode(NodeLayout Layout, string? Slot, string? TextKey, string TypeRole, string Color, int MaxLines, string TextAlignment, bool OnMedia) : CompositionNode(Layout);

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
    CompositionNode Root)
{
    public bool UsesBanner => ArtSource is "banner" or "cover-over-banner";

    public int NodeCount => Count(Root);

    private static int Count(CompositionNode node) => 1 + node switch
    {
        StackNode stack => stack.Children.Sum(Count),
        GridNode grid => grid.Children.Sum(Count),
        OverlayNode overlay => overlay.Children.Sum(Count),
        _ => 0,
    };
}
