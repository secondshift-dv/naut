using System.Collections.ObjectModel;
using System.Windows.Input;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Activity;

public sealed record ActivityDisplayItem(
    Guid ActivityId,
    string EventType,
    string Description,
    string Detail,
    string Category,
    DateTimeOffset OccurredAtUtc,
    Guid? ProfileId,
    Guid? MediaId,
    Guid? ImportUnitId);

public sealed class ActivityViewModel : ScreenStateViewModel, IDisposable
{
    private readonly CatalogDb? _catalog;
    private readonly NavigationCoordinator? _navigation;
    private readonly ActivityReads? _activityReads;
    private bool _disposed;

    private readonly List<ActivityDisplayItem> _allItems = [];
    private string _selectedCategory = "All";
    private string? _nextPageToken;
    private bool _hasMore;

    public ActivityViewModel(
        CatalogDb? catalog = null,
        NavigationCoordinator? navigation = null,
        ActivityReads? activityReads = null)
    {
        _catalog = catalog;
        _navigation = navigation;
        _activityReads = activityReads ?? (_catalog is not null ? new ActivityReads(_catalog) : null);

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(reset: true, cancellationToken: RouteCancellationToken));
        LoadMoreCommand = new AsyncRelayCommand(() => LoadMoreAsync(RouteCancellationToken), () => HasMore);
        FilterCategoryCommand = new RelayCommand(param =>
        {
            if (param is string cat)
            {
                SelectedCategory = cat;
            }
        });

        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated += OnCatalogInvalidated;
        }
    }

    public ObservableCollection<ActivityDisplayItem> DisplayEntries { get; } = [];

    public IReadOnlyList<string> Categories => ActivityPresentation.Categories;

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool HasMore
    {
        get => _hasMore;
        private set
        {
            if (SetProperty(ref _hasMore, value))
            {
                (LoadMoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string? NextPageToken
    {
        get => _nextPageToken;
        private set => SetProperty(ref _nextPageToken, value);
    }

    public ICommand RefreshCommand { get; }

    public ICommand LoadMoreCommand { get; }

    public ICommand FilterCategoryCommand { get; }

    public async Task LoadAsync(bool reset = true, CancellationToken cancellationToken = default)
    {
        if (_activityReads is null)
        {
            ShowReady();
            return;
        }

        if (reset)
        {
            ShowLoading();
            _allItems.Clear();
            DisplayEntries.Clear();
            NextPageToken = null;
            HasMore = false;
        }

        try
        {
            var page = await _activityReads.GetActivityPageAsync(
                pageSize: 50,
                continuationToken: NextPageToken,
                cancellationToken: cancellationToken);

            foreach (var item in page.Items)
            {
                _allItems.Add(Project(item));
            }

            NextPageToken = page.NextPageToken;
            HasMore = page.HasMore;

            ApplyFilter();

            if (_allItems.Count == 0)
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
            ShowRecoverableError($"Failed to load activity journal: {OperationExecution.SafeMessage(ex)}");
        }
    }

    public async Task LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        if (!HasMore || string.IsNullOrEmpty(NextPageToken) || _activityReads is null)
        {
            return;
        }

        BeginBackgroundUpdate();
        try
        {
            var page = await _activityReads.GetActivityPageAsync(
                pageSize: 50,
                continuationToken: NextPageToken,
                cancellationToken: cancellationToken);

            foreach (var item in page.Items)
            {
                _allItems.Add(Project(item));
            }

            NextPageToken = page.NextPageToken;
            HasMore = page.HasMore;

            ApplyFilter();
        }
        catch (Exception ex)
        {
            ShowRecoverableError($"Failed to load more activity: {OperationExecution.SafeMessage(ex)}");
        }
        finally
        {
            EndBackgroundUpdate();
        }
    }

    private void ApplyFilter()
    {
        DisplayEntries.Clear();
        var filtered = ActivityPresentation.IsAllCategory(_selectedCategory)
            ? _allItems
            : _allItems.Where(item => string.Equals(item.Category, _selectedCategory, StringComparison.OrdinalIgnoreCase));

        foreach (var item in filtered)
        {
            DisplayEntries.Add(item);
        }
    }

    private static ActivityDisplayItem Project(ActivityItemReadModel item)
    {
        var eventDescription = DescribeActivity(item.EventType);
        var detail = ActivityPresentation.DescribeSubject(item);
        var visibleDescription = string.IsNullOrWhiteSpace(detail)
            ? eventDescription
            : $"{eventDescription} — {detail}";

        return new ActivityDisplayItem(
            item.ActivityId,
            item.EventType,
            visibleDescription,
            detail,
            CategorizeEvent(item.EventType),
            item.OccurredAtUtc,
            item.ProfileId,
            item.MediaId,
            item.ImportUnitId);
    }

    public static string DescribeActivity(string eventType) =>
        ActivityPresentation.DescribeActivity(eventType);

    public static string CategorizeEvent(string eventType) =>
        ActivityPresentation.CategorizeEvent(eventType);

    private void OnCatalogInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        if (_disposed)
        {
            return;
        }

        switch (invalidation.DomainKind)
        {
            case CatalogInvalidationDomain.Activity:
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
        ct => _disposed ? Task.CompletedTask : LoadAsync(reset: true, cancellationToken: ct),
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
