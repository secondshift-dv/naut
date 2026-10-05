namespace Neuterradise.Profiling.Protocol;

/// <summary>
/// Names the process-bound runtime authority values inherited by the Profiling Worker from the app.
/// The worker must not rediscover deployment roots from its working directory or executable location.
/// </summary>
public static class ProfilingRuntimeEnvironment
{
    public const string InstallRootEnvironmentVariable = "NEUTERRADISE_INSTALL_ROOT";
}
