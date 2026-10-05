using System.Diagnostics;

namespace Neuterradise.App.SystemServices.Database;

public static class CatalogInvalidationDomain
{
    public const string Profile = "PROFILE";
    public const string Category = "CATEGORY";
    public const string Tag = "TAG";
    public const string Appearance = "APPEARANCE";
    public const string Media = "MEDIA";
    public const string Face = "FACE";
    public const string Related = "RELATED";
    public const string Activity = "ACTIVITY";
    public const string Health = "HEALTH";
    public const string Import = "IMPORT";
    public const string Trash = "TRASH";
    public const string TaxonomyUsage = "TAXONOMY_USAGE";
}

public sealed record CatalogInvalidation(Guid VaultId, IReadOnlyList<Guid> EntityIds, string DomainKind, long Revision);

public sealed class CatalogWriteCoordinator : IDisposable
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;
    public event EventHandler<CatalogInvalidation>? Invalidated;

    public async ValueTask<Lease> EnterAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(this);
    }

    public void Publish(CatalogInvalidation invalidation) => PublishAfterCommit(invalidation);

    public void Publish(string domainKind, params Guid[] entityIds)
    {
        PublishAfterCommit(new CatalogInvalidation(Guid.Empty, entityIds, domainKind, 0));
    }

    internal void PublishAfterCommit(CatalogInvalidation invalidation)
    {
        ArgumentNullException.ThrowIfNull(invalidation);
        ArgumentException.ThrowIfNullOrWhiteSpace(invalidation.DomainKind);
        if (invalidation.Revision < 0) throw new ArgumentOutOfRangeException(nameof(invalidation));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var handlers = Invalidated?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var subscriber in handlers)
        {
            try
            {
                ((EventHandler<CatalogInvalidation>)subscriber)(this, invalidation);
            }
            catch (Exception exception) when (exception is not (
                OutOfMemoryException
                or StackOverflowException
                or AccessViolationException))
            {
                var identity = subscriber.Target?.GetType().FullName
                    ?? subscriber.Method.DeclaringType?.FullName
                    ?? subscriber.Method.Name;
                Trace.TraceWarning(
                    "Catalog invalidation subscriber failed after commit: domain={0}, subscriber={1}, error={2}",
                    invalidation.DomainKind,
                    identity,
                    exception.GetType().FullName ?? exception.GetType().Name);
            }
        }
    }

    private void Exit() => _writeGate.Release();
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _writeGate.Dispose(); }

    public sealed class Lease : IDisposable, IAsyncDisposable
    {
        private CatalogWriteCoordinator? _owner;
        internal Lease(CatalogWriteCoordinator owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
