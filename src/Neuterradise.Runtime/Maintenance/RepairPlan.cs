using System.Text.Json;
using System.Text.Json.Serialization;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.Import;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Maintenance;

public enum RepairKind
{
    RegenerateProfileManifest,
    RegenerateMediaAsset,
    RemoveRetiredProfileDirectory
}

public enum RepairPhysicalFact
{
    ManifestMissing,
    ManifestMalformed,
    ManifestStale,
    SourcePresent,
    ArtifactInvalid,
    EmptyDirectory
}

public sealed record RepairPlan(
    int SchemaVersion,
    Guid RepairPlanId,
    string FindingCode,
    RepairKind Kind,
    Guid? ProfileId,
    Guid? MediaId,
    Guid? ImportItemId,
    long ExpectedRowVersion,
    string ExpectedAuthorityState,
    string SourcePathOrRelative,
    string TargetPathOrRelative,
    string? ExpectedPhysicalSha256,
    long? ExpectedPhysicalByteLength,
    RepairPhysicalFact CurrentPhysicalFact,
    string IntendedTargetAction,
    bool IsMaterialOrDestructive,
    Guid OperationId,
    DateTimeOffset PreparedAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions _serializerOptions = CreateSerializerOptions();

    public string ToJson()
    {
        Validate();
        return JsonSerializer.Serialize(this, _serializerOptions);
    }

    public static RepairPlan? FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            var plan = JsonSerializer.Deserialize<RepairPlan>(json, _serializerOptions);
            plan?.Validate();
            return plan;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException($"Unsupported repair-plan schema {SchemaVersion}.");
        }

        if (RepairPlanId == Guid.Empty || OperationId == Guid.Empty)
        {
            throw new ArgumentException("Repair plans require stable non-empty identifiers.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(FindingCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExpectedAuthorityState);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourcePathOrRelative);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetPathOrRelative);
        ArgumentException.ThrowIfNullOrWhiteSpace(IntendedTargetAction);

        if (ExpectedRowVersion < 0 || ExpectedPhysicalByteLength < 0)
        {
            throw new ArgumentException("Repair-plan versions and byte lengths cannot be negative.");
        }
    }
}

public sealed record PersistedRepairOperation(
    RepairPlan Plan,
    string State,
    long RowVersion,
    string? ErrorCode,
    string? ErrorDetailSafe);
