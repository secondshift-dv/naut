using System.Collections.Concurrent;

namespace Neuterradise.App.SystemServices.Database;

/// <summary>
/// Process-local serialization authority for all mutation actors that can advance, cancel,
/// publish, recover, or roll back one ImportUnit. The scope is established synchronously before
/// an asynchronous gate wait so nested authorities remain re-entrant even after contention.
/// </summary>
public sealed class ImportUnitMutationCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly AsyncLocal<FlowScope?> _flow = new();
    private int _disposed;

    public ValueTask<Lease> EnterAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            throw new ArgumentException("An ImportUnit mutation lease requires a stable identifier.", nameof(unitId));
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var scope = _flow.Value;
        if (scope is not null
            && scope.Depths.TryGetValue(unitId, out var depth)
            && depth > 0)
        {
            scope.Depths[unitId] = checked(depth + 1);
            return ValueTask.FromResult(new Lease(this, unitId, gate: null, scope, ownsGate: false));
        }

        scope ??= new FlowScope();
        _flow.Value = scope;

        if (scope.Depths.ContainsKey(unitId))
        {
            throw new InvalidOperationException(
                $"ImportUnit {unitId:D} already has a pending mutation acquisition in this async flow.");
        }

        // The pending marker is installed synchronously in the caller's ExecutionContext before
        // any await can yield. AwaitGateAsync mutates this shared holder after contention resolves.
        scope.Depths.Add(unitId, 0);

        var gate = _gates.GetOrAdd(unitId, static _ => new SemaphoreSlim(1, 1));
        var wait = gate.WaitAsync(cancellationToken);
        if (wait.IsCompletedSuccessfully)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                scope.Depths.Remove(unitId);
                gate.Release();
                throw new ObjectDisposedException(nameof(ImportUnitMutationCoordinator));
            }

            scope.Depths[unitId] = 1;
            return ValueTask.FromResult(new Lease(this, unitId, gate, scope, ownsGate: true));
        }

        return AwaitGateAsync(unitId, gate, scope, wait);
    }

    private async ValueTask<Lease> AwaitGateAsync(
        Guid unitId,
        SemaphoreSlim gate,
        FlowScope scope,
        Task wait)
    {
        try
        {
            await wait.ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) != 0)
            {
                gate.Release();
                throw new ObjectDisposedException(nameof(ImportUnitMutationCoordinator));
            }

            scope.Depths[unitId] = 1;
            return new Lease(this, unitId, gate, scope, ownsGate: true);
        }
        catch
        {
            scope.Depths.Remove(unitId);
            throw;
        }
    }

    private void Exit(Guid unitId, SemaphoreSlim? gate, FlowScope scope, bool ownsGate)
    {
        if (!scope.Depths.TryGetValue(unitId, out var depth) || depth <= 0)
        {
            throw new InvalidOperationException("ImportUnit mutation lease ownership was lost before release.");
        }

        if (ownsGate && depth != 1)
        {
            throw new InvalidOperationException(
                "The outer ImportUnit mutation lease was released before its nested mutation leases.");
        }

        depth--;
        if (depth == 0)
        {
            scope.Depths.Remove(unitId);
            if (ownsGate)
            {
                gate!.Release();
            }
        }
        else
        {
            scope.Depths[unitId] = depth;
        }

        if (scope.Depths.Count == 0 && ReferenceEquals(_flow.Value, scope))
        {
            _flow.Value = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }

        _gates.Clear();
        _flow.Value = null;
    }

    internal sealed class FlowScope
    {
        public Dictionary<Guid, int> Depths { get; } = [];
    }

    public sealed class Lease : IDisposable, IAsyncDisposable
    {
        private ImportUnitMutationCoordinator? _owner;
        private readonly Guid _unitId;
        private readonly SemaphoreSlim? _gate;
        private readonly FlowScope _scope;
        private readonly bool _ownsGate;

        internal Lease(
            ImportUnitMutationCoordinator owner,
            Guid unitId,
            SemaphoreSlim? gate,
            FlowScope scope,
            bool ownsGate)
        {
            _owner = owner;
            _unitId = unitId;
            _gate = gate;
            _scope = scope;
            _ownsGate = ownsGate;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Exit(_unitId, _gate, _scope, _ownsGate);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
