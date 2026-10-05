using Neuterradise.Profiling.Protocol;
namespace Neuterradise.App.SystemServices.Jobs;

using System.Diagnostics;
using System.IO;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Jobs.Transport;
using Neuterradise.App.SystemServices.Storage;

public enum ProfilingWorkerHostState
{
    Stopped,
    Starting,
    Ready,
    Crashed,
    Stopping,
    NeedsAttention
}

public sealed class ProfilingWorkerProcessHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly InstallPaths _install;
    private readonly TimeProvider _timeProvider;

    private Process? _currentProcess;
    private ProfilingServerTransport? _pipeServer;
    private bool _disposed;
    private DateTimeOffset? _readySinceUtc;

    public const int MaxConsecutiveFailures = 3;
    public static TimeSpan HealthyStabilityWindow { get; } = TimeSpan.FromMinutes(1);

    public ProfilingWorkerProcessHost(
        InstallPaths? install = null,
        TimeProvider? timeProvider = null)
    {
        _install = install ?? InstallPaths.CreateProduction();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ProfilingWorkerHostState State { get; private set; } = ProfilingWorkerHostState.Stopped;
    public int? ProcessId => _currentProcess?.Id;
    public int ConsecutiveFailures { get; private set; }
    public int RestartCount { get; private set; }
    public HelloPayload? ProfilingHello { get; private set; }
    public ProfilingServerTransport? CurrentProfilingServerTransport => _pipeServer;
    public Process? CurrentProcess => _currentProcess;

    public long? ProfilingWorkingSetBytes
    {
        get
        {
            var process = _currentProcess;
            if (process is null)
            {
                return null;
            }

            try
            {
                if (process.HasExited)
                {
                    return null;
                }

                process.Refresh();
                return process.WorkingSet64;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }

    public ProfilingWorkerMetrics GetMetrics() => new(
        State,
        ProcessId,
        RestartCount,
        ConsecutiveFailures,
        ProfilingWorkingSetBytes,
        ProfilingHello?.ProtocolVersion);

    public string ResolveExecutablePath()
    {
        var deployedWorker = _install.ResolveContainedPath(
            InstallPathArea.Root,
            Path.Combine("workers", ProductIdentity.ProfilingWorkerExecutableName));
        if (File.Exists(deployedWorker) && IsApprovedWorkerPath(deployedWorker))
        {
            return deployedWorker;
        }

        var adjacentWorker = _install.ProfilingWorkerExecutablePath;
        if (File.Exists(adjacentWorker) && IsApprovedWorkerPath(adjacentWorker))
        {
            return adjacentWorker;
        }

        throw new FileNotFoundException(
            $"Could not locate {ProductIdentity.ProfilingWorkerExecutableName} in the approved application deployment.");
    }

    private bool IsApprovedWorkerPath(string candidate)
    {
        var full = Path.GetFullPath(candidate);
        return _install.IsWithinInstallRoot(full)
            && string.Equals(
                Path.GetFileName(full),
                ProductIdentity.ProfilingWorkerExecutableName,
                StringComparison.OrdinalIgnoreCase);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var startAttempted = false;
        try
        {
            if (State == ProfilingWorkerHostState.Ready &&
                _currentProcess is { HasExited: false } &&
                _pipeServer is { IsConnected: true })
            {
                return;
            }

            if (ConsecutiveFailures >= MaxConsecutiveFailures)
            {
                State = ProfilingWorkerHostState.NeedsAttention;
                throw new ProfilingWorkerProcessException(
                    $"Profiling Worker process exceeded maximum consecutive restart failure limit ({MaxConsecutiveFailures}) and requires attention.");
            }

            startAttempted = true;
            await CleanupResourcesUnsafeAsync().ConfigureAwait(false);

            State = ProfilingWorkerHostState.Starting;
            var executablePath = ResolveExecutablePath();

            _pipeServer = new ProfilingServerTransport();
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? _install.Root,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(_pipeServer.PipeName);
            startInfo.Environment[ProfilingRuntimeEnvironment.InstallRootEnvironmentVariable] = _install.Root;

            _currentProcess = Process.Start(startInfo)
                ?? throw new ProfilingWorkerProcessException($"Failed to start Profiling Worker process '{executablePath}'.");
            TrySetBelowNormalPriority(_currentProcess);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));

            await _pipeServer.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false);

            var helloEnvelope = await _pipeServer.ReadEnvelopeAsync(connectCts.Token).ConfigureAwait(false);
            if (helloEnvelope is null)
            {
                throw new ProfilingProtocolException("Profiling Worker disconnected before completing handshake.");
            }

            if (helloEnvelope.ProtocolVersion != ProfilingProtocolVersion.Current)
            {
                var errorAck = ProfilingEnvelope.Create(
                    ProfilingMessageType.HelloAck,
                    new HelloAckPayload(
                        ProfilingProtocolVersion.Current,
                        false,
                        $"Incompatible protocol version {helloEnvelope.ProtocolVersion}; expected {ProfilingProtocolVersion.Current}."),
                    helloEnvelope.RequestId);
                await SendHandshakeRejectionAsync(errorAck, connectCts.Token).ConfigureAwait(false);
                throw new ProfilingProtocolException(
                    $"Incompatible worker envelope protocol version {helloEnvelope.ProtocolVersion}; expected {ProfilingProtocolVersion.Current}.");
            }

            if (helloEnvelope.MessageType != ProfilingMessageType.Hello)
            {
                var errorAck = ProfilingEnvelope.Create(
                    ProfilingMessageType.HelloAck,
                    new HelloAckPayload(ProfilingProtocolVersion.Current, false, "Expected Hello message."),
                    helloEnvelope.RequestId);
                await SendHandshakeRejectionAsync(errorAck, connectCts.Token).ConfigureAwait(false);
                throw new ProfilingProtocolException($"Expected Hello from worker but received {helloEnvelope.MessageType}.");
            }

            var hello = helloEnvelope.DeserializePayload<HelloPayload>();
            if (hello is null || hello.ProtocolVersion != ProfilingProtocolVersion.Current)
            {
                var version = hello?.ProtocolVersion ?? -1;
                var errorAck = ProfilingEnvelope.Create(
                    ProfilingMessageType.HelloAck,
                    new HelloAckPayload(
                        ProfilingProtocolVersion.Current,
                        false,
                        $"Incompatible protocol version {version}; expected {ProfilingProtocolVersion.Current}."),
                    helloEnvelope.RequestId);
                await SendHandshakeRejectionAsync(errorAck, connectCts.Token).ConfigureAwait(false);
                throw new ProfilingProtocolException(
                    $"Incompatible worker protocol version {version}; expected {ProfilingProtocolVersion.Current}.");
            }

            string[] requiredRequests = ["Ping", "CancelRequest", "Shutdown"];
            if (string.IsNullOrWhiteSpace(hello.WorkerBuildVersion) ||
                hello.SupportedRequestTypes is null ||
                requiredRequests.Any(required => !hello.SupportedRequestTypes.Contains(required, StringComparer.Ordinal)) ||
                hello.SupportedEmbeddingSpaces is null)
            {
                var errorAck = ProfilingEnvelope.Create(
                    ProfilingMessageType.HelloAck,
                    new HelloAckPayload(
                        ProfilingProtocolVersion.Current,
                        false,
                        "Profiling Worker Hello is missing required build or request-capability information."),
                    helloEnvelope.RequestId);
                await SendHandshakeRejectionAsync(errorAck, connectCts.Token).ConfigureAwait(false);
                throw new ProfilingProtocolException("Profiling Worker Hello is missing required build or request-capability information.");
            }

            var successAck = ProfilingEnvelope.Create(
                ProfilingMessageType.HelloAck,
                new HelloAckPayload(ProfilingProtocolVersion.Current, true),
                helloEnvelope.RequestId);
            await _pipeServer.WriteEnvelopeAsync(successAck, connectCts.Token).ConfigureAwait(false);

            ProfilingHello = hello;
            State = ProfilingWorkerHostState.Ready;
            _readySinceUtc = _timeProvider.GetUtcNow();
        }
        catch
        {
            if (!startAttempted)
            {
                throw;
            }

            _readySinceUtc = null;
            ConsecutiveFailures++;
            RestartCount++;
            State = ConsecutiveFailures >= MaxConsecutiveFailures
                ? ProfilingWorkerHostState.NeedsAttention
                : ProfilingWorkerHostState.Crashed;
            await CleanupResourcesUnsafeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task ShutdownAsync(TimeSpan? gracePeriod = null, CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == ProfilingWorkerHostState.Stopped)
            {
                return;
            }

            State = ProfilingWorkerHostState.Stopping;
            var timeout = gracePeriod ?? TimeSpan.FromSeconds(3);

            if (_pipeServer is { IsConnected: true })
            {
                try
                {
                    var shutdownEnvelope = ProfilingEnvelope.Create(ProfilingMessageType.Shutdown);
                    using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _pipeServer.WriteEnvelopeAsync(shutdownEnvelope, sendCts.Token).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (_currentProcess is { HasExited: false } process)
            {
                try
                {
                    using var waitCts = new CancellationTokenSource(timeout);
                    await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    KillProcess(process);
                }
            }

            await CleanupResourcesUnsafeAsync().ConfigureAwait(false);
            State = ProfilingWorkerHostState.Stopped;
            _readySinceUtc = null;
            ConsecutiveFailures = 0;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void NotifyCrash()
    {
        if (State is ProfilingWorkerHostState.Ready or ProfilingWorkerHostState.Starting)
        {
            if (State == ProfilingWorkerHostState.Ready
                && _readySinceUtc is { } readySince
                && _timeProvider.GetUtcNow() - readySince >= HealthyStabilityWindow)
            {
                ConsecutiveFailures = 0;
            }

            _readySinceUtc = null;
            ConsecutiveFailures++;
            RestartCount++;
            State = ConsecutiveFailures >= MaxConsecutiveFailures
                ? ProfilingWorkerHostState.NeedsAttention
                : ProfilingWorkerHostState.Crashed;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await ShutdownAsync().ConfigureAwait(false);
        _disposed = true;
        _lifecycleLock.Dispose();
    }

    private async Task CleanupResourcesUnsafeAsync()
    {
        if (_pipeServer is not null)
        {
            await _pipeServer.DisposeAsync().ConfigureAwait(false);
            _pipeServer = null;
        }

        if (_currentProcess is not null)
        {
            if (!_currentProcess.HasExited)
            {
                KillProcess(_currentProcess);
                try
                {
                    using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _currentProcess.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _currentProcess.Dispose();
            _currentProcess = null;
        }
    }

    private async Task SendHandshakeRejectionAsync(
        ProfilingEnvelope rejection,
        CancellationToken cancellationToken)
    {
        if (_pipeServer is null)
        {
            return;
        }

        await _pipeServer.WriteEnvelopeAsync(rejection, cancellationToken).ConfigureAwait(false);

        if (_currentProcess is not { HasExited: false } process)
        {
            return;
        }

        using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        exitCts.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static void TrySetBelowNormalPriority(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (InvalidOperationException) { }
        catch (NotSupportedException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class ProfilingWorkerProcessException : InvalidOperationException
{
    public ProfilingWorkerProcessException(string message) : base(message) { }
    public ProfilingWorkerProcessException(string message, Exception innerException) : base(message, innerException) { }
}
