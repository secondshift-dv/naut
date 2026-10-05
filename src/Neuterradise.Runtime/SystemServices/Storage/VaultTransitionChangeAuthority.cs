namespace Neuterradise.App.SystemServices.Storage;

public sealed partial class VaultTransitionAuthority
{
    public async Task<VaultTransitionCommandResult> BeginChangeAsync(
        string currentRoot,
        string selectedTargetRoot,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = RootPathRules.NormalizeRoot(currentRoot, nameof(currentRoot));
        if (!Directory.Exists(sourceRoot))
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_CHANGE_SOURCE_MISSING",
                "The current Vault folder is no longer available.");
        }

        var inspection = _selectionPolicy.Inspect(selectedTargetRoot, sourceRoot);
        if (!inspection.Accepted || inspection.Paths is null || inspection.Kind is null)
        {
            return VaultTransitionCommandResult.Failed(
                inspection.ErrorCode ?? "VAULT_CHANGE_TARGET_INVALID",
                inspection.SafeErrorDetail ?? "The selected folder cannot be used as a Vault.");
        }

        var active = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (active is not null)
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_TRANSITION_PENDING",
                "A previous Vault transition still requires startup recovery.");
        }

        var targetKind = inspection.Kind == VaultSelectionKind.ExistingVault
            ? VaultChangeTargetKind.ExistingVault
            : VaultChangeTargetKind.EmptyFolder;
        var operationId = Guid.NewGuid();
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var record = new VaultTransitionRecord(
            operationId,
            VaultTransitionKind.Change,
            VaultTransitionPhase.Prepared,
            sourceRoot,
            inspection.Paths.Root,
            now,
            now,
            ChangeTargetKind: targetKind);

        await _store.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        return VaultTransitionCommandResult.Success(new VaultChangePlan(
            operationId,
            sourceRoot,
            inspection.Paths.Root,
            targetKind));
    }

    public async Task<VaultTransitionCommandResult> MarkRuntimeRetiredAsync(
        VaultChangePlan plan,
        VaultRuntimeRetirementProof retirementProof,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(retirementProof);

        if (!RootPathRules.AreSameRoot(plan.SourceRoot, retirementProof.VaultRoot))
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_RETIREMENT_PROOF_MISMATCH",
                "Runtime retirement proof does not belong to this Vault root.");
        }

        var active = await RequireActiveChangeAsync(plan, cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_TRANSITION_MISSING",
                "The Change Vault journal is missing or no longer matches this operation.");
        }

        active = active with
        {
            Phase = VaultTransitionPhase.RuntimeRetired,
            UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            LastError = null,
        };
        await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);
        return VaultTransitionCommandResult.Success(plan);
    }

    public async Task<VaultTransitionCommandResult> ApplyChangeAfterRetirementAsync(
        VaultChangePlan plan,
        AppConfigurationStore configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(configuration);

        var active = await RequireActiveChangeAsync(plan, cancellationToken).ConfigureAwait(false);
        if (active is null || active.Phase != VaultTransitionPhase.RuntimeRetired)
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_RUNTIME_NOT_RETIRED",
                "Change Vault was refused because runtime retirement was not proven.");
        }

        if (!Directory.Exists(plan.SourceRoot))
        {
            return await RecordFailureAsync(
                active,
                "VAULT_CHANGE_SOURCE_MISSING",
                "The previous Vault folder disappeared before the configuration switch.",
                cancellationToken).ConfigureAwait(false);
        }

        if (!Directory.Exists(plan.TargetRoot))
        {
            return await RecordFailureAsync(
                active,
                "VAULT_CHANGE_TARGET_MISSING",
                "The selected Vault folder disappeared before the configuration switch.",
                cancellationToken).ConfigureAwait(false);
        }

        var targetInspection = _selectionPolicy.Inspect(plan.TargetRoot, plan.SourceRoot);
        var targetStillMatchesSelection = targetInspection.Accepted
            && targetInspection.Kind is not null
            && (plan.TargetKind == VaultChangeTargetKind.ExistingVault
                ? targetInspection.Kind == VaultSelectionKind.ExistingVault
                : targetInspection.Kind == VaultSelectionKind.EmptyFolder);
        if (!targetStillMatchesSelection)
        {
            return await RecordFailureAsync(
                active,
                "VAULT_CHANGE_TARGET_CHANGED",
                targetInspection.SafeErrorDetail
                    ?? "The selected folder changed after it was chosen. The previous Vault remains configured.",
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var save = await configuration
                .UpdateAsync(config => config with { VaultRoot = plan.TargetRoot }, cancellationToken)
                .ConfigureAwait(false);
            if (!save.IsSaved)
            {
                return await RecordFailureAsync(
                    active,
                    "VAULT_CONFIG_COMMIT_FAILED",
                    save.SafeErrorDetail ?? "The selected Vault path could not be written to configuration.",
                    cancellationToken).ConfigureAwait(false);
            }

            active = active with
            {
                Phase = VaultTransitionPhase.ConfigurationCommitted,
                UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                LastError = null,
            };
            await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);
            return VaultTransitionCommandResult.Success(plan);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return await RecordFailureAsync(
                active,
                "VAULT_CHANGE_FAILED",
                exception.Message,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<VaultTransitionRecoveryResult> RecoverFailedChangeTargetAsync(
        string failedTargetRoot,
        AppConfigurationStore configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var active = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (active is null
            || active.Kind != VaultTransitionKind.Change
            || active.Phase != VaultTransitionPhase.ConfigurationCommitted)
        {
            return VaultTransitionRecoveryResult.NoTransition();
        }

        if (!RootPathRules.AreSameRoot(active.TargetRoot, failedTargetRoot))
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_CHANGE_FAILURE_ROOT_MISMATCH",
                "The failed startup root does not match the pending Change Vault target.");
        }

        if (!Directory.Exists(active.SourceRoot))
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_CHANGE_PREVIOUS_ROOT_MISSING",
                "The selected Vault failed to open and the previous Vault folder is unavailable.");
        }

        var sourceInspection = _selectionPolicy.Inspect(active.SourceRoot);
        if (!sourceInspection.Accepted
            || sourceInspection.Kind != VaultSelectionKind.ExistingVault)
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_CHANGE_PREVIOUS_ROOT_INVALID",
                sourceInspection.SafeErrorDetail
                    ?? "The previous Vault can no longer be validated for recovery.");
        }

        var converged = await ConvergeConfiguredRootAsync(
            active.SourceRoot,
            configuration,
            cancellationToken).ConfigureAwait(false);
        if (!converged.Succeeded)
        {
            return converged;
        }

        await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
        return VaultTransitionRecoveryResult.Success(active.SourceRoot);
    }

    private async Task<VaultTransitionRecoveryResult> RecoverChangeStartupAsync(
        VaultTransitionRecord active,
        AppConfigurationStore configuration,
        CancellationToken cancellationToken)
    {
        if (active.ChangeTargetKind is null)
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_CHANGE_TARGET_KIND_MISSING",
                "Change Vault recovery has no recorded target kind.");
        }

        if (!Directory.Exists(active.SourceRoot))
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_CHANGE_PREVIOUS_ROOT_MISSING",
                "The previous Vault folder is unavailable during Change Vault recovery.");
        }

        if (active.Phase is VaultTransitionPhase.Prepared or VaultTransitionPhase.RuntimeRetired)
        {
            var sourceInspection = _selectionPolicy.Inspect(active.SourceRoot);
            if (!sourceInspection.Accepted
                || sourceInspection.Kind != VaultSelectionKind.ExistingVault)
            {
                return VaultTransitionRecoveryResult.Failed(
                    "VAULT_CHANGE_PREVIOUS_ROOT_INVALID",
                    sourceInspection.SafeErrorDetail
                        ?? "The previous Vault can no longer be validated.");
            }

            var sourceConverged = await ConvergeConfiguredRootAsync(
                active.SourceRoot,
                configuration,
                cancellationToken).ConfigureAwait(false);
            if (!sourceConverged.Succeeded)
            {
                return sourceConverged;
            }

            await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
            return VaultTransitionRecoveryResult.Success(active.SourceRoot);
        }

        if (active.Phase == VaultTransitionPhase.TargetBootstrapSucceeded)
        {
            var targetConverged = await ConvergeConfiguredRootAsync(
                active.TargetRoot,
                configuration,
                cancellationToken).ConfigureAwait(false);
            if (!targetConverged.Succeeded)
            {
                return targetConverged;
            }

            await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
            return VaultTransitionRecoveryResult.Success(active.TargetRoot);
        }

        if (active.Phase != VaultTransitionPhase.ConfigurationCommitted)
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_CHANGE_PHASE_INVALID",
                $"Change Vault cannot recover safely from phase '{active.Phase}'.");
        }

        if (!Directory.Exists(active.TargetRoot))
        {
            var fallback = await ConvergeConfiguredRootAsync(
                active.SourceRoot,
                configuration,
                cancellationToken).ConfigureAwait(false);
            if (!fallback.Succeeded)
            {
                return fallback;
            }

            await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
            return VaultTransitionRecoveryResult.Success(active.SourceRoot);
        }

        var targetInspection = _selectionPolicy.Inspect(active.TargetRoot, active.SourceRoot);
        var targetMatchesRecovery = targetInspection.Accepted
            && targetInspection.Kind is not null
            && (active.ChangeTargetKind == VaultChangeTargetKind.ExistingVault
                ? targetInspection.Kind == VaultSelectionKind.ExistingVault
                : targetInspection.Kind is VaultSelectionKind.EmptyFolder or VaultSelectionKind.ExistingVault);
        if (!targetMatchesRecovery)
        {
            var fallback = await ConvergeConfiguredRootAsync(
                active.SourceRoot,
                configuration,
                cancellationToken).ConfigureAwait(false);
            if (!fallback.Succeeded)
            {
                return VaultTransitionRecoveryResult.Failed(
                    "VAULT_CHANGE_TARGET_INVALID",
                    $"{targetInspection.SafeErrorDetail ?? "The selected target is no longer valid."} Previous Vault recovery also failed: {fallback.SafeErrorDetail}");
            }

            await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
            return VaultTransitionRecoveryResult.Success(active.SourceRoot);
        }

        var converged = await ConvergeConfiguredRootAsync(
            active.TargetRoot,
            configuration,
            cancellationToken).ConfigureAwait(false);
        return converged.Succeeded
            ? VaultTransitionRecoveryResult.Success(active.TargetRoot)
            : converged;
    }

    private async Task<VaultTransitionRecord?> RequireActiveChangeAsync(
        VaultChangePlan plan,
        CancellationToken cancellationToken)
    {
        var active = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (active is null
            || active.OperationId != plan.OperationId
            || active.Kind != VaultTransitionKind.Change
            || active.ChangeTargetKind != plan.TargetKind
            || !RootPathRules.AreSameRoot(active.SourceRoot, plan.SourceRoot)
            || !RootPathRules.AreSameRoot(active.TargetRoot, plan.TargetRoot))
        {
            return null;
        }

        return active;
    }
}
