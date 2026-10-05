using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Updates;

public sealed class UpdateHandoffService
{
    private readonly AppStatePaths _appState;
    private readonly InstallPaths _install;
    private readonly UpdateStateStore _stateStore;

    public UpdateHandoffService(AppStatePaths appState, InstallPaths install, UpdateStateStore? stateStore = null)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _stateStore = stateStore ?? new UpdateStateStore(_appState);
    }

    public async Task<UpdateHandoffResult> PrepareAsync(UpdateHandoff handoff, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        if (handoff.OperationId == Guid.Empty)
            return UpdateHandoffResult.Reject("Update operation id is invalid.");
        if (string.IsNullOrWhiteSpace(handoff.VaultRoot))
            return UpdateHandoffResult.Reject("Update handoff is missing the protected VaultRoot authority.");

        string sourcePayloadPath;
        string destinationInstallRoot;
        string expectedPayloadRoot;
        string vaultRoot;
        string replacementStagingRoot;
        string replacementBackupRoot;
        try
        {
            vaultRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(handoff.VaultRoot));
            _install.EnsureDisjointFrom(_appState, vaultRoot);
            sourcePayloadPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(handoff.SourcePayloadPath));
            destinationInstallRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(handoff.DestinationInstallRoot));
            expectedPayloadRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Path.Combine(_appState.UpdateStagingPath, handoff.OperationId.ToString("D"))));

            var installParent = Path.GetDirectoryName(destinationInstallRoot)
                ?? throw new IOException("InstallRoot cannot be a filesystem drive root.");
            var installName = Path.GetFileName(destinationInstallRoot);
            if (string.IsNullOrWhiteSpace(installName))
                throw new IOException("InstallRoot cannot be a filesystem drive root.");

            replacementStagingRoot = Path.Combine(installParent, $"{installName}.staged.{handoff.OperationId:N}");
            replacementBackupRoot = Path.Combine(installParent, $"{installName}.backup.{handoff.OperationId:N}");
            RootPathRules.EnsureDisjoint("UpdateStagingRoot", replacementStagingRoot, "AppStateRoot", _appState.Root);
            RootPathRules.EnsureDisjoint("UpdateBackupRoot", replacementBackupRoot, "AppStateRoot", _appState.Root);
            RootPathRules.EnsureDisjoint("UpdateStagingRoot", replacementStagingRoot, "VaultRoot", vaultRoot);
            RootPathRules.EnsureDisjoint("UpdateBackupRoot", replacementBackupRoot, "VaultRoot", vaultRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return UpdateHandoffResult.Reject("Update handoff contains an unsafe runtime path.");
        }

        if (!Directory.Exists(sourcePayloadPath))
            return UpdateHandoffResult.Reject("Validated update payload directory is unavailable.");
        if (!RootPathRules.AreSameRoot(sourcePayloadPath, expectedPayloadRoot))
            return UpdateHandoffResult.Reject("Update payload is not the approved operation-scoped staging directory.");
        if (!RootPathRules.AreSameRoot(destinationInstallRoot, _install.Root))
            return UpdateHandoffResult.Reject("Update destination is not the approved InstallRoot.");

        try
        {
            RootPathRules.RejectExistingReparsePoints(_appState.Root, sourcePayloadPath);
            handoff.Manifest.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or UnauthorizedAccessException)
        {
            return UpdateHandoffResult.Reject("Update handoff payload or manifest failed authority validation.");
        }

        var packageValidation = new UpdatePackageValidator(_install).Validate(handoff.Manifest, sourcePayloadPath);
        if (!packageValidation.IsAccepted)
            return UpdateHandoffResult.Reject(packageValidation.SafeError ?? "Update payload validation failed.");

        var helper = _install.UpdaterExecutablePath;
        if (!File.Exists(helper))
            return UpdateHandoffResult.Reject("Approved updater helper is unavailable.");

        var operationRoot = _appState.GetUpdateToolsPath(handoff.OperationId);
        Directory.CreateDirectory(operationRoot);

        var isolatedHelper = Path.Combine(operationRoot, Path.GetFileName(helper));
        try
        {
            File.Copy(helper, isolatedHelper, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return UpdateHandoffResult.Reject($"Failed to stage updater helper: {exception.Message}");
        }

        var normalizedHandoff = handoff with
        {
            SourcePayloadPath = sourcePayloadPath,
            DestinationInstallRoot = destinationInstallRoot,
            VaultRoot = vaultRoot,
        };
        var handoffPath = Path.Combine(operationRoot, "handoff.json");
        var json = JsonSerializer.Serialize(
            normalizedHandoff,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        var handoffBytes = Encoding.UTF8.GetBytes(json);
        var handoffSha256 = Convert.ToHexString(SHA256.HashData(handoffBytes)).ToLowerInvariant();
        var manifestAuthoritySha256 = normalizedHandoff.Manifest.ComputeAuthoritySha256();
        await File.WriteAllBytesAsync(handoffPath, handoffBytes, cancellationToken).ConfigureAwait(false);

        await _stateStore.PublishHandoffPendingAsync(handoff.OperationId, cancellationToken).ConfigureAwait(false);

        var start = new ProcessStartInfo(isolatedHelper)
        {
            WorkingDirectory = operationRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--handoff");
        start.ArgumentList.Add(handoffPath);
        start.ArgumentList.Add("--handoff-sha256");
        start.ArgumentList.Add(handoffSha256);
        start.ArgumentList.Add("--manifest-sha256");
        start.ArgumentList.Add(manifestAuthoritySha256);
        start.ArgumentList.Add("--parent-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            await _stateStore.PublishAbortedNoMutationAsync(
                handoff.OperationId,
                $"Helper launch failed: {exception.Message}",
                cancellationToken).ConfigureAwait(false);
            await new UpdateTerminalCleanup(_appState, _install, vaultRoot)
                .CleanupAbortedNoMutationAsync(
                    handoff.OperationId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return UpdateHandoffResult.Reject($"Updater helper could not be started: {exception.Message}");
        }

        if (process is null)
        {
            await _stateStore.PublishAbortedNoMutationAsync(
                handoff.OperationId,
                "Helper process could not be launched (null process returned).",
                cancellationToken).ConfigureAwait(false);
            await new UpdateTerminalCleanup(_appState, _install, vaultRoot)
                .CleanupAbortedNoMutationAsync(
                    handoff.OperationId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return UpdateHandoffResult.Reject("Updater helper could not be started.");
        }

        process.Dispose();
        return UpdateHandoffResult.Started();
    }
}

public sealed record UpdateHandoffResult(bool IsStarted, string? SafeError)
{
    public static UpdateHandoffResult Started() => new(true, null);
    public static UpdateHandoffResult Reject(string error) => new(false, error);
}
