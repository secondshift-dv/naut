using Neuterradise.Profiling.Protocol;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Neuterradise.App.SystemServices.MediaTools;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Diagnostics;



public static class DiagnosticSeverity
{
    public const string Information = "information";
    public const string Warning = "warning";
    public const string Error = "error";

    public static bool IsKnown(string? severity) =>
        severity is Information or Warning or Error;
}

public sealed record DiagnosticEvent(
    DateTimeOffset TimestampUtc,
    string Severity,
    string Capability,
    string Code,
    string? SafeErrorDetail = null,
    Guid? ProfileId = null,
    Guid? MediaId = null,
    Guid? IdentityId = null,
    Guid? FaceId = null,
    Guid? ImportSessionId = null,
    Guid? ImportUnitId = null,
    Guid? ImportItemId = null,
    Guid? JobId = null,
    Guid? OperationId = null,
    int? Attempt = null,
    string? StateTransition = null,
    string? SourceCleanupState = null,
    string? ReconciliationState = null,
    string? SchemaVersion = null,
    string? AppVersion = null,
    string? WorkerVersion = null,
    string? ProtocolVersion = null,
    string? ModelVersion = null,

    string? StorageToken = null,
    string? LibraryCommitState = null,
    string? ManifestHealthState = null,
    string? EmbeddingSpaceKey = null,
    string? ToolVersion = null);

public sealed class StructuredDiagnostics : IDisposable
{

    public const long DefaultMaximumBytes = 4L * 1024 * 1024;

    public const int MaximumSafeErrorDetailLength = 512;

    public const string TruncationMarker = "[truncated]";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly Regex RootedPath = new(
        @"(?:[A-Za-z]:[\\/]|\\\\[^\\/\s]+[\\/]|(?<![^\s])/)(?:[^\s<>|]*[\\/])*(?<leaf>[^\s<>|\\/]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _writeGate = new();
    private bool _disposed;

    public StructuredDiagnostics(string logPath, long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumBytes, 0);

        LogPath = Path.GetFullPath(logPath);
        MaximumBytes = maximumBytes;
    }

    public string LogPath { get; }

    public long MaximumBytes { get; }

    public string RolledLogPath => LogPath + ".1";

    /// <summary>
    /// Structured diagnostics are application state, so they are written under AppStateRoot and never
    /// into the Vault or the extracted application directory.
    /// </summary>
    public static StructuredDiagnostics CreateProduction(AppStatePaths appState)
    {
        ArgumentNullException.ThrowIfNull(appState);
        appState.EnsureStructuralDirectories();
        return new StructuredDiagnostics(
            Path.Combine(appState.DiagnosticsPath, "diagnostics.jsonl"));
    }

    public void Write(DiagnosticEvent diagnosticEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticEvent.Capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticEvent.Code);

        if (!DiagnosticSeverity.IsKnown(diagnosticEvent.Severity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(diagnosticEvent),
                diagnosticEvent.Severity,
                "Severity must be one of the DiagnosticSeverity constants.");
        }

        var line = Serialize(diagnosticEvent);

        try
        {
            lock (_writeGate)
            {
                var directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RollIfNeeded(line.Length);
                File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception writeException)
        {

            Trace.TraceError(
                "{0} structured diagnostic write failed ({1}); dropped {2}/{3}.",
                ProductIdentity.DisplayName,
                writeException.GetType().Name,
                diagnosticEvent.Capability,
                diagnosticEvent.Code);
        }
    }

    public void WriteStateTransition(
        string capability,
        string code,
        string stateTransition,
        string severity = DiagnosticSeverity.Information,
        string? schemaVersion = null,
        string? appVersion = null,
        Guid? operationId = null,
        string? safeErrorDetail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateTransition);

        Write(new DiagnosticEvent(
            DateTimeOffset.UtcNow,
            severity,
            capability,
            code,
            SafeErrorDetail: safeErrorDetail,
            OperationId: operationId,
            StateTransition: stateTransition,
            SchemaVersion: schemaVersion,
            AppVersion: appVersion));
    }

    public static string Serialize(DiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        var safe = diagnosticEvent with
        {
            TimestampUtc = diagnosticEvent.TimestampUtc.ToUniversalTime(),
            SafeErrorDetail = SanitizeDetail(diagnosticEvent.SafeErrorDetail),
        };

        return JsonSerializer.Serialize(safe, SerializerOptions);
    }

    public static string? SanitizeDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var builder = new StringBuilder(detail.Length);
        foreach (var character in detail)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        var collapsed = RootedPath
            .Replace(
                builder.ToString(),
                match =>
                {
                    var leaf = match.Groups["leaf"].Value;
                    return leaf.Length == 0 ? "<path>" : leaf;
                })
            .Trim();

        if (collapsed.Length == 0)
        {
            return null;
        }

        return collapsed.Length <= MaximumSafeErrorDetailLength
            ? collapsed
            : string.Concat(
                collapsed.AsSpan(0, MaximumSafeErrorDetailLength),
                TruncationMarker.AsSpan());
    }

    public void Dispose() => _disposed = true;

    private void RollIfNeeded(int pendingLength)
    {
        var file = new FileInfo(LogPath);
        if (!file.Exists || file.Length + pendingLength <= MaximumBytes)
        {
            return;
        }

        File.Move(LogPath, RolledLogPath, overwrite: true);
    }
}

public sealed record DiagnosticVersionReport(
    string AppVersion,
    string TargetFramework,
    string SchemaVersion,
    string ProtocolVersion,
    string? DetectionModelVersion = null,
    string? RecognitionModelVersion = null,
    string? EmbeddingSpaceKey = null,
    string? ManagedNamingPolicyVersion = null,
    string? ProfileManifestSchemaVersion = null)
{

    public static DiagnosticVersionReport CreateCurrent(
        int? schemaVersion = null,
        DeploymentArtifactCatalog? artifacts = null)
    {
        var assembly = typeof(DiagnosticVersionReport).Assembly;

        return new DiagnosticVersionReport(
            AppVersion: ProductIdentity.Version,
            TargetFramework: ResolveTargetFrameworkMoniker(assembly),
            SchemaVersion: schemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "unopened",
            ProtocolVersion: Neuterradise.Profiling.Protocol.ProfilingProtocolVersion.Current.ToString(CultureInfo.InvariantCulture),
            DetectionModelVersion: artifacts?.FindModel("yunet")?.Version,
            RecognitionModelVersion: artifacts?.FindModel("sface")?.Version,
            EmbeddingSpaceKey: Neuterradise.App.Faces.EmbeddingSpaceKey.Baseline.Canonical,
            ManagedNamingPolicyVersion: ManagedNamingPolicyVersionValue,
            ProfileManifestSchemaVersion: ProfileManifestSchemaVersionValue.ToString(CultureInfo.InvariantCulture));
    }

    public const string ManagedNamingPolicyVersionValue = "1";

    public const int ProfileManifestSchemaVersionValue = 1;

    public string RuntimeDescription => RuntimeInformation.FrameworkDescription;

    private static string ResolveTargetFrameworkMoniker(Assembly assembly)
    {
        var frameworkName = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        if (string.IsNullOrWhiteSpace(frameworkName))
        {
            return RuntimeInformation.FrameworkDescription;
        }

        var parsed = new FrameworkName(frameworkName);
        if (!string.Equals(parsed.Identifier, ".NETCoreApp", StringComparison.Ordinal))
        {
            return frameworkName;
        }

        var moniker = string.Create(
            CultureInfo.InvariantCulture,
            $"net{parsed.Version.Major}.{parsed.Version.Minor}");

        var platform = assembly.GetCustomAttribute<TargetPlatformAttribute>()?.PlatformName;
        if (string.IsNullOrWhiteSpace(platform))
        {
            return moniker;
        }

        var platformName = new string(platform.TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
        return platformName.Length == 0 ? moniker : $"{moniker}-{platformName}";
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"app={AppVersion} tfm={TargetFramework} schema={SchemaVersion} protocol={ProtocolVersion}");
}
