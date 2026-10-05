namespace Neuterradise.App.Maintenance;

public enum HealthSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public sealed record HealthFinding(
    string Code,
    HealthSeverity Severity,
    Guid? ProfileId,
    Guid? MediaId,
    Guid? JobId,
    string? OperationId,
    string Summary,
    bool RepairAvailable,
    string? ManagedRelativePath = null);
