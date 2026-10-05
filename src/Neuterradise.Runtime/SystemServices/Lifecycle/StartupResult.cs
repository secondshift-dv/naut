namespace Neuterradise.App.SystemServices.Lifecycle;

public sealed record StartupResult(
    StartupState State,
    BootstrapContext? Context,
    string? ErrorCode,
    Exception? Exception,
    StartupTimingReport Timings)
{
    public bool IsReady => State == StartupState.Ready && Context is not null;

    public bool PreservesWritableAuthority => State == StartupState.StartupFailed && Context is not null;

    internal static StartupResult Ready(BootstrapContext context, StartupTimingReport timings) =>
        new(StartupState.Ready, context, ErrorCode: null, Exception: null, timings);

    internal static StartupResult Failed(
        string errorCode,
        Exception exception,
        StartupTimingReport timings) =>
        new(StartupState.StartupFailed, Context: null, errorCode, exception, timings);

    internal static StartupResult FailedPreservingAuthority(
        string errorCode,
        Exception exception,
        BootstrapContext context,
        StartupTimingReport timings) =>
        new(StartupState.StartupFailed, context, errorCode, exception, timings);
}

public enum StartupStage
{
    ProcessStartToRootResolved,
    RootResolvedToDbOpened,
    DbOpenedToSchemaReady,
    SchemaReadyToRecoveryComplete,
    RecoveryCompleteToCriticalGateComplete,
    CriticalGateCompleteToPrewarmComplete,
    PrewarmCompleteToMainWindowShown,
    CriticalGateCompleteToMainWindowShown,
}

public sealed record StartupStageTiming(StartupStage Stage, TimeSpan Elapsed);

public sealed record StartupStageChangedEventArgs(StartupStage Stage, StartupState State, TimeSpan Elapsed);

public sealed record StartupTimingReport(IReadOnlyList<StartupStageTiming> Stages)
{
    public static StartupTimingReport Empty { get; } = new([]);

    public TimeSpan TotalCriticalStartup =>
        TimeSpan.FromTicks(Stages.Sum(stage => stage.Elapsed.Ticks));
}
