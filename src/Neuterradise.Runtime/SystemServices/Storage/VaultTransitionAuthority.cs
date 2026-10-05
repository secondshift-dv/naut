namespace Neuterradise.App.SystemServices.Storage;

public sealed partial class VaultTransitionAuthority
{
    private readonly AppStatePaths _appState;
    private readonly InstallPaths _install;
    private readonly VaultTransitionStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly VaultMoveEngine _moveEngine;
    private readonly Func<string, string, bool> _sameVolumeResolver;
    private readonly VaultRootSelectionPolicy _selectionPolicy;

    public VaultTransitionAuthority(
        AppStatePaths appState,
        InstallPaths install,
        TimeProvider? timeProvider = null,
        Func<string, string, bool>? sameVolumeResolver = null)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _store = new VaultTransitionStore(appState);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _moveEngine = new VaultMoveEngine(appState);
        _sameVolumeResolver = sameVolumeResolver ?? VaultVolumeIdentity.AreSameVolume;
        _selectionPolicy = new VaultRootSelectionPolicy(install, appState);
    }

    public async Task<VaultTransitionCommandResult> BeginRenameAsync(
        string currentRoot,
        string requestedName,
        CancellationToken cancellationToken = default)
    {
        if (!VaultNameRules.TryNormalize(requestedName, out var targetName, out var validationError))
        {
            return VaultTransitionCommandResult.Failed("VAULT_NAME_INVALID", validationError!);
        }

        var sourceRoot = RootPathRules.NormalizeRoot(currentRoot, nameof(currentRoot));
        var sourceLeaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceRoot));
        if (string.Equals(sourceLeaf, targetName, StringComparison.OrdinalIgnoreCase))
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_NAME_UNCHANGED",
                "The new Vault name must be different from the current folder name.");
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(sourceRoot));
        if (string.IsNullOrWhiteSpace(parent))
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_RENAME_ROOT_UNSUPPORTED",
                "A filesystem root cannot be renamed as a Vault.");
        }

        var targetRoot = RootPathRules.NormalizeRoot(Path.Combine(parent, targetName), nameof(requestedName));
        var targetPaths = new VaultPaths(targetRoot);
        try
        {
            targetPaths.EnsureDisjointFrom(_install, _appState);
        }
        catch (ArgumentException exception)
        {
            return VaultTransitionCommandResult.Failed("VAULT_RENAME_TARGET_INVALID", exception.Message);
        }

        if (!Directory.Exists(sourceRoot))
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_RENAME_SOURCE_MISSING",
                "The current Vault folder is no longer available.");
        }

        if (Directory.Exists(targetRoot) || File.Exists(targetRoot))
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_RENAME_TARGET_EXISTS",
                "A file or folder already uses that name next to the current Vault.");
        }

        var active = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (active is not null)
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_TRANSITION_PENDING",
                "A previous Vault transition still requires startup recovery.");
        }

        var operationId = Guid.NewGuid();
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var record = new VaultTransitionRecord(
            operationId,
            VaultTransitionKind.Rename,
            VaultTransitionPhase.Prepared,
            sourceRoot,
            targetRoot,
            now,
            now);

        await _store.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        return VaultTransitionCommandResult.Success(
            new VaultRenamePlan(operationId, sourceRoot, targetRoot, targetName));
    }

    public async Task<VaultTransitionCommandResult> MarkRuntimeRetiredAsync(
        VaultRenamePlan plan,
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

        var active = await RequireActiveRenameAsync(plan, cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_TRANSITION_MISSING",
                "The Vault rename journal is missing or no longer matches this operation.");
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await _store.WriteAsync(
            active with
            {
                Phase = VaultTransitionPhase.RuntimeRetired,
                UpdatedAtUnixMs = now,
                LastError = null,
            },
            cancellationToken).ConfigureAwait(false);
        return VaultTransitionCommandResult.Success(plan);
    }

    public async Task<VaultTransitionCommandResult> ApplyRenameAfterRetirementAsync(
        VaultRenamePlan plan,
        AppConfigurationStore configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(configuration);

        var active = await RequireActiveRenameAsync(plan, cancellationToken).ConfigureAwait(false);
        if (active is null || active.Phase != VaultTransitionPhase.RuntimeRetired)
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_RUNTIME_NOT_RETIRED",
                "Vault rename was refused because runtime retirement was not proven.");
        }

        try
        {
            if (!Directory.Exists(plan.SourceRoot))
            {
                throw new DirectoryNotFoundException("The current Vault folder disappeared before rename.");
            }

            if (Directory.Exists(plan.TargetRoot) || File.Exists(plan.TargetRoot))
            {
                throw new IOException("The destination Vault folder already exists.");
            }

            Directory.Move(plan.SourceRoot, plan.TargetRoot);

            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            active = active with
            {
                Phase = VaultTransitionPhase.FilesystemApplied,
                UpdatedAtUnixMs = now,
                LastError = null,
            };
            await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);

            var save = await configuration
                .UpdateAsync(config => config with { VaultRoot = plan.TargetRoot }, cancellationToken)
                .ConfigureAwait(false);
            if (!save.IsSaved)
            {
                return await RecordFailureAsync(
                    active,
                    "VAULT_CONFIG_COMMIT_FAILED",
                    save.SafeErrorDetail ?? "The renamed Vault path could not be written to configuration.",
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
            or ArgumentException
            or DirectoryNotFoundException)
        {
            return await RecordFailureAsync(
                active,
                "VAULT_RENAME_FAILED",
                exception.Message,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<VaultTransitionRecoveryResult> RecoverStartupAsync(
        AppConfigurationStore configuration,
        IProgress<VaultMoveProgress>? moveProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var active = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            return VaultTransitionRecoveryResult.NoTransition();
        }

        return active.Kind switch
        {
            VaultTransitionKind.Rename => await RecoverRenameStartupAsync(
                active,
                configuration,
                cancellationToken).ConfigureAwait(false),
            VaultTransitionKind.Move => await RecoverMoveStartupAsync(
                active,
                configuration,
                moveProgress,
                cancellationToken).ConfigureAwait(false),
            VaultTransitionKind.Change => await RecoverChangeStartupAsync(
                active,
                configuration,
                cancellationToken).ConfigureAwait(false),
            _ => VaultTransitionRecoveryResult.Failed(
                "VAULT_TRANSITION_KIND_UNSUPPORTED",
                $"Startup recovery does not yet support transition kind '{active.Kind}'."),
        };
    }

    private async Task<VaultTransitionRecoveryResult> RecoverRenameStartupAsync(
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
                    ? "Both the previous and renamed Vault folders exist; automatic recovery stopped to preserve data."
                    : "Neither the previous nor renamed Vault folder exists; automatic recovery stopped to preserve data.");
        }

        var effectiveRoot = targetExists ? active.TargetRoot : active.SourceRoot;
        try
        {
            var converged = await ConvergeConfiguredRootAsync(
                effectiveRoot,
                configuration,
                cancellationToken).ConfigureAwait(false);
            if (!converged.Succeeded)
            {
                return converged;
            }

            await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
            return VaultTransitionRecoveryResult.Success(effectiveRoot);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_TRANSITION_RECOVERY_FAILED",
                exception.Message);
        }
    }

    private async Task<VaultTransitionRecoveryResult> ConvergeConfiguredRootAsync(
        string effectiveRoot,
        AppConfigurationStore configuration,
        CancellationToken cancellationToken)
    {
        var effectivePaths = new VaultPaths(effectiveRoot);
        effectivePaths.EnsureDisjointFrom(_install, _appState);
        RootPathRules.RejectExistingReparsePoints(effectivePaths.Root, effectivePaths.Root);

        var load = await configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (load.Status is AppConfigurationStatus.AccessDenied or AppConfigurationStatus.Unreadable)
        {
            return VaultTransitionRecoveryResult.Failed(
                "VAULT_TRANSITION_CONFIG_UNAVAILABLE",
                load.SafeErrorDetail ?? "Configuration could not be read during Vault transition recovery.");
        }

        if (string.IsNullOrWhiteSpace(load.Configuration.VaultRoot)
            || !RootPathRules.AreSameRoot(load.Configuration.VaultRoot, effectiveRoot))
        {
            var save = await configuration
                .UpdateAsync(config => config with { VaultRoot = effectiveRoot }, cancellationToken)
                .ConfigureAwait(false);
            if (!save.IsSaved)
            {
                return VaultTransitionRecoveryResult.Failed(
                    "VAULT_TRANSITION_CONFIG_RECOVERY_FAILED",
                    save.SafeErrorDetail ?? "Configuration could not be converged to the surviving Vault folder.");
            }
        }

        return VaultTransitionRecoveryResult.Success(effectiveRoot);
    }
    private async Task<VaultTransitionRecord?> RequireActiveRenameAsync(
        VaultRenamePlan plan,
        CancellationToken cancellationToken)
    {
        var active = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (active is null
            || active.OperationId != plan.OperationId
            || active.Kind != VaultTransitionKind.Rename
            || !RootPathRules.AreSameRoot(active.SourceRoot, plan.SourceRoot)
            || !RootPathRules.AreSameRoot(active.TargetRoot, plan.TargetRoot))
        {
            return null;
        }

        return active;
    }

    private async Task<VaultTransitionCommandResult> RecordFailureAsync(
        VaultTransitionRecord active,
        string code,
        string detail,
        CancellationToken cancellationToken)
    {
        try
        {
            await _store.WriteAsync(
                active with
                {
                    UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    LastError = $"{code}:{detail}",
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the primary failure; startup reconciliation also reasons from filesystem truth.
        }

        return VaultTransitionCommandResult.Failed(code, detail);
    }
}
