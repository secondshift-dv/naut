using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Neuterradise.App.SystemServices.Updates;

/// <summary>
/// Reads the identity already embedded in a canonical naut ZIP and binds it to the exact ZIP
/// bytes selected by the user. It does not establish publisher identity; explicit user confirmation is
/// still required by UpdateTrustPolicy before the package may enter staging.
/// </summary>
public sealed class LocalUpdatePackageReader
{
    private const long MaxPayloadBytes = 8L * 1024 * 1024 * 1024;
    private const long MaxEmbeddedManifestBytes = 2L * 1024 * 1024;

    public async Task<LocalUpdatePackageReadResult> ReadAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return LocalUpdatePackageReadResult.Rejected("Select a naut ZIP package.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(archivePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return LocalUpdatePackageReadResult.Rejected("The selected update path is invalid.");
        }

        try
        {
            UpdateReleaseManifest embeddedManifest;
            await using var stream = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var payloadLength = stream.Length;
            if (payloadLength <= 0 || payloadLength > MaxPayloadBytes)
                return LocalUpdatePackageReadResult.Rejected("The selected update ZIP exceeds the safe size bound.");
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                var candidates = archive.Entries
                    .Where(entry => string.Equals(
                        entry.FullName.Replace('\\', '/'),
                        "release-manifest.json",
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (candidates.Length != 1)
                {
                    return LocalUpdatePackageReadResult.Rejected(
                        "The selected ZIP must contain exactly one release-manifest.json.");
                }

                var entry = candidates[0];
                if (entry.Length <= 0 || entry.Length > MaxEmbeddedManifestBytes)
                {
                    return LocalUpdatePackageReadResult.Rejected(
                        "The embedded release manifest exceeds the safe size bound.");
                }

                await using var manifestStream = entry.Open();
                using var reader = new StreamReader(
                    manifestStream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                    detectEncodingFromByteOrderMarks: true);
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                embeddedManifest = UpdateReleaseManifest.Parse(json);
            }

            // Parse and hash the same open file; deny writes and replacement during inspection.
            stream.Position = 0;
            var payloadSha256 = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));

            var manifest = new UpdateManifest(
                embeddedManifest.SchemaVersion,
                embeddedManifest.ProductId,
                embeddedManifest.ProductVersion,
                embeddedManifest.RuntimeIdentifier,
                payloadLength,
                payloadSha256,
                embeddedManifest.MinimumCompatibleVersion,
                embeddedManifest.Files);
            manifest.Validate();

            return new LocalUpdatePackageReadResult(true, manifest, fullPath, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DecoderFallbackException)
        {
            return LocalUpdatePackageReadResult.Rejected("The embedded release manifest is not valid UTF-8.");
        }
        catch (InvalidDataException)
        {
            return LocalUpdatePackageReadResult.Rejected("The selected file is not a valid naut ZIP package.");
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or FormatException
            or ArgumentException
            or System.Text.Json.JsonException)
        {
            return LocalUpdatePackageReadResult.Rejected(
                $"The selected update ZIP is invalid: {exception.Message}");
        }
    }
}

public sealed record LocalUpdatePackageReadResult(
    bool IsSuccess,
    UpdateManifest? Manifest,
    string? ArchivePath,
    string? SafeError)
{
    public static LocalUpdatePackageReadResult Rejected(string error) =>
        new(false, null, null, error);
}

public sealed record LocalUpdateInspectionResult(
    bool IsAccepted,
    string? ProductVersion,
    string? RuntimeIdentifier,
    string? PayloadSha256,
    string? SafeError)
{
    public static LocalUpdateInspectionResult Reject(string error) =>
        new(false, null, null, null, error);
}
