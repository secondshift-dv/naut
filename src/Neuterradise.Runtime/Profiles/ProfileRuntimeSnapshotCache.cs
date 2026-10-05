using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;

namespace Neuterradise.App.Profiles;

/// <summary>
/// Disposable runtime snapshot used to make Profile activation presentation-only.
/// The catalog remains canonical truth; this cache never writes or persists state.
/// </summary>
public sealed record ProfileRuntimeSnapshot(
    Guid ProfileId,
    ProfileDetailReadModel Detail,
    ProfileFolderReadModel? Folder,
    string DefaultLayoutPresetId,
    ProfileMediaPage InitialMediaPage);

public sealed class ProfileRuntimeSnapshotCache : IAsyncDisposable
{
    public const int MaximumEntries = 24;

    private readonly CatalogDb _catalog;
    private readonly ProfileReads _profileReads;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Task<ProfileRuntimeSnapshot?>> _entries = [];
    private readonly LinkedList<Guid> _lru = [];
    private readonly Dictionary<Guid, LinkedListNode<Guid>> _nodes = [];
    private bool _disposed;
    public ProfileRuntimeSnapshotCache(CatalogDb catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _profileReads = catalog.ProfileReads;
        _catalog.WriteCoordinator.Invalidated += OnInvalidated;
    }

    public void Warm(Guid profileId)
    {
        if (profileId == Guid.Empty || _disposed)
        {
            return;
        }

        var task = GetOrCreate(profileId);
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task<ProfileRuntimeSnapshot?> WarmAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty || _disposed)
        {
            return null;
        }

        var task = GetOrCreate(profileId);
        return cancellationToken.CanBeCanceled
            ? await task.WaitAsync(cancellationToken).ConfigureAwait(false)
            : await task.ConfigureAwait(false);
    }

    public bool TryGetReady(Guid profileId, out ProfileRuntimeSnapshot? snapshot)
    {
        snapshot = null;
        if (profileId == Guid.Empty || _disposed)
        {
            return false;
        }
        lock (_sync)
        {
            if (!_entries.TryGetValue(profileId, out var task)
                || !task.IsCompletedSuccessfully
                || task.Result is not { } ready)
            {
                return false;
            }

            Touch(profileId);
            snapshot = ready;
            return true;
        }
    }

    public void Invalidate(Guid profileId)
    {
        if (profileId == Guid.Empty)
        {
            return;
        }

        lock (_sync)
        {
            _entries.Remove(profileId);
            if (_nodes.Remove(profileId, out var node))
            {
                _lru.Remove(node);
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _nodes.Clear();
            _lru.Clear();
        }
    }

    private Task<ProfileRuntimeSnapshot?> GetOrCreate(Guid profileId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(profileId, out var existing))
            {
                Touch(profileId);
                return existing;
            }

            var task = BuildAsync(profileId, _lifetime.Token);
            _entries[profileId] = task;
            Touch(profileId);
            Trim();
            return task;
        }
    }
    private async Task<ProfileRuntimeSnapshot?> BuildAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var detailTask = _profileReads.GetDetailAsync(profileId, cancellationToken);
        var folderTask = _profileReads.GetFolderAsync(profileId, cancellationToken);
        var mediaTask = _profileReads.GetMediaPageAsync(
            profileId,
            pageSize: MediaGridViewModel.DefaultPageSize,
            cancellationToken: cancellationToken);

        await Task.WhenAll(detailTask, folderTask, mediaTask).ConfigureAwait(false);
        var detail = detailTask.Result;
        if (detail is null)
        {
            return null;
        }

        return new ProfileRuntimeSnapshot(
            profileId,
            detail,
            folderTask.Result,
            ProfileLayoutResolver.FallbackPresetId,
            mediaTask.Result);
    }

    private void OnInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        if (_disposed)
        {
            return;
        }

        if (invalidation.DomainKind is CatalogInvalidationDomain.Profile
            or CatalogInvalidationDomain.Appearance)
        {
            if (invalidation.EntityIds.Count == 0)
            {
                Clear();
                return;
            }

            foreach (var profileId in invalidation.EntityIds)
            {
                Invalidate(profileId);
            }
            return;
        }
        if (invalidation.DomainKind is CatalogInvalidationDomain.Media
            or CatalogInvalidationDomain.Category
            or CatalogInvalidationDomain.Tag
            or CatalogInvalidationDomain.Import
            or CatalogInvalidationDomain.Trash
            or CatalogInvalidationDomain.TaxonomyUsage)
        {
            Clear();
        }
    }

    private void Touch(Guid profileId)
    {
        if (_nodes.Remove(profileId, out var existing))
        {
            _lru.Remove(existing);
        }

        var node = _lru.AddFirst(profileId);
        _nodes[profileId] = node;
    }

    private void Trim()
    {
        while (_entries.Count > MaximumEntries && _lru.Last is { } last)
        {
            var profileId = last.Value;
            _lru.RemoveLast();
            _nodes.Remove(profileId);
            _entries.Remove(profileId);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _catalog.WriteCoordinator.Invalidated -= OnInvalidated;
        _lifetime.Cancel();
        _lifetime.Dispose();
        Clear();
        return ValueTask.CompletedTask;
    }
}
