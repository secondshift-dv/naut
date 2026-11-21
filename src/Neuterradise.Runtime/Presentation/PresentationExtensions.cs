using System.Text.Json;

namespace Neuterradise.App.Presentation;

public sealed record SurfaceNode(string Kind, string? Semantic, double Spacing, double Padding,
    IReadOnlyList<string> Columns, IReadOnlyList<SurfaceNode> Children)
{
    public bool Modular { get; init; }
    public IReadOnlyList<string> Rows { get; init; } = [];
    public NodeLayout Placement { get; init; } = new();
    public SurfaceNode? Narrow { get; init; }
    public double Breakpoint { get; init; } = 800;

    public bool HasRegion(string id) => Semantic == id || Children.Any(child => child.HasRegion(id));
}

public sealed record TypographyFace(string Family, string? AssetPath);
public sealed record TypographyPlan(TypographyFace Display, TypographyFace Heading, TypographyFace Body, TypographyFace Mono);

public sealed record ControlSkinPlan(double Radius, double BorderWidth, double HorizontalPadding, double VerticalPadding,
    double Height, double ScrollbarWidth, string Fill, string HoverFill, string PressedFill, string Border, string Focus)
{
    public string Material { get; init; } = "flat";
    public string Edge { get; init; } = "all";
    public double Relief { get; init; } = 0;
    public double ThumbWidth { get; init; } = 20;
    public double ThumbHeight { get; init; } = 20;
    public IReadOnlyDictionary<string, ControlSkinPlan> Families { get; init; } = new Dictionary<string, ControlSkinPlan>();
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
        if (!spec.TryGetProperty("surface", out var root)) return null;
        var spotlight = kind == DefinitionKinds.SpotlightStyle;
        var modular = spotlight || context.Bool(root, "modular", false);
        if (spotlight && context.ContractVersion < 3)
            context.Error("surface", "Independent Spotlight regions require Presentation Contract 3.");
        if (modular && context.ContractVersion < 2)
            context.Error("surface.modular", "Modular surfaces require Presentation Contract 2.");
        string[] semantics = kind == DefinitionKinds.HomeLayout
            ? ["featured", "recent", "favorites", "profiles", "activity", "statistics", "attention", "imports", "discovery", "local-navigation"]
            : ["identity", "overview", "related", "faces", "recent-media", "media", "actions", "review", "statistics"];
        if (spotlight) semantics = ["artwork", "identity", "pagination"];
        if (modular && kind == DefinitionKinds.ProfileLayout)
            semantics = [.. semantics, "banner", "cover", "figure"];

        var used = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
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
                if (type is "semantic" or "spacer" or "divider")
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

            var hasSize = node.TryGetProperty("width", out _) || node.TryGetProperty("height", out _);
            if (hasSize && (context.ContractVersion < 3 || !modular))
                context.Error("surface", "Region dimensions require a modular Presentation Contract 3 surface.");
            return new SurfaceNode(type, semantic, context.Number(node, "spacing", 12, 0, 64),
                context.Number(node, "padding", 0, 0, 64), columns, children)
            {
                Modular = modular,
                Rows = rows,
                Placement = modular ? new NodeLayout(
                    Width: node.TryGetProperty("width", out _) ? context.Number(node, "width", 320, semantic == "figure" ? 200 : 48, 1440) : null,
                    Height: node.TryGetProperty("height", out _) ? context.Number(node, "height", 320, semantic == "figure" ? 240 : 24, 960) : null,
                    Row: (int)context.Number(node, "row", 0, 0, 7),
                    Column: (int)context.Number(node, "column", 0, 0, 7),
                    RowSpan: (int)context.Number(node, "rowSpan", 1, 1, 8),
                    ColumnSpan: (int)context.Number(node, "columnSpan", 1, 1, 8)) : new(),
            };
        }

        SurfaceNode? ReadArrangement(JsonElement source)
        {
            used.Clear();
            var result = Read(source, 0);
            string[] required = spotlight ? ["artwork", "identity"] : kind == DefinitionKinds.HomeLayout
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

    private static string ReadIdentityPlacement(JsonElement spec, SpecContext context)
    {
        if (!spec.TryGetProperty("identityPlacement", out _)) return "legacy";
        if (context.ContractVersion < 2)
            context.Error("identityPlacement", "Spotlight identity placement requires Presentation Contract 2.");
        return context.Enum(spec, "identityPlacement", ["beside", "below", "overlay"], "below");
    }

    private static ArtworkPlacement? ReadArtworkPlacement(JsonElement layer, string property, SpecContext context, int order)
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
            (int)context.Number(value, "order", order, 0, MaxBackdropLayers - 1));
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
        foreach (var layer in layers.EnumerateArray())
        {
            if (layer.ValueKind != JsonValueKind.Object)
            {
                context.Error(name, "An artwork layer must be an object.");
                continue;
            }
            var kind = context.Enum(layer, "type", ["artwork", "image", "scrim", "effect"], "artwork");
            if (kind != "image" && !roles.Add(kind))
                context.Error(name, $"Artwork layer '{kind}' appears more than once.");
            var path = kind == "image" ? context.Asset(context.String(layer, "asset", ""), PackAssetKind.Image, name) : null;
            plans.Add(new(kind, path, Rotation(layer),
                ReadArtworkPlacement(layer, "placement", context, plans.Count),
                ReadArtworkPlacement(layer, "narrow", context, plans.Count)));
        }
        if (!roles.Contains("artwork")) context.Error(name, "The artwork layer is required.");
        var groupRotation = Rotation(artwork);
        var animated = plans.Count(layer => layer.Rotation is not null) + (groupRotation is null ? 0 : 1);
        if (animated > context.Definition.Performance.MaxAnimatedLayers)
            context.Error(name, "Artwork rotation exceeds its declared animation budget.");
        return new(maskPath, groupRotation, plans,
            artwork.TryGetProperty("aspect", out _) ? context.Number(artwork, "aspect", 1, 0.5, 4) : null,
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
        return new(Face("display", "Segoe UI"), Face("heading", "Segoe UI"), Face("body", "Segoe UI"), Face("mono", "Consolas"));
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
                if (property.Name is not ("button" or "icon-button" or "chip" or "tab" or "input" or "toggle" or "slider" or "scrollbar"))
                    context.Error("families", $"Unknown control family '{property.Name}'.");
                else if (property.Value.ValueKind != JsonValueKind.Object)
                    context.Error("families", "A control family must declare an object.");
                else families[property.Name] = ReadControlSkin(property.Value, context, fallback);
            }
        }
        return fallback with { Families = families };
    }

    private static ControlSkinPlan ReadControlSkin(JsonElement spec, SpecContext context, ControlSkinPlan? fallback = null) => new ControlSkinPlan(
        context.Number(spec, "radius", fallback?.Radius ?? 6, 0, 24), context.Number(spec, "borderWidth", fallback?.BorderWidth ?? 1, 0, 3),
        context.Number(spec, "horizontalPadding", fallback?.HorizontalPadding ?? 10, 4, 24), context.Number(spec, "verticalPadding", fallback?.VerticalPadding ?? 5, 2, 12),
        context.Number(spec, "height", fallback?.Height ?? 30, 24, 48), context.Number(spec, "scrollbarWidth", fallback?.ScrollbarWidth ?? 8, 6, 16),
        context.Color(spec, "fill", fallback?.Fill ?? "token:surface3"), context.Color(spec, "hoverFill", fallback?.HoverFill ?? "token:surfaceHover"),
        context.Color(spec, "pressedFill", fallback?.PressedFill ?? "token:surfacePressed"), context.Color(spec, "border", fallback?.Border ?? "token:borderDefault"),
        context.Color(spec, "focus", fallback?.Focus ?? "token:focus"))
    {
        Material = context.Enum(spec, "material", ["flat", "bevel", "inset", "luminous"], fallback?.Material ?? "flat"),
        Edge = context.Enum(spec, "edge", ["all", "bottom", "leading"], fallback?.Edge ?? "all"),
        Relief = context.Number(spec, "relief", fallback?.Relief ?? 0, 0, 0.45),
        ThumbWidth = context.Number(spec, "thumbWidth", fallback?.ThumbWidth ?? 20, 12, 28),
        ThumbHeight = context.Number(spec, "thumbHeight", fallback?.ThumbHeight ?? 20, 12, 28),
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
