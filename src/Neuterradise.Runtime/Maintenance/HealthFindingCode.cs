namespace Neuterradise.App.Maintenance;

public static class HealthFindingCode
{
    public const string ProfileFolderMissing = "PROFILE_FOLDER_MISSING";

    public const string ProfileFolderNameMismatch = "PROFILE_FOLDER_NAME_MISMATCH";

    public const string ManagedFileNameMismatch = "MANAGED_FILE_NAME_MISMATCH";

    public const string ManagedPathMismatch = "MANAGED_PATH_MISMATCH";

    public const string UnexpectedManagedFile = "UNEXPECTED_MANAGED_FILE";

    public const string RetiredProfileDirectory = "RETIRED_PROFILE_DIRECTORY";

    public const string StorageTokenMissing = "STORAGE_TOKEN_MISSING";

    public const string StorageTokenConflict = "STORAGE_TOKEN_CONFLICT";

    public const string StorageTokenPathMismatch = "STORAGE_TOKEN_PATH_MISMATCH";

    public const string ProfileManifestMissing = "PROFILE_MANIFEST_MISSING";

    public const string ProfileManifestMalformed = "PROFILE_MANIFEST_MALFORMED";

    public const string ProfileManifestStale = "PROFILE_MANIFEST_STALE";

    public const string ProfileManifestIdMismatch = "PROFILE_MANIFEST_ID_MISMATCH";

    public const string SourceDeletePending = "SOURCE_DELETE_PENDING";

    public const string SourceDeleteFailed = "SOURCE_DELETE_FAILED";

    public const string SourceChanged = "SOURCE_CHANGED";

    public const string ContentMismatch = "CONTENT_MISMATCH";

    public const string OwnerCountInvalid = "OWNER_COUNT_INVALID";

    public const string IdentityCardinalityInvalid = "IDENTITY_CARDINALITY_INVALID";

    public const string StagingOrphanAmbiguous = "STAGING_ORPHAN_AMBIGUOUS";

    public const string JobStuck = "JOB_STUCK";

    public const string RelatedSummaryMismatch = "RELATED_SUMMARY_MISMATCH";

    public const string CacheCorrupt = "CACHE_CORRUPT";

    public const string CacheOrphan = "CACHE_ORPHAN";

    public const string TrashStateMismatch = "TRASH_STATE_MISMATCH";

    public const string ActiveFingerprintMissing = "ACTIVE_FINGERPRINT_MISSING";

    public const string UnknownSequenceInvalid = "UNKNOWN_SEQUENCE_INVALID";

    public const string AppearanceReferenceInvalid = "APPEARANCE_REFERENCE_INVALID";

    public const string ReconciliationInconsistent = "RECONCILIATION_INCONSISTENT";

    public const string FaceRecordOrphaned = "FACE_RECORD_ORPHANED";

    public const string EmbeddingProvenanceInvalid = "EMBEDDING_PROVENANCE_INVALID";

    public const string ManualRelatedPairInvalid = "MANUAL_RELATED_PAIR_INVALID";

    public const string TaxonomyNameCollision = "TAXONOMY_NAME_COLLISION";
    public const string MediaAssetMissing = "MEDIA_ASSET_MISSING";
    public const string MediaAssetFingerprintMismatch = "MEDIA_ASSET_FINGERPRINT_MISMATCH";
    public const string MediaAssetNeedsRepair = "MEDIA_ASSET_NEEDS_REPAIR";
    public const string ArtifactContractObsolete = "MEDIA_ASSET_CONTRACT_OBSOLETE";
    public const string ProfileMediaSelectionInvalid = "PROFILE_MEDIA_SELECTION_INVALID";
    public const string ArtifactOrphaned = "MEDIA_ASSET_ORPHANED";
}
