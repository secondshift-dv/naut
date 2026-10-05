using System.Security.Cryptography;
using System.Text;

namespace Neuterradise.App.SystemServices.Storage;

public sealed class StorageTokenAllocator
{
    private const int _profileInitialHexLength = 6;
    private const int _assetInitialHexLength = 8;
    private const int _maximumHexLength = 64;

    public ProfileStorageToken GetProfileCandidate(Guid profileId, int collisionAttempt)
    {
        var digest = CreateDigest(profileId);
        var length = GetCandidateLength(_profileInitialHexLength, collisionAttempt);
        return new ProfileStorageToken($"P-{digest[..length]}");
    }

    public MediaStorageToken GetMediaCandidate(Guid assetId, int collisionAttempt)
    {
        var digest = CreateDigest(assetId);
        var length = GetCandidateLength(_assetInitialHexLength, collisionAttempt);
        return new MediaStorageToken($"A-{digest[..length]}");
    }

    public ProfileStorageToken AllocateProfile(
        Guid profileId,
        Func<ProfileStorageToken, bool> isAssigned)
    {
        ArgumentNullException.ThrowIfNull(isAssigned);
        for (var attempt = 0; ; attempt++)
        {
            var candidate = GetProfileCandidate(profileId, attempt);
            if (!isAssigned(candidate))
            {
                return candidate;
            }
        }
    }

    public MediaStorageToken AllocateMedia(
        Guid assetId,
        Func<MediaStorageToken, bool> isAssigned)
    {
        ArgumentNullException.ThrowIfNull(isAssigned);
        for (var attempt = 0; ; attempt++)
        {
            var candidate = GetMediaCandidate(assetId, attempt);
            if (!isAssigned(candidate))
            {
                return candidate;
            }
        }
    }

    public Func<int, string> CreateProfileCandidateProvider(Guid profileId) =>
        attempt => GetProfileCandidate(profileId, attempt).Value;

    public Func<int, string> CreateMediaCandidateProvider(Guid assetId) =>
        attempt => GetMediaCandidate(assetId, attempt).Value;

    private static string CreateDigest(Guid entityId)
    {
        if (entityId == Guid.Empty)
        {
            throw new ArgumentException("A storage token requires a non-empty stable identifier.", nameof(entityId));
        }

        var canonicalId = entityId.ToString("D").ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalId)));
    }

    private static int GetCandidateLength(int initialLength, int collisionAttempt)
    {
        var maximumAttempt = (_maximumHexLength - initialLength) / 2;
        if (collisionAttempt < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(collisionAttempt));
        }

        if (collisionAttempt > maximumAttempt)
        {
            throw new InvalidOperationException(
                "Every deterministic storage-token candidate for the stable identifier is already assigned.");
        }

        return initialLength + (collisionAttempt * 2);
    }
}
