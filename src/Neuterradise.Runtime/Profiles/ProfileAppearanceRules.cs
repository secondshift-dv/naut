using System.Globalization;
using Neuterradise.App.Design.ProfileLayouts;
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

    public static AppearanceVisualCandidates EvaluateVisualCandidates(
        IReadOnlyList<AppearanceCandidateEvaluationInput> inputs)
    {
        if (inputs is null || inputs.Count == 0)
        {
            return AppearanceVisualCandidates.Empty;
        }

        var covers = new List<CoverVisualCandidate>();
        var banners = new List<BannerVisualCandidate>();

        foreach (var item in inputs)
        {
            covers.AddRange(BuildCoverCandidates(item));
            banners.AddRange(BuildBannerCandidates(item));
        }

        return new AppearanceVisualCandidates(RankCovers(covers), RankBanners(banners));
    }

    private static IEnumerable<CoverVisualCandidate> BuildCoverCandidates(
        AppearanceCandidateEvaluationInput item)
    {
        if (!IsCoverMediaTypeEligible(item.MediaType))
        {
            yield break;
        }

        if (item.MediaType == MediaType.Image)
        {
            yield return ScoreCover(item, timestamp: null, face: OrderedFaceEvidence(item).FirstOrDefault());
            yield break;
        }

        var emitted = new HashSet<long>();
        foreach (var face in OrderedFaceEvidence(item).Take(MaximumFaceCoverCandidatesPerMedia))
        {
            var timestamp = face.SampledTimestampMilliseconds ?? 0;
            if (emitted.Add(timestamp))
            {
                yield return ScoreCover(item, timestamp, face);
            }
        }

        foreach (var timestamp in DeterministicVideoPositions(item.DurationMs))
        {
            if (emitted.Count >= MaximumCoverCandidatesPerMedia)
            {
                yield break;
            }

            if (emitted.Add(timestamp))
            {
                yield return ScoreCover(item, timestamp, face: null);
            }
        }
    }

    private static CoverVisualCandidate ScoreCover(
        AppearanceCandidateEvaluationInput item,
        long? timestamp,
        AppearanceFaceEvidence? face)
    {
        var kind = timestamp is null ? CoverVisualSourceKind.Image : CoverVisualSourceKind.VideoFrame;
        var score = 50.0;
        string reason;

        if (face is { IsConfirmedForTargetProfile: true })
        {
            score += 100;
            reason = "Confirmed face for this Profile";
        }
        else if (face is { TargetIdentitySimilarity: { } similarity })
        {

            score += 40 + (similarity * 60);
            reason = "Face resembling this Profile";
        }
        else if (face is not null || item.HasConfirmedFaceForProfile)
        {
            score += 50;
            reason = "Detected face with suitable framing";
        }
        else if (item.HasFaceDetection)
        {
            score += 25;
            reason = "Frame from media containing a face";
        }
        else
        {
            reason = kind == CoverVisualSourceKind.Image
                ? "Eligible image candidate"
                : "Sampled frame";
        }

        if (face is not null)
        {

            score += 30 * Math.Clamp(face.RelativeArea, 0, 1);
            score += 20 * Math.Clamp(face.DetectionConfidence, 0, 1);
        }

        score += ShapeAndResolutionScore(item, ref reason);

        if (item.HasDerivedPreview)
        {
            score += 10;
        }

        if (item.IsExistingCover && timestamp is null)
        {
            score += 5;
        }

        return new CoverVisualCandidate(
            CandidateId: CoverCandidateId(item.MediaId, timestamp),
            SourceMediaId: item.MediaId,
            SourceMediaType: item.MediaType,
            SourceKind: kind,
            TimestampMilliseconds: timestamp,
            SuggestedCropX: SuggestedFocal(face).X,
            SuggestedCropY: SuggestedFocal(face).Y,
            FaceId: face?.FaceId,
            DetectionConfidence: face?.DetectionConfidence,
            TargetIdentitySimilarity: face?.TargetIdentitySimilarity,
            PixelWidth: item.PixelWidth,
            PixelHeight: item.PixelHeight,
            DisplayName: item.DisplayName,
            Score: score,
            Rank: 0,
            IsRecommended: false,
            Reason: reason);
    }

    private static double ShapeAndResolutionScore(
        AppearanceCandidateEvaluationInput item,
        ref string reason)
    {
        if (item.PixelWidth is not > 0 || item.PixelHeight is not > 0)
        {
            return 0;
        }

        var score = 0.0;
        var ratio = (double)item.PixelWidth.Value / item.PixelHeight.Value;
        if (ratio is >= 0.65 and <= 1.35)
        {
            score += 30;
            if (!item.HasConfirmedFaceForProfile && !item.HasFaceDetection)
            {
                reason = "Balanced framing with high resolution";
            }
        }
        else if (ratio is >= 0.5 and <= 1.6)
        {
            score += 15;
        }
        else if (ratio is > 2.0 or < 0.4)
        {
            score -= 25;
        }

        if (item.PixelWidth >= 800 && item.PixelHeight >= 800)
        {
            score += 20;
        }
        else if (item.PixelWidth >= 400 && item.PixelHeight >= 400)
        {
            score += 10;
        }
        else if (item.PixelWidth < 150 || item.PixelHeight < 150)
        {
            score -= 20;
        }

        return score;
    }

    private static IEnumerable<BannerVisualCandidate> BuildBannerCandidates(
        AppearanceCandidateEvaluationInput item)
    {
        if (item.MediaType != MediaType.Video)
        {
            yield break;
        }

        yield return ScoreBanner(item, OrderedFaceEvidence(item).FirstOrDefault());
    }

    private static BannerVisualCandidate ScoreBanner(
        AppearanceCandidateEvaluationInput item,
        AppearanceFaceEvidence? face)
    {
        var score = 80.0;
        string reason = "Eligible video banner";

        if (item.PixelWidth is > 0 && item.PixelHeight is > 0)
        {
            var ratio = (double)item.PixelWidth.Value / item.PixelHeight.Value;
            if (ratio >= 1.7)
            {
                score += 70;
                reason = "Wide landscape composition";
            }
            else if (ratio >= 1.3)
            {
                score += 40;
                reason = "Landscape composition";
            }
            else if (ratio >= 0.9)
            {
                score += 10;
            }
            else
            {
                score -= 40;
                reason = "Portrait aspect ratio is less suitable for a banner";
            }

            score += item.PixelWidth switch
            {
                >= 1600 => 25,
                >= 1200 => 15,
                >= 800 => 5,
                _ => 0,
            };
        }

        if (face is not null)
        {
            score += 25 + (20 * Math.Clamp(face.DetectionConfidence, 0, 1));
            reason = face.IsConfirmedForTargetProfile
                ? "Video with a confirmed face for this Profile"
                : "Video with a detected face";
        }

        if (item.HasDerivedPreview)
        {
            score += 10;
        }

        if (item.IsExistingBanner)
        {
            score += 5;
        }

        var focal = SuggestedFocal(face);
        return new BannerVisualCandidate(
            CandidateId: item.MediaId.ToString("N"),
            SourceMediaId: item.MediaId,
            SourceMediaType: item.MediaType,
            FocusX: focal.X,
            FocusY: focal.Y,
            FaceId: face?.FaceId,
            DisplayName: item.DisplayName,
            Score: score,
            Rank: 0,
            IsRecommended: false,
            Reason: reason);
    }

    private static IReadOnlyList<CoverVisualCandidate> RankCovers(List<CoverVisualCandidate> candidates)
    {
        var ordered = candidates
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();

        return [.. ordered.Select((candidate, index) => candidate with
        {
            Rank = index + 1,
            IsRecommended = index < MaximumRecommendedCandidates && candidate.Score >= RecommendationFloor,
        })];
    }

    private static IReadOnlyList<BannerVisualCandidate> RankBanners(List<BannerVisualCandidate> candidates)
    {
        var ordered = candidates
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();

        return [.. ordered.Select((candidate, index) => candidate with
        {
            Rank = index + 1,
            IsRecommended = index < MaximumRecommendedCandidates && candidate.Score >= RecommendationFloor,
        })];
    }

    private static IEnumerable<AppearanceFaceEvidence> OrderedFaceEvidence(
        AppearanceCandidateEvaluationInput item) =>
        (item.FaceEvidence ?? [])
            .OrderByDescending(static face => face.IsConfirmedForTargetProfile)
            .ThenByDescending(static face => face.TargetIdentitySimilarity ?? -1)
            .ThenByDescending(static face => face.DetectionConfidence)
            .ThenBy(static face => face.SampledTimestampMilliseconds ?? 0);

    private static (double X, double Y) SuggestedFocal(AppearanceFaceEvidence? face)
    {
        if (face?.NormalizedCenterX is not { } centerX || face.NormalizedCenterY is not { } centerY)
        {
            return (DefaultCoverCrop, DefaultCoverCrop);
        }

        // A small upward bias keeps eyes away from the vertical centre; safe clamps prevent a tiny
        // edge face from forcing an extreme crop.
        var upwardBias = Math.Min(0.08, (face.NormalizedHeight ?? 0) * 0.12);
        return (Math.Clamp(centerX, 0.12, 0.88), Math.Clamp(centerY - upwardBias, 0.12, 0.88));
    }

    private static IReadOnlyList<long> DeterministicVideoPositions(int? durationMilliseconds)
    {
        if (durationMilliseconds is not { } duration || duration <= 0)
        {
            return [0];
        }

        return [duration / 4, duration / 2, duration * 3 / 4];
    }

    public static string CoverCandidateId(Guid assetId, long? timestampMilliseconds) =>
        timestampMilliseconds is { } timestamp
            ? string.Create(CultureInfo.InvariantCulture, $"{assetId:N}@{timestamp}")
            : assetId.ToString("N");

    public const int MaximumRecommendedCandidates = 5;

    public const double RecommendationFloor = 50.0;

    public const int MaximumFaceCoverCandidatesPerMedia = 4;

    public const int MaximumCoverCandidatesPerMedia = 6;

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

public sealed record AppearanceCandidateEvaluationInput(
    Guid MediaId,
    MediaType MediaType,
    string DisplayName,
    int? PixelWidth,
    int? PixelHeight,
    int? DurationMs,
    bool HasDerivedPreview,
    bool HasConfirmedFaceForProfile,
    bool HasFaceDetection,
    long CreatedAtMs,
    bool IsExistingCover = false,
    bool IsExistingBanner = false,
    IReadOnlyList<AppearanceFaceEvidence>? FaceEvidence = null);

public sealed record CoverVisualCandidate(
    string CandidateId,
    Guid SourceMediaId,
    MediaType SourceMediaType,
    CoverVisualSourceKind SourceKind,
    long? TimestampMilliseconds,
    double SuggestedCropX,
    double SuggestedCropY,
    Guid? FaceId,
    double? DetectionConfidence,
    double? TargetIdentitySimilarity,
    int? PixelWidth,
    int? PixelHeight,
    string DisplayName,
    double Score,
    int Rank,
    bool IsRecommended,
    string Reason);

public sealed record BannerVisualCandidate(
    string CandidateId,
    Guid SourceMediaId,
    MediaType SourceMediaType,
    double FocusX,
    double FocusY,
    Guid? FaceId,
    string DisplayName,
    double Score,
    int Rank,
    bool IsRecommended,
    string Reason);

public sealed record AppearanceVisualCandidates(
    IReadOnlyList<CoverVisualCandidate> Covers,
    IReadOnlyList<BannerVisualCandidate> Banners)
{
    public static AppearanceVisualCandidates Empty { get; } = new([], []);

    public IReadOnlyList<CoverVisualCandidate> RecommendedCovers =>
        [.. Covers.Where(static candidate => candidate.IsRecommended)];

    public IReadOnlyList<BannerVisualCandidate> RecommendedBanners =>
        [.. Banners.Where(static candidate => candidate.IsRecommended)];
}
