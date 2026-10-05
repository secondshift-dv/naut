using System.IO;
using System.Threading;
using Neuterradise.Profiling.Protocol;
using Neuterradise.Release.Contracts;

namespace Neuterradise.Profiling.Worker;

/// <summary>
/// Process-lifetime deployment authority for the Profiling Worker. The trusted parent app supplies
/// InstallRoot explicitly; the worker never infers model locations from its working directory or
/// executable placement.
/// </summary>
internal sealed class WorkerRuntimeEnvironment
{
    private static readonly Lazy<WorkerRuntimeEnvironment> CurrentAuthority = new(
        CreateCurrent,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private WorkerRuntimeEnvironment(string installRoot)
    {
        InstallRoot = NormalizeRoot(installRoot);
        var nestedRuntimeRoot = Path.Combine(InstallRoot, ReleaseLayout.RuntimeDirectoryName);
        var legacyRuntimeRoot = Path.Combine(InstallRoot, ReleaseLayout.LegacyRuntimeDirectoryName);
        RuntimeRoot = Directory.Exists(nestedRuntimeRoot) ? NormalizeRoot(nestedRuntimeRoot)
            : Directory.Exists(legacyRuntimeRoot) ? NormalizeRoot(legacyRuntimeRoot) : InstallRoot;
        ModelsRoot = Path.Combine(
            RuntimeRoot,
            ReleaseContract.Current.ModelsRelativeRoot.Replace('/', Path.DirectorySeparatorChar));
    }

    public static WorkerRuntimeEnvironment Current => CurrentAuthority.Value;

    public string InstallRoot { get; }

    public string RuntimeRoot { get; }

    public string ModelsRoot { get; }

    public string ResolveModelPath(string modelId, string artifactFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactFileName);

        if (Path.IsPathRooted(modelId)
            || Path.IsPathRooted(artifactFileName)
            || modelId.Contains(Path.DirectorySeparatorChar)
            || modelId.Contains(Path.AltDirectorySeparatorChar)
            || artifactFileName.Contains(Path.DirectorySeparatorChar)
            || artifactFileName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Profiling model identifiers and artifact names must be single relative path segments.");
        }

        var resolved = Path.GetFullPath(Path.Combine(ModelsRoot, modelId, artifactFileName));
        var authorityRoot = Path.TrimEndingDirectorySeparator(ModelsRoot) + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(authorityRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved profiling model path escaped the approved models root.");
        }

        return resolved;
    }

    private static WorkerRuntimeEnvironment CreateCurrent()
    {
        var installRoot = Environment.GetEnvironmentVariable(
            ProfilingRuntimeEnvironment.InstallRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            throw new InvalidOperationException(
                "Profiling Worker install-root authority was not supplied by the host process.");
        }

        return new WorkerRuntimeEnvironment(installRoot);
    }

    private static string NormalizeRoot(string root)
    {
        var trimmed = root.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new InvalidOperationException(
                "Profiling Worker install-root authority must be a fully qualified path.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
    }
}
