namespace Neuterradise.App.SystemServices.Lifecycle;

public enum StartupState
{
    ProcessStart,
    Bootstrapping,
    OpeningStorage,
    InitializingDatabase,
    Recovering,
    Prewarming,
    Ready,
    ShuttingDown,
    Stopped,
    RecoveryRequired,
    StartupFailed
}
