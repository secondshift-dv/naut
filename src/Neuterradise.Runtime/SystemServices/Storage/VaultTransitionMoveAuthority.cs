using System.Text.Json;

namespace Neuterradise.App.SystemServices.Storage;

public sealed partial class VaultTransitionAuthority
{
    public async Task<VaultTransitionCommandResult> BeginMoveAsync(
        string currentRoot,
        string targetParent,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = RootPathRules.NormalizeRoot(currentRoot, nameof(currentRoot));
        var sourcePaths = new VaultPaths(sourceRoot);
        var sourceIdentity = await new VaultIdentityStore(_timeProvider)
            .TryReadAsync(sourcePaths, cancellationToken)
            .ConfigureAwait(false);
        if (sourceIdentity is null)
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_MOVE_IDENTITY_MISSING",
                "The current Vault has no technical identity yet. Reopen it before moving.");
        }

        var normalizedParent = RootPathRules.NormalizeRoot(targetParent, nameof(targetParent));
        if (!Directory.Exists(sourceRoot))
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_MOVE_SOURCE_MISSING",
                "The current Vault folder is no longer available.");
        }

        if (!Directory.Exists(normalizedParent))
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_MOVE_PARENT_MISSING",
                "The selected destination folder does not exist.");
        }

        var vaultName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceRoot));
        if (string.IsNullOrWhiteSpace(vaultName))
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_MOVE_NAME_INVALID",
                "The current Vault folder does not have a movable folder name.");
        }

        var targetRoot = RootPathRules.NormalizeRoot(
            Path.Combine(normalizedParent, vaultName),
            nameof(targetParent));
        if (RootPathRules.AreSameRoot(sourceRoot, targetRoot))
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_MOVE_UNCHANGED",
                "Choose a different destination folder.");
        }

        try
        {
            RootPathRules.EnsureDisjoint("SourceVault", sourceRoot, "TargetVault", targetRoot);
            var targetPaths = new VaultPaths(targetRoot);
            targetPaths.EnsureDisjointFrom(_install, _appState);
            RootPathRules.RejectExistingReparsePoints(
                Path.GetPathRoot(normalizedParent) ?? normalizedParent,
                normalizedParent);
        }
        catch (ArgumentException exception)
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_MOVE_TARGET_INVALID",
                exception.Message);
        }

        if (Directory.Exists(targetRoot) || File.Exists(targetRoot))
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_MOVE_TARGET_EXISTS",
                "A folder or file with the Vault name already exists in the selected destination.");
        }

        try
        {
            ProbeWritableDirectory(normalizedParent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_MOVE_TARGET_NOT_WRITABLE",
                exception.Message);
        }

        var active = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (active is not null)
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_TRANSITION_PENDING",
                "A previous Vault transition still requires startup recovery.");
        }

        var operationId = Guid.NewGuid();
        var mode = _sameVolumeResolver(sourceRoot, normalizedParent)
            ? VaultMoveMode.SameVolume
            : VaultMoveMode.CrossVolume;
        if (mode == VaultMoveMode.CrossVolume)
        {
            try
            {
                var estimatedBytes = _moveEngine.EstimateSourceBytes(sourceRoot);
                EnsureDestinationCapacity(normalizedParent, estimatedBytes);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or OverflowException)
            {
                return VaultTransitionCommandResult.Failed(
                    "VAULT_MOVE_PREFLIGHT_FAILED",
                    exception.Message);
            }
        }

        var stagingRoot = mode == VaultMoveMode.CrossVolume
            ? Path.Combine(normalizedParent, $".naut-move-{operationId:N}.staging")
            : null;
        if (stagingRoot is not null
            && (Directory.Exists(stagingRoot) || File.Exists(stagingRoot)))
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_MOVE_STAGING_EXISTS",
                "The operation staging path already exists.");
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var record = new VaultTransitionRecord(
            operationId,
            VaultTransitionKind.Move,
            VaultTransitionPhase.Prepared,
            sourceRoot,
            targetRoot,
            now,
            now,
            MoveMode: mode,
            StagingRoot: stagingRoot,
            VaultId: sourceIdentity.VaultId);
        await _store.WriteAsync(record, cancellationToken).ConfigureAwait(false);

        return VaultTransitionCommandResult.Success(new VaultMovePlan(
            operationId,
            sourceRoot,
            targetRoot,
            normalizedParent,
            vaultName,
            sourceIdentity.VaultId,
            mode,
            stagingRoot));
    }

    public async Task<VaultTransitionCommandResult> MarkRuntimeRetiredAsync(
        VaultMovePlan plan,
        VaultRuntimeRetirementProof retirementProof,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(retirementProof);
        if (!RootPathRules.AreSameRoot(plan.SourceRoot, retirementProof.VaultRoot)
            || plan.VaultId != retirementProof.VaultId)
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_RETIREMENT_PROOF_MISMATCH",
                "Runtime retirement proof does not belong to this Vault identity/root.");
        }

        var active = await RequireActiveMoveAsync(plan, cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_TRANSITION_MISSING",
                "The Vault Move journal is missing or no longer matches this operation.");
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

    public async Task<VaultTransitionCommandResult> ApplyMoveAfterRetirementAsync(
        VaultMovePlan plan,
        AppConfigurationStore configuration,
        IProgress<VaultMoveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(configuration);

        var active = await RequireActiveMoveAsync(plan, cancellationToken).ConfigureAwait(false);
        if (active is null || active.Phase != VaultTransitionPhase.RuntimeRetired)
        {
            return VaultTransitionCommandResult.Failed(
                "VAULT_RUNTIME_NOT_RETIRED",
                "Vault Move was refused because runtime retirement was not proven.");
        }

        return plan.Mode switch
        {
            VaultMoveMode.SameVolume => await ApplySameVolumeMoveAsync(
                plan,
                active,
                configuration,
                cancellationToken).ConfigureAwait(false),
            VaultMoveMode.CrossVolume => await ApplyCrossVolumeMoveAsync(
                plan,
                active,
                configuration,
                progress,
                cancellationToken).ConfigureAwait(false),
            _ => VaultTransitionCommandResult.Failed(
                "VAULT_MOVE_MODE_INVALID",
                "Vault Move mode is invalid."),
        };
    }

    private async Task<VaultTransitionCommandResult> ApplySameVolumeMoveAsync(
        VaultMovePlan plan,
        VaultTransitionRecord active,
        AppConfigurationStore configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(plan.SourceRoot))
            {
                throw new DirectoryNotFoundException("The current Vault disappeared before Move.");
            }

            if (Directory.Exists(plan.TargetRoot) || File.Exists(plan.TargetRoot))
            {
                throw new IOException("The Move destination already exists.");
            }

            Directory.Move(plan.SourceRoot, plan.TargetRoot);
            await EnsureExpectedMovedVaultIdentityAsync(
                plan.TargetRoot,
                active.VaultId,
                cancellationToken).ConfigureAwait(false);
            active = active with
            {
                Phase = VaultTransitionPhase.FilesystemApplied,
                UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                LastError = null,
            };
            await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);

            var converged = await ConvergeConfiguredRootAsync(
                plan.TargetRoot,
                configuration,
                cancellationToken).ConfigureAwait(false);
            if (!converged.Succeeded)
            {
                return await RecordFailureAsync(
                    active,
                    converged.ErrorCode ?? "VAULT_CONFIG_COMMIT_FAILED",
                    converged.SafeErrorDetail ?? "The moved Vault path could not be saved.",
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
                "VAULT_MOVE_FAILED",
                exception.Message,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<VaultTransitionCommandResult> ApplyCrossVolumeMoveAsync(
        VaultMovePlan plan,
        VaultTransitionRecord active,
        AppConfigurationStore configuration,
        IProgress<VaultMoveProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            active = active with
            {
                Phase = VaultTransitionPhase.Copying,
                UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                LastError = null,
            };
            await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);

            var inventory = await _moveEngine
                .GetOrCreateSourceInventoryAsync(active, progress, cancellationToken)
                .ConfigureAwait(false);
            active = active with
            {
                TotalBytes = inventory.TotalBytes,
                TotalFiles = inventory.Files.Count,
                UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            };
            await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);

            var stagedBytes = _moveEngine.EstimateExistingStagingBytes(active);
            EnsureDestinationCapacity(
                plan.TargetParent,
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
                throw new IOException(
                    "The source Vault changed while it was being copied. Destination promotion was refused.");
            }

            active = active with
            {
                Phase = VaultTransitionPhase.DestinationVerified,
                UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            };
            await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(active.StagingRoot)
                || !Directory.Exists(active.StagingRoot))
            {
                throw new DirectoryNotFoundException("The verified Move staging folder is missing.");
            }

            if (Directory.Exists(active.TargetRoot) || File.Exists(active.TargetRoot))
            {
                throw new IOException("The Move destination appeared before promotion.");
            }

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
                return await RecordFailureAsync(
                    active,
                    converged.ErrorCode ?? "VAULT_CONFIG_COMMIT_FAILED",
                    converged.SafeErrorDetail ?? "The moved Vault path could not be saved.",
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or DirectoryNotFoundException
            or JsonException)
        {
            return await RecordFailureAsync(
                active,
                "VAULT_MOVE_FAILED",
                exception.Message,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PersistMoveProgressAsync(
        VaultMoveProgress progress,
        CancellationToken cancellationToken)
    {
        var active = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (active is null || active.Kind != VaultTransitionKind.Move)
        {
            throw new InvalidOperationException("Vault Move progress cannot be persisted without an active Move journal.");
        }

        active = active with
        {
            Phase = VaultTransitionPhase.Copying,
            CompletedFiles = progress.CompletedFiles,
            CompletedBytes = progress.CompletedBytes,
            TotalFiles = progress.TotalFiles,
            TotalBytes = progress.TotalBytes,
            UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            LastError = null,
        };
        await _store.WriteAsync(active, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureExpectedMovedVaultIdentityAsync(
        string root,
        Guid? expectedVaultId,
        CancellationToken cancellationToken)
    {
        if (expectedVaultId is not Guid expected || expected == Guid.Empty)
        {
            throw new IOException("Vault Move journal has no valid VaultId.");
        }

        var identity = await new VaultIdentityStore(_timeProvider)
            .TryReadAsync(new VaultPaths(root), cancellationToken)
            .ConfigureAwait(false);
        if (identity is null || identity.VaultId != expected)
        {
            throw new IOException("The moved Vault identity does not match the source VaultId.");
        }
    }

    private async Task<VaultTransitionRecord?> RequireActiveMoveAsync(
        VaultMovePlan plan,
        CancellationToken cancellationToken)
    {
        var active = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (active is null
            || active.OperationId != plan.OperationId
            || active.Kind != VaultTransitionKind.Move
            || active.MoveMode != plan.Mode
            || active.VaultId != plan.VaultId
            || !RootPathRules.AreSameRoot(active.SourceRoot, plan.SourceRoot)
            || !RootPathRules.AreSameRoot(active.TargetRoot, plan.TargetRoot))
        {
            return null;
        }

        return active;
    }

    private static void ProbeWritableDirectory(string directory)
    {
        var probe = Path.Combine(directory, $".naut-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
            stream.WriteByte(0x4E);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
    }

    private static void EnsureDestinationCapacity(string targetParent, long requiredBytes)
    {
        var root = Path.GetPathRoot(RootPathRules.NormalizeRoot(targetParent, nameof(targetParent)));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new IOException("The Move destination has no local volume root.");
        }

        if (requiredBytes < 0)
        {
            throw new IOException("The Move destination capacity requirement is invalid.");
        }

        var drive = new DriveInfo(root);
        const long reserveBytes = 64L * 1024L * 1024L;
        long requiredWithReserve;
        try
        {
            requiredWithReserve = checked(requiredBytes + reserveBytes);
        }
        catch (OverflowException exception)
        {
            throw new IOException("The Move destination capacity requirement overflowed.", exception);
        }

        if (drive.AvailableFreeSpace < requiredWithReserve)
        {
            throw new IOException(
                $"The Move destination needs at least {requiredWithReserve:N0} free bytes, including safety reserve.");
        }
    }
}
