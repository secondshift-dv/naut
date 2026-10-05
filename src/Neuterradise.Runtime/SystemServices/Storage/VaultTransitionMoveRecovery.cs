using System.Text.Json;

namespace Neuterradise.App.SystemServices.Storage;

public sealed partial class VaultTransitionAuthority
{
    private async Task<VaultTransitionRecoveryResult> RecoverMoveStartupAsync(
        VaultTransitionRecord active,
        AppConfigurationStore configuration,
        IProgress<VaultMoveProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (active.MoveMode is null)
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_MOVE_MODE_MISSING",
                "Vault Move recovery has no recorded Move mode.");
        }

        return active.MoveMode == VaultMoveMode.SameVolume
            ? await RecoverSameVolumeMoveStartupAsync(active, configuration, cancellationToken).ConfigureAwait(false)
            : await RecoverCrossVolumeMoveStartupAsync(active, configuration, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VaultTransitionRecoveryResult> RecoverSameVolumeMoveStartupAsync(
        VaultTransitionRecord active,
        AppConfigurationStore configuration,
        CancellationToken cancellationToken)
    {
        var sourceExists = Directory.Exists(active.SourceRoot);
        var targetExists = Directory.Exists(active.TargetRoot);
        if (sourceExists == targetExists)
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_TRANSITION_AMBIGUOUS",
                sourceExists
                    ? "Both old and moved Vault roots exist after a same-volume Move."
                    : "Neither old nor moved Vault root exists after a same-volume Move.");
        }

        var effectiveRoot = targetExists ? active.TargetRoot : active.SourceRoot;
        try
        {
            await EnsureExpectedMovedVaultIdentityAsync(
                effectiveRoot,
                active.VaultId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_MOVE_IDENTITY_MISMATCH",
                exception.Message);
        }

        var converged = await ConvergeConfiguredRootAsync(
            effectiveRoot,
            configuration,
            cancellationToken).ConfigureAwait(false);
        if (!converged.Succeeded)
        {
            return converged;
        }

        _moveEngine.DeleteOperationState(active.OperationId);
        await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
        return VaultTransitionRecoveryResult.Success(effectiveRoot);
    }

    private async Task<VaultTransitionRecoveryResult> RecoverCrossVolumeMoveStartupAsync(
        VaultTransitionRecord active,
        AppConfigurationStore configuration,
        IProgress<VaultMoveProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(active.StagingRoot))
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_MOVE_STAGING_MISSING",
                "Cross-volume Move recovery has no staging path.");
        }

        var targetParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(active.TargetRoot));
        if (string.IsNullOrWhiteSpace(targetParent))
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_MOVE_TARGET_INVALID",
                "Cross-volume Move recovery target has no parent directory.");
        }

        var expectedStagingRoot = Path.Combine(
            targetParent,
            $".naut-move-{active.OperationId:N}.staging");
        if (!RootPathRules.AreSameRoot(active.StagingRoot, expectedStagingRoot))
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_MOVE_STAGING_INVALID",
                "Cross-volume Move recovery refused an unexpected staging path.");
        }

        try
        {
            RootPathRules.EnsureDisjoint("SourceVault", active.SourceRoot, "TargetVault", active.TargetRoot);
            new VaultPaths(active.TargetRoot).EnsureDisjointFrom(_install, _appState);
        }
        catch (ArgumentException exception)
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_MOVE_TARGET_INVALID",
                exception.Message);
        }

        var sourceExists = Directory.Exists(active.SourceRoot);
        var targetExists = Directory.Exists(active.TargetRoot);
        var stagingExists = Directory.Exists(active.StagingRoot);
        var cleanupRoot = _moveEngine.GetCleanupRoot(active);
        var cleanupExists = Directory.Exists(cleanupRoot);

        if (targetExists)
        {
            try
            {
                await EnsureExpectedMovedVaultIdentityAsync(
                    active.TargetRoot,
                    active.VaultId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return VaultTransitionRecoveryResult.Failed(
                    "VAULT_MOVE_IDENTITY_MISMATCH",
                    exception.Message);
            }

            if (stagingExists)
            {
                return VaultTransitionRecoveryResult.Failed(
                    "VAULT_TRANSITION_AMBIGUOUS",
                    "Both a promoted destination and Move staging folder exist.");
            }

            if (cleanupExists && active.Phase != VaultTransitionPhase.TargetBootstrapSucceeded)
            {
                return VaultTransitionRecoveryResult.Failed(
                    "VAULT_TRANSITION_AMBIGUOUS",
                    "Old-source cleanup began before a successful destination bootstrap was recorded.");
            }

            if (active.Phase != VaultTransitionPhase.TargetBootstrapSucceeded
                && !await _moveEngine.VerifyTargetAsync(active, cancellationToken).ConfigureAwait(false))
            {
                return VaultTransitionRecoveryResult.Failed(
                    "VAULT_MOVE_TARGET_VERIFY_FAILED",
                    "The moved Vault does not match the verified Move inventory.");
            }

            var converged = await ConvergeConfiguredRootAsync(
                active.TargetRoot,
                configuration,
                cancellationToken).ConfigureAwait(false);
            if (!converged.Succeeded)
            {
                return converged;
            }

            if (active.Phase is not (VaultTransitionPhase.ConfigurationCommitted or VaultTransitionPhase.TargetBootstrapSucceeded))
            {
                active = active with
                {
                    Phase = VaultTransitionPhase.ConfigurationCommitted,
                    UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    LastError = null,
                };
                await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);
            }

            if (active.Phase == VaultTransitionPhase.TargetBootstrapSucceeded
                && !sourceExists
                && !cleanupExists)
            {
                _moveEngine.DeleteOperationState(active.OperationId);
                await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
            }

            return VaultTransitionRecoveryResult.Success(active.TargetRoot);
        }

        if (!sourceExists)
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_TRANSITION_AMBIGUOUS",
                cleanupExists
                    ? "The old Vault is in cleanup quarantine but the destination is missing."
                    : "Neither the old Vault nor moved destination exists.");
        }

        try
        {
            await EnsureExpectedMovedVaultIdentityAsync(
                active.SourceRoot,
                active.VaultId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_MOVE_IDENTITY_MISMATCH",
                exception.Message);
        }

        if (cleanupExists)
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_TRANSITION_AMBIGUOUS",
                "Both the old Vault and cleanup quarantine exist before destination promotion.");
        }

        if (active.Phase == VaultTransitionPhase.Prepared)
        {
            if (stagingExists)
            {
                return VaultTransitionRecoveryResult.Failed(
                    "VAULT_TRANSITION_AMBIGUOUS",
                    "A Move staging folder exists even though copying was never journaled. Automatic deletion was refused.");
            }

            var converged = await ConvergeConfiguredRootAsync(
                active.SourceRoot,
                configuration,
                cancellationToken).ConfigureAwait(false);
            if (!converged.Succeeded)
            {
                return converged;
            }

            _moveEngine.DeleteOperationState(active.OperationId);
            await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
            return VaultTransitionRecoveryResult.Success(active.SourceRoot);
        }

        if (active.Phase is not (
            VaultTransitionPhase.RuntimeRetired
            or VaultTransitionPhase.Copying
            or VaultTransitionPhase.DestinationVerified))
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_TRANSITION_AMBIGUOUS",
                $"Cross-volume Move cannot resume safely from phase '{active.Phase}'.");
        }

        try
        {
            var inventory = await _moveEngine
                .GetOrCreateSourceInventoryAsync(active, progress, cancellationToken)
                .ConfigureAwait(false);
            active = active with
            {
                Phase = VaultTransitionPhase.Copying,
                TotalBytes = inventory.TotalBytes,
                TotalFiles = inventory.Files.Count,
                UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                LastError = null,
            };
            await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);

            var stagedBytes = _moveEngine.EstimateExistingStagingBytes(active);
            EnsureDestinationCapacity(
                Path.GetDirectoryName(active.TargetRoot)
                    ?? throw new InvalidOperationException("Move target has no parent."),
                Math.Max(0, inventory.TotalBytes - stagedBytes));
            await _moveEngine
                .CopyAndVerifyStagingAsync(
                    active,
                    inventory,
                    progress,
                    PersistMoveProgressAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!await _moveEngine.VerifySourceAsync(active, cancellationToken).ConfigureAwait(false))
            {
                return VaultTransitionRecoveryResult.Failed(
                    "VAULT_MOVE_SOURCE_CHANGED",
                    "The old Vault changed while interrupted Move recovery was copying it.");
            }

            active = active with
            {
                Phase = VaultTransitionPhase.DestinationVerified,
                UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            };
            await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);

            Directory.Move(active.StagingRoot, active.TargetRoot);
            await EnsureExpectedMovedVaultIdentityAsync(
                active.TargetRoot,
                active.VaultId,
                cancellationToken).ConfigureAwait(false);
            active = active with
            {
                Phase = VaultTransitionPhase.FilesystemApplied,
                UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            };
            await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);

            var converged = await ConvergeConfiguredRootAsync(
                active.TargetRoot,
                configuration,
                cancellationToken).ConfigureAwait(false);
            if (!converged.Succeeded)
            {
                return converged;
            }

            active = active with
            {
                Phase = VaultTransitionPhase.ConfigurationCommitted,
                UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            };
            await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);
            return VaultTransitionRecoveryResult.Success(active.TargetRoot);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or JsonException)
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_MOVE_RECOVERY_FAILED",
                exception.Message);
        }
    }
}
