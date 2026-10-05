using Neuterradise.App.Faces;
using Neuterradise.App.Import;
using Neuterradise.App.Import.Preparation;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Profiles;
using Neuterradise.App.RelatedProfiles;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.SystemServices.Database;

public static class DbEnum
{
    public static string Format(MediaAssetRole value) => value switch
    {
        MediaAssetRole.Thumbnail => "THUMBNAIL",
        MediaAssetRole.Hover => "HOVER",
        MediaAssetRole.ModelRender => "MODEL_RENDER",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static MediaAssetRole ParseMediaAssetRole(string value) => value switch
    {
        "THUMBNAIL" => MediaAssetRole.Thumbnail,
        "HOVER" => MediaAssetRole.Hover,
        "MODEL_RENDER" => MediaAssetRole.ModelRender,
        _ => throw new FormatException($"'{value}' is not a valid MediaAssetRole database value."),
    };

    public static string Format(MediaAssetState value) => value switch
    {
        MediaAssetState.Ready => "READY",
        MediaAssetState.NeedsRepair => "NEEDS_REPAIR",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static MediaAssetState ParseMediaAssetState(string value) => value switch
    {
        "READY" => MediaAssetState.Ready,
        "NEEDS_REPAIR" => MediaAssetState.NeedsRepair,
        _ => throw new FormatException($"'{value}' is not a valid MediaAssetState database value."),
    };

    public static string Format(DestinationKind value) => value switch
    {
        DestinationKind.NewNormal => "NEW_NORMAL",
        DestinationKind.ExistingNormal => "EXISTING_NORMAL",
        DestinationKind.SystemUnknown => "SYSTEM_UNKNOWN",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static DestinationKind ParseDestinationKind(string value) => value switch
    {
        "NEW_NORMAL" => DestinationKind.NewNormal,
        "EXISTING_NORMAL" => DestinationKind.ExistingNormal,
        "SYSTEM_UNKNOWN" => DestinationKind.SystemUnknown,
        _ => throw new FormatException($"'{value}' is not a valid DestinationKind database value."),
    };

    public static string Format(ItemDisposition value) => value switch
    {
        ItemDisposition.Included => "INCLUDED",
        ItemDisposition.Skipped => "SKIPPED",
        ItemDisposition.Reused => "REUSED",
        ItemDisposition.Invalid => "INVALID",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ItemDisposition ParseItemDisposition(string value) => value switch
    {
        "INCLUDED" => ItemDisposition.Included,
        "SKIPPED" => ItemDisposition.Skipped,
        "REUSED" => ItemDisposition.Reused,
        "INVALID" => ItemDisposition.Invalid,
        _ => throw new FormatException($"'{value}' is not a valid ItemDisposition database value."),
    };

    public static string Format(DuplicateDecision value) => value switch
    {
        DuplicateDecision.Include => "INCLUDE",
        DuplicateDecision.Reuse => "REUSE",
        DuplicateDecision.Skip => "SKIP",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    /// <summary>NULL means the exact-duplicate decision is still open.</summary>
    public static string? FormatOrNull(DuplicateDecision? value) =>
        value is { } decision ? Format(decision) : null;

    public static DuplicateDecision ParseDuplicateDecision(string value) => value switch
    {
        "INCLUDE" => DuplicateDecision.Include,
        "REUSE" => DuplicateDecision.Reuse,
        "SKIP" => DuplicateDecision.Skip,
        _ => throw new FormatException($"'{value}' is not a valid DuplicateDecision database value."),
    };

    public static DuplicateDecision? ParseDuplicateDecisionOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : ParseDuplicateDecision(value);

    /// <summary>The disposition an item takes once the duplicate decision is applied.</summary>
    public static ItemDisposition DispositionFor(DuplicateDecision value) => value switch
    {
        DuplicateDecision.Include => ItemDisposition.Included,
        DuplicateDecision.Reuse => ItemDisposition.Reused,
        DuplicateDecision.Skip => ItemDisposition.Skipped,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    /// <summary>
    /// The retirement reason the candidate carries when a duplicate decision retires it. INCLUDE keeps
    /// the candidate alive, so it has no retirement reason.
    /// </summary>
    public static MediaRetirementReason? RetirementReasonFor(DuplicateDecision value) => value switch
    {
        DuplicateDecision.Include => null,
        DuplicateDecision.Reuse => MediaRetirementReason.DedupReused,
        DuplicateDecision.Skip => MediaRetirementReason.Skipped,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string Format(ImportSessionState value) => value switch
    {
        ImportSessionState.Open => "OPEN",
        ImportSessionState.Completed => "COMPLETED",
        ImportSessionState.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ImportSessionState ParseImportSessionState(string value) => value switch
    {
        "OPEN" => ImportSessionState.Open,
        "COMPLETED" => ImportSessionState.Completed,
        "CANCELLED" => ImportSessionState.Cancelled,
        _ => throw new FormatException($"'{value}' is not a valid ImportSessionState database value."),
    };

    public static string Format(ImportUnitState value) => value switch
    {
        ImportUnitState.Intake => "INTAKE",
        ImportUnitState.Preparing => "PREPARING",
        ImportUnitState.ReadyForVerification => "READY_FOR_VERIFICATION",
        ImportUnitState.Committing => "COMMITTING",
        ImportUnitState.Committed => "COMMITTED",
        ImportUnitState.Completed => "COMPLETED",
        ImportUnitState.FailedRetryable => "FAILED_RETRYABLE",
        ImportUnitState.FailedTerminal => "FAILED_TERMINAL",
        ImportUnitState.Cancelled => "CANCELLED",
        ImportUnitState.CommittedWithCleanupAttention => "COMMITTED_WITH_CLEANUP_ATTENTION",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ImportUnitState ParseImportUnitState(string value) => value switch
    {
        "INTAKE" => ImportUnitState.Intake,
        "PREPARING" => ImportUnitState.Preparing,
        "READY_FOR_VERIFICATION" => ImportUnitState.ReadyForVerification,
        "COMMITTING" => ImportUnitState.Committing,
        "COMMITTED" => ImportUnitState.Committed,
        "COMPLETED" => ImportUnitState.Completed,
        "FAILED_RETRYABLE" => ImportUnitState.FailedRetryable,
        "FAILED_TERMINAL" => ImportUnitState.FailedTerminal,
        "CANCELLED" => ImportUnitState.Cancelled,
        "COMMITTED_WITH_CLEANUP_ATTENTION" => ImportUnitState.CommittedWithCleanupAttention,
        _ => throw new FormatException($"'{value}' is not a valid ImportUnitState database value."),
    };

    public static string Format(ImportPreparationStatus value) => value switch
    {
        ImportPreparationStatus.Pending => "PENDING",
        ImportPreparationStatus.Running => "RUNNING",
        ImportPreparationStatus.Ready => "READY",
        ImportPreparationStatus.FailedRetryable => "FAILED_RETRYABLE",
        ImportPreparationStatus.FailedTerminal => "FAILED_TERMINAL",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ImportPreparationStatus ParseImportPreparationStatus(string value) => value switch
    {
        "PENDING" => ImportPreparationStatus.Pending,
        "RUNNING" => ImportPreparationStatus.Running,
        "READY" => ImportPreparationStatus.Ready,
        "FAILED_RETRYABLE" => ImportPreparationStatus.FailedRetryable,
        "FAILED_TERMINAL" => ImportPreparationStatus.FailedTerminal,
        _ => throw new FormatException($"'{value}' is not a valid ImportPreparationStatus database value."),
    };

    public static string Format(ImportCleanupPolicy value) => value switch
    {
        ImportCleanupPolicy.Copy => "COPY",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ImportCleanupPolicy ParseImportCleanupPolicy(string value) => value switch
    {
        "COPY" => ImportCleanupPolicy.Copy,
        _ => throw new FormatException($"'{value}' is not a valid ImportCleanupPolicy database value."),
    };

    public static string Format(SourceCleanupState value) => value switch
    {
        SourceCleanupState.SourcePresent => "SOURCE_PRESENT",
        SourceCleanupState.DestinationVerified => "DESTINATION_VERIFIED",
        SourceCleanupState.LibraryCommitted => "LIBRARY_COMMITTED",
        SourceCleanupState.SourceDeletePending => "SOURCE_DELETE_PENDING",
        SourceCleanupState.SourceConsumed => "SOURCE_CONSUMED",
        SourceCleanupState.SourceDeleteFailed => "SOURCE_DELETE_FAILED",
        SourceCleanupState.SourcePreserved => "SOURCE_PRESERVED",
        SourceCleanupState.SourceChanged => "SOURCE_CHANGED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static SourceCleanupState ParseSourceCleanupState(string value) => value switch
    {
        "SOURCE_PRESENT" => SourceCleanupState.SourcePresent,
        "DESTINATION_VERIFIED" => SourceCleanupState.DestinationVerified,
        "LIBRARY_COMMITTED" => SourceCleanupState.LibraryCommitted,
        "SOURCE_DELETE_PENDING" => SourceCleanupState.SourceDeletePending,
        "SOURCE_CONSUMED" => SourceCleanupState.SourceConsumed,
        "SOURCE_DELETE_FAILED" => SourceCleanupState.SourceDeleteFailed,
        "SOURCE_PRESERVED" => SourceCleanupState.SourcePreserved,
        "SOURCE_CHANGED" => SourceCleanupState.SourceChanged,
        _ => throw new FormatException($"'{value}' is not a valid SourceCleanupState database value."),
    };

    public static string Format(ImportCommitCheckpoint value) => value switch
    {
        ImportCommitCheckpoint.NotCommitted => "NOT_COMMITTED",
        ImportCommitCheckpoint.DecisionValidated => "DECISION_VALIDATED",
        ImportCommitCheckpoint.DestinationPrepared => "DESTINATION_PREPARED",
        ImportCommitCheckpoint.PlacementPlanned => "PLACEMENT_PLANNED",
        ImportCommitCheckpoint.DestinationBytesVerified => "DESTINATION_BYTES_VERIFIED",
        ImportCommitCheckpoint.DomainAuthorityCommitted => "DOMAIN_AUTHORITY_COMMITTED",
        ImportCommitCheckpoint.SourceCleanupPending => "SOURCE_CLEANUP_PENDING",
        ImportCommitCheckpoint.SourceCleanupComplete => "SOURCE_CLEANUP_COMPLETE",
        ImportCommitCheckpoint.Terminal => "TERMINAL",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ImportCommitCheckpoint ParseImportCommitCheckpoint(string value) => value switch
    {
        "NOT_COMMITTED" => ImportCommitCheckpoint.NotCommitted,
        "DECISION_VALIDATED" => ImportCommitCheckpoint.DecisionValidated,
        "DESTINATION_PREPARED" => ImportCommitCheckpoint.DestinationPrepared,
        "PLACEMENT_PLANNED" => ImportCommitCheckpoint.PlacementPlanned,
        "DESTINATION_BYTES_VERIFIED" => ImportCommitCheckpoint.DestinationBytesVerified,
        "DOMAIN_AUTHORITY_COMMITTED" => ImportCommitCheckpoint.DomainAuthorityCommitted,
        "SOURCE_CLEANUP_PENDING" => ImportCommitCheckpoint.SourceCleanupPending,
        "SOURCE_CLEANUP_COMPLETE" => ImportCommitCheckpoint.SourceCleanupComplete,
        "TERMINAL" => ImportCommitCheckpoint.Terminal,
        _ => throw new FormatException($"'{value}' is not a valid ImportCommitCheckpoint database value."),
    };

    /// <summary>An absent library commit state means the unit has not committed anything yet.</summary>
    public static ImportCommitCheckpoint ParseImportCommitCheckpointOrDefault(string? value) =>
        string.IsNullOrEmpty(value)
            ? ImportCommitCheckpoint.NotCommitted
            : ParseImportCommitCheckpoint(value);

    public static string Format(MediaDependencyStatus value) => value switch
    {
        MediaDependencyStatus.SelfContained => "SELF_CONTAINED",
        MediaDependencyStatus.Complete => "COMPLETE",
        MediaDependencyStatus.DependenciesMissing => "DEPENDENCIES_MISSING",
        MediaDependencyStatus.DependenciesUnknown => "DEPENDENCIES_UNKNOWN",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static MediaDependencyStatus ParseMediaDependencyStatus(string value) => value switch
    {
        "SELF_CONTAINED" => MediaDependencyStatus.SelfContained,
        "COMPLETE" => MediaDependencyStatus.Complete,
        "DEPENDENCIES_MISSING" => MediaDependencyStatus.DependenciesMissing,
        "DEPENDENCIES_UNKNOWN" => MediaDependencyStatus.DependenciesUnknown,
        _ => throw new FormatException($"'{value}' is not a valid MediaDependencyStatus database value."),
    };

    public static string Format(DependencyDiscoveryState value) => value switch
    {
        DependencyDiscoveryState.Complete => "COMPLETE",
        DependencyDiscoveryState.MissingDependencies => "MISSING_DEPENDENCIES",
        DependencyDiscoveryState.Unknown => "UNKNOWN",
        DependencyDiscoveryState.FailedRetryable => "FAILED_RETRYABLE",
        DependencyDiscoveryState.FailedTerminal => "FAILED_TERMINAL",
        DependencyDiscoveryState.Unsupported => "UNSUPPORTED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static DependencyDiscoveryState ParseDependencyDiscoveryState(string value) => value switch
    {
        "COMPLETE" => DependencyDiscoveryState.Complete,
        "MISSING_DEPENDENCIES" => DependencyDiscoveryState.MissingDependencies,
        "UNKNOWN" => DependencyDiscoveryState.Unknown,
        "FAILED_RETRYABLE" => DependencyDiscoveryState.FailedRetryable,
        "FAILED_TERMINAL" => DependencyDiscoveryState.FailedTerminal,
        "UNSUPPORTED" => DependencyDiscoveryState.Unsupported,
        _ => throw new FormatException($"'{value}' is not a valid DependencyDiscoveryState database value."),
    };

    public static string Format(ComponentRole value) => value switch
    {
        ComponentRole.Primary => "PRIMARY",
        ComponentRole.Dependency => "DEPENDENCY",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ComponentRole ParseComponentRole(string value) => value switch
    {
        "PRIMARY" => ComponentRole.Primary,
        "DEPENDENCY" => ComponentRole.Dependency,
        _ => throw new FormatException($"'{value}' is not a valid ComponentRole database value."),
    };

    public static string Format(MediaState value) => value switch
    {
        MediaState.Candidate => "CANDIDATE",
        MediaState.Active => "ACTIVE",
        MediaState.Trashed => "TRASHED",
        MediaState.Retired => "RETIRED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static MediaState ParseMediaState(string value) => value switch
    {
        "CANDIDATE" => MediaState.Candidate,
        "ACTIVE" => MediaState.Active,
        "TRASHED" => MediaState.Trashed,
        "RETIRED" => MediaState.Retired,
        _ => throw new FormatException($"'{value}' is not a valid MediaState database value."),
    };

    public static string Format(MediaRetirementReason value) => value switch
    {
        MediaRetirementReason.Skipped => "SKIPPED",
        MediaRetirementReason.Cancelled => "CANCELLED",
        MediaRetirementReason.Invalid => "INVALID",
        MediaRetirementReason.DedupReused => "DEDUP_REUSED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    /// <summary>Only RETIRED media carry a retirement reason; every other state stores NULL.</summary>
    public static string? FormatOrNull(MediaRetirementReason? value) =>
        value is { } reason ? Format(reason) : null;

    public static MediaRetirementReason? ParseMediaRetirementReasonOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : ParseMediaRetirementReason(value);

    public static MediaRetirementReason ParseMediaRetirementReason(string value) => value switch
    {
        "SKIPPED" => MediaRetirementReason.Skipped,
        "CANCELLED" => MediaRetirementReason.Cancelled,
        "INVALID" => MediaRetirementReason.Invalid,
        "DEDUP_REUSED" => MediaRetirementReason.DedupReused,
        _ => throw new FormatException($"'{value}' is not a valid MediaRetirementReason database value."),
    };

    public static string Format(MediaType value) => value switch
    {
        MediaType.Image => "IMAGE",
        MediaType.Video => "VIDEO",
        MediaType.Model => "MODEL",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static MediaType ParseMediaType(string value) => value switch
    {
        "IMAGE" => MediaType.Image,
        "VIDEO" => MediaType.Video,
        "MODEL" => MediaType.Model,
        _ => throw new FormatException($"'{value}' is not a valid MediaType database value."),
    };

    public static string Format(ProfileKind value) => value switch
    {
        ProfileKind.Normal => "NORMAL",
        ProfileKind.Unknown => "UNKNOWN",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ProfileKind ParseProfileKind(string value) => value switch
    {
        "NORMAL" => ProfileKind.Normal,
        "UNKNOWN" => ProfileKind.Unknown,
        _ => throw new FormatException($"'{value}' is not a valid ProfileKind database value."),
    };

    public static string Format(ProfileMediaRelation value) => value switch
    {
        ProfileMediaRelation.Owner => "OWNER",
        ProfileMediaRelation.Appears => "APPEARS",
        ProfileMediaRelation.Manual => "MANUAL",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ProfileMediaRelation ParseProfileMediaRelation(string value) => value switch
    {
        "OWNER" => ProfileMediaRelation.Owner,
        "APPEARS" => ProfileMediaRelation.Appears,
        "MANUAL" => ProfileMediaRelation.Manual,
        _ => throw new FormatException($"'{value}' is not a valid ProfileMediaRelation database value."),
    };

    public static string Format(ManagedPathState value) => value switch
    {
        ManagedPathState.None => "NONE",
        ManagedPathState.Pending => "PENDING",
        ManagedPathState.NeedsAttention => "NEEDS_ATTENTION",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ManagedPathState ParseManagedPathState(string value) => value switch
    {
        "NONE" => ManagedPathState.None,
        "PENDING" => ManagedPathState.Pending,
        "NEEDS_ATTENTION" => ManagedPathState.NeedsAttention,
        _ => throw new FormatException($"'{value}' is not a valid ManagedPathState database value."),
    };

    public static string Format(JobLane value) => value switch
    {
        JobLane.Fast => "FAST",
        JobLane.Io => "IO",
        JobLane.Cpu => "CPU",
        JobLane.Media => "MEDIA",
        JobLane.Face => "FACE",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static JobLane ParseJobLane(string value) => value switch
    {
        "FAST" => JobLane.Fast,
        "IO" => JobLane.Io,
        "CPU" => JobLane.Cpu,
        "MEDIA" => JobLane.Media,
        "FACE" => JobLane.Face,
        _ => throw new FormatException($"'{value}' is not a valid JobLane database value."),
    };

    public static string Format(JobState value) => value switch
    {
        JobState.Pending => "PENDING",
        JobState.Runnable => "RUNNABLE",
        JobState.Running => "RUNNING",
        JobState.Paused => "PAUSED",
        JobState.Succeeded => "SUCCEEDED",
        JobState.FailedRetryable => "FAILED_RETRYABLE",
        JobState.FailedTerminal => "FAILED_TERMINAL",
        JobState.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static JobState ParseJobState(string value) => value switch
    {
        "PENDING" => JobState.Pending,
        "RUNNABLE" => JobState.Runnable,
        "RUNNING" => JobState.Running,
        "PAUSED" => JobState.Paused,
        "SUCCEEDED" => JobState.Succeeded,
        "FAILED_RETRYABLE" => JobState.FailedRetryable,
        "FAILED_TERMINAL" => JobState.FailedTerminal,
        "CANCELLED" => JobState.Cancelled,
        _ => throw new FormatException($"'{value}' is not a valid JobState database value."),
    };

    public static string Format(FaceDecisionState value) => value switch
    {
        FaceDecisionState.Unknown => "UNKNOWN",
        FaceDecisionState.Suggested => "SUGGESTED",
        FaceDecisionState.Confirmed => "CONFIRMED",
        FaceDecisionState.Rejected => "REJECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static FaceDecisionState ParseFaceDecisionState(string value) => value switch
    {
        "UNKNOWN" => FaceDecisionState.Unknown,
        "SUGGESTED" => FaceDecisionState.Suggested,
        "CONFIRMED" => FaceDecisionState.Confirmed,
        "REJECTED" => FaceDecisionState.Rejected,
        _ => throw new FormatException($"'{value}' is not a valid FaceDecisionState database value."),
    };

    public static string Format(RelatedProfileEvidence value) => value switch
    {
        RelatedProfileEvidence.SharedMedia => "SHARED_ASSET",
        RelatedProfileEvidence.ConfirmedFace => "CONFIRMED_FACE",
        RelatedProfileEvidence.Manual => "MANUAL",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static RelatedProfileEvidence ParseRelatedProfileEvidence(string value) => value switch
    {
        "SHARED_ASSET" => RelatedProfileEvidence.SharedMedia,
        "CONFIRMED_FACE" => RelatedProfileEvidence.ConfirmedFace,
        "MANUAL" => RelatedProfileEvidence.Manual,
        _ => throw new FormatException(
            $"'{value}' is not a valid RelatedProfileEvidence database value."),
    };

    public static string Format(MediaCapability value) => value switch
    {
        MediaCapability.ManagedOriginal => "MANAGED_ORIGINAL",
        MediaCapability.Hover => "HOVER",
        MediaCapability.ModelRender => "MODEL_RENDER",
        MediaCapability.Metadata => "METADATA",
        MediaCapability.Thumbnail => "THUMBNAIL",
        MediaCapability.FaceDetection => "FACE_DETECTION",
        MediaCapability.FaceEmbedding => "FACE_EMBEDDING",
        MediaCapability.SearchProjection => "SEARCH_PROJECTION",
        MediaCapability.SimilarityRelated => "SIMILARITY_RELATED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static MediaCapability ParseMediaCapability(string value) => value switch
    {
        "MANAGED_ORIGINAL" => MediaCapability.ManagedOriginal,
        "HOVER" => MediaCapability.Hover,
        "MODEL_RENDER" => MediaCapability.ModelRender,
        "METADATA" => MediaCapability.Metadata,
        "THUMBNAIL" => MediaCapability.Thumbnail,
        "FACE_DETECTION" => MediaCapability.FaceDetection,
        "FACE_EMBEDDING" => MediaCapability.FaceEmbedding,
        "SEARCH_PROJECTION" => MediaCapability.SearchProjection,
        "SIMILARITY_RELATED" => MediaCapability.SimilarityRelated,
        _ => throw new FormatException($"'{value}' is not a valid MediaCapability database value."),
    };

    public static string Format(MediaCapabilityState value) => value switch
    {
        MediaCapabilityState.Queued => "QUEUED",
        MediaCapabilityState.Processing => "PROCESSING",
        MediaCapabilityState.Ready => "READY",
        MediaCapabilityState.NotApplicable => "NOT_APPLICABLE",
        MediaCapabilityState.Failed => "FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static MediaCapabilityState ParseMediaCapabilityState(string value) => value switch
    {
        "QUEUED" => MediaCapabilityState.Queued,
        "PROCESSING" => MediaCapabilityState.Processing,
        "READY" => MediaCapabilityState.Ready,
        "NOT_APPLICABLE" => MediaCapabilityState.NotApplicable,
        "FAILED" => MediaCapabilityState.Failed,
        _ => throw new FormatException($"'{value}' is not a valid MediaCapabilityState database value."),
    };
}

public enum ManagedPathState
{
    None,
    Pending,
    NeedsAttention,
}

public sealed class CatalogInvariantException : InvalidOperationException
{
    public CatalogInvariantException(string message)
        : base(message)
    {
    }

    public CatalogInvariantException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CatalogConcurrencyConflictException : InvalidOperationException
{
    public CatalogConcurrencyConflictException(string message)
        : base(message)
    {
    }
}
