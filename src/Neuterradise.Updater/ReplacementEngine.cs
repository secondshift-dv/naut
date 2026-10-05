using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Neuterradise.Release.Contracts;
using System.Threading;
using System.Threading.Tasks;

namespace Neuterradise.Updater;

public sealed class ReplacementEngine
{
    public static async Task<ReplacementResult> ExecuteAsync(
        string handoffPath,
        string expectedHandoffSha256,
        string expectedManifestSha256,
        int? parentPid,
        bool launchApp,
        CancellationToken cancellationToken = default)
    {
        string normalizedHandoffPath;
        string operationDir;
        try
        {
            normalizedHandoffPath = Path.GetFullPath(handoffPath);
            operationDir = Path.GetDirectoryName(normalizedHandoffPath)
                ?? throw new InvalidOperationException("Handoff path has no operation directory.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ReplacementResult.Failed($"Invalid handoff path: {ex.Message}");
        }

        var recoveryPlanPath = Path.Combine(operationDir, "recovery-plan.json");

        HandoffData handoff;
        try
        {
            if (!UpdateManifestAuthority.IsLowerSha256(expectedHandoffSha256)
                || !UpdateManifestAuthority.IsLowerSha256(expectedManifestSha256))
            {
                return ReplacementResult.Failed("Updater authority digest format is invalid.");
            }

            var handoffBytes = await File.ReadAllBytesAsync(normalizedHandoffPath, cancellationToken);
            var actualHandoffSha256 = Convert.ToHexString(SHA256.HashData(handoffBytes)).ToLowerInvariant();
            if (!string.Equals(actualHandoffSha256, expectedHandoffSha256, StringComparison.Ordinal))
                return ReplacementResult.Failed("Handoff bytes changed after approval.");

            handoff = JsonSerializer.Deserialize<HandoffData>(handoffBytes, JsonOptions)
                ?? throw new InvalidOperationException("Handoff data is empty.");
            var parsedManifestError = ValidateManifestAuthority(handoff.Manifest);
            if (parsedManifestError is not null)
                return ReplacementResult.Failed(parsedManifestError);

            var actualManifestSha256 = ComputeManifestAuthoritySha256(handoff.Manifest!);
            if (!string.Equals(actualManifestSha256, expectedManifestSha256, StringComparison.Ordinal))
                return ReplacementResult.Failed("Handoff manifest authority changed after approval.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return ReplacementResult.Failed($"Failed to parse handoff JSON: {ex.Message}");
        }

        if (handoff.OperationId == Guid.Empty)
            return ReplacementResult.Failed("Handoff operation id is invalid.");
        if (string.IsNullOrWhiteSpace(handoff.VaultRoot))
            return ReplacementResult.Failed("Handoff is missing the protected VaultRoot authority.");
        if (handoff.ShutdownDeadlineUtc is null)
            return ReplacementResult.Failed("Handoff is missing the shared shutdown deadline authority.");
        string installRoot;
        string payloadPath;
        string appStateRoot;
        string vaultRoot;
        try
        {
            installRoot = TrimRoot(Path.GetFullPath(handoff.DestinationInstallRoot));
            payloadPath = TrimRoot(Path.GetFullPath(handoff.SourcePayloadPath));
            vaultRoot = TrimRoot(Path.GetFullPath(handoff.VaultRoot));

            if (!string.Equals(Path.GetFileName(operationDir), handoff.OperationId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                return ReplacementResult.Failed("Handoff operation directory does not match the operation id.");

            var updateToolsRoot = Directory.GetParent(operationDir)?.FullName;
            if (string.IsNullOrWhiteSpace(updateToolsRoot)
                || !string.Equals(Path.GetFileName(TrimRoot(updateToolsRoot)), "update-tools", StringComparison.OrdinalIgnoreCase))
            {
                return ReplacementResult.Failed("Handoff is not located under the approved update-tools authority.");
            }

            appStateRoot = TrimRoot(Directory.GetParent(updateToolsRoot)?.FullName
                ?? throw new InvalidOperationException("Update-tools authority has no AppState root."));

            var expectedPayloadPath = TrimRoot(Path.Combine(
                appStateRoot,
                "updates",
                "staging",
                handoff.OperationId.ToString("D")));
            if (!SamePath(payloadPath, expectedPayloadPath))
                return ReplacementResult.Failed("Source payload is not the approved operation-scoped AppState staging directory.");
            if (Overlaps(installRoot, appStateRoot))
                return ReplacementResult.Failed("InstallRoot and AppStateRoot must be disjoint.");
            if (Overlaps(installRoot, vaultRoot))
                return ReplacementResult.Failed("InstallRoot and VaultRoot must be disjoint.");
            if (Overlaps(appStateRoot, vaultRoot))
                return ReplacementResult.Failed("AppStateRoot and VaultRoot must be disjoint.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ReplacementResult.Failed($"Handoff runtime path authority is invalid: {ex.Message}");
        }

        if (!Directory.Exists(installRoot))
            return ReplacementResult.Failed($"Destination InstallRoot does not exist: {installRoot}");
        if (!Directory.Exists(payloadPath))
            return ReplacementResult.Failed($"Expected extracted payload directory does not exist: {payloadPath}");

        var parentDir = Path.GetDirectoryName(installRoot);
        var dirName = Path.GetFileName(installRoot);
        if (string.IsNullOrEmpty(parentDir) || string.IsNullOrWhiteSpace(dirName))
            return ReplacementResult.Failed("InstallRoot cannot be a filesystem drive root.");

        var backupRoot = Path.Combine(parentDir, $"{dirName}.backup.{handoff.OperationId:N}");
        var stagingRoot = Path.Combine(parentDir, $"{dirName}.staged.{handoff.OperationId:N}");
        if (Overlaps(stagingRoot, appStateRoot) || Overlaps(backupRoot, appStateRoot))
            return ReplacementResult.Failed("Replacement sibling paths overlap AppStateRoot.");
        if (Overlaps(stagingRoot, vaultRoot) || Overlaps(backupRoot, vaultRoot))
            return ReplacementResult.Failed("Replacement sibling paths overlap VaultRoot.");

        if (parentPid.HasValue && parentPid.Value > 0)
        {
            var remainingParentWait = handoff.ShutdownDeadlineUtc.Value - DateTimeOffset.UtcNow;
            var exited = remainingParentWait > TimeSpan.Zero
                && await WaitForProcessExitAsync(
                    parentPid.Value,
                    remainingParentWait,
                    cancellationToken);
            if (!exited)
            {
                await PersistRecoveryStepAsync(
                    recoveryPlanPath,
                    handoff.OperationId,
                    installRoot,
                    stagingRoot,
                    backupRoot,
                    payloadPath,
                    expectedManifestSha256,
                    "DeferredShutdownDeadlineExpired",
                    cancellationToken);
                return ReplacementResult.Failed(
                    $"Parent process (PID {parentPid.Value}) did not exit before the shared update shutdown deadline. Replacement was deferred without mutating InstallRoot.");
            }
        }

        var installMovedToBackup = false;

        try
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);

            CopyDirectory(payloadPath, stagingRoot);
            var stagedValidationError = ValidateStagedPayload(stagingRoot, handoff.Manifest);
            if (stagedValidationError is not null)
            {
                Directory.Delete(stagingRoot, recursive: true);
                return ReplacementResult.Failed(stagedValidationError);
            }

            await PersistRecoveryStepAsync(
                recoveryPlanPath,
                handoff.OperationId,
                installRoot,
                stagingRoot,
                backupRoot,
                payloadPath,
                expectedManifestSha256,
                "StagingPrepared",
                cancellationToken);

            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);

            stagedValidationError = ValidateStagedPayload(stagingRoot, handoff.Manifest);
            if (stagedValidationError is not null)
            {
                Directory.Delete(stagingRoot, recursive: true);
                return ReplacementResult.Failed(stagedValidationError);
            }

            Directory.Move(installRoot, backupRoot);
            installMovedToBackup = true;
            await PersistRecoveryStepAsync(
                recoveryPlanPath,
                handoff.OperationId,
                installRoot,
                stagingRoot,
                backupRoot,
                payloadPath,
                expectedManifestSha256,
                "InstallMovedToBackup",
                CancellationToken.None);

            try
            {
                Directory.Move(stagingRoot, installRoot);
                await PersistRecoveryStepAsync(
                    recoveryPlanPath,
                    handoff.OperationId,
                    installRoot,
                    stagingRoot,
                    backupRoot,
                    payloadPath,
                    expectedManifestSha256,
                    "ReplacementCompleted",
                    CancellationToken.None);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                try
                {
                    if (Directory.Exists(backupRoot) && !Directory.Exists(installRoot))
                    {
                        Directory.Move(backupRoot, installRoot);
                        await PersistRecoveryStepAsync(
                            recoveryPlanPath,
                            handoff.OperationId,
                            installRoot,
                            stagingRoot,
                            backupRoot,
                            payloadPath,
                            expectedManifestSha256,
                            "RestoredFromBackup",
                            CancellationToken.None);
                    }
                }
                catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
                {
                    await PersistRecoveryStepAsync(
                        recoveryPlanPath,
                        handoff.OperationId,
                        installRoot,
                        stagingRoot,
                        backupRoot,
                        payloadPath,
                        expectedManifestSha256,
                        "RestoreFailed",
                        CancellationToken.None);
                    return ReplacementResult.Failed($"Replacement failed and restoration also failed: {moveEx.Message} | Restore error: {restoreEx.Message}");
                }

                return ReplacementResult.Failed($"Replacement failed and was restored from backup: {moveEx.Message}");
            }

            if (launchApp)
            {
                var appExe = Path.Combine(installRoot, ReleaseLayout.LauncherName(ReleaseLayout.IsLegacyPayload(handoff.Manifest.Files.Select(file => file.RelativePath))));
                if (File.Exists(appExe))
                {
                    var startInfo = new ProcessStartInfo(appExe)
                    {
                        WorkingDirectory = installRoot,
                        UseShellExecute = true
                    };
                    Process.Start(startInfo);
                }
            }

            return ReplacementResult.Succeeded();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await PersistRecoveryStepAsync(
                recoveryPlanPath,
                handoff.OperationId,
                installRoot,
                stagingRoot,
                backupRoot,
                payloadPath,
                expectedManifestSha256,
                "ReplacementFailed",
                installMovedToBackup ? CancellationToken.None : cancellationToken);
            return ReplacementResult.Failed(ex.Message);
        }
    }

    private static string ComputeManifestAuthoritySha256(HandoffManifestData manifest) =>
        UpdateManifestAuthority.ComputeSha256(
            manifest.SchemaVersion,
            manifest.ProductId,
            manifest.ProductVersion,
            manifest.RuntimeIdentifier,
            manifest.PayloadByteLength,
            manifest.PayloadSha256,
            manifest.MinimumCompatibleVersion,
            manifest.Files.Select(file => new ManifestAuthorityFile(
                file.RelativePath,
                file.ByteLength,
                file.Sha256,
                file.Role)));

    private static string? ValidateManifestAuthority(HandoffManifestData? manifest)
    {
        if (manifest is null)
            return "Handoff manifest is missing.";
        if (manifest.SchemaVersion != 1)
            return "Handoff manifest schema is unsupported.";
        if (!UpdateManifestAuthority.IsLowerSha256(manifest.PayloadSha256)
            || manifest.PayloadByteLength < 0
            || string.IsNullOrWhiteSpace(manifest.ProductVersion)
            || manifest.Files is null
            || manifest.Files.Count > 100_000)
        {
            return "Handoff manifest identity is invalid.";
        }

        ReleaseContractDocument contract;
        try
        {
            contract = ReleaseContract.Current;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            return $"Release contract is invalid: {ex.Message}";
        }

        if (!string.Equals(manifest.ProductId, contract.ProductId, StringComparison.Ordinal)
            || !string.Equals(manifest.RuntimeIdentifier, contract.RuntimeIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            return "Handoff manifest contradicts the canonical release identity.";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            string relative;
            try
            {
                relative = ReleaseContract.NormalizeRelativePath(file.RelativePath);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                return "Handoff manifest contains an unsafe file path.";
            }

            if (!seen.Add(relative)
                || file.ByteLength < 0
                || !UpdateManifestAuthority.IsLowerSha256(file.Sha256)
                || string.Equals(relative, "release-manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                return "Handoff manifest file identity is invalid.";
            }
        }

        var legacyLayout = ReleaseLayout.IsLegacyPayload(seen);
        foreach (var required in contract.RequiredMembers)
        {
            if (!seen.Contains(ReleaseLayout.RequiredMember(required, legacyLayout)))
                return $"Handoff manifest is missing required release member '{required}'.";
        }

        foreach (var requiredFileName in contract.RequiredUniqueFileNames)
        {
            if (seen.Count(path => string.Equals(Path.GetFileName(path), requiredFileName, StringComparison.OrdinalIgnoreCase)) != 1)
                return $"Handoff manifest must contain exactly one runtime member named '{requiredFileName}'.";
        }

        if (!seen.Contains(ReleaseLayout.LauncherName(legacyLayout))
            || seen.Any(path => !ReleaseLayout.IsManifestPayloadMember(path, legacyLayout)))
        {
            return "Handoff manifest must contain one supported root launcher and its matching runtime directory.";
        }

        return null;
    }

    private static string? ValidateStagedPayload(string root, HandoffManifestData manifest)
    {
        var authorityError = ValidateManifestAuthority(manifest);
        if (authorityError is not null)
            return authorityError;

        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                return "Replacement staging root cannot be a reparse point.";

            var approved = manifest.Files.ToDictionary(
                file => ReleaseContract.NormalizeRelativePath(file.RelativePath),
                file => file,
                StringComparer.OrdinalIgnoreCase);

            foreach (var pair in approved)
            {
                var path = ResolveContainedFile(root, pair.Key);
                EnsureNoReparseAncestors(root, path);
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    return $"Replacement staging is missing approved file '{pair.Key}'.";

                var info = new FileInfo(path);
                if (info.Length != pair.Value.ByteLength
                    || !string.Equals(ComputeSha256(path), pair.Value.Sha256, StringComparison.Ordinal))
                {
                    return $"Replacement staging file '{pair.Key}' changed after approval.";
                }
            }

            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>();
            pending.Enqueue(root);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        return "Replacement staging contains a reparse-point directory.";
                    pending.Enqueue(directory);
                }

                foreach (var file in Directory.EnumerateFiles(current))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        return "Replacement staging contains a reparse-point file.";
                    actual.Add(ReleaseContract.NormalizeRelativePath(Path.GetRelativePath(root, file)));
                }
            }

            var expected = new HashSet<string>(approved.Keys, StringComparer.OrdinalIgnoreCase)
            {
                ReleaseLayout.ReleaseManifestFileName
            };
            if (!actual.SetEquals(expected))
                return "Replacement staging membership changed after approval.";

            var legacyLayout = ReleaseLayout.IsLegacyPayload(approved.Keys);
            var rootFiles = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rootDirectories = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (rootFiles.Count != 2
                || !rootFiles.Contains(ReleaseLayout.LauncherName(legacyLayout))
                || !rootFiles.Contains(ReleaseLayout.ReleaseManifestFileName)
                || rootDirectories.Count != 1
                || !rootDirectories.Contains(ReleaseLayout.RuntimeDirectory(legacyLayout)))
            {
                return "Replacement staging root must contain only its supported launcher, release-manifest.json, and matching runtime directory.";
            }

            var controlPath = ResolveContainedFile(root, ReleaseLayout.ReleaseManifestFileName);
            if (new FileInfo(controlPath).Length > 2 * 1024 * 1024)
                return "Replacement control manifest exceeds safe limit.";
            var control = JsonSerializer.Deserialize<EmbeddedReleaseManifestData>(
                File.ReadAllBytes(controlPath),
                JsonOptions);
            if (control is null
                || control.SchemaVersion != manifest.SchemaVersion
                || !string.Equals(control.ProductId, manifest.ProductId, StringComparison.Ordinal)
                || !string.Equals(control.ProductVersion, manifest.ProductVersion, StringComparison.Ordinal)
                || !string.Equals(control.RuntimeIdentifier, manifest.RuntimeIdentifier, StringComparison.OrdinalIgnoreCase)
                || control.Files is null
                || control.Files.Count != manifest.Files.Count)
            {
                return "Replacement control manifest contradicts approved update authority.";
            }

            var controlByPath = control.Files.ToDictionary(
                file => ReleaseContract.NormalizeRelativePath(file.RelativePath),
                file => file,
                StringComparer.OrdinalIgnoreCase);
            if (controlByPath.Count != approved.Count)
                return "Replacement control manifest contains duplicate or missing membership.";

            foreach (var pair in approved)
            {
                if (!controlByPath.TryGetValue(pair.Key, out var controlFile)
                    || controlFile.ByteLength != pair.Value.ByteLength
                    || !string.Equals(controlFile.Sha256, pair.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return $"Replacement control manifest contradicts '{pair.Key}'.";
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException)
        {
            return $"Replacement staging validation failed safely: {ex.Message}";
        }
    }

    private static string ResolveContainedFile(string root, string relativePath)
    {
        var canonicalRoot = TrimRoot(Path.GetFullPath(root));
        var normalized = ReleaseContract.NormalizeRelativePath(relativePath);
        var candidate = Path.GetFullPath(Path.Combine(
            canonicalRoot,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithinOrEqual(canonicalRoot, candidate) || SamePath(canonicalRoot, candidate))
            throw new IOException("Replacement file path escaped staging authority.");
        return candidate;
    }

    private static void EnsureNoReparseAncestors(string root, string filePath)
    {
        var canonicalRoot = TrimRoot(Path.GetFullPath(root));
        var current = Path.GetDirectoryName(Path.GetFullPath(filePath))
            ?? throw new IOException("Replacement file has no parent directory.");

        while (!SamePath(current, canonicalRoot))
        {
            if (!IsWithinOrEqual(canonicalRoot, current))
                throw new IOException("Replacement file parent escaped staging authority.");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Replacement staging contains a reparse-point ancestor.");

            current = Path.GetDirectoryName(current)
                ?? throw new IOException("Replacement staging ancestry is incomplete.");
        }

        if ((File.GetAttributes(canonicalRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Replacement staging root cannot be a reparse point.");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<bool> WaitForProcessExitAsync(int pid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
                return true;

            var start = Stopwatch.GetTimestamp();
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(start) > timeout)
                    return false;

                await Task.Delay(250, cancellationToken);
                process.Refresh();
            }

            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        if ((File.GetAttributes(sourceDir) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Update payload root cannot be a reparse point.");

        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update payload cannot contain reparse-point files.");

            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update payload cannot contain reparse-point directories.");

            var destSub = Path.Combine(targetDir, Path.GetFileName(sub));
            CopyDirectory(sub, destSub);
        }
    }

    private static async Task PersistRecoveryStepAsync(
        string planPath,
        Guid operationId,
        string installRoot,
        string stagingRoot,
        string backupRoot,
        string payloadRoot,
        string manifestAuthoritySha256,
        string step,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = new
            {
                operationId,
                installRoot,
                stagingRoot,
                backupRoot,
                payloadRoot,
                manifestAuthoritySha256,
                step,
                updatedAtUtc = DateTimeOffset.UtcNow
            };
            var temp = planPath + ".tmp";
            var json = JsonSerializer.Serialize(record, JsonOptions);
            await File.WriteAllTextAsync(temp, json, cancellationToken);
            File.Move(temp, planPath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The deterministic sibling paths and update-state operation id remain recoverable evidence
            // even when the best-effort diagnostic journal cannot be persisted.
        }
    }

    private static string TrimRoot(string path) => Path.TrimEndingDirectorySeparator(path);

    private static bool SamePath(string left, string right) =>
        string.Equals(TrimRoot(left), TrimRoot(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinOrEqual(string ancestor, string candidate)
    {
        var normalizedAncestor = TrimRoot(ancestor);
        var normalizedCandidate = TrimRoot(candidate);
        if (SamePath(normalizedAncestor, normalizedCandidate))
            return true;

        return normalizedCandidate.StartsWith(
            normalizedAncestor + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool Overlaps(string left, string right) =>
        IsWithinOrEqual(left, right) || IsWithinOrEqual(right, left);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

public sealed record ReplacementResult(bool Success, string? Error)
{
    public static ReplacementResult Succeeded() => new(true, null);
    public static ReplacementResult Failed(string error) => new(false, error);
}

internal sealed record HandoffData(
    Guid OperationId,
    string SourcePayloadPath,
    string DestinationInstallRoot,
    HandoffManifestData Manifest,
    string? VaultRoot,
    DateTimeOffset? ShutdownDeadlineUtc);

internal sealed record HandoffManifestData(
    int SchemaVersion,
    string ProductId,
    string ProductVersion,
    string RuntimeIdentifier,
    long PayloadByteLength,
    string PayloadSha256,
    string? MinimumCompatibleVersion,
    IReadOnlyList<HandoffManifestFile> Files);

internal sealed record HandoffManifestFile(
    string RelativePath,
    long ByteLength,
    string Sha256,
    string? Role);

internal sealed record EmbeddedReleaseManifestData(
    int SchemaVersion,
    string ProductId,
    string ProductVersion,
    string RuntimeIdentifier,
    IReadOnlyList<HandoffManifestFile> Files);