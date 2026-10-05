using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Neuterradise.Release.Contracts;

public sealed record UpdatePublisherKeyContract(
    string KeyId,
    string Algorithm,
    string PublicKeyPem,
    bool Revoked);

public sealed record ReleaseContractDocument(
    int SchemaVersion,
    string ProductId,
    string RuntimeIdentifier,
    string ModelsRelativeRoot,
    IReadOnlyList<string> RequiredMembers,
    IReadOnlyList<string> RequiredUniqueFileNames,
    string FfmpegMirrorUrl,
    string UpdatePublisherSigningKeyId,
    IReadOnlyList<UpdatePublisherKeyContract> UpdatePublisherKeys);

public static class ReleaseContract
{
    public const string UpdatePublisherAlgorithm = "ECDSA_P256_SHA256";
    private const string ResourceName = "Neuterradise.Release.Contracts.release-contract.json";
    private static readonly Lazy<ReleaseContractDocument> Authority =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static ReleaseContractDocument Current => Authority.Value;

    public static string NormalizeRelativePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Replace('\\', '/').Trim();
        if (Path.IsPathRooted(normalized)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.Contains('\0'))
        {
            throw new FormatException("Release contract contains an unsafe relative path.");
        }

        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length == 0
            || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".."))
        {
            throw new FormatException("Release contract contains an unsafe relative path segment.");
        }

        return string.Join('/', segments);
    }

    private static ReleaseContractDocument Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded release-contract.json is missing.");
        var document = JsonSerializer.Deserialize<ReleaseContractDocument>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new FormatException("Release contract is empty.");

        if (document.SchemaVersion != 1)
            throw new FormatException("Unsupported release contract schema.");
        if (string.IsNullOrWhiteSpace(document.ProductId)
            || string.IsNullOrWhiteSpace(document.RuntimeIdentifier))
        {
            throw new FormatException("Release contract identity is incomplete.");
        }

        var modelsRoot = NormalizeRelativePath(document.ModelsRelativeRoot);
        var required = ValidateUnique(document.RequiredMembers, NormalizeRelativePath, "required member");
        if (!required.Contains(
                ReleaseLayout.LauncherExecutableName,
                StringComparer.OrdinalIgnoreCase)
            || required.Any(path => !ReleaseLayout.IsManifestPayloadMember(path)))
        {
            throw new FormatException(
                "Release contract members must contain the root launcher and otherwise live under runtime/.");
        }

        var requiredNames = ValidateUnique(
            document.RequiredUniqueFileNames,
            value =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
                var trimmed = value.Trim();
                if (trimmed.Contains(Path.DirectorySeparatorChar)
                    || trimmed.Contains(Path.AltDirectorySeparatorChar)
                    || trimmed.Contains(':', StringComparison.Ordinal)
                    || trimmed.Contains('\0'))
                {
                    throw new FormatException("Release contract required file name is unsafe.");
                }

                return trimmed;
            },
            "required file name");

        if (!Uri.TryCreate(document.FfmpegMirrorUrl, UriKind.Absolute, out var mirror)
            || !string.Equals(mirror.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(mirror.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Release contract FFmpeg mirror URL is invalid.");
        }

        var publisherKeys = ValidatePublisherKeys(document.UpdatePublisherKeys);
        var signingKeyId = document.UpdatePublisherSigningKeyId?.Trim();
        if (string.IsNullOrWhiteSpace(signingKeyId))
            throw new FormatException("Release contract update publisher signing key id is missing.");

        var signingKey = publisherKeys.SingleOrDefault(
            key => string.Equals(key.KeyId, signingKeyId, StringComparison.Ordinal));
        if (signingKey is null)
            throw new FormatException("Release contract signing key id does not resolve to a pinned publisher key.");
        if (signingKey.Revoked)
            throw new FormatException("Release contract signing key is revoked.");

        return document with
        {
            ModelsRelativeRoot = modelsRoot,
            RequiredMembers = required,
            RequiredUniqueFileNames = requiredNames,
            FfmpegMirrorUrl = mirror.AbsoluteUri,
            UpdatePublisherSigningKeyId = signingKeyId,
            UpdatePublisherKeys = publisherKeys,
        };
    }

    private static IReadOnlyList<UpdatePublisherKeyContract> ValidatePublisherKeys(
        IReadOnlyList<UpdatePublisherKeyContract>? values)
    {
        if (values is null || values.Count == 0)
            throw new FormatException("Release contract update publisher key list is empty.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<UpdatePublisherKeyContract>(values.Count);
        foreach (var value in values)
        {
            if (value is null)
                throw new FormatException("Release contract contains a null update publisher key.");

            var keyId = value.KeyId?.Trim();
            if (string.IsNullOrWhiteSpace(keyId)
                || keyId.Length > 128
                || keyId.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character)
                      || character is '-' or '_' or '.')))
            {
                throw new FormatException("Release contract update publisher key id is invalid.");
            }

            if (!seen.Add(keyId))
                throw new FormatException("Release contract contains a duplicate update publisher key id.");
            if (!string.Equals(value.Algorithm, UpdatePublisherAlgorithm, StringComparison.Ordinal))
                throw new FormatException("Release contract update publisher algorithm is unsupported.");
            if (string.IsNullOrWhiteSpace(value.PublicKeyPem))
                throw new FormatException("Release contract update publisher public key is missing.");

            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(value.PublicKeyPem);
                if (ecdsa.KeySize != 256)
                    throw new FormatException("Release contract update publisher key is not P-256.");
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or CryptographicException)
            {
                throw new FormatException("Release contract update publisher public key is invalid.", exception);
            }

            result.Add(value with
            {
                KeyId = keyId,
                Algorithm = UpdatePublisherAlgorithm,
                PublicKeyPem = value.PublicKeyPem.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n",
            });
        }

        return result;
    }

    private static IReadOnlyList<string> ValidateUnique(
        IReadOnlyList<string>? values,
        Func<string, string> normalize,
        string label)
    {
        if (values is null || values.Count == 0)
            throw new FormatException($"Release contract {label} list is empty.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            var normalized = normalize(value);
            if (!seen.Add(normalized))
                throw new FormatException($"Release contract contains a duplicate {label}.");
            result.Add(normalized);
        }

        return result;
    }
}
