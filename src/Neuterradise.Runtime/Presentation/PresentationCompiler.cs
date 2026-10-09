using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.MediaLayouts;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Design.Themes;

namespace Neuterradise.App.Presentation;

/// <summary>
/// validate → resolve slots/assets/tokens → compile an immutable plan → cache by pack content hash.
/// The renderer never parses a pack; the hot path only reads plans (document 01 §25).
/// </summary>
public sealed partial class PresentationCompiler
{
    public const int MaxCompositionDepth = 8;
    public const int MaxCompositionNodes = 48;
    public const int MaxBackdropLayers = 8;
    public const int MaxParticles = 160;

    private readonly IPresentationAssetStore? _assets;
    private readonly ConcurrentDictionary<(string Pack, string Definition, string Hash), CompiledDefinition?> _cache = new();
    private readonly ThemeLoader _themes = new();

    public PresentationCompiler(IPresentationAssetStore? assets) => _assets = assets;

    public int CachedCount => _cache.Count;

    public void Invalidate(string packId)
    {
        foreach (var key in _cache.Keys.Where(k => k.Pack == packId).ToList())
        {
            _cache.TryRemove(key, out _);
        }
    }

    /// <summary>Compiles (or returns the cached plan for) one definition. Returns null when it fails closed.</summary>
    public CompiledDefinition? Compile(InstalledPack pack, PresentationDefinition definition) =>
        _cache.GetOrAdd((pack.PackId, definition.Id, pack.ContentHash), cacheKey => CompileCore(pack.Manifest, pack.Origin, definition, _assets, _themes, out _));

    /// <summary>Install-time validation: compiles every definition without an asset store.</summary>
    public static IReadOnlyList<PackDiagnostic> ValidateDefinitions(PresentationPackManifest manifest)
    {
        var diagnostics = new List<PackDiagnostic>();
        var themes = new ThemeLoader();
        foreach (var definition in manifest.Definitions.Where(d => DefinitionKinds.IsKnown(d.Kind)))
        {
            var compiled = CompileCore(manifest, PackOrigin.User, definition, null, themes, out var errors);
            if (compiled is null)
            {
                if (errors.Count > 0) diagnostics.AddRange(errors);
                else diagnostics.Add(new(PackDiagnosticCodes.SpecInvalid, definition.Id, "The definition could not be compiled."));
            }
            else
            {
                diagnostics.AddRange(compiled.Diagnostics);
            }
        }

        return diagnostics;
    }

    private static CompiledDefinition? CompileCore(
        PresentationPackManifest manifest,
        PackOrigin origin,
        PresentationDefinition definition,
        IPresentationAssetStore? assets,
        ThemeLoader themes,
        out IReadOnlyList<PackDiagnostic> diagnostics)
    {
        var context = new SpecContext(manifest, definition, assets);
        diagnostics = context.Diagnostics;
        try
        {
            using var document = JsonDocument.Parse(definition.SpecJson);
            var spec = document.RootElement;
            object? plan = definition.Kind switch
            {
                DefinitionKinds.Theme => CompileTheme(spec, context, themes),
                DefinitionKinds.Backdrop => CompileBackdrop(spec, context),
                DefinitionKinds.HomeLayout => CompileHomeLayout(spec, context),
                DefinitionKinds.SpotlightStyle => CompileSpotlight(spec, context),
                DefinitionKinds.GalleryLayout => CompileGalleryLayout(spec, context),
                DefinitionKinds.ProfileCard => CompileCard(spec, context),
                DefinitionKinds.ProfileLayout => CompileProfileLayout(spec, context),
                DefinitionKinds.ProfileMediaLayout => CompileProfileMediaLayout(spec, context),
                DefinitionKinds.CoverFrame => CompileFrame(spec, context),
                DefinitionKinds.Typography => CompileTypography(spec, context),
                DefinitionKinds.ControlSkin => CompileControlSkin(spec, context),
                DefinitionKinds.Effect => CompileEffect(spec, context),
                _ => null,
            };

            if (plan is null || context.HasErrors)
            {
                return null;
            }

            if (IsNontrivialBackdrop(plan) && definition.Performance.MaxAnimatedLayers > MaxBackdropLayers)
            {
                context.Error("performance.maxAnimatedLayers", $"At most {MaxBackdropLayers} animated layers are allowed.");
                return null;
            }

            return new CompiledDefinition(
                definition.RefIn(manifest.PackId),
                definition.Kind,
                definition.Name,
                definition.Description,
                definition.Tags,
                origin,
                definition.PreviewAccent,
                definition.Performance,
                plan,
                context.Diagnostics);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            context.Error("spec", exception.Message);
            return null;
        }
    }

    private static bool IsNontrivialBackdrop(object plan) => plan is BackdropPlan;

    // ---------------------------------------------------------------- appearance

    private static ThemePlan? CompileTheme(JsonElement spec, SpecContext context, ThemeLoader themes)
    {
        ThemeDefinition? theme = null;
        if (spec.TryGetProperty("themeId", out var themeId) && themeId.ValueKind == JsonValueKind.String)
        {
            var result = themes.LoadBuiltInTheme(themeId.GetString()!);
            theme = result.Theme;
            if (theme is null)
            {
                context.Error("themeId", $"Built-in theme '{themeId.GetString()}' is not available.");
            }
        }
        else if (spec.TryGetProperty("theme", out var inline) && inline.ValueKind == JsonValueKind.Object)
        {
            var parsed = themes.ParseTheme(inline.GetRawText(), context.Definition.Id);
            theme = parsed.Theme;
            foreach (var diagnostic in parsed.Diagnostics)
            {
                context.Error("theme", $"{diagnostic.Code}: {diagnostic.Detail}");
            }
        }
        else
        {
            context.Error("theme", "A theme definition names a built-in 'themeId' or embeds a full 'theme' object.");
        }

        if (theme is null)
        {
            return null;
        }

        var material = MaterialSpec.Neutral;
        if (spec.TryGetProperty("material", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            material = new MaterialSpec(
                context.Enum(m, "personality", ["grounded", "smoked-glass", "white-frost", "brass-metal", "energy-glass", "milky-paper", "ink-glass", "chroma-glass", "tactical-acrylic"], "grounded"),
                context.Enum(m, "atmosphere", ["none", "fog", "daylight", "dust", "electric", "editorial", "ink", "chroma", "precision"], "none"),
                context.Number(m, "grain", 0.02, 0, 0.2),
                context.Number(m, "glassOpacity", 0.9, 0.5, 1),
                context.Number(m, "glassBlur", 12, 0, 40),
                context.Number(m, "specular", 0.3, 0, 1),
                context.Number(m, "rimLight", 0.3, 0, 1),
                context.Color(m, "ambientPrimary", "token:accent"),
                context.Color(m, "ambientSecondary", "token:accentSecondary"),
                context.StringList(m, "frameAffinity"));
        }

        return new ThemePlan(theme, material);
    }

    // ---------------------------------------------------------------- backdrop / environment

    private static readonly string[] LayerKinds =
        ["gradient", "radial-glow", "image", "video", "lottie", "fog", "particles", "noise", "vignette", "light-sweep", "scrim", "calm-tint", "media-tint", "grid-field", "wave-field"];

    private static BackdropPlan? CompileBackdrop(JsonElement spec, SpecContext context, bool deriveEffectVariants = false)
    {
        if (!spec.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Object)
        {
            context.Error("variants", "A backdrop declares 'full', 'reduced' and 'fallback' variants.");
            return null;
        }

        var result = new Dictionary<VisualTier, BackdropVariant>();
        foreach (var (name, tier) in new[] { ("full", VisualTier.Full), ("reduced", VisualTier.Reduced), ("fallback", VisualTier.Fallback) })
        {
            if (!variants.TryGetProperty(name, out var variant) || variant.ValueKind != JsonValueKind.Object)
            {
                if (deriveEffectVariants && tier != VisualTier.Full && !variants.TryGetProperty(name, out _))
                {
                    result[tier] = new BackdropVariant([]);
                    continue;
                }
                context.Error($"variants.{name}", $"The '{name}' variant is mandatory for every backdrop.");
                continue;
            }

            var layers = new List<BackdropLayer>();
            if (variant.TryGetProperty("layers", out var layerArray) && layerArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var layer in layerArray.EnumerateArray())
                {
                    if (layers.Count >= MaxBackdropLayers)
                    {
                        context.Error($"variants.{name}.layers", $"At most {MaxBackdropLayers} layers are allowed.");
                        break;
                    }

                    var parsed = CompileLayer(layer, context, $"variants.{name}");
                    if (parsed is not null)
                    {
                        layers.Add(parsed);
                    }
                }
            }

            result[tier] = new BackdropVariant(layers);
        }

        if (result.TryGetValue(VisualTier.Fallback, out var fallback) && fallback.IsAnimated)
        {
            context.Error("variants.fallback", "The fallback variant must be static (no continuous motion).");
        }

        var plan = new BackdropPlan(result);
        return deriveEffectVariants ? plan : PresentationVisualPolicy.PreserveBackdropContinuity(plan);
    }

    private static BackdropLayer? CompileLayer(JsonElement layer, SpecContext context, string at)
    {
        var kind = context.Enum(layer, "kind", LayerKinds, "gradient");
        string? assetPath = null;
        string? source = null;
        if (kind is "image" or "video" or "lottie")
        {
            if (layer.TryGetProperty("asset", out var asset) && asset.ValueKind == JsonValueKind.String)
            {
                var assetKind = kind == "video" ? PackAssetKind.Video : kind == "lottie" ? PackAssetKind.Lottie : PackAssetKind.Image;
                assetPath = context.Asset(asset.GetString()!, assetKind, $"{at}.asset");
            }
            else
            {
                source = context.Enum(layer, "source", ["media-ambient", "media-banner", "media-cover"], "media-ambient");
            }
        }

        IReadOnlyList<GradientStopSpec>? stops = null;
        if (layer.TryGetProperty("stops", out var stopArray) && stopArray.ValueKind == JsonValueKind.Array)
        {
            stops = [.. stopArray.EnumerateArray().Take(8).Select(stop => new GradientStopSpec(
                context.Color(stop, "color", "token:canvas"),
                context.Number(stop, "offset", 0, 0, 1)))];
        }

        return new BackdropLayer(
            kind,
            Opacity: context.Number(layer, "opacity", 1, 0, 1),
            Color: layer.TryGetProperty("color", out _) ? context.Color(layer, "color", "token:accent") : null,
            Color2: layer.TryGetProperty("color2", out _) ? context.Color(layer, "color2", "token:accentSecondary") : null,
            Stops: stops,
            Angle: context.Number(layer, "angle", 90, 0, 360),
            CenterX: context.Number(layer, "centerX", 0.5, -0.5, 1.5),
            CenterY: context.Number(layer, "centerY", 0.5, -0.5, 1.5),
            Radius: context.Number(layer, "radius", 0.6, 0.05, 2),
            Speed: context.Number(layer, "speed", 1, 0, 4),
            Amplitude: context.Number(layer, "amplitude", 0, 0, 0.5),
            Period: context.Number(layer, "period", 20, 2, 120),
            Depth: context.Number(layer, "depth", 0, 0, 1),
            Blur: context.Number(layer, "blur", 0, 0, 64),
            Scale: context.Number(layer, "scale", 1, 0.5, 3),
            Count: (int)context.Number(layer, "count", 0, 0, MaxParticles),
            SizeMin: context.Number(layer, "sizeMin", 1, 0.2, 12),
            SizeMax: context.Number(layer, "sizeMax", 3, 0.2, 24),
            Width: context.Number(layer, "width", 0.2, 0.02, 1),
            AssetPath: assetPath,
            Source: source,
            Blend: context.Enum(layer, "blend", ["normal", "screen", "multiply", "overlay", "soft-light", "plus"], "normal"),
            Direction: context.Enum(layer, "direction", ["up", "down", "diagonal", "still", "radial"], "up"),
            ParticleShape: context.Enum(layer, "particleShape", ["dot", "ring", "line", "petal", "leaf", "diamond"], "dot"));
    }

    // ---------------------------------------------------------------- structure

    private static readonly string[] RailIds = ["recent", "favorites", "profiles", "activity", "statistics", "attention", "imports", "discovery"];

    private static HomeLayoutPlan CompileHomeLayout(JsonElement spec, SpecContext context)
    {
        var hero = spec.TryGetProperty("hero", out var h) && h.ValueKind == JsonValueKind.Object ? h : default;
        var rails = new List<HomeRailPlan>();
        if (spec.TryGetProperty("rails", out var railArray) && railArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var rail in railArray.EnumerateArray().Take(12))
            {
                rails.Add(new HomeRailPlan(
                    context.Enum(rail, "id", RailIds, "recent"),
                    context.Bool(rail, "visible", true),
                    context.Enum(rail, "style", ["poster", "tile", "compact", "strip"], "tile"),
                    (int)context.Number(rail, "maxItems", 12, 3, 24)));
            }
        }

        return new HomeLayoutPlan(
            hero.ValueKind == JsonValueKind.Object ? context.Enum(hero, "placement", ["full-bleed", "inset", "split"], "full-bleed") : "full-bleed",
            hero.ValueKind == JsonValueKind.Object ? context.Number(hero, "heightFraction", 0.38, 0.25, 0.7) : 0.38,
            hero.ValueKind == JsonValueKind.Object ? context.Number(hero, "minHeight", 240, 180, 520) : 240,
            hero.ValueKind == JsonValueKind.Object ? context.Number(hero, "maxHeight", 420, 280, 1000) : 420,
            rails, CompileSurface(spec, context, DefinitionKinds.HomeLayout));
    }

    private static SpotlightPlan CompileSpotlight(JsonElement spec, SpecContext context) => new(
        context.Enum(spec, "composition", ["cinematic-left", "centered-poster", "split-glass", "editorial", "storefront", "snapshot", "cinema"], "cinematic-left"),
        context.Enum(spec, "motion", ["ken-burns", "parallax-drift", "still"], "ken-burns"),
        context.Enum(spec, "transition", ["crossfade-depth", "slide", "fade"], "crossfade-depth"),
        context.Number(spec, "rotationSeconds", 12, 10, 60),
        context.Bool(spec, "showCover", true),
        context.Bool(spec, "showOverview", true),
        context.Enum(spec, "pagination", ["counter-rail", "counter", "none"], "counter-rail"),
        context.Number(spec, "scrimStrength", 0.65, 0.3, 1));

    private static GalleryLayoutPlan? CompileGalleryLayout(JsonElement spec, SpecContext context)
    {
        var primitive = context.String(spec, "primitive", CollectionPrimitives.Grid);
        if (!CollectionPrimitives.All.Contains(primitive))
        {
            context.Error("primitive", "Gallery layouts must use an approved virtualized primitive.");
            return null;
        }

        double? aspect = spec.TryGetProperty("itemAspect", out var a) && a.ValueKind == JsonValueKind.Number
            ? context.Number(spec, "itemAspect", 1, 0.3, 4)
            : null;
        IReadOnlyList<double>? pattern = null;
        if (spec.TryGetProperty("heightPattern", out var patternElement))
        {
            if (patternElement.ValueKind != JsonValueKind.Array || patternElement.GetArrayLength() is < 1 or > 8)
                context.Error("heightPattern", "Use one to eight bounded item aspects.");
            else
            {
                pattern = patternElement.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN).ToArray();
                if (pattern.Any(v => !double.IsFinite(v) || v < 0.5 || v > 2.5)) context.Error("heightPattern", "Item aspects must be between 0.5 and 2.5.");
            }
        }
        return new GalleryLayoutPlan(
            primitive,
            context.Number(spec, "itemMinWidth", 220, 120, 640),
            context.Number(spec, "itemMaxWidth", 420, 160, 960),
            aspect,
            context.Number(spec, "spacing", 16, 4, 40),
            context.Number(spec, "rowHeight", 72, 48, 320),
            (int)context.Number(spec, "maxColumns", 12, 1, 16),
            context.Enum(spec, "toolbar", ["standard", "feed", "catalog", "drawer"], "standard"),
            pattern);
    }

    private static ProfileLayoutPlan? CompileProfileLayout(JsonElement spec, SpecContext context)
    {
        if (spec.TryGetProperty("preset", out var preset) && preset.ValueKind == JsonValueKind.String)
        {
            if (!ProfileLayoutResolver.TryGetBuiltInPreset(preset.GetString(), out var builtIn))
            {
                context.Error("preset", $"'{preset.GetString()}' is not a built-in Profile layout preset.");
                return null;
            }

            return new ProfileLayoutPlan(builtIn, CompileSurface(spec, context, DefinitionKinds.ProfileLayout));
        }

        if (spec.TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object)
        {
            var parsed = new ProfileLayoutResolver().ParseDefinition(layout.GetRawText(), context.Definition.Id);
            foreach (var diagnostic in parsed.Diagnostics)
            {
                context.Error("layout", $"{diagnostic.Code}: {diagnostic.Detail}");
            }

            return parsed.Definition is null ? null : new ProfileLayoutPlan(parsed.Definition, CompileSurface(spec, context, DefinitionKinds.ProfileLayout));
        }

        context.Error("layout", "A Profile layout names a built-in 'preset' or embeds a 'layout' object.");
        return null;
    }

    private static ProfileMediaLayoutPlan CompileProfileMediaLayout(JsonElement spec, SpecContext context)
    {
        var layoutId = context.Enum(
            spec,
            "layoutId",
            [.. MediaLayoutCatalog.BuiltInIds],
            MediaLayoutCatalog.FallbackLayoutId);
        var placement = context.Enum(
            spec,
            "metadataPlacement",
            ["hover-overlay", "persistent-overlay", "below", "side", "hidden"],
            "hover-overlay");
        var frameStyle = context.Enum(
            spec,
            "frameStyle",
            ["none", "hairline", "gallery", "double", "luminous", "neon"],
            "hairline");
        var fields = context.StringList(spec, "metadataFields")
            .Where(field => field is "name" or "type" or "duration" or "dimensions" or "favorite" or "added")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new ProfileMediaLayoutPlan(new MediaLayoutDefinition(
            layoutId,
            context.Definition.Name,
            context.Number(spec, "cardWidth", 190, 140, 960),
            context.Number(spec, "aspect", 1, 0.5, 2.5),
            context.Number(spec, "mediaWidthFraction", 1, 0.35, 1),
            context.Enum(spec, "fit", ["fill", "fit"], "fill"),
            placement switch
            {
                "persistent-overlay" => MediaMetadataPlacement.PersistentOverlay,
                "below" => MediaMetadataPlacement.Below,
                "side" => MediaMetadataPlacement.Side,
                "hidden" => MediaMetadataPlacement.Hidden,
                _ => MediaMetadataPlacement.HoverOverlay,
            },
            fields,
            (int)context.Number(spec, "metadataLines", 2, 0, 3),
            frameStyle switch
            {
                "gallery" => MediaFrameStyle.Gallery,
                "double" => MediaFrameStyle.Double,
                "luminous" => MediaFrameStyle.Luminous,
                "neon" => MediaFrameStyle.Neon,
                "none" => MediaFrameStyle.None,
                _ => MediaFrameStyle.Hairline,
            },
            context.Number(spec, "frameRadius", 12, 0, 28),
            context.Number(spec, "frameThickness", 1, 0, 8),
            context.Color(spec, "frameColor", "token:borderSubtle"),
            context.Color(spec, "selectedFrameColor", "token:borderSelected"),
            context.Number(spec, "frameGlow", 0, 0, 1),
            context.Enum(spec, "hoverStyle", ["lift", "spotlight", "none"], "lift"),
            context.Bool(spec, "showTypeBadge", true)),
            context.Enum(spec, "browse", ["wall", "workbench", "reel"], "wall"));
    }

    private static FramePlan? CompileFrame(JsonElement spec, SpecContext context)
    {
        if (spec.TryGetProperty("frameId", out var frameId) && frameId.ValueKind == JsonValueKind.String)
        {
            if (!CoverFrameCatalog.TryGetFrame(frameId.GetString(), out var builtIn))
            {
                context.Error("frameId", $"'{frameId.GetString()}' is not a built-in frame.");
                return null;
            }

            var artwork = spec.TryGetProperty("overlayAsset", out var overlayAsset) && overlayAsset.ValueKind == JsonValueKind.String
                ? context.Asset(overlayAsset.GetString()!, PackAssetKind.Image, "overlayAsset") : null;
            var inset = spec.TryGetProperty("coverInset", out _)
                ? (double?)context.Number(spec, "coverInset", 0.2, 0, 0.4) : null;
            return new FramePlan(builtIn, artwork, inset);
        }

        var family = context.Enum(spec, "family", Enum.GetNames<CoverFrameFamily>(), nameof(CoverFrameFamily.Minimal));
        var shapes = context.StringList(spec, "shapes")
            .Select(s => Enum.TryParse<CoverShape>(s, true, out var shape) ? shape : (CoverShape?)null)
            .Where(s => s is not null).Select(s => s!.Value).ToArray();
        if (shapes.Length == 0)
        {
            shapes = [CoverShape.Circle, CoverShape.Square, CoverShape.RoundedSquare];
        }

        string? overlay = null;
        if (spec.TryGetProperty("overlayAsset", out var asset) && asset.ValueKind == JsonValueKind.String)
        {
            overlay = context.Asset(asset.GetString()!, PackAssetKind.Image, "overlayAsset");
        }

        var definition = new CoverFrameDefinition(
            context.Definition.Id,
            context.Definition.Name,
            Enum.Parse<CoverFrameFamily>(family),
            shapes,
            context.Number(spec, "scale", 1, CoverFrameCatalog.MinimumFrameScale, CoverFrameCatalog.MaximumFrameScale),
            context.Bool(spec, "tint", true),
            context.Bool(spec, "animated", false),
            context.OptionalString(spec, "reducedFallback"),
            Enum.TryParse<CoverFrameCategory>(context.String(spec, "category", "Ornate"), true, out var category) ? category : CoverFrameCategory.Ornate,
            1000);
        var coverInset = spec.TryGetProperty("coverInset", out _)
            ? (double?)context.Number(spec, "coverInset", 0.2, 0, 0.4) : null;
        return new FramePlan(definition, overlay, coverInset);
    }

    // ---------------------------------------------------------------- cards

    private static CardPlan? CompileCard(JsonElement spec, SpecContext context)
    {
        if (!spec.TryGetProperty("composition", out var composition) || composition.ValueKind != JsonValueKind.Object)
        {
            context.Error("composition", "A card declares its 'composition' in approved primitives.");
            return null;
        }

        var hover = context.Enum(spec, "hover", ["lift", "sheen", "spotlight", "banner", "none"], "lift");
        var frameMode = context.Enum(spec, "frame", ["lite", "full", "none"], "lite");
        var root = CompileNode(composition, context, "composition", depth: 0);
        if (root is null)
        {
            return null;
        }

        var plan = new CardPlan(
            context.Number(spec, "aspect", 1, 0.3, 5),
            context.Enum(spec, "art", ["cover", "banner", "cover-over-banner", "none"], "cover"),
            context.Enum(spec, "density", ["minimal", "standard", "detailed"], "standard"),
            hover,
            frameMode,
            context.OptionalString(spec, "legacyVariant"),
            root);

        if (plan.NodeCount > MaxCompositionNodes)
        {
            context.Error("composition", $"A card may use at most {MaxCompositionNodes} nodes.");
            return null;
        }

        if (hover == "banner" && CountBannerHosts(root) != 1)
        {
            context.Error("composition", "Banner hover requires exactly one Banner image or non-ambient Cover image.");
            return null;
        }

        return plan;
    }

    private static int CountBannerHosts(CompositionNode node) => node switch
    {
        ImageNode image when image.Slot == SemanticSlots.ProfileBanner
            || (image.Slot == SemanticSlots.ProfileCover && !image.Ambient) => 1,
        StackNode stack => stack.Children.Sum(CountBannerHosts),
        GridNode grid => grid.Children.Sum(CountBannerHosts),
        OverlayNode overlay => overlay.Children.Sum(CountBannerHosts),
        _ => 0,
    };

    private static readonly string[] NodeTypes = ["stack", "grid", "overlay", "text", "image", "frame", "badge", "scrim", "shape", "icon"];

    private static CompositionNode? CompileNode(JsonElement node, SpecContext context, string at, int depth)
    {
        if (depth > MaxCompositionDepth)
        {
            context.Error(at, $"Compositions may nest at most {MaxCompositionDepth} levels.");
            return null;
        }

        var type = context.Enum(node, "type", NodeTypes, "stack");
        var layout = new NodeLayout(
            MarginLeft: Margin(node, context, 0),
            MarginTop: Margin(node, context, 1),
            MarginRight: Margin(node, context, 2),
            MarginBottom: Margin(node, context, 3),
            Width: node.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? context.Number(node, "width", 0, 0, 2000) : null,
            Height: node.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? context.Number(node, "height", 0, 0, 2000) : null,
            HorizontalAlignment: context.Enum(node, "hAlign", ["stretch", "left", "center", "right"], "stretch"),
            VerticalAlignment: context.Enum(node, "vAlign", ["stretch", "top", "center", "bottom"], "stretch"),
            Row: (int)context.Number(node, "row", 0, 0, 12),
            Column: (int)context.Number(node, "column", 0, 0, 12),
            RowSpan: (int)context.Number(node, "rowSpan", 1, 1, 12),
            ColumnSpan: (int)context.Number(node, "columnSpan", 1, 1, 12),
            Opacity: context.Number(node, "opacity", 1, 0, 1),
            VisibleWhen: node.TryGetProperty("visibleWhen", out _) ? context.Slot(node, "visibleWhen", at) : null);

        List<CompositionNode> Children()
        {
            var children = new List<CompositionNode>();
            if (node.TryGetProperty("children", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var child in array.EnumerateArray())
                {
                    var compiled = CompileNode(child, context, $"{at}.children[{index++}]", depth + 1);
                    if (compiled is not null)
                    {
                        children.Add(compiled);
                    }
                }
            }

            return children;
        }

        return type switch
        {
            "stack" => new StackNode(layout,
                context.Enum(node, "orientation", ["vertical", "horizontal"], "vertical"),
                context.Number(node, "spacing", 0, 0, 48),
                context.Number(node, "padding", 0, 0, 64),
                node.TryGetProperty("background", out _) ? context.Color(node, "background", "token:surface2") : null,
                node.TryGetProperty("cornerRadius", out _) ? context.Radius(node, "cornerRadius") : null,
                Children()),
            "grid" => new GridNode(layout,
                context.Tracks(node, "rows"),
                context.Tracks(node, "columns"),
                context.Number(node, "spacing", 0, 0, 48),
                Children()),
            "overlay" => new OverlayNode(layout, Children()),
            "text" => new TextNode(layout,
                node.TryGetProperty("slot", out _) ? context.Slot(node, "slot", at) : null,
                context.OptionalString(node, "textKey"),
                context.Enum(node, "role", [.. TokenAuthority.Foundations.TypeStyles.Keys], "body"),
                context.Color(node, "color", "token:textPrimary"),
                (int)context.Number(node, "maxLines", 1, 1, 6),
                context.Enum(node, "align", ["left", "center", "right"], "left"),
                context.Bool(node, "onMedia", false)),
            "image" => new ImageNode(layout,
                context.Slot(node, "slot", at),
                context.Enum(node, "stretch", ["fill", "uniform", "uniform-to-fill"], "uniform-to-fill"),
                node.TryGetProperty("cornerRadius", out _) ? context.Radius(node, "cornerRadius") : null,
                context.Enum(node, "shape", ["rect", "circle", "cover-shape"], "rect"),
                context.Bool(node, "ambient", false),
                context.Enum(node, "fallback", ["identity", "none"], "identity")),
            "frame" => new FrameNode(layout, context.Number(node, "size", 96, 32, 320)),
            "badge" => new BadgeNode(layout,
                context.Slot(node, "slot", at),
                context.Enum(node, "style", ["glass", "solid", "outline", "text"], "glass")),
            "scrim" => new ScrimNode(layout,
                context.Enum(node, "direction", ["to-top", "to-bottom", "to-left", "to-right", "radial"], "to-top"),
                context.Color(node, "from", "token:heroScrimStrong"),
                context.Color(node, "to", "#00000000")),
            "shape" => new ShapeNode(layout,
                node.TryGetProperty("fill", out _) ? context.Color(node, "fill", "token:surface2") : null,
                node.TryGetProperty("stroke", out _) ? context.Color(node, "stroke", "token:borderSubtle") : null,
                context.Number(node, "strokeThickness", 1, 0, 6),
                node.TryGetProperty("cornerRadius", out _) ? context.Radius(node, "cornerRadius") : null),
            "icon" => new IconNode(layout,
                IconCatalog.IsKnownKey(context.String(node, "key", "icon.profile.favorite")) ? context.String(node, "key", "icon.profile.favorite") : "icon.profile.favorite",
                context.Number(node, "size", 16, 8, 48),
                context.Color(node, "color", "token:textSecondary")),
            _ => null,
        };
    }

    private static double Margin(JsonElement node, SpecContext context, int index)
    {
        if (!node.TryGetProperty("margin", out var margin))
        {
            return 0;
        }

        if (margin.ValueKind == JsonValueKind.Number)
        {
            return Math.Clamp(margin.GetDouble(), -64, 64);
        }

        if (margin.ValueKind == JsonValueKind.Array)
        {
            var values = margin.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => Math.Clamp(v.GetDouble(), -64, 64)).ToArray();
            return values.Length switch
            {
                1 => values[0],
                2 => index % 2 == 0 ? values[0] : values[1],
                4 => values[index],
                _ => 0,
            };
        }

        context.Error("margin", "Margins are a number or an array of 1, 2 or 4 numbers.");
        return 0;
    }

    // ---------------------------------------------------------------- spec reading context

    private sealed class SpecContext(PresentationPackManifest manifest, PresentationDefinition definition, IPresentationAssetStore? assets)
    {
        private readonly List<PackDiagnostic> _diagnostics = [];

        public PresentationPackManifest Manifest { get; } = manifest;

        public PresentationDefinition Definition { get; } = definition;

        public IReadOnlyList<PackDiagnostic> Diagnostics => _diagnostics;

        public bool HasErrors => _diagnostics.Any(d => d.IsError);

        public void Error(string at, string detail) => _diagnostics.Add(new(PackDiagnosticCodes.SpecInvalid, $"{Definition.Id}:{at}", detail));

        public void Warning(string at, string detail) => _diagnostics.Add(new(PackDiagnosticCodes.SpecInvalid, $"{Definition.Id}:{at}", detail, IsError: false));

        public double Number(JsonElement element, string name, double fallback, double min, double max)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                return fallback;
            }

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            {
                Error(name, "Expected a number.");
                return fallback;
            }

            if (number < min || number > max)
            {
                Error(name, string.Create(CultureInfo.InvariantCulture, $"Must be between {min} and {max}."));
                return Math.Clamp(number, min, max);
            }

            return number;
        }

        public bool Bool(JsonElement element, string name, bool fallback) =>
            element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;

        public string String(JsonElement element, string name, string fallback) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : fallback;

        public string? OptionalString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;

        public string Enum(JsonElement element, string name, IReadOnlyCollection<string> allowed, string fallback)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                return fallback;
            }

            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var match = allowed.FirstOrDefault(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                Error(name, $"'{text}' is not one of: {string.Join(", ", allowed)}.");
                return fallback;
            }

            return match;
        }

        public IReadOnlyList<string> StringList(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                ? [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).Take(32)]
                : [];

        /// <summary>A colour is a canonical token reference ("token:accent") or a literal #RRGGBB / #AARRGGBB.</summary>
        public string Color(JsonElement element, string name, string fallback)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                return fallback;
            }

            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (text is not null && TokenAuthority.IsValidColorReference(text))
            {
                return text;
            }

            Error(name, $"'{text}' is not a known colour token or #AARRGGBB colour.");
            return fallback;
        }

        public string Radius(JsonElement element, string name)
        {
            if (element.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number)
                {
                    return Math.Clamp(value.GetDouble(), 0, 999).ToString(CultureInfo.InvariantCulture);
                }

                if (value.ValueKind == JsonValueKind.String && value.GetString() is { } token && TokenAuthority.IsRadiusToken(token))
                {
                    return token;
                }
            }

            Error(name, "A radius is a number or a radius token (token:radiusSm, token:radiusCard, ...).");
            return "0";
        }

        public IReadOnlyList<string> Tracks(JsonElement element, string name)
        {
            var tracks = StringList(element, name);
            foreach (var track in tracks)
            {
                var value = track.EndsWith('*') ? track[..^1] : track;
                if (track is not ("*" or "auto") && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)
                    || !double.IsFinite(size) || size <= 0 || size > 4096))
                {
                    Error(name, $"'{track}' is not a grid track ('*', 'n*', 'auto' or a number).");
                }
            }

            return tracks.Count == 0 ? ["*"] : tracks;
        }

        public string Slot(JsonElement element, string name, string at)
        {
            var text = String(element, name, string.Empty);
            if (!SemanticSlots.CardBindable.Contains(text))
            {
                Error($"{at}.{name}", $"'{text}' is not a semantic slot a card may bind.");
                return SemanticSlots.ProfileName;
            }

            return text;
        }

        public string? Asset(string assetId, PackAssetKind kind, string at)
        {
            var entry = Manifest.Assets.FirstOrDefault(a => a.Id == assetId);
            if (entry is null || entry.Kind != kind)
            {
                Error(at, $"Asset '{assetId}' is not a {kind} asset of this pack.");
                return null;
            }

            if (assets is null)
            {
                return null;
            }

            var path = assets.ResolveAssetPath(Manifest.PackId, assetId);
            if (path is null)
            {
                Error(at, $"Asset '{assetId}' is not available.");
            }

            return path;
        }
    }
}
