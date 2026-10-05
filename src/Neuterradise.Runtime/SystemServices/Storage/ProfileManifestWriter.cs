using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;

namespace Neuterradise.App.SystemServices.Storage;

public enum ManifestStatus
{
    Valid,
    Missing,
    Malformed
}

public sealed record ManifestInspectionResult(
    ManifestStatus Status,
    ProfileManifestV1? Manifest = null,
    string? ErrorDetail = null);

public sealed class ProfileManifestWriter
{
    public const string ManifestFileName = "manifest.json";

    public static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly VaultPaths? _paths;

    public ProfileManifestWriter(VaultPaths? paths = null)
    {
        _paths = paths;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new ProfileKindUpperStringConverter());
        options.Converters.Add(new IsoDateTimeOffsetUtcConverter());
        return options;
    }

    public static string Serialize(ProfileManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, SerializerOptions);
    }

    public static byte[] SerializeToUtf8Bytes(ProfileManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
    }

    public static ProfileManifestV1 Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var manifest = JsonSerializer.Deserialize<ProfileManifestV1>(json, SerializerOptions)
            ?? throw new JsonException("Deserialized manifest is null.");
        ValidateManifest(manifest);
        return manifest;
    }

    public static ProfileManifestV1 Deserialize(ReadOnlySpan<byte> utf8)
    {
        var manifest = JsonSerializer.Deserialize<ProfileManifestV1>(utf8, SerializerOptions)
            ?? throw new JsonException("Deserialized manifest is null.");
        ValidateManifest(manifest);
        return manifest;
    }

    public static void ValidateManifest(ProfileManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.SchemaVersion != 1)
        {
            throw new JsonException($"Unsupported manifest schema version {manifest.SchemaVersion}. Expected 1.");
        }

        if (manifest.ProfileId == Guid.Empty)
        {
            throw new JsonException("Manifest ProfileId cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
        {
            throw new JsonException("Manifest DisplayName cannot be empty or whitespace.");
        }

        if (string.IsNullOrWhiteSpace(manifest.FolderName))
        {
            throw new JsonException("Manifest FolderName cannot be empty or whitespace.");
        }

        if (manifest.Kind == ProfileKind.Unknown
            && (!manifest.UnknownSequence.HasValue || manifest.UnknownSequence.Value <= 0))
        {
            throw new JsonException("UNKNOWN manifest must have a positive UnknownSequence.");
        }

        if (manifest.FigureMediaId == Guid.Empty) throw new JsonException("Manifest FigureMediaId cannot be empty.");

        if (manifest.Kind == ProfileKind.Unknown &&
            (manifest.CoverMediaAssetId.HasValue || manifest.BannerMediaAssetId.HasValue || manifest.FigureMediaId.HasValue))
            throw new JsonException("UNKNOWN manifest cannot select Profile media.");
    }

    public static ProfileManifestV1 CreateManifest(ProfileDetailReadModel profile, string? folderName = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var effectiveFolderName = folderName;
        if (string.IsNullOrWhiteSpace(effectiveFolderName))
        {
            var namePolicy = new ManagedNamePolicy();
            var safeName = namePolicy.ToSafeProfileName(profile.DisplayName);
            effectiveFolderName = $"{safeName} [{profile.StorageToken ?? "P-000000"}]";
        }

        return new ProfileManifestV1(
            schemaVersion: 1,
            profileId: profile.ProfileId,
            kind: profile.Kind,
            displayName: profile.DisplayName,
            folderName: effectiveFolderName,
            identityId: profile.IdentityId,
            coverMediaAssetId: profile.CoverMediaAssetId,
            bannerMediaAssetId: profile.BannerMediaAssetId,
            layoutPresetId: profile.LayoutPresetId,
            appearanceOverridesJson: profile.AppearanceOverridesJson,
            updatedAtUtc: profile.UpdatedAtUtc.ToUniversalTime(),
            unknownSequence: (int?)profile.UnknownSequence, figureMediaId: profile.FigureMediaId);
    }

    public static ProfileManifestV1 CreateManifest(
        Guid profileId,
        ProfileKind kind,
        string displayName,
        string folderName,
        Guid? identityId,
        DateTimeOffset updatedAtUtc,
        int? unknownSequence = null)
    {
        return new ProfileManifestV1(
            schemaVersion: 1,
            profileId: profileId,
            kind: kind,
            displayName: displayName,
            folderName: folderName,
            identityId: identityId,
            coverMediaAssetId: null,
            bannerMediaAssetId: null,
            layoutPresetId: null,
            appearanceOverridesJson: null,
            updatedAtUtc: updatedAtUtc.ToUniversalTime(),
            unknownSequence: unknownSequence);
    }

    public async Task<StorageOperationResult> WriteManifestAsync(
        string profileFolderAbsolutePath,
        ProfileManifestV1 manifest,
        Guid? operationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileFolderAbsolutePath);
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateManifest(manifest);

        if (_paths is not null)
        {
            try
            {
                var fullPath = Path.GetFullPath(profileFolderAbsolutePath);
                var vaultProfiles = _paths.ProfilesPath;
                if (!RootPathRules.IsStrictDescendant(vaultProfiles, fullPath))
                {
                    return new StorageOperationResult(
                        StorageOperationStatus.PathOutsideVault,
                        SafeErrorDetail: "Profile folder path is outside vault profiles directory.");
                }
                RootPathRules.RejectExistingReparsePoints(_paths.Root, fullPath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                return new StorageOperationResult(
                    StorageOperationStatus.PathOutsideVault,
                    SafeErrorDetail: "Invalid profile folder path.");
            }
        }

        var opId = operationId ?? Guid.NewGuid();
        var manifestPath = Path.Combine(profileFolderAbsolutePath, ManifestFileName);
        var tempPath = Path.Combine(profileFolderAbsolutePath, $"{ManifestFileName}.tmp-{opId:N}");
        var backupPath = Path.Combine(profileFolderAbsolutePath, $"{ManifestFileName}.bak-{opId:N}");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(profileFolderAbsolutePath);

            var bytes = SerializeToUtf8Bytes(manifest);
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);

            var tempBytes = await File.ReadAllBytesAsync(tempPath, cancellationToken).ConfigureAwait(false);
            _ = Deserialize(tempBytes);

            if (File.Exists(manifestPath))
            {
                try
                {
                    File.Replace(tempPath, manifestPath, backupPath, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(manifestPath);
                    File.Move(tempPath, manifestPath);
                }
                catch (IOException)
                {
                    if (File.Exists(tempPath))
                    {
                        File.Copy(tempPath, manifestPath, overwrite: true);
                        File.Delete(tempPath);
                    }
                }
            }
            else
            {
                File.Move(tempPath, manifestPath);
            }

            if (!File.Exists(manifestPath))
            {
                return new StorageOperationResult(
                    StorageOperationStatus.Failed,
                    SafeErrorDetail: "Final manifest does not exist after replace.");
            }

            var finalBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            _ = Deserialize(finalBytes);

            if (File.Exists(backupPath))
            {
                try
                {
                    File.Delete(backupPath);
                }
                catch
                {
                }
            }

            return new StorageOperationResult(
                StorageOperationStatus.Success,
                BytesProcessed: bytes.Length);
        }
        catch (OperationCanceledException)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new StorageOperationResult(
                StorageOperationStatus.Failed,
                SafeErrorDetail: $"Manifest write failed: {ex.Message}");
        }
    }

    public Task<StorageOperationResult> RegenerateManifestAsync(
        CatalogDb catalog,
        Guid profileId,
        CancellationToken cancellationToken = default) =>
        RegenerateManifestCoreAsync(catalog, profileId, operationId: null, cancellationToken);

    public Task<StorageOperationResult> RegenerateManifestAsync(
        CatalogDb catalog,
        Guid profileId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A repair OperationId cannot be empty.", nameof(operationId));
        }

        return RegenerateManifestCoreAsync(catalog, profileId, operationId, cancellationToken);
    }

    private async Task<StorageOperationResult> RegenerateManifestCoreAsync(
        CatalogDb catalog,
        Guid profileId,
        Guid? operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (profileId == Guid.Empty)
        {
            return new StorageOperationResult(
                StorageOperationStatus.SourceMissing,
                SafeErrorDetail: "ProfileId cannot be empty.");
        }

        var profile = await catalog.ProfileReads.GetDetailAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return new StorageOperationResult(
                StorageOperationStatus.SourceMissing,
                SafeErrorDetail: $"Profile {profileId} not found in catalog.");
        }

        if (profile.TrashedAtUtc.HasValue)
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: $"Profile {profileId} is trashed; manifest regeneration is for active profiles.");
        }

        var folder = await catalog.ProfileReads.GetFolderAsync(profileId, cancellationToken).ConfigureAwait(false);
        var currentRelative = folder?.CurrentManagedRelativePath;

        if (string.IsNullOrWhiteSpace(currentRelative))
        {
            // Manifest repair is not placement authority. A missing persisted folder must be
            // reconciled by the storage lifecycle before any writer is allowed to materialize bytes.
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: $"Profile {profileId} has no persisted managed folder authority.");
        }

        var absFolder = catalog.Paths.ResolveVaultRelativePath(currentRelative);
        var folderName = Path.GetFileName(currentRelative);

        var manifest = CreateManifest(profile, folderName);
        return await WriteManifestAsync(absFolder, manifest, operationId, cancellationToken).ConfigureAwait(false);
    }

    public static ManifestInspectionResult InspectManifest(string profileFolderAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(profileFolderAbsolutePath) || !Directory.Exists(profileFolderAbsolutePath))
        {
            return new ManifestInspectionResult(
                ManifestStatus.Missing,
                ErrorDetail: "Profile directory does not exist.");
        }

        var manifestPath = Path.Combine(profileFolderAbsolutePath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return new ManifestInspectionResult(
                ManifestStatus.Missing,
                ErrorDetail: "manifest.json does not exist.");
        }

        try
        {
            var bytes = File.ReadAllBytes(manifestPath);
            var manifest = Deserialize(bytes);
            return new ManifestInspectionResult(ManifestStatus.Valid, manifest);
        }
        catch (Exception ex)
        {
            return new ManifestInspectionResult(ManifestStatus.Malformed, ErrorDetail: ex.Message);
        }
    }
}
