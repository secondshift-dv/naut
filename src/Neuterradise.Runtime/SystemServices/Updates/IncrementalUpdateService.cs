using System.Security.Cryptography;
using System.Text.Json;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Updates;

public sealed record IncrementalUpdatePlan(IReadOnlyList<UpdateManifestFile> ReusedFiles,
    IReadOnlyList<UpdateManifestFile> DownloadFiles, long DownloadBytes);

public sealed record IncrementalUpdateResult(bool IsSuccess, bool CanFallback, string? SafeError);

/// <summary>
/// Consumes only a publisher-authenticated manifest. The protocol marker is itself a signed
/// payload member; remote filenames are derived exclusively from signed lowercase content hashes.
/// Reuse is a copy into complete isolated staging, never an in-place installation mutation.
/// </summary>
public sealed class IncrementalUpdateService(AppStatePaths appState, InstallPaths install,
    UpdateDownloadService download, UpdatePackageValidator validator)
{
    public const string MarkerPath = "runtime/deployment/incremental-update.json";
    public const string MetadataName = "update-incremental.json";
    public static string AssetName(string sha256) => $"update-file-{sha256}.bin";

    public static async Task<IncrementalUpdatePlan> PlanAsync(UpdateManifest manifest, string installRoot,
        CancellationToken cancellationToken = default)
    {
        manifest.Validate();
        if (manifest.Files.Count == 0 || manifest.Files.Any(file => file.ByteLength > 512L * 1024 * 1024)
            || manifest.Files.Sum(file => file.ByteLength) > 4L * 1024 * 1024 * 1024)
            throw new FormatException("Incremental target exceeds staging bounds.");
        var reused = new List<UpdateManifestFile>();
        var required = new List<UpdateManifestFile>();
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = RootPathRules.ResolveContainedPath(installRoot, installRoot, file.RelativePath, nameof(installRoot));
            if (await MatchesAsync(path, file, cancellationToken).ConfigureAwait(false)) reused.Add(file);
            else required.Add(file);
        }
        return new(reused, required, required.Sum(file => file.ByteLength));
    }

    public async Task<IncrementalUpdateResult> StageAsync(Uri feed, UpdateManifest manifest, Guid operationId,
        IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        manifest.Validate();
        var marker = manifest.Files.SingleOrDefault(file => file.RelativePath == MarkerPath);
        if (marker is null) return new(false, true, "Target does not offer incremental transfer.");
        if (marker.ByteLength > 4096) return new(false, false, "Signed incremental metadata exceeds its safe bound.");
        var written = new List<string>();
        var staging = appState.ResolveContainedPath(AppStatePathArea.UpdateStaging, operationId.ToString("D"));
        if (Directory.Exists(staging) || File.Exists(staging))
            return new(false, false, "Incremental operation staging already exists.");
        try
        {
            progress?.Report(new(UpdatePhase.Planning, 0, null, 0, manifest.Files.Count));
            var metadata = await download.DownloadAsync(new Uri(feed, MetadataName), Guid.NewGuid(),
                marker.ByteLength, marker.Sha256, cancellationToken).ConfigureAwait(false);
            if (!metadata.IsSuccess) return new(false, metadata.CanFallback, metadata.SafeError);
            try
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(metadata.PayloadPath!, cancellationToken).ConfigureAwait(false));
                if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Count() != 1 || !json.RootElement.TryGetProperty("schemaVersion", out var schema)
                    || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var protocol))
                    return new(false, false, "Signed incremental metadata is malformed.");
                if (protocol != 1) return new(false, true, "Incremental protocol is unsupported.");
            }
            finally { File.Delete(metadata.PayloadPath!); }

            var plan = await PlanAsync(manifest, install.Root, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(staging);
            foreach (var file in plan.ReusedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = RootPathRules.ResolveContainedPath(install.Root, install.Root, file.RelativePath, nameof(install));
                var destination = Destination(staging, file.RelativePath);
                written.Add(destination);
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                if (!await MatchesAsync(destination, file, cancellationToken).ConfigureAwait(false))
                    return new(false, true, "Local reuse changed during preparation.");
            }

            long transferred = 0;
            var completedFiles = 0;
            progress?.Report(new(UpdatePhase.Downloading, 0, plan.DownloadBytes, 0, plan.DownloadFiles.Count));
            foreach (var file in plan.DownloadFiles)
            {
                var fileProgress = new UpdateProgressCallback(value =>
                {
                    if (value.Phase == UpdatePhase.Downloading)
                        progress?.Report(new(UpdatePhase.Downloading, transferred + value.CompletedBytes,
                            plan.DownloadBytes, completedFiles, plan.DownloadFiles.Count));
                });
                var downloaded = await download.DownloadAsync(new Uri(feed, AssetName(file.Sha256)), Guid.NewGuid(),
                    file.ByteLength, file.Sha256, cancellationToken, fileProgress).ConfigureAwait(false);
                if (!downloaded.IsSuccess) return new(false, downloaded.CanFallback, downloaded.SafeError);
                var destination = Destination(staging, file.RelativePath);
                written.Add(destination);
                try { File.Move(downloaded.PayloadPath!, destination); }
                finally { if (File.Exists(downloaded.PayloadPath)) File.Delete(downloaded.PayloadPath!); }
                transferred = checked(transferred + file.ByteLength);
                completedFiles++;
                progress?.Report(new(UpdatePhase.Downloading, transferred, plan.DownloadBytes,
                    completedFiles, plan.DownloadFiles.Count));
            }

            // The signed file membership is also the control manifest's entire semantic authority.
            // Reconstructing it avoids a circular hash of the manifest that contains its own hash.
            var controlPath = Destination(staging, "release-manifest.json");
            written.Add(controlPath);
            await File.WriteAllTextAsync(controlPath, JsonSerializer.Serialize(new UpdateReleaseManifest(
                manifest.SchemaVersion, manifest.ProductId, manifest.ProductVersion, manifest.RuntimeIdentifier,
                manifest.Files), UpdateManifest.JsonOptions), cancellationToken).ConfigureAwait(false);
            progress?.Report(new(UpdatePhase.Verifying, 0, null, 0, manifest.Files.Count));
            var validation = validator.Validate(manifest, staging);
            if (!validation.IsAccepted) return new(false, false, validation.SafeError);
            written.Clear();
            return new(true, false, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException) { return new(false, false, "Signed incremental metadata is malformed."); }
        catch (FormatException exception) { return new(false, false, exception.Message); }
        catch (ArgumentException exception) { return new(false, false, exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new(false, true, "Incremental staging is unavailable; secure ZIP transfer is required."); }
        finally
        {
            foreach (var path in written)
            {
                try
                {
                    var relative = Path.GetRelativePath(staging, path);
                    var safe = RootPathRules.ResolveContainedPath(staging, staging, relative, nameof(staging));
                    if (File.Exists(safe)) File.Delete(safe);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
    }

    private static string Destination(string root, string relative)
    {
        var path = RootPathRules.ResolveContainedPath(root, root, relative, nameof(relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return RootPathRules.ResolveContainedPath(root, root, relative, nameof(relative));
    }

    private static async Task<bool> MatchesAsync(string path, UpdateManifestFile file, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return stream.Length == file.ByteLength && string.Equals(Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)), file.Sha256, StringComparison.Ordinal);
    }
}
