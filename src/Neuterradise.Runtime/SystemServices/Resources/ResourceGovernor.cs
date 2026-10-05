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
    private readonly bool _systemTimer;

    private long _interactiveUntil;
    private bool _windowActive = true;
    private bool _windowMinimized;
    private bool _reducedMotion;
    private bool _memoryPressure;
    private int _importPressure;
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
        _timer = new Timer(_ => Tick(), null, startTimer ? TimeSpan.FromMilliseconds(250) : Timeout.InfiniteTimeSpan, startTimer ? TimeSpan.FromMilliseconds(250) : Timeout.InfiniteTimeSpan);
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

    /// <summary>Raised (on a pool thread) when the mode or presentation tier changes.</summary>
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

    public bool IsPrefetchAllowed => Mode == InteractionMode.ForegroundIdle && !_memoryPressure;

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
            var previousLimits = CaptureLimitsLocked();
            _workingSet = Math.Max(0, workingSetBytes);
            _memoryPressure = memoryPressure;
            changed = Recompute(previousLimits);
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

    public ValueTask<ResourcePermit> AcquireAsync(ResourceClass resourceClass, CancellationToken cancellationToken = default)
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
            state.Waiting.AddLast(waiter);
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

    public int CurrentLimit(ResourceClass resourceClass)
    {
        var limits = _mode switch
        {
            InteractionMode.ForegroundInteractive => InteractiveLimits,
            InteractionMode.ForegroundIdle => IdleLimits,
            _ => BackgroundLimits,
        };

        var limit = limits.For(resourceClass);
        if (_memoryPressure && resourceClass is ResourceClass.Prefetch or ResourceClass.VideoHover)
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        lock (_sync)
        {
            foreach (var state in _classes.Values)
            {
                foreach (var waiter in state.Waiting)
                {
                    waiter.TrySetCanceled();
                }

                state.Waiting.Clear();
            }
        }
    }

    private int _tickCount;

    private void Tick()
    {
        if (_disposed)
        {
            return;
        }

        if (_systemTimer && Interlocked.Increment(ref _tickCount) % 8 == 0)
        {
            SampleMemory();
        }

        Update();
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

    private bool Recompute(IReadOnlyDictionary<ResourceClass, int>? previousLimits = null)
    {
        previousLimits ??= CaptureLimitsLocked();
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

        foreach (var resourceClass in _classes.Keys)
        {
            if (CurrentLimit(resourceClass) > previousLimits[resourceClass])
            {
                PumpLocked(resourceClass);
            }
        }

        return changed;
    }

    private Dictionary<ResourceClass, int> CaptureLimitsLocked() =>
        _classes.Keys.ToDictionary(resourceClass => resourceClass, CurrentLimit);

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
