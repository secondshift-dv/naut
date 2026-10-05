using System.Text.Json;
using System.Text.Json.Serialization;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Trash;

public static class PurgePlanState
{
    public const string Pending = "PURGE_PENDING";
    public const string Executing = "PURGE_EXECUTING";
    public const string RetryRequired = "PURGE_RETRY_REQUIRED";
    public const string Purged = "PURGED";
}

public static class PurgeEntityType
{
    public const string Media = "MEDIA";
    public const string Profile = "PROFILE";
}

internal static class PurgeOperationEntityType
{
    internal const string Media = "PURGE_MEDIA";
    internal const string Profile = "PURGE_PROFILE";
}

public enum PurgePhysicalTargetKind
{
    File,
    EmptyDirectory,
}

public sealed record PurgePhysicalTarget(
    VaultPathArea Area,
    string VaultRelativePath,
    PurgePhysicalTargetKind Kind,
    long? ExpectedByteLength = null,
    string? ExpectedSha256 = null);

public sealed record PurgeAffectedData(string Name, long RowCount);

public sealed record PurgeAuthorityVersion(string EntityType, Guid EntityId, long RowVersion);

public sealed record PurgePlan(
    Guid PurgePlanId,
    string EntityType,
    Guid EntityId,
    Guid TrashEntryId,
    IReadOnlyList<PurgePhysicalTarget> PhysicalDeletionTargets,
    IReadOnlyList<PurgeAffectedData> DependentDataAffected,
    IReadOnlyList<PurgeAuthorityVersion> PreparedAuthorityVersions,
    DateTimeOffset PreparedAtUtc,
    string WarningSummary,
    Guid OperationId)
{
    public const int SchemaVersion = 1;

    [JsonInclude]
    public int Version { get; init; } = SchemaVersion;

    public int CompletedPhysicalTargetCount { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, PurgePlanJson.Options);

    public static PurgePlan? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var plan = JsonSerializer.Deserialize<PurgePlan>(json, PurgePlanJson.Options);
            return plan is null || plan.Version != SchemaVersion || !plan.IsStructurallyValid()
                ? null
                : plan;
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

    public bool Equals(PurgePlan? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
            && Version == other.Version
            && PurgePlanId == other.PurgePlanId
            && EntityType == other.EntityType
            && EntityId == other.EntityId
            && TrashEntryId == other.TrashEntryId
            && PreparedAtUtc == other.PreparedAtUtc
            && WarningSummary == other.WarningSummary
            && OperationId == other.OperationId
            && CompletedPhysicalTargetCount == other.CompletedPhysicalTargetCount
            && PhysicalDeletionTargets.SequenceEqual(other.PhysicalDeletionTargets)
            && DependentDataAffected.SequenceEqual(other.DependentDataAffected)
            && PreparedAuthorityVersions.SequenceEqual(other.PreparedAuthorityVersions);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        hash.Add(PurgePlanId);
        hash.Add(EntityType);
        hash.Add(EntityId);
        hash.Add(TrashEntryId);
        hash.Add(OperationId);
        hash.Add(CompletedPhysicalTargetCount);
        foreach (var target in PhysicalDeletionTargets)
        {
            hash.Add(target);
        }
        foreach (var affected in DependentDataAffected)
        {
            hash.Add(affected);
        }
        foreach (var authority in PreparedAuthorityVersions)
        {
            hash.Add(authority);
        }
        return hash.ToHashCode();
    }

    private bool IsStructurallyValid() =>
        PurgePlanId != Guid.Empty
        && EntityId != Guid.Empty
        && TrashEntryId != Guid.Empty
        && OperationId != Guid.Empty
        && EntityType is PurgeEntityType.Media or PurgeEntityType.Profile
        && !string.IsNullOrWhiteSpace(WarningSummary)
        && PhysicalDeletionTargets is not null
        && DependentDataAffected is not null
        && PreparedAuthorityVersions is not null
        && CompletedPhysicalTargetCount >= 0
        && CompletedPhysicalTargetCount <= PhysicalDeletionTargets.Count;
}

internal static class PurgePlanJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
