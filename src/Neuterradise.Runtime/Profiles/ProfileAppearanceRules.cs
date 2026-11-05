using Neuterradise.App.Media;

namespace Neuterradise.App.Profiles;

public enum CoverVisualSourceKind
{

    Image,

    VideoFrame,
}


public static class ProfileAppearanceRules
{

    public static bool CanReferenceMedia(Guid profileId, Guid assetId)
    {
        return profileId != Guid.Empty && assetId != Guid.Empty;
    }

    public static bool ValidateEligibility(
        ProfileKind profileKind,
        bool isProfileActive,
        MediaState assetState,
        bool isMediaActive,
        bool hasActiveRelation)
    {
        if (profileKind != ProfileKind.Normal)
        {
            return false;
        }

        if (!isProfileActive)
        {
            return false;
        }

        if (assetState != MediaState.Active || !isMediaActive)
        {
            return false;
        }

        return hasActiveRelation;
    }

    public static bool IsCoverMediaTypeEligible(MediaType mediaType) =>
        mediaType is MediaType.Image or MediaType.Video;

    public static bool IsCoverVisualSourceValid(
        MediaType mediaType,
        CoverVisualSourceKind kind,
        long? timestampMilliseconds) => (mediaType, kind) switch
        {
            (MediaType.Image, CoverVisualSourceKind.Image) => timestampMilliseconds is null,
            (MediaType.Video, CoverVisualSourceKind.VideoFrame) => timestampMilliseconds is >= 0,
            _ => false,
        };

    public static CoverVisualSourceKind ResolveCoverSourceKind(MediaType mediaType) =>
        mediaType == MediaType.Video ? CoverVisualSourceKind.VideoFrame : CoverVisualSourceKind.Image;

    /// <summary>A Profile Banner is always a prepared video HOVER artifact.</summary>
    public static bool IsBannerMediaTypeEligible(MediaType mediaType) =>
        mediaType == MediaType.Video;

    public const double MinimumCoverCrop = 0;

    public const double MaximumCoverCrop = 1;

    public const double DefaultCoverCrop = 0.5;

    public const double MinimumCoverZoom = 1;

    public const double MaximumCoverZoom = 4;

    public const double MinimumBannerFocus = 0;

    public const double MaximumBannerFocus = 1;

    public const double DefaultBannerFocus = 0.5;

    public const double MinimumBannerZoom = 1;

    public const double MaximumBannerZoom = 2;

    public static bool IsCoverCropValid(double value) =>
        double.IsFinite(value) && value >= MinimumCoverCrop && value <= MaximumCoverCrop;

    public static bool IsCoverZoomValid(double value) =>
        double.IsFinite(value) && value >= MinimumCoverZoom && value <= MaximumCoverZoom;

    public static bool IsBannerFocusValid(double value) =>
        double.IsFinite(value) && value >= MinimumBannerFocus && value <= MaximumBannerFocus;

    public static bool IsBannerZoomValid(double value) =>
        double.IsFinite(value) && value >= MinimumBannerZoom && value <= MaximumBannerZoom;

    public static double SourceCandidateScore(double faceEvidence, int width, int height) =>
        faceEvidence * 100 + Math.Min(Math.Min(width, height), 1600) / 40d;

}

public sealed record AppearanceFaceEvidence(
    Guid FaceId,
    long? SampledTimestampMilliseconds,
    double DetectionConfidence,
    double? TargetIdentitySimilarity,
    bool IsConfirmedForTargetProfile,
    double RelativeArea,
    double? NormalizedCenterX = null,
    double? NormalizedCenterY = null,
    double? NormalizedX = null,
    double? NormalizedY = null,
    double? NormalizedWidth = null,
    double? NormalizedHeight = null);
