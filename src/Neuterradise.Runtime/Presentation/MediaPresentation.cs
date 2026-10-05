using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Profiles;

namespace Neuterradise.App.Presentation;

/// <summary>
/// Canonical normalized presentation of one chosen media asset (document 01 §12.1). Coordinates are
/// normalized so the composition survives any slot size or aspect ratio: the layout owns the slot, the
/// media presentation owns focal point, zoom and pan.
/// </summary>
public sealed record MediaTransformState(
    string Fit,
    double FocalX,
    double FocalY,
    double Zoom,
    double OffsetX,
    double OffsetY,
    double Rotation)
{
    public static MediaTransformState Default { get; } = new("fill", 0.5, 0.5, 1, 0, 0, 0);

    public static MediaTransformState FromCover(ProfileAppearanceOverrides overrides) => new(
        overrides.CoverFit ?? "fill",
        overrides.CropX,
        overrides.CropY,
        overrides.Zoom,
        overrides.CoverOffsetX,
        overrides.CoverOffsetY,
        overrides.CoverRotation);

    public static MediaTransformState FromBanner(ProfileAppearanceOverrides overrides) => new(
        "fill",
        overrides.BannerFocusX,
        overrides.BannerFocusY,
        overrides.BannerZoom,
        0,
        0,
        0);

    public ProfileAppearanceOverrides WriteCover(ProfileAppearanceOverrides overrides) => overrides with
    {
        CoverFit = Fit,
        CropX = Math.Clamp(FocalX, 0, 1),
        CropY = Math.Clamp(FocalY, 0, 1),
        Zoom = Math.Clamp(Zoom, ProfileAppearanceRules.MinimumCoverZoom, ProfileAppearanceRules.MaximumCoverZoom),
        CoverOffsetX = Math.Clamp(OffsetX, -1, 1),
        CoverOffsetY = Math.Clamp(OffsetY, -1, 1),
        CoverRotation = Math.Clamp(Rotation, -180, 180),
    };

    public ProfileAppearanceOverrides WriteBanner(ProfileAppearanceOverrides overrides) => overrides with
    {
        BannerFocusX = Math.Clamp(FocalX, ProfileAppearanceRules.MinimumBannerFocus, ProfileAppearanceRules.MaximumBannerFocus),
        BannerFocusY = Math.Clamp(FocalY, ProfileAppearanceRules.MinimumBannerFocus, ProfileAppearanceRules.MaximumBannerFocus),
        BannerZoom = Math.Clamp(Zoom, ProfileAppearanceRules.MinimumBannerZoom, ProfileAppearanceRules.MaximumBannerZoom),
    };

    /// <summary>
    /// Where the source lands inside a slot: scale and translation in slot pixels. Declarative stretch
    /// fits each axis independently; fit/fill preserve aspect ratio. The focal
    /// point stays under the same relative slot position for every aspect ratio, and fill never leaves
    /// an empty edge.
    /// </summary>
    public MediaPlacement Place(double sourceWidth, double sourceHeight, double slotWidth, double slotHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || slotWidth <= 0 || slotHeight <= 0)
        {
            return new MediaPlacement(1, 0, 0, sourceWidth, sourceHeight, Rotation);
        }

        var baseScale = Fit == "fit"
            ? Math.Min(slotWidth / sourceWidth, slotHeight / sourceHeight)
            : Math.Max(slotWidth / sourceWidth, slotHeight / sourceHeight);
        var scale = baseScale * Math.Max(1, Zoom);
        var width = Fit == "stretch" ? slotWidth * Math.Max(1, Zoom) : sourceWidth * scale;
        var height = Fit == "stretch" ? slotHeight * Math.Max(1, Zoom) : sourceHeight * scale;

        double Axis(double displayed, double slot, double focal, double offset)
        {
            var overflow = displayed - slot;
            if (overflow <= 0)
            {
                return (-overflow / 2) + (offset * slot * 0.5);
            }

            var translate = -(overflow * Math.Clamp(focal, 0, 1)) + (offset * slot * 0.5);
            return Fit == "fit" ? translate : Math.Clamp(translate, -overflow, 0);
        }

        return new MediaPlacement(scale, Axis(width, slotWidth, FocalX, OffsetX), Axis(height, slotHeight, FocalY, OffsetY), width, height, Rotation);
    }

}

/// <summary>Resolved placement of a source inside a slot, in slot pixels.</summary>
public readonly record struct MediaPlacement(double Scale, double TranslateX, double TranslateY, double Width, double Height, double Rotation);

/// <summary>Banner video presentation window and policy (document 01 §12.5). It never rewrites the source.</summary>
public sealed record PlaybackPresentationState(
    string LoopMode,
    bool Mute,
    double Rate)
{
    public static PlaybackPresentationState Banner { get; } = new("loop", true, 1);
}

/// <summary>Everything a renderer needs to present a Profile's Cover and Banner on one surface.</summary>
public sealed record ProfileMediaPresentation(
    MediaTransformState Cover,
    MediaTransformState Banner,
    PlaybackPresentationState Playback)
{
    public static ProfileMediaPresentation From(ProfileAppearanceOverrides overrides) => new(
        MediaTransformState.FromCover(overrides),
        MediaTransformState.FromBanner(overrides),
        PlaybackPresentationState.Banner);
}
