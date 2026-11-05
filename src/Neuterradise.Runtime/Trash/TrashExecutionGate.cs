using System.Runtime.CompilerServices;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Trash;

/// <summary>
/// Serializes lifecycle I/O as well as catalog commits within one Vault runtime. The catalog
/// journals retain restart authority; this gate prevents a live Restore racing physical Purge.
/// Nested group members share the parent's lease instead of starting competing operations.
/// </summary>
internal sealed class TrashExecutionGate
{
    private static readonly ConditionalWeakTable<CatalogDb, TrashExecutionGate> Gates = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly AsyncLocal<bool> _inside = new();

    internal static Task<T> RunAsync<T>(CatalogDb catalog, Func<Task<T>> action, CancellationToken ct) =>
        Gates.GetValue(catalog, _ => new TrashExecutionGate()).RunAsync(action, ct);

    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (_inside.Value) return await action().ConfigureAwait(false);
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _inside.Value = true;
            return await action().ConfigureAwait(false);
        }
        finally { _inside.Value = false; _mutex.Release(); }
    }
}
