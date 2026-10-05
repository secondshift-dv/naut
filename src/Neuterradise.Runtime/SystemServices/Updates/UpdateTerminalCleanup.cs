using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Updates;

public sealed record UpdateTerminalCleanupResult(
    bool Success,
    string? SafeError)
{
    public static UpdateTerminalCleanupResult Completed() =>
        new(true, null);

    public static UpdateTerminalCleanupResult Retry(string error) =>
        new(false, error);
}

public sealed class UpdateTerminalCleanup
{
    private readonly AppStatePaths _appState;
    private readonly InstallPaths _install;
    private readonly string? _vaultRoot;

    public UpdateTerminalCleanup(
        AppStatePaths appState,
        InstallPaths install,
        string? vaultRoot)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _vaultRoot = string.IsNullOrWhiteSpace(vaultRoot)
            ? null
            : RootPathRules.NormalizeRoot(vaultRoot, nameof(vaultRoot));
        _install.EnsureDisjointFrom(_appState, _vaultRoot);
    }

    public Task<UpdateTerminalCleanupResult> CleanupAsync(
        Guid operationId,
        string? persistedBackupPath,
        CancellationToken cancellationToken = default) =>
        CleanupCoreAsync(
            operationId,
            persistedBackupPath,
            deleteBackup: true,
            cancellationToken);

    public Task<UpdateTerminalCleanupResult> CleanupAbortedNoMutationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        CleanupCoreAsync(
            operationId,
            persistedBackupPath: null,
            deleteBackup: false,
            cancellationToken);

    private Task<UpdateTerminalCleanupResult> CleanupCoreAsync(
        Guid operationId,
        string? persistedBackupPath,
        bool deleteBackup,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException(
                "Update operation id is required.",
                nameof(operationId));

        cancellationToken.ThrowIfCancellationRequested();

        CleanupPaths paths;
        try
        {
            paths = ResolvePaths(operationId, persistedBackupPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or UnauthorizedAccessException)
        {
            return Task.FromResult(
                UpdateTerminalCleanupResult.Retry(
                    $"Update terminal cleanup path authority is invalid ({exception.GetType().Name})."));
        }

        try
        {
            DeleteDirectoryTreeNoFollow(paths.StagingRoot, cancellationToken);
            DeleteDirectoryTreeNoFollow(paths.PayloadRoot, cancellationToken);

            if (deleteBackup)
            {
                // Backup is the rollback authority. It is intentionally retained until the
                // replacement has reached Ready and CleanupPending has been persisted.
                DeleteDirectoryTreeNoFollow(paths.BackupRoot, cancellationToken);
            }

            // Handoff/recovery/helper evidence is retired last. A crash before this
            // point leaves enough durable authority for the next idempotent retry.
            DeleteDirectoryTreeNoFollow(paths.ToolsRoot, cancellationToken);
            return Task.FromResult(UpdateTerminalCleanupResult.Completed());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return Task.FromResult(
                UpdateTerminalCleanupResult.Retry(
                    $"Update terminal cleanup is incomplete and will be retried ({exception.GetType().Name})."));
        }
    }

    private CleanupPaths ResolvePaths(
        Guid operationId,
        string? persistedBackupPath)
    {
        var installRoot =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_install.Root));
        var parent = Path.GetDirectoryName(installRoot)
            ?? throw new IOException(
                "InstallRoot cannot be a filesystem drive root.");
        var name = Path.GetFileName(installRoot);
        if (string.IsNullOrWhiteSpace(name))
            throw new IOException(
                "InstallRoot cannot be a filesystem drive root.");

        var stagingRoot = Path.GetFullPath(
            Path.Combine(parent, $"{name}.staged.{operationId:N}"));
        var backupRoot = Path.GetFullPath(
            Path.Combine(parent, $"{name}.backup.{operationId:N}"));
        var payloadRoot = Path.GetFullPath(
            Path.Combine(
                _appState.UpdateStagingPath,
                operationId.ToString("D")));
        var toolsRoot = Path.GetFullPath(
            _appState.GetUpdateToolsPath(operationId));

        RootPathRules.EnsureDisjoint(
            "UpdateStagingRoot",
            stagingRoot,
            "InstallRoot",
            installRoot);
        RootPathRules.EnsureDisjoint(
            "UpdateBackupRoot",
            backupRoot,
            "InstallRoot",
            installRoot);
        RootPathRules.EnsureDisjoint(
            "UpdateStagingRoot",
            stagingRoot,
            "AppStateRoot",
            _appState.Root);
        RootPathRules.EnsureDisjoint(
            "UpdateBackupRoot",
            backupRoot,
            "AppStateRoot",
            _appState.Root);

        if (!RootPathRules.IsWithinOrEqual(_appState.Root, payloadRoot)
            || !RootPathRules.IsWithinOrEqual(_appState.Root, toolsRoot))
        {
            throw new IOException(
                "Update cleanup AppState path escaped its authority.");
        }

        if (!string.IsNullOrWhiteSpace(_vaultRoot))
        {
            RootPathRules.EnsureDisjoint(
                "UpdateStagingRoot",
                stagingRoot,
                "VaultRoot",
                _vaultRoot);
            RootPathRules.EnsureDisjoint(
                "UpdateBackupRoot",
                backupRoot,
                "VaultRoot",
                _vaultRoot);
            RootPathRules.EnsureDisjoint(
                "UpdatePayloadRoot",
                payloadRoot,
                "VaultRoot",
                _vaultRoot);
            RootPathRules.EnsureDisjoint(
                "UpdateToolsRoot",
                toolsRoot,
                "VaultRoot",
                _vaultRoot);
        }

        if (!string.IsNullOrWhiteSpace(persistedBackupPath))
        {
            var persisted = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(persistedBackupPath));
            if (!RootPathRules.AreSameRoot(persisted, backupRoot))
            {
                throw new IOException(
                    "Persisted update backup path contradicts deterministic cleanup authority.");
            }
        }

        return new CleanupPaths(
            stagingRoot,
            backupRoot,
            payloadRoot,
            toolsRoot);
    }

    private static void DeleteDirectoryTreeNoFollow(
        string root,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(root))
            return;

        var rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException(
                "Update cleanup root cannot be a reparse point.");

        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var isReparsePoint =
                (attributes & FileAttributes.ReparsePoint) != 0;

            if (isDirectory)
            {
                if (isReparsePoint)
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    DeleteDirectoryTreeNoFollow(entry, cancellationToken);
                }
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(root, recursive: false);
    }

    private sealed record CleanupPaths(
        string StagingRoot,
        string BackupRoot,
        string PayloadRoot,
        string ToolsRoot);
}
