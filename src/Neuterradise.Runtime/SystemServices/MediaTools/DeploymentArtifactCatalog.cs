using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.MediaTools;

public enum DeploymentArtifactKind
{

    Model,

    Tool,

    Native,

    Notice,
}

public enum DeploymentArtifactDistribution
{

    Provisioned,

    Build,
}

public enum DeploymentArtifactContentState
{
    NotProvisioned,
    Provisioned,
}

public enum DeploymentArtifactAvailability
{

    Verified,

    Missing,

    ChecksumMismatch,
}

public enum DeploymentSourceKind
{

    File,

    Archive,

    NuGet,
}

public sealed record DeploymentArtifactSource(
    string SourceId,
    DeploymentSourceKind Kind,
    string License,
    string Provenance,
    string? Url = null,
    string? Sha256 = null,
    string? Package = null,
    string? Version = null,
    string? LicenseUrl = null);

public sealed record DeploymentArtifact(
    string LogicalName,
    DeploymentArtifactKind Kind,
    string DeployedRelativePath,
    string Sha256,
    bool StartupBlocking,
    DeploymentArtifactContentState ContentState,
    string? ModelId = null,
    string? ToolId = null,
    string? Version = null,
    string? License = null,
    string? FeatureImpactWhenMissing = null,
    string? SourceId = null,
    string? SourceEntryPath = null,
    string? Provenance = null,
    string? MismatchBehavior = null,
    DeploymentArtifactDistribution Distribution = DeploymentArtifactDistribution.Provisioned,
    bool ReleaseRequired = false,
    IReadOnlyList<string>? Consumers = null);

public sealed record DeploymentDeclinedArtifact(
    string LogicalName,
    DeploymentArtifactKind Kind,
    string Decision,
    string Reason,
    string? FeatureImpactWhenAbsent = null,
    IReadOnlyList<string>? Consumers = null);

/// <summary>
/// The kind of runtime capability a status describes. This is deliberately independent of the
/// artifact manifest so preview adapters, which are managed code rather than deployed files, can
/// report through the same state model.
/// </summary>
public enum RuntimeCapabilityKind
{
    Tool,

    Model,

    Native,

    Notice,

    PreviewAdapter,
}

/// <summary>
/// The single runtime capability state model shared by tools, models and preview adapters.
/// Consumers must never treat anything other than <see cref="Available"/> as usable.
/// </summary>
public enum RuntimeCapabilityState
{
    /// <summary>Deployed, integrity-verified and usable now.</summary>
    Available,

    /// <summary>Deployable and intact, but switched off by an explicit policy or user decision.</summary>
    Disabled,

    /// <summary>Not deployed, not declared, or deliberately declined; the feature cannot run.</summary>
    Unavailable,

    /// <summary>Deployed but its bytes do not match the declared SHA-256; it must never be executed or loaded.</summary>
    IntegrityFailed,
}

/// <summary>
/// Truthful capability status handed to feature consumers. A non-available state always carries the
/// concrete reason, so a surface can explain the limitation instead of faking a successful result.
/// </summary>
public sealed record RuntimeCapabilityStatus(
    string CapabilityId,
    RuntimeCapabilityKind Kind,
    RuntimeCapabilityState State,
    string Reason,
    string? ResolvedPath = null,
    string? FeatureImpact = null)
{
    /// <summary>
    /// True only when the capability may actually be used. File-backed capabilities additionally
    /// require a resolved path, so a missing path can never be mistaken for a usable tool.
    /// </summary>
    public bool IsUsable =>
        State == RuntimeCapabilityState.Available
        && (Kind == RuntimeCapabilityKind.PreviewAdapter || ResolvedPath is not null);

    public static RuntimeCapabilityStatus Available(
        string capabilityId,
        RuntimeCapabilityKind kind,
        string reason,
        string? resolvedPath = null) =>
        new(capabilityId, kind, RuntimeCapabilityState.Available, reason, resolvedPath);

    public static RuntimeCapabilityStatus Disabled(
        string capabilityId,
        RuntimeCapabilityKind kind,
        string reason,
        string? featureImpact = null) =>
        new(capabilityId, kind, RuntimeCapabilityState.Disabled, reason, null, featureImpact);

    public static RuntimeCapabilityStatus Unavailable(
        string capabilityId,
        RuntimeCapabilityKind kind,
        string reason,
        string? featureImpact = null) =>
        new(capabilityId, kind, RuntimeCapabilityState.Unavailable, reason, null, featureImpact);

    public static RuntimeCapabilityStatus IntegrityFailed(
        string capabilityId,
        RuntimeCapabilityKind kind,
        string reason,
        string? featureImpact = null) =>
        new(capabilityId, kind, RuntimeCapabilityState.IntegrityFailed, reason, null, featureImpact);
}

public sealed record DeploymentArtifactStatus(
    DeploymentArtifact Artifact,
    DeploymentArtifactAvailability Availability,
    string ResolvedPath,
    string? ActualSha256 = null)
{

    public bool IsBlockingFailure =>
        Availability == DeploymentArtifactAvailability.ChecksumMismatch
        || (Availability == DeploymentArtifactAvailability.Missing && Artifact.StartupBlocking);

    public bool IsReleaseBlockingFailure =>
        Availability == DeploymentArtifactAvailability.ChecksumMismatch
        || (Availability == DeploymentArtifactAvailability.Missing && Artifact.ReleaseRequired);

    /// <summary>
    /// Projects this deployment fact onto the shared runtime capability state model. An artifact the
    /// manifest marks NOT_PROVISIONED is reported as unavailable rather than integrity-failed, because
    /// its declared digest is a placeholder and not evidence of tampering.
    /// </summary>
    public RuntimeCapabilityStatus ToCapabilityStatus()
    {
        var capabilityId = Artifact.Kind switch
        {
            DeploymentArtifactKind.Model => Artifact.ModelId ?? Artifact.LogicalName,
            DeploymentArtifactKind.Tool => Artifact.ToolId ?? Artifact.LogicalName,
            _ => Artifact.LogicalName,
        };

        var kind = Artifact.Kind switch
        {
            DeploymentArtifactKind.Model => RuntimeCapabilityKind.Model,
            DeploymentArtifactKind.Tool => RuntimeCapabilityKind.Tool,
            DeploymentArtifactKind.Native => RuntimeCapabilityKind.Native,
            _ => RuntimeCapabilityKind.Notice,
        };

        if (Artifact.ContentState == DeploymentArtifactContentState.NotProvisioned)
        {
            return RuntimeCapabilityStatus.Unavailable(
                capabilityId,
                kind,
                $"{Artifact.LogicalName} is declared but its bytes are not provisioned in this deployment.",
                Artifact.FeatureImpactWhenMissing);
        }

        return Availability switch
        {
            DeploymentArtifactAvailability.Verified => RuntimeCapabilityStatus.Available(
                capabilityId,
                kind,
                $"{Artifact.LogicalName} {Artifact.Version} verified at {Artifact.DeployedRelativePath}.",
                ResolvedPath),
            DeploymentArtifactAvailability.ChecksumMismatch => RuntimeCapabilityStatus.IntegrityFailed(
                capabilityId,
                kind,
                $"{Artifact.LogicalName} at {Artifact.DeployedRelativePath} failed its integrity check"
                    + $" (declared {Artifact.Sha256}, found {ActualSha256}); it was not used.",
                Artifact.FeatureImpactWhenMissing),
            _ => RuntimeCapabilityStatus.Unavailable(
                capabilityId,
                kind,
                $"{Artifact.LogicalName} is not present at {Artifact.DeployedRelativePath}.",
                Artifact.FeatureImpactWhenMissing),
        };
    }
}

public sealed class DeploymentArtifactCatalog
{

    public const string ManifestRelativePath = "deployment/artifacts.json";

    public const int SupportedSchemaVersion = 2;

    private static readonly JsonSerializerOptions ReaderOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly List<DeploymentArtifact> _artifacts;
    private readonly List<DeploymentArtifactSource> _sources;
    private readonly List<DeploymentDeclinedArtifact> _declined;

    private DeploymentArtifactCatalog(
        int schemaVersion,
        string policy,
        string? noticesRelativePath,
        List<DeploymentArtifactSource> sources,
        List<DeploymentArtifact> artifacts,
        List<DeploymentDeclinedArtifact> declined)
    {
        SchemaVersion = schemaVersion;
        Policy = policy;
        NoticesRelativePath = noticesRelativePath;
        _sources = sources;
        _artifacts = artifacts;
        _declined = declined;
    }

    public int SchemaVersion { get; }

    public string Policy { get; }

    public string? NoticesRelativePath { get; }

    public IReadOnlyList<DeploymentArtifactSource> Sources => _sources;

    public IReadOnlyList<DeploymentArtifact> Artifacts => _artifacts;

    public IReadOnlyList<DeploymentDeclinedArtifact> DeclinedArtifacts => _declined;

    public static DeploymentArtifactCatalog Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                "The deployment artifact manifest is required and was not found.",
                manifestPath);
        }

        return Parse(File.ReadAllText(manifestPath));
    }

    /// <summary>
    /// Absolute manifest path for a deployment. The manifest is always resolved directly under
    /// InstallRoot; no parent directory, PATH entry or working directory is ever searched, so a
    /// developer repository layout can never satisfy a production deployment lookup.
    /// </summary>
    public static string GetManifestPath(InstallPaths install)
    {
        ArgumentNullException.ThrowIfNull(install);
        return install.ResolveContainedPath(InstallPathArea.Runtime, ManifestRelativePath);
    }

    /// <summary>
    /// Loads the deployment manifest for an InstallRoot, or returns null when this deployment has no
    /// readable manifest. A null result means every artifact-backed capability is unavailable; it is
    /// never a reason to fall back to an unapproved binary.
    /// </summary>
    public static DeploymentArtifactCatalog? TryLoad(InstallPaths install)
    {
        ArgumentNullException.ThrowIfNull(install);

        try
        {
            var manifestPath = GetManifestPath(install);
            return File.Exists(manifestPath) ? Load(manifestPath) : null;
        }
        catch (Exception exception) when (exception
            is FileNotFoundException
            or DirectoryNotFoundException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return null;
        }
    }

    public static DeploymentArtifactCatalog Parse(string manifestJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);

        ManifestDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ManifestDocument>(manifestJson, ReaderOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The deployment artifact manifest is not valid JSON: {exception.Message}");
        }

        if (document is null)
        {
            throw new InvalidDataException("The deployment artifact manifest is empty.");
        }

        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported deployment artifact manifest schema version {document.SchemaVersion}."
                + $" Expected {SupportedSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(document.Policy))
        {
            throw new InvalidDataException("The deployment artifact manifest must state its provisioning policy.");
        }

        var sources = new List<DeploymentArtifactSource>();
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Sources ?? [])
        {
            var source = ReadSource(entry);
            if (!sourceIds.Add(source.SourceId))
            {
                throw new InvalidDataException($"Two sources declare the same sourceId '{source.SourceId}'.");
            }

            sources.Add(source);
        }

        var artifacts = new List<DeploymentArtifact>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in document.Artifacts ?? [])
        {
            artifacts.Add(ReadArtifact(entry, seenPaths, sourceIds));
        }

        if (artifacts.Count == 0)
        {
            throw new InvalidDataException("The deployment artifact manifest declares no artifacts.");
        }

        var declined = new List<DeploymentDeclinedArtifact>();
        foreach (var entry in document.DeclinedArtifacts ?? [])
        {
            declined.Add(ReadDeclined(entry));
        }

        return new DeploymentArtifactCatalog(
            document.SchemaVersion,
            document.Policy!,
            document.NoticesRelativePath,
            sources,
            artifacts,
            declined);
    }

    public DeploymentArtifact? Find(string logicalName) =>
        _artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.LogicalName, logicalName, StringComparison.Ordinal));

    public DeploymentArtifact? FindModel(string modelId) =>
        _artifacts.FirstOrDefault(artifact =>
            artifact.Kind == DeploymentArtifactKind.Model
            && string.Equals(artifact.ModelId, modelId, StringComparison.Ordinal));

    public DeploymentArtifact? FindTool(string toolId) =>
        _artifacts.FirstOrDefault(artifact =>
            artifact.Kind == DeploymentArtifactKind.Tool
            && string.Equals(artifact.ToolId, toolId, StringComparison.Ordinal));

    public DeploymentArtifactSource? FindSource(string? sourceId) =>
        sourceId is null
            ? null
            : _sources.FirstOrDefault(source => string.Equals(source.SourceId, sourceId, StringComparison.Ordinal));

    public DeploymentDeclinedArtifact? FindDeclined(string logicalName) =>
        _declined.FirstOrDefault(declined =>
            string.Equals(declined.LogicalName, logicalName, StringComparison.Ordinal));

    public static DeploymentArtifactStatus Verify(DeploymentArtifact artifact, string deploymentRoot)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentRoot);

        var resolved = Path.GetFullPath(Path.Combine(
            deploymentRoot,
            artifact.DeployedRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!File.Exists(resolved))
        {
            return new DeploymentArtifactStatus(
                artifact,
                DeploymentArtifactAvailability.Missing,
                resolved);
        }

        var actual = ComputeSha256(resolved);
        return actual.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)
            ? new DeploymentArtifactStatus(
                artifact, DeploymentArtifactAvailability.Verified, resolved, actual)
            : new DeploymentArtifactStatus(
                artifact, DeploymentArtifactAvailability.ChecksumMismatch, resolved, actual);
    }

    public IReadOnlyList<DeploymentArtifactStatus> VerifyAll(string deploymentRoot) =>
        _artifacts.Select(artifact => Verify(artifact, deploymentRoot)).ToArray();

    /// <summary>
    /// Single entry point for capability questions about a declared tool. A tool the deployment
    /// explicitly declined is reported as unavailable with the recorded decision, never as an error
    /// the user can fix by retrying.
    /// </summary>
    public RuntimeCapabilityStatus DescribeTool(string toolId, string deploymentRoot)
        => DescribeCapability(
            DeploymentArtifactKind.Tool,
            RuntimeCapabilityKind.Tool,
            toolId,
            FindTool(toolId),
            deploymentRoot);

    /// <summary>
    /// Single entry point for capability questions about a declared inference model.
    /// </summary>
    public RuntimeCapabilityStatus DescribeModel(string modelId, string deploymentRoot)
        => DescribeCapability(
            DeploymentArtifactKind.Model,
            RuntimeCapabilityKind.Model,
            modelId,
            FindModel(modelId),
            deploymentRoot);

    /// <summary>
    /// Capability status for every declared artifact plus every declined one, so a settings surface
    /// can list the whole deployment truthfully in one pass.
    /// </summary>
    public IReadOnlyList<RuntimeCapabilityStatus> DescribeAll(string deploymentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentRoot);

        var statuses = new List<RuntimeCapabilityStatus>(_artifacts.Count + _declined.Count);
        foreach (var status in VerifyAll(deploymentRoot))
        {
            statuses.Add(status.ToCapabilityStatus());
        }

        foreach (var declined in _declined)
        {
            statuses.Add(DescribeDeclined(declined.LogicalName, declined));
        }

        return statuses;
    }

    private RuntimeCapabilityStatus DescribeCapability(
        DeploymentArtifactKind artifactKind,
        RuntimeCapabilityKind capabilityKind,
        string capabilityId,
        DeploymentArtifact? artifact,
        string deploymentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentRoot);

        if (artifact is not null)
        {
            return Verify(artifact, deploymentRoot).ToCapabilityStatus();
        }

        var declined = _declined.FirstOrDefault(entry =>
            entry.Kind == artifactKind
            && string.Equals(entry.LogicalName, capabilityId, StringComparison.Ordinal));

        if (declined is not null)
        {
            return DescribeDeclined(capabilityId, declined);
        }

        return RuntimeCapabilityStatus.Unavailable(
            capabilityId,
            capabilityKind,
            $"The deployment artifact manifest declares no {artifactKind.ToString().ToLowerInvariant()}"
                + $" '{capabilityId}', so nothing may be loaded for it.");
    }

    private static RuntimeCapabilityStatus DescribeDeclined(
        string capabilityId,
        DeploymentDeclinedArtifact declined)
    {
        var capabilityKind = declined.Kind switch
        {
            DeploymentArtifactKind.Model => RuntimeCapabilityKind.Model,
            DeploymentArtifactKind.Tool => RuntimeCapabilityKind.Tool,
            DeploymentArtifactKind.Native => RuntimeCapabilityKind.Native,
            _ => RuntimeCapabilityKind.Notice,
        };

        return RuntimeCapabilityStatus.Disabled(
            capabilityId,
            capabilityKind,
            $"{declined.LogicalName} is deliberately not shipped ({declined.Decision}): {declined.Reason}",
            declined.FeatureImpactWhenAbsent);
    }

    public static string ComputeSha256(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static DeploymentArtifactSource ReadSource(ManifestSource entry)
    {
        if (string.IsNullOrWhiteSpace(entry.SourceId))
        {
            throw new InvalidDataException("Every declared source needs a sourceId.");
        }

        var kind = entry.Kind?.ToLowerInvariant() switch
        {
            "file" => DeploymentSourceKind.File,
            "archive" => DeploymentSourceKind.Archive,
            "nuget" => DeploymentSourceKind.NuGet,
            _ => throw new InvalidDataException(
                $"Source '{entry.SourceId}' declares an unknown kind '{entry.Kind}'."),
        };

        if (string.IsNullOrWhiteSpace(entry.License))
        {
            throw new InvalidDataException($"Source '{entry.SourceId}' must declare a license.");
        }

        if (string.IsNullOrWhiteSpace(entry.Provenance))
        {

            throw new InvalidDataException($"Source '{entry.SourceId}' must declare its provenance.");
        }

        if (kind is DeploymentSourceKind.File or DeploymentSourceKind.Archive)
        {
            if (string.IsNullOrWhiteSpace(entry.Url))
            {
                throw new InvalidDataException($"Source '{entry.SourceId}' must declare a url.");
            }

            if (entry.Sha256 is not { Length: 64 } || !IsLowercaseHex(entry.Sha256))
            {
                throw new InvalidDataException(
                    $"Source '{entry.SourceId}' must declare a lowercase 64-character SHA-256.");
            }
        }
        else if (string.IsNullOrWhiteSpace(entry.Package) || string.IsNullOrWhiteSpace(entry.Version))
        {
            throw new InvalidDataException(
                $"Source '{entry.SourceId}' must declare the package and version it comes from.");
        }

        return new DeploymentArtifactSource(
            entry.SourceId!,
            kind,
            entry.License!,
            entry.Provenance!,
            entry.Url,
            entry.Sha256,
            entry.Package,
            entry.Version,
            entry.LicenseUrl);
    }

    private static DeploymentArtifact ReadArtifact(
        ManifestArtifact entry,
        HashSet<string> seenPaths,
        HashSet<string> sourceIds)
    {
        if (string.IsNullOrWhiteSpace(entry.LogicalName))
        {
            throw new InvalidDataException("Every declared artifact needs a logicalName.");
        }

        if (string.IsNullOrWhiteSpace(entry.DeployedRelativePath))
        {
            throw new InvalidDataException(
                $"Artifact '{entry.LogicalName}' has no deployedRelativePath.");
        }

        if (Path.IsPathRooted(entry.DeployedRelativePath)
            || entry.DeployedRelativePath.Contains("..", StringComparison.Ordinal))
        {

            throw new InvalidDataException(
                $"Artifact '{entry.LogicalName}' declares an escaping deployedRelativePath.");
        }

        if (!seenPaths.Add(entry.DeployedRelativePath))
        {
            throw new InvalidDataException(
                $"Two artifacts declare the same deployedRelativePath '{entry.DeployedRelativePath}'.");
        }

        if (entry.Sha256 is not { Length: 64 } || !IsLowercaseHex(entry.Sha256))
        {
            throw new InvalidDataException(
                $"Artifact '{entry.LogicalName}' must declare a lowercase 64-character SHA-256.");
        }

        var kind = entry.Kind?.ToLowerInvariant() switch
        {
            "model" => DeploymentArtifactKind.Model,
            "tool" => DeploymentArtifactKind.Tool,
            "native" => DeploymentArtifactKind.Native,
            "notice" => DeploymentArtifactKind.Notice,
            _ => throw new InvalidDataException(
                $"Artifact '{entry.LogicalName}' declares an unknown kind '{entry.Kind}'."),
        };

        var distribution = entry.Distribution?.ToLowerInvariant() switch
        {
            "provisioned" => DeploymentArtifactDistribution.Provisioned,
            "build" => DeploymentArtifactDistribution.Build,
            _ => throw new InvalidDataException(
                $"Artifact '{entry.LogicalName}' declares an unknown distribution '{entry.Distribution}'."),
        };

        var contentState = entry.ContentState?.ToUpperInvariant() switch
        {
            "NOT_PROVISIONED" => DeploymentArtifactContentState.NotProvisioned,
            "PROVISIONED" => DeploymentArtifactContentState.Provisioned,
            _ => throw new InvalidDataException(
                $"Artifact '{entry.LogicalName}' declares an unknown contentState '{entry.ContentState}'."),
        };

        if (kind == DeploymentArtifactKind.Model && string.IsNullOrWhiteSpace(entry.ModelId))
        {
            throw new InvalidDataException($"Model artifact '{entry.LogicalName}' needs a modelId.");
        }

        if (string.IsNullOrWhiteSpace(entry.Provenance))
        {
            throw new InvalidDataException($"Artifact '{entry.LogicalName}' must declare its provenance.");
        }

        if (string.IsNullOrWhiteSpace(entry.License))
        {
            throw new InvalidDataException($"Artifact '{entry.LogicalName}' must declare its license.");
        }

        if (string.IsNullOrWhiteSpace(entry.MismatchBehavior))
        {

            throw new InvalidDataException(
                $"Artifact '{entry.LogicalName}' must declare its mismatchBehavior.");
        }

        if (string.IsNullOrWhiteSpace(entry.SourceId) || !sourceIds.Contains(entry.SourceId))
        {
            throw new InvalidDataException(
                $"Artifact '{entry.LogicalName}' names sourceId '{entry.SourceId}', which no source declares.");
        }

        var consumers = entry.Consumers ?? [];
        if (consumers.Count == 0 || consumers.Any(string.IsNullOrWhiteSpace))
        {

            throw new InvalidDataException(
                $"Artifact '{entry.LogicalName}' must name at least one consumer.");
        }

        return new DeploymentArtifact(
            entry.LogicalName!,
            kind,
            entry.DeployedRelativePath!,
            entry.Sha256!,
            entry.StartupBlocking,
            contentState,
            entry.ModelId,
            entry.ToolId,
            entry.Version,
            entry.License,
            entry.FeatureImpactWhenMissing,
            entry.SourceId,
            entry.SourceEntryPath,
            entry.Provenance,
            entry.MismatchBehavior,
            distribution,
            entry.ReleaseRequired,
            [.. consumers]);
    }

    private static DeploymentDeclinedArtifact ReadDeclined(ManifestDeclinedArtifact entry)
    {
        if (string.IsNullOrWhiteSpace(entry.LogicalName))
        {
            throw new InvalidDataException("Every declined artifact needs a logicalName.");
        }

        var kind = entry.Kind?.ToLowerInvariant() switch
        {
            "model" => DeploymentArtifactKind.Model,
            "tool" => DeploymentArtifactKind.Tool,
            "native" => DeploymentArtifactKind.Native,
            "notice" => DeploymentArtifactKind.Notice,
            _ => throw new InvalidDataException(
                $"Declined artifact '{entry.LogicalName}' declares an unknown kind '{entry.Kind}'."),
        };

        if (string.IsNullOrWhiteSpace(entry.Decision) || string.IsNullOrWhiteSpace(entry.Reason))
        {
            throw new InvalidDataException(
                $"Declined artifact '{entry.LogicalName}' must record its decision and the reason for it.");
        }

        return new DeploymentDeclinedArtifact(
            entry.LogicalName!,
            kind,
            entry.Decision!,
            entry.Reason!,
            entry.FeatureImpactWhenAbsent,
            entry.Consumers is null ? null : [.. entry.Consumers]);
    }

    private static bool IsLowercaseHex(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character) && (character is < 'a' or > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record ManifestDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("policy")]
        public string? Policy { get; init; }

        [JsonPropertyName("noticesRelativePath")]
        public string? NoticesRelativePath { get; init; }

        [JsonPropertyName("sources")]
        public IReadOnlyList<ManifestSource>? Sources { get; init; }

        [JsonPropertyName("artifacts")]
        public IReadOnlyList<ManifestArtifact>? Artifacts { get; init; }

        [JsonPropertyName("declinedArtifacts")]
        public IReadOnlyList<ManifestDeclinedArtifact>? DeclinedArtifacts { get; init; }
    }

    private sealed record ManifestSource
    {
        [JsonPropertyName("sourceId")]
        public string? SourceId { get; init; }

        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }

        [JsonPropertyName("package")]
        public string? Package { get; init; }

        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("license")]
        public string? License { get; init; }

        [JsonPropertyName("licenseUrl")]
        public string? LicenseUrl { get; init; }

        [JsonPropertyName("provenance")]
        public string? Provenance { get; init; }
    }

    private sealed record ManifestArtifact
    {
        [JsonPropertyName("logicalName")]
        public string? LogicalName { get; init; }

        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("modelId")]
        public string? ModelId { get; init; }

        [JsonPropertyName("toolId")]
        public string? ToolId { get; init; }

        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("sourceId")]
        public string? SourceId { get; init; }

        [JsonPropertyName("sourceEntryPath")]
        public string? SourceEntryPath { get; init; }

        [JsonPropertyName("deployedRelativePath")]
        public string? DeployedRelativePath { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }

        [JsonPropertyName("license")]
        public string? License { get; init; }

        [JsonPropertyName("provenance")]
        public string? Provenance { get; init; }

        [JsonPropertyName("consumers")]
        public IReadOnlyList<string>? Consumers { get; init; }

        [JsonPropertyName("distribution")]
        public string? Distribution { get; init; }

        [JsonPropertyName("featureImpactWhenMissing")]
        public string? FeatureImpactWhenMissing { get; init; }

        [JsonPropertyName("mismatchBehavior")]
        public string? MismatchBehavior { get; init; }

        [JsonPropertyName("startupBlocking")]
        public bool StartupBlocking { get; init; }

        [JsonPropertyName("releaseRequired")]
        public bool ReleaseRequired { get; init; }

        [JsonPropertyName("contentState")]
        public string? ContentState { get; init; }
    }

    private sealed record ManifestDeclinedArtifact
    {
        [JsonPropertyName("logicalName")]
        public string? LogicalName { get; init; }

        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("decision")]
        public string? Decision { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }

        [JsonPropertyName("consumers")]
        public IReadOnlyList<string>? Consumers { get; init; }

        [JsonPropertyName("featureImpactWhenAbsent")]
        public string? FeatureImpactWhenAbsent { get; init; }
    }
}
