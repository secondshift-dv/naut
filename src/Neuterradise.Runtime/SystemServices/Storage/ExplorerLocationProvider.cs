using System.Diagnostics;
using System.IO;

namespace Neuterradise.App.SystemServices.Storage;

public sealed class ExplorerLocationProvider
{
    private readonly VaultPaths _paths;
    private readonly Func<string, CancellationToken, Task> _directoryLauncher;
    private readonly Func<string, CancellationToken, Task> _fileSelector;
    private readonly Func<string, CancellationToken, Task> _defaultAppLauncher;

    public ExplorerLocationProvider(
        VaultPaths paths,
        Func<string, CancellationToken, Task>? directoryLauncher = null,
        Func<string, CancellationToken, Task>? fileSelector = null,
        Func<string, CancellationToken, Task>? defaultAppLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _directoryLauncher = directoryLauncher ?? LaunchWithShellAsync;
        _fileSelector = fileSelector ?? SelectInExplorerAsync;
        _defaultAppLauncher = defaultAppLauncher ?? LaunchWithShellAsync;
    }

    public async Task<StorageOperationResult> OpenVaultAsync(CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(_paths.Root) || !Directory.Exists(_paths.Root))
        {
            return new StorageOperationResult(
                StorageOperationStatus.SourceMissing,
                SafeErrorDetail: "The configured Vault folder does not exist on disk.");
        }

        return await LaunchDirectoryAsync(
            _paths.Root,
            "Windows Explorer could not be started for the Vault folder.",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StorageOperationResult> OpenProfileFolderAsync(
        string currentVaultRelativePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentVaultRelativePath))
        {
            return new StorageOperationResult(
                StorageOperationStatus.SourceMissing,
                SafeErrorDetail: "No current managed Profile folder is recorded.");
        }

        string absolutePath;
        try
        {
            absolutePath = _paths.ResolveVaultRelativePath(currentVaultRelativePath);
        }
        catch (ArgumentException)
        {
            return new StorageOperationResult(
                StorageOperationStatus.PathOutsideVault,
                SafeErrorDetail: "The recorded Profile folder is not inside the managed profiles area.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The recorded Profile folder could not be resolved safely.");
        }

        if (!Directory.Exists(absolutePath))
        {
            return new StorageOperationResult(
                StorageOperationStatus.SourceMissing,
                SafeErrorDetail: "The recorded current Profile folder does not exist on disk.");
        }

        return await LaunchDirectoryAsync(
            absolutePath,
            "Windows Explorer could not be started for the Profile folder.",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StorageOperationResult> ShowMediaInFolderAsync(
        string currentVaultRelativeFilePath,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolveManagedFile(currentVaultRelativeFilePath);
        if (resolved.Failure is not null)
        {
            return resolved.Failure;
        }

        try
        {
            await _fileSelector(resolved.AbsolutePath!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new StorageOperationResult(
                StorageOperationStatus.Failed,
                SafeErrorDetail: "Windows Explorer could not be started for the managed file.");
        }

        return new StorageOperationResult(StorageOperationStatus.Success);
    }

    public async Task<StorageOperationResult> OpenManagedFileInDefaultAppAsync(
        string currentVaultRelativeFilePath,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolveManagedFile(currentVaultRelativeFilePath);
        if (resolved.Failure is not null)
        {
            return resolved.Failure;
        }

        try
        {
            await _defaultAppLauncher(resolved.AbsolutePath!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new StorageOperationResult(
                StorageOperationStatus.Failed,
                SafeErrorDetail: "No default application could be started for the managed file.");
        }

        return new StorageOperationResult(StorageOperationStatus.Success);
    }

    private async Task<StorageOperationResult> LaunchDirectoryAsync(
        string absolutePath,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await _directoryLauncher(absolutePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new StorageOperationResult(
                StorageOperationStatus.Failed,
                SafeErrorDetail: failureMessage);
        }

        return new StorageOperationResult(StorageOperationStatus.Success);
    }

    private (string? AbsolutePath, StorageOperationResult? Failure) ResolveManagedFile(
        string currentVaultRelativeFilePath)
    {
        if (string.IsNullOrWhiteSpace(currentVaultRelativeFilePath))
        {
            return (null, new StorageOperationResult(
                StorageOperationStatus.SourceMissing,
                SafeErrorDetail: "No current managed file is recorded."));
        }

        string absolutePath;
        try
        {
            absolutePath = _paths.ResolveVaultRelativePath(currentVaultRelativeFilePath);
        }
        catch (ArgumentException)
        {
            return (null, new StorageOperationResult(
                StorageOperationStatus.PathOutsideVault,
                SafeErrorDetail: "The recorded managed file is not inside the managed profiles area."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The recorded managed file could not be resolved safely."));
        }

        if (!File.Exists(absolutePath))
        {
            return (null, new StorageOperationResult(
                StorageOperationStatus.SourceMissing,
                SafeErrorDetail: "The recorded current managed file does not exist on disk."));
        }

        return (absolutePath, null);
    }

    private static Task LaunchWithShellAsync(string absolutePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo(absolutePath)
        {
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("Windows did not create a shell process.");

        return Task.CompletedTask;
    }

    private static Task SelectInExplorerAsync(string absolutePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo("explorer.exe")
        {
            ArgumentList = { "/select,", absolutePath },
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Windows Explorer did not start.");

        return Task.CompletedTask;
    }
}
