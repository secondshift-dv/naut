using System.Diagnostics;

namespace Neuterradise.App.SystemServices.Resources;

/// <summary>Classes of expensive work the governor budgets. Unknown work is treated as <see cref="CpuHeavy"/>.</summary>
public enum ResourceClass
{
    CpuHeavy,
    DiskHeavy,
    MediaDerivation,
    VideoHover,
    Face,
    Prefetch,
}

/// <summary>What the person is doing right now, as far as expensive work is concerned.</summary>
public enum InteractionMode
{
    /// <summary>Pointer/keyboard/scroll/navigation within the last moment: the UI owns the machine.</summary>
    ForegroundInteractive,

    /// <summary>Window active but quiet: background work may use a little more.</summary>
    ForegroundIdle,

    /// <summary>Window inactive or minimized: durable work continues conservatively, nothing decorative runs.</summary>
    Background,
}

public enum InteractionSignal
{
    Pointer,
    Keyboard,
    Scroll,
    Navigation,
    Resize,
}

/// <summary>How much decorative presentation work is allowed right now.</summary>
public enum PresentationTier
{
    Full,
    Reduced,
    Fallback,

    /// <summary>Nothing continuous may run (minimized / hidden).</summary>
    Suspended,
}

public sealed record ResourceLimits(int CpuHeavy, int DiskHeavy, int MediaDerivation, int VideoHover, int Face, int Prefetch)
{
    public int For(ResourceClass resourceClass) => resourceClass switch
    {
        ResourceClass.CpuHeavy => CpuHeavy,
        ResourceClass.DiskHeavy => DiskHeavy,
        ResourceClass.MediaDerivation => MediaDerivation,
        ResourceClass.VideoHover => VideoHover,
        ResourceClass.Face => Face,
        ResourceClass.Prefetch => Prefetch,
        _ => CpuHeavy,
    };
}

public sealed record ResourceGovernorSnapshot(
    InteractionMode Mode,
    PresentationTier Tier,
    bool MemoryPressure,
    long WorkingSetBytes,
    IReadOnlyDictionary<ResourceClass, (int Active, int Waiting, int Limit)> Classes);

/// <summary>
/// The single application-wide authority for expensive work (documents 00 §8, 02 §5).
///
/// Every heavy unit — hashing, derivation, face analysis, decode prefetch, hover video — takes a
/// permit here. Limits follow the person: while they interact, background throughput collapses to one
/// unit per class and face work pauses; when they pause, it widens a little; when the window is
/// minimized, durable work continues at one unit per class and nothing decorative runs.
///
/// Interaction signals are in-memory and debounced. Nothing transient is ever written to SQLite.
/// Lowering a limit never cancels running work; new permits simply wait for the running count to fall.
/// </summary>
public sealed class ResourceGovernor : IDisposable
{
    /// <summary>How long after the last pointer/key/navigation signal the UI is still "interactive".</summary>
    public static readonly TimeSpan InteractiveHold = TimeSpan.FromMilliseconds(1200);

    /// <summary>Scrolling holds a little longer: flings and wheel bursts arrive in trains.</summary>
    public static readonly TimeSpan ScrollHold = TimeSpan.FromMilliseconds(1600);

    private static ResourceGovernor _shared = new();

    private readonly Lock _sync = new();
    private readonly Dictionary<ResourceClass, ClassState> _classes = [];
    private readonly Func<long> _clock;
    private readonly Timer _timer;
    private readonly Timer _interactionTimer;
    private readonly bool _systemTimer;
    private long _interactionWakeAt;

    private long _interactiveUntil;
    private bool _windowActive = true;
    private bool _windowMinimized;
    private bool _reducedMotion;
    private bool _memoryPressure;
    private int _importPressure;
    private int _foregroundDiskWork;
    public const long TransferThrottleQuantumBytes = 4L * 1024 * 1024;
    private const int ObservationMilliseconds = 1000;
    private const int RecoveryMilliseconds = 2000;
    private const int ContendedOperationMilliseconds = 8;
    private const int DispatcherLatencyThresholdMilliseconds = 50;
    private readonly Queue<(long At, double Latency)> _dispatcherLatencies = new();
    private long _contentionSince;
    private long _lastContention;
    private long _lastForegroundCompletion;
    private int _diskDelay;
    private long _workingSet;
    private InteractionMode _mode = InteractionMode.ForegroundIdle;
    private PresentationTier _tier = PresentationTier.Full;
    private bool _disposed;

    public ResourceGovernor(
        ResourceLimits? interactive = null,
        ResourceLimits? idle = null,
        ResourceLimits? background = null,
        Func<long>? monotonicMilliseconds = null,
        bool startTimer = true)
    {
        var cores = Math.Max(1, Environment.ProcessorCount);
        var wide = cores >= 6 ? 2 : 1;

        InteractiveLimits = interactive ?? new ResourceLimits(CpuHeavy: 1, DiskHeavy: 1, MediaDerivation: 1, VideoHover: 1, Face: 0, Prefetch: 0);
        IdleLimits = idle ?? new ResourceLimits(CpuHeavy: 2, DiskHeavy: 2, MediaDerivation: 1, VideoHover: 1, Face: 1, Prefetch: wide);
        BackgroundLimits = background ?? new ResourceLimits(CpuHeavy: 1, DiskHeavy: 1, MediaDerivation: 1, VideoHover: 0, Face: 1, Prefetch: 0);

        _clock = monotonicMilliseconds ?? (() => Environment.TickCount64);
        foreach (var resourceClass in Enum.GetValues<ResourceClass>())
        {
            _classes[resourceClass] = new ClassState();
        }

        _systemTimer = startTimer;
        _interactionTimer = new Timer(_ => SettleInteraction(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // Memory pressure needs periodic sampling, but interaction expiration has an exact
        // deadline. Idle work no longer wakes the governor four times per second.
        _timer = new Timer(_ => Tick(), null,
            startTimer ? TimeSpan.FromSeconds(2) : Timeout.InfiniteTimeSpan,
            startTimer ? TimeSpan.FromSeconds(2) : Timeout.InfiniteTimeSpan);
    }

    /// <summary>The process-wide instance. Composition may replace it once before work starts.</summary>
    public static ResourceGovernor Shared => Volatile.Read(ref _shared);

    public static void InstallShared(ResourceGovernor governor)
    {
        ArgumentNullException.ThrowIfNull(governor);
        var previous = Interlocked.Exchange(ref _shared, governor);
        if (!ReferenceEquals(previous, governor))
        {
            previous.Dispose();
        }
    }

    public ResourceLimits InteractiveLimits { get; }

    public ResourceLimits IdleLimits { get; }

    public ResourceLimits BackgroundLimits { get; }

    /// <summary>Raised (on a pool thread) when the mode, presentation tier, or primary-video eligibility changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when memory pressure asks bounded caches to shed.</summary>
    public event EventHandler? TrimRequested;

    public InteractionMode Mode
    {
        get { lock (_sync) return _mode; }
    }

    public PresentationTier Tier
    {
        get { lock (_sync) return _tier; }
    }

    public bool IsHoverVideoAllowed => Mode != InteractionMode.Background && !IsScrolling;

    public bool IsPrefetchAllowed
    {
        get { lock (_sync) return _mode == InteractionMode.ForegroundIdle && !_memoryPressure && Volatile.Read(ref _importPressure) == 0; }
    }

    // Import lowers decorative detail, but only memory pressure or an inactive/reduced-motion
    // window suspends the selected Home/Profile video. Hover stays on its separate budget.
    public bool IsPrimaryVideoAllowed
    {
        get { lock (_sync) return (_tier is PresentationTier.Full or PresentationTier.Reduced) && !_memoryPressure; }
    }

    public bool IsScrolling { get; private set; }

    public bool IsMemoryPressure => _memoryPressure;

    public void NotifyInteraction(InteractionSignal signal)
    {
        var hold = signal == InteractionSignal.Scroll ? ScrollHold : InteractiveHold;
        var until = _clock() + (long)hold.TotalMilliseconds;
        bool changed;
        lock (_sync)
        {
            if (until > _interactiveUntil)
            {
                _interactiveUntil = until;
            }

            if (signal == InteractionSignal.Scroll)
            {
                IsScrolling = true;
            }

            changed = Recompute();
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private void ScheduleInteractionWakeLocked()
    {
        if (!_systemTimer || _disposed || _mode != InteractionMode.ForegroundInteractive
            || _interactionWakeAt != 0) return;

        var now = _clock();
        var dueMilliseconds = Math.Max(1, _interactiveUntil - now);
        _interactionWakeAt = now + dueMilliseconds;
        _interactionTimer.Change(TimeSpan.FromMilliseconds(dueMilliseconds), Timeout.InfiniteTimeSpan);
    }

    private void SettleInteraction()
    {
        bool changed;
        lock (_sync)
        {
            if (_disposed) return;
            _interactionWakeAt = 0;
            changed = Recompute();
        }
        if (changed) RaiseChanged();
    }

    public void SetWindowState(bool isActive, bool isMinimized)
    {
        bool changed;
        lock (_sync)
        {
            _windowActive = isActive;
            _windowMinimized = isMinimized;
            changed = Recompute();
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    public void SetReducedMotion(bool reducedMotion)
    {
        bool changed;
        lock (_sync)
        {
            _reducedMotion = reducedMotion;
            changed = Recompute();
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    internal void UpdateMemoryPressure(bool memoryPressure, long workingSetBytes)
    {
        bool changed;
        lock (_sync)
        {
            var previousMemoryPressure = _memoryPressure;
            var eligibilityChanged = previousMemoryPressure != memoryPressure && _tier == PresentationTier.Reduced;
            _workingSet = Math.Max(0, workingSetBytes);
            _memoryPressure = memoryPressure;
            changed = Recompute(previousMemoryPressure) || eligibilityChanged;
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    public IDisposable EnterImportPressure()
    {
        Interlocked.Increment(ref _importPressure);
        Update();
        return new Scope(() =>
        {
            Interlocked.Decrement(ref _importPressure);
            Update();
        });
    }

    public IDisposable EnterForegroundDiskWork()
    {
        Interlocked.Increment(ref _foregroundDiskWork);
        var started = _clock();
        return new Scope(() =>
        {
            Interlocked.Decrement(ref _foregroundDiskWork);
            lock (_sync)
            {
                if (_clock() - started >= ContendedOperationMilliseconds)
                    _lastForegroundCompletion = _clock();
            }
        });
    }

    public void ReportDispatcherLatency(TimeSpan latency)
    {
        if (latency < TimeSpan.Zero || !double.IsFinite(latency.TotalMilliseconds)) return;
        lock (_sync)
        {
            var now = _clock();
            _dispatcherLatencies.Enqueue((now, latency.TotalMilliseconds));
            while (_dispatcherLatencies.Count > 32
                || (_dispatcherLatencies.Count > 0 && now - _dispatcherLatencies.Peek().At > RecoveryMilliseconds))
                _dispatcherLatencies.Dequeue();
        }
    }

    public void ReportTransferIoLatency(TimeSpan latency)
    {
        if (latency.TotalMilliseconds < ContendedOperationMilliseconds) return;

        // Import reports once per read. With no active or recently completed foreground
        // disk work, do not contend on the governor lock for every transfer buffer.
        var now = _clock();
        if (Volatile.Read(ref _foregroundDiskWork) == 0)
        {
            var lastForeground = Volatile.Read(ref _lastForegroundCompletion);
            if (lastForeground == 0 || now - lastForeground > ObservationMilliseconds) return;
        }

        lock (_sync)
        {
            if (Volatile.Read(ref _foregroundDiskWork) == 0
                && (_lastForegroundCompletion == 0 || now - _lastForegroundCompletion > ObservationMilliseconds)) return;
            if (_lastContention == 0 || now - _lastContention > ObservationMilliseconds)
                _contentionSince = now;
            _lastContention = now;
        }
    }

    public int BackgroundDiskDelayMilliseconds
    {
        get
        {
            lock (_sync)
            {
                var now = _clock();
                if (_lastContention == 0 || now - _lastContention >= RecoveryMilliseconds)
                    return _diskDelay = 0;
                if (now - _lastContention > ObservationMilliseconds
                    || _lastContention - _contentionSince < ObservationMilliseconds) return _diskDelay;
                // At most 32 recent samples. Keep the 95th percentile calculation on
                // the stack: the 4 MiB transfer throttle must not allocate per check.
                Span<double> samples = stackalloc double[32];
                var sampleCount = 0;
                foreach (var sample in _dispatcherLatencies)
                {
                    if (now - sample.At <= RecoveryMilliseconds)
                        samples[sampleCount++] = sample.Latency;
                }
                var degraded = false;
                if (sampleCount >= 8)
                {
                    var recent = samples[..sampleCount];
                    recent.Sort();
                    degraded = recent[(int)Math.Ceiling(sampleCount * .95) - 1] >= DispatcherLatencyThresholdMilliseconds;
                }
                return _diskDelay = degraded ? 4 : 2;
            }
        }
    }

    public async ValueTask YieldBackgroundDiskAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var delay = BackgroundDiskDelayMilliseconds;
        if (delay != 0) await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    public ValueTask<ResourcePermit> AcquireAsync(ResourceClass resourceClass, CancellationToken cancellationToken = default,
        bool foreground = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource<ResourcePermit> waiter;
        lock (_sync)
        {
            Recompute();
            var state = _classes[resourceClass];
            if (state.Waiting.Count == 0 && state.Active < CurrentLimit(resourceClass))
            {
                state.Active++;
                return ValueTask.FromResult(new ResourcePermit(this, resourceClass));
            }

            waiter = new TaskCompletionSource<ResourcePermit>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (foreground && resourceClass == ResourceClass.CpuHeavy) state.Waiting.AddFirst(waiter);
            else state.Waiting.AddLast(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() =>
            {
                lock (_sync)
                {
                    if (_classes[resourceClass].Waiting.Remove(waiter))
                    {
                        waiter.TrySetCanceled(cancellationToken);
                    }
                }
            });
            Neuterradise.App.Shell.TaskObserver.Observe(
                waiter.Task.ContinueWith(
                    static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                    registration,
                    TaskScheduler.Default),
                "ResourceGovernor.DisposeCancellationRegistration");
        }

        return new ValueTask<ResourcePermit>(waiter.Task);
    }

    public bool TryAcquire(ResourceClass resourceClass, out ResourcePermit? permit)
    {
        lock (_sync)
        {
            Recompute();
            var state = _classes[resourceClass];
            if (state.Waiting.Count == 0 && state.Active < CurrentLimit(resourceClass))
            {
                state.Active++;
                permit = new ResourcePermit(this, resourceClass);
                return true;
            }
        }

        permit = null;
        return false;
    }

    public int CurrentLimit(ResourceClass resourceClass) => LimitFor(resourceClass, _mode, _memoryPressure);

    private int LimitFor(ResourceClass resourceClass, InteractionMode mode, bool memoryPressure)
    {
        var limits = mode switch
        {
            InteractionMode.ForegroundInteractive => InteractiveLimits,
            InteractionMode.ForegroundIdle => IdleLimits,
            _ => BackgroundLimits,
        };

        var limit = limits.For(resourceClass);
        if (memoryPressure && resourceClass is ResourceClass.Prefetch or ResourceClass.VideoHover)
        {
            limit = Math.Min(limit, resourceClass == ResourceClass.VideoHover ? 1 : 0);
        }

        return limit;
    }

    public ResourceGovernorSnapshot Snapshot()
    {
        lock (_sync)
        {
            var classes = _classes.ToDictionary(
                pair => pair.Key,
                pair => (pair.Value.Active, pair.Value.Waiting.Count, CurrentLimit(pair.Key)));
            return new ResourceGovernorSnapshot(_mode, _tier, _memoryPressure, _workingSet, classes);
        }
    }

    internal void Release(ResourceClass resourceClass)
    {
        lock (_sync)
        {
            var state = _classes[resourceClass];
            state.Active = Math.Max(0, state.Active - 1);
            PumpLocked(resourceClass);
        }
    }

    public void Update()
    {
        bool changed;
        lock (_sync)
        {
            changed = Recompute();
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var state in _classes.Values)
            {
                foreach (var waiter in state.Waiting)
                    waiter.TrySetCanceled();
                state.Waiting.Clear();
            }
        }

        // The deadline callback takes the same lock, so none can rearm its timer
        // after disposal wins the lock.
        _interactionTimer.Dispose();
        _timer.Dispose();
    }

    private void Tick()
    {
        if (_disposed) return;
        // UpdateMemoryPressure performs Recompute, so a second Update is redundant.
        SampleMemory();
    }

    private void SampleMemory()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var workingSet = process.WorkingSet64;
            var info = GC.GetGCMemoryInfo();
            var systemPressure = info.HighMemoryLoadThresholdBytes > 0 && info.MemoryLoadBytes >= info.HighMemoryLoadThresholdBytes;
            // A fixed process working-set ceiling is misleading on the supported range of
            // machines: it starves preparation early on a 12 GB system and is too permissive on
            // a smaller one. Use the OS memory-load signal plus usable headroom instead.
            var available = info.TotalAvailableMemoryBytes;
            var headroomThreshold = available > 0
                ? Math.Max(256L * 1024 * 1024, (long)(available * 0.85))
                : long.MaxValue;
            var pressure = workingSet >= headroomThreshold || systemPressure;
            bool raiseTrim;
            lock (_sync) raiseTrim = pressure && !_memoryPressure;
            UpdateMemoryPressure(pressure, workingSet);

            if (raiseTrim)
            {
                Trace.TraceWarning("ResourceGovernor: memory pressure (working set {0:N0} MB); asking caches to trim.", workingSet / (1024 * 1024));
                TrimRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private bool Recompute(bool? previousMemoryPressure = null)
    {
        var previousMode = _mode;
        var previousPressure = previousMemoryPressure ?? _memoryPressure;
        var now = _clock();
        if (IsScrolling && now >= _interactiveUntil)
        {
            IsScrolling = false;
        }

        var mode = !_windowActive || _windowMinimized
            ? InteractionMode.Background
            : now < _interactiveUntil ? InteractionMode.ForegroundInteractive : InteractionMode.ForegroundIdle;

        var tier = _windowMinimized
            ? PresentationTier.Suspended
            : _reducedMotion
                ? PresentationTier.Fallback
                : !_windowActive
                    ? PresentationTier.Fallback
                    : _memoryPressure || Volatile.Read(ref _importPressure) > 0
                        ? PresentationTier.Reduced
                        : PresentationTier.Full;

        var changed = mode != _mode || tier != _tier;
        _mode = mode;
        _tier = tier;
        // Covers both direct interaction and a foreground window being restored
        // while a prior interaction deadline is still active.
        ScheduleInteractionWakeLocked();

        if (mode != previousMode || _memoryPressure != previousPressure)
        {
            foreach (var resourceClass in _classes.Keys)
            {
                if (CurrentLimit(resourceClass) > LimitFor(resourceClass, previousMode, previousPressure))
                {
                    PumpLocked(resourceClass);
                }
            }
        }

        return changed;
    }

    private void PumpLocked(ResourceClass resourceClass)
    {
        var state = _classes[resourceClass];
        while (state.Waiting.First is { } next && state.Active < CurrentLimit(resourceClass))
        {
            state.Waiting.RemoveFirst();
            state.Active++;
            if (!next.Value.TrySetResult(new ResourcePermit(this, resourceClass)))
            {
                state.Active--;
            }
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("ResourceGovernor change handler failed: {0}", exception.GetType().Name);
        }
    }

    private sealed class ClassState
    {
        public int Active;
        public LinkedList<TaskCompletionSource<ResourcePermit>> Waiting { get; } = new();
    }

    private sealed class Scope(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

public sealed class ResourcePermit : IDisposable
{
    private ResourceGovernor? _owner;

    internal ResourcePermit(ResourceGovernor owner, ResourceClass resourceClass)
    {
        _owner = owner;
        ResourceClass = resourceClass;
    }

    public ResourceClass ResourceClass { get; }

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(ResourceClass);
}
