using System.Runtime.CompilerServices;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.MediaTools;

/// <summary>
/// Resolves approved external tool executables for the current deployment. Resolution is manifest
/// driven and InstallRoot scoped: PATH, the working directory, the source tree and any remote
/// location are never consulted, and an unresolved tool becomes a truthful unavailable capability
/// instead of an unapproved executable.
/// </summary>
public sealed class ExternalToolResolver
{
    public const string FfprobeToolId = "ffprobe";

    public const string FfmpegToolId = "ffmpeg";

    private static readonly ConditionalWeakTable<InstallPaths, ExternalToolResolver> InstallResolvers = new();

    private readonly DeploymentArtifactCatalog? _catalog;
    private readonly string? _installRoot;
    private readonly Dictionary<string, RuntimeCapabilityStatus> _resolutions = new(StringComparer.Ordinal);
    private readonly object _syncLock = new();

    private ExternalToolResolver(DeploymentArtifactCatalog? catalog, string? installRoot)
    {
        _catalog = catalog;
        _installRoot = installRoot;
    }

    /// <summary>
    /// Returns the resolver owned by this InstallPaths authority. Production uses a process-singleton
    /// InstallPaths value, so manifest loading, executable hashing and tool capability resolution are
    /// performed once per process instead of once per feature or registry construction. Explicit
    /// InstallPaths instances retain independent caches for isolated callers.
    /// </summary>
    public static ExternalToolResolver ForInstall(InstallPaths install)
    {
        ArgumentNullException.ThrowIfNull(install);
        return InstallResolvers.GetValue(install, static authority => CreateForInstall(authority));
    }

    private static ExternalToolResolver CreateForInstall(InstallPaths install)
    {
        var catalog = DeploymentArtifactCatalog.TryLoad(install);
        return catalog is null
            ? new ExternalToolResolver(null, install.RuntimeRoot)
            : new ExternalToolResolver(catalog, install.RuntimeRoot);
    }

    /// <summary>
    /// Convenience factory for the running deployment.
    /// </summary>
    public static ExternalToolResolver ForProduction() => ForInstall(InstallPaths.CreateProduction());

    /// <summary>
    /// Returns the capability status for a tool id. Results are cached per InstallRoot authority because
    /// artifact verification hashes the executable and the deployment cannot change while the app runs.
    /// </summary>
    public RuntimeCapabilityStatus Resolve(string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);

        lock (_syncLock)
        {
            if (_resolutions.TryGetValue(toolId, out var cached))
            {
                return cached;
            }

            var resolution = ResolveCore(toolId);
            _resolutions[toolId] = resolution;
            return resolution;
        }
    }

    /// <summary>
    /// Returns the verified executable path, or null when the tool may not be launched.
    /// </summary>
    public string? TryResolveExecutablePath(string toolId)
    {
        var status = Resolve(toolId);
        return status.IsUsable ? status.ResolvedPath : null;
    }

    private RuntimeCapabilityStatus ResolveCore(string toolId)
    {
        if (_catalog is null || _installRoot is null)
        {
            return RuntimeCapabilityStatus.Unavailable(
                toolId,
                RuntimeCapabilityKind.Tool,
                $"This deployment has no readable artifact manifest, so the approved '{toolId}' executable cannot be resolved.");
        }

        return _catalog.DescribeTool(toolId, _installRoot);
    }
}
