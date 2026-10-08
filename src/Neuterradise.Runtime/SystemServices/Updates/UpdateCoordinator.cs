using System.Diagnostics;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Updates;

/// <summary>
/// Application-scoped authority for the manual shipping update flow. It composes the existing
/// check/download/stage/handoff services and never performs automatic startup network access.
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    public static readonly TimeSpan UpdateShutdownBudget = TimeSpan.FromSeconds(60);
    public const string StatusNotChecked = "not_checked";
    public const string StatusFeedSaved = "feed_saved";
    public const string StatusChecking = "checking";
    public const string StatusAvailable = "available";
    public const string StatusUnavailable = "unavailable";
    public const string StatusPlanning = "planning";
    public const string StatusDownloading = "downloading";
    public const string StatusStaging = "staging";
    public const string StatusPreparing = "preparing";
    public const string StatusRestarting = "restarting";

    private readonly CatalogMutationAdmissionGate _mutationAdmission;
    private readonly AppConfigurationStore _configuration;
    private readonly AppStatePaths _appState;
    private readonly InstallPaths _install;
    private readonly string _vaultRoot;
    private readonly UpdateCheckService _check;
    private readonly UpdateDownloadService _download;
    private readonly LocalUpdatePackageReader _localPackageReader;
    private readonly UpdateTrustPolicy _trust;
    private readonly UpdatePackageStager _stager;
    private readonly UpdateHandoffService _handoff;
    private readonly Action<DateTimeOffset> _requestControlledShutdown;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateGate = new();

    private UpdatePresentationState _state =
        new(StatusNotChecked, null, false, null, false, null);
    private UpdateManifest? _acceptedManifest;
    private Uri? _acceptedFeed;
    private bool _disposed;

    public UpdateCoordinator(
        CatalogMutationAdmissionGate mutationAdmission,
        AppConfigurationStore configuration,
        AppStatePaths appState,
        InstallPaths install,
        string vaultRoot,
        UpdateCheckService check,
        UpdateDownloadService download,
        LocalUpdatePackageReader localPackageReader,
        UpdateTrustPolicy trust,
        UpdatePackageStager stager,
        UpdateHandoffService handoff,
        Action<DateTimeOffset> requestControlledShutdown)
    {
        _mutationAdmission = mutationAdmission ?? throw new ArgumentNullException(nameof(mutationAdmission));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _download = download ?? throw new ArgumentNullException(nameof(download));
        _localPackageReader = localPackageReader ?? throw new ArgumentNullException(nameof(localPackageReader));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _stager = stager ?? throw new ArgumentNullException(nameof(stager));
        _handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));
        _requestControlledShutdown = requestControlledShutdown
            ?? throw new ArgumentNullException(nameof(requestControlledShutdown));

        _vaultRoot = RootPathRules.NormalizeRoot(vaultRoot, nameof(vaultRoot));
        _install.EnsureDisjointFrom(_appState, _vaultRoot);
    }

    public event Action<UpdatePresentationState>? StateChanged;

    public UpdatePresentationState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public bool HasAcceptedCandidate
    {
        get
        {
            lock (_stateGate)
            {
                return _acceptedManifest is not null && _acceptedFeed is not null;
            }
        }
    }

    public async Task<UpdateCommandResult> SaveFeedAsync(
        string feedText,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _mutationAdmission.Enter(nameof(SaveFeedAsync));
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var trust = new UpdateTrustConfiguration(feedText?.Trim(), AllowExplicitLocalPackage: false);
            if (!trust.TryGetConfiguredFeed(out var feed))
            {
                ClearCandidate();
                const string error =
                    "Enter an absolute HTTPS update feed without user info or a fragment.";
                SetState(new UpdatePresentationState(
                    StatusUnavailable,
                    error,
                    false,
                    null,
                    false,
                    null));
                return UpdateCommandResult.Failed(error);
            }

            var save = await _configuration.UpdateAsync(
                configuration => configuration with
                {
                    Updates = configuration.Updates with { FeedUrl = feed.AbsoluteUri }
                },
                cancellationToken).ConfigureAwait(false);

            if (!save.IsSaved)
            {
                ClearCandidate();
                var error = save.SafeErrorDetail ?? "The update feed could not be saved.";
                SetState(new UpdatePresentationState(
                    StatusUnavailable,
                    error,
                    false,
                    null,
                    false,
                    null));
                return UpdateCommandResult.Failed(error);
            }

            ClearCandidate();
            SetState(new UpdatePresentationState(
                StatusFeedSaved,
                null,
                false,
                null,
                false,
                null));
            return UpdateCommandResult.Success();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<UpdateCommandResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var load = await _configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
            var trust = new UpdateTrustConfiguration(
                load.Configuration.Updates.FeedUrl,
                AllowExplicitLocalPackage: false);
            if (!trust.TryGetConfiguredFeed(out var feed))
            {
                ClearCandidate();
                const string error = "No valid configured HTTPS update feed is available.";
                SetState(new UpdatePresentationState(
                    StatusUnavailable,
                    error,
                    false,
                    null,
                    false,
                    null));
                return UpdateCommandResult.Failed(error);
            }

            SetState(new UpdatePresentationState(
                StatusChecking,
                null,
                false,
                null,
                false,
                null,
            new UpdateProgress(UpdatePhase.Checking, 0, null, 0, 0)));

            var result = await _check.CheckAsync(feed, trust, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                ClearCandidate();
                const string cancelled = "Update check cancelled.";
                SetState(new UpdatePresentationState(
                    StatusUnavailable,
                    cancelled,
                    false,
                    null,
                    false,
                    null));
                return UpdateCommandResult.Failed(cancelled);
            }

            var checkedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var persisted = await _configuration.UpdateAsync(
                configuration => configuration with
                {
                    Updates = configuration.Updates with { LastCheckedAtUnixMs = checkedAt }
                },
                cancellationToken).ConfigureAwait(false);
            if (!persisted.IsSaved)
            {
                ClearCandidate();
                var error = persisted.SafeErrorDetail ?? "The update check time could not be saved.";
                SetState(new UpdatePresentationState(
                    StatusUnavailable,
                    error,
                    false,
                    null,
                    false,
                    null));
                return UpdateCommandResult.Failed(error);
            }

            if (result.IsCurrent)
            {
                ClearCandidate();
                SetState(new UpdatePresentationState(
                    StatusUnavailable,
                    null,
                    false,
                    null,
                    false,
                    null));
                return UpdateCommandResult.Success();
            }

            if (!result.IsAvailable || result.Manifest is null || result.Decision?.Accepted != true)
            {
                ClearCandidate();
                var error = result.SafeError ?? "Update check failed.";
                SetState(new UpdatePresentationState(
                    StatusUnavailable,
                    error,
                    false,
                    null,
                    false,
                    null));
                return UpdateCommandResult.Failed(error);
            }

            lock (_stateGate)
            {
                _acceptedManifest = result.Manifest;
                _acceptedFeed = feed;
            }

            SetState(new UpdatePresentationState(
                StatusAvailable,
                null,
                false,
                null,
                false,
                result.Manifest.ProductVersion));
            return UpdateCommandResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearCandidate();
            const string error = "Update check cancelled.";
            SetState(new UpdatePresentationState(
                StatusUnavailable,
                error,
                false,
                null,
                false,
                null));
            return UpdateCommandResult.Failed(error);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<UpdateCommandResult> DownloadAndInstallAsync(
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _mutationAdmission.Enter(nameof(DownloadAndInstallAsync));
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UpdateManifest? manifest;
            Uri? feed;
            lock (_stateGate)
            {
                manifest = _acceptedManifest;
                feed = _acceptedFeed;
            }

            if (manifest is null || feed is null)
            {
                const string error =
                    "Check for updates and accept a newer manifest before installing.";
                SetState(new UpdatePresentationState(
                    StatusUnavailable,
                    error,
                    false,
                    null,
                    false,
                    null));
                return UpdateCommandResult.Failed(error);
            }

            var operationId = Guid.NewGuid();
            if (!TryResolvePayloadUri(feed, manifest, out var payloadUri, out var uriError))
            {
                var error = uriError ?? "The update payload URI could not be resolved.";
                SetState(new UpdatePresentationState(
                    StatusAvailable,
                    error,
                    false,
                    null,
                    false,
                    manifest.ProductVersion));
                return UpdateCommandResult.Failed(error);
            }

            var progress = new UpdateProgressCallback(value => ReportProgress(value, manifest.ProductVersion));
            var incremental = await new IncrementalUpdateService(_appState, _install, _download,
                new UpdatePackageValidator(_install)).StageAsync(feed, manifest, operationId, progress,
                    cancellationToken).ConfigureAwait(false);
            if (incremental.IsSuccess)
                return await HandoffStagingAsync(operationId, manifest, cancellationToken).ConfigureAwait(false);
            if (!incremental.CanFallback)
            {
                SetState(new(StatusUnavailable, incremental.SafeError, false, null, false, manifest.ProductVersion));
                ClearCandidate();
                return UpdateCommandResult.Failed(incremental.SafeError ?? "Incremental integrity verification failed.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Fallback owns a fresh operation root; failed staging is never reused.
            operationId = Guid.NewGuid();
            ReportProgress(new(UpdatePhase.Downloading, 0, manifest.PayloadByteLength, 0, 1), manifest.ProductVersion);

            var download = await _download.DownloadAsync(
                payloadUri!,
                operationId,
                manifest.PayloadByteLength,
                manifest.PayloadSha256,
                cancellationToken, progress).ConfigureAwait(false);
            if (!download.IsSuccess || string.IsNullOrWhiteSpace(download.PayloadPath))
            {
                var error = download.SafeError ?? "Update payload download failed.";
                SetState(new UpdatePresentationState(
                    StatusAvailable,
                    error,
                    false,
                    null,
                    false,
                    manifest.ProductVersion));
                return UpdateCommandResult.Failed(error);
            }

            return await StageAndHandoffAsync(
                download.PayloadPath,
                operationId,
                manifest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            UpdateManifest? candidate;
            lock (_stateGate)
            {
                candidate = _acceptedManifest;
            }

            const string error = "Update operation cancelled before updater handoff.";
            SetState(new UpdatePresentationState(
                candidate is null ? StatusUnavailable : StatusAvailable,
                error,
                false,
                null,
                false,
                candidate?.ProductVersion));
            return UpdateCommandResult.Failed(error);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LocalUpdateInspectionResult> InspectLocalPackageAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await _localPackageReader.ReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess || read.Manifest is null)
            {
                return LocalUpdateInspectionResult.Reject(
                    read.SafeError ?? "The selected update ZIP could not be inspected.");
            }

            var trust = new UpdateTrustConfiguration(
                ConfiguredFeed: null,
                AllowExplicitLocalPackage: true);
            var decision = _trust.Evaluate(
                read.Manifest,
                trust,
                isLocalPackage: true,
                userConfirmedLocalPackage: true);
            return decision.Accepted
                ? new LocalUpdateInspectionResult(
                    true,
                    read.Manifest.ProductVersion,
                    read.Manifest.RuntimeIdentifier,
                    read.Manifest.PayloadSha256,
                    null)
                : LocalUpdateInspectionResult.Reject(decision.Reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LocalUpdateInspectionResult.Reject("Local update inspection cancelled.");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<UpdateCommandResult> InstallFromLocalPackageAsync(
        string archivePath,
        string expectedPayloadSha256,
        bool userConfirmedLocalPackage,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _mutationAdmission.Enter(nameof(InstallFromLocalPackageAsync));
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await _localPackageReader.ReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess || read.Manifest is null || string.IsNullOrWhiteSpace(read.ArchivePath))
            {
                var readError = read.SafeError ?? "The selected update ZIP could not be read.";
                SetState(new UpdatePresentationState(
                    StatusUnavailable, readError, false, null, false, null));
                return UpdateCommandResult.Failed(readError);
            }

            if (string.IsNullOrWhiteSpace(expectedPayloadSha256)
                || !string.Equals(
                    read.Manifest.PayloadSha256,
                    expectedPayloadSha256,
                    StringComparison.Ordinal))
            {
                const string changed =
                    "The selected update ZIP changed after confirmation. Inspect and confirm the package again.";
                SetState(new UpdatePresentationState(
                    StatusUnavailable,
                    changed,
                    false,
                    null,
                    false,
                    read.Manifest.ProductVersion));
                return UpdateCommandResult.Failed(changed);
            }

            var trust = new UpdateTrustConfiguration(
                ConfiguredFeed: null,
                AllowExplicitLocalPackage: true);
            var decision = _trust.Evaluate(
                read.Manifest,
                trust,
                isLocalPackage: true,
                userConfirmedLocalPackage);
            if (!decision.Accepted)
            {
                SetState(new UpdatePresentationState(
                    StatusUnavailable,
                    decision.Reason,
                    false,
                    null,
                    false,
                    read.Manifest.ProductVersion));
                return UpdateCommandResult.Failed(decision.Reason);
            }

            ClearCandidate();
            var operationId = Guid.NewGuid();
            return await StageAndHandoffAsync(
                read.ArchivePath,
                operationId,
                read.Manifest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            const string cancelled = "Local update cancelled before updater handoff.";
            SetState(new UpdatePresentationState(
                StatusUnavailable, cancelled, false, null, false, null));
            return UpdateCommandResult.Failed(cancelled);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<UpdateCommandResult> StageAndHandoffAsync(
        string archivePath,
        Guid operationId,
        UpdateManifest manifest,
        CancellationToken cancellationToken)
    {
        SetState(new UpdatePresentationState(
            StatusStaging,
            null,
            true,
            null,
            false,
            manifest.ProductVersion,
            new UpdateProgress(UpdatePhase.Verifying, 0, null, 0, 0)));

        var validation = await _stager.ExtractAndValidateAsync(
            archivePath,
            operationId,
            manifest,
            cancellationToken).ConfigureAwait(false);
        if (!validation.IsAccepted)
        {
            var error = validation.SafeError ?? "Update package validation failed.";
            SetState(new UpdatePresentationState(
                HasAcceptedCandidate ? StatusAvailable : StatusUnavailable,
                error,
                false,
                null,
                false,
                manifest.ProductVersion));
            return UpdateCommandResult.Failed(error);
        }

        return await HandoffStagingAsync(operationId, manifest, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateCommandResult> HandoffStagingAsync(Guid operationId, UpdateManifest manifest,
        CancellationToken cancellationToken)
    {
        var stagedRoot = _appState.ResolveContainedPath(
            AppStatePathArea.UpdateStaging,
            operationId.ToString("D"));
        // one absolute deadline is established before helper launch and is consumed by
        // both the application shutdown path and the updater parent-wait path. No component gets
        // a fresh timeout window after another component already consumed time.
        var shutdownDeadlineUtc = DateTimeOffset.UtcNow.Add(UpdateShutdownBudget);
        var handoff = new UpdateHandoff(
            operationId,
            stagedRoot,
            _install.Root,
            manifest,
            _vaultRoot,
            shutdownDeadlineUtc);

        SetState(new UpdatePresentationState(
            StatusPreparing,
            null,
            true,
            null,
            false,
            manifest.ProductVersion,
            new UpdateProgress(UpdatePhase.Preparing, 0, null, 0, 0)));

        var prepared = await _handoff.PrepareAsync(handoff, cancellationToken).ConfigureAwait(false);
        if (!prepared.IsStarted)
        {
            var error = prepared.SafeError ?? "Updater helper could not be started.";
            SetState(new UpdatePresentationState(
                HasAcceptedCandidate ? StatusAvailable : StatusUnavailable,
                error,
                false,
                null,
                false,
                manifest.ProductVersion));
            return UpdateCommandResult.Failed(error);
        }

        SetState(new UpdatePresentationState(
            StatusRestarting,
            null,
            false,
            null,
            true,
            manifest.ProductVersion,
            new UpdateProgress(UpdatePhase.Restarting, 0, null, 0, 0)));

        _requestControlledShutdown(shutdownDeadlineUtc);
        return UpdateCommandResult.Success();
    }

    private static bool TryResolvePayloadUri(
        Uri feed,
        UpdateManifest manifest,
        out Uri? payload,
        out string? error)
    {
        payload = null;
        error = null;

        var archiveName = Neuterradise.Release.Contracts.ReleaseLayout.IsLegacyPayload(
            manifest.Files.Select(file => file.RelativePath)) ? "NeuTerradise" : "naut";
        var payloadFileName = $"{archiveName}-v{manifest.ProductVersion}-{manifest.RuntimeIdentifier}.zip";
        var candidate = new Uri(feed, payloadFileName);
        if (!candidate.IsAbsoluteUri
            || !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || Uri.Compare(
                feed,
                candidate,
                UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) != 0)
        {
            error = "Resolved update payload is outside the configured HTTPS feed authority.";
            return false;
        }

        payload = candidate;
        return true;
    }

    private void ReportProgress(UpdateProgress progress, string? version)
    {
        var status = progress.Phase switch
        {
            UpdatePhase.Checking => StatusChecking,
            UpdatePhase.Planning => StatusPlanning,
            UpdatePhase.Downloading => StatusDownloading,
            UpdatePhase.Verifying => StatusStaging,
            UpdatePhase.Preparing => StatusPreparing,
            UpdatePhase.Restarting => StatusRestarting,
            _ => StatusUnavailable
        };
        SetState(new(status, null, true, progress.Percentage, false, version, progress));
    }

    private void ClearCandidate()
    {
        lock (_stateGate)
        {
            _acceptedManifest = null;
            _acceptedFeed = null;
        }
    }

    private void SetState(UpdatePresentationState state)
    {
        Action<UpdatePresentationState>? handlers;
        lock (_stateGate)
        {
            _state = state;
            handlers = StateChanged;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (Action<UpdatePresentationState> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(state);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.TraceWarning(
                    "Update state subscriber {0} failed: {1}",
                    handler.Method.DeclaringType?.FullName ?? handler.Method.Name,
                    exception.Message);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StateChanged = null;
    }
}

public sealed record UpdateCommandResult(bool Succeeded, string? SafeError)
{
    public static UpdateCommandResult Success() => new(true, null);

    public static UpdateCommandResult Failed(string error) => new(false, error);
}
