-- NeuTerradise initial database schema.
-- Complete pre-release schema baseline for a new catalog.
-- Catalog Schema v1 is the only supported catalog shape in this build.

-- Core catalog.
CREATE TABLE activity_log (
    activity_id    TEXT PRIMARY KEY,
    event_type     TEXT NOT NULL CHECK (trim(event_type) <> ''),
    profile_id     TEXT NULL,
    media_id       TEXT NULL,
    import_unit_id TEXT NULL,
    operation_id   TEXT NULL,
    payload_json   TEXT NOT NULL DEFAULT '{}',
    occurred_at_ms INTEGER NOT NULL
);

CREATE TABLE media_metadata (
    media_id                TEXT PRIMARY KEY
        REFERENCES media(media_id) ON DELETE CASCADE,
    metadata_schema_version INTEGER NOT NULL,
    captured_at_ms          INTEGER NULL,
    width                   INTEGER NULL,
    height                  INTEGER NULL,
    duration_ms             INTEGER NULL,
    metadata_json           TEXT NOT NULL,
    updated_at_ms           INTEGER NOT NULL,
    CHECK (width IS NULL OR width > 0),
    CHECK (height IS NULL OR height > 0),
    CHECK (duration_ms IS NULL OR duration_ms >= 0)
);

CREATE TABLE media (
    media_id                       TEXT PRIMARY KEY,
    state                          TEXT NOT NULL
        CHECK (state IN ('CANDIDATE','ACTIVE','TRASHED','RETIRED')),
    retirement_reason              TEXT NULL,
    media_type                     TEXT NOT NULL
        CHECK (media_type IN ('IMAGE','VIDEO','MODEL')),
    is_favorite                     INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0,1)),
    bundle_sha256                   TEXT NULL CHECK (bundle_sha256 IS NULL OR (length(bundle_sha256)=64 AND bundle_sha256=lower(bundle_sha256))),
    dependency_status               TEXT NOT NULL DEFAULT 'SELF_CONTAINED' CHECK (dependency_status IN ('SELF_CONTAINED','COMPLETE','DEPENDENCIES_MISSING','DEPENDENCIES_UNKNOWN')),
    dependency_discovery_state       TEXT NOT NULL DEFAULT 'COMPLETE'
        CHECK (dependency_discovery_state IN ('COMPLETE','MISSING_DEPENDENCIES','UNKNOWN','FAILED_RETRYABLE','FAILED_TERMINAL','UNSUPPORTED')),
    media_storage_token            TEXT NOT NULL UNIQUE,
    sha256                         TEXT NULL,
    byte_length                    INTEGER NULL CHECK (byte_length IS NULL OR byte_length >= 0),
    original_source_path           TEXT NULL,
    original_file_name             TEXT NULL,
    source_kind                    TEXT NULL,
    source_display_name            TEXT NULL,
    current_managed_relative_path  TEXT NULL CHECK (current_managed_relative_path IS NULL OR (current_managed_relative_path LIKE 'profiles/%' AND instr(current_managed_relative_path, '..') = 0)),
    current_managed_file_name      TEXT NULL,
    target_managed_relative_path   TEXT NULL CHECK (target_managed_relative_path IS NULL OR (target_managed_relative_path LIKE 'profiles/%' AND instr(target_managed_relative_path, '..') = 0)),
    target_managed_file_name       TEXT NULL,
    path_state                     TEXT NOT NULL DEFAULT 'NONE'
        CHECK (path_state IN ('NONE','PENDING','NEEDS_ATTENTION')),
    reconciliation_operation_id    TEXT NULL,
    created_at_ms                  INTEGER NOT NULL,
    added_to_library_at_ms         INTEGER NULL,
    trashed_at_ms                  INTEGER NULL,
    row_version                    INTEGER NOT NULL DEFAULT 0,
    CHECK (length(media_storage_token) >= 3),
    CHECK (sha256 IS NULL OR (length(sha256) = 64 AND sha256 = lower(sha256))),
    CHECK (
        (state = 'RETIRED' AND retirement_reason IN ('SKIPPED','CANCELLED','INVALID','DEDUP_REUSED'))
        OR
        (state <> 'RETIRED' AND retirement_reason IS NULL)
    ),
    CHECK (
        (path_state = 'NONE' AND reconciliation_operation_id IS NULL)
        OR
        (path_state IN ('PENDING','NEEDS_ATTENTION') AND reconciliation_operation_id IS NOT NULL)
    )
);

CREATE TABLE categories (
    category_id     TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    created_at_ms   INTEGER NOT NULL,
    updated_at_ms   INTEGER NOT NULL,
    row_version     INTEGER NOT NULL DEFAULT 0,
    UNIQUE(normalized_name)
);

CREATE TABLE face_detections (
    face_id                    TEXT PRIMARY KEY,
    media_id                   TEXT NOT NULL REFERENCES media(media_id) ON DELETE CASCADE,
    detection_key              TEXT NOT NULL CHECK (trim(detection_key) <> ''),
    bounding_box_json          TEXT NOT NULL,
    embedding                  BLOB NULL,
    embedding_space_key        TEXT NULL,
    suggested_identity_id      TEXT NULL REFERENCES identities(identity_id),
    confirmed_identity_id      TEXT NULL REFERENCES identities(identity_id),
    confidence                 REAL NULL,
    decision_state             TEXT NOT NULL
        CHECK (decision_state IN ('UNKNOWN','SUGGESTED','CONFIRMED','REJECTED')),
    model_id                   TEXT NOT NULL CHECK (trim(model_id) <> ''),
    model_version              TEXT NOT NULL CHECK (trim(model_version) <> ''),
    created_at_ms              INTEGER NOT NULL,
    updated_at_ms              INTEGER NOT NULL,
    row_version                INTEGER NOT NULL DEFAULT 0 CHECK (row_version >= 0),
    sampled_timestamp_ms       INTEGER NULL CHECK (sampled_timestamp_ms IS NULL OR sampled_timestamp_ms >= 0),
    suggested_candidates_json  TEXT NULL,
    UNIQUE(media_id, detection_key, model_id, model_version),
    CHECK ((embedding IS NULL) = (embedding_space_key IS NULL)),
    CHECK (embedding IS NULL OR length(embedding)=512),
    CHECK (
        (decision_state = 'CONFIRMED' AND confirmed_identity_id IS NOT NULL)
        OR
        (decision_state <> 'CONFIRMED' AND confirmed_identity_id IS NULL)
    )
);

CREATE TABLE identities (
    identity_id   TEXT PRIMARY KEY,
    profile_id    TEXT NOT NULL REFERENCES profiles(profile_id),
    is_active     INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    created_at_ms INTEGER NOT NULL,
    retired_at_ms INTEGER NULL,
    row_version   INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE identity_samples (
    identity_sample_id  TEXT PRIMARY KEY,
    identity_id         TEXT NOT NULL REFERENCES identities(identity_id),
    face_id             TEXT NOT NULL REFERENCES face_detections(face_id),
    embedding           BLOB NOT NULL CHECK (length(embedding) = 512),
    embedding_space_key TEXT NOT NULL CHECK (trim(embedding_space_key) <> ''),
    model_id            TEXT NOT NULL CHECK (trim(model_id) <> ''),
    model_version       TEXT NOT NULL CHECK (trim(model_version) <> ''),
    confirmed_at_ms     INTEGER NOT NULL,
    UNIQUE(face_id)
);

CREATE TABLE import_items (
    import_item_id       TEXT PRIMARY KEY,
    import_unit_id       TEXT NOT NULL REFERENCES import_units(import_unit_id),
    candidate_media_id   TEXT NULL UNIQUE REFERENCES media(media_id),
    reused_media_id      TEXT NULL REFERENCES media(media_id),
    source_path          TEXT NOT NULL,
    source_file_name     TEXT NOT NULL,
    source_byte_length   INTEGER NULL CHECK (source_byte_length IS NULL OR source_byte_length >= 0),
    source_last_write_ms INTEGER NULL,
    disposition          TEXT NOT NULL DEFAULT 'INCLUDED' CHECK (disposition IN ('INCLUDED','SKIPPED','REUSED','INVALID')),
    duplicate_decision   TEXT NULL CHECK (duplicate_decision IS NULL OR duplicate_decision IN ('INCLUDE','REUSE','SKIP')),
    cleanup_policy       TEXT NOT NULL DEFAULT 'COPY' CHECK (cleanup_policy IN ('COPY','MOVE')),
    source_identity_json TEXT NULL,
    preparation_status   TEXT NOT NULL DEFAULT 'PENDING' CHECK (preparation_status IN ('PENDING','RUNNING','READY','FAILED_RETRYABLE','FAILED_TERMINAL')),
    source_cleanup_state TEXT NOT NULL DEFAULT 'SOURCE_PRESENT'
        CHECK (source_cleanup_state IN (
            'SOURCE_PRESENT',
            'DESTINATION_VERIFIED',
            'LIBRARY_COMMITTED',
            'SOURCE_DELETE_PENDING',
            'SOURCE_CONSUMED',
            'SOURCE_DELETE_FAILED',
            'SOURCE_PRESERVED',
            'SOURCE_CHANGED'
        )),
    source_cleanup_error TEXT NULL,
    created_at_ms        INTEGER NOT NULL,
    updated_at_ms        INTEGER NOT NULL,
    row_version          INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE import_sessions (
    import_session_id TEXT PRIMARY KEY,
    state             TEXT NOT NULL CHECK (state IN ('OPEN','COMPLETED','CANCELLED')),
    is_paused         INTEGER NOT NULL DEFAULT 0 CHECK (is_paused IN (0,1)),
    hidden_from_history INTEGER NOT NULL DEFAULT 0 CHECK (hidden_from_history IN (0,1)),
    created_at_ms     INTEGER NOT NULL,
    updated_at_ms     INTEGER NOT NULL,
    completed_at_ms   INTEGER NULL,
    row_version       INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE import_units (
    import_unit_id           TEXT PRIMARY KEY,
    import_session_id        TEXT NOT NULL REFERENCES import_sessions(import_session_id),
    parent_import_unit_id    TEXT NULL REFERENCES import_units(import_unit_id),
    source_kind              TEXT NOT NULL,
    source_display_name      TEXT NOT NULL,
    source_path_or_reference TEXT NULL,
    state                    TEXT NOT NULL CHECK (state IN ('INTAKE','PREPARING','READY_FOR_VERIFICATION','COMMITTING','COMMITTED','COMPLETED','FAILED_RETRYABLE','FAILED_TERMINAL','CANCELLED','COMMITTED_WITH_CLEANUP_ATTENTION')),
    is_paused                INTEGER NOT NULL DEFAULT 0 CHECK (is_paused IN (0,1)),
    hidden_from_history      INTEGER NOT NULL DEFAULT 0 CHECK (hidden_from_history IN (0,1)),
    destination_kind         TEXT NULL,
    destination_profile_id   TEXT NULL REFERENCES profiles(profile_id),
    verification_step        INTEGER NOT NULL DEFAULT 1 CHECK (verification_step BETWEEN 1 AND 5),
    verification_draft_json  TEXT NOT NULL DEFAULT '{}',
    verification_version     INTEGER NOT NULL DEFAULT 0,
    commit_operation_id      TEXT NULL,
    library_commit_state     TEXT NOT NULL DEFAULT 'NOT_COMMITTED',
    preparation_started_at_ms INTEGER NULL,
    rollback_settled         INTEGER NULL CHECK (rollback_settled IS NULL OR rollback_settled IN (0,1)),
    created_at_ms            INTEGER NOT NULL,
    updated_at_ms            INTEGER NOT NULL,
    completed_at_ms          INTEGER NULL,
    row_version              INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE job_dependencies (
    job_id            TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,
    depends_on_job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,
    PRIMARY KEY (job_id, depends_on_job_id),
    CHECK (job_id <> depends_on_job_id)
);

CREATE TABLE jobs (
    job_id             TEXT PRIMARY KEY,
    kind               TEXT NOT NULL CHECK (trim(kind) <> ''),
    lane               TEXT NOT NULL
        CHECK (lane IN ('FAST','IO','CPU','MEDIA','FACE')),
    state              TEXT NOT NULL
        CHECK (state IN (
            'PENDING',
            'RUNNABLE',
            'RUNNING',
            'PAUSED',
            'SUCCEEDED',
            'FAILED_RETRYABLE',
            'FAILED_TERMINAL',
            'CANCELLED'
        )),
    priority           INTEGER NOT NULL DEFAULT 50,
    owner_type         TEXT NOT NULL CHECK (trim(owner_type) <> ''),
    owner_id           TEXT NOT NULL,
    attempt            INTEGER NOT NULL DEFAULT 0 CHECK (attempt >= 0),
    max_attempts       INTEGER NOT NULL DEFAULT 5 CHECK (max_attempts > 0),
    not_before_ms      INTEGER NULL,
    progress_completed INTEGER NULL CHECK (progress_completed IS NULL OR progress_completed >= 0),
    progress_total     INTEGER NULL CHECK (progress_total IS NULL OR progress_total >= 0),
    stage              TEXT NULL,
    checkpoint_json    TEXT NOT NULL DEFAULT '{}',
    error_code         TEXT NULL,
    error_detail_safe  TEXT NULL,
    created_at_ms      INTEGER NOT NULL,
    started_at_ms      INTEGER NULL,
    completed_at_ms    INTEGER NULL,
    row_version        INTEGER NOT NULL DEFAULT 0 CHECK (row_version >= 0),
    CHECK (attempt <= max_attempts),
    CHECK (
        progress_completed IS NULL
        OR progress_total IS NULL
        OR progress_completed <= progress_total
    )
);

CREATE TABLE profile_appearance (
    profile_id       TEXT PRIMARY KEY
        REFERENCES profiles(profile_id) ON DELETE CASCADE,
    schema_version   INTEGER NOT NULL,
    layout_preset_id TEXT NOT NULL DEFAULT 'cinematic',
    overrides_json   TEXT NOT NULL,
    cover_media_asset_id TEXT NULL REFERENCES media_assets(media_asset_id),
    banner_media_asset_id TEXT NULL REFERENCES media_assets(media_asset_id),
    figure_media_id TEXT NULL REFERENCES media(media_id),
    updated_at_ms    INTEGER NOT NULL,
    row_version      INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE profile_media (
    profile_id     TEXT NOT NULL REFERENCES profiles(profile_id),
    media_id       TEXT NOT NULL REFERENCES media(media_id),
    relation_type  TEXT NOT NULL
        CHECK (relation_type IN ('OWNER','APPEARS','MANUAL')),
    provenance_key TEXT NULL,
    publication_import_unit_id TEXT NULL REFERENCES import_units(import_unit_id),
    created_at_ms  INTEGER NOT NULL,
    PRIMARY KEY (profile_id, media_id, relation_type)
);

CREATE TABLE profile_tags (
    profile_id    TEXT NOT NULL REFERENCES profiles(profile_id) ON DELETE CASCADE,
    tag_id        TEXT NOT NULL REFERENCES tags(tag_id) ON DELETE CASCADE,
    created_at_ms INTEGER NOT NULL,
    PRIMARY KEY (profile_id, tag_id)
);

CREATE TABLE profiles (
    profile_id                    TEXT PRIMARY KEY,
    kind                          TEXT NOT NULL
        CHECK (kind IN ('NORMAL','UNKNOWN')),
    display_name                  TEXT NULL,
    unknown_sequence              INTEGER NULL UNIQUE,
    category_id                   TEXT NULL REFERENCES categories(category_id),
    rating                        INTEGER NULL CHECK (rating IS NULL OR rating BETWEEN 0 AND 5),
    is_favorite                   INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0,1)),
    overview                      TEXT NULL,
    notes                         TEXT NULL,
    profile_storage_token         TEXT NULL UNIQUE,
    visibility                    TEXT NOT NULL DEFAULT 'PUBLISHED' CHECK (visibility IN ('DRAFT','PUBLISHED')),
    current_managed_relative_path TEXT NULL CHECK (current_managed_relative_path IS NULL OR (current_managed_relative_path LIKE 'profiles/%' AND instr(current_managed_relative_path, '..') = 0)),
    target_managed_relative_path  TEXT NULL CHECK (target_managed_relative_path IS NULL OR (target_managed_relative_path LIKE 'profiles/%' AND instr(target_managed_relative_path, '..') = 0)),
    path_state                    TEXT NOT NULL DEFAULT 'NONE'
        CHECK (path_state IN ('NONE','PENDING','NEEDS_ATTENTION')),
    reconciliation_operation_id   TEXT NULL,
    cover_media_id                TEXT NULL REFERENCES media(media_id),
    created_at_ms                 INTEGER NOT NULL,
    updated_at_ms                 INTEGER NOT NULL,
    trashed_at_ms                 INTEGER NULL,
    row_version                   INTEGER NOT NULL DEFAULT 0,
    CHECK (
        (kind = 'NORMAL' AND display_name IS NOT NULL AND trim(display_name) <> '' AND unknown_sequence IS NULL)
        OR
        (kind = 'UNKNOWN' AND display_name IS NULL AND unknown_sequence IS NOT NULL AND unknown_sequence > 0)
    ),
    CHECK (
        (path_state = 'NONE' AND reconciliation_operation_id IS NULL)
        OR
        (path_state IN ('PENDING','NEEDS_ATTENTION') AND reconciliation_operation_id IS NOT NULL)
    )
);

CREATE TABLE media_assets (
    media_asset_id TEXT PRIMARY KEY,
    media_id TEXT NOT NULL REFERENCES media(media_id) ON DELETE CASCADE,
    role TEXT NOT NULL CHECK (role IN ('THUMBNAIL','HOVER','MODEL_RENDER')),
    contract_version INTEGER NOT NULL CHECK (contract_version > 0),
    state TEXT NOT NULL CHECK (state IN ('READY','NEEDS_REPAIR')),
    relative_path TEXT NOT NULL UNIQUE CHECK (relative_path LIKE 'media-assets/%' AND instr(relative_path, '..') = 0),
    byte_length INTEGER NOT NULL CHECK (byte_length > 0),
    sha256 TEXT NOT NULL CHECK (length(sha256) = 64 AND sha256 = lower(sha256)),
    pixel_width INTEGER NULL CHECK (pixel_width IS NULL OR pixel_width > 0),
    pixel_height INTEGER NULL CHECK (pixel_height IS NULL OR pixel_height > 0),
    duration_ms INTEGER NULL CHECK (duration_ms IS NULL OR duration_ms > 0),
    source_timestamp_ms INTEGER NULL CHECK (source_timestamp_ms IS NULL OR source_timestamp_ms >= 0),
    created_at_ms INTEGER NOT NULL,
    updated_at_ms INTEGER NOT NULL,
    row_version INTEGER NOT NULL DEFAULT 0 CHECK (row_version >= 0),
    CHECK (role <> 'THUMBNAIL' OR (pixel_width IS NOT NULL AND pixel_height IS NOT NULL)),
    CHECK (role <> 'HOVER' OR (duration_ms IS NOT NULL AND duration_ms BETWEEN 1 AND 6000)),
    CHECK (role <> 'MODEL_RENDER' OR (contract_version = 1 AND pixel_width IS NULL
        AND pixel_height IS NULL AND duration_ms IS NULL AND source_timestamp_ms IS NULL)),
    UNIQUE (media_id, role)
);

CREATE INDEX ix_media_assets_media_state ON media_assets(media_id, state, role);

CREATE TABLE related_profile_evidence (
    evidence_key    TEXT PRIMARY KEY,
    profile_id_low  TEXT NOT NULL REFERENCES profiles(profile_id),
    profile_id_high TEXT NOT NULL REFERENCES profiles(profile_id),
    evidence_type   TEXT NOT NULL
        CHECK (evidence_type IN ('SHARED_ASSET','CONFIRMED_FACE','MANUAL')),
    media_id        TEXT NULL REFERENCES media(media_id),
    face_id         TEXT NULL REFERENCES face_detections(face_id),
    created_at_ms   INTEGER NOT NULL,
    CHECK (profile_id_low < profile_id_high),
    CHECK (
        (evidence_type = 'SHARED_ASSET' AND media_id IS NOT NULL AND face_id IS NULL)
        OR
        (evidence_type = 'CONFIRMED_FACE' AND media_id IS NOT NULL AND face_id IS NOT NULL)
        OR
        (evidence_type = 'MANUAL' AND media_id IS NULL AND face_id IS NULL)
    )
);

CREATE TABLE related_profile_summary (
    profile_id_low       TEXT NOT NULL REFERENCES profiles(profile_id),
    profile_id_high      TEXT NOT NULL REFERENCES profiles(profile_id),
    shared_media_count   INTEGER NOT NULL DEFAULT 0 CHECK (shared_media_count >= 0),
    confirmed_face_count INTEGER NOT NULL DEFAULT 0 CHECK (confirmed_face_count >= 0),
    manual_relation      INTEGER NOT NULL DEFAULT 0 CHECK (manual_relation IN (0,1)),
    last_evidence_at_ms  INTEGER NULL,
    rank_score           INTEGER NOT NULL DEFAULT 0 CHECK (rank_score >= 0),
    updated_at_ms        INTEGER NOT NULL,
    PRIMARY KEY (profile_id_low, profile_id_high),
    CHECK (profile_id_low < profile_id_high)
);

CREATE TABLE sequences (
    name       TEXT PRIMARY KEY,
    next_value INTEGER NOT NULL CHECK (next_value > 0)
);

CREATE TABLE settings (
    key           TEXT PRIMARY KEY,
    value_json    TEXT NOT NULL,
    updated_at_ms INTEGER NOT NULL
);

CREATE TABLE storage_operations (
    operation_id      TEXT PRIMARY KEY,
    kind              TEXT NOT NULL,
    entity_type       TEXT NOT NULL,
    entity_id         TEXT NOT NULL,
    state             TEXT NOT NULL,
    checkpoint_json   TEXT NOT NULL,
    created_at_ms     INTEGER NOT NULL,
    updated_at_ms     INTEGER NOT NULL,
    completed_at_ms   INTEGER NULL,
    error_code        TEXT NULL,
    error_detail_safe TEXT NULL,
    row_version       INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE tags (
    tag_id          TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    created_at_ms   INTEGER NOT NULL,
    updated_at_ms   INTEGER NOT NULL,
    row_version     INTEGER NOT NULL DEFAULT 0,
    UNIQUE(normalized_name)
);

CREATE TABLE trash_entries (
    trash_entry_id         TEXT PRIMARY KEY,
    entity_type            TEXT NOT NULL CHECK (trim(entity_type) <> ''),
    entity_id              TEXT NOT NULL,
    state                  TEXT NOT NULL CHECK (trim(state) <> ''),
    recovery_relative_path TEXT NULL CHECK (recovery_relative_path IS NULL OR (recovery_relative_path LIKE '_trash/%' AND instr(recovery_relative_path, '..') = 0)),
    plan_json              TEXT NOT NULL,
    created_at_ms          INTEGER NOT NULL,
    updated_at_ms          INTEGER NOT NULL,
    completed_at_ms        INTEGER NULL,
    row_version            INTEGER NOT NULL DEFAULT 0 CHECK (row_version >= 0)
);

CREATE INDEX ix_activity_log_media
ON activity_log(media_id, occurred_at_ms DESC)
WHERE media_id IS NOT NULL;

CREATE INDEX ix_activity_log_profile
ON activity_log(profile_id, occurred_at_ms DESC)
WHERE profile_id IS NOT NULL;

CREATE INDEX ix_activity_log_time
ON activity_log(occurred_at_ms DESC, activity_id);

CREATE INDEX ix_media_current_path
ON media(current_managed_relative_path, current_managed_file_name)
WHERE current_managed_relative_path IS NOT NULL;

CREATE INDEX ix_media_state
ON media(state, added_to_library_at_ms, media_id);

CREATE INDEX ix_media_active_byte_length
ON media(byte_length, media_id)
WHERE state = 'ACTIVE';

CREATE INDEX ix_media_state_type
ON media(state, media_type, added_to_library_at_ms DESC, media_id);

CREATE INDEX ix_media_favorite
ON media(is_favorite, state, added_to_library_at_ms DESC, media_id);

CREATE INDEX ix_face_detections_media_decision
ON face_detections(media_id, decision_state, face_id);

CREATE INDEX ix_face_detections_decision
ON face_detections(decision_state, created_at_ms DESC, face_id);

CREATE INDEX ix_identity_samples_identity_space
ON identity_samples(identity_id, embedding_space_key);

CREATE INDEX ix_import_items_unit
ON import_items(import_unit_id, disposition, import_item_id);

CREATE INDEX ix_import_units_session_state
ON import_units(import_session_id, state, import_unit_id);

CREATE INDEX ix_jobs_owner
ON jobs(owner_type, owner_id, state);

CREATE INDEX ix_jobs_runnable
ON jobs(state, lane, priority DESC, not_before_ms, created_at_ms);

CREATE INDEX ix_profile_media_media_relation
ON profile_media(media_id, relation_type, profile_id);

CREATE INDEX ix_profile_media_profile_relation
ON profile_media(profile_id, relation_type, media_id);

CREATE INDEX ix_profile_tags_tag
ON profile_tags(tag_id, profile_id);

CREATE INDEX ix_profiles_active_name
ON profiles(trashed_at_ms, display_name, profile_id);

CREATE INDEX ix_profiles_active_rating
ON profiles(trashed_at_ms, rating DESC, updated_at_ms DESC, profile_id);

CREATE INDEX ix_profiles_active_updated
ON profiles(trashed_at_ms, updated_at_ms DESC, profile_id);

CREATE INDEX ix_profiles_category
ON profiles(category_id, trashed_at_ms, profile_id)
WHERE category_id IS NOT NULL;

CREATE INDEX ix_profiles_favorite
ON profiles(is_favorite, trashed_at_ms, updated_at_ms DESC, profile_id);

CREATE INDEX ix_related_evidence_pair
ON related_profile_evidence(profile_id_low, profile_id_high, evidence_type);

CREATE INDEX ix_related_summary_high
ON related_profile_summary(profile_id_high, profile_id_low);

CREATE INDEX ix_storage_operations_entity
ON storage_operations(entity_type, entity_id, state);

CREATE INDEX ix_trash_entries_entity
ON trash_entries(entity_type, entity_id, state);

CREATE INDEX ix_trash_entries_state
ON trash_entries(state, created_at_ms DESC, trash_entry_id);

CREATE UNIQUE INDEX ux_identities_one_active_per_profile
ON identities(profile_id)
WHERE is_active = 1;

CREATE UNIQUE INDEX ux_profile_media_one_owner
ON profile_media(media_id)
WHERE relation_type = 'OWNER';

CREATE TRIGGER trg_media_nonactive_has_no_owner
BEFORE UPDATE OF state ON media
WHEN NEW.state <> 'ACTIVE'
 AND EXISTS (
     SELECT 1 FROM profile_media
     WHERE media_id = OLD.media_id AND relation_type = 'OWNER'
 )
BEGIN
    SELECT RAISE(ABORT, 'a non-ACTIVE media item cannot retain an OWNER');
END;

CREATE TRIGGER trg_media_storage_token_immutable
BEFORE UPDATE OF media_storage_token ON media
WHEN OLD.media_storage_token IS NOT NULL AND NEW.media_storage_token IS NOT OLD.media_storage_token
BEGIN
    SELECT RAISE(ABORT, 'media storage token is immutable');
END;

CREATE TRIGGER trg_identities_active_normal_only
BEFORE INSERT ON identities
WHEN NEW.is_active = 1 AND NOT EXISTS (SELECT 1 FROM profiles WHERE profile_id=NEW.profile_id AND kind='NORMAL' AND trashed_at_ms IS NULL)
BEGIN
    SELECT RAISE(ABORT, 'only a NORMAL profile may have an active identity');
END;

CREATE TRIGGER trg_profile_media_owner_requires_active
BEFORE INSERT ON profile_media
WHEN NEW.relation_type = 'OWNER'
 AND (SELECT state FROM media WHERE media_id = NEW.media_id) <> 'ACTIVE'
BEGIN
    SELECT RAISE(ABORT, 'only ACTIVE media may have an OWNER');
END;

CREATE TRIGGER trg_profiles_storage_token_immutable
BEFORE UPDATE OF profile_storage_token ON profiles
WHEN OLD.profile_storage_token IS NOT NULL AND NEW.profile_storage_token IS NOT OLD.profile_storage_token
BEGIN
    SELECT RAISE(ABORT, 'profile storage token is immutable');
END;

CREATE TRIGGER trg_profiles_unknown_has_no_active_identity
BEFORE UPDATE OF kind ON profiles
WHEN NEW.kind = 'UNKNOWN'
 AND EXISTS (SELECT 1 FROM identities WHERE profile_id = OLD.profile_id AND is_active = 1)
BEGIN
    SELECT RAISE(ABORT, 'an UNKNOWN profile cannot have an active identity');
END;

CREATE TABLE media_components (
    media_id TEXT NOT NULL REFERENCES media(media_id) ON DELETE CASCADE,
    component_relative_path TEXT NOT NULL CHECK (trim(component_relative_path) <> '' AND component_relative_path NOT GLOB '[A-Za-z]:*' AND component_relative_path NOT LIKE '/%' AND component_relative_path NOT LIKE '\%' AND instr(component_relative_path, '..') = 0),
    normalized_component_path TEXT NOT NULL CHECK (trim(normalized_component_path) <> '' AND normalized_component_path NOT GLOB '[A-Za-z]:*' AND normalized_component_path NOT LIKE '/%' AND normalized_component_path NOT LIKE '\%' AND instr(normalized_component_path, '..') = 0),
    component_role TEXT NOT NULL CHECK (component_role IN ('PRIMARY','DEPENDENCY')),
    sha256 TEXT NOT NULL CHECK (length(sha256)=64 AND sha256=lower(sha256)),
    byte_length INTEGER NOT NULL CHECK (byte_length >= 0),
    original_source_path TEXT NULL,
    source_identity_json TEXT NULL,
    source_cleanup_state TEXT NOT NULL DEFAULT 'SOURCE_PRESENT' CHECK (source_cleanup_state IN ('SOURCE_PRESENT','DESTINATION_VERIFIED','LIBRARY_COMMITTED','SOURCE_DELETE_PENDING','SOURCE_CONSUMED','SOURCE_DELETE_FAILED','SOURCE_PRESERVED','SOURCE_CHANGED')),
    source_cleanup_error TEXT NULL,
    row_version INTEGER NOT NULL DEFAULT 0 CHECK (row_version >= 0),
    PRIMARY KEY (media_id, component_relative_path),
    UNIQUE(media_id, normalized_component_path)
);
CREATE UNIQUE INDEX ux_media_components_primary ON media_components(media_id)
WHERE component_role='PRIMARY';

CREATE TRIGGER trg_profile_media_update_owner_requires_active
BEFORE UPDATE OF media_id, relation_type ON profile_media
WHEN NEW.relation_type='OWNER'
 AND NOT EXISTS (SELECT 1 FROM media WHERE media_id=NEW.media_id AND state='ACTIVE')
BEGIN SELECT RAISE(ABORT,'only ACTIVE media may have an OWNER'); END;

CREATE TRIGGER trg_profile_media_active_profile_insert
BEFORE INSERT ON profile_media
WHEN NOT EXISTS (SELECT 1 FROM profiles WHERE profile_id=NEW.profile_id AND trashed_at_ms IS NULL)
BEGIN SELECT RAISE(ABORT,'active relations require a nontrashed profile'); END;

CREATE TRIGGER trg_profile_media_active_profile_update
BEFORE UPDATE OF profile_id ON profile_media
WHEN NOT EXISTS (SELECT 1 FROM profiles WHERE profile_id=NEW.profile_id AND trashed_at_ms IS NULL)
BEGIN SELECT RAISE(ABORT,'active relations require a nontrashed profile'); END;

CREATE TRIGGER trg_identities_update_active_normal_only
BEFORE UPDATE OF profile_id,is_active ON identities
WHEN NEW.is_active=1
 AND NOT EXISTS (SELECT 1 FROM profiles WHERE profile_id=NEW.profile_id AND kind='NORMAL' AND trashed_at_ms IS NULL)
BEGIN SELECT RAISE(ABORT,'active identity requires active NORMAL profile'); END;

CREATE TRIGGER trg_profiles_cover_active_update
BEFORE UPDATE OF cover_media_id ON profiles
WHEN NEW.cover_media_id IS NOT NULL
 AND NOT EXISTS (SELECT 1 FROM media WHERE media_id=NEW.cover_media_id AND state='ACTIVE' AND media_type IN ('IMAGE','VIDEO'))
BEGIN SELECT RAISE(ABORT,'Cover requires active image or video'); END;

CREATE TRIGGER trg_media_nonactive_has_no_appearance_source
BEFORE UPDATE OF state ON media
WHEN NEW.state<>'ACTIVE'
 AND EXISTS (SELECT 1 FROM profiles WHERE cover_media_id=OLD.media_id)
BEGIN SELECT RAISE(ABORT,'clear Cover reference before media leaves ACTIVE'); END;

CREATE TRIGGER trg_profiles_appearance_source_active_insert
BEFORE INSERT ON profiles
WHEN NEW.cover_media_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM media WHERE media_id=NEW.cover_media_id AND state='ACTIVE' AND media_type IN ('IMAGE','VIDEO'))
BEGIN SELECT RAISE(ABORT,'new Profile Cover source requires active image or video'); END;

CREATE TRIGGER trg_profiles_unknown_appearance_source_insert
BEFORE INSERT ON profiles
WHEN NEW.kind='UNKNOWN' AND NEW.cover_media_id IS NOT NULL
BEGIN SELECT RAISE(ABORT,'UNKNOWN profile uses system appearance'); END;

CREATE TRIGGER trg_profiles_unknown_appearance_source_update
BEFORE UPDATE OF kind,cover_media_id ON profiles
WHEN NEW.kind='UNKNOWN' AND NEW.cover_media_id IS NOT NULL
BEGIN SELECT RAISE(ABORT,'UNKNOWN profile uses system appearance'); END;

-- A NORMAL Profile is born as DRAFT. Publication is the durable READY boundary:
-- it must already own a valid prepared Cover selection and its appearance row.
CREATE TRIGGER trg_profiles_normal_publish_insert
BEFORE INSERT ON profiles
WHEN NEW.kind='NORMAL' AND NEW.visibility='PUBLISHED'
BEGIN SELECT RAISE(ABORT,'NORMAL profile must be created DRAFT and published only after appearance readiness'); END;

CREATE TRIGGER trg_profiles_normal_publish_update
BEFORE UPDATE OF visibility ON profiles
WHEN NEW.kind='NORMAL' AND NEW.visibility='PUBLISHED' AND OLD.visibility<>'PUBLISHED'
  AND NOT EXISTS (
      SELECT 1
      FROM profile_appearance pa
      JOIN media_assets cover_asset ON cover_asset.media_asset_id = pa.cover_media_asset_id
      JOIN media cover_media ON cover_media.media_id = cover_asset.media_id
      JOIN profile_media relation
        ON relation.profile_id = NEW.profile_id
       AND relation.media_id = cover_media.media_id
      WHERE pa.profile_id = NEW.profile_id
        AND pa.schema_version = 1
        AND cover_asset.role = 'THUMBNAIL'
        AND cover_asset.state = 'READY'
        AND cover_media.state = 'ACTIVE'
        AND cover_media.trashed_at_ms IS NULL
        AND cover_media.media_id = NEW.cover_media_id
        AND relation.publication_import_unit_id IS NULL
  )
BEGIN SELECT RAISE(ABORT,'NORMAL profile publication requires a ready canonical appearance and Cover'); END;

CREATE TRIGGER trg_profile_appearance_normal_insert
BEFORE INSERT ON profile_appearance
WHEN NOT EXISTS (SELECT 1 FROM profiles WHERE profile_id=NEW.profile_id AND kind='NORMAL')
BEGIN SELECT RAISE(ABORT,'appearance belongs to NORMAL profile'); END;

CREATE TRIGGER trg_profile_appearance_normal_update
BEFORE UPDATE OF profile_id ON profile_appearance
WHEN NOT EXISTS (SELECT 1 FROM profiles WHERE profile_id=NEW.profile_id AND kind='NORMAL')
BEGIN SELECT RAISE(ABORT,'appearance belongs to NORMAL profile'); END;

CREATE TRIGGER trg_profile_media_active_media_insert
BEFORE INSERT ON profile_media
WHEN NOT EXISTS (SELECT 1 FROM media WHERE media_id=NEW.media_id AND state='ACTIVE')
BEGIN SELECT RAISE(ABORT,'Profile relation requires ACTIVE media'); END;

CREATE TRIGGER trg_profile_media_active_media_update
BEFORE UPDATE OF media_id,relation_type ON profile_media
WHEN NOT EXISTS (SELECT 1 FROM media WHERE media_id=NEW.media_id AND state='ACTIVE')
BEGIN SELECT RAISE(ABORT,'Profile relation requires ACTIVE media'); END;

CREATE TRIGGER trg_media_nonactive_has_no_relations
BEFORE UPDATE OF state ON media
WHEN NEW.state<>'ACTIVE' AND EXISTS (SELECT 1 FROM profile_media WHERE media_id=OLD.media_id)
BEGIN SELECT RAISE(ABORT,'remove active relations before deactivating media'); END;

CREATE TRIGGER trg_profiles_trash_has_no_active_relations
BEFORE UPDATE OF trashed_at_ms ON profiles
WHEN NEW.trashed_at_ms IS NOT NULL AND (
    EXISTS (SELECT 1 FROM profile_media WHERE profile_id=OLD.profile_id)
    OR EXISTS (SELECT 1 FROM identities WHERE profile_id=OLD.profile_id AND is_active=1))
BEGIN SELECT RAISE(ABORT,'resolve Profile relations and identity before trash'); END;

-- Presentation contract.
-- Versioned presentation selection and state.
-- One row per (scope, slot) keeps presentation choices extensible.
-- Presentation data never owns domain data: no foreign keys into profiles/media, so removing a
-- binding or a pack can never cascade into Profile, Media or Vault bytes.

CREATE TABLE presentation_packs (
    pack_id          TEXT PRIMARY KEY,
    version          TEXT NOT NULL,
    name             TEXT NOT NULL,
    origin           TEXT NOT NULL CHECK (origin IN ('BUILTIN','USER')),
    content_hash     TEXT NOT NULL,
    contract_version INTEGER NOT NULL CHECK (contract_version > 0),
    state            TEXT NOT NULL CHECK (state IN ('ACTIVE','INVALID')),
    diagnostics_json TEXT NULL,
    installed_at_ms  INTEGER NOT NULL,
    updated_at_ms    INTEGER NOT NULL
);

CREATE TABLE presentation_bindings (
    scope_kind         TEXT NOT NULL CHECK (scope_kind IN ('GLOBAL','SURFACE','PROFILE','ITEM')),
    scope_id           TEXT NOT NULL CHECK (trim(scope_id) <> ''),
    slot               TEXT NOT NULL CHECK (trim(slot) <> ''),
    definition_pack_id TEXT NULL,
    definition_id      TEXT NULL,
    state_json         TEXT NULL,
    schema_version     INTEGER NOT NULL CHECK (schema_version > 0),
    updated_at_ms      INTEGER NOT NULL,
    row_version        INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (scope_kind, scope_id, slot),
    CHECK ((definition_pack_id IS NULL) = (definition_id IS NULL)),
    CHECK (definition_id IS NOT NULL OR state_json IS NOT NULL)
);

CREATE INDEX ix_presentation_bindings_definition
ON presentation_bindings(definition_pack_id, definition_id);

-- Import assignment review.
-- Durable grouped exceptions produced by the central import-assignment policy.
-- The cluster stores evidence, not ownership certainty.  Ownership remains authoritative in
-- profile_media and is changed only by the existing Unknown resolution operation.

CREATE TABLE import_assignment_clusters (
    cluster_id            TEXT PRIMARY KEY,
    import_unit_id        TEXT NOT NULL REFERENCES import_units(import_unit_id),
    cluster_key           TEXT NOT NULL,
    source_directory      TEXT NULL,
    candidate_profile_id  TEXT NULL REFERENCES profiles(profile_id),
    evidence_kind         TEXT NOT NULL
        CHECK (evidence_kind IN ('FACE_MATCH','UNKNOWN_FACE','CONFLICTING','NO_FACE')),
    state                 TEXT NOT NULL DEFAULT 'PENDING'
        CHECK (state IN ('PENDING','ACCEPTED','KEPT_UNKNOWN')),
    decided_profile_id    TEXT NULL REFERENCES profiles(profile_id),
    created_at_ms         INTEGER NOT NULL,
    updated_at_ms         INTEGER NOT NULL,
    row_version           INTEGER NOT NULL DEFAULT 0,
    UNIQUE(import_unit_id, cluster_key),
    CHECK (
        (state = 'PENDING' AND decided_profile_id IS NULL)
        OR (state = 'ACCEPTED' AND decided_profile_id IS NOT NULL)
        OR (state = 'KEPT_UNKNOWN' AND decided_profile_id IS NULL)
    )
);

CREATE TABLE import_assignment_cluster_items (
    cluster_id      TEXT NOT NULL REFERENCES import_assignment_clusters(cluster_id) ON DELETE CASCADE,
    import_item_id  TEXT NOT NULL REFERENCES import_items(import_item_id),
    PRIMARY KEY (cluster_id, import_item_id)
);

CREATE INDEX ix_import_assignment_clusters_pending
ON import_assignment_clusters(state, import_unit_id, created_at_ms, cluster_id);

CREATE INDEX ix_import_assignment_cluster_items_item
ON import_assignment_cluster_items(import_item_id, cluster_id);

-- Unknown Profile sequence.
-- Initialize Unknown Profile allocation after the greatest persisted sequence value.
INSERT INTO sequences(name, next_value) VALUES ('unknown_profile', 1);

CREATE INDEX ix_profiles_visibility_active
ON profiles(visibility, trashed_at_ms, updated_at_ms DESC, profile_id)
WHERE visibility = 'PUBLISHED';

-- Media capability readiness.
-- Preparation completes when every required capability is READY or NOT_APPLICABLE.

CREATE TABLE media_capability_readiness (
    media_id        TEXT NOT NULL REFERENCES media(media_id) ON DELETE CASCADE,
    capability      TEXT NOT NULL CHECK (capability IN (
                    'MANAGED_ORIGINAL', 'METADATA', 'THUMBNAIL', 'HOVER', 'MODEL_RENDER',
                    'FACE_DETECTION', 'FACE_EMBEDDING', 'SEARCH_PROJECTION', 'SIMILARITY_RELATED')),
    state           TEXT NOT NULL DEFAULT 'QUEUED'
                    CHECK (state IN ('QUEUED', 'PROCESSING', 'READY', 'NOT_APPLICABLE', 'FAILED')),
    job_id          TEXT,
    source_fingerprint TEXT,
    updated_at_ms   INTEGER NOT NULL,
    PRIMARY KEY (media_id, capability)
);

CREATE INDEX ix_media_capability_state
ON media_capability_readiness(state, media_id);

-- preparation_started_at_ms is the durable Stage 2 start: permanent MediaAsset preparation after canonical domain commit.
-- Cancellation rollback settlement.
-- rollback_settled is NULL when not cancelled, 0 while rollback is pending, and 1 when settled.

-- Import publication boundary.
-- canonical commit makes canonical media authoritative before Verify/Save, but profile projection must not
-- expose that delta until the publication boundary. NULL means the relation is published; a Unit id
-- means the relation belongs to that unpublished import delta.

CREATE INDEX ix_profile_media_publication_import_unit
ON profile_media(publication_import_unit_id)
WHERE publication_import_unit_id IS NOT NULL;

-- Mark import-owned relations at insert time so unpublished Stage-1 media never leaks into a Profile.
CREATE TRIGGER trg_profile_media_mark_import_delta
AFTER INSERT ON profile_media
WHEN NEW.publication_import_unit_id IS NULL
BEGIN
    UPDATE profile_media
    SET publication_import_unit_id = (
        SELECT u.import_unit_id
        FROM import_units u
        JOIN import_items i ON i.import_unit_id = u.import_unit_id
        WHERE u.destination_profile_id = NEW.profile_id
          AND u.state NOT IN (
              'CANCELLED','COMMITTED','COMPLETED',
              'COMMITTED_WITH_CLEANUP_ATTENTION','FAILED_TERMINAL')
          AND (
              (NEW.relation_type = 'OWNER' AND i.candidate_media_id = NEW.media_id)
              OR
              (NEW.relation_type = 'MANUAL'
               AND i.reused_media_id = NEW.media_id
               AND lower(COALESCE(NEW.provenance_key, '')) = 'import:' || lower(u.import_unit_id))
          )
        ORDER BY u.created_at_ms DESC, u.import_unit_id DESC
        LIMIT 1
    )
    WHERE profile_id = NEW.profile_id
      AND media_id = NEW.media_id
      AND relation_type = NEW.relation_type
      AND publication_import_unit_id IS NULL;
END;

-- Surface read indexes.
-- Indexes for Gallery, Home, Profile, and Media Detail reads.

-- Published Profile recency.
CREATE INDEX ix_profiles_published_updated
    ON profiles (updated_at_ms DESC, profile_id DESC)
    WHERE trashed_at_ms IS NULL AND visibility = 'PUBLISHED';

-- Published rating order.
CREATE INDEX ix_profiles_published_rating
    ON profiles (rating DESC, updated_at_ms DESC, profile_id DESC)
    WHERE trashed_at_ms IS NULL AND visibility = 'PUBLISHED';

-- Published Profile-to-Media relations.
CREATE INDEX ix_profile_media_public_profile_relation
    ON profile_media (profile_id, relation_type, media_id)
    WHERE publication_import_unit_id IS NULL;

-- Published Media-to-Profile relations.
CREATE INDEX ix_profile_media_public_media_relation
    ON profile_media (media_id, relation_type, profile_id)
    WHERE publication_import_unit_id IS NULL;

-- Import durability.
CREATE TABLE import_media_interests (
    import_unit_id   TEXT NOT NULL REFERENCES import_units(import_unit_id) ON DELETE CASCADE,
    media_id         TEXT NOT NULL REFERENCES media(media_id) ON DELETE CASCADE,
    desired_priority INTEGER NOT NULL DEFAULT 50 CHECK (desired_priority BETWEEN 0 AND 100),
    created_at_ms    INTEGER NOT NULL,
    updated_at_ms    INTEGER NOT NULL,
    PRIMARY KEY (import_unit_id, media_id)
);

CREATE INDEX ix_import_media_interests_asset
ON import_media_interests(media_id, import_unit_id);

-- Shared rollback reservation.

-- Once a Media Trash entry is PENDING/EXECUTING, new consumers may not attach to that Media.
-- Existing consumers are revalidated by cancellation rollback before physical movement.

CREATE TRIGGER trg_import_interest_blocks_active_media_trash
BEFORE INSERT ON import_media_interests
WHEN EXISTS (
    SELECT 1
    FROM trash_entries te
    WHERE te.entity_type = 'MEDIA'
      AND te.entity_id = NEW.media_id
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'media with an active Trash reservation cannot acquire import interest');
END;

CREATE TRIGGER trg_import_reuse_blocks_active_media_trash
BEFORE UPDATE OF reused_media_id ON import_items
WHEN NEW.reused_media_id IS NOT NULL
 AND EXISTS (
    SELECT 1
    FROM trash_entries te
    WHERE te.entity_type = 'MEDIA'
      AND te.entity_id = NEW.reused_media_id
      AND te.state IN ('PENDING','EXECUTING')
 )
BEGIN
    SELECT RAISE(ABORT, 'media with an active Trash reservation cannot be selected for REUSE');
END;

CREATE TRIGGER trg_profile_relation_blocks_active_media_trash
BEFORE INSERT ON profile_media
WHEN EXISTS (
    SELECT 1
    FROM trash_entries te
    WHERE te.entity_type = 'MEDIA'
      AND te.entity_id = NEW.media_id
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'media with an active Trash reservation cannot acquire a Profile relation');
END;

-- Cancellation media reservation.

-- A cancellation obtains this reservation only after proving the Media has no competing live
-- import consumer or published/external Profile relation. The reservation then closes the TOCTOU
-- window until rollback_settled releases it.

CREATE TABLE import_cancel_media_reservations (
    import_unit_id TEXT NOT NULL REFERENCES import_units(import_unit_id) ON DELETE CASCADE,
    media_id       TEXT NOT NULL REFERENCES media(media_id) ON DELETE CASCADE,
    created_at_ms  INTEGER NOT NULL,
    PRIMARY KEY (import_unit_id, media_id)
);

CREATE UNIQUE INDEX ux_import_cancel_media_reservations_asset
ON import_cancel_media_reservations(media_id);

CREATE TRIGGER trg_reserved_media_blocks_import_interest
BEFORE INSERT ON import_media_interests
WHEN EXISTS (
    SELECT 1
    FROM import_cancel_media_reservations reservation
    WHERE reservation.media_id = NEW.media_id
)
BEGIN
    SELECT RAISE(ABORT, 'media reserved for cancellation rollback cannot acquire import interest');
END;

CREATE TRIGGER trg_reserved_media_blocks_import_reuse_insert
BEFORE INSERT ON import_items
WHEN NEW.reused_media_id IS NOT NULL
 AND EXISTS (
    SELECT 1
    FROM import_cancel_media_reservations reservation
    WHERE reservation.media_id = NEW.reused_media_id
 )
BEGIN
    SELECT RAISE(ABORT, 'media reserved for cancellation rollback cannot be selected for REUSE');
END;

CREATE TRIGGER trg_reserved_media_blocks_import_reuse_update
BEFORE UPDATE OF reused_media_id ON import_items
WHEN NEW.reused_media_id IS NOT NULL
 AND EXISTS (
    SELECT 1
    FROM import_cancel_media_reservations reservation
    WHERE reservation.media_id = NEW.reused_media_id
 )
BEGIN
    SELECT RAISE(ABORT, 'media reserved for cancellation rollback cannot be selected for REUSE');
END;

CREATE TRIGGER trg_reserved_media_blocks_profile_relation
BEFORE INSERT ON profile_media
WHEN EXISTS (
    SELECT 1
    FROM import_cancel_media_reservations reservation
    WHERE reservation.media_id = NEW.media_id
)
BEGIN
    SELECT RAISE(ABORT, 'media reserved for cancellation rollback cannot acquire a Profile relation');
END;

-- Trash/Profile lifecycle.

-- Freeze mutable graph edges while Trash/Purge owns their lifecycle so the persisted snapshot
-- cannot be invalidated between physical work and the terminal database transition.

CREATE TRIGGER trg_profile_relation_update_blocks_active_media_trash
BEFORE UPDATE ON profile_media
WHEN EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'MEDIA'
      AND te.entity_id IN (OLD.media_id, NEW.media_id)
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'media with an active Trash reservation cannot mutate Profile relations');
END;

CREATE TRIGGER trg_profile_appearance_source_insert_blocks_active_media_trash
BEFORE INSERT ON profiles
WHEN NEW.cover_media_id IS NOT NULL
 AND EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'MEDIA'
      AND te.entity_id = NEW.cover_media_id
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'media with an active Trash reservation cannot acquire Cover source references');
END;

CREATE TRIGGER trg_profile_appearance_source_update_blocks_active_media_trash
BEFORE UPDATE OF cover_media_id ON profiles
WHEN NEW.cover_media_id IS NOT OLD.cover_media_id
 AND NEW.cover_media_id IS NOT NULL
 AND EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'MEDIA'
      AND te.entity_id = NEW.cover_media_id
      AND te.state IN ('PENDING','EXECUTING')
 )
BEGIN
    SELECT RAISE(ABORT, 'media with an active Trash reservation cannot acquire Cover source references');
END;

CREATE TRIGGER trg_profile_relation_blocks_active_profile_trash
BEFORE INSERT ON profile_media
WHEN EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'PROFILE'
      AND te.entity_id = NEW.profile_id
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'a Profile with active Trash lifecycle cannot acquire relations');
END;

CREATE TRIGGER trg_profile_relation_update_blocks_active_profile_trash
BEFORE UPDATE ON profile_media
WHEN EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'PROFILE'
      AND te.entity_id = NEW.profile_id
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'a Profile with active Trash lifecycle cannot retain or acquire mutated relations');
END;

CREATE TRIGGER trg_identity_insert_blocks_active_profile_trash
BEFORE INSERT ON identities
WHEN NEW.is_active = 1
 AND EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'PROFILE'
      AND te.entity_id = NEW.profile_id
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'a Profile with active Trash lifecycle cannot acquire an active identity');
END;

CREATE TRIGGER trg_identity_update_blocks_active_profile_trash
BEFORE UPDATE OF profile_id, is_active ON identities
WHEN NEW.is_active = 1
 AND EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'PROFILE'
      AND te.entity_id = NEW.profile_id
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'a Profile with active Trash lifecycle cannot acquire an active identity');
END;

CREATE TRIGGER trg_assignment_cluster_insert_blocks_active_profile_purge
BEFORE INSERT ON import_assignment_clusters
WHEN EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'PURGE_PROFILE'
      AND te.entity_id IN (NEW.candidate_profile_id, NEW.decided_profile_id)
      AND te.state IN ('PURGE_PENDING','PURGE_EXECUTING','PURGE_RETRY_REQUIRED')
)
BEGIN
    SELECT RAISE(ABORT, 'a Profile with active Purge authorization cannot acquire assignment references');
END;

CREATE TRIGGER trg_assignment_cluster_update_blocks_active_profile_purge
BEFORE UPDATE OF candidate_profile_id, decided_profile_id ON import_assignment_clusters
WHEN EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'PURGE_PROFILE'
      AND te.entity_id IN (NEW.candidate_profile_id, NEW.decided_profile_id)
      AND te.state IN ('PURGE_PENDING','PURGE_EXECUTING','PURGE_RETRY_REQUIRED')
)
BEGIN
    SELECT RAISE(ABORT, 'a Profile with active Purge authorization cannot acquire assignment references');
END;

-- Restore/Purge exclusion.
-- Profile Restore and Profile Purge are mutually exclusive.
-- One durable lifecycle must win before either operation can touch recovery material.

CREATE TRIGGER trg_profile_purge_insert_blocks_active_restore
BEFORE INSERT ON trash_entries
WHEN NEW.entity_type = 'PURGE_PROFILE'
 AND NEW.state IN ('PURGE_PENDING','PURGE_EXECUTING','PURGE_RETRY_REQUIRED')
 AND EXISTS (
    SELECT 1
    FROM trash_entries source
    WHERE source.entity_type = 'PROFILE'
      AND source.entity_id = NEW.entity_id
      AND source.state IN ('RESTORE_EXECUTING','RESTORE_FINALIZING')
 )
BEGIN
    SELECT RAISE(ABORT, 'Profile Purge cannot start while Restore is active');
END;

CREATE TRIGGER trg_profile_purge_update_blocks_active_restore
BEFORE UPDATE OF state ON trash_entries
WHEN NEW.entity_type = 'PURGE_PROFILE'
 AND NEW.state IN ('PURGE_PENDING','PURGE_EXECUTING','PURGE_RETRY_REQUIRED')
 AND EXISTS (
    SELECT 1
    FROM trash_entries source
    WHERE source.entity_type = 'PROFILE'
      AND source.entity_id = NEW.entity_id
      AND source.state IN ('RESTORE_EXECUTING','RESTORE_FINALIZING')
 )
BEGIN
    SELECT RAISE(ABORT, 'Profile Purge cannot advance while Restore is active');
END;

CREATE TRIGGER trg_profile_restore_update_blocks_active_purge
BEFORE UPDATE OF state ON trash_entries
WHEN NEW.entity_type = 'PROFILE'
 AND NEW.state IN ('RESTORE_EXECUTING','RESTORE_FINALIZING')
 AND EXISTS (
    SELECT 1
    FROM trash_entries purge
    WHERE purge.entity_type = 'PURGE_PROFILE'
      AND purge.entity_id = NEW.entity_id
      AND purge.state IN ('PURGE_PENDING','PURGE_EXECUTING','PURGE_RETRY_REQUIRED')
 )
BEGIN
    SELECT RAISE(ABORT, 'Profile Restore cannot start while Purge is active');
END;

-- Face embedding provenance.
CREATE TRIGGER trg_identity_samples_embedding_provenance_insert
BEFORE INSERT ON identity_samples
WHEN NEW.embedding_space_key NOT LIKE NEW.model_id || '|' || NEW.model_version || '|%'
BEGIN
    SELECT RAISE(ABORT, 'identity sample model provenance must match embedding_space_key');
END;

CREATE TRIGGER trg_identity_samples_embedding_provenance_update
BEFORE UPDATE OF embedding_space_key, model_id, model_version ON identity_samples
WHEN NEW.embedding_space_key NOT LIKE NEW.model_id || '|' || NEW.model_version || '|%'
BEGIN
    SELECT RAISE(ABORT, 'identity sample model provenance must match embedding_space_key');
END;

CREATE TRIGGER trg_media_asset_authority_insert
BEFORE INSERT ON media_assets
WHEN NOT EXISTS (
    SELECT 1 FROM media a
    WHERE a.media_id = NEW.media_id
      AND NEW.relative_path = 'media-assets/' || a.media_storage_token || '/'
          || CASE NEW.role WHEN 'THUMBNAIL' THEN 'thumbnail.webp' WHEN 'HOVER' THEN 'hover.mp4' WHEN 'MODEL_RENDER' THEN 'model-render.nfig' END
      AND (NEW.role <> 'HOVER' OR a.media_type = 'VIDEO')
      AND (NEW.role <> 'MODEL_RENDER' OR (a.media_type = 'MODEL' AND a.dependency_status = 'SELF_CONTAINED'
          AND lower(coalesce(a.current_managed_file_name, '')) LIKE '%.glb'))
)
BEGIN
    SELECT RAISE(ABORT, 'MediaAsset path or role is not owned by its Media');
END;

CREATE TRIGGER trg_media_asset_authority_update
BEFORE UPDATE OF media_id, role, relative_path ON media_assets
WHEN NOT EXISTS (
    SELECT 1 FROM media a
    WHERE a.media_id = NEW.media_id
      AND NEW.relative_path = 'media-assets/' || a.media_storage_token || '/'
          || CASE NEW.role WHEN 'THUMBNAIL' THEN 'thumbnail.webp' WHEN 'HOVER' THEN 'hover.mp4' WHEN 'MODEL_RENDER' THEN 'model-render.nfig' END
      AND (NEW.role <> 'HOVER' OR a.media_type = 'VIDEO')
      AND (NEW.role <> 'MODEL_RENDER' OR (a.media_type = 'MODEL' AND a.dependency_status = 'SELF_CONTAINED'
          AND lower(coalesce(a.current_managed_file_name, '')) LIKE '%.glb'))
)
BEGIN
    SELECT RAISE(ABORT, 'MediaAsset path or role is not owned by its Media');
END;

CREATE TRIGGER trg_profile_appearance_selection_insert
BEFORE INSERT ON profile_appearance
WHEN
    (NEW.cover_media_asset_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM media_assets a
        JOIN media source_media ON source_media.media_id = a.media_id
        JOIN profile_media relation ON relation.media_id = source_media.media_id
        WHERE a.media_asset_id = NEW.cover_media_asset_id
          AND a.role = 'THUMBNAIL' AND a.state = 'READY'
          AND source_media.state = 'ACTIVE' AND relation.profile_id = NEW.profile_id
          AND relation.publication_import_unit_id IS NULL))
    OR (NEW.banner_media_asset_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM media_assets a
        JOIN media source_media ON source_media.media_id = a.media_id
        JOIN profile_media relation ON relation.media_id = source_media.media_id
        WHERE a.media_asset_id = NEW.banner_media_asset_id
          AND a.role = 'HOVER' AND a.state = 'READY'
          AND source_media.media_type = 'VIDEO'
          AND source_media.state = 'ACTIVE' AND relation.profile_id = NEW.profile_id
          AND relation.publication_import_unit_id IS NULL))
BEGIN
    SELECT RAISE(ABORT, 'Profile appearance selection must reference a valid owned MediaAsset');
END;

CREATE TRIGGER trg_profile_appearance_selection_update
BEFORE UPDATE OF profile_id, cover_media_asset_id, banner_media_asset_id ON profile_appearance
WHEN
    (NEW.cover_media_asset_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM media_assets a
        JOIN media source_media ON source_media.media_id = a.media_id
        JOIN profile_media relation ON relation.media_id = source_media.media_id
        WHERE a.media_asset_id = NEW.cover_media_asset_id
          AND a.role = 'THUMBNAIL' AND a.state = 'READY'
          AND source_media.state = 'ACTIVE' AND relation.profile_id = NEW.profile_id
          AND relation.publication_import_unit_id IS NULL))
    OR (NEW.banner_media_asset_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM media_assets a
        JOIN media source_media ON source_media.media_id = a.media_id
        JOIN profile_media relation ON relation.media_id = source_media.media_id
        WHERE a.media_asset_id = NEW.banner_media_asset_id
          AND a.role = 'HOVER' AND a.state = 'READY'
          AND source_media.media_type = 'VIDEO'
          AND source_media.state = 'ACTIVE' AND relation.profile_id = NEW.profile_id
          AND relation.publication_import_unit_id IS NULL))
BEGIN
    SELECT RAISE(ABORT, 'Profile appearance selection must reference a valid owned MediaAsset');
END;

CREATE TRIGGER trg_profile_figure_source_insert
BEFORE INSERT ON profile_appearance
WHEN NEW.figure_media_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM media m JOIN profiles p ON p.profile_id = NEW.profile_id
    JOIN profile_media pm ON pm.profile_id = p.profile_id AND pm.media_id = m.media_id
    WHERE m.media_id = NEW.figure_media_id AND m.state = 'ACTIVE' AND m.trashed_at_ms IS NULL
      AND m.media_type = 'MODEL' AND m.sha256 IS NOT NULL AND m.dependency_status = 'SELF_CONTAINED'
      AND lower(coalesce(m.current_managed_file_name,m.original_file_name,'')) LIKE '%.glb'
      AND p.kind = 'NORMAL' AND p.visibility = 'PUBLISHED' AND p.trashed_at_ms IS NULL
      AND pm.publication_import_unit_id IS NULL
      AND NOT EXISTS (SELECT 1 FROM trash_entries te WHERE te.entity_type = 'MEDIA' AND te.entity_id = m.media_id AND te.state IN ('PENDING','EXECUTING')))
BEGIN
    SELECT RAISE(ABORT,'Figure source must be eligible public canonical Model media');
END;

CREATE TRIGGER trg_profile_figure_source_update
BEFORE UPDATE OF profile_id,figure_media_id ON profile_appearance
WHEN NEW.figure_media_id IS NOT NULL AND (NEW.figure_media_id IS NOT OLD.figure_media_id OR NEW.profile_id IS NOT OLD.profile_id)
 AND NOT EXISTS (
    SELECT 1 FROM media m JOIN profiles p ON p.profile_id = NEW.profile_id
    JOIN profile_media pm ON pm.profile_id = p.profile_id AND pm.media_id = m.media_id
    WHERE m.media_id = NEW.figure_media_id AND m.state = 'ACTIVE' AND m.trashed_at_ms IS NULL
      AND m.media_type = 'MODEL' AND m.sha256 IS NOT NULL AND m.dependency_status = 'SELF_CONTAINED'
      AND lower(coalesce(m.current_managed_file_name,m.original_file_name,'')) LIKE '%.glb'
      AND p.kind = 'NORMAL' AND p.visibility = 'PUBLISHED' AND p.trashed_at_ms IS NULL
      AND pm.publication_import_unit_id IS NULL
      AND NOT EXISTS (SELECT 1 FROM trash_entries te WHERE te.entity_type = 'MEDIA' AND te.entity_id = m.media_id AND te.state IN ('PENDING','EXECUTING')))
BEGIN
    SELECT RAISE(ABORT,'Figure source must be eligible public canonical Model media');
END;