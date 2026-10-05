using System.Security.Cryptography;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.Release.Contracts;

namespace Neuterradise.App.SystemServices.Updates;

public sealed class UpdatePackageValidator
{
    private readonly InstallPaths _install;

    public UpdatePackageValidator(InstallPaths install) =>
        _install = install ?? throw new ArgumentNullException(nameof(install));

    public UpdatePackageValidationResult Validate(UpdateManifest manifest, string payloadRoot)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);

        try
        {
            manifest.Validate();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return UpdatePackageValidationResult.Reject(exception.Message);
        }

        var root = Path.GetFullPath(payloadRoot);
        if (!Directory.Exists(root))
            return UpdatePackageValidationResult.Reject("Payload root is unavailable.");

        var manifestFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var normalizedRelativePath = NormalizeRelativePath(file.RelativePath);
            if (!manifestFiles.Add(normalizedRelativePath))
                return UpdatePackageValidationResult.Reject("Update manifest contains duplicate file membership.");

            string path;
            try
            {
                path = RootPathRules.ResolveContainedPath(root, root, normalizedRelativePath, nameof(payloadRoot));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return UpdatePackageValidationResult.Reject("Payload contains an unsafe path.");
            }

            if (!File.Exists(path))
                return UpdatePackageValidationResult.Reject("Payload membership is incomplete.");

            var info = new FileInfo(path);
            if (info.Length != file.ByteLength
                || !string.Equals(ComputeSha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return UpdatePackageValidationResult.Reject("Payload file integrity does not match its manifest.");
            }
        }

        ReleaseContractDocument contract;
        try
        {
            contract = ReleaseContract.Current;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return UpdatePackageValidationResult.Reject($"Release contract is invalid: {exception.Message}");
        }

        if (!string.Equals(contract.ProductId, manifest.ProductId, StringComparison.Ordinal)
            || !string.Equals(contract.RuntimeIdentifier, manifest.RuntimeIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePackageValidationResult.Reject("Update manifest contradicts the canonical release contract identity.");
        }

        var legacyLayout = ReleaseLayout.IsLegacyPayload(manifestFiles);
        foreach (var requiredMember in contract.RequiredMembers)
        {
            if (!manifestFiles.Contains(ReleaseLayout.RequiredMember(requiredMember, legacyLayout)))
                return UpdatePackageValidationResult.Reject($"Payload is missing required release member '{requiredMember}'.");
        }

        foreach (var requiredFileName in contract.RequiredUniqueFileNames)
        {
            var matches = manifestFiles.Count(path =>
                string.Equals(Path.GetFileName(path), requiredFileName, StringComparison.OrdinalIgnoreCase));
            if (matches != 1)
                return UpdatePackageValidationResult.Reject($"Payload must contain exactly one required runtime member named '{requiredFileName}'.");
        }

        if (!manifestFiles.Contains(ReleaseLayout.LauncherName(legacyLayout))
            || manifestFiles.Any(path => !ReleaseLayout.IsManifestPayloadMember(path, legacyLayout)))
        {
            return UpdatePackageValidationResult.Reject(
                "Payload layout must contain one supported root launcher and its matching runtime directory.");
        }

        if (manifestFiles.Any(path =>
                path.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("Vault/", StringComparison.OrdinalIgnoreCase)
                || IsDatabasePayloadPath(path)))
        {
            return UpdatePackageValidationResult.Reject("Payload contains source, test, user-data, or database content.");
        }

        if (!TryEnumeratePayloadFiles(root, out var actualFiles, out var enumerationError))
            return UpdatePackageValidationResult.Reject(enumerationError!);

        var expectedExtractedFiles = new HashSet<string>(manifestFiles, StringComparer.OrdinalIgnoreCase)
        {
            "release-manifest.json"
        };
        if (!actualFiles.SetEquals(expectedExtractedFiles))
            return UpdatePackageValidationResult.Reject("Extracted payload membership does not exactly match the approved update manifest plus its control manifest.");

        var rootFiles = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path) ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rootDirectories = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path) ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (rootFiles.Count != 2
            || !rootFiles.Contains(ReleaseLayout.LauncherName(legacyLayout))
            || !rootFiles.Contains(ReleaseLayout.ReleaseManifestFileName)
            || rootDirectories.Count != 1
            || !rootDirectories.Contains(ReleaseLayout.RuntimeDirectory(legacyLayout)))
        {
            return UpdatePackageValidationResult.Reject(
                "Payload root must contain only its supported launcher, release-manifest.json, and matching runtime directory.");
        }

        string embeddedManifestPath;
        try
        {
            embeddedManifestPath = RootPathRules.ResolveContainedPath(root, root, "release-manifest.json", nameof(payloadRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return UpdatePackageValidationResult.Reject("Embedded manifest path is unsafe.");
        }

        var embeddedInfo = new FileInfo(embeddedManifestPath);
        if (!embeddedInfo.Exists)
            return UpdatePackageValidationResult.Reject("Embedded release-manifest.json is missing.");
        if (embeddedInfo.Length > 2 * 1024 * 1024)
            return UpdatePackageValidationResult.Reject("Embedded release manifest exceeds safe limit.");

        string embeddedJson;
        try
        {
            using var stream = new FileStream(embeddedManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            embeddedJson = reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return UpdatePackageValidationResult.Reject($"Embedded release manifest could not be read: {exception.Message}");
        }

        UpdateReleaseManifest embeddedManifest;
        try
        {
            embeddedManifest = UpdateReleaseManifest.Parse(embeddedJson);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or System.Text.Json.JsonException)
        {
            return UpdatePackageValidationResult.Reject($"Embedded release manifest is invalid: {exception.Message}");
        }

        if (!string.Equals(embeddedManifest.ProductId, manifest.ProductId, StringComparison.Ordinal)
            || !string.Equals(embeddedManifest.ProductVersion, manifest.ProductVersion, StringComparison.Ordinal)
            || !string.Equals(embeddedManifest.RuntimeIdentifier, manifest.RuntimeIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePackageValidationResult.Reject("Embedded release manifest identity contradicts approved update metadata.");
        }

        if (embeddedManifest.Files.Count != manifest.Files.Count)
            return UpdatePackageValidationResult.Reject("Embedded release manifest file count contradicts approved update manifest.");

        var approvedByPath = manifest.Files.ToDictionary(
            file => NormalizeRelativePath(file.RelativePath),
            file => file,
            StringComparer.OrdinalIgnoreCase);

        foreach (var embeddedFile in embeddedManifest.Files)
        {
            var embeddedKey = NormalizeRelativePath(embeddedFile.RelativePath);
            if (!approvedByPath.TryGetValue(embeddedKey, out var approvedFile))
                return UpdatePackageValidationResult.Reject($"Embedded release manifest contains unexpected file '{embeddedFile.RelativePath}'.");
            if (embeddedFile.ByteLength != approvedFile.ByteLength
                || !string.Equals(embeddedFile.Sha256, approvedFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return UpdatePackageValidationResult.Reject($"Embedded release manifest entry for '{embeddedFile.RelativePath}' contradicts approved file identity.");
            }
        }

        return new UpdatePackageValidationResult(true, null);
    }

    private static bool TryEnumeratePayloadFiles(
        string root,
        out HashSet<string> files,
        out string? error)
    {
        files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        var pending = new Queue<string>();
        pending.Enqueue(root);

        try
        {
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                foreach (var directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        error = "Extracted payload contains a reparse-point directory.";
                        return false;
                    }

                    pending.Enqueue(directory);
                }

                foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    {
                        error = "Extracted payload contains a reparse-point file.";
                        return false;
                    }

                    var relative = NormalizeRelativePath(Path.GetRelativePath(root, file));
                    if (!files.Add(relative))
                    {
                        error = "Extracted payload contains duplicate normalized file membership.";
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = "Extracted payload membership could not be enumerated safely.";
            return false;
        }
    }

    private static bool IsDatabasePayloadPath(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sqlite-wal", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sqlite-shm", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sqlite3-wal", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sqlite3-shm", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public sealed record UpdatePackageValidationResult(bool IsAccepted, string? SafeError)
{
    public static UpdatePackageValidationResult Reject(string error) => new(false, error);
}
