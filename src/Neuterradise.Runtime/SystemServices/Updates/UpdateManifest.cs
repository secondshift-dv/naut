using System.Text.Json;
using System.Text.Json.Serialization;
using Neuterradise.App.SystemServices;
using Neuterradise.Release.Contracts;

namespace Neuterradise.App.SystemServices.Updates;

public sealed record UpdateManifest(
    int SchemaVersion,
    string ProductId,
    string ProductVersion,
    string RuntimeIdentifier,
    long PayloadByteLength,
    string PayloadSha256,
    string? MinimumCompatibleVersion,
    IReadOnlyList<UpdateManifestFile> Files)
{
    public const int CurrentSchemaVersion = 1;
    private const int MaxFiles = 100_000;

    public static UpdateManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (json.Length > 16 * 1024 * 1024)
            throw new FormatException("Update manifest exceeds the safe parser limit.");

        var value = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions)
            ?? throw new FormatException("Update manifest is empty.");
        value.Validate();
        return value;
    }

    public string ComputeAuthoritySha256() =>
        UpdateManifestAuthority.ComputeSha256(
            SchemaVersion,
            ProductId,
            ProductVersion,
            RuntimeIdentifier,
            PayloadByteLength,
            PayloadSha256,
            MinimumCompatibleVersion,
            Files.Select(file => new ManifestAuthorityFile(
                file.RelativePath,
                file.ByteLength,
                file.Sha256,
                file.Role)));

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new FormatException("Unsupported update manifest schema.");
        if (!string.Equals(ProductId, ProductIdentity.ProductId, StringComparison.Ordinal))
            throw new FormatException("Update product identity does not match.");

        var productVersion = ParseStrictVersion(ProductVersion, "Update product version");
        ArgumentException.ThrowIfNullOrWhiteSpace(RuntimeIdentifier);
        if (MinimumCompatibleVersion is { } minimumCompatibleVersion)
        {
            var minimum = ParseStrictVersion(minimumCompatibleVersion, "Minimum compatible version");
            if (minimum > productVersion)
                throw new FormatException("Minimum compatible version cannot be newer than the update itself.");
        }
        if (PayloadByteLength < 0)
            throw new FormatException("Update payload length is invalid.");
        ValidateHash(PayloadSha256);
        ValidateFileMembership(Files, MaxFiles, "Update manifest");
    }

    internal static Version ParseStrictVersion(string value, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        if (!Version.TryParse(trimmed, out var parsed)
            || parsed.Major < 0
            || parsed.Minor < 0
            || parsed.Build < 0)
        {
            throw new FormatException($"{label} is not a supported numeric product version.");
        }

        if (!string.Equals(parsed.ToString(), trimmed, StringComparison.Ordinal))
            throw new FormatException($"{label} must use canonical numeric version syntax.");

        return parsed;
    }

    internal static void ValidateFileMembership(
        IReadOnlyList<UpdateManifestFile>? files,
        int maximumFiles,
        string sourceName)
    {
        if (files is null || files.Count > maximumFiles)
            throw new FormatException($"{sourceName} file membership is invalid.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            file.Validate();
            var normalized = file.RelativePath.Replace('\\', '/');
            if (string.Equals(normalized, "release-manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException(
                    "release-manifest.json is a control manifest and cannot be part of payload-hashed file membership.");
            }

            if (!seen.Add(normalized))
                throw new FormatException($"{sourceName} contains duplicate file membership.");
        }
    }

    private static void ValidateHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 64
            || value.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f')))
        {
            throw new FormatException("Update hash must be lowercase SHA-256.");
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
}

/// <summary>
/// Control manifest embedded inside the extracted update payload. It deliberately excludes the ZIP
/// byte length and ZIP SHA-256 because a file inside an archive cannot truthfully contain the final
/// hash of the archive that contains that same file. The feed UpdateManifest remains the authority
/// for archive identity; this control manifest binds the installed file membership to product/version/runtime.
/// </summary>
public sealed record UpdateReleaseManifest(
    int SchemaVersion,
    string ProductId,
    string ProductVersion,
    string RuntimeIdentifier,
    IReadOnlyList<UpdateManifestFile> Files)
{
    private const int MaxFiles = 100_000;

    public static UpdateReleaseManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (json.Length > 2 * 1024 * 1024)
            throw new FormatException("Embedded release manifest exceeds the safe parser limit.");

        var value = JsonSerializer.Deserialize<UpdateReleaseManifest>(json, UpdateManifest.JsonOptions)
            ?? throw new FormatException("Embedded release manifest is empty.");
        value.Validate();
        return value;
    }

    public void Validate()
    {
        if (SchemaVersion != UpdateManifest.CurrentSchemaVersion)
            throw new FormatException("Unsupported embedded release manifest schema.");
        if (!string.Equals(ProductId, ProductIdentity.ProductId, StringComparison.Ordinal))
            throw new FormatException("Embedded release manifest product identity does not match.");

        ArgumentException.ThrowIfNullOrWhiteSpace(ProductVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(RuntimeIdentifier);
        UpdateManifest.ValidateFileMembership(Files, MaxFiles, "Embedded release manifest");
    }
}

public sealed record UpdateManifestFile(string RelativePath, long ByteLength, string Sha256, string? Role)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RelativePath)
            || Path.IsPathRooted(RelativePath)
            || RelativePath.Contains("..", StringComparison.Ordinal)
            || RelativePath.Contains(':', StringComparison.Ordinal)
            || RelativePath.Contains('\0'))
        {
            throw new FormatException("Update file path is unsafe.");
        }

        if (ByteLength < 0
            || string.IsNullOrWhiteSpace(Sha256)
            || Sha256.Length != 64
            || Sha256.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f')))
        {
            throw new FormatException("Update file identity is invalid.");
        }
    }
}

/// <summary>
/// Durable handoff from the running app to the isolated updater. VaultRoot is protection metadata,
/// not a mutation target: the helper must prove its install/staging/backup roots are disjoint from it
/// before any replacement filesystem mutation is allowed.
/// </summary>
public sealed record UpdateHandoff(
    Guid OperationId,
    string SourcePayloadPath,
    string DestinationInstallRoot,
    UpdateManifest Manifest,
    string? VaultRoot = null,
    DateTimeOffset? ShutdownDeadlineUtc = null);
