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
        _cache.GetOrAdd((pack.PackId, definition.Id, pack.ContentHash), cacheKey =>
        {
            var compiled = CompileCore(pack.Manifest, pack.Origin, definition, _assets, _themes, out _, pack.ContentHash);
            var identity = $"pack-art:{pack.PackId}:{cacheKey.Hash}:{definition.Id}";
            if (compiled is null) return null;
            compiled = compiled with { ContentIdentity = identity };
            return compiled.Plan switch
            {
                ThemePlan theme => compiled with { Plan = theme with { ArtworkIdentity = identity } },
                FramePlan frame => compiled with { Plan = frame with { OverlayAssetIdentity = identity } },
                _ => compiled,
            };
        });

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
        out IReadOnlyList<PackDiagnostic> diagnostics, string? contentHash = null)
    {
        var context = new SpecContext(manifest, definition, assets, contentHash);
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

        string? artwork = null;
        if (!spec.TryGetProperty("artwork", out var art)
            || art.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(art.GetString()))
        {
            context.Error("artwork", "Every Theme requires a declared Image asset for its representative thumbnail.");
        }
        else
        {
            artwork = context.Asset(art.GetString()!, PackAssetKind.Image, "artwork");
        }
        string? dropzone = null;
        if (spec.TryGetProperty("importDropzoneArtwork", out _))
        {
            if (context.ContractVersion < 4)
                context.Error("importDropzoneArtwork", "Import dropzone artwork requires Contract 4.");
            else
                dropzone = context.Asset(context.String(spec, "importDropzoneArtwork", ""), PackAssetKind.Image, "importDropzoneArtwork");
        }
        InspectorPhonePlan? phone = null;
        if (spec.TryGetProperty("inspectorPhone", out var phoneSpec))
        {
            if (context.ContractVersion < 4 || phoneSpec.ValueKind != JsonValueKind.Object)
                context.Error("inspectorPhone", "The Media Inspector phone shell requires Contract 4 and an object.");
            else phone = new InspectorPhonePlan(
                context.Asset(context.String(phoneSpec, "brandArtwork", ""), PackAssetKind.Image, "inspectorPhone.brandArtwork"),
                context.Color(phoneSpec, "bezel", "#FF100D18"),
                context.Color(phoneSpec, "rim", "token:borderStrong"));
        }
        return new ThemePlan(theme, material, artwork, ImportDropzoneArtwork: dropzone, InspectorPhone: phone);
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
            Direction: context.Enum(layer, "direction", ["up", "down", "left", "right", "diagonal", "still", "radial"], "up"),
            ParticleShape: context.Enum(layer, "particleShape", ["dot", "ring", "line", "petal", "leaf", "diamond", "pixel", "sparkle", "bokeh"], "dot"),
            Drift: context.Number(layer, "drift", 0.015, 0, 0.2),
            Twinkle: context.Number(layer, "twinkle", 0.45, 0, 0.5),
            SpawnRegion: context.Enum(layer, "spawnRegion", ["all", "upper", "lower", "edges"], "all"),
            AssetIdentity: assetPath is null ? null : context.ContentHash);
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
                var plan = new HomeRailPlan(
                    context.Enum(rail, "id", RailIds, "recent"),
                    context.Bool(rail, "visible", true),
                    context.Enum(rail, "style", context.ContractVersion >= 4
                        ? ["poster", "tile", "compact", "strip", "cover-only", "hud"]
                        : ["poster", "tile", "compact", "strip"], "tile"),
                    (int)context.Number(rail, "maxItems", 12, 3, 24))
                {
                    CoverScale = context.ContractVersion >= 4
                        ? context.Number(rail, "coverScale", 1, 0.5, 1) : 1,
                    Motion = context.ContractVersion >= 4
                        ? context.Enum(rail, "motion", ["static", "reel"], "static") : "static",
                    ReelSpeed = context.ContractVersion >= 4
                        ? context.Number(rail, "reelSpeed", 24, 8, 60) : 24,
                };
                if (plan.Motion == "reel" && (plan.Id != "favorites" || plan.Style != "cover-only"))
                    context.Error("rails.motion", "Reel motion requires the cover-only Favorites rail.");
                rails.Add(plan);
            }
        }

        var surface = CompileSurface(spec, context, DefinitionKinds.HomeLayout);
        var stage = context.ContractVersion >= 4 && context.Bool(spec, "viewportStage", false);
        if (stage && (surface is not { Kind: "canvas", Modular: true, Narrow: null } || surface.Padding != 0))
            context.Error("viewportStage", "A viewport stage requires a single unpadded modular canvas.");
        return new HomeLayoutPlan(
            hero.ValueKind == JsonValueKind.Object ? context.Enum(hero, "placement", ["full-bleed", "inset", "split"], "full-bleed") : "full-bleed",
            hero.ValueKind == JsonValueKind.Object ? context.Number(hero, "heightFraction", 0.38, 0.25, 0.7) : 0.38,
            hero.ValueKind == JsonValueKind.Object ? context.Number(hero, "minHeight", 240, 180, 520) : 240,
            hero.ValueKind == JsonValueKind.Object ? context.Number(hero, "maxHeight", 420, 280, 1000) : 420,
            rails, surface) { ViewportStage = stage };
    }

    private static SpotlightPlan CompileSpotlight(JsonElement spec, SpecContext context) => new(
        context.Enum(spec, "composition", context.ContractVersion >= 4
            ? ["cinematic-left", "centered-poster", "split-glass", "editorial", "storefront", "snapshot", "cinema", "marquee"]
            : ["cinematic-left", "centered-poster", "split-glass", "editorial", "storefront", "snapshot", "cinema"], "cinematic-left"),
        context.Enum(spec, "motion", ["ken-burns", "parallax-drift", "still"], "ken-burns"),
        context.Enum(spec, "transition", ["crossfade-depth", "slide", "fade"], "crossfade-depth"),
        context.Number(spec, "rotationSeconds", 12, 10, 60),
        context.Bool(spec, "showCover", true),
        context.Bool(spec, "showOverview", true),
        context.Enum(spec, "pagination", ["counter-rail", "counter", "none"], "counter-rail"),
        context.Number(spec, "scrimStrength", 0.65, 0.3, 1),
        CompileArtwork(spec, context, "artwork"), ReadIdentityPlacement(spec, context),
        CompileSurface(spec, context, DefinitionKinds.SpotlightStyle))
        { Marquee = ReadMarquee(spec, context) };

    private static MarqueePlan ReadMarquee(JsonElement spec, SpecContext context)
    {
        if (!spec.TryGetProperty("marquee", out var marquee)) return new();
        if (context.ContractVersion < 4 || marquee.ValueKind != JsonValueKind.Object
            || !string.Equals(context.String(spec, "composition", ""), "marquee", StringComparison.OrdinalIgnoreCase))
        {
            context.Error("marquee", "Marquee options require a Contract 4 marquee composition.");
            return new();
        }
        return new(
            context.Number(marquee, "angle", 0, -12, 12),
            context.Number(marquee, "spacing", 0, 0, 24),
            context.Number(marquee, "inset", 0, 0, 24),
            context.Bool(marquee, "showMetadata", true),
            context.Color(marquee, "textColor", "token:textInverse"));
    }

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
        GalleryScenePlan? scene = null;
        if (spec.TryGetProperty("scene", out var authored))
        {
            if (context.ContractVersion < 4 || authored.ValueKind != JsonValueKind.Object)
                context.Error("scene", "Gallery architecture requires Contract 4.");
            else
            {
                BackdropPlan? ReadEnvironment(string name)
                {
                    if (!authored.TryGetProperty(name, out var environment)) return null;
                    if (environment.ValueKind != JsonValueKind.Object)
                    {
                        context.Error("scene." + name, "Declare a backdrop variant object.");
                        return null;
                    }
                    return CompileBackdrop(environment, context);
                }
                var background = ReadEnvironment("background");
                var foreground = ReadEnvironment("foreground");
                var overlay = context.OptionalString(authored, "itemOverlay");
                var sizing = context.Enum(authored, "cardSizing", ["contain", "intrinsic", "crop"], "contain");
                if (sizing == "crop" && primitive is (CollectionPrimitives.AdaptiveWall or CollectionPrimitives.SingleColumn)
                    && context.String(spec, "toolbar", "standard") == "feed")
                    context.Error("scene.cardSizing", "Catalogue detail rows require uncropped Cards.");
                if (sizing == "intrinsic" && primitive is (CollectionPrimitives.Carousel or CollectionPrimitives.List or CollectionPrimitives.SingleColumn))
                    context.Error("scene.cardSizing", "Intrinsic masonry requires a vertical grid or shelf collection.");
                var fixedSlot = context.Bool(authored, "fixedSlot", false);
                var compactColumns = (int)context.Number(authored, "compactColumns", 0, 0, 16);
                if (fixedSlot && (aspect is null || pattern is not null || sizing != "contain"
                    || primitive is not (CollectionPrimitives.Grid or CollectionPrimitives.UniformGrid or CollectionPrimitives.AdaptiveWall)))
                    context.Error("scene.fixedSlot", "A fixed slot requires itemAspect, contain sizing, and a uniform vertical grid without heightPattern.");
                if (compactColumns > 0 && !fixedSlot)
                    context.Error("scene.compactColumns", "Compact columns require a fixed slot.");
                var viewportArtwork = context.OptionalString(authored, "viewportArtwork");
                var actionArtwork = context.OptionalString(authored, "actionArtwork");
                if (viewportArtwork is not null && background is not null)
                    context.Error("scene.viewportArtwork", "Use either viewport artwork or a field background.");
                scene = new(background, foreground,
                    overlay is null ? null : context.Asset(overlay, PackAssetKind.Image, "scene.itemOverlay"),
                    context.Number(authored, "itemInset", 0, 0, 0.3), sizing,
                    context.Number(authored, "cardRotation", 0, -20, 20),
                    context.Number(authored, "cardSkew", 0, -20, 20),
                    context.Enum(authored, "overlayPlacement", ["front", "behind"], "front"),
                    fixedSlot, compactColumns,
                    viewportArtwork is null ? null : context.Asset(viewportArtwork, PackAssetKind.Image, "scene.viewportArtwork"),
                    context.Number(authored, "headerSpace", 0, 0, .35),
                    actionArtwork is null ? null : context.Asset(actionArtwork, PackAssetKind.Image, "scene.actionArtwork"),
                    context.Color(authored, "actionText", "#FFFF68C8"),
                    context.Color(authored, "actionHoverText", "#FFFFC5EA"));
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
            pattern, scene);
    }

    private static ProfileLayoutPlan? CompileProfileLayout(JsonElement spec, SpecContext context)
    {
        ProfilePaperPlan? paper = null;
        if (spec.TryGetProperty("paper", out var paperSpec))
        {
            if (context.ContractVersion < 4 || paperSpec.ValueKind != JsonValueKind.Object)
                context.Error("paper", "A paper surface requires a Contract 4 object.");
            else
            {
                string Solid(string key, string fallback)
                {
                    var color = context.Color(paperSpec, key, fallback);
                    if (!color.StartsWith("#FF", StringComparison.OrdinalIgnoreCase) || color.Length != 9)
                        context.Error("paper." + key, "Paper colors must be opaque #AARRGGBB literals.");
                    return color;
                }
                paper = new(Solid("background", "#FFF8F4F4"), Solid("ink", "#FF21182C"), Solid("accent", "#FFC51B68"))
                {
                    ShowBackdrop = context.Bool(paperSpec, "showBackdrop", false),
                    SurfaceOpacity = context.Number(paperSpec, "surfaceOpacity", 1, 0, 1),
                };
            }
        }
        ProfileLayoutPlan Compile(ProfileLayoutDefinition layout)
        {
            var scene = CompileSurface(spec, context, DefinitionKinds.ProfileLayout);
            if (paper is not null && scene is not { Modular: true })
                context.Error("paper", "Paper surfaces require a modular scene.");
            return new(layout, scene, CompileArtwork(spec, context, "bannerArtwork"), paper)
            { Visuals = ReadComponentVisuals(spec, context, media: false) };
        }
        if (spec.TryGetProperty("preset", out var preset) && preset.ValueKind == JsonValueKind.String)
        {
            if (!ProfileLayoutResolver.TryGetBuiltInPreset(preset.GetString(), out var builtIn))
            {
                context.Error("preset", $"'{preset.GetString()}' is not a built-in Profile layout preset.");
                return null;
            }

            return Compile(builtIn);
        }

        if (spec.TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object)
        {
            var parsed = new ProfileLayoutResolver().ParseDefinition(layout.GetRawText(), context.Definition.Id);
            foreach (var diagnostic in parsed.Diagnostics)
            {
                context.Error("layout", $"{diagnostic.Code}: {diagnostic.Detail}");
            }

            return parsed.Definition is null ? null : Compile(parsed.Definition);
        }

        context.Error("layout", "A Profile layout names a built-in 'preset' or embeds a 'layout' object.");
        return null;
    }

    private static ProfileMediaLayoutPlan CompileProfileMediaLayout(JsonElement spec, SpecContext context)
    {
        var layoutId = context.Enum(
            spec,
            "layoutId",
            [.. MediaLayoutCatalog.CompatibleLayoutIds],
            MediaLayoutCatalog.FallbackLayoutId);
        var placement = context.Enum(
            spec,
            "metadataPlacement",
            ["hover-overlay", "persistent-overlay", "below", "side", "hidden", "live-overlay"],
            "hover-overlay");
        if (placement == "live-overlay" && context.ContractVersion < 4)
            context.Error("metadataPlacement", "Live media overlays require Contract 4.");
        var frameStyle = context.Enum(
            spec,
            "frameStyle",
            ["none", "hairline", "gallery", "double", "luminous", "neon"],
            "hairline");
        var fields = context.StringList(spec, "metadataFields")
            .Where(field => field is "name" or "type" or "format" or "duration" or "dimensions" or "favorite" or "added")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var browse = context.Enum(spec, "browse", ["wall", "workbench", "editorial", "reel"], "wall");
        if (browse == "reel") browse = "wall";
        MediaCollectionPlan? collection = null;
        if (spec.TryGetProperty("collection", out var collectionSpec))
        {
            if (context.ContractVersion < 4 || collectionSpec.ValueKind != JsonValueKind.Object)
                context.Error("collection", "Authored media collection geometry requires Contract 4.");
            else collection = new(context.Enum(collectionSpec, "layout", ["grid", "editorial"], "grid"),
                (int)context.Number(collectionSpec, "maxColumns", 12, 1, 12),
                context.Number(collectionSpec, "rowSpacing", 10, 0, 48),
                context.Number(collectionSpec, "columnSpacing", 10, 0, 48));
        }

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
                "live-overlay" => MediaMetadataPlacement.LiveOverlay,
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
            context.Bool(spec, "showTypeBadge", true))
            { Visuals = ReadComponentVisuals(spec, context, media: true) },
            browse, collection);
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

        CompositionNode? hoverRoot = null;
        if (spec.TryGetProperty("hoverComposition", out var hoverComposition))
        {
            if (context.ContractVersion < 4 || hoverComposition.ValueKind != JsonValueKind.Object || hover != "banner")
                context.Error("hoverComposition", "An independent Banner hover composition requires Contract 4 and hover: banner.");
            else
                hoverRoot = CompileNode(hoverComposition, context, "hoverComposition", 0);
            if (hoverRoot is not CardCanvasNode || root is not CardCanvasNode
                || CountBannerHosts(hoverRoot) != 1 || CountBannerHosts(hoverRoot, bannerOnly: true) != 1)
                context.Error("hoverComposition", "Use independent canvas roots and exactly one Banner image in the hover canvas.");
        }

        var plan = new CardPlan(
            context.Number(spec, "aspect", 1, 0.3, 5),
            context.Enum(spec, "art", ["cover", "banner", "cover-over-banner", "none"], "cover"),
            context.Enum(spec, "density", ["minimal", "standard", "detailed"], "standard"),
            hover,
            frameMode,
            context.OptionalString(spec, "legacyVariant"),
            root, hoverRoot);

        if (plan.NodeCount > MaxCompositionNodes)
        {
            context.Error("composition", $"A card may use at most {MaxCompositionNodes} nodes.");
            return null;
        }

        if (hover == "banner" && hoverRoot is null && CountBannerHosts(root) != 1)
        {
            context.Error("composition", "Banner hover requires exactly one Banner image or non-ambient Cover image.");
            return null;
        }

        return plan;
    }

    private static int CountBannerHosts(CompositionNode? node, bool bannerOnly = false) => node switch
    {
        ImageNode image when image.Slot == SemanticSlots.ProfileBanner
            || (!bannerOnly && image.Slot == SemanticSlots.ProfileCover && !image.Ambient) => 1,
        CardCanvasNode canvas => canvas.Children.Sum(child => CountBannerHosts(child, bannerOnly)),
        StackNode stack => stack.Children.Sum(child => CountBannerHosts(child, bannerOnly)),
        GridNode grid => grid.Children.Sum(child => CountBannerHosts(child, bannerOnly)),
        OverlayNode overlay => overlay.Children.Sum(child => CountBannerHosts(child, bannerOnly)),
        _ => 0,
    };

    private static readonly string[] NodeTypes = ["stack", "grid", "overlay", "text", "image", "frame", "badge", "scrim", "shape", "icon", "canvas", "asset"];

    private static CompositionNode? CompileNode(JsonElement node, SpecContext context, string at, int depth)
    {
        if (depth > MaxCompositionDepth)
        {
            context.Error(at, $"Compositions may nest at most {MaxCompositionDepth} levels.");
            return null;
        }

        var type = context.Enum(node, "type", NodeTypes, "stack");
        if (context.ContractVersion < 4 && (type is "canvas" or "asset" || node.TryGetProperty("position", out _)))
            context.Error(at, "Card canvas artwork requires Contract 4.");
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
            VisibleWhen: node.TryGetProperty("visibleWhen", out _) ? context.Slot(node, "visibleWhen", at) : null,
            CanvasPlacement: ReadArtworkPlacement(node, "position", context, 0, canvas: true));

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

        var ratingStyle = type == "badge" && context.OptionalString(node, "style") == "stars";
        if (ratingStyle && (context.ContractVersion < 4 || context.OptionalString(node, "slot") != SemanticSlots.ProfileRating))
            context.Error(at, "Stars are a Contract 4 Profile rating badge.");
        CardCanvasNode Canvas()
        {
            var children = Children();
            foreach (var child in children)
                if (child.Layout.CanvasPlacement is not { } placement || placement.X < 0 || placement.Y < 0
                    || placement.X + placement.Width > 1.000001 || placement.Y + placement.Height > 1.000001
                    || placement.Horizontal != "left" || placement.Vertical != "top")
                    context.Error(at, "Canvas children require bounded normalized top-left positions.");
            return new CardCanvasNode(layout, children);
        }

        return type switch
        {
            "canvas" => Canvas(),
            "asset" => new CardAssetNode(layout,
                context.Asset(context.String(node, "asset", ""), PackAssetKind.Image, at + ".asset"),
                context.Bool(node, "foreground", false)),
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
                context.Bool(node, "onMedia", false))
            {
                OutlineColor = node.TryGetProperty("outlineColor", out _) && context.ContractVersion >= 4
                    ? context.Color(node, "outlineColor", "#FF000000") : null,
                OutlineWidth = context.ContractVersion >= 4 ? context.Number(node, "outlineWidth", 1, 0.5, 2) : 1,
            },
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
                context.Enum(node, "style", ["glass", "solid", "outline", "text", "stars"], "glass")),
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

    private sealed class SpecContext(PresentationPackManifest manifest, PresentationDefinition definition, IPresentationAssetStore? assets, string? contentHash)
    {
        public int ContractVersion => Manifest.MinContractVersion;
        public string? ContentHash { get; } = contentHash;
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
