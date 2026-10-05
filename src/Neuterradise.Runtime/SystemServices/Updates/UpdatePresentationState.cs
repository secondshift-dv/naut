namespace Neuterradise.App.SystemServices.Updates;

public sealed record UpdatePresentationState(
    string Status,
    string? Error,
    bool IsDownloading,
    double? Progress,
    bool RestartRequired,
    string? CandidateVersion)
{
    public static UpdatePresentationState Idle { get; } =
        new(UpdateCoordinator.StatusNotChecked, null, false, null, false, null);
}
