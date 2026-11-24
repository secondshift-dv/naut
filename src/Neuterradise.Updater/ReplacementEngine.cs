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
        CancellationToken cancellationToken = default,
        bool recover = false)
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

        if (!recover && !Directory.Exists(installRoot))
            return ReplacementResult.Failed($"Destination InstallRoot does not exist: {installRoot}");
        if (!recover && !Directory.Exists(payloadPath))
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
                return ReplacementResult.Failed(
                    $"Parent process (PID {parentPid.Value}) did not exit before the shared update shutdown deadline. Replacement was deferred without mutating InstallRoot.");
            }
        }

        try
        {
            UpdatePathGuard.RequireDisjoint(installRoot, appStateRoot, vaultRoot, stagingRoot, backupRoot);
            UpdatePathGuard.Resolve(payloadPath);
            UpdatePathGuard.Resolve(normalizedHandoffPath);
        }
        catch (IOException exception)
        {
            return ReplacementResult.Failed(exception.Message);
        }
        await using var updateLease = await UpdateOperationLease.AcquireAsync(appStateRoot, cancellationToken);
        var installMovedToBackup = false;
        if (recover)
            return await RecoverBoundOperationAsync(operationDir, handoff, expectedHandoffSha256,
                expectedManifestSha256, installRoot, stagingRoot, backupRoot, payloadPath, appStateRoot,
                launchApp, cancellationToken);

        try
        {
            // The original backup and any interrupted staging are recovery evidence, not scratch space.
            if (Path.Exists(stagingRoot) || Path.Exists(backupRoot))
                return ReplacementResult.Failed("Replacement already has recovery evidence; startup reconciliation is required.");

            // Only the currently published pending operation may consume its handoff.
            var statePath = Path.Combine(appStateRoot, "updates", "update-state.json");
            try
            {
                using var state = JsonDocument.Parse(await File.ReadAllBytesAsync(statePath, cancellationToken));
                if (!state.RootElement.TryGetProperty("OperationId", out var operation)
                    || operation.ValueKind != JsonValueKind.String
                    || !operation.TryGetGuid(out var approvedOperation)
                    || approvedOperation != handoff.OperationId
                    || !state.RootElement.TryGetProperty("Cutoff", out var cutoff)
                    || cutoff.ValueKind != JsonValueKind.Number
                    || !cutoff.TryGetInt32(out var cutoffValue)
                    || cutoffValue != 1) // persisted HandoffPending
                    return ReplacementResult.Failed("Replacement is not the current pending update operation.");
                if (state.RootElement.TryGetProperty("HandoffSha256", out var approvedHandoff) &&
                    approvedHandoff.ValueKind != JsonValueKind.Null &&
                    (!string.Equals(approvedHandoff.GetString(), expectedHandoffSha256, StringComparison.Ordinal) ||
                     !state.RootElement.TryGetProperty("ManifestAuthoritySha256", out var approvedManifest) ||
                     !string.Equals(approvedManifest.GetString(), expectedManifestSha256, StringComparison.Ordinal)))
                    return ReplacementResult.Failed("Handoff differs from the persisted approval.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return ReplacementResult.Failed("Pending update authority is missing or unreadable.");
            }
            // CreateNew is atomic between competing helpers for this operation.
            // Keep the claim until terminal cleanup, including failures after it is created.
            try
            {
                using var claim = new FileStream(Path.Combine(operationDir, "replacement.claim"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                claim.Write(handoff.OperationId.ToByteArray());
                claim.Flush(flushToDisk: true);
            }
            catch (IOException)
            {
                return ReplacementResult.Failed("Replacement operation has already been claimed or its claim cannot be persisted.");
            }

            UpdatePathGuard.CopyNewTree(payloadPath, stagingRoot, cancellationToken);
            var stagedValidationError = ValidateStagedPayload(stagingRoot, handoff.Manifest);
            if (stagedValidationError is not null)
            {
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

            if (Path.Exists(backupRoot))
                return ReplacementResult.Failed("Original replacement backup must not be overwritten.");

            stagedValidationError = ValidateStagedPayload(stagingRoot, handoff.Manifest);
            if (stagedValidationError is not null)
            {
                return ReplacementResult.Failed(stagedValidationError);
            }

            UpdatePathGuard.RequireDisjoint(installRoot, appStateRoot, vaultRoot, stagingRoot, backupRoot);
            UpdateBackupIdentity.RecordBeforeMove(operationDir, installRoot, expectedHandoffSha256, cancellationToken);
            UpdatePathGuard.MoveSibling(installRoot, backupRoot);
            installMovedToBackup = true;
            if (!UpdateBackupIdentity.Matches(operationDir, backupRoot, expectedHandoffSha256, CancellationToken.None))
                throw new IOException("Moved backup identity does not match the approved original.");
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
                UpdatePathGuard.MoveSibling(stagingRoot, installRoot);
                if (ValidateStagedPayload(installRoot, handoff.Manifest) is { } installedError)
                    throw new IOException(installedError);
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
                    if (Directory.Exists(backupRoot) && !Directory.Exists(installRoot) &&
                        UpdateBackupIdentity.Matches(operationDir, backupRoot, expectedHandoffSha256, CancellationToken.None))
                    {
                        UpdatePathGuard.MoveSibling(backupRoot, installRoot);
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

    // The same isolated helper is usable even while InstallRoot is absent. Never
    // accept command-line authority alone for a recovery that can move directories.
    public static async Task<ReplacementResult> RecoverAsync(string operationDir, bool launchApp,
        CancellationToken cancellationToken = default)
    {
        operationDir = TrimRoot(Path.GetFullPath(operationDir));
        var toolsRoot = Directory.GetParent(operationDir);
        if (toolsRoot is null || !string.Equals(toolsRoot.Name, "update-tools", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(Path.GetFileName(operationDir), out var operationId) || operationId == Guid.Empty)
            return ReplacementResult.Failed("Recovery must run from the isolated update operation directory.");
        var statePath = Path.Combine(toolsRoot.Parent!.FullName, "updates", "update-state.json");
        UpdatePathGuard.Resolve(statePath);
        using var state = JsonDocument.Parse(await File.ReadAllBytesAsync(statePath, cancellationToken));
        if (state.RootElement.GetProperty("OperationId").GetGuid() != operationId)
            return ReplacementResult.Failed("This helper does not own the pending update.");
        return await ExecuteAsync(Path.Combine(operationDir, "handoff.json"),
            state.RootElement.GetProperty("HandoffSha256").GetString() ?? "",
            state.RootElement.GetProperty("ManifestAuthoritySha256").GetString() ?? "",
            null, launchApp, cancellationToken, recover: true);
    }

    private static async Task<ReplacementResult> RecoverBoundOperationAsync(string operationDir,
        HandoffData handoff, string handoffSha256, string manifestSha256, string installRoot,
        string stagingRoot, string backupRoot, string payloadPath, string appStateRoot,
        bool launchApp, CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(appStateRoot, "updates", "update-state.json");
        UpdatePathGuard.Resolve(statePath);
        using var state = JsonDocument.Parse(await File.ReadAllBytesAsync(statePath, cancellationToken));
        var value = state.RootElement;
        if (value.GetProperty("OperationId").GetGuid() != handoff.OperationId ||
            value.GetProperty("HandoffSha256").GetString() != handoffSha256 ||
            value.GetProperty("ManifestAuthoritySha256").GetString() != manifestSha256)
            return ReplacementResult.Failed("Recovery approval changed.");
        var cutoff = value.GetProperty("Cutoff").GetInt32();
        var blockedRecovery = cutoff == 3;
        if (blockedRecovery && value.TryGetProperty("RecoveryOrigin", out var origin) && origin.ValueKind == JsonValueKind.Number)
            cutoff = origin.GetInt32();
        if (cutoff is not (1 or 2))
            return ReplacementResult.Failed("Update cutoff does not permit external recovery.");
        UpdatePathGuard.RequireDisjoint(installRoot, appStateRoot, handoff.VaultRoot!, stagingRoot, backupRoot);
        var planPath = Path.Combine(operationDir, "recovery-plan.json");
        string step;
        if (Directory.Exists(backupRoot) &&
            UpdateBackupIdentity.Matches(operationDir, backupRoot, handoffSha256, cancellationToken))
        {
            if (!Directory.Exists(installRoot))
            {
                if (cutoff == 1)
                {
                    UpdatePathGuard.MoveSibling(backupRoot, installRoot);
                    step = "RestoredFromBackup";
                }
                else
                {
                    if (!Path.Exists(stagingRoot))
                    {
                        // The original approved payload remains in AppState until terminal cleanup.
                        // Reconstruct only an absent staging tree; never overwrite recovery evidence.
                        if (ValidateStagedPayload(payloadPath, handoff.Manifest) is not null)
                            return ReplacementResult.Failed("No verified forward payload remains after catalog mutation.");
                        UpdatePathGuard.CopyNewTree(payloadPath, stagingRoot, cancellationToken);
                    }
                    if (ValidateStagedPayload(stagingRoot, handoff.Manifest) is not null)
                        return ReplacementResult.Failed("No verified forward replacement remains after catalog mutation.");
                    UpdatePathGuard.RequireDisjoint(installRoot, appStateRoot, handoff.VaultRoot!, stagingRoot, backupRoot);
                    UpdatePathGuard.MoveSibling(stagingRoot, installRoot);
                    step = "ReplacementCompleted";
                }
            }
            else if (ValidateStagedPayload(installRoot, handoff.Manifest) is null)
                step = "ReplacementCompleted";
            else
                return ReplacementResult.Failed("Installed tree is ambiguous; recovery evidence retained.");
        }
        else if (cutoff == 1 && !Path.Exists(backupRoot) && Directory.Exists(installRoot) &&
            UpdateBackupIdentity.Matches(operationDir, installRoot, handoffSha256, cancellationToken))
            step = "RestoredFromBackup";
        else
            return ReplacementResult.Failed("Verified pre-replacement install identity is unavailable.");

        var restored = step == "RestoredFromBackup";
        if (restored && !UpdateBackupIdentity.Matches(operationDir, installRoot, handoffSha256, CancellationToken.None))
            return ReplacementResult.Failed("Restored install does not match the approved original.");
        if (!restored && ValidateStagedPayload(installRoot, handoff.Manifest) is not null)
            return ReplacementResult.Failed("Recovered target identity does not match the approved replacement.");
        var journalStep = blockedRecovery && !restored ? "ExternalReplacementCompleted" : step;
        await PersistRecoveryStepAsync(planPath, handoff.OperationId, installRoot, stagingRoot,
            backupRoot, payloadPath, manifestSha256, journalStep, CancellationToken.None);
        if (restored)
        {
            // A restored executable may predate RecoveryOrigin. Publish the established
            // AbortedNoMutation state under the operation lease after verifying the original.
            await UpdatePathGuard.WriteAtomicAsync(statePath, JsonSerializer.SerializeToUtf8Bytes(new
            {
                OperationId = handoff.OperationId, Cutoff = 5, BackupPath = (string?)null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                AbortReason = "Externally restored verified pre-replacement install.",
                HandoffSha256 = handoffSha256, ManifestAuthoritySha256 = manifestSha256
            }), CancellationToken.None).ConfigureAwait(false);
        }
        if (launchApp)
        {
            var launcher = Path.Combine(installRoot, ReleaseLayout.LauncherExecutableName);
            if (!File.Exists(launcher) && step == "RestoredFromBackup")
                launcher = Path.Combine(installRoot, ReleaseLayout.LegacyExecutableName);
            UpdatePathGuard.Resolve(launcher);
            if (!File.Exists(launcher))
                return ReplacementResult.Failed("Verified recovery tree has no runnable launcher.");
            Process.Start(new ProcessStartInfo(launcher) { WorkingDirectory = installRoot, UseShellExecute = true });
        }
        return ReplacementResult.Succeeded();
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
                || !string.Equals(control.MinimumCompatibleVersion, manifest.MinimumCompatibleVersion, StringComparison.Ordinal)
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
        await UpdatePathGuard.WriteAtomicAsync(planPath, JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions),
            cancellationToken).ConfigureAwait(false);
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
    IReadOnlyList<HandoffManifestFile> Files,
    string? MinimumCompatibleVersion = null);