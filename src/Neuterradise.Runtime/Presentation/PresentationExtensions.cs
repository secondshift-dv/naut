using System.Text.Json;
using SkiaSharp;

namespace Neuterradise.App.Presentation;

public sealed record SurfaceNode(string Kind, string? Semantic, double Spacing, double Padding,
    IReadOnlyList<string> Columns, IReadOnlyList<SurfaceNode> Children)
{
    public bool Modular { get; init; }
    public IReadOnlyList<string> Rows { get; init; } = [];
    public NodeLayout Placement { get; init; } = new();
    public SurfaceNode? Narrow { get; init; }
    public double Breakpoint { get; init; } = 800;
    public ArtworkPlacement? CanvasPlacement { get; init; }
    public double CanvasAspect { get; init; } = 1;
    public string? AssetPath { get; init; }
    public string? Fill { get; init; }
    public double Opacity { get; init; } = 1;
    public SceneMotionPlan? Motion { get; init; }

    public bool HasRegion(string id) => Semantic == id || Children.Any(child => child.HasRegion(id));
}

public sealed record SceneKeyframe(double Time, double X, double Y, double Scale, double Rotation, double Opacity);
public sealed record SceneMotionPlan(double Seconds, bool Loop, IReadOnlyList<SceneKeyframe> Keyframes);

public sealed record TypographyFace(string Family, string? AssetPath);
public sealed record TypographyPlan(TypographyFace Display, TypographyFace Heading, TypographyFace Body, TypographyFace Mono)
{
    public IReadOnlyDictionary<string, TypeStyle> Styles { get; init; } = new Dictionary<string, TypeStyle>();
}

public sealed record SemanticIconSkin(string Path, string? SelectedPath, double StrokeWidth);

public sealed record NavigationScenePlan(SurfaceNode Scene, string Dock, double Extent, bool Immersive)
{
    public bool OverlayHome { get; init; }
}

public sealed record ControlSkinPlan(double Radius, double BorderWidth, double HorizontalPadding, double VerticalPadding,
    double Height, double ScrollbarWidth, string Fill, string HoverFill, string PressedFill, string Border, string Focus)
{
    public string Material { get; init; } = "flat";
    public string Edge { get; init; } = "all";
    public double Relief { get; init; } = 0;
    public double ThumbWidth { get; init; } = 20;
    public double ThumbHeight { get; init; } = 20;
    public IReadOnlyDictionary<string, ControlSkinPlan> Families { get; init; } = new Dictionary<string, ControlSkinPlan>();
    public IReadOnlyDictionary<string, SemanticIconSkin> Icons { get; init; } = new Dictionary<string, SemanticIconSkin>();
    public double? PassiveChipHeight { get; init; }
    public double? IconButtonSize { get; init; }
    public string? SelectedFill { get; init; }
    public bool SelectedUnderline { get; init; }
    public double BreadcrumbStripeOpacity { get; init; }
    public NavigationScenePlan? Navigation { get; init; }
    public ControlSkinPlan For(string family) => Families.TryGetValue(family, out var plan) ? plan : this;
}

public sealed record EffectPlan(IReadOnlySet<string> Targets, string Channel, BackdropPlan Environment, bool InteractionOnly)
{
    public bool Supports(string slot) => Targets.Contains("all") || Targets.Contains(slot);
}

public sealed partial class PresentationCompiler
{
    private static SurfaceNode? CompileSurface(JsonElement spec, SpecContext context, string kind)
    {
        var hasScene = spec.TryGetProperty("scene", out var root);
        if (hasScene)
        {
            if (context.ContractVersion < 4)
                context.Error("scene", "Scene graphs require Presentation Contract 4.");
            if (spec.TryGetProperty("surface", out _))
                context.Error("scene", "Declare scene or legacy surface, not both.");
        }
        else if (!spec.TryGetProperty("surface", out root)) return null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            context.Error("scene", "A composition root must be an object.");
            return null;
        }
        var world = context.ContractVersion >= 4;
        var spotlight = kind == DefinitionKinds.SpotlightStyle;
        var navigation = kind == DefinitionKinds.ControlSkin;
        var modular = spotlight || navigation || context.Bool(root, "modular", false);
        if (spotlight && context.ContractVersion < 3)
            context.Error("surface", "Independent Spotlight regions require Presentation Contract 3.");
        if (modular && context.ContractVersion < 2)
            context.Error("surface.modular", "Modular surfaces require Presentation Contract 2.");
        string[] semantics = kind == DefinitionKinds.HomeLayout
            ? ["featured", "recent", "favorites", "profiles", "activity", "statistics", "attention", "imports", "discovery", "local-navigation"]
            : ["identity", "overview", "related", "faces", "recent-media", "media", "actions", "review", "statistics"];
        if (spotlight) semantics = ["artwork", "identity", "pagination"];
        if (navigation) semantics = ["vault", "home", "gallery", "import", "settings", "activity", "customize"];
        if (modular && kind == DefinitionKinds.ProfileLayout)
            semantics = [.. semantics, "banner", "cover", "figure", "tags", "profile-tools"];

        var used = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        var animatedCount = 0;
        SurfaceNode? Read(JsonElement node, int depth)
        {
            if (++count > MaxCompositionNodes || depth > MaxCompositionDepth)
            {
                context.Error("surface", "Surface composition exceeds the node or nesting budget.");
                return null;
            }
            string[] types = modular
                ? ["stack", "grid", "columns", "panel", "overlay", "spacer", "divider", "semantic"]
                : ["stack", "columns", "panel", "overlay", "spacer", "divider", "semantic"];
            if (world && modular) types = [.. types, "canvas", "image", "shape"];
            var type = context.Enum(node, "type", types, "stack");
            string? semantic = null;
            if (type == "semantic")
            {
                semantic = context.Enum(node, "id", semantics, semantics[0]);
                if (!used.Add(semantic)) context.Error("surface", $"Semantic region '{semantic}' may appear only once per arrangement.");
            }
            var children = new List<SurfaceNode>();
            if (node.TryGetProperty("children", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                if (type is "semantic" or "spacer" or "divider" or "image" or "shape")
                    context.Error("surface.children", "A leaf region cannot contain children.");
                foreach (var child in array.EnumerateArray())
                    if (Read(child, depth + 1) is { } parsed) children.Add(parsed);
            }

            var columns = context.Tracks(node, "columns");
            var rows = modular ? context.Tracks(node, "rows") : [];
            if (type == "grid" && (rows.Count == 0 || columns.Count == 0 || children.Any(child =>
                child.Placement.Row + child.Placement.RowSpan > rows.Count
                || child.Placement.Column + child.Placement.ColumnSpan > columns.Count)))
                context.Error("surface.grid", "A grid must declare tracks and contain all child placements.");

            if (node.TryGetProperty("position", out _) && (!world || !modular))
                context.Error("position", "Canvas placement requires a modular Contract 4 surface.");
            var motion = ReadSceneMotion(node, context, type);
            if (motion is not null) animatedCount++;
            if (type == "canvas" && (children.Count == 0 || children.Any(child => child.CanvasPlacement is null)))
                context.Error("surface.canvas", "Canvas children require explicit normalized position.");
            if (type != "canvas" && children.Any(child => child.CanvasPlacement is not null))
                context.Error("surface.position", "Position belongs to a direct canvas child.");
            // Scrollable collections retain their native scrolling and virtualization boundary.
            if (type == "canvas" && children.Any(child => child.HasRegion("media") || child.HasRegion("faces")
                || child.HasRegion("recent-media")))
                context.Error("surface.canvas", "Scrollable Profile collections belong outside finite canvas stages.");
            var hasSize = node.TryGetProperty("width", out _) || node.TryGetProperty("height", out _);
            if (hasSize && (context.ContractVersion < 3 || !modular))
                context.Error("surface", "Region dimensions require a modular Presentation Contract 3 surface.");
            return new SurfaceNode(type, semantic, context.Number(node, "spacing", 12, 0, 64),
                context.Number(node, "padding", 0, 0, 64), columns, children)
            {
                Modular = modular,
                Rows = rows,
                CanvasPlacement = world && modular ? ReadArtworkPlacement(node, "position", context, 0, canvas: true) : null,
                CanvasAspect = type == "canvas" ? context.Number(node, "aspect", 1.6, 0.25, 6) : 1,
                AssetPath = type == "image" ? context.Asset(context.String(node, "asset", ""), PackAssetKind.Image, "surface.image") : null,
                Fill = type == "shape" ? context.Color(node, "fill", "token:surface2") : null,
                Opacity = type is "image" or "shape" ? context.Number(node, "opacity", 1, 0, 1) : 1,
                Motion = motion,
                Placement = modular ? new NodeLayout(
                    Width: node.TryGetProperty("width", out _) ? context.Number(node, "width", 320, semantic == "figure" ? 200 : 48, 1440) : null,
                    Height: node.TryGetProperty("height", out _) ? context.Number(node, "height", 320, semantic == "figure" ? 240 : 24, 960) : null,
                    Row: (int)context.Number(node, "row", 0, 0, 7),
                    Column: (int)context.Number(node, "column", 0, 0, 7),
                    StretchHeight: world && semantic == "figure" && context.Bool(node, "stretchHeight", false),
                    RowSpan: (int)context.Number(node, "rowSpan", 1, 1, 8),
                    ColumnSpan: (int)context.Number(node, "columnSpan", 1, 1, 8)) : new(),
            };
        }

        SurfaceNode? ReadArrangement(JsonElement source)
        {
            used.Clear();
            animatedCount = 0;
            var result = Read(source, 0);
            if (animatedCount > Math.Min(MaxBackdropLayers, context.Definition.Performance.MaxAnimatedLayers))
                context.Error("scene.motion", "Scene animation exceeds its declared layer budget.");
            if (result?.CanvasPlacement is not null) context.Error("surface.position", "The arrangement root cannot be a positioned child.");
            string[] required = navigation ? semantics : spotlight ? ["artwork", "identity"] : kind == DefinitionKinds.HomeLayout
                ? modular ? ["featured"] : ["featured", "activity", "statistics"]
                : modular ? ["identity", "media"] : ["identity", "overview", "media", "related", "faces", "actions", "review"];
            foreach (var semantic in required.Where(s => !used.Contains(s)))
                context.Error("surface", $"Required region '{semantic}' is missing.");
            return result;
        }

        var wide = ReadArrangement(root);
        if (!root.TryGetProperty("narrow", out var narrow)) return wide;
        if (!modular)
        {
            context.Error("surface.narrow", "An alternate arrangement requires a modular surface.");
            return wide;
        }
        var compact = ReadArrangement(narrow);
        return wide is null ? null : wide with
        {
            Narrow = compact,
            Breakpoint = context.Number(root, "breakpoint", 800, 320, 1920),
        };
    }

    private static SceneMotionPlan? ReadSceneMotion(JsonElement node, SpecContext context, string type)
    {
        if (!node.TryGetProperty("motion", out var motion)) return null;
        if (context.ContractVersion < 4 || type is not ("image" or "shape") || motion.ValueKind != JsonValueKind.Object)
        {
            context.Error("scene.motion", "Contract 4 motion is limited to decorative image and shape leaves.");
            return null;
        }
        if (!motion.TryGetProperty("keyframes", out var frames) || frames.ValueKind != JsonValueKind.Array
            || frames.GetArrayLength() is < 2 or > 32)
        {
            context.Error("scene.motion", "A timeline requires 2 to 32 keyframes.");
            return null;
        }
        var loop = context.Bool(motion, "loop", false);
        if (loop && !context.Definition.Performance.ContinuousAnimation)
            context.Error("scene.motion", "Looping motion requires declared continuousAnimation.");
        var keyframes = new List<SceneKeyframe>();
        foreach (var frame in frames.EnumerateArray())
        {
            if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty("time", out _))
            {
                context.Error("scene.motion", "Each keyframe requires normalized time.");
                continue;
            }
            var time = context.Number(frame, "time", 0, 0, 1);
            if (keyframes.Count > 0 && time <= keyframes[^1].Time)
                context.Error("scene.motion", "Keyframe times must strictly increase.");
            keyframes.Add(new(time, context.Number(frame, "x", 0, -1, 1),
                context.Number(frame, "y", 0, -1, 1), context.Number(frame, "scale", 1, 0.1, 3),
                context.Number(frame, "rotation", 0, -720, 720), context.Number(frame, "opacity", 1, 0, 1)));
        }
        if (keyframes.Count < 2 || keyframes[0].Time != 0 || keyframes[^1].Time != 1)
            context.Error("scene.motion", "Timeline endpoints must be 0 and 1.");
        return new(context.Number(motion, "seconds", 4, 0.25, 120), loop, keyframes);
    }

    private static string ReadIdentityPlacement(JsonElement spec, SpecContext context)
    {
        if (!spec.TryGetProperty("identityPlacement", out _)) return "legacy";
        if (context.ContractVersion < 2)
            context.Error("identityPlacement", "Spotlight identity placement requires Presentation Contract 2.");
        return context.Enum(spec, "identityPlacement", ["beside", "below", "overlay"], "below");
    }

    private static ArtworkPlacement? ReadArtworkPlacement(JsonElement layer, string property, SpecContext context, int order, bool canvas = false)
    {
        if (!layer.TryGetProperty(property, out var value)) return null;
        if (context.ContractVersion < 3 || value.ValueKind != JsonValueKind.Object)
        {
            context.Error(property, "Artwork placement must be an object and requires Presentation Contract 3.");
            return null;
        }
        return new ArtworkPlacement(
            context.Number(value, "x", 0, -1, 1),
            context.Number(value, "y", 0, -1, 1),
            context.Number(value, "width", 1, 0.05, 2),
            context.Number(value, "height", 1, 0.05, 2),
            context.Enum(value, "horizontal", ["left", "center", "right"], "left"),
            context.Enum(value, "vertical", ["top", "center", "bottom"], "top"),
            (int)context.Number(value, "order", order, 0, canvas ? MaxCompositionNodes - 1 : MaxBackdropLayers - 1));
    }

    private static ArtworkPlan? CompileArtwork(JsonElement spec, SpecContext context, string name)
    {
        if (!spec.TryGetProperty(name, out var artwork)) return null;
        if (context.ContractVersion < 2)
            context.Error(name, "Artwork composition requires Presentation Contract 2.");
        if (artwork.ValueKind != JsonValueKind.Object
            || !artwork.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array
            || layers.GetArrayLength() is < 1 or > MaxBackdropLayers)
        {
            context.Error(name, "Artwork declares between one and eight ordered layers.");
            return null;
        }

        if (artwork.TryGetProperty("breakpoint", out _) && context.ContractVersion < 3)
            context.Error(name, "Artwork breakpoints require Presentation Contract 3.");

        ArtworkRotation? Rotation(JsonElement node)
        {
            if (!node.TryGetProperty("rotation", out var rotation)) return null;
            if (rotation.ValueKind != JsonValueKind.Object)
            {
                context.Error(name, "Rotation must be an object.");
                return null;
            }
            return new(context.Number(rotation, "seconds", 16, 2, 120),
                context.Number(rotation, "pivotX", 0.5, 0, 1), context.Number(rotation, "pivotY", 0.5, 0, 1),
                context.Enum(rotation, "direction", ["clockwise", "counterclockwise"], "clockwise") == "clockwise");
        }

        string? maskPath = null;
        if (artwork.TryGetProperty("mask", out _))
        {
            var mask = context.String(artwork, "mask", "");
            if (!context.Manifest.Assets.Any(asset => asset.Id == mask
                && string.Equals(Path.GetExtension(asset.Path), ".png", StringComparison.OrdinalIgnoreCase)))
                context.Error(name, "The alpha mask must reference a PNG asset.");
            maskPath = context.Asset(mask, PackAssetKind.Image, name);
        }

        var plans = new List<ArtworkLayer>();
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var banners = new HashSet<int>();
        var bannerSlots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in layers.EnumerateArray())
        {
            if (layer.ValueKind != JsonValueKind.Object)
            {
                context.Error(name, "An artwork layer must be an object.");
                continue;
            }
            var kind = context.Enum(layer, "type", context.ContractVersion >= 4
                ? ["artwork", "image", "scrim", "effect", "banner"] : ["artwork", "image", "scrim", "effect"], "artwork");
            if (kind is not ("image" or "banner") && !roles.Add(kind))
                context.Error(name, $"Artwork layer '{kind}' appears more than once.");
            var path = kind == "image" ? context.Asset(context.String(layer, "asset", ""), PackAssetKind.Image, name) : null;
            var index = kind == "banner" ? (int)context.Number(layer, "sourceIndex", 1, 1, MaxBackdropLayers - 1) : 0;
            string? slotId = null;
            if (kind == "banner")
            {
                slotId = context.OptionalString(layer, "slotId") ?? $"banner-{index}";
                if (!banners.Add(index) || !BannerSlotState.IsValidSlotId(slotId) || !bannerSlots.Add(slotId))
                    context.Error(name, "Additional Banner layers require distinct source indices and valid, distinct slot IDs.");
            }
            plans.Add(new(kind, path, Rotation(layer),
                ReadArtworkPlacement(layer, "placement", context, plans.Count),
                ReadArtworkPlacement(layer, "narrow", context, plans.Count), index, slotId));
        }
        if (!roles.Contains("artwork")) context.Error(name, "The artwork layer is required.");
        var groupRotation = Rotation(artwork);
        var animated = plans.Count(layer => layer.Rotation is not null) + (groupRotation is null ? 0 : 1);
        if (animated > context.Definition.Performance.MaxAnimatedLayers)
            context.Error(name, "Artwork rotation exceeds its declared animation budget.");
        return new(maskPath, groupRotation, plans,
            artwork.TryGetProperty("aspect", out _) ? context.Number(artwork, "aspect", 1, 0.5, context.ContractVersion >= 4 ? 6 : 4) : null,
            artwork.TryGetProperty("breakpoint", out _) && context.ContractVersion >= 3
                ? context.Number(artwork, "breakpoint", 800, 320, 1920) : 800);
    }

    private static TypographyPlan CompileTypography(JsonElement spec, SpecContext context)
    {
        TypographyFace Face(string name, string fallback)
        {
            if (!spec.TryGetProperty(name, out var face) || face.ValueKind != JsonValueKind.Object)
            {
                context.Error(name, "A typography style declares display, heading, body and mono faces.");
                return new(fallback, null);
            }
            var family = context.String(face, "family", fallback);
            if (family.Length > 64 || family.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '#' or '{' or '}' or ','))
                context.Error(name, "Font families must be plain names; asset references carry local font paths.");
            var path = face.TryGetProperty("asset", out var asset) && asset.ValueKind == JsonValueKind.String
                ? context.Asset(asset.GetString()!, PackAssetKind.Font, name) : null;
            return new(family, path);
        }
        var plan = new TypographyPlan(Face("display", "Segoe UI"), Face("heading", "Segoe UI"), Face("body", "Segoe UI"), Face("mono", "Consolas"));
        if (!spec.TryGetProperty("styles", out var styles)) return plan;
        if (context.ContractVersion < 4 || styles.ValueKind != JsonValueKind.Object)
        {
            context.Error("styles", "Semantic typography styles require Contract 4.");
            return plan;
        }
        var overrides = new Dictionary<string, TypeStyle>(StringComparer.Ordinal);
        foreach (var property in styles.EnumerateObject())
        {
            if (!TokenAuthority.Foundations.TypeStyles.TryGetValue(property.Name, out var basis)
                || property.Value.ValueKind != JsonValueKind.Object)
            {
                context.Error("styles", $"Unknown or invalid typography style '{property.Name}'.");
                continue;
            }
            var value = property.Value;
            overrides[property.Name] = basis with
            {
                FontRole = context.Enum(value, "font", ["display", "heading", "body", "mono"], basis.FontRole),
                Size = context.Number(value, "size", basis.Size, 8, 96),
                Weight = (int)context.Number(value, "weight", basis.Weight, 100, 900),
                LineHeight = context.Number(value, "lineHeight", basis.LineHeight, 8, 144),
                ColorRole = context.Color(value, "color", basis.ColorRole.StartsWith("token:") ? basis.ColorRole : "token:" + basis.ColorRole),

            };
        }
        return plan with { Styles = overrides };
    }

    private static ControlSkinPlan CompileControlSkin(JsonElement spec, SpecContext context)
    {
        var fallback = ReadControlSkin(spec, context);
        var families = new Dictionary<string, ControlSkinPlan>(StringComparer.Ordinal);
        if (spec.TryGetProperty("families", out var source))
        {
            if (source.ValueKind != JsonValueKind.Object) context.Error("families", "Control families must be an object.");
            else foreach (var property in source.EnumerateObject())
            {
                if (property.Name is not ("button" or "icon-button" or "chip" or "tab" or "input" or "toggle" or "slider" or "scrollbar")
                    && !(context.ContractVersion >= 4 && property.Name is ("chrome" or "navigation" or "breadcrumb")))
                    context.Error("families", $"Unknown control family '{property.Name}'.");
                else if (property.Value.ValueKind != JsonValueKind.Object)
                    context.Error("families", "A control family must declare an object.");
                else families[property.Name] = ReadControlSkin(property.Value, context, fallback);
            }
        }
        var icons = new Dictionary<string, SemanticIconSkin>(StringComparer.Ordinal);
        if (spec.TryGetProperty("icons", out var iconSource))
        {
            if (context.ContractVersion < 4 || iconSource.ValueKind != JsonValueKind.Object)
                context.Error("icons", "Semantic icon skinning requires Contract 4.");
            else foreach (var property in iconSource.EnumerateObject())
            {
                if (!IconCatalog.IsKnownKey(property.Name) || property.Value.ValueKind != JsonValueKind.Object)
                {
                    context.Error("icons", $"Unknown or invalid icon '{property.Name}'.");
                    continue;
                }
                var data = context.String(property.Value, "path", "");
                var selected = context.OptionalString(property.Value, "selectedPath");
                if (!ValidIcon(data) || (selected is not null && !ValidIcon(selected)))
                {
                    context.Error("icons", $"Invalid or unbounded vector path for '{property.Name}'.");
                    continue;
                }
                icons[property.Name] = new(data, selected, context.Number(property.Value, "strokeWidth", IconCatalog.StrokeWidth, 0.25, 6));
            }
        }
        NavigationScenePlan? navigation = null;
        if (spec.TryGetProperty("navigation", out var nav))
        {
            if (context.ContractVersion < 4 || nav.ValueKind != JsonValueKind.Object)
                context.Error("navigation", "Navigation scenes require Contract 4.");
            else if (CompileSurface(nav, context, DefinitionKinds.ControlSkin) is { } scene)
                navigation = new(scene, context.Enum(nav, "dock", ["top", "bottom", "left", "right"], "top"),
                    context.Number(nav, "extent", 120, 48, 240), context.Bool(nav, "immersive", false))
                { OverlayHome = context.Bool(nav, "overlayHome", false) };
            else context.Error("navigation", "Navigation requires a scene.");
        }
        var stripeOpacity = 0.0;
        if (spec.TryGetProperty("breadcrumbStripeOpacity", out _))
        {
            if (context.ContractVersion < 4) context.Error("breadcrumbStripeOpacity", "Breadcrumb stripes require Contract 4.");
            else stripeOpacity = context.Number(spec, "breadcrumbStripeOpacity", 0, 0, 0.25);
        }
        return fallback with { Families = families, Icons = icons, Navigation = navigation, BreadcrumbStripeOpacity = stripeOpacity };

        static bool ValidIcon(string data)
        {
            if (data.Length is < 1 or > 4096 || !IconCatalog.IsSafePathData(data)) return false;
            using var path = SKPath.ParseSvgPathData(data);
            if (path is null || path.IsEmpty || path.PointCount > 256) return false;
            var bounds = path.Bounds;
            return float.IsFinite(bounds.Left) && float.IsFinite(bounds.Right)
                && float.IsFinite(bounds.Top) && float.IsFinite(bounds.Bottom)
                && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 24 && bounds.Bottom <= 24;
        }
    }

    private static ControlSkinPlan ReadControlSkin(JsonElement spec, SpecContext context, ControlSkinPlan? fallback = null) => new ControlSkinPlan(
        context.Number(spec, "radius", fallback?.Radius ?? 6, 0, context.ContractVersion >= 4 ? 64 : 24), context.Number(spec, "borderWidth", fallback?.BorderWidth ?? 1, 0, 3),
        context.Number(spec, "horizontalPadding", fallback?.HorizontalPadding ?? 10, 4, 24), context.Number(spec, "verticalPadding", fallback?.VerticalPadding ?? 5, 2, 12),
        context.Number(spec, "height", fallback?.Height ?? 30, 24, context.ContractVersion >= 4 ? 96 : 48), context.Number(spec, "scrollbarWidth", fallback?.ScrollbarWidth ?? 8, 6, 16),
        context.Color(spec, "fill", fallback?.Fill ?? "token:surface3"), context.Color(spec, "hoverFill", fallback?.HoverFill ?? "token:surfaceHover"),
        context.Color(spec, "pressedFill", fallback?.PressedFill ?? "token:surfacePressed"), context.Color(spec, "border", fallback?.Border ?? "token:borderDefault"),
        context.Color(spec, "focus", fallback?.Focus ?? "token:focus"))
    {
        Material = context.Enum(spec, "material", ["flat", "bevel", "inset", "luminous"], fallback?.Material ?? "flat"),
        Edge = context.Enum(spec, "edge", ["all", "bottom", "leading"], fallback?.Edge ?? "all"),
        Relief = context.Number(spec, "relief", fallback?.Relief ?? 0, 0, 0.45),
        ThumbWidth = context.Number(spec, "thumbWidth", fallback?.ThumbWidth ?? 20, 12, 28),
        ThumbHeight = context.Number(spec, "thumbHeight", fallback?.ThumbHeight ?? 20, 12, 28),
        PassiveChipHeight = context.ContractVersion >= 4 && spec.TryGetProperty("passiveHeight", out _)
            ? context.Number(spec, "passiveHeight", 20, 12, 96) : fallback?.PassiveChipHeight,
        IconButtonSize = context.ContractVersion >= 4 && spec.TryGetProperty("iconButtonSize", out _)
            ? context.Number(spec, "iconButtonSize", 28, 24, 96) : fallback?.IconButtonSize,
        SelectedUnderline = context.ContractVersion >= 4 && context.Bool(spec, "selectedUnderline", fallback?.SelectedUnderline ?? false),
        SelectedFill = context.ContractVersion >= 4 && spec.TryGetProperty("selectedFill", out _)
            ? context.Color(spec, "selectedFill", "token:surfaceSelected") : fallback?.SelectedFill,
    };

    private static EffectPlan? CompileEffect(JsonElement spec, SpecContext context)
    {
        var targets = context.StringList(spec, "targets").ToHashSet(StringComparer.Ordinal);
        if (targets.Count == 0 || targets.Any(t => t != "all" && !PresentationEffectTargets.IsKnown(t)))
            context.Error("targets", "Effects declare compatible engine attachment targets.");
        var environment = CompileBackdrop(spec, context, deriveEffectVariants: true);
        if (environment?.Variants.Values.Any(v => v.Layers.Any(l => l.Kind is "image" or "video" or "lottie")) == true)
            context.Error("variants", "Effects use engine primitives; media sources belong to Backdrops.");
        if (environment?.Variants.Values.Any(v => v.Layers.Count > 3 || v.Layers.Sum(l => l.Count) > 80) == true)
            context.Error("variants", "An effect stack may contain at most three layers and eighty particles.");
        if (environment?.Variants.Values.Any(v => v.Layers.Sum(l => l.Opacity) > 0.6) == true)
            context.Error("variants", "Combined effect opacity must not exceed 0.6; content must remain readable.");
        if (environment is not null) environment = PresentationVisualPolicy.PreserveEffectContinuity(environment);
        return environment is null ? null : new EffectPlan(targets,
            context.Enum(spec, "channel", ["environment", "particles", "lighting", "surface", "interaction"], "environment"),
            environment, context.Bool(spec, "interactionOnly", false));
    }
}
