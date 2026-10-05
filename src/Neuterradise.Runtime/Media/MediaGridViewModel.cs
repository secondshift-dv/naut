using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Neuterradise.App.Shell;

namespace Neuterradise.App.Media;

public sealed class MediaGridCardViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private string? _thumbnailPath;
    private string? _hoverPath;
    private bool? _favoriteOverride;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MediaGridCardViewModel(MediaGridItem item, string? thumbnailPath = null, string? hoverPath = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
        _thumbnailPath = thumbnailPath ?? item.ThumbnailPath;
        _hoverPath = hoverPath ?? item.HoverPath;
    }

    public MediaGridItem Item { get; }
    public Guid MediaId => Item.MediaId;
    public MediaType MediaType => Item.MediaType;
    public MediaRelationBadge Relation => Item.Relation;
    public string? CurrentManagedFileName => Item.CurrentManagedFileName;
    public string? DisplayFileName => Item.DisplayFileName ?? Item.CurrentManagedFileName;
    public int? PixelWidth => Item.PixelWidth;
    public int? PixelHeight => Item.PixelHeight;
    public int? DurationMs => Item.DurationMs;
    public bool HasAttention => Item.HasAttention;

    /// <summary>
    /// Media Favorite. The override lets the fire mark flip the instant the user clicks it
    /// while the catalog write is still in flight; <see cref="SetFavorite"/> then reconciles it to
    /// whatever the catalog actually stored.
    /// </summary>
    public bool IsFavorite => _favoriteOverride ?? Item.IsFavorite;

    public bool ShowFavoriteBadge => IsFavorite;
    public bool IsVideoAndFavorite => IsVideo && IsFavorite;

    public void SetFavorite(bool isFavorite)
    {
        if (IsFavorite == isFavorite)
        {
            return;
        }

        _favoriteOverride = isFavorite;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowFavoriteBadge)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVideoAndFavorite)));
    }

    public bool IsImage => MediaType == MediaType.Image;
    public bool IsVideo => MediaType == MediaType.Video;
    public bool IsModel => MediaType == MediaType.Model;

    public string FormattedDuration => FormatDuration(DurationMs);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set => SetField(ref _thumbnailPath, value);
    }

    private ImageRef? _thumbnailSource;

    /// <summary>
    /// The tile image: a derived thumbnail decoded at tile size off the UI thread and shared through the
    /// bounded memory cache. Binding a path string instead made the UI decode the file synchronously, at
    /// full resolution, on the UI thread for every tile.
    /// </summary>
    public ImageRef? ThumbnailSource
    {
        get => _thumbnailSource;
        set => SetField(ref _thumbnailSource, value);
    }

    public string? HoverPath
    {
        get => _hoverPath;
        set => SetField(ref _hoverPath, value);
    }

    private static string FormatDuration(int? durationMs)
    {
        if (!durationMs.HasValue || durationMs.Value <= 0) return string.Empty;
        var ts = TimeSpan.FromMilliseconds(durationMs.Value);
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class MediaGridViewModel : INotifyPropertyChanged, IDisposable
{
    public const int DefaultPageSize = 48;
    public static readonly IReadOnlyList<int> PageSizeOptions = [24, 48, 96];

    private MediaGridState _state = MediaGridState.Ready;
    private MediaRelationFilter _relationFilter = MediaRelationFilter.All;
    private MediaTypeFilter _typeFilter = MediaTypeFilter.All;
    private MediaGridSort _sort = MediaGridSort.NewestFirst;
    private bool _isFavoriteOnly;
    private int _pageSize = DefaultPageSize;
    private int _currentPage = 1;
    private int _totalCount;
    private bool _isDisposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MediaGridViewModel(MediaSelectionModel? selection = null)
    {
        Selection = selection ?? new MediaSelectionModel();
        Selection.Changed += OnSelectionChanged;

        FirstPageCommand = new RelayCommand(_ => GoToPage(1), _ => CanFirstPage);
        PreviousPageCommand = new RelayCommand(_ => GoToPage(CurrentPage - 1), _ => CanPreviousPage);
        NextPageCommand = new RelayCommand(_ => GoToPage(CurrentPage + 1), _ => CanNextPage);
        LastPageCommand = new RelayCommand(_ => GoToPage(TotalPages), _ => CanLastPage);
        ToggleFavoriteFilterCommand = new RelayCommand(_ => IsFavoriteOnly = !IsFavoriteOnly);
        // Awaited through the async command. ToggleFavoriteAsync handles its own failures, and the
        // onError hook keeps anything unexpected from ever becoming an unobserved task.
        ToggleFavoriteCommand = new AsyncRelayCommand(
            parameter => parameter is MediaGridCardViewModel card ? ToggleFavoriteAsync(card) : Task.CompletedTask,
            onError: exception => Trace.TraceError("Media favorite command failed: {0}", exception));
    }

    /// <summary>
    /// Persists a Media Favorite change. Supplied by the hosting view model, which owns the catalog;
    /// the grid stays free of persistence concerns exactly as it does for opening media.
    /// Returns the value the catalog actually stored.
    /// </summary>
    public Func<Guid, bool, Task<bool>>? PersistFavoriteAction { get; set; }

    /// <summary>
    /// Receives a safe, user-facing sentence when a recoverable grid action (such as saving a
    /// favorite) fails. The hosting surface decides where to show it.
    /// </summary>
    public Action<string>? ReportRecoverableErrorAction { get; set; }

    private readonly HashSet<Guid> _favoriteWritesInFlight = [];

    /// <summary>
    /// Flips the fire mark optimistically, then reconciles with the stored value. If the write fails
    /// the mark returns to where it was and the failure is reported, never rethrown: a favorite that
    /// could not be saved is a recoverable problem, not a reason to shut the application down.
    /// Returns true when the catalog accepted the requested value.
    /// </summary>
    public async Task<bool> ToggleFavoriteAsync(MediaGridCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (_isDisposed || PersistFavoriteAction is null)
        {
            return false;
        }

        // One write per card at a time, so fast repeated clicks cannot race contradictory values.
        if (!_favoriteWritesInFlight.Add(card.MediaId))
        {
            return false;
        }

        var previous = card.IsFavorite;
        var desired = !previous;
        card.SetFavorite(desired);

        try
        {
            var stored = await PersistFavoriteAction(card.MediaId, desired).ConfigureAwait(true);
            card.SetFavorite(stored);
            return stored == desired;
        }
        catch (Exception exception)
        {
            card.SetFavorite(previous);
            Trace.TraceWarning("Media favorite could not be saved: {0}", exception);
            ReportRecoverableErrorAction?.Invoke(Localization.SurfaceText.Get(
                "Media.Favorite.Failed",
                "That favorite could not be saved. Please try again."));
            return false;
        }
        finally
        {
            _favoriteWritesInFlight.Remove(card.MediaId);
        }
    }

    public ObservableCollection<MediaGridCardViewModel> Cards { get; } = [];

    public MediaSelectionModel Selection { get; }

    public ICommand FirstPageCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand LastPageCommand { get; }
    public ICommand ToggleFavoriteFilterCommand { get; }

    /// <summary>Toggles the Media Favorite mark for the card passed as the command parameter.</summary>
    public ICommand ToggleFavoriteCommand { get; }

    public Action<Guid>? OpenMediaAction { get; set; }
    public Action<Guid>? InspectMediaAction { get; set; }
    public Action? QueryChangedAction { get; set; }

    public MediaGridState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowBlockingState)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowBackgroundState)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowEmptyState)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateMessage)));
            }
        }
    }

    public bool ShowBlockingState => State is MediaGridState.Loading or MediaGridState.RecoverableQueryError;
    public bool ShowBackgroundState => State is MediaGridState.BackgroundUpdating;
    public bool ShowEmptyState => State is MediaGridState.EmptyProfileMedia or MediaGridState.FilteredNoResults;

    public string StateMessage => State switch
    {
        MediaGridState.Loading => "Loading media…",
        MediaGridState.EmptyProfileMedia => "This Profile does not have media yet.",
        MediaGridState.FilteredNoResults => "No media matches these filters.",
        MediaGridState.BackgroundUpdating => "Media is updating in the background.",
        MediaGridState.RecoverablePreviewFailure => "A preview could not be shown. Open the media item for details.",
        MediaGridState.RecoverableQueryError => "Media could not be loaded. Try refreshing the Profile.",
        _ => string.Empty,
    };

    public MediaRelationFilter RelationFilter
    {
        get => _relationFilter;
        set
        {
            if (SetField(ref _relationFilter, value))
            {
                _currentPage = 1;
                Selection.Clear();
                NotifyPaginationChanged();
                QueryChangedAction?.Invoke();
            }
        }
    }

    public MediaTypeFilter TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (SetField(ref _typeFilter, value))
            {
                _currentPage = 1;
                Selection.Clear();
                NotifyPaginationChanged();
                QueryChangedAction?.Invoke();
            }
        }
    }

    public MediaGridSort Sort
    {
        get => _sort;
        set
        {
            if (SetField(ref _sort, value))
            {
                _currentPage = 1;
                NotifyPaginationChanged();
                QueryChangedAction?.Invoke();
            }
        }
    }

    public bool IsFavoriteOnly
    {
        get => _isFavoriteOnly;
        set
        {
            if (SetField(ref _isFavoriteOnly, value))
            {
                _currentPage = 1;
                Selection.Clear();
                NotifyPaginationChanged();
                QueryChangedAction?.Invoke();
            }
        }
    }

    public int PageSize
    {
        get => _pageSize;
        set
        {
            var valid = PageSizeOptions.Contains(value) ? value : DefaultPageSize;
            if (SetField(ref _pageSize, valid))
            {
                _currentPage = 1;
                NotifyPaginationChanged();
                QueryChangedAction?.Invoke();
            }
        }
    }

    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            var clamped = Math.Clamp(value, 1, TotalPages);
            if (SetField(ref _currentPage, clamped))
            {
                NotifyPaginationChanged();
                QueryChangedAction?.Invoke();
            }
        }
    }

    public int TotalCount
    {
        get => _totalCount;
        set
        {
            if (SetField(ref _totalCount, value))
            {
                NotifyPaginationChanged();
            }
        }
    }

    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / Math.Max(1, PageSize)));

    public string RangeText
    {
        get
        {
            if (TotalCount == 0) return "0–0 of 0";
            var start = (CurrentPage - 1) * PageSize + 1;
            var end = Math.Min(CurrentPage * PageSize, TotalCount);
            return $"{start}–{end} of {TotalCount}";
        }
    }

    public bool CanFirstPage => CurrentPage > 1 && TotalCount > 0;
    public bool CanPreviousPage => CurrentPage > 1 && TotalCount > 0;
    public bool CanNextPage => CurrentPage < TotalPages && TotalCount > 0;
    public bool CanLastPage => CurrentPage < TotalPages && TotalCount > 0;

    public void GoToPage(int page)
    {
        if (_isDisposed) return;
        var target = Math.Clamp(page, 1, TotalPages);
        if (target != CurrentPage)
        {
            CurrentPage = target;
        }
    }

    private void NotifyPaginationChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TotalPages)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RangeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanFirstPage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPreviousPage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanNextPage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanLastPage)));
        (FirstPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PreviousPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (LastPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public MediaOriginState CaptureState(Guid profileId)
    {
        return new MediaOriginState(
            ProfileId: profileId,
            RelationFilter: RelationFilter.ToString(),
            TypeFilter: TypeFilter.ToString(),
            Sort: Sort.ToString(),
            SelectedMediaId: Selection.SelectedMediaId,
            Page: CurrentPage,
            PageSize: PageSize,
            IsFavoriteOnly: IsFavoriteOnly);
    }

    public void RestoreState(MediaOriginState? state)
    {
        if (state == null) return;
        _relationFilter = Enum.TryParse<MediaRelationFilter>(state.RelationFilter, ignoreCase: true, out var relationFilter)
            ? relationFilter
            : MediaRelationFilter.All;
        _typeFilter = Enum.TryParse<MediaTypeFilter>(state.TypeFilter, ignoreCase: true, out var typeFilter)
            ? typeFilter
            : MediaTypeFilter.All;
        _sort = Enum.TryParse<MediaGridSort>(state.Sort, ignoreCase: true, out var sort)
            ? sort
            : MediaGridSort.NewestFirst;
        _pageSize = PageSizeOptions.Contains(state.PageSize) ? state.PageSize : DefaultPageSize;
        _currentPage = Math.Max(1, state.Page);
        _isFavoriteOnly = state.IsFavoriteOnly;

        Selection.Clear();
        if (state.SelectedMediaId is { } selected && selected != Guid.Empty)
        {
            Selection.SelectOnly(selected);
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RelationFilter)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TypeFilter)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sort)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSize)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentPage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavoriteOnly)));
        NotifyPaginationChanged();
    }

    public void SetItems(
        IReadOnlyList<MediaGridItem> items,
        Func<MediaGridItem, (string? ThumbnailPath, string? HoverPath)>? pathResolver = null,
        int? totalCount = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        Cards.Clear();

        foreach (var item in items)
        {
            var (thumb, prev) = pathResolver?.Invoke(item) ?? (item.ThumbnailPath, item.HoverPath);
            var card = new MediaGridCardViewModel(item, thumb, prev)
            {
                IsSelected = Selection.IsSelected(item.MediaId)
            };
            Cards.Add(card);
        }

        TotalCount = totalCount ?? Cards.Count;
        Selection.ReconcileTo(Cards.Select(c => c.MediaId).ToList());

        if (Cards.Count == 0)
        {
            State = RelationFilter != MediaRelationFilter.All || TypeFilter != MediaTypeFilter.All || IsFavoriteOnly
                ? MediaGridState.FilteredNoResults
                : MediaGridState.EmptyProfileMedia;
        }
        else
        {
            State = MediaGridState.Ready;
        }

        NotifyPaginationChanged();
    }

    /// <summary>
    /// Normal media activation is intentionally singular: a primary click selects this card and
    /// opens the managed original in the operating system's default application. Inspector access
    /// remains an explicit information action rather than an overloaded card-click behavior.
    /// </summary>
    public void HandleCardPointerDown(MediaGridCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (_isDisposed)
        {
            return;
        }

        Selection.SelectOnly(card.MediaId);
        OpenMediaAction?.Invoke(card.MediaId);
    }

    public void HandleCardClick(MediaGridCardViewModel card) => HandleCardPointerDown(card);

    public void HandleInspect(MediaGridCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (_isDisposed)
        {
            return;
        }

        Selection.SelectOnly(card.MediaId);
        InspectMediaAction?.Invoke(card.MediaId);
    }

    public void HandleKeyDown(Key key)
    {
        if (_isDisposed)
        {
            return;
        }

        // Escape is the only gallery-level keyboard state action. Enter belongs to focused
        // forms/dialogs and modifier shortcuts do not create hidden media-selection modes.
        if (key == Key.Escape)
        {
            Selection.Clear();
        }
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        foreach (var card in Cards)
        {
            card.IsSelected = Selection.IsSelected(card.MediaId);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Selection.Changed -= OnSelectionChanged;
        Cards.Clear();
    }
}
