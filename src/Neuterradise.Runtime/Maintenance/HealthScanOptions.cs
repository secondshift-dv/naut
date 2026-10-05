namespace Neuterradise.App.Maintenance;

public enum HealthScanMode
{

    StartupCritical,

    Standard,

    Deep,

    Focused
}

public sealed record HealthScanProgress(
    string CurrentStage,
    int ItemsScanned,
    int? TotalItems,
    int FindingsCount);

public sealed record HealthScanOptions(
    HealthScanMode Mode = HealthScanMode.Standard,
    Guid? FocusedProfileId = null,
    Guid? FocusedMediaId = null,
    string? FocusedOperationId = null,
    int BatchSize = 100);
