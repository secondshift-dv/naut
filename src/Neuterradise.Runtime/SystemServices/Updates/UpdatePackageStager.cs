using System.IO.Compression;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Updates;

public sealed class UpdatePackageStager
{
    private const int MaxEntries = 100_000;
    private const long MaxEntryBytes = 512L * 1024 * 1024;
    private const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;

    private readonly AppStatePaths _appState;
    private readonly UpdatePackageValidator _validator;

    public UpdatePackageStager(AppStatePaths appState, UpdatePackageValidator validator)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<UpdatePackageValidationResult> ExtractAndValidateAsync(
        string archivePath,
        Guid operationId,
        UpdateManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An update operation id is required.", nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(manifest);

        var operationName = operationId.ToString("D");
        var staging = _appState.ResolveContainedPath(AppStatePathArea.UpdateStaging, operationName);
        if (Directory.Exists(staging) || File.Exists(staging))
        {
            return UpdatePackageValidationResult.Reject(
                "Update operation staging already exists; retry with a new operation id.");
        }

        var writtenFiles = new List<string>();
        try
        {
            Directory.CreateDirectory(staging);

            await using var archiveStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);

            if (archive.Entries.Count > MaxEntries)
            {
                return RejectAndCleanup(
                    "Update archive contains too many entries.",
                    writtenFiles);
            }

            long total = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rawName = entry.FullName.Replace('\\', '/');
                var isDirectoryEntry = rawName.EndsWith("/", StringComparison.Ordinal);
                var normalized = isDirectoryEntry ? rawName.TrimEnd('/') : rawName;
                if (string.IsNullOrWhiteSpace(normalized)
                    || Path.IsPathRooted(normalized)
                    || normalized.Contains("../", StringComparison.Ordinal)
                    || normalized.Contains(":", StringComparison.Ordinal)
                    || normalized.Contains('\0'))
                {
                    return RejectAndCleanup(
                        "Update archive contains an unsafe entry.",
                        writtenFiles);
                }

                // Canonical names intentionally ignore a directory entry's trailing slash so
                // "a/b/" and "a/b" cannot describe two different archive objects.
                if (!names.Add(normalized))
                {
                    return RejectAndCleanup(
                        "Update archive contains duplicate canonical entries.",
                        writtenFiles);
                }

                if (entry.Length < 0
                    || entry.Length > MaxEntryBytes
                    || (entry.CompressedLength > 0
                        && entry.Length / (double)entry.CompressedLength > 100))
                {
                    return RejectAndCleanup(
                        "Update archive entry exceeds safety bounds.",
                        writtenFiles);
                }

                if (isDirectoryEntry)
                {
                    if (entry.Length != 0)
                    {
                        return RejectAndCleanup(
                            "Update archive directory entry contains file data.",
                            writtenFiles);
                    }

                    var directory = ResolveArchiveDestination(staging, normalized, operationId);
                    Directory.CreateDirectory(directory);

                    // Re-resolve after creation so a concurrently introduced reparse point
                    // cannot silently convert a valid directory entry into an escape.
                    _ = ResolveArchiveDestination(staging, normalized, operationId);
                    continue;
                }

                total = checked(total + entry.Length);
                if (total > MaxTotalBytes)
                {
                    return RejectAndCleanup(
                        "Update archive exceeds the total extraction bound.",
                        writtenFiles);
                }

                var destination = ResolveArchiveDestination(staging, normalized, operationId);

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                // Repeat the explicit canonical containment proof immediately before mutation,
                // after parent creation, then apply the shared reparse-aware authority as well.
                destination = ResolveArchiveDestination(staging, normalized, operationId);

                await using var input = entry.Open();
                await using var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                writtenFiles.Add(destination);
                await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            }

            var validation = _validator.Validate(manifest, staging);
            if (!validation.IsAccepted)
            {
                CleanupWrittenFiles(writtenFiles);
            }

            return validation;
        }
        catch (OperationCanceledException)
        {
            CleanupWrittenFiles(writtenFiles);
            throw;
        }
        catch (ArgumentException ex)
        {
            CleanupWrittenFiles(writtenFiles);
            return UpdatePackageValidationResult.Reject(
                $"Update archive staging rejected an unsafe path: {ex.Message}");
        }
        catch (InvalidDataException)
        {
            CleanupWrittenFiles(writtenFiles);
            return UpdatePackageValidationResult.Reject("Update archive is not a valid ZIP payload.");
        }
        catch (IOException ex)
        {
            CleanupWrittenFiles(writtenFiles);
            return UpdatePackageValidationResult.Reject($"Update archive staging failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            CleanupWrittenFiles(writtenFiles);
            return UpdatePackageValidationResult.Reject($"Update archive staging access was denied: {ex.Message}");
        }
    }

    private static string ResolveArchiveDestination(string stagingRoot, string normalizedEntryName, Guid operationId)
    {
        if (string.IsNullOrWhiteSpace(normalizedEntryName)
            || Path.IsPathRooted(normalizedEntryName)
            || normalizedEntryName.StartsWith("/", StringComparison.Ordinal)
            || normalizedEntryName.Contains(':', StringComparison.Ordinal)
            || normalizedEntryName.Contains('\0'))
        {
            throw new ArgumentException("Update archive entry is not a safe relative path.", nameof(normalizedEntryName));
        }

        var segments = normalizedEntryName.Split('/', StringSplitOptions.None);
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".."))
        {
            throw new ArgumentException("Update archive entry contains an unsafe path segment.", nameof(normalizedEntryName));
        }

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
        var platformRelative = string.Join(Path.DirectorySeparatorChar.ToString(), segments);
        var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, platformRelative));
        var rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Update archive entry escapes the operation staging root.", nameof(normalizedEntryName));

        var sharedAuthority = RootPathRules.ResolveContainedPath(
            canonicalRoot,
            canonicalRoot,
            normalizedEntryName,
            nameof(operationId));
        if (!string.Equals(candidate, sharedAuthority, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Update archive containment authorities disagree.");

        return candidate;
    }

    private static UpdatePackageValidationResult RejectAndCleanup(
        string error,
        IEnumerable<string> writtenFiles)
    {
        CleanupWrittenFiles(writtenFiles);
        return UpdatePackageValidationResult.Reject(error);
    }

    private static void CleanupWrittenFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Best-effort cleanup is deliberately limited to files created by this invocation.
            }
        }
    }
}
