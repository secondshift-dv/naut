using System.Globalization;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;

using Neuterradise.App.Import;

namespace Neuterradise.App.Import.Verification;

public sealed record VerificationReadModel(
    Guid UnitId,
    Guid SessionId,
    Guid? ParentUnitId,
    string SourceKind,
    string SourceDisplayName,
    string? SourcePathOrReference,
    ImportUnitState State,
    string? DestinationKind,
    Guid? DestinationProfileId,
    int VerificationStep,
    string VerificationDraftJson,
    int VerificationVersion,
    string? CommitOperationId,
    ImportCommitCheckpoint LibraryCommitState,
    int TotalItemCount,
    int IncludedItemCount,
    int SkippedItemCount,
    int ExactDuplicateCount,
    int NeedsAttentionCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long RowVersion);

public sealed record PagedItemsResult(
    IReadOnlyList<ImportItemSummary> Items,
    int TotalCount,
    int Offset,
    int Limit);

public sealed record SplitUnitResult(
    Guid SourceUnitId,
    Guid NewUnitId,
    int RemainingItemCount,
    int MovedItemCount);

public sealed record VerificationReadinessResult(
    bool IsReady,
    IReadOnlyList<VerificationBlocker> Blockers,
    IReadOnlyList<string> Warnings);

public sealed record VerificationCoverCandidate(
    string CandidateId,
    Guid MediaId,
    Guid? ImportItemId,
    string DisplayName,
    MediaType MediaType,
    CoverVisualSourceKind SourceKind,
    long? TimestampMilliseconds,
    bool IsFromImportUnit,
    int Rank,
    bool IsRecommended,
    string? Reason,
    string? PreviewImagePath = null)
{
    /// <summary>True when a real derived still exists, so the tile can show the media itself.</summary>
    public bool HasPreviewImage => !string.IsNullOrWhiteSpace(PreviewImagePath);

    public string? TimestampText => TimestampMilliseconds is { } milliseconds
        ? TimeSpan.FromMilliseconds(milliseconds).ToString(@"mm\:ss\.fff", CultureInfo.CurrentCulture)
        : null;

    public string DisplayTitle => TimestampText is { } timestamp
        ? string.Create(CultureInfo.CurrentCulture, $"{DisplayName} @ {timestamp}")
        : DisplayName;

    public string RankText => string.Create(CultureInfo.CurrentCulture, $"#{Rank}");
}

public sealed record VerificationBannerCandidate(
    string CandidateId,
    Guid MediaId,
    Guid? ImportItemId,
    string DisplayName,
    MediaType MediaType,
    bool IsFromImportUnit,
    int Rank,
    bool IsRecommended,
    string? Reason,
    string? PreviewImagePath = null)
{
    public bool HasPreviewImage => !string.IsNullOrWhiteSpace(PreviewImagePath);

    public string DisplayTitle => DisplayName;

    public string RankText => string.Create(CultureInfo.CurrentCulture, $"#{Rank}");
}

public sealed record VerificationAppearanceCandidates(
    IReadOnlyList<VerificationCoverCandidate> Covers,
    IReadOnlyList<VerificationBannerCandidate> Banners)
{
    public static VerificationAppearanceCandidates Empty { get; } = new([], []);
}

public sealed record ProfileLookupResult(
    Guid ProfileId,
    string DisplayName,
    string StorageToken,
    string? CategoryName = null);

public sealed record VerificationFaceCandidate(
    Guid FaceId,
    Guid MediaId,
    Guid ImportItemId,
    string SourceFileName,
    long FaceRowVersion,
    string? SuggestedProfileName,
    Guid? SuggestedProfileId,
    double? Confidence,
    string DecisionState,
    double? SuggestionSimilarity = null);

public sealed record VerificationDuplicateProblem(
    Guid ImportItemId,
    Guid MatchedMediaId,
    string SourceFileName,
    string? ExistingProfileName,
    string? PreviewImagePath);

public sealed record VerificationProfileCollisionProblem(
    Guid ImportItemId,
    Guid MediaId,
    string SourceFileName,
    Guid CandidateProfileId,
    string CandidateProfileName,
    double Similarity,
    string? PreviewImagePath);

public sealed record VerificationPreparationProblem(
    Guid ImportItemId,
    string SourceFileName,
    IReadOnlyList<ImportPreparationFailure> Failures);

public sealed record VerificationGateProblems(
    IReadOnlyList<VerificationDuplicateProblem> Duplicates,
    IReadOnlyList<VerificationProfileCollisionProblem> ProfileCollisions,
    IReadOnlyList<VerificationPreparationProblem> PreparationFailures,
    bool RequiredPreparationPending)
{
    public static VerificationGateProblems Empty { get; } = new([], [], [], false);
}
