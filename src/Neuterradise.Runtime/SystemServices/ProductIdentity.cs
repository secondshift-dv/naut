using System.Reflection;
using Neuterradise.Release.Contracts;

namespace Neuterradise.App.SystemServices;

/// <summary>
/// Canonical public identity for naut.
/// Persistent package/update identifiers remain deliberately legacy-compatible.
/// Candidate update versions remain manifest-owned and must not be replaced with these values.
/// </summary>
public static class ProductIdentity
{
    public const string DisplayName = "naut";
    public const string ProductSlug = "neuterradise";
    public const string ProductId = "neuterradise";
    public static string Version => typeof(ProductIdentity).Assembly
        .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "ProductVersion")?.Value
        ?? throw new InvalidOperationException("ProductVersion assembly metadata is missing.");
    public static string AssemblyVersion => typeof(ProductIdentity).Assembly.GetName().Version?.ToString()
        ?? throw new InvalidOperationException("ProductAssemblyVersion assembly metadata is missing.");
    public static string RuntimeId => typeof(ProductIdentity).Assembly
        .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "ProductRuntimeIdentifier")?.Value
        ?? throw new InvalidOperationException("ProductRuntimeIdentifier assembly metadata is missing.");
    public const string Channel = "stable";

    public const string AppExecutableName = ReleaseLayout.LauncherExecutableName;
    public const string RuntimeDirectoryName = ReleaseLayout.RuntimeDirectoryName;
    public const string ProfilingWorkerExecutableName = "NeuTerradise.Profiling.Worker.exe";
    public const string UpdaterExecutableName = "NeuTerradise.Updater.exe";
    public static string ZipArchiveName => $"naut-v{Version}-{RuntimeId}.zip";
}
