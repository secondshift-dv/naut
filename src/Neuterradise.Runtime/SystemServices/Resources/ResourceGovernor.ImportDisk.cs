using System.Collections.Concurrent;

namespace Neuterradise.App.SystemServices.Resources;

public sealed partial class ResourceGovernor
{
    private const int UnknownDisk = -1;
    private const long VolumeMappingLifetimeMilliseconds = 15_000;
    private readonly ConcurrentDictionary<string, VolumeMapping> _diskVolumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ImportDiskState> _importDisks = new();
    private TaskCompletionSource _diskAdmissionChanged = NewDiskSignal();

    internal async ValueTask<ImportDiskLease> AcquireImportDiskAsync(
        string firstPath, string? otherPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var first = ResolveImportDisks(firstPath);
        var second = otherPath is null ? [] : ResolveImportDisks(otherPath);
        var states = first.Concat(second).Distinct().Select(disk => _importDisks.GetOrAdd(disk, CreateDiskState)).ToArray();
        RefreshDisks(states);

        while (true)
        {
            Task changed;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (states.All(state => state.Active < state.Concurrency))
                {
                    foreach (var state in states) state.Active++;
                    return new ImportDiskLease(this, states);
                }
                changed = _diskAdmissionChanged.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Disk admissions atomically reserve all devices involved in one operation. The same device
    // shared by two volumes is charged once, and a transfer cannot hold one device waiting for the other.
    private int[] ResolveImportDisks(string path)
    {
        var volume = WindowsDiskTelemetry.GetVolume(path);
        if (volume is null) return [UnknownDisk];
        var now = Environment.TickCount64;
        if (_diskVolumes.TryGetValue(volume, out var cached) && cached.Expires > now) return cached.Numbers;
        var mapped = WindowsDiskTelemetry.GetDisks(volume);
        var result = new VolumeMapping(mapped.Length > 0 ? mapped : [UnknownDisk],
            now + VolumeMappingLifetimeMilliseconds);
        _diskVolumes[volume] = result;
        return result.Numbers;
    }

    private static ImportDiskState CreateDiskState(int disk) =>
        new(disk, disk == UnknownDisk ? DiskSeekPenalty.Unknown : WindowsDiskTelemetry.GetSeekPenalty(disk));

    private static void RefreshDisks(ImportDiskState[] states)
    {
        foreach (var state in states) state.Sample();
    }

    private void ReleaseImportDisks(ImportDiskState[] states)
    {
        lock (_sync)
        {
            foreach (var state in states) state.Active--;
            SignalDiskAdmissionsLocked();
        }
    }

    private void SignalDiskAdmissionsLocked()
    {
        var previous = _diskAdmissionChanged;
        _diskAdmissionChanged = NewDiskSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewDiskSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record VolumeMapping(int[] Numbers, long Expires);

    internal sealed class ImportDiskState(int number, DiskSeekPenalty penalty)
    {
        // Sample only from active import work, at device granularity, without a polling loop.
        private const int SampleIntervalMilliseconds = 1000;
        private const int PressureHoldMilliseconds = 2500;
        private long _nextSample;
        private long _pressureUntil;
        private int _delay;
        private DiskCounters? _previous;

        public int Active { get; set; }

        public int Delay => Environment.TickCount64 < Volatile.Read(ref _pressureUntil) ? Volatile.Read(ref _delay) : 0;

        public int Concurrency => penalty == DiskSeekPenalty.SolidState && Delay == 0 ? 2 : 1;
        public bool HasSeekPenalty => penalty != DiskSeekPenalty.SolidState;

        public void Sample()
        {
            if (number == UnknownDisk) return;
            var now = Environment.TickCount64;
            var next = Volatile.Read(ref _nextSample);
            if (next > now || Interlocked.CompareExchange(ref _nextSample, now + SampleIntervalMilliseconds, next) != next)
                return;
            var current = WindowsDiskTelemetry.GetCounters(number);
            if (_previous is { } before && current is { } after && after.QueryTime > before.QueryTime
                && after.ReadTime >= before.ReadTime && after.WriteTime >= before.WriteTime)
            {
                var operations = (long)unchecked(after.Reads - before.Reads) + unchecked(after.Writes - before.Writes);
                var latency = operations == 0 ? 0d
                    : ((double)(after.ReadTime - before.ReadTime) + (after.WriteTime - before.WriteTime)) / operations / TimeSpan.TicksPerMillisecond;
                var busy = 1 - (double)(after.IdleTime - before.IdleTime) / (after.QueryTime - before.QueryTime);
                var solidState = penalty == DiskSeekPenalty.SolidState;
                var maxHealthyLatency = solidState ? 10d : 22d;
                var queued = after.QueueDepth >= (solidState ? 8u : 2u);
                if (queued && (busy >= 0.85 || latency >= maxHealthyLatency)
                    || busy >= 0.9 && latency >= maxHealthyLatency)
                {
                    Volatile.Write(ref _delay, solidState ? 4 : latency >= 2 * maxHealthyLatency || after.QueueDepth >= 4 ? 12 : 6);
                    Volatile.Write(ref _pressureUntil, now + PressureHoldMilliseconds);
                }
            }
            _previous = current;
        }
    }

    internal sealed class ImportDiskLease(ResourceGovernor governor, ImportDiskState[] states) : IDisposable
    {
        private ImportDiskState[]? _states = states;

        public DiskIoPriority BulkPriority
        {
            get
            {
                var priority = DiskIoPriority.Normal;
                if (_states is not { } deviceStates) return priority;
                foreach (var disk in deviceStates)
                {
                    if (!disk.HasSeekPenalty) continue;
                    if (disk.Delay >= 12) return DiskIoPriority.VeryLow;
                    priority = DiskIoPriority.Low;
                }
                return priority;
            }
        }

        public long BytesBetweenYields => BulkPriority == DiskIoPriority.VeryLow
            ? TransferThrottleQuantumBytes / 4 : TransferThrottleQuantumBytes;

        public async ValueTask YieldAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_states is not { } deviceStates) return;
            RefreshDisks(deviceStates);
            var delay = governor.BackgroundDiskDelayMilliseconds;
            foreach (var device in deviceStates) delay = Math.Max(delay, device.Delay);
            if (delay > 0) await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _states, null) is { } deviceStates)
                governor.ReleaseImportDisks(deviceStates);
        }
    }
}
