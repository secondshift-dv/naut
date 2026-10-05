using Neuterradise.App.Design.CoverFrames;

namespace Neuterradise.App.Design.ProfileLayouts;

public enum ProfileLayoutBannerMode
{
    None,
    Contained,
    FullBleed,
    Split,
    Backdrop
}

public enum ProfileLayoutHeight
{
    Low,
    Medium,
    Tall
}

public enum ProfileLayoutCoverPlacement
{
    None,
    Left,
    Right,
    InlineLeft,
    InlineCenter,
    BottomLeftOverlap,
    BottomCenterOverlap
}

public enum ProfileLayoutCoverSize
{
    Small,
    Medium,
    Large,
    ExtraLarge
}

public enum ProfileLayoutIdentityAlignment
{
    Left,
    Center,
    Right
}

public enum ProfileLayoutContentWidth
{
    Normal,
    Wide
}

public enum ProfileLayoutOverlay
{
    Soft,
    Medium,
    Strong
}

public enum ProfileLayoutBannerBlur
{
    Off,
    Subtle
}

public enum ProfileLayoutFade
{
    None,
    Bottom,
    Full
}

public enum ProfileLayoutSectionDensity
{
    Compact,
    Comfortable,
    Spacious
}

public enum ProfileLayoutModuleWidth
{
    Full,
    Half,
    Third
}

public enum ProfileLayoutModuleId
{
    Overview,
    Related,
    Faces,
    RecentMedia,
    Media
}

public sealed record ProfileLayoutModule(
    ProfileLayoutModuleId Id,
    int Order,
    bool Visible,
    ProfileLayoutModuleWidth Width);

/// <summary>
/// Complete geometry contract for a Profile surface. This is resolved once from the persisted
/// layout preset and applied to stable Profile view regions; it is not a recipe for rebuilding
/// the Profile visual tree when unrelated Profile data changes.
/// </summary>
public sealed record ProfileLayoutDefinition(
    int SchemaVersion,
    string Id,
    string Name,
    ProfileLayoutBannerMode BannerMode,
    ProfileLayoutHeight Height,
    ProfileLayoutCoverPlacement CoverPlacement,
    ProfileLayoutCoverSize CoverSize,
    CoverFrameDetailLevel CoverDetailLevel,
    ProfileLayoutIdentityAlignment IdentityAlignment,
    ProfileLayoutContentWidth ContentWidth,
    ProfileLayoutOverlay Overlay,
    ProfileLayoutBannerBlur Blur,
    ProfileLayoutFade Fade,
    ProfileLayoutSectionDensity SectionDensity,
    IReadOnlyList<ProfileLayoutModule> Modules);
