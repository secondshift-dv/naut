using System.Globalization;
using System.IO;
using System.Text;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.SystemServices.Storage;

/// <summary>
/// Canonical, side-effect-free planner for Profile media and presentation paths. A caller resolves
/// collisions before persisting a plan and before any filesystem write; retries consume that
/// persisted plan instead of calling the allocator again.
/// </summary>
public sealed class ManagedPathPlanner
{
    public const int MaximumAbsolutePathCodeUnits = 240;
    public const string PathTooLongCode = "MANAGED_PATH_TOO_LONG";
    public const string PathConflictCode = "PATH_CONFLICT";

    private static readonly int[] AllowedSuffixLengths = [8, 12, 16, 20, 24, 28, 32];
    private readonly ManagedNamePolicy _namePolicy;
    private readonly string? _vaultRoot;

    public ManagedPathPlanner(ManagedNamePolicy? namePolicy = null)
        : this(null, namePolicy)
    {
    }

    public ManagedPathPlanner(string? vaultRoot, ManagedNamePolicy? namePolicy = null)
    {
        _namePolicy = namePolicy ?? new ManagedNamePolicy();
        _vaultRoot = string.IsNullOrWhiteSpace(vaultRoot) ? null : Path.GetFullPath(vaultRoot);
    }

    public ManagedPathPlan PlanProfile(
        Guid profileId,
        string displayLabel,
        ProfileStorageToken profileStorageToken,
        int idSuffixLength = 8)
    {
        EnsureNonEmpty(profileId, nameof(profileId));
        Validate(profileStorageToken);
        ValidateSuffixLength(idSuffixLength);

        var suffix = $"__{IdSuffix(profileId, idSuffixLength)}";
        var safeName = FitHumanSegment(
            _namePolicy.ToSafeProfileName(displayLabel),
            suffix,
            "profiles");
        var profileFolder = CombineRelative("profiles", safeName + suffix);
        EnsureWithinBudget(profileFolder);

        return new ManagedPathPlan(
            profileId,
            profileStorageToken,
            profileFolder,
            MediaId: null,
            MediaStorageToken: null,
            MediaType: null,
            ManagedFileRelativePath: null,
            ManagedFileName: null);
    }

    public ManagedPathPlan PlanMedia(
        Guid profileId,
        string displayLabel,
        ProfileStorageToken profileStorageToken,
        Guid assetId,
        MediaStorageToken assetStorageToken,
        MediaType mediaType,
        string extension,
        int profileIdSuffixLength = 8,
        int assetIdSuffixLength = 8)
    {
        EnsureNonEmpty(assetId, nameof(assetId));
        Validate(assetStorageToken);
        ValidateSuffixLength(assetIdSuffixLength);

        var profilePlan = PlanProfile(profileId, displayLabel, profileStorageToken, profileIdSuffixLength);
        var normalizedExtension = _namePolicy.NormalizeExtension(extension);
        var mediaFolder = GetMediaFolder(mediaType);
        var role = GetMediaRole(mediaType);
        var suffix = $"__{role}__{IdSuffix(assetId, assetIdSuffixLength)}.{normalizedExtension}";
        var safeName = FitHumanSegment(
            _namePolicy.ToSafeProfileName(displayLabel),
            suffix,
            profilePlan.ProfileFolderRelativePath,
            "Media",
            mediaFolder);
        var managedFileName = safeName + suffix;
        var managedFilePath = CombineRelative(
            profilePlan.ProfileFolderRelativePath,
            "Media",
            mediaFolder,
            managedFileName);
        EnsureWithinBudget(managedFilePath);

        return profilePlan with
        {
            MediaId = assetId,
            MediaStorageToken = assetStorageToken,
            MediaType = mediaType,
            ManagedFileRelativePath = managedFilePath,
            ManagedFileName = managedFileName,
        };
    }

    /// <summary>Plans the directory form required for a model package with dependencies.</summary>
    public ManagedModelPackagePlan PlanModelPackage(
        Guid profileId,
        string displayLabel,
        ProfileStorageToken profileStorageToken,
        Guid assetId,
        MediaStorageToken assetStorageToken,
        string primaryRelativeComponentPath,
        int profileIdSuffixLength = 8,
        int assetIdSuffixLength = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryRelativeComponentPath);
        EnsureSafeComponentPath(primaryRelativeComponentPath);
        var profile = PlanProfile(profileId, displayLabel, profileStorageToken, profileIdSuffixLength);
        Validate(assetStorageToken);
        ValidateSuffixLength(assetIdSuffixLength);

        var suffix = $"__model__{IdSuffix(assetId, assetIdSuffixLength)}";
        var normalizedPrimary = NormalizeComponentPath(primaryRelativeComponentPath);
        var safeName = FitHumanSegment(
            _namePolicy.ToSafeProfileName(displayLabel), suffix + "/" + normalizedPrimary,
            profile.ProfileFolderRelativePath, "Media", "Models");
        var packageDirectory = CombineRelative(profile.ProfileFolderRelativePath, "Media", "Models", safeName + suffix);
        var primary = CombineRelative(packageDirectory, normalizedPrimary);
        EnsureWithinBudget(primary);
        return new ManagedModelPackagePlan(assetId, packageDirectory, primary, Path.GetFileName(primaryRelativeComponentPath));
    }

    public string PlanMediaAsset(MediaStorageToken token, MediaAssetRole role)
    {
        Validate(token);
        var name = role switch
        {
            MediaAssetRole.Thumbnail => "thumbnail.webp",
            MediaAssetRole.Hover => "hover.mp4",
            MediaAssetRole.ModelRender => "model-render.nfig",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
        };
        var relative = CombineRelative("media-assets", token.Value, name);
        EnsureWithinBudget(relative);
        return relative;
    }

    public ManagedPathPlan AllocateProfilePlan(
        Guid profileId,
        string displayLabel,
        ProfileStorageToken profileStorageToken,
        Func<ManagedPathPlan, bool> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        foreach (var profileLength in AllowedSuffixLengths)
        {
            var candidate = PlanProfile(profileId, displayLabel, profileStorageToken, profileLength);
            if (!conflicts(candidate))
            {
                return candidate;
            }
        }

        throw new ManagedPathPlanningException(
            PathConflictCode,
            "All deterministic Profile ID suffixes conflict with another entity or unknown bytes.");
    }

    public ManagedModelPackagePlan AllocateModelPackagePlan(
        Guid profileId,
        string displayLabel,
        ProfileStorageToken profileStorageToken,
        Guid assetId,
        MediaStorageToken assetStorageToken,
        string primaryRelativeComponentPath,
        Func<ManagedModelPackagePlan, bool> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        foreach (var profileLength in AllowedSuffixLengths)
        {
            foreach (var assetLength in AllowedSuffixLengths)
            {
                var candidate = PlanModelPackage(
                    profileId,
                    displayLabel,
                    profileStorageToken,
                    assetId,
                    assetStorageToken,
                    primaryRelativeComponentPath,
                    profileLength,
                    assetLength);
                if (!conflicts(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new ManagedPathPlanningException(
            PathConflictCode,
            "All deterministic model-package ID suffixes conflict with another entity or unknown bytes.");
    }

    /// <summary>
    /// Selects the first nonconflicting deterministic ID suffix. The predicate must return true
    /// only when the candidate is owned by another entity or unknown bytes.
    /// </summary>
    public ManagedPathPlan AllocateMediaPlan(
        Guid profileId,
        string displayLabel,
        ProfileStorageToken profileStorageToken,
        Guid assetId,
        MediaStorageToken assetStorageToken,
        MediaType mediaType,
        string extension,
        Func<ManagedPathPlan, bool> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        foreach (var profileLength in AllowedSuffixLengths)
        {
            foreach (var assetLength in AllowedSuffixLengths)
            {
                var candidate = PlanMedia(profileId, displayLabel, profileStorageToken, assetId,
                    assetStorageToken, mediaType, extension, profileLength, assetLength);
                if (!conflicts(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new ManagedPathPlanningException(PathConflictCode,
            "All deterministic ID suffixes conflict with another entity or unknown bytes.");
    }

    private string FitHumanSegment(string value, string fixedSuffix, params string[] parentSegments)
    {
        if (_vaultRoot is null)
        {
            return value;
        }

        var prefix = Path.Combine([_vaultRoot, .. parentSegments]);
        var available = MaximumAbsolutePathCodeUnits - prefix.Length - 1 - fixedSuffix.Length;
        if (available < 1)
        {
            throw TooLong();
        }

        var starts = StringInfo.ParseCombiningCharacters(value);
        while (value.Length > available && starts.Length > 0)
        {
            value = value[..starts[^1]];
            starts = StringInfo.ParseCombiningCharacters(value);
        }

        value = value.TrimEnd('.', ' ');
        if (value.Length == 0)
        {
            throw TooLong();
        }

        return value;
    }

    private void EnsureWithinBudget(string relativePath)
    {
        if (_vaultRoot is not null
            && Path.Combine(_vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)).Length > MaximumAbsolutePathCodeUnits)
        {
            throw TooLong();
        }
    }

    private static ManagedPathPlanningException TooLong() => new(
        PathTooLongCode,
        $"The Vault root is too long for a managed path limited to {MaximumAbsolutePathCodeUnits} UTF-16 code units.");

    private static string GetMediaFolder(MediaType mediaType) => mediaType switch
    {
        MediaType.Image => "Images",
        MediaType.Video => "Videos",
        MediaType.Model => "Models",
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, null),
    };

    private static string GetMediaRole(MediaType mediaType) => mediaType switch
    {
        MediaType.Image => "image",
        MediaType.Video => "video",
        MediaType.Model => "model",
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, null),
    };

    private static string IdSuffix(Guid id, int length) => id.ToString("N")[..length].ToLowerInvariant();

    private static void ValidateSuffixLength(int length)
    {
        if (!AllowedSuffixLengths.Contains(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Suffix length must be 8, 12, 16, 20, 24, 28, or 32.");
        }
    }

    private static void EnsureSafeComponentPath(string path)
    {
        if (Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A model component path must be relative.", nameof(path));
        }

        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("A model component path cannot contain traversal.", nameof(path));
        }
    }

    private static string NormalizeComponentPath(string path) =>
        string.Join('/', path.Normalize(NormalizationForm.FormC).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

    private static string CombineRelative(params string[] segments) => string.Join('/', segments);
    private static void Validate(ProfileStorageToken token) => _ = new ProfileStorageToken(token.Value);
    private static void Validate(MediaStorageToken token) => _ = new MediaStorageToken(token.Value);

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A managed path requires a non-empty stable identifier.", parameterName);
        }
    }
}

public sealed record ManagedModelPackagePlan(
    Guid MediaId,
    string PackageDirectoryRelativePath,
    string PrimaryManagedRelativePath,
    string PrimaryFileName);

public sealed class ManagedPathPlanningException : InvalidOperationException
{
    public ManagedPathPlanningException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}
