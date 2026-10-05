namespace Neuterradise.App.SystemServices.Storage;

public sealed partial class VaultTransitionAuthority
{
    public async Task<VaultTransitionCommandResult> FinalizeSuccessfulBootstrapAsync(
        VaultSuccessfulBootstrapProof bootstrapProof,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bootstrapProof);
        var active = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            return VaultTransitionCommandResult.Success();
        }

        if (active.Kind == VaultTransitionKind.Change)
        {
            if (active.Phase is not (
                VaultTransitionPhase.ConfigurationCommitted
                or VaultTransitionPhase.TargetBootstrapSucceeded))
            {
                return VaultTransitionCommandResult.Success();
            }

            if (!RootPathRules.AreSameRoot(active.TargetRoot, bootstrapProof.VaultRoot))
            {
                return VaultTransitionCommandResult.Failed(
                    "VAULT_CHANGE_BOOTSTRAP_PROOF_MISMATCH",
                    "The successful bootstrap proof does not belong to the selected Vault.");
            }

            if (active.Phase != VaultTransitionPhase.TargetBootstrapSucceeded)
            {
                active = active with
                {
                    Phase = VaultTransitionPhase.TargetBootstrapSucceeded,
                    UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    LastError = null,
                };
                await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);
            }

            await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
            return VaultTransitionCommandResult.Success();
        }

        if (active.Kind != VaultTransitionKind.Move
            || active.MoveMode != VaultMoveMode.CrossVolume
            || active.Phase is not (
                VaultTransitionPhase.ConfigurationCommitted
                or VaultTransitionPhase.TargetBootstrapSucceeded))
        {
            return VaultTransitionCommandResult.Success();
        }

        if (!RootPathRules.AreSameRoot(active.TargetRoot, bootstrapProof.VaultRoot)
            || active.VaultId is not Guid expectedVaultId
            || expectedVaultId != bootstrapProof.VaultId)
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_MOVE_BOOTSTRAP_PROOF_MISMATCH",
                "The successful bootstrap proof does not belong to the moved Vault identity/root.");
        }

        if (active.Phase != VaultTransitionPhase.TargetBootstrapSucceeded)
        {
            active = active with
            {
                Phase = VaultTransitionPhase.TargetBootstrapSucceeded,
                UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                LastError = null,
            };
            await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);
        }

        var cleanup = await _moveEngine
            .CleanupSourceAfterSuccessfulBootstrapAsync(active, cancellationToken)
            .ConfigureAwait(false);
        if (!cleanup.Succeeded)
        {
            return await RecordFailureAsync(
                active,
                cleanup.ErrorCode ?? "VAULT_MOVE_SOURCE_CLEANUP_FAILED",
                cleanup.SafeErrorDetail ?? "Old Vault cleanup was deferred.",
                cancellationToken).ConfigureAwait(false);
        }

        _moveEngine.DeleteOperationState(active.OperationId);
        await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
        return VaultTransitionCommandResult.Success();
    }
}
