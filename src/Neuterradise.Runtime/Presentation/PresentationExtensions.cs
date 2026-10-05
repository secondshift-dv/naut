using System.Text.Json;

namespace Neuterradise.App.Presentation;

public sealed record SurfaceNode(string Kind, string? Semantic, double Spacing, double Padding,
    IReadOnlyList<string> Columns, IReadOnlyList<SurfaceNode> Children);

public sealed record TypographyFace(string Family, string? AssetPath);
public sealed record TypographyPlan(TypographyFace Display, TypographyFace Heading, TypographyFace Body, TypographyFace Mono);

public sealed record ControlSkinPlan(double Radius, double BorderWidth, double HorizontalPadding, double VerticalPadding,
    double Height, double ScrollbarWidth, string Fill, string HoverFill, string PressedFill, string Border, string Focus)
{
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
        var semantics = kind switch
        {
            DefinitionKinds.HomeLayout => new[] { "featured", "recent", "favorites", "profiles", "activity", "statistics", "attention", "imports", "discovery", "local-navigation" },
            DefinitionKinds.ProfileLayout => new[] { "identity", "overview", "related", "faces", "recent-media", "media", "actions", "review", "statistics" },
            _ => Array.Empty<string>(),
        };
        var used = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        SurfaceNode? Read(JsonElement node, int depth)
        {
            if (++count > MaxCompositionNodes || depth > MaxCompositionDepth)
            {
                context.Error("surface", "Surface composition exceeds the node or nesting budget.");
                return null;
            }
            var type = context.Enum(node, "type", ["stack", "columns", "panel", "overlay", "spacer", "divider", "semantic"], "stack");
            string? semantic = null;
            if (type == "semantic")
            {
                semantic = context.Enum(node, "id", semantics, semantics.FirstOrDefault() ?? "");
                if (!used.Add(semantic)) context.Error("surface", $"Semantic region '{semantic}' may appear only once.");
            }
            var children = new List<SurfaceNode>();
            if (node.TryGetProperty("children", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in array.EnumerateArray())
                    if (Read(child, depth + 1) is { } parsed) children.Add(parsed);
            }
            return new SurfaceNode(type, semantic, context.Number(node, "spacing", 12, 0, 64),
                context.Number(node, "padding", 0, 0, 64), context.Tracks(node, "columns"), children);
        }
        var result = Read(root, 0);
        var required = kind == DefinitionKinds.HomeLayout ? new[] { "featured", "activity", "statistics" }
            : new[] { "identity", "overview", "media", "related", "faces", "actions", "review" };
        foreach (var semantic in required.Where(s => !used.Contains(s))) context.Error("surface", $"Required region '{semantic}' is missing.");
        return result;
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

    private static ControlSkinPlan ReadControlSkin(JsonElement spec, SpecContext context, ControlSkinPlan? fallback = null) => new(
        context.Number(spec, "radius", fallback?.Radius ?? 6, 0, 24), context.Number(spec, "borderWidth", fallback?.BorderWidth ?? 1, 0, 3),
        context.Number(spec, "horizontalPadding", fallback?.HorizontalPadding ?? 10, 4, 24), context.Number(spec, "verticalPadding", fallback?.VerticalPadding ?? 5, 2, 12),
        context.Number(spec, "height", fallback?.Height ?? 30, 28, 48), context.Number(spec, "scrollbarWidth", fallback?.ScrollbarWidth ?? 8, 6, 16),
        context.Color(spec, "fill", fallback?.Fill ?? "token:surface3"), context.Color(spec, "hoverFill", fallback?.HoverFill ?? "token:surfaceHover"),
        context.Color(spec, "pressedFill", fallback?.PressedFill ?? "token:surfacePressed"), context.Color(spec, "border", fallback?.Border ?? "token:borderDefault"),
        context.Color(spec, "focus", fallback?.Focus ?? "token:focus"));

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
