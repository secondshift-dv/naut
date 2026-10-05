using System.Diagnostics.CodeAnalysis;

namespace Neuterradise.App.Presentation;

/// <summary>
/// Versioned Presentation Contract (document 01). Built-in and user presentation travel the same path:
/// pack → registry → bindings → resolver → compiler → immutable render plan → renderer.
/// </summary>
public static class PresentationContract
{
    /// <summary>The contract version this renderer implements.</summary>
    public const int Version = 1;

    /// <summary>The Presentation Pack manifest schema this build reads.</summary>
    public const int PackSchemaVersion = 1;

    /// <summary>Binding state payload schema.</summary>
    public const int BindingSchemaVersion = 1;

    /// <summary>The application-owned pack. It is an ordinary pack: same reader, validator and compiler.</summary>
    public const string BuiltInPackId = "builtin.neuterradise";
}

/// <summary>The family a definition kind belongs to (document 01 §4).</summary>
public enum DefinitionFamily
{
    Structural,
    Visual,
    MotionEnvironment,
}

/// <summary>Registered definition kinds. New kinds are added here plus a compiler; no schema change is needed.</summary>
public static class DefinitionKinds
{
    public const string Theme = "theme";
    public const string HomeLayout = "home-layout";
    public const string Backdrop = "backdrop";
    public const string SpotlightStyle = "spotlight-style";
    public const string GalleryLayout = "gallery-layout";
    public const string ProfileCard = "profile-card";
    public const string ProfileLayout = "profile-layout";
    public const string ProfileMediaLayout = "profile-media-layout";
    public const string CoverFrame = "cover-frame";
    public const string Typography = "typography";
    public const string ControlSkin = "control-skin";
    public const string Effect = "effect";

    public static IReadOnlyDictionary<string, DefinitionFamily> Families { get; } = new Dictionary<string, DefinitionFamily>(StringComparer.Ordinal)
    {
        [Theme] = DefinitionFamily.Visual,
        [HomeLayout] = DefinitionFamily.Structural,
        [Backdrop] = DefinitionFamily.MotionEnvironment,
        [SpotlightStyle] = DefinitionFamily.Visual,
        [GalleryLayout] = DefinitionFamily.Structural,
        [ProfileCard] = DefinitionFamily.Visual,
        [ProfileLayout] = DefinitionFamily.Structural,
        [ProfileMediaLayout] = DefinitionFamily.Structural,
        [CoverFrame] = DefinitionFamily.Visual,
        [Typography] = DefinitionFamily.Visual,
        [ControlSkin] = DefinitionFamily.Visual,
        [Effect] = DefinitionFamily.MotionEnvironment,
    };

    public static IReadOnlyCollection<string> All => (IReadOnlyCollection<string>)Families.Keys;

    public static bool IsKnown(string? kind) => kind is not null && Families.ContainsKey(kind);
}

/// <summary>Where a binding is persisted. Built-in defaults are never persisted.</summary>
public enum ScopeKind
{
    Global,
    Surface,
    Profile,
    Item,
}

/// <summary>Which scope actually supplied a resolved value; the UI shows this as inherited/overridden.</summary>
public enum ResolutionSource
{
    BuiltInDefault,
    Global,
    Surface,
    Profile,
    Item,
}

/// <summary>Stable identity of a definition: pack + definition id. Display names are never identity.</summary>
public readonly record struct DefinitionRef(string PackId, string DefinitionId)
{
    public override string ToString() => $"{PackId}/{DefinitionId}";

    public static bool TryParse(string? text, [NotNullWhen(true)] out DefinitionRef? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var slash = text.IndexOf('/');
        if (slash <= 0 || slash == text.Length - 1)
        {
            return false;
        }

        value = new DefinitionRef(text[..slash], text[(slash + 1)..]);
        return true;
    }

    public static DefinitionRef BuiltIn(string definitionId) => new(PresentationContract.BuiltInPackId, definitionId);
}

/// <summary>How the Customization Center edits a slot. Complex kinds bind to specialised editors.</summary>
public enum EditorKind
{
    DefinitionChooser,
    CoverEditor,
    BannerEditor,
}

/// <summary>Customization Center sections (document 01 §21).</summary>
public static class CustomizationCategories
{
    // Global Appearance is edited in Settings ? General, not in the Customization Center rail.
    public const string Appearance = "appearance";
    public const string Home = "home";
    public const string Gallery = "gallery";
    public const string Profile = "profile";
    public static IReadOnlyList<string> Ordered { get; } = [Home, Gallery, Profile];
}

/// <summary>
/// One customizable slot with its editor discovery metadata. The Customization Center renders sections
/// from this table, so a new definition kind appears without editing unrelated pages.
/// </summary>
public sealed record SlotDescriptor(
    string Id,
    string? Kind,
    string Category,
    string LabelKey,
    string LabelFallback,
    string DescriptionFallback,
    IReadOnlyList<ScopeKind> Scopes,
    string? Surface,
    DefinitionRef? Default,
    EditorKind Editor)
{
    public bool IsStateOnly => Kind is null;

    public bool AllowsScope(ScopeKind scope) => Scopes.Contains(scope);
}

/// <summary>Customization slots. Slot ids are stable persisted identifiers.</summary>
public static class PresentationSlots
{
    public const string Theme = "appearance.theme";
    public const string Typography = "appearance.typography";
    public const string ControlSkin = "appearance.control-skin";
    public const string ProfileBackdrop = "profile.backdrop";
    public const string HomeLayout = "home.layout";
    public const string HomeBackdrop = "home.backdrop";
    public const string HomeSpotlight = "home.spotlight";
    public const string GalleryLayout = "gallery.layout";
    public const string ProfileCard = "profile.card";
    public const string ProfileLayout = "profile.layout";
    public const string ProfileMediaLayout = "profile.media.presentation";
    public const string ProfileCover = "profile.cover";
    public const string ProfileBanner = "profile.banner";
    public const string ProfileFrame = "profile.frame";
    public const string AppEffect = "effect.app";
    public const string HomeEffect = "effect.home";
    public const string GalleryEffect = "effect.gallery";
    public const string SettingsEffect = "effect.settings";
    public const string ImportEffect = "effect.import";
    public const string ProfileEffect = "effect.profile";
    public const string BackdropEffect = "effect.profile.backdrop";
    public const string BannerEffect = "effect.profile.banner";
    public const string CoverEffect = "effect.profile.cover";
    public const string FrameEffect = "effect.profile.frame";
    public const string CardEffect = "effect.card";
    public const string MediaEffect = "effect.media";
    public const string ControlsEffect = "effect.controls";

    private static readonly ScopeKind[] GlobalOnly = [ScopeKind.Global];
    private static readonly ScopeKind[] SurfaceOnly = [ScopeKind.Surface];
    private static readonly ScopeKind[] ProfileOnly = [ScopeKind.Profile];

    public static IReadOnlyList<SlotDescriptor> All { get; } =
    [
        new(Theme, DefinitionKinds.Theme, CustomizationCategories.Appearance, "Customize.Theme", "Theme",
            "Colour, material and shape.", GlobalOnly, null,
            BuiltInPresentationCatalog.DefaultFor(Theme), EditorKind.DefinitionChooser),
        new(Typography, DefinitionKinds.Typography, CustomizationCategories.Appearance, "Customize.Typography", "Typography",
            "Independent font families with multilingual fallback.", GlobalOnly, null,
            BuiltInPresentationCatalog.DefaultFor(Typography), EditorKind.DefinitionChooser),
        new(ControlSkin, DefinitionKinds.ControlSkin, CustomizationCategories.Appearance, "Customize.ControlSkin", "Control Skin",
            "Control shapes and visual states.", GlobalOnly, null,
            BuiltInPresentationCatalog.DefaultFor(ControlSkin), EditorKind.DefinitionChooser),

        new(HomeLayout, DefinitionKinds.HomeLayout, CustomizationCategories.Home, "Customize.Home.Layout", "Layout",
            "How the Spotlight and the rails below it are arranged.", SurfaceOnly, "home",
            BuiltInPresentationCatalog.DefaultFor(HomeLayout), EditorKind.DefinitionChooser),
        new(HomeBackdrop, DefinitionKinds.Backdrop, CustomizationCategories.Home, "Customize.Home.Backdrop", "Backdrop",
            "The living background behind Home.", SurfaceOnly, "home",
            BuiltInPresentationCatalog.DefaultFor(HomeBackdrop), EditorKind.DefinitionChooser),
        new(HomeSpotlight, DefinitionKinds.SpotlightStyle, CustomizationCategories.Home, "Customize.Home.Spotlight", "Spotlight",
            "Composition and motion of the featured Profile.", SurfaceOnly, "home",
            BuiltInPresentationCatalog.DefaultFor(HomeSpotlight), EditorKind.DefinitionChooser),

        new(GalleryLayout, DefinitionKinds.GalleryLayout, CustomizationCategories.Gallery, "Customize.Gallery.Layout", "Gallery Layout",
            "Search, filters, collection layout and virtualized browsing. Each Profile owns its Card composition.", SurfaceOnly, "gallery",
            BuiltInPresentationCatalog.DefaultFor(GalleryLayout), EditorKind.DefinitionChooser),
        new(ProfileCard, DefinitionKinds.ProfileCard, CustomizationCategories.Profile, "Customize.Profile.Card", "Card",
            "How this Profile is represented in Gallery and other card collections.", ProfileOnly, "profile",
            BuiltInPresentationCatalog.DefaultFor(ProfileCard), EditorKind.DefinitionChooser),
        new(ProfileBackdrop, DefinitionKinds.Backdrop, CustomizationCategories.Profile, "Customize.Profile.Backdrop", "Backdrop",
            "The background behind this Profile.", ProfileOnly, "profile",
            BuiltInPresentationCatalog.DefaultFor(ProfileBackdrop), EditorKind.DefinitionChooser),

        new(ProfileLayout, DefinitionKinds.ProfileLayout, CustomizationCategories.Profile, "Customize.Profile.Layout", "Layout",
            "Arrangement of the Banner, Cover, identity and sections.", ProfileOnly, "profile",
            BuiltInPresentationCatalog.DefaultFor(ProfileLayout), EditorKind.DefinitionChooser),
        new(ProfileMediaLayout, DefinitionKinds.ProfileMediaLayout, CustomizationCategories.Profile, "Customize.Profile.MediaLayout", "Media Layout",
            "How this Profile's media cards are composed. Media data and hover assets remain unchanged.", ProfileOnly, "profile",
            BuiltInPresentationCatalog.DefaultFor(ProfileMediaLayout), EditorKind.DefinitionChooser),
        new(ProfileCover, null, CustomizationCategories.Profile, "Customize.Profile.Cover", "Cover",
            "Prepared Cover media selection for this Profile.", ProfileOnly, "profile",
            null, EditorKind.CoverEditor),
        new(ProfileBanner, null, CustomizationCategories.Profile, "Customize.Profile.Banner", "Banner",
            "Prepared Banner media selection for this Profile.", ProfileOnly, "profile",
            null, EditorKind.BannerEditor),
        new(ProfileFrame, DefinitionKinds.CoverFrame, CustomizationCategories.Profile, "Customize.Profile.Frame", "Frame",
            "The collectible frame around the Cover.", ProfileOnly, "profile",
            BuiltInPresentationCatalog.DefaultFor(ProfileFrame), EditorKind.DefinitionChooser),
        EffectSlot(AppEffect, "App", CustomizationCategories.Appearance, GlobalOnly, null),
        EffectSlot(ControlsEffect, "Controls", CustomizationCategories.Appearance, GlobalOnly, null),
        EffectSlot(HomeEffect, "Effects", CustomizationCategories.Home, SurfaceOnly, "home"),
        EffectSlot(GalleryEffect, "Effects", CustomizationCategories.Gallery, SurfaceOnly, "gallery"),
        EffectSlot(SettingsEffect, "Settings Effects", CustomizationCategories.Appearance, SurfaceOnly, "settings"),
        EffectSlot(ImportEffect, "Import Effects", CustomizationCategories.Appearance, SurfaceOnly, "import"),
        EffectSlot(ProfileEffect, "Profile Effects", CustomizationCategories.Profile, ProfileOnly, "profile"),
        EffectSlot(BackdropEffect, "Backdrop Effects", CustomizationCategories.Profile, ProfileOnly, "profile"),
        EffectSlot(BannerEffect, "Banner Effects", CustomizationCategories.Profile, ProfileOnly, "profile"),
        EffectSlot(CoverEffect, "Cover Effects", CustomizationCategories.Profile, ProfileOnly, "profile"),
        EffectSlot(FrameEffect, "Frame Effects", CustomizationCategories.Profile, ProfileOnly, "profile"),
        EffectSlot(CardEffect, "Card Effects", CustomizationCategories.Profile, ProfileOnly, "profile"),
        EffectSlot(MediaEffect, "Media Effects", CustomizationCategories.Profile, ProfileOnly, "profile"),

    ];

    private static SlotDescriptor EffectSlot(string id, string label, string category, ScopeKind[] scopes, string? surface) =>
        new(id, DefinitionKinds.Effect, category, $"Customize.{id}", label,
            "Optional decoration. Motion follows accessibility and resource limits.", scopes, surface,
            BuiltInPresentationCatalog.DefaultFor(id), EditorKind.DefinitionChooser);

    private static readonly Dictionary<string, SlotDescriptor> ById = All.ToDictionary(slot => slot.Id, StringComparer.Ordinal);

    public static bool TryGet(string? id, [NotNullWhen(true)] out SlotDescriptor? descriptor)
    {
        descriptor = null;
        return id is not null && ById.TryGetValue(id, out descriptor);
    }

    public static SlotDescriptor Get(string id) =>
        ById.TryGetValue(id, out var descriptor)
            ? descriptor
            : throw new ArgumentException($"Unknown presentation slot '{id}'.", nameof(id));

    public static IEnumerable<SlotDescriptor> ForKind(string kind) => All.Where(slot => slot.Kind == kind);
}

/// <summary>
/// Engine effect attachment points are deliberately broader than the built-in customization UI.
/// Third-party packs may target any engine slot here; Naut exposes Home, Profile and independent Card effect pickers.
/// </summary>
public static class PresentationEffectTargets
{
    public static IReadOnlySet<string> All { get; } = PresentationSlots.All
        .Where(slot => slot.Kind == DefinitionKinds.Effect)
        .Select(slot => slot.Id)
        .ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> BuiltInUserFacing { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        PresentationSlots.HomeEffect,
        PresentationSlots.ProfileEffect,
        PresentationSlots.CardEffect,
    };

    public static IReadOnlySet<string> HomeCascadeTargets { get; } = new HashSet<string>(StringComparer.Ordinal);

    public static IReadOnlySet<string> ProfileCascadeTargets { get; } = new HashSet<string>(StringComparer.Ordinal);

    public static bool IsKnown(string target) => All.Contains(target);
}

/// <summary>
/// Semantic data slots are the stable bridge between domain data and presentation definitions.
/// Definitions name these, never tables, columns or view-model members.
/// </summary>
public static class SemanticSlots
{
    public const string ProfileId = "profile.identity.id";
    public const string ProfileName = "profile.identity.name";
    public const string ProfileCover = "profile.identity.cover";
    public const string ProfileBanner = "profile.identity.banner";
    public const string ProfileFrame = "profile.identity.frame";
    public const string ProfileRating = "profile.identity.rating";
    public const string ProfileFavorite = "profile.identity.favorite";
    public const string ProfileCategory = "profile.identity.category";
    public const string ProfileTags = "profile.identity.tags";
    public const string ProfileOverview = "profile.overview";
    public const string ProfileNotes = "profile.notes";
    public const string ProfileMedia = "profile.media.collection";
    public const string ProfileRelated = "profile.related.collection";
    public const string ProfileStatistics = "profile.statistics";
    public const string ProfileMediaCount = "profile.statistics.media-count";
    public const string ProfileRelatedCount = "profile.statistics.related-count";
    public const string ProfileOpenDirectory = "profile.location.open-directory-action";

    public const string MediaId = "media.id";
    public const string MediaName = "media.name";
    public const string MediaType = "media.type";
    public const string MediaDuration = "media.duration";
    public const string MediaDimensions = "media.dimensions";
    public const string MediaFavorite = "media.favorite";
    public const string MediaRating = "media.rating";
    public const string MediaExif = "media.exif";
    public const string MediaExifCamera = "media.exif.camera";
    public const string MediaExifLens = "media.exif.lens";
    public const string MediaExifCaptureTime = "media.exif.capture-time";
    public const string MediaRelations = "media.relations";
    public const string MediaOpenDefault = "media.file.open-default-action";
    public const string MediaOpenDirectory = "media.file.open-directory-action";

    public const string GalleryItems = "gallery.items";
    public const string GallerySelection = "gallery.selection";
    public const string GalleryQuery = "gallery.query";
    public const string GalleryFilters = "gallery.filters";
    public const string GallerySort = "gallery.sort";
    public const string GalleryPagination = "gallery.pagination";
    public const string GalleryLayoutState = "gallery.layout-state";

    public const string ImportSource = "import.source";
    public const string ImportDestination = "import.destination-profile";
    public const string ImportReview = "import.review";
    public const string ImportProgress = "import.progress";
    public const string ImportActivity = "import.activity";
    public const string ImportNeedsAttention = "import.needs-attention";
    public const string ImportActions = "import.actions";

    /// <summary>Slots a card composition may bind to (text, image and badge nodes).</summary>
    public static IReadOnlySet<string> CardBindable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ProfileName, ProfileCover, ProfileBanner, ProfileFrame, ProfileRating, ProfileFavorite,
        ProfileCategory, ProfileTags, ProfileOverview, ProfileMediaCount, ProfileRelatedCount,
    };
}
