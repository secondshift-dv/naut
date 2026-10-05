using System.Security.Cryptography;
using System.Text.Json;
using Neuterradise.Release.Contracts;

namespace Neuterradise.App.SystemServices.Updates;

public sealed record UpdatePublisherVerification(
    bool Verified,
    string? KeyId,
    string Reason)
{
    public static UpdatePublisherVerification Reject(string reason) =>
        new(false, null, reason);

    public static UpdatePublisherVerification Accept(string keyId) =>
        new(true, keyId, "Update metadata publisher signature is valid.");
}

public sealed class UpdatePublisherSignatureVerifier
{
    public const string SignatureFileName = "update-signature.json";
    private const int CurrentSchemaVersion = 1;
    private const int MaximumSignatureDocumentBytes = 64 * 1024;

    public UpdatePublisherVerification Verify(
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> signatureDocumentBytes)
    {
        if (manifestBytes.IsEmpty)
            return UpdatePublisherVerification.Reject("Update metadata is empty.");
        if (signatureDocumentBytes.IsEmpty
            || signatureDocumentBytes.Length > MaximumSignatureDocumentBytes)
        {
            return UpdatePublisherVerification.Reject("Update publisher signature document is invalid.");
        }

        UpdatePublisherSignatureDocument signatureDocument;
        try
        {
            signatureDocument = JsonSerializer.Deserialize<UpdatePublisherSignatureDocument>(
                    signatureDocumentBytes,
                    JsonOptions)
                ?? throw new JsonException("Update publisher signature document is empty.");
        }
        catch (JsonException)
        {
            return UpdatePublisherVerification.Reject("Update publisher signature document is invalid.");
        }

        if (signatureDocument.SchemaVersion != CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(signatureDocument.KeyId)
            || !string.Equals(
                signatureDocument.Algorithm,
                ReleaseContract.UpdatePublisherAlgorithm,
                StringComparison.Ordinal)
            || !UpdateManifestAuthority.IsLowerSha256(signatureDocument.SignedPayloadSha256)
            || string.IsNullOrWhiteSpace(signatureDocument.SignatureBase64))
        {
            return UpdatePublisherVerification.Reject("Update publisher signature authority is invalid.");
        }

        var manifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        if (!string.Equals(
                manifestSha256,
                signatureDocument.SignedPayloadSha256,
                StringComparison.Ordinal))
        {
            return UpdatePublisherVerification.Reject("Update metadata changed after publisher signing.");
        }

        ReleaseContractDocument contract;
        try
        {
            contract = ReleaseContract.Current;
        }
        catch (Exception exception) when (
            exception is FormatException
            or InvalidOperationException
            or ArgumentException)
        {
            return UpdatePublisherVerification.Reject(
                $"Pinned update publisher authority is invalid: {exception.Message}");
        }

        var publisherKey = contract.UpdatePublisherKeys.SingleOrDefault(
            key => string.Equals(key.KeyId, signatureDocument.KeyId, StringComparison.Ordinal));
        if (publisherKey is null)
            return UpdatePublisherVerification.Reject("Update metadata was signed by an unknown publisher key.");
        if (publisherKey.Revoked)
            return UpdatePublisherVerification.Reject("Update metadata was signed by a revoked publisher key.");
        if (!string.Equals(
                publisherKey.Algorithm,
                signatureDocument.Algorithm,
                StringComparison.Ordinal))
        {
            return UpdatePublisherVerification.Reject("Update publisher signature algorithm does not match the pinned key.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureDocument.SignatureBase64);
        }
        catch (FormatException)
        {
            return UpdatePublisherVerification.Reject("Update publisher signature encoding is invalid.");
        }

        if (signature.Length != 64)
            return UpdatePublisherVerification.Reject("Update publisher signature length is invalid.");

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publisherKey.PublicKeyPem);
            if (ecdsa.KeySize != 256)
                return UpdatePublisherVerification.Reject("Pinned update publisher key is not P-256.");

            var verified = ecdsa.VerifyData(
                manifestBytes,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return verified
                ? UpdatePublisherVerification.Accept(publisherKey.KeyId)
                : UpdatePublisherVerification.Reject("Update publisher signature verification failed.");
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or CryptographicException)
        {
            return UpdatePublisherVerification.Reject(
                $"Pinned update publisher key could not verify the metadata: {exception.Message}");
        }
    }

    private sealed record UpdatePublisherSignatureDocument(
        int SchemaVersion,
        string KeyId,
        string Algorithm,
        string SignedPayloadSha256,
        string SignatureBase64);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
