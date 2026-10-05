using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neuterradise.App.Import.Verification;

public enum DestinationKind
{
    NewNormal,
    ExistingNormal,
    SystemUnknown
}

/// <summary>
/// Three-state intent for Cover/Banner appearance on an existing Profile: unchanged, set, or clear.
/// </summary>
public enum VerificationAppearanceIntent
{
    Unchanged,
    Set,
    Clear
}

/// <summary>
/// Everything the user chose for a brand-new Profile while importing. the product contract requires all of it
/// to survive commit, so category and tags carry the catalog's own string identifiers rather than the
/// GUIDs an earlier draft assumed -- that mismatch is why they could never be filled in.
/// </summary>
public sealed record NewProfileDraft(
    string DisplayName,
    string? CategoryId = null,
    IReadOnlyList<string>? TagIds = null,
    int? Rating = null,
    bool? Favorite = null,
    string? Overview = null);

public sealed record VerificationDestinationDraft(
    DestinationKind? Kind,
    Guid? ProfileId,
    NewProfileDraft? NewProfile);

public sealed record VerificationAppearanceDraft(
    Guid? CoverMediaId,
    Guid? BannerMediaId,
    string? CoverSourceKind = null,
    Guid? CoverImportItemId = null,
    Guid? BannerImportItemId = null,
    long? CoverVideoTimestampMilliseconds = null,
    VerificationAppearanceIntent CoverIntent = VerificationAppearanceIntent.Unchanged,
    VerificationAppearanceIntent BannerIntent = VerificationAppearanceIntent.Unchanged,
    Guid? CoverMediaAssetId = null,
    Guid? BannerMediaAssetId = null);

public enum VerificationFaceDecision
{
    Confirm,
    Reject
}

public enum VerificationFaceTargetKind
{

    Destination,

    ExistingProfile
}

public sealed record StagedFaceDecision(
    Guid FaceId,
    long ExpectedFaceRowVersion,
    VerificationFaceDecision Decision,
    VerificationFaceTargetKind? TargetKind = null,
    Guid? TargetProfileId = null,
    Guid? SourceImportItemId = null,
    Guid? CandidateMediaId = null,
    string? CandidateModelId = null,
    string? CandidateModelVersion = null);

public enum DuplicateDecisionAction
{
    Reuse, // Continue in destination
    Skip   // Remove from this import
}

public sealed record StagedDuplicateDecision(
    Guid ImportItemId,
    DuplicateDecisionAction Action);

public enum ProfileCollisionAction
{
    KeepDestination, // Continue in chosen destination
    MoveToProfile,   // Move to candidate profile
    Skip             // Cancel this media
}

public sealed record StagedProfileCollisionDecision(
    Guid ImportItemId,
    ProfileCollisionAction Action,
    Guid? TargetProfileId);

public sealed record VerificationDraftV1(
    int SchemaVersion,
    int CurrentStep,
    VerificationDestinationDraft Destination,
    VerificationAppearanceDraft Appearance,
    IReadOnlyList<StagedFaceDecision> FaceDecisions,
    long UpdatedAtMs,
    IReadOnlyList<Guid>? AcknowledgedMissingDependencyItemIds = null,
    bool? ImportRequested = null,
    IReadOnlyList<Guid>? AttentionItemIds = null,
    IReadOnlyList<StagedDuplicateDecision>? DuplicateDecisions = null,
    IReadOnlyList<StagedProfileCollisionDecision>? ProfileCollisionDecisions = null)
{
    /// <summary>
    /// True once the user pressed Import in the two-step overlay. It is the durable record of intent:
    /// <see cref="Neuterradise.App.Import.ImportFinalizer"/> finishes the import when preparation
    /// allows, including after an application restart. Older drafts simply omit it.
    /// </summary>
    public bool IsImportRequested => ImportRequested == true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>canonical draft schema. Older persisted drafts remain readable and are migrated in memory.</summary>
    public const int CurrentSchemaVersion = 5;

    /// <summary>
    /// Validates the destination discriminated union before a draft can enter commit. A destination
    /// is never inferred from a profile id or from UI labels.
    /// </summary>
    public IReadOnlyList<string> ValidateDestination()
    {
        var errors = new List<string>();
        switch (Destination.Kind)
        {
            case DestinationKind.NewNormal:
                if (Destination.ProfileId is not null)
                    errors.Add("NEW_NORMAL_MUST_NOT_HAVE_PROFILE_ID");
                if (Destination.NewProfile is null || string.IsNullOrWhiteSpace(Destination.NewProfile.DisplayName))
                    errors.Add("NEW_NORMAL_DISPLAY_NAME_REQUIRED");
                break;
            case DestinationKind.ExistingNormal:
                if (!Destination.ProfileId.HasValue || Destination.ProfileId.Value == Guid.Empty)
                    errors.Add("EXISTING_NORMAL_PROFILE_ID_REQUIRED");
                if (Destination.NewProfile is not null)
                    errors.Add("EXISTING_NORMAL_MUST_NOT_HAVE_NEW_PROFILE");
                break;
            case DestinationKind.SystemUnknown:
                if (Destination.ProfileId is not null || Destination.NewProfile is not null)
                    errors.Add("SYSTEM_UNKNOWN_MUST_NOT_HAVE_PROFILE_DATA");
                break;
            default:
                errors.Add("DESTINATION_REQUIRED");
                break;
        }
        return errors;
    }

    /// <summary>
    /// A fresh draft. The caller supplies the instant from the centralized clock; there is no ambient
    /// system-clock fallback, so a draft timestamp can never come from an uncontrolled source.
    /// </summary>
    public static VerificationDraftV1 CreateDefault(long updatedAtMs) =>
        new(
            SchemaVersion: CurrentSchemaVersion,
            CurrentStep: 1,
            Destination: new VerificationDestinationDraft(null, null, null),
            Appearance: new VerificationAppearanceDraft(null, null, null),
            FaceDecisions: [],
            UpdatedAtMs: updatedAtMs,
            AcknowledgedMissingDependencyItemIds: []);

    public string ToJson() =>
        JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Reads a persisted draft. <paramref name="fallbackUpdatedAtMs"/> is used only when there is no
    /// stored draft yet; an unreadable draft is never silently reset (see the throw below).
    /// </summary>
    public static VerificationDraftV1 FromJson(string? json, long fallbackUpdatedAtMs)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json.Trim(), "{}", StringComparison.Ordinal))
        {
            return CreateDefault(fallbackUpdatedAtMs);
        }

        try
        {
            var draft = JsonSerializer.Deserialize<VerificationDraftV1>(json, JsonOptions);
            if (draft is null)
            {
                return CreateDefault(fallbackUpdatedAtMs);
            }

            if (draft.SchemaVersion is < 1 or > CurrentSchemaVersion)
            {
                throw new InvalidOperationException($"Unsupported VerificationDraft schema version: {draft.SchemaVersion}");
            }

            // v1/v2 drafts have the same durable destination contract; normalize them to v5
            // without inventing destination data or silently accepting malformed unions.
            var clampedStep = Math.Clamp(draft.CurrentStep, 1, 5);
            var destination = draft.Destination ?? new VerificationDestinationDraft(null, null, null);
            var appearance = draft.Appearance ?? new VerificationAppearanceDraft(null, null, null);
            var faceDecisions = draft.FaceDecisions ?? (IReadOnlyList<StagedFaceDecision>)[];
            var acknowledged = draft.AcknowledgedMissingDependencyItemIds ?? (IReadOnlyList<Guid>)[];
            var duplicateDecisions = draft.DuplicateDecisions ?? (IReadOnlyList<StagedDuplicateDecision>)[];
            var collisionDecisions = draft.ProfileCollisionDecisions ?? (IReadOnlyList<StagedProfileCollisionDecision>)[];

            // Legacy schema versions encoded appearance intent through nullable asset ids; normalize them here.
            // Legacy null = Unchanged (NOT Clear, since old drafts had no Clear representation).
            // Legacy non-null = Set.
            if (draft.SchemaVersion < 5)
            {
                appearance = appearance with
                {
                    CoverIntent = appearance.CoverMediaId.HasValue
                        ? VerificationAppearanceIntent.Set
                        : VerificationAppearanceIntent.Unchanged,
                    BannerIntent = appearance.BannerMediaId.HasValue
                        ? VerificationAppearanceIntent.Set
                        : VerificationAppearanceIntent.Unchanged,
                };
            }

            draft = draft with
            {
                SchemaVersion = CurrentSchemaVersion,
                CurrentStep = clampedStep,
                Destination = destination,
                Appearance = appearance,
                FaceDecisions = faceDecisions,
                AcknowledgedMissingDependencyItemIds = acknowledged,
                DuplicateDecisions = duplicateDecisions,
                ProfileCollisionDecisions = collisionDecisions
            };

            return draft;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Failed to deserialize VerificationDraftV1 JSON.", ex);
        }
    }
}
