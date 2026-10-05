namespace Neuterradise.App.SystemServices.Operations;

public static class OperationErrorCode
{
    public const string ProfileNotFound = "PROFILE_NOT_FOUND";

    public const string ProfileNotNormal = "PROFILE_NOT_NORMAL";

    public const string ProfileNameInvalid = "PROFILE_NAME_INVALID";

    public const string ProfileConflict = "PROFILE_CONFLICT";

    public const string ProfilePathReconciliationBlocked = "PROFILE_PATH_RECONCILIATION_BLOCKED";

    public const string ProfileManifestWriteFailed = "PROFILE_MANIFEST_WRITE_FAILED";

    public const string ProfileMetadataInvalid = "PROFILE_METADATA_INVALID";

    public const string ProfileCategoryNotFound = "PROFILE_CATEGORY_NOT_FOUND";

    public const string ProfileTagNotFound = "PROFILE_TAG_NOT_FOUND";

    public const string ProfileLayoutInvalid = "PROFILE_LAYOUT_INVALID";

    public const string ProfileAppearanceInvalid = "PROFILE_APPEARANCE_INVALID";

    public const string ProfileBannerPresentationInvalid = "PROFILE_BANNER_PRESENTATION_INVALID";

    public const string MediaNotFound = "ASSET_NOT_FOUND";

    public const string MediaNotActive = "ASSET_NOT_ACTIVE";

    public const string MediaOwnerConflict = "ASSET_OWNER_CONFLICT";

    public const string MediaContentMismatch = "ASSET_CONTENT_MISMATCH";

    public const string AppearanceMediaInvalid = "APPEARANCE_ASSET_INVALID";

    public const string RelatedEndpointInvalid = "RELATED_ENDPOINT_INVALID";

    public const string FaceNotFound = "FACE_NOT_FOUND";

    public const string FaceConflict = "FACE_CONFLICT";

    public const string FaceTargetInvalid = "FACE_TARGET_INVALID";

    public const string FaceMediaNotActive = "FACE_ASSET_NOT_ACTIVE";

    public const string FaceDecisionStateInvalid = "FACE_DECISION_STATE_INVALID";

    public const string UnknownOwnershipChanged = "UNKNOWN_OWNERSHIP_CHANGED";

    public const string TrashDispositionRequired = "TRASH_DISPOSITION_REQUIRED";

    public const string TrashDispositionInvalid = "TRASH_DISPOSITION_INVALID";

    public const string TrashEntryNotFound = "TRASH_ENTRY_NOT_FOUND";

    public const string TrashPlanStale = "TRASH_PLAN_STALE";

    public const string TrashPlanUnreadable = "TRASH_PLAN_UNREADABLE";

    public const string TrashDestinationConflict = "TRASH_DESTINATION_CONFLICT";

    public const string TrashPhysicalMoveFailed = "TRASH_PHYSICAL_MOVE_FAILED";

    public const string ProfileAlreadyTrashed = "PROFILE_ALREADY_TRASHED";

    public const string RestoreOwnerUnavailable = "RESTORE_OWNER_UNAVAILABLE";

    public const string PurgeConfirmationRequired = "PURGE_CONFIRMATION_REQUIRED";

    public const string PurgePlanNotFound = "PURGE_PLAN_NOT_FOUND";

    public const string PurgePlanStale = "PURGE_PLAN_STALE";

    public const string PurgeStateInvalid = "PURGE_STATE_INVALID";

    public const string PurgeDependencyBlocked = "PURGE_DEPENDENCY_BLOCKED";

    public const string PurgePlanUnreadable = "PURGE_PLAN_UNREADABLE";

    public const string PurgePhysicalDeleteFailed = "PURGE_PHYSICAL_DELETE_FAILED";

    public const string CurrentPathMissing = "CURRENT_PATH_MISSING";

    public const string CurrentPathAmbiguous = "CURRENT_PATH_AMBIGUOUS";

    public const string RepairNotSupported = "REPAIR_NOT_SUPPORTED";

    public const string RepairPlanStale = "REPAIR_PLAN_STALE";

    public const string RepairPlanNotFound = "REPAIR_PLAN_NOT_FOUND";

    public const string RepairExecutionFailed = "REPAIR_EXECUTION_FAILED";

    public const string SettingsInvalid = "SETTINGS_INVALID";

    public const string ThemeInvalid = "THEME_INVALID";


    public const string ProfileCardInvalid = "PROFILE_CARD_INVALID";


    // Protocol-level codes. These describe the boundary itself, not a feature rule.

    /// <summary>Cooperative cancellation was observed before any durable commit.</summary>
    public const string OperationCancelled = "OPERATION_CANCELLED";

    /// <summary>The request failed the command's own precondition validation.</summary>
    public const string OperationRequestInvalid = "OPERATION_REQUEST_INVALID";

    /// <summary>The same OperationId was replayed with a different canonical payload.</summary>

    /// <summary>The catalog was busy or briefly unavailable; the same operation may be retried.</summary>
    public const string CatalogUnavailable = "CATALOG_UNAVAILABLE";

    /// <summary>The catalog rejected the write in a way retrying unchanged cannot fix.</summary>
    public const string CatalogWriteFailed = "CATALOG_WRITE_FAILED";

    /// <summary>Managed storage could not be read or written for this step.</summary>
    public const string StorageUnavailable = "STORAGE_UNAVAILABLE";

    /// <summary>Windows denied access to a managed location or file needed by this step.</summary>
    public const string StorageAccessDenied = "STORAGE_ACCESS_DENIED";

    /// <summary>An external process (Explorer, default app, media tool) could not be started.</summary>
    public const string ExternalProcessFailed = "EXTERNAL_PROCESS_FAILED";

    /// <summary>An unclassified failure. Full technical detail goes to diagnostics only.</summary>
    public const string UnexpectedFailure = "UNEXPECTED_FAILURE";
}

/// <summary>
/// A safe, user-facing failure description. <see cref="ErrorCode"/> is stable and
/// machine-readable; <see cref="UserMessage"/> is one sentence a person can act on;
/// <see cref="SafeDetail"/> is optional extra sanitized context. None of these fields may carry raw
/// provider messages, stack traces or absolute internal paths — those belong in structured diagnostics.
/// </summary>
public sealed record OperationError(
    string ErrorCode,
    string UserMessage,
    string? SafeDetail = null)
{
    /// <summary>Neutral message used when a failure result was built without its own sentence.</summary>
    public const string GenericUserMessage = "The action did not complete. Nothing was changed.";

    /// <summary>Neutral message for cooperative cancellation.</summary>
    public const string CancelledUserMessage = "The action was cancelled. Nothing was changed.";

    public static OperationError Cancelled(string? userMessage = null) =>
        new(OperationErrorCode.OperationCancelled, userMessage ?? CancelledUserMessage);

    public static OperationError Unexpected(string? userMessage = null, string? safeDetail = null) =>
        new(OperationErrorCode.UnexpectedFailure, userMessage ?? GenericUserMessage, safeDetail);
}
