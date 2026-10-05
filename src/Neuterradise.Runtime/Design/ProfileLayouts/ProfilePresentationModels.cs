using Neuterradise.App.Profiles;

namespace Neuterradise.App.Design.ProfileLayouts;

/// <summary>The semantic identity payload Profile-derived surfaces present (card, hero, preview).</summary>
public sealed record ProfilePresentationModel(
    Guid ProfileId,
    string DisplayName,
    string? CategoryName = null,
    IReadOnlyList<string>? Tags = null,
    int? Rating = null,
    bool IsFavorite = false,
    string? Overview = null,
    string? Notes = null,
    long MediaCount = 0,
    bool HasRelatedIndicator = false,
    string? CardVariantId = null)
{
    public IReadOnlyList<string> TagList => Tags ?? [];
}

public enum BannerPreviewStopReason
{
    None,
    PointerLeft,
    FocusLost,
    Scrolled,
    Hidden,
    Unloaded,
    MotionDisabled,
    ClipEnded,
    PlaybackFailed,
    MediaEnded,
    ReplacedByAnotherPreview
}
