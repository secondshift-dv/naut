using Neuterradise.Profiling.Protocol;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Neuterradise.App.Faces;
using Neuterradise.App.Import.Preparation;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Jobs.Handlers;
using Neuterradise.App.SystemServices.Jobs.Transport;
using Neuterradise.App.SystemServices.MediaTools;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Lifecycle;

public enum ProfilingInitializationState { Available, Disabled, Unavailable }

public sealed class ProductionRuntimeRegistry : IAsyncDisposable
{

    private static readonly ConcurrentDictionary<string, byte> _activeVaultRoots =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly BootstrapContext _context;
    private readonly string _vaultRootKey;
    private readonly SchedulerReads _schedulerReads;
    private readonly SemaphoreSlim _workerGate = new(1, 1);

    private ProfilingWorkerConnection? _worker;
    private CancellationTokenSource? _statusLoop;
    private Task? _statusTask;
    private Channel<byte>? _statusSignals;
    private int _statusSignalsSubscribed;
    private int _started;
    private int _disposed;

    private ProductionRuntimeRegistry(
        BootstrapContext context,
        string vaultRootKey,
        JobScheduler scheduler,
        JobCancellationOperations cancellation,
        JobHandlerRegistry registry,
        ModelPreviewAdapterRegistry modelAdapters,
        RuntimeCapabilitySnapshot capabilities)
    {
        _context = context;
        _vaultRootKey = vaultRootKey;
        _schedulerReads = new SchedulerReads(context.Catalog);
        Scheduler = scheduler;
        Cancellation = cancellation;
        Registry = registry;
        ModelAdapters = modelAdapters;
        Capabilities = capabilities;
    }

    public JobScheduler Scheduler { get; }

    /// <summary>
    /// Shared cancellation operations used by both the scheduler and ImportUnitControlAuthority.
    /// Sharing ensures that control intents (Pause/Cancel/Shutdown) set by the authority are
    /// visible to the scheduler's completion path.
    /// </summary>
    public JobCancellationOperations Cancellation { get; }

    public JobHandlerRegistry Registry { get; }

    public MediaResourceAuthority MediaResources { get; private set; } = null!;

    public ModelPreviewAdapterRegistry ModelAdapters { get; }

    /// <summary>
    /// Truthful, session-scoped capability facts for tools, inference models and preview adapters.
    /// Missing or integrity-failed artifacts disable only the features that need them.
    /// </summary>
    public RuntimeCapabilitySnapshot Capabilities { get; }

    public ProfilingWorkerConnection? Worker => Volatile.Read(ref _worker);

    public ProfilingInitializationState ProfilingState { get; private set; } = ProfilingInitializationState.Disabled;
    public string? ProfilingStateReason { get; private set; }

    public static async Task<ProductionRuntimeRegistry> CreateAsync(
        BootstrapContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.State != StartupState.Ready && context.State != StartupState.Prewarming)
        {
            throw new InvalidOperationException(
                "The production runtime may only be composed during startup prewarming or ready.");
        }

        var vaultRootKey = context.Paths.Root;
        if (!_activeVaultRoots.TryAdd(vaultRootKey, 0))
        {
            throw new InvalidOperationException(
                "A production runtime is already composed for this vault; one writer has one scheduler.");
        }

        ProductionRuntimeRegistry? services = null;
        try
        {
            var launcher = new BoundedProcessLauncher();

            var install = InstallPaths.CreateProduction();
            var toolResolver = ExternalToolResolver.ForInstall(install);
            var modelAdapters = ModelPreviewAdapterRegistry.CreateDefaultRegistry(launcher);

            // Capability probing happens once per session. A missing tool or model is a feature-scoped
            // limitation, never a startup failure and never a silent success.
            var capabilities = RuntimeCapabilitySnapshot.Create(
                install,
                toolResolver,
                modelAdapters.DescribeAdapters());

            foreach (var limitation in capabilities.Limitations)
            {
                Trace.TraceWarning(
                    "Runtime capability {0} is {1}: {2}",
                    limitation.CapabilityId,
                    limitation.State,
                    limitation.Reason);
            }

            var mediaReads = new MediaReads(context.Catalog);
            var assetWrites = new MediaWrites(context.Catalog);
            var faceWrites = new FaceWrites(context.Catalog);

            var registry = new JobHandlerRegistry();
            var cancellation = new JobCancellationOperations(context.Catalog);
            services = new ProductionRuntimeRegistry(
                context,
                vaultRootKey,
                CreateScheduler(context, registry, cancellation),
                cancellation,
                registry,
                modelAdapters,
                capabilities);

            var planSource = new ProductionMediaToolPlanSource(
                mediaReads,
                assetWrites,
                context.Paths,
                toolResolver,
                modelAdapters);
            var externalTool = new ExternalMediaToolJobOperation(launcher, planSource);
            var assetArtifacts = new PrepareMediaAssetJobOperation(
                mediaReads, context.Catalog, context.Paths, toolResolver, launcher, modelAdapters);

            services.MediaResources = new MediaResourceAuthority(context.Catalog);

            var importPreparation = new ImportPreparationCoordinator(context.Catalog);
            registry.Register("HashMedia",
                new HashMediaJobHandler(new CandidateHashJobOperation(importPreparation)));
            registry.Register("TransferOriginal",
                new TransferOriginalJobHandler(context.Catalog, importPreparation));
            var pathReconciler = new PathReconciler(
                context.Paths, context.Catalog.ProfileWrites, new MediaWrites(context.Catalog),
                new ManagedFileVerifier(), new WindowsVolumeIdentityProvider());
            registry.Register("ProfileRenameReconciliation", new ProfileRenameReconciliationJobHandler(pathReconciler));
            registry.Register("OwnerRelocation", new OwnerRelocationJobHandler(pathReconciler));
            registry.Register("ExtractMetadata", new ExtractMetadataJobHandler(externalTool));
            registry.Register(
                "GenerateThumbnail",
                new GenerateThumbnailJobHandler(assetArtifacts));
            registry.Register("GenerateVideoMediaAssets",
                new GenerateVideoMediaAssetsJobHandler(assetArtifacts));
            registry.Register("GenerateModelMediaAssets", new GenerateModelMediaAssetsJobHandler(assetArtifacts));
            registry.Register(
                "FaceAnalysis",
                new FaceAnalysisJobHandler(new ProfilingFaceAnalysisJobOperation(
                    mediaReads,
                    faceWrites,
                    context.Paths,
                    services.SendToWorkerAsync,
                    analysisTimeout: null,

                    identityBank: new IdentityBankProvider(context.Catalog))));

            // Wire media preparation capability tracking: when a job completes, update capability state
            // and attempt readiness join. After reconciliation, project terminal job state into
            // capability state for jobs that were interrupted and moved to terminal states.
            var mediaPreparationCoordinator = new ImportMediaPreparationCoordinator(context.Catalog, context.Paths);
            var mediaPreparationHandler = new ImportMediaPreparationCompletionHandler(context.Catalog, mediaPreparationCoordinator);
            services.Scheduler.OnJobCompleted = mediaPreparationHandler.HandleCompletionAsync;
            services.Scheduler.OnReconciled = mediaPreparationHandler.ReconcileTerminalCapabilitiesAsync;
            services.Cancellation.OnJobCancelled = mediaPreparationHandler.HandleCompletionAsync;

            return services;
        }
        catch
        {
            if (services is not null)
            {
                try
                {
                    await services.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning("Partial runtime disposal failed: {0}", exception.GetType().Name);
                }
            }

            _activeVaultRoots.TryRemove(vaultRootKey, out _);
            throw;
        }
    }

    public async Task StartAsync(
        Action<ShellWorkSnapshot>? publishStatus = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The production runtime can start only once.");
        }

        await Scheduler.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var worker = await ConnectWorkerAsync(cancellationToken).ConfigureAwait(false);
            var hello = worker.Host.ProfilingHello;
            if (hello is null)
            {
                throw new ProfilingProtocolException("Worker handshake did not expose Hello outcome.");
            }
            if (hello.YuNetModelAvailable && hello.SFaceModelAvailable)
            {
                ProfilingState = ProfilingInitializationState.Available;
                ProfilingStateReason = null;
            }
            else
            {
                ProfilingState = ProfilingInitializationState.Unavailable;
                ProfilingStateReason = "Profiling worker connected, but required YuNet/SFace model outcome is unavailable.";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ProfilingProtocolException or FileNotFoundException)
        {
            ProfilingState = ProfilingInitializationState.Unavailable;
            ProfilingStateReason = $"Profiling initialization unavailable: {ex.GetType().Name}.";
        }

        if (publishStatus is not null)
        {
            var signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite,
            });
            _statusSignals = signals;
            SubscribeStatusSignals();

            _statusLoop = new CancellationTokenSource();
            _statusTask = Task.Run(
                () => PublishStatusLoopAsync(publishStatus, signals.Reader, _statusLoop.Token),
                CancellationToken.None);
        }
    }

    public async Task<SchedulerShutdownReport> ShutdownSchedulerAsync(
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
    {
        await StopStatusLoopAsync().ConfigureAwait(false);

        var releaseWorker = Volatile.Read(ref _worker) is null
            ? (Func<CancellationToken, Task>?)null
            : ReleaseWorkerAsync;

        return await Scheduler
            .ShutdownAsync(gracePeriod, releaseWorker, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ProfilingEnvelope> SendToWorkerAsync(
        ProfilingEnvelope request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await ConnectWorkerAsync(cancellationToken).ConfigureAwait(false);
        return await connection.SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProfilingWorkerConnection> ConnectWorkerAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var existing = Volatile.Read(ref _worker);
        if (existing is not null)
        {
            await existing.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        await _workerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = Volatile.Read(ref _worker);
            if (existing is not null)
            {
                await existing.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }

            var created = new ProfilingWorkerConnection();
            try
            {
                await created.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {

                await created.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            Volatile.Write(ref _worker, created);
            return created;
        }
        finally
        {
            _workerGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopStatusLoopAsync().ConfigureAwait(false);

        try
        {
            await Scheduler.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Scheduler disposal failed: {0}", exception.GetType().Name);
        }

        await ReleaseWorkerAsync(CancellationToken.None).ConfigureAwait(false);

        _workerGate.Dispose();
        _activeVaultRoots.TryRemove(_vaultRootKey, out _);
    }

    private static JobScheduler CreateScheduler(
        BootstrapContext context,
        JobHandlerRegistry registry,
        JobCancellationOperations cancellation) =>
        new(context.Catalog, registry, cancellation: cancellation);

    private void SubscribeStatusSignals()
    {
        if (Interlocked.Exchange(ref _statusSignalsSubscribed, 1) != 0) return;
        JobSignals.StatusChanged += OnWorkStatusChanged;
        _context.Catalog.WriteCoordinator.Invalidated += OnCatalogInvalidatedForStatus;
    }

    private void UnsubscribeStatusSignals()
    {
        if (Interlocked.Exchange(ref _statusSignalsSubscribed, 0) == 0) return;
        JobSignals.StatusChanged -= OnWorkStatusChanged;
        _context.Catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidatedForStatus;
    }

    private void OnWorkStatusChanged() => SignalStatusRefresh();

    private void OnCatalogInvalidatedForStatus(object? sender, CatalogInvalidation invalidation)
    {
        if (string.Equals(invalidation.DomainKind, CatalogInvalidationDomain.Import, StringComparison.Ordinal))
        {
            SignalStatusRefresh();
        }
    }

    private void SignalStatusRefresh()
    {
        var signals = _statusSignals;
        signals?.Writer.TryWrite(0);
    }

    private async Task PublishStatusLoopAsync(
        Action<ShellWorkSnapshot> publishStatus,
        ChannelReader<byte> signals,
        CancellationToken cancellationToken)
    {
        const int coalesceDelayMs = 40;
        var fallbackInterval = TimeSpan.FromSeconds(10);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                publishStatus(await ReadShellSnapshotAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (CatalogDb.IsDatabaseFailure(exception) || exception is InvalidOperationException)
            {
                Trace.TraceWarning("Shell status read failed: {0}", exception.GetType().Name);
            }

            using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wake.CancelAfter(fallbackInterval);
            var signaled = false;
            try
            {
                signaled = await signals.WaitToReadAsync(wake.Token).ConfigureAwait(false);
                if (!signaled) return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Missed-signal recovery fallback. Normal publication is event-driven.
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!signaled) continue;

            try
            {
                await Task.Delay(coalesceDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (signals.TryRead(out _))
            {
            }
        }
    }

    public async Task<ShellWorkSnapshot> ReadShellSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var byLane = await _schedulerReads.GetStateCountsByLaneAsync(cancellationToken).ConfigureAwait(false);

        var running = 0L;
        var pending = 0L;
        var attention = 0L;
        var completed = 0L;

        foreach (var lane in byLane.Values)
        {
            foreach (var (state, count) in lane)
            {
                switch (state)
                {
                    case JobState.Running:
                        running += count;
                        break;
                    case JobState.Pending:
                    case JobState.Runnable:
                    case JobState.FailedRetryable:
                        pending += count;
                        break;
                    case JobState.FailedTerminal:
                        attention += count;
                        break;
                    case JobState.Succeeded:
                        completed += count;
                        break;
                    default:
                        break;
                }
            }
        }

        var outstanding = running + pending;
        double? progress = outstanding + completed > 0 && running > 0
            ? Math.Clamp((double)completed / (outstanding + completed), 0, 1)
            : null;

        return new ShellWorkSnapshot(
            (int)Math.Min(int.MaxValue, running),
            (int)Math.Min(int.MaxValue, pending),
            (int)Math.Min(int.MaxValue, attention),
            progress);
    }

    private async Task StopStatusLoopAsync()
    {
        UnsubscribeStatusSignals();

        var signals = Interlocked.Exchange(ref _statusSignals, null);
        signals?.Writer.TryComplete();

        var loop = Interlocked.Exchange(ref _statusLoop, null);
        var task = Interlocked.Exchange(ref _statusTask, null);
        if (loop is null)
        {
            return;
        }

        await loop.CancelAsync().ConfigureAwait(false);
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        loop.Dispose();
    }

    private async Task ReleaseWorkerAsync(CancellationToken cancellationToken)
    {
        var worker = Interlocked.Exchange(ref _worker, null);
        if (worker is null)
        {
            return;
        }

        try
        {
            await worker.ShutdownAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Profiling Worker shutdown failed: {0}", exception.GetType().Name);
        }
        finally
        {
            await worker.DisposeAsync().ConfigureAwait(false);
        }
    }
}
