using Neuterradise.App.Import.Verification;

namespace Neuterradise.App.Import;

/// <summary>
/// Single authority for import Profile resolution. Exact-duplicate ownership is authoritative.
/// Face evidence may become a deterministic candidate only when the SFace match is strong and
/// unambiguous; raw similarity remains evidence and is never presented as a calibrated probability.
/// </summary>
public sealed class AutomaticMediaAssignmentPolicy
{
    public const double StrongFaceSimilarity = ImportProfileCollisionPolicy.StrongSimilarityThreshold;
    public const double StrongFaceMargin = 0.08;

    public ImportAssignmentDecision Resolve(
        Guid? explicitProfileId,
        Guid? durableOwnerProfileId,
        Guid? faceProfileId = null,
        double? faceSimilarity = null,
        bool faceIsConfirmed = false,
        bool faceIsAmbiguous = false)
    {
        if (explicitProfileId is { } explicitId && explicitId != Guid.Empty)
            return ImportAssignmentDecision.Authoritative(
                explicitId, ImportAssignmentEvidence.ExplicitContext);

        if (durableOwnerProfileId is { } ownerId && ownerId != Guid.Empty)
            return ImportAssignmentDecision.Authoritative(
                ownerId, ImportAssignmentEvidence.DurableRelationship);

        if (!faceIsAmbiguous
            && faceProfileId is { } faceId
            && faceId != Guid.Empty
            && (faceIsConfirmed
                || faceSimilarity is { } similarity && similarity >= StrongFaceSimilarity))
        {
            return ImportAssignmentDecision.Authoritative(
                faceId, ImportAssignmentEvidence.DeterministicFaceMatch);
        }

        return ImportAssignmentDecision.Unknown;
    }

    public ImportAssignmentDecision EvaluateBatch(IEnumerable<ImportAssignmentEvidenceItem> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var items = evidence.ToArray();
        if (items.Length == 0)
            return ImportAssignmentDecision.Unknown;

        var decisions = items
            .Select(item => Resolve(
                explicitProfileId: null,
                item.DurableOwnerProfileId,
                item.FaceProfileId,
                item.FaceSimilarity,
                item.FaceIsConfirmed,
                item.FaceIsAmbiguous))
            .ToArray();

        if (decisions.Any(static decision => !decision.IsAuthoritative))
            return ImportAssignmentDecision.Unknown;

        var profileIds = decisions
            .Select(static decision => decision.ProfileId!.Value)
            .Distinct()
            .ToArray();
        if (profileIds.Length != 1)
            return ImportAssignmentDecision.Unknown;

        var evidenceKind = decisions.All(static decision =>
                decision.Evidence == ImportAssignmentEvidence.DurableRelationship)
            ? ImportAssignmentEvidence.DurableRelationship
            : ImportAssignmentEvidence.DeterministicFaceMatch;
        return ImportAssignmentDecision.Authoritative(profileIds[0], evidenceKind);
    }
}

public sealed record ImportAssignmentEvidenceItem(
    Guid ItemId,
    Guid? DurableOwnerProfileId,
    Guid? FaceProfileId,
    double? FaceSimilarity,
    bool FaceIsConfirmed,
    bool FaceIsAmbiguous);

public sealed record ImportAssignmentCluster(
    string Key,
    Guid? CandidateProfileId,
    ImportAssignmentClusterEvidence Evidence,
    IReadOnlyList<Guid> ItemIds,
    double? Similarity = null);

public enum ImportAssignmentClusterEvidence
{
    FaceMatch,
    UnknownFace,
    Conflicting,
    NoFace
}

public sealed record ImportAssignmentAssessment(
    ImportAssignmentDecision Decision,
    IReadOnlyList<ImportAssignmentCluster> Clusters,
    int TotalItemCount,
    int SupportedItemCount)
{
    public bool HasAmbiguity => !Decision.IsAuthoritative && Clusters.Count > 0;
}

public enum ImportAssignmentEvidence
{
    Unknown,
    ExplicitContext,
    DurableRelationship,
    DeterministicFaceMatch
}

public sealed record ImportAssignmentDecision(Guid? ProfileId, ImportAssignmentEvidence Evidence)
{
    public bool IsAuthoritative => ProfileId is not null
        && Evidence is ImportAssignmentEvidence.ExplicitContext
            or ImportAssignmentEvidence.DurableRelationship
            or ImportAssignmentEvidence.DeterministicFaceMatch;

    public static ImportAssignmentDecision Unknown { get; } =
        new(null, ImportAssignmentEvidence.Unknown);

    public static ImportAssignmentDecision Authoritative(
        Guid profileId,
        ImportAssignmentEvidence evidence) => new(profileId, evidence);
}
