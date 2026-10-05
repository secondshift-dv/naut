using System.Text.Json;
using System.Text.Json.Serialization;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Trash;

public static class TrashEntryState
{

    public const string Pending = "PENDING";

    public const string Executing = "EXECUTING";

    public const string InTrash = "IN_TRASH";

    public const string RestoreExecuting = "RESTORE_EXECUTING";

    public const string RestoreFinalizing = "RESTORE_FINALIZING";

    public const string Restored = "RESTORED";
}

public static class TrashEntityType
{
    public const string Media = "MEDIA";

    public const string Profile = "PROFILE";
}

public sealed record MediaTrashComponentPlan(
    string ComponentRelativePath,
    string SourceRelativePath,
    string RecoveryRelativePath,
    long ByteLength,
    string Sha256,
    ComponentRole Role);

public sealed record DurableMediaAssetTrashPlan(
    string SourceRelativePath,
    string RecoveryRelativePath,
    long ByteLength,
    string Sha256,
    [property: JsonRequired] MediaAssetRole Role)
{
    public bool RepairOnRestore { get; init; }
    public bool HasValidPaths(Guid mediaId, string storageToken, MediaType mediaType)
    {
        if (!Enum.IsDefined(Role) || ByteLength <= 0 || Sha256 is not {Length:64}
            || Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || Role == MediaAssetRole.Hover && mediaType != MediaType.Video
            || Role == MediaAssetRole.ModelRender && mediaType != MediaType.Model) return false;
        try
        {
            var expected = new ManagedPathPlanner().PlanMediaAsset(new MediaStorageToken(storageToken), Role);
            var name = expected[(expected.LastIndexOf('/')+1)..];
            return SourceRelativePath == expected
                && RecoveryRelativePath == $"_trash/media/{mediaId:D}/media-assets/{name}";
        }
        catch (ArgumentException) { return false; }
    }
}

public sealed record ProfileMediaTrashReference(
    Guid ProfileId, Guid? CoverMediaAssetId, Guid? BannerMediaAssetId,
    long AppearanceRowVersion, Guid? FigureMediaId = null);

public sealed record MediaTrashPlan(
    Guid TrashEntryId,
    Guid MediaId,
    Guid OwnerProfileId,
    string CurrentManagedRelativePath,
    string CurrentManagedFileName,
    string MediaStorageToken,
    long ByteLength,
    string Sha256,
    MediaType MediaType,
    string RecoveryRelativePath,
    IReadOnlyList<AffectedAppearanceReference> AffectedAppearanceReferences,
    Guid OperationId,
    DateTimeOffset PreparedAtUtc,
    long ExpectedMediaRowVersion,
    IReadOnlyList<MediaTrashComponentPlan>? PackageComponents = null)
{

    public const int SchemaVersion = 1;

    [JsonInclude]
    public int Version { get; init; } = SchemaVersion;

    public MediaRestoreCheckpoint? RestoreCheckpoint { get; init; }

    public IReadOnlyList<MediaProfileRelationSnapshot> RelationSnapshots { get; init; } = [];

    public IReadOnlyList<DurableMediaAssetTrashPlan> MediaAssets { get; init; } = [];

    public IReadOnlyList<ProfileMediaTrashReference> SelectedMediaReferences { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, TrashPlanJson.Options);

    public static MediaTrashPlan? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var plan = JsonSerializer.Deserialize<MediaTrashPlan>(json, TrashPlanJson.Options);
            return plan is null || plan.Version != SchemaVersion || plan.MediaAssets is null
                || plan.MediaAssets.Any(a => a is null || !a.HasValidPaths(plan.MediaId,plan.MediaStorageToken,plan.MediaType))
                || plan.MediaAssets.Select(a => a.Role).Distinct().Count() != plan.MediaAssets.Count ? null : plan;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public bool Equals(MediaTrashPlan? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
            && Version == other.Version
            && TrashEntryId == other.TrashEntryId
            && MediaId == other.MediaId
            && OwnerProfileId == other.OwnerProfileId
            && CurrentManagedRelativePath == other.CurrentManagedRelativePath
            && CurrentManagedFileName == other.CurrentManagedFileName
            && MediaStorageToken == other.MediaStorageToken
            && ByteLength == other.ByteLength
            && Sha256 == other.Sha256
            && MediaType == other.MediaType
            && RecoveryRelativePath == other.RecoveryRelativePath
            && OperationId == other.OperationId
            && PreparedAtUtc == other.PreparedAtUtc
            && ExpectedMediaRowVersion == other.ExpectedMediaRowVersion
            && RestoreCheckpoint == other.RestoreCheckpoint
            && AffectedAppearanceReferences.SequenceEqual(other.AffectedAppearanceReferences)
            && RelationSnapshots.SequenceEqual(other.RelationSnapshots)
            && MediaAssets.SequenceEqual(other.MediaAssets)
            && SelectedMediaReferences.SequenceEqual(other.SelectedMediaReferences)
            && ((PackageComponents is null && other.PackageComponents is null)
                || (PackageComponents is not null && other.PackageComponents is not null && PackageComponents.SequenceEqual(other.PackageComponents)));
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        hash.Add(TrashEntryId);
        hash.Add(MediaId);
        hash.Add(OwnerProfileId);
        hash.Add(MediaStorageToken);
        hash.Add(Sha256);
        hash.Add(RecoveryRelativePath);
        hash.Add(OperationId);
        hash.Add(ExpectedMediaRowVersion);
        hash.Add(RestoreCheckpoint);

        foreach (var reference in AffectedAppearanceReferences)
        {
            hash.Add(reference);
        }

        foreach (var relation in RelationSnapshots)
        {
            hash.Add(relation);
        }

        foreach (var mediaAsset in MediaAssets)
        {
            hash.Add(mediaAsset);
        }
        foreach (var reference in SelectedMediaReferences)
        {
            hash.Add(reference);
        }

        if (PackageComponents is not null)
        {
            foreach (var comp in PackageComponents)
            {
                hash.Add(comp);
            }
        }

        return hash.ToHashCode();
    }
}

public sealed record MediaRestoreCheckpoint(
    Guid OwnerProfileId,
    VaultPathArea SourceArea,
    string SourceRelativePath,
    string TargetManagedRelativePath,
    string TargetManagedFileName);

public sealed record AffectedAppearanceReference(
    Guid ProfileId,
    bool IsCover,
    bool IsBanner,
    bool IsFigure = false);

public sealed record MediaProfileRelationSnapshot(
    Guid ProfileId,
    string RelationType,
    string? ProvenanceKey,
    Guid? PublicationImportUnitId,
    long CreatedAtMilliseconds);

public sealed record ProfileRelationSnapshot(
    Guid MediaId,
    string RelationType,
    string? ProvenanceKey,
    Guid? PublicationImportUnitId,
    long CreatedAtMilliseconds);

public sealed record ProfileIdentitySnapshot(
    Guid IdentityId,
    long RowVersion,
    long? RetiredAtMilliseconds);

public sealed record ProfileTrashCheckpoint(
    string RecoveryRelativePath);

public sealed record ProfileRestoreCheckpoint(
    string RecoveryRelativePath,
    string TargetManagedRelativePath);

public sealed record ProfileOwnedMediaSnapshot(
    Guid MediaId,
    long RowVersion);

public enum ProfileOwnedMediaDispositionKind
{

    TrashMedia,

    ChangeOwner,
}

public sealed record ProfileOwnedMediaDisposition(
    Guid MediaId,
    ProfileOwnedMediaDispositionKind Kind,
    Guid? NewOwnerProfileId = null);

public sealed record ProfileOwnerTransferReceipt(
    Guid MediaId,
    Guid AssignedOwnerProfileId,
    long OwnerRelationCreatedAtMilliseconds);

public sealed record ProfileTrashPlan(
    Guid TrashEntryId,
    Guid ProfileId,
    long ExpectedProfileRowVersion,
    IReadOnlyList<ProfileOwnedMediaSnapshot> OwnedActiveMedias,
    Guid OperationId,
    DateTimeOffset PreparedAtUtc)
{
    public const int SchemaVersion = 1;

    [JsonInclude]
    public int Version { get; init; } = SchemaVersion;

    public Guid? ExpectedCoverMediaId { get; init; }

    public Guid? ExpectedBannerMediaId { get; init; }
    public Guid? ExpectedFigureMediaId { get; init; }
    public long? ExpectedAppearanceRowVersion { get; init; }

    public IReadOnlyList<ProfileOwnedMediaDisposition> SelectedDispositions { get; init; } = [];

    public IReadOnlyList<ProfileOwnerTransferReceipt> OwnerTransferReceipts { get; init; } = [];

    public IReadOnlyList<ProfileRelationSnapshot> RelationSnapshots { get; init; } = [];

    public string? CurrentManagedRelativePath { get; init; }

    public ProfileIdentitySnapshot? ActiveIdentitySnapshot { get; init; }

    public ProfileTrashCheckpoint? TrashCheckpoint { get; init; }

    public ProfileRestoreCheckpoint? RestoreCheckpoint { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, TrashPlanJson.Options);

    public static ProfileTrashPlan? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var plan = JsonSerializer.Deserialize<ProfileTrashPlan>(json, TrashPlanJson.Options);
            return plan is null || plan.Version != SchemaVersion ? null : plan;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public bool Equals(ProfileTrashPlan? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
            && Version == other.Version
            && TrashEntryId == other.TrashEntryId
            && ProfileId == other.ProfileId
            && ExpectedProfileRowVersion == other.ExpectedProfileRowVersion
            && ExpectedCoverMediaId == other.ExpectedCoverMediaId
            && ExpectedBannerMediaId == other.ExpectedBannerMediaId
            && ExpectedFigureMediaId == other.ExpectedFigureMediaId
            && ExpectedAppearanceRowVersion == other.ExpectedAppearanceRowVersion
            && OperationId == other.OperationId
            && PreparedAtUtc == other.PreparedAtUtc
            && ActiveIdentitySnapshot == other.ActiveIdentitySnapshot
            && TrashCheckpoint == other.TrashCheckpoint
            && RestoreCheckpoint == other.RestoreCheckpoint
            && OwnedActiveMedias.SequenceEqual(other.OwnedActiveMedias)
            && SelectedDispositions.SequenceEqual(other.SelectedDispositions)
            && OwnerTransferReceipts.SequenceEqual(other.OwnerTransferReceipts)
            && RelationSnapshots.SequenceEqual(other.RelationSnapshots)
            && CurrentManagedRelativePath == other.CurrentManagedRelativePath;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        hash.Add(TrashEntryId);
        hash.Add(ProfileId);
        hash.Add(ExpectedProfileRowVersion);
        hash.Add(ExpectedCoverMediaId);
        hash.Add(ExpectedBannerMediaId);
        hash.Add(CurrentManagedRelativePath);
        hash.Add(OperationId);

        hash.Add(ActiveIdentitySnapshot);
        hash.Add(TrashCheckpoint);
        hash.Add(RestoreCheckpoint);

        foreach (var asset in OwnedActiveMedias)
        {
            hash.Add(asset);
        }

        foreach (var disposition in SelectedDispositions)
        {
            hash.Add(disposition);
        }

        foreach (var receipt in OwnerTransferReceipts)
        {
            hash.Add(receipt);
        }

        foreach (var relation in RelationSnapshots)
        {
            hash.Add(relation);
        }

        return hash.ToHashCode();
    }
}

internal static class TrashPlanJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
