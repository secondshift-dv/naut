using System.Collections.ObjectModel;
using System.Windows.Input;
using Neuterradise.App.Localization;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Trash;

public sealed record TrashItemDisplayModel(
    Guid TrashEntryId,
    string EntityType,
    Guid EntityId,
    string DisplayName,
    DateTimeOffset TrashedAtUtc,
    string? RecoveryPath,
    string State);

public sealed class TrashViewModel : ScreenStateViewModel, IDisposable
{
    private const int ActiveTrashPageSize = 200;
    private readonly CatalogDb? _catalog;
    private readonly TrashOperations? _trashOperations;
    private bool _disposed;
    private readonly TrashReads? _trashReads;
    private readonly PurgeExecutor? _purgeExecutor;
    private readonly ProfileTrashRetentionService? _retentionService;
    private readonly AsyncRelayCommand _confirmPurgeCommand;
    private readonly AsyncRelayCommand _loadMoreCommand;

    private string? _actionMessage;
    private TrashItemDisplayModel? _pendingPurgeItem;
    private bool _isPurgeConfirmationActive;
    private bool _isEmptyTrashPending;
    private bool _isMassPurgeActive;
    private long _activeTrashCount;
    private string? _nextPageToken;
    private bool _hasMoreItems;

    public TrashViewModel(
        CatalogDb? catalog = null,
        TrashOperations? trashOperations = null,
        TrashReads? trashReads = null,
        PurgeExecutor? purgeExecutor = null)
    {
        _catalog = catalog;

        if (_catalog is not null)
        {
            var moveExecutor = new ManagedMoveExecutor(
                _catalog.Paths,
                new WindowsVolumeIdentityProvider(),
                new ManagedFileVerifier(),
                new MediaWrites(_catalog));

            _trashOperations = trashOperations ?? new TrashOperations(_catalog, moveExecutor: moveExecutor);
            _trashReads = trashReads ?? new TrashReads(_catalog);
            _purgeExecutor = purgeExecutor ?? new PurgeExecutor(_catalog, moveExecutor);
            _retentionService = new ProfileTrashRetentionService(_trashReads, _purgeExecutor);
        }
        else
        {
            _trashOperations = trashOperations;
            _trashReads = trashReads;
            _purgeExecutor = purgeExecutor;
        }

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(RouteCancellationToken));
        _loadMoreCommand = new AsyncRelayCommand(
            () => LoadMoreAsync(RouteCancellationToken),
            () => HasMoreItems);
        RestoreItemCommand = new AsyncRelayCommand(async param =>
        {
            if (param is TrashItemDisplayModel item)
            {
                await RestoreAsync(item, RouteCancellationToken);
            }
        });
        PurgeItemCommand = new RelayCommand(param =>
        {
            if (param is TrashItemDisplayModel item)
            {
                RequestPurgeConfirmation(item);
            }
        });
        EmptyTrashCommand = new RelayCommand(_ => RequestEmptyTrashConfirmation(), _ => _activeTrashCount > 0);
        _confirmPurgeCommand = new AsyncRelayCommand(
            () => ConfirmPendingPurgeAsync(),
            () => IsPurgeConfirmationActive);
        CancelPurgeCommand = new RelayCommand(_ => CancelPurgeConfirmation());

        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated += OnCatalogInvalidated;
        }
    }

    public ObservableCollection<TrashItemDisplayModel> Items { get; } = [];

    public string? ActionMessage
    {
        get => _actionMessage;
        set => SetProperty(ref _actionMessage, value);
    }

    public bool IsPurgeConfirmationActive
    {
        get => _isPurgeConfirmationActive;
        private set
        {
            if (SetProperty(ref _isPurgeConfirmationActive, value))
            {
                _confirmPurgeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? PurgeConfirmationTitle => _isEmptyTrashPending
        ? SurfaceText.Get("Trash.Purge.Empty.Title", "Empty Trash?")
        : _pendingPurgeItem is { } item
            ? SurfaceText.Format("Trash.Purge.Single.Title", "Permanently delete {0}?", item.DisplayName)
            : null;

    public string? PurgeConfirmationMessage => _isEmptyTrashPending
        ? SurfaceText.Format(
            "Trash.Purge.Empty.Message",
            "Permanently delete all {0} items in Trash and their managed files? This cannot be undone, and none can be restored afterward.",
            _activeTrashCount)
        : _pendingPurgeItem is { } item
            ? SurfaceText.Format(
                "Trash.Purge.Single.Message",
                "Permanently delete {0} and its managed files? This cannot be undone, and it cannot be restored afterward.",
                item.DisplayName)
            : null;

    public string PurgeConfirmationConfirmLabel => _isEmptyTrashPending
        ? SurfaceText.Get("Trash.Purge.Empty.Confirm", "Empty Trash permanently")
        : SurfaceText.Get("Trash.Purge.Single.Confirm", "Delete permanently");

    public ICommand RefreshCommand { get; }
    public ICommand LoadMoreCommand => _loadMoreCommand;
    public ICommand RestoreItemCommand { get; }
    public ICommand PurgeItemCommand { get; }
    public ICommand EmptyTrashCommand { get; }
    public ICommand ConfirmPurgeCommand => _confirmPurgeCommand;
    public ICommand CancelPurgeCommand { get; }

    public bool HasMoreItems
    {
        get => _hasMoreItems;
        private set
        {
            if (SetProperty(ref _hasMoreItems, value))
            {
                _loadMoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_trashReads is null)
        {
            ShowReady();
            return;
        }

        ShowLoading();
        Items.Clear();
        _nextPageToken = null;
        HasMoreItems = false;

        try
        {
            if (_retentionService is not null)
            {
                var sweep = await _retentionService
                    .PurgeExpiredProfilesAsync(cancellationToken)
                    .ConfigureAwait(true);
                ActionMessage = sweep.Failures.Count > 0
                    ? $"{sweep.Failures.Count} expired Profile(s) could not be deleted automatically. You can retry from Trash."
                    : sweep.PurgedCount > 0
                        ? $"{sweep.PurgedCount} Profile(s) older than {ProfileTrashRetentionPolicy.ProfileRetentionDays} days were permanently deleted."
                        : null;
            }

            var page = await _trashReads.GetActiveTrashPageAsync(
                pageSize: ActiveTrashPageSize,
                cancellationToken: cancellationToken);
            _nextPageToken = page.NextPageToken;
            HasMoreItems = page.HasMore;
            _activeTrashCount = await _trashReads.CountActiveTrashEntriesAsync(cancellationToken);
            RaiseEmptyTrashCanExecute();
            if (_isEmptyTrashPending)
            {
                RaisePurgeConfirmationCopyChanged();
            }

            foreach (var entry in page.Items)
            {
                Items.Add(ToDisplayModel(entry));
            }

            if (Items.Count == 0)
            {
                ShowEmpty();
            }
            else
            {
                ShowReady();
            }
        }
        catch (Exception ex)
        {
            ShowRecoverableError($"Failed to load Trash items: {OperationExecution.SafeMessage(ex)}");
        }
    }

    public async Task LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        if (_trashReads is null || !HasMoreItems || string.IsNullOrWhiteSpace(_nextPageToken))
        {
            return;
        }

        try
        {
            var page = await _trashReads.GetActiveTrashPageAsync(
                pageSize: ActiveTrashPageSize,
                continuationToken: _nextPageToken,
                cancellationToken: cancellationToken);

            foreach (var entry in page.Items)
            {
                if (Items.All(item => item.TrashEntryId != entry.TrashEntryId))
                {
                    Items.Add(ToDisplayModel(entry));
                }
            }

            _nextPageToken = page.NextPageToken;
            HasMoreItems = page.HasMore;

            if (Items.Count == 0)
            {
                ShowEmpty();
            }
            else
            {
                ShowReady();
            }
        }
        catch (Exception ex)
        {
            ShowRecoverableError($"Failed to load more Trash items: {OperationExecution.SafeMessage(ex)}");
        }
    }

    public async Task RestoreAsync(TrashItemDisplayModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_trashOperations is null)
        {
            Items.Remove(item);
            ActionMessage = $"Restored {item.DisplayName}.";
            return;
        }

        try
        {
            ProfileRestoreOutcome? profileOutcome = null;
            if (string.Equals(item.EntityType, TrashEntityType.Media, StringComparison.OrdinalIgnoreCase))
            {
                var result = await _trashOperations.RestoreMediaAsync(item.TrashEntryId, cancellationToken: cancellationToken);
                if (!result.IsSuccess)
                {
                    ShowRecoverableError(result.UserMessage ?? "Failed to restore media asset.");
                    return;
                }
            }
            else
            {
                var result = await _trashOperations.RestoreProfileAsync(item.TrashEntryId, cancellationToken: cancellationToken);
                if (!result.IsSuccess)
                {
                    ShowRecoverableError(result.UserMessage ?? "Failed to restore profile.");
                    return;
                }

                profileOutcome = result.Value;
            }

            ActionMessage = profileOutcome switch
            {
                { PreservedChangedOwnershipCount: > 0 } outcome =>
                    $"Restored {item.DisplayName}. {outcome.ReturnedOwnedMediaCount} media item(s) returned to this Profile; "
                    + $"{outcome.PreservedChangedOwnershipCount} kept their current owner because ownership changed after deletion.",
                { ReturnedOwnedMediaCount: > 0 } outcome =>
                    $"Restored {item.DisplayName}. {outcome.ReturnedOwnedMediaCount} media item(s) returned to this Profile.",
                _ => $"Restored {item.DisplayName}.",
            };
            await RefreshAfterSingleRemovalAsync(item, cancellationToken);
        }
        catch (Exception ex)
        {
            ShowRecoverableError($"Restore failed: {OperationExecution.SafeMessage(ex)}");
        }
    }

    public void RequestPurgeConfirmation(TrashItemDisplayModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        _pendingPurgeItem = item;
        _isEmptyTrashPending = false;
        RaisePurgeConfirmationCopyChanged();
        IsPurgeConfirmationActive = true;
    }

    public void RequestEmptyTrashConfirmation()
    {
        if (_activeTrashCount == 0)
        {
            return;
        }

        _pendingPurgeItem = null;
        _isEmptyTrashPending = true;
        RaisePurgeConfirmationCopyChanged();
        IsPurgeConfirmationActive = true;
    }

    public void CancelPurgeConfirmation()
    {
        _pendingPurgeItem = null;
        _isEmptyTrashPending = false;
        RaisePurgeConfirmationCopyChanged();
        IsPurgeConfirmationActive = false;
    }

    public async Task ConfirmPendingPurgeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPurgeConfirmationActive)
        {
            return;
        }

        var item = _pendingPurgeItem;
        var emptyTrash = _isEmptyTrashPending;
        CancelPurgeConfirmation();

        if (emptyTrash)
        {
            await EmptyTrashAsync(cancellationToken);
        }
        else if (item is not null)
        {
            await PurgeAsync(item, cancellationToken);
        }
    }

    public async Task PurgeAsync(TrashItemDisplayModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_purgeExecutor is null)
        {
            Items.Remove(item);
            ActionMessage = $"Purged {item.DisplayName}.";
            return;
        }

        try
        {
            if (string.Equals(item.EntityType, TrashEntityType.Media, StringComparison.OrdinalIgnoreCase))
            {
                var prepare = await _purgeExecutor.PreparePurgeMediaAsync(item.TrashEntryId, cancellationToken);
                if (!prepare.IsSuccess || prepare.Value is null)
                {
                    ShowRecoverableError(prepare.UserMessage ?? "Failed to prepare Purge.");
                    return;
                }

                var confirm = await _purgeExecutor.ConfirmPurgeMediaAsync(
                    prepare.Value.PurgePlanId,
                    irreversibleConfirmation: true,
                    cancellationToken: cancellationToken);
                if (!confirm.IsSuccess)
                {
                    ShowRecoverableError(confirm.UserMessage ?? "Failed to confirm Purge.");
                    return;
                }
            }
            else
            {
                var prepare = await _purgeExecutor.PreparePurgeProfileAsync(item.TrashEntryId, cancellationToken);
                if (!prepare.IsSuccess || prepare.Value is null)
                {
                    ShowRecoverableError(prepare.UserMessage ?? "Failed to prepare Purge.");
                    return;
                }

                var confirm = await _purgeExecutor.ConfirmPurgeProfileAsync(
                    prepare.Value.PurgePlanId,
                    irreversibleConfirmation: true,
                    cancellationToken: cancellationToken);
                if (!confirm.IsSuccess)
                {
                    ShowRecoverableError(confirm.UserMessage ?? "Failed to confirm Purge.");
                    return;
                }
            }

            ActionMessage = $"Permanently purged {item.DisplayName}.";
            if (_isMassPurgeActive)
            {
                Items.Remove(item);
            }
            else
            {
                await RefreshAfterSingleRemovalAsync(item, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            ShowRecoverableError($"Purge failed: {OperationExecution.SafeMessage(ex)}");
        }
    }

    public async Task EmptyTrashAsync(CancellationToken cancellationToken = default)
    {
        if (_trashReads is null)
        {
            return;
        }

        _isMassPurgeActive = true;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = await _trashReads.GetActiveTrashEntriesAsync(limit: 200, cancellationToken: cancellationToken);
                if (batch.Count == 0)
                {
                    _activeTrashCount = 0;
                    _nextPageToken = null;
                    HasMoreItems = false;
                    RaiseEmptyTrashCanExecute();
                    ActionMessage = "Empty Trash completed.";
                    Items.Clear();
                    ShowEmpty();
                    return;
                }

                var before = await _trashReads.CountActiveTrashEntriesAsync(cancellationToken);
                foreach (var entry in batch)
                {
                    var item = Items.FirstOrDefault(x => x.TrashEntryId == entry.TrashEntryId)
                        ?? ToDisplayModel(entry);
                    await PurgeAsync(item, cancellationToken);
                }

                var after = await _trashReads.CountActiveTrashEntriesAsync(cancellationToken);
                _activeTrashCount = after;
                RaiseEmptyTrashCanExecute();
                if (after >= before)
                {
                    await LoadAsync(cancellationToken);
                    ActionMessage = "Trash could not be emptied completely. Review the remaining items and try again.";
                    return;
                }
            }
        }
        finally
        {
            _isMassPurgeActive = false;
        }
    }

    private async Task RefreshAfterSingleRemovalAsync(
        TrashItemDisplayModel item,
        CancellationToken cancellationToken)
    {
        Items.Remove(item);
        if (_trashReads is not null)
        {
            _activeTrashCount = await _trashReads.CountActiveTrashEntriesAsync(cancellationToken);
            RaiseEmptyTrashCanExecute();
        }
        else
        {
            _activeTrashCount = Math.Max(0, _activeTrashCount - 1);
            RaiseEmptyTrashCanExecute();
        }

        if (Items.Count == 0 && _activeTrashCount > 0 && _trashReads is not null)
        {
            // The UI only presents a bounded window. Refill it instead of falsely showing an empty
            // state while durable Trash rows still exist beyond that window.
            await LoadAsync(cancellationToken);
            return;
        }

        if (Items.Count == 0)
        {
            ShowEmpty();
        }
        else
        {
            ShowReady();
        }
    }

    private static TrashItemDisplayModel ToDisplayModel(TrashItemSummary entry) => new(
        entry.TrashEntryId,
        entry.EntityType,
        entry.EntityId,
        entry.DisplayName ?? $"{entry.EntityType} ({entry.EntityId.ToString()[..8]})",
        entry.CreatedAtUtc,
        entry.RecoveryRelativePath,
        entry.State);

    private void RaiseEmptyTrashCanExecute() =>
        ((RelayCommand)EmptyTrashCommand).RaiseCanExecuteChanged();

    private void RaisePurgeConfirmationCopyChanged()
    {
        RaisePropertyChanged(nameof(PurgeConfirmationTitle));
        RaisePropertyChanged(nameof(PurgeConfirmationMessage));
        RaisePropertyChanged(nameof(PurgeConfirmationConfirmLabel));
    }

    private void OnCatalogInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        if (_disposed)
        {
            return;
        }

        switch (invalidation.DomainKind)
        {
            case CatalogInvalidationDomain.Trash:
                ScheduleRefresh();
                break;
        }
    }

    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            return;
        }

        UiDispatch.Run(() => Coalescer.Signal());
    }

    private RefreshCoalescer? _refreshCoalescer;

    private RefreshCoalescer Coalescer => _refreshCoalescer ??= new RefreshCoalescer(
        ct => _disposed ? Task.CompletedTask : LoadAsync(ct),
        TimeSpan.FromMilliseconds(600));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshCoalescer?.Dispose();
        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidated;
        }
    }
}
