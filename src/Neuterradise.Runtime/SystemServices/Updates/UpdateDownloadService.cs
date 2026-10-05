using System.Net.Http;
using System.Security.Cryptography;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Updates;

public sealed class UpdateDownloadService
{
    private const long MaxPayloadBytes = 8L * 1024 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly AppStatePaths _appState;

    public UpdateDownloadService(HttpClient http, AppStatePaths appState)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        Uri source,
        Guid operationId,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsAbsoluteUri
            || !string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(source.UserInfo)
            || !string.IsNullOrEmpty(source.Fragment))
        {
            return UpdateDownloadResult.Rejected(
                "Download source must be an absolute HTTPS URI without user info or fragment.");
        }

        if (operationId == Guid.Empty)
            throw new ArgumentException("Operation id is required.", nameof(operationId));
        if (expectedLength < 0 || expectedLength > MaxPayloadBytes)
            return UpdateDownloadResult.Rejected("Payload length exceeds the safe bound.");
        if (string.IsNullOrWhiteSpace(expectedSha256)
            || expectedSha256.Length != 64
            || expectedSha256.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f')))
            return UpdateDownloadResult.Rejected("Payload hash is invalid.");

        var directory = _appState.GetUpdateToolsPath(operationId);
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, "payload.partial");
        var completed = Path.Combine(directory, "payload.zip");
        var completedSuccessfully = false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var effectiveSource = response.RequestMessage?.RequestUri;
            if (effectiveSource is null || !UpdateUriPolicy.IsTrustedEffectiveUri(source, effectiveSource))
            {
                return UpdateDownloadResult.Rejected(
                    "Payload download redirected outside the trusted update authority.");
            }

            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long declaredLength)
            {
                if (declaredLength > MaxPayloadBytes)
                    return UpdateDownloadResult.Rejected("Payload is too large.");
                if (declaredLength != expectedLength)
                    return UpdateDownloadResult.Rejected("Payload Content-Length does not match the manifest.");
            }

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                partial,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    total = checked(total + read);
                    if (total > expectedLength || total > MaxPayloadBytes)
                        return UpdateDownloadResult.Rejected("Downloaded payload exceeded the manifest byte bound.");

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (total != expectedLength)
                    return UpdateDownloadResult.Rejected("Downloaded payload length does not match the manifest.");
            }

            await using (var stream = new FileStream(
                partial,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var hash = Convert.ToHexString(
                        await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                    .ToLowerInvariant();
                if (!string.Equals(hash, expectedSha256, StringComparison.Ordinal))
                    return UpdateDownloadResult.Rejected("Downloaded payload hash does not match the manifest.");
            }

            File.Move(partial, completed, overwrite: true);
            completedSuccessfully = true;
            return new UpdateDownloadResult(true, completed, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UpdateDownloadResult.Cancelled();
        }
        catch (HttpRequestException)
        {
            return UpdateDownloadResult.Rejected("Payload download failed; Vault was not touched.");
        }
        catch (IOException)
        {
            return UpdateDownloadResult.Rejected("Payload staging failed; Vault was not touched.");
        }
        catch (OverflowException)
        {
            return UpdateDownloadResult.Rejected("Downloaded payload exceeded the safe byte bound.");
        }
        finally
        {
            if (!completedSuccessfully)
                TryDelete(partial);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed record UpdateDownloadResult(bool IsSuccess, string? PayloadPath, string? SafeError)
{
    public static UpdateDownloadResult Rejected(string error) => new(false, null, error);
    public static UpdateDownloadResult Cancelled() => new(false, null, "Payload download cancelled.");
}
