using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Updates;

public sealed record UpdateStartupRecoveryResult(
    bool CanContinue,
    bool ReplacementInstalled,
    Guid? OperationId,
    string? BackupPath,
    string? SafeError)
{
    public static UpdateStartupRecoveryResult NoAction(
        string? warning = null) =>
        new(true, false, null, null, warning);

    public static UpdateStartupRecoveryResult ReplacementReady(
        Guid operationId,
        string? backupPath) =>
        new(true, true, operationId, backupPath, null);

    public static UpdateStartupRecoveryResult Blocked(
        Guid? operationId,
        string? backupPath,
        string error) =>
        new(false, false, operationId, backupPath, error);
}

public sealed class UpdateStartupRecovery
{
    private readonly AppStatePaths _appState;
    private readonly InstallPaths _install;
    private readonly string? _vaultRoot;
    private readonly UpdateStateStore _stateStore;
    private readonly UpdateRecoveryStore _recoveryStore;
    private readonly UpdateTerminalCleanup _terminalCleanup;

    public UpdateStartupRecovery(
        AppStatePaths appState,
        InstallPaths install,
        string? vaultRoot,
        UpdateStateStore? stateStore = null,
        UpdateRecoveryStore? recoveryStore = null,
        UpdateTerminalCleanup? terminalCleanup = null)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _vaultRoot = vaultRoot;
        _install.EnsureDisjointFrom(_appState, _vaultRoot);
        _stateStore = stateStore ?? new UpdateStateStore(_appState);
        _recoveryStore =
            recoveryStore ?? new UpdateRecoveryStore(_appState);
        _terminalCleanup =
            terminalCleanup
            ?? new UpdateTerminalCleanup(
                _appState,
                _install,
                _vaultRoot);
    }

    public async Task<UpdateStartupRecoveryResult> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        var stateRead = await _stateStore
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (stateRead.IsMissing)
            return UpdateStartupRecoveryResult.NoAction();
        if (stateRead.IsCorrupt)
        {
            return UpdateStartupRecoveryResult.Blocked(
                null,
                null,
                stateRead.SafeError
                ?? "Persisted update state is corrupt; startup is blocked to preserve recovery evidence.");
        }

        var state = stateRead.Value!;
        var persistedBackupError = ValidatePersistedBackupAuthority(state);
        if (persistedBackupError is not null)
        {
            return UpdateStartupRecoveryResult.Blocked(
                state.OperationId,
                state.BackupPath,
                persistedBackupError);
        }

        if (state.Cutoff == UpdateStartupCutoff.Completed)
        {
            var cleanup = await _terminalCleanup
                .CleanupAsync(
                    state.OperationId,
                    state.BackupPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!cleanup.Success)
            {
                await _stateStore
                    .PublishCleanupPendingAsync(
                        state.OperationId,
                        state.BackupPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return UpdateStartupRecoveryResult.NoAction(cleanup.SafeError);
        }

        if (state.Cutoff == UpdateStartupCutoff.AbortedNoMutation)
        {
            var abortedPaths = GetReplacementPaths(state.OperationId);
            if (!Directory.Exists(_install.Root)
                || Directory.Exists(abortedPaths.BackupRoot))
            {
                return await BlockAsync(
                    state.OperationId,
                    Directory.Exists(abortedPaths.BackupRoot)
                        ? abortedPaths.BackupRoot
                        : null,
                    "Aborted-no-mutation update state contradicts the replacement filesystem topology.",
                    cancellationToken).ConfigureAwait(false);
            }

            var cleanup = await _terminalCleanup
                .CleanupAbortedNoMutationAsync(
                    state.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
            return UpdateStartupRecoveryResult.NoAction(cleanup.SafeError);
        }

        if (state.Cutoff == UpdateStartupCutoff.CleanupPending)
        {
            var cleanup = await _terminalCleanup
                .CleanupAsync(
                    state.OperationId,
                    state.BackupPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (cleanup.Success)
            {
                await _stateStore
                    .PublishCompletedAsync(
                        state.OperationId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return UpdateStartupRecoveryResult.NoAction(cleanup.SafeError);
        }

        if (state.Cutoff == UpdateStartupCutoff.RecoveryRequired)
        {
            return UpdateStartupRecoveryResult.Blocked(
                state.OperationId,
                state.BackupPath,
                "Update replacement recovery is required before startup can continue.");
        }

        if (state.Cutoff == UpdateStartupCutoff.CatalogWriteStarted)
        {
            var catalogWritePaths = GetReplacementPaths(state.OperationId);
            if (string.IsNullOrWhiteSpace(state.BackupPath)
                || !Directory.Exists(catalogWritePaths.BackupRoot))
            {
                return UpdateStartupRecoveryResult.Blocked(
                    state.OperationId,
                    state.BackupPath,
                    "Update rollback backup is missing before the replacement reached Ready.");
            }

            return UpdateStartupRecoveryResult.ReplacementReady(
                state.OperationId,
                catalogWritePaths.BackupRoot);
        }

        if (state.Cutoff
            is not (UpdateStartupCutoff.HandoffPending
                or UpdateStartupCutoff.None))
        {
            return await BlockAsync(
                state.OperationId,
                state.BackupPath,
                "Persisted update state is not recognized for startup reconciliation.",
                cancellationToken).ConfigureAwait(false);
        }

        var paths = GetReplacementPaths(state.OperationId);
        var planRead = await _recoveryStore
            .ReadAsync(
                state.OperationId,
                _install,
                _vaultRoot,
                cancellationToken)
            .ConfigureAwait(false);

        if (planRead.IsCorrupt)
        {
            return await BlockAsync(
                state.OperationId,
                Directory.Exists(paths.BackupRoot)
                    ? paths.BackupRoot
                    : null,
                planRead.SafeError
                ?? "Update recovery journal is corrupt; startup cannot prove a safe replacement state.",
                cancellationToken).ConfigureAwait(false);
        }

        if (planRead.IsMissing)
        {
            if (Directory.Exists(paths.BackupRoot)
                || !Directory.Exists(_install.Root))
            {
                return await BlockAsync(
                    state.OperationId,
                    Directory.Exists(paths.BackupRoot)
                        ? paths.BackupRoot
                        : null,
                    "Update recovery journal is missing after possible filesystem mutation.",
                    cancellationToken).ConfigureAwait(false);
            }

            return await PublishAbortedNoMutationAndCleanupAsync(
                state.OperationId,
                "No replacement filesystem mutation is present.",
                cancellationToken).ConfigureAwait(false);
        }

        var plan = planRead.Value!;
        if (plan.Step is "AbortedParentStillRunning"
            or "DeferredShutdownDeadlineExpired"
            or "StagingPrepared"
            or "RestoredFromBackup")
        {
            if (Directory.Exists(_install.Root)
                && !Directory.Exists(paths.BackupRoot))
            {
                return await PublishAbortedNoMutationAndCleanupAsync(
                    state.OperationId,
                    $"Updater ended at safe step {plan.Step} before a replacement remained installed.",
                    cancellationToken).ConfigureAwait(false);
            }

            return await BlockAsync(
                state.OperationId,
                Directory.Exists(paths.BackupRoot)
                    ? paths.BackupRoot
                    : null,
                $"Update filesystem state contradicts recovery step {plan.Step}.",
                cancellationToken).ConfigureAwait(false);
        }

        if (plan.Step == "ReplacementCompleted")
        {
            return await VerifyInstalledReplacementAsync(
                state.OperationId,
                paths,
                plan.ManifestAuthoritySha256,
                cancellationToken).ConfigureAwait(false);
        }

        if (plan.Step is "InstallMovedToBackup"
                or "RestoreFailed"
                or "ReplacementFailed"
            || plan.Step.StartsWith("Failed:", StringComparison.Ordinal))
        {
            if (!Directory.Exists(_install.Root)
                && Directory.Exists(paths.BackupRoot))
            {
                try
                {
                    Directory.Move(paths.BackupRoot, _install.Root);
                    return await PublishAbortedNoMutationAndCleanupAsync(
                        state.OperationId,
                        "Interrupted replacement was restored from its deterministic backup before catalog access.",
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException
                    or UnauthorizedAccessException)
                {
                    return await BlockAsync(
                        state.OperationId,
                        paths.BackupRoot,
                        "Interrupted replacement backup could not be restored safely.",
                        cancellationToken).ConfigureAwait(false);
                }
            }

            if (Directory.Exists(_install.Root)
                && Directory.Exists(paths.BackupRoot))
            {
                return await VerifyInstalledReplacementAsync(
                    state.OperationId,
                    paths,
                    plan.ManifestAuthoritySha256,
                    cancellationToken).ConfigureAwait(false);
            }

            if (Directory.Exists(_install.Root)
                && !Directory.Exists(paths.BackupRoot)
                && plan.Step == "ReplacementFailed")
            {
                return await PublishAbortedNoMutationAndCleanupAsync(
                    state.OperationId,
                    "Updater failed before InstallRoot was moved.",
                    cancellationToken).ConfigureAwait(false);
            }

            return await BlockAsync(
                state.OperationId,
                Directory.Exists(paths.BackupRoot)
                    ? paths.BackupRoot
                    : null,
                "Update replacement filesystem state is ambiguous.",
                cancellationToken).ConfigureAwait(false);
        }

        return await BlockAsync(
            state.OperationId,
            Directory.Exists(paths.BackupRoot)
                ? paths.BackupRoot
                : null,
            $"Unsupported update recovery step '{plan.Step}'.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateStartupRecoveryResult>
        VerifyInstalledReplacementAsync(
            Guid operationId,
            ReplacementPaths paths,
            string manifestAuthoritySha256,
            CancellationToken cancellationToken)
    {
        var handoffRead = await _recoveryStore
            .ReadHandoffAsync(operationId, cancellationToken)
            .ConfigureAwait(false);
        if (handoffRead.IsCorrupt)
        {
            return await BlockAsync(
                operationId,
                Directory.Exists(paths.BackupRoot)
                    ? paths.BackupRoot
                    : null,
                handoffRead.SafeError
                ?? "Installed replacement handoff is corrupt.",
                cancellationToken).ConfigureAwait(false);
        }

        if (handoffRead.IsMissing)
        {
            return await BlockAsync(
                operationId,
                Directory.Exists(paths.BackupRoot)
                    ? paths.BackupRoot
                    : null,
                "Installed replacement has no handoff evidence.",
                cancellationToken).ConfigureAwait(false);
        }

        var handoff = handoffRead.Value!;
        if (!string.Equals(
            handoff.Manifest.ComputeAuthoritySha256(),
            manifestAuthoritySha256,
            StringComparison.Ordinal))
        {
            return await BlockAsync(
                operationId,
                Directory.Exists(paths.BackupRoot)
                    ? paths.BackupRoot
                    : null,
                "Installed replacement manifest authority does not match its recovery journal.",
                cancellationToken).ConfigureAwait(false);
        }

        string expectedPayload;
        string actualPayload;
        string actualInstall;
        string? actualVault = null;
        try
        {
            expectedPayload =
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(paths.PayloadRoot));
            actualPayload =
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(handoff.SourcePayloadPath));
            actualInstall =
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(handoff.DestinationInstallRoot));

            if (!string.IsNullOrWhiteSpace(_vaultRoot))
            {
                if (string.IsNullOrWhiteSpace(handoff.VaultRoot))
                    throw new FormatException(
                        "Installed replacement handoff is missing VaultRoot protection evidence.");

                actualVault =
                    Path.TrimEndingDirectorySeparator(
                        Path.GetFullPath(handoff.VaultRoot));
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or FormatException
            or IOException
            or UnauthorizedAccessException)
        {
            return await BlockAsync(
                operationId,
                Directory.Exists(paths.BackupRoot)
                    ? paths.BackupRoot
                    : null,
                $"Installed replacement handoff path authority is corrupt ({exception.GetType().Name}).",
                cancellationToken).ConfigureAwait(false);
        }

        if (!RootPathRules.AreSameRoot(expectedPayload, actualPayload)
            || !RootPathRules.AreSameRoot(_install.Root, actualInstall))
        {
            return await BlockAsync(
                operationId,
                Directory.Exists(paths.BackupRoot)
                    ? paths.BackupRoot
                    : null,
                "Installed replacement handoff violates runtime path authority.",
                cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(_vaultRoot)
            && (actualVault is null
                || !RootPathRules.AreSameRoot(_vaultRoot, actualVault)))
        {
            return await BlockAsync(
                operationId,
                Directory.Exists(paths.BackupRoot)
                    ? paths.BackupRoot
                    : null,
                "Installed replacement handoff VaultRoot does not match startup authority.",
                cancellationToken).ConfigureAwait(false);
        }

        var validation =
            new UpdatePackageValidator(_install)
                .Validate(handoff.Manifest, _install.Root);
        if (!validation.IsAccepted)
        {
            return await BlockAsync(
                operationId,
                Directory.Exists(paths.BackupRoot)
                    ? paths.BackupRoot
                    : null,
                validation.SafeError
                ?? "Installed replacement does not match the approved manifest.",
                cancellationToken).ConfigureAwait(false);
        }

        if (!Directory.Exists(paths.BackupRoot))
        {
            return await BlockAsync(
                operationId,
                null,
                "Installed replacement is valid, but its rollback backup is missing before Ready.",
                cancellationToken).ConfigureAwait(false);
        }

        return UpdateStartupRecoveryResult.ReplacementReady(
            operationId,
            paths.BackupRoot);
    }

    private async Task<UpdateStartupRecoveryResult> PublishAbortedNoMutationAndCleanupAsync(
        Guid operationId,
        string reason,
        CancellationToken cancellationToken)
    {
        await _stateStore
            .PublishAbortedNoMutationAsync(
                operationId,
                reason,
                cancellationToken)
            .ConfigureAwait(false);

        var cleanup = await _terminalCleanup
            .CleanupAbortedNoMutationAsync(
                operationId,
                cancellationToken)
            .ConfigureAwait(false);
        return UpdateStartupRecoveryResult.NoAction(cleanup.SafeError);
    }

    private async Task<UpdateStartupRecoveryResult> BlockAsync(
        Guid operationId,
        string? backupPath,
        string reason,
        CancellationToken cancellationToken)
    {
        await _stateStore
            .PublishRecoveryRequiredAsync(
                operationId,
                backupPath,
                cancellationToken)
            .ConfigureAwait(false);
        return UpdateStartupRecoveryResult.Blocked(
            operationId,
            backupPath,
            reason);
    }

    private string? ValidatePersistedBackupAuthority(UpdateState state)
    {
        if (string.IsNullOrWhiteSpace(state.BackupPath))
            return null;

        try
        {
            var expected = GetReplacementPaths(state.OperationId).BackupRoot;
            var actual = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(state.BackupPath));
            return RootPathRules.AreSameRoot(expected, actual)
                ? null
                : "Persisted update backup path contradicts deterministic replacement authority.";
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or UnauthorizedAccessException)
        {
            return $"Persisted update backup path is invalid ({exception.GetType().Name}).";
        }
    }

    private ReplacementPaths GetReplacementPaths(Guid operationId)
    {
        var parent = Path.GetDirectoryName(_install.Root)
            ?? throw new InvalidOperationException(
                "InstallRoot cannot be a filesystem drive root.");
        var name = Path.GetFileName(_install.Root);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                "InstallRoot cannot be a filesystem drive root.");
        }

        var staging =
            Path.Combine(
                parent,
                $"{name}.staged.{operationId:N}");
        var backup =
            Path.Combine(
                parent,
                $"{name}.backup.{operationId:N}");
        var payload =
            Path.Combine(
                _appState.UpdateStagingPath,
                operationId.ToString("D"));
        RootPathRules.EnsureDisjoint(
            "UpdateStagingRoot",
            staging,
            "AppStateRoot",
            _appState.Root);
        RootPathRules.EnsureDisjoint(
            "UpdateBackupRoot",
            backup,
            "AppStateRoot",
            _appState.Root);
        if (!string.IsNullOrWhiteSpace(_vaultRoot))
        {
            RootPathRules.EnsureDisjoint(
                "UpdateStagingRoot",
                staging,
                "VaultRoot",
                _vaultRoot);
            RootPathRules.EnsureDisjoint(
                "UpdateBackupRoot",
                backup,
                "VaultRoot",
                _vaultRoot);
        }

        return new ReplacementPaths(
            staging,
            backup,
            payload);
    }

    private sealed record ReplacementPaths(
        string StagingRoot,
        string BackupRoot,
        string PayloadRoot);
}
