using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Neuterradise.Release.Contracts;

public sealed record ManifestAuthorityFile(
    string RelativePath,
    long ByteLength,
    string Sha256,
    string? Role);

public static class UpdateManifestAuthority
{
    public static string ComputeSha256(
        int schemaVersion,
        string productId,
        string productVersion,
        string runtimeIdentifier,
        long payloadByteLength,
        string payloadSha256,
        string? minimumCompatibleVersion,
        IEnumerable<ManifestAuthorityFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var normalizedFiles = files
            .Select(file => new ManifestAuthorityFile(
                ReleaseContract.NormalizeRelativePath(file.RelativePath),
                file.ByteLength,
                file.Sha256,
                file.Role))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

        var authority = new
        {
            schemaVersion,
            productId,
            productVersion,
            runtimeIdentifier,
            payloadByteLength,
            payloadSha256,
            minimumCompatibleVersion,
            files = normalizedFiles,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            authority,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static bool IsLowerSha256(string? value) =>
        value is { Length: 64 }
        && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
