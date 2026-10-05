using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Neuterradise.App.SystemServices.Updates;

public sealed class UpdateCheckService
{
    private const int MaxMetadataBytes = 4 * 1024 * 1024;
    private const int MaxSignatureBytes = 64 * 1024;
    private readonly HttpClient _http;
    private readonly UpdateTrustPolicy _trust;
    private readonly UpdatePublisherSignatureVerifier _publisherVerifier;

    public UpdateCheckService(
        HttpClient http,
        UpdateTrustPolicy? trust = null,
        UpdatePublisherSignatureVerifier? publisherVerifier = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _trust = trust ?? new UpdateTrustPolicy();
        _publisherVerifier = publisherVerifier ?? new UpdatePublisherSignatureVerifier();
    }

    public async Task<UpdateCheckResult> CheckAsync(
        Uri feed,
        UpdateTrustConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.MatchesConfiguredFeed(feed))
            return UpdateCheckResult.Unavailable("Update feed does not match the configured HTTPS feed.");

        try
        {
            var metadataRead = await ReadTrustedAsync(
                feed,
                MaxMetadataBytes,
                "Update metadata",
                cancellationToken).ConfigureAwait(false);
            if (!metadataRead.Success || metadataRead.Bytes is null)
                return UpdateCheckResult.Unavailable(metadataRead.Error ?? "Update feed is unavailable.");

            var signatureUri = ResolvePublisherSignatureUri(feed);
            var signatureRead = await ReadTrustedAsync(
                signatureUri,
                MaxSignatureBytes,
                "Update publisher signature",
                cancellationToken).ConfigureAwait(false);
            if (!signatureRead.Success || signatureRead.Bytes is null)
                return UpdateCheckResult.Unavailable(
                    signatureRead.Error ?? "Update publisher signature is unavailable.");

            var publisherVerification = _publisherVerifier.Verify(
                metadataRead.Bytes,
                signatureRead.Bytes);
            if (!publisherVerification.Verified)
                return UpdateCheckResult.Unavailable(publisherVerification.Reason);

            var json = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                .GetString(metadataRead.Bytes);
            var manifest = UpdateManifest.Parse(json);
            var decision = _trust.Evaluate(
                manifest,
                configuration,
                isLocalPackage: false,
                userConfirmedLocalPackage: false,
                publisherVerified: true);

            if (decision.Accepted)
                return new UpdateCheckResult(true, manifest, decision, null, false);

            var isCurrent = string.Equals(
                    manifest.RuntimeIdentifier,
                    ProductIdentity.RuntimeId,
                    StringComparison.OrdinalIgnoreCase)
                && Version.TryParse(manifest.ProductVersion, out var candidateVersion)
                && Version.TryParse(ProductIdentity.Version, out var installedVersion)
                && candidateVersion <= installedVersion;

            return isCurrent
                ? UpdateCheckResult.Current()
                : UpdateCheckResult.Unavailable(decision.Reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Cancelled();
        }
        catch (HttpRequestException)
        {
            return UpdateCheckResult.Unavailable(
                "Update feed could not be reached; the offline application remains available.");
        }
        catch (DecoderFallbackException)
        {
            return UpdateCheckResult.Unavailable("Update metadata is not valid UTF-8.");
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Unavailable("Update metadata is invalid.");
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return UpdateCheckResult.Unavailable(exception.Message);
        }
    }

    private async Task<TrustedReadResult> ReadTrustedAsync(
        Uri requestedUri,
        int maximumBytes,
        string label,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(requestedUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return TrustedReadResult.Failed($"{label} is unavailable.");

        var effectiveUri = response.RequestMessage?.RequestUri;
        if (effectiveUri is null
            || !UpdateUriPolicy.IsTrustedEffectiveUri(requestedUri, effectiveUri))
        {
            return TrustedReadResult.Failed(
                $"{label} redirected outside the trusted update authority.");
        }

        if (response.Content.Headers.ContentLength is long declaredLength
            && declaredLength > maximumBytes)
        {
            return TrustedReadResult.Failed($"{label} exceeds the safe limit.");
        }

        var bytes = await ReadBoundedAsync(
            response.Content,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
        return bytes is null
            ? TrustedReadResult.Failed($"{label} exceeds the safe limit.")
            : TrustedReadResult.Succeeded(bytes);
    }

    private static Uri ResolvePublisherSignatureUri(Uri feed)
    {
        var signature = new Uri(feed, UpdatePublisherSignatureVerifier.SignatureFileName);
        if (!signature.IsAbsoluteUri
            || !string.Equals(
                signature.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(signature.UserInfo)
            || !string.IsNullOrEmpty(signature.Fragment))
        {
            throw new FormatException("Update publisher signature URI is invalid.");
        }

        return signature;
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[64 * 1024];
        var total = 0;

        while (true)
        {
            var read = await input
                .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            total = checked(total + read);
            if (total > maximumBytes)
                return null;

            await output
                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return output.ToArray();
    }

    private sealed record TrustedReadResult(
        bool Success,
        byte[]? Bytes,
        string? Error)
    {
        public static TrustedReadResult Succeeded(byte[] bytes) =>
            new(true, bytes, null);

        public static TrustedReadResult Failed(string error) =>
            new(false, null, error);
    }
}

public sealed record UpdateCheckResult(
    bool IsAvailable,
    UpdateManifest? Manifest,
    UpdateTrustDecision? Decision,
    string? SafeError,
    bool IsCurrent)
{
    public static UpdateCheckResult Unavailable(string error) =>
        new(false, null, null, error, false);

    public static UpdateCheckResult Current() =>
        new(false, null, null, null, true);

    public static UpdateCheckResult Cancelled() =>
        new(false, null, null, "Update check cancelled.", false);
}
