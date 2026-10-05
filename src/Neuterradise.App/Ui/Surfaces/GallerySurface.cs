using Microsoft.UI.Xaml;
using Layout = Microsoft.UI.Xaml.Controls.Layout;
using ToggleButton = Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Gallery;
using Neuterradise.App.Presentation;
using Neuterradise.App.Shell;

namespace Neuterradise.App.Ui;

/// <summary>
/// Gallery: calm premium browsing over a virtualized repeater (no ScrollViewer + WrapPanel). Only the
/// viewport plus a small buffer is realized; cards are pooled per compiled card plan and rebound on
/// reuse. Gallery Layout is only the virtualized shell; each Profile's own Profile Card remains
/// the content rendered inside that shell.
/// </summary>
public sealed class GallerySurface : Surface, ITopBarCustomizableSurface
{
    private readonly GalleryViewModel _vm;
    private readonly Grid _root = UI.Grid("auto,*,auto", "*");
    private readonly PooledElementFactory _factory;
    private readonly ItemsRepeater _repeater;
    private readonly ScrollViewer _scroll;
    private readonly Grid _field = new();
    private readonly TextBlock _count = UI.Text(string.Empty, "metadata");
    private readonly TextBlock _notice = UI.WrappedText(string.Empty, "caption", "danger");
    private readonly Grid _empty = new() { Visibility = Visibility.Collapsed };
    private readonly FrameworkElement _paginationHost;
    private readonly ContentControl _header = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private Disposables _headerBag = new();
    private readonly Dictionary<Guid, CompiledDefinition> _cardByProfile = [];
    private readonly Dictionary<FrameworkElement, IDisposable> _cardObservers = [];
    private GalleryLayoutPlan _layout = null!;
    private int _presentationGeneration;
    private CompiledDefinition _defaultCard = null!;
    private bool _initialized;

    public GallerySurface(AppServices services, GalleryViewModel vm) : base(services)
    {
        _vm = vm;
        _root.Background = ThemeRuntime.Current.Brush("canvas");
        _factory = new PooledElementFactory(KindOf, Create, Bind, Recycle);
        (_scroll, _repeater) = Repeaters.Virtualized(_factory, Repeaters.Grid(240, 135, 16));
        _repeater.ItemsSource = _vm.Cards;
        _scroll.ViewChanged += (_, _) => HoverVideoCoordinator.Shared.NotifyScroll();
        _field.Children.Add(_scroll);
        _field.Children.Add(_empty);
        _field.SizeChanged += (_, _) => { if (_layout is not null) ApplyLayout(refreshItems: false); };

        _root.Children.Add(_header.At(0));
        Bag.Add(() => _headerBag.Dispose());
        _root.Children.Add(_field.At(1));
        _paginationHost = Pagination();
        _root.Children.Add(_paginationHost.At(2));

        _root.SizeChanged += (_, _) =>
        {
            ApplyPadding();
            if (_layout?.Primitive is CollectionPrimitives.Carousel or CollectionPrimitives.SingleColumn)
            {
                ApplyLayout();
            }
        };

        ResolvePlans();
        _notice.TextWrapping = TextWrapping.Wrap;
        Bag.Add(Observe.Props(
            _vm,
            UpdateState,
            nameof(GalleryViewModel.Status),
            nameof(GalleryViewModel.ErrorMessage),
            nameof(GalleryViewModel.ResultCountText),
            nameof(GalleryViewModel.ErrorNotice)));
        Bag.Add(Observe.Collection(_vm.Cards, () => _cardByProfile.Clear()));
        UpdateState();
    }

    public override FrameworkElement View => _root;

    public override Neuterradise.App.Shell.ScreenStateViewModel Model => _vm;

    public string TopBarCustomizeLabel => UI.T("Gallery.Customize", "Customize Gallery");

    public bool CanTopBarCustomize => true;

    public void OpenTopBarCustomization() =>
        Services.OpenCustomization(CustomizationCategories.Gallery, Context);

    public GalleryOriginState CaptureOrigin() => _vm.BuildOrigin(_vm.SelectedProfileId, _scroll.VerticalOffset);

    /// <summary>A real loaded card for Customization previews: the requested Profile, else the first visible one.</summary>
    public Neuterradise.App.Gallery.GalleryCardViewModel? SampleCard(Guid? profileId) =>
        (profileId is { } id ? _vm.Cards.FirstOrDefault(c => c.ProfileId == id) : null) ?? _vm.Cards.FirstOrDefault();

    private PresentationContext Context => new("gallery");

    protected override void RefreshPreviewPresentation(string? slot = null)
    {
        if (slot is not null && slot != PresentationSlots.GalleryLayout) return;
        var horizontal = _scroll.HorizontalOffset;
        var vertical = _scroll.VerticalOffset;
        ResolvePlans();
        _scroll.DispatcherQueue.TryEnqueue(() =>
            _scroll.ChangeView(horizontal, vertical, null, disableAnimation: true));
    }

    private void ResolvePlans()
    {
        var layoutDefinition = ResolvePresentation(PresentationSlots.GalleryLayout, Context);
        _layout = layoutDefinition.PlanAs<GalleryLayoutPlan>();
        _defaultCard = ResolvePresentation(PresentationSlots.ProfileCard, Context);
        _headerBag.Dispose();
        _headerBag = new Disposables();
        SurfaceComposer.Detach(_count);
        SurfaceComposer.Detach(_notice);
        _header.Content = Header();

        _cardByProfile.Clear();
        _presentationGeneration = unchecked(_presentationGeneration + 1);
        _factory.Clear();
        ApplyLayout();
    }

    private void ApplyLayout(bool refreshItems = true)
    {
        // Gallery owns the envelope; each Profile card owns its aspect and composition.
        var viewportWidth = Math.Max(180, _field.ActualWidth > 0 ? _field.ActualWidth : _root.ActualWidth);
        var viewportHeight = Math.Max(240, _field.ActualHeight > 0 ? _field.ActualHeight : 540);
        var availableWidth = Math.Max(120, viewportWidth - (2 * UI.PagePadding(viewportWidth)) - 12);
        var preferredWidth = Math.Min(availableWidth, Math.Clamp(_layout.ItemMinWidth, 120, _layout.ItemMaxWidth));
        var columns = Math.Max(1, (int)Math.Floor((availableWidth + _layout.Spacing) / (preferredWidth + _layout.Spacing)));
        columns = Math.Min(columns, Math.Max(1, _layout.MaxColumns));
        var width = Math.Min(_layout.ItemMaxWidth, (availableWidth - ((columns - 1) * _layout.Spacing)) / columns);
        if (_layout.Primitive == CollectionPrimitives.Carousel)
        {
            width = Math.Min(_layout.ItemMaxWidth, Math.Max(120, viewportWidth * 0.6));
        }
        var requestedHeight = _layout.ItemAspect is { } containerAspect
            ? width / Math.Max(0.3, containerAspect)
            : _layout.RowHeight;
        var rowHeight = Math.Min(Math.Max(120, requestedHeight), Math.Max(160, viewportHeight * 0.52));

        Layout layout = _layout.Primitive switch
        {
            // Uno Skia's LinedFlowLayout does not reliably realize this Gallery yet.
            // Preserve the curated semantic choices on the proven virtualized hosts.
            CollectionPrimitives.AdaptiveWall => Repeaters.Grid(width, rowHeight, _layout.Spacing, _layout.MaxColumns),
            CollectionPrimitives.Shelves => Repeaters.Grid(
                Math.Max(width, _layout.ItemMinWidth),
                rowHeight,
                _layout.Spacing,
                _layout.MaxColumns),
            CollectionPrimitives.SingleColumn => Repeaters.Stack(false, _layout.Spacing),
            CollectionPrimitives.List => Repeaters.Stack(false, _layout.Spacing),
            CollectionPrimitives.Carousel => Repeaters.Stack(true, _layout.Spacing),
            _ => Repeaters.Grid(width, rowHeight, _layout.Spacing, _layout.MaxColumns),
        };
        if (layout is UniformGridLayout gridLayout)
        {
            // The envelope owns row height; FitCardVisual preserves each Profile card aspect inside it.
            gridLayout.ItemsStretch = UniformGridLayoutItemsStretch.None;
        }
        _repeater.Layout = layout;
        if (_layout.HeightPattern is { } pattern)
            _repeater.Layout = new MasonryLayout(_layout.ItemMinWidth, _layout.ItemMaxWidth, _layout.MaxColumns, _layout.Spacing, pattern);
        foreach (var host in _cardObservers.Keys.OfType<Grid>().ToArray())
        {
            if (host.Children.FirstOrDefault() is CardVisual visual)
            {
                UpdateHostGeometry(host, visual);
            }
        }
        var horizontal = _layout.Primitive == CollectionPrimitives.Carousel;
        _scroll.HorizontalScrollMode = horizontal ? ScrollMode.Enabled : ScrollMode.Disabled;
        _scroll.HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollMode = horizontal ? ScrollMode.Disabled : ScrollMode.Enabled;
        if (refreshItems)
        {
            _repeater.ItemsSource = null;
            _repeater.ItemsSource = _vm.Cards;
        }
    }

    private void ApplyPadding()
    {
        var padding = UI.PagePadding(_root.ActualWidth);
        _scroll.Padding = new Thickness(padding, 6, padding, 16);
    }

    protected override void OnActivated(AppRoute route)
    {
        var origin = (route as GalleryRoute)?.Origin;
        if (origin is not null)
        {
            _vm.RestoreOrigin(origin);
        }

        var operation = ActivateAsync(origin);
        _initialized = true;
        Services.RunUserAction(operation, "GallerySurface.ActivateAsync", UI.T("Gallery.RefreshFailed", "Gallery could not be refreshed."));
    }

    private async Task ActivateAsync(GalleryOriginState? origin)
    {
        if (_initialized)
        {
            await _vm.RefreshAsync().ConfigureAwait(true);
        }
        else
        {
            await _vm.InitializeAsync().ConfigureAwait(true);
        }

        if (origin is not null && origin.AnchorOffsetDip > 0)
        {
            _scroll.ChangeView(null, origin.AnchorOffsetDip, null, disableAnimation: true);
        }
    }

    protected override void OnSuspended() => HoverVideoCoordinator.Shared.StopAll();

    public override void OnPresentationChanged(PresentationChangedEventArgs args)
    {
        var profilePresentationChanged = args.PacksChanged
            || args.Slots.Any(s => s.StartsWith("profile.", StringComparison.Ordinal)
                || s.StartsWith("surface.card", StringComparison.Ordinal));

        if (profilePresentationChanged && _initialized)
        {
            Services.RunUserAction(
                RefreshPresentationAsync(),
                "GallerySurface.RefreshPresentationAsync",
                UI.T("Gallery.RefreshFailed", "Gallery could not be refreshed."));
            return;
        }

        if (profilePresentationChanged
            || args.Slots.Any(s => s.StartsWith("gallery.", StringComparison.Ordinal)
                || s.StartsWith("appearance.", StringComparison.Ordinal)))
        {
            ResolvePlans();
        }
    }

    private async Task RefreshPresentationAsync()
    {
        await _vm.RefreshAsync().ConfigureAwait(true);
        ResolvePlans();
    }

    // ---------------------------------------------------------------- toolbar

    private FrameworkElement Header()
    {
        var search = new AutoSuggestBox
        {
            PlaceholderText = UI.T("Gallery.Search", "Search Profiles, categories or tags"),
            Text = _vm.SearchText,
            ItemsSource = _vm.Suggestions,
            TextMemberPath = nameof(GallerySearchSuggestion.Text),
        };
        search.MinWidth = 180;
        search.Width = 220;
        search.MaxWidth = 280;
        search.TextChanged += (sender, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                _vm.SearchText = sender.Text;
            }
        };
        search.SuggestionChosen += (_, args) =>
        {
            if (args.SelectedItem is GallerySearchSuggestion suggestion)
            {
                _vm.ApplySuggestion(suggestion);
            }
        };
        search.QuerySubmitted += (sender, args) =>
        {
            if (args.ChosenSuggestion is null)
            {
                _vm.SearchText = sender.Text;
                _vm.DismissSuggestions();
            }
        };
        _headerBag.Add(Observe.Props(_vm, () =>
        {
            if (!string.Equals(search.Text, _vm.SearchText, StringComparison.Ordinal))
            {
                search.Text = _vm.SearchText;
            }
            search.IsSuggestionListOpen = _vm.IsSuggestionFlyoutOpen;
        }, nameof(GalleryViewModel.SearchText), nameof(GalleryViewModel.IsSuggestionFlyoutOpen)));

        ComboBox FacetFilter(bool isCategory)
        {
            var label = isCategory ? UI.T("Gallery.Category", "Category") : UI.T("Gallery.Tag", "Tag");
            var allLabel = isCategory ? UI.T("Gallery.AllCategories", "All categories") : UI.T("Gallery.AllTags", "All tags");
            var options = isCategory ? _vm.Categories : _vm.Tags;
            var combo = new ComboBox { PlaceholderText = label, MinWidth = isCategory ? 140 : 120 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(combo, label);
            var synchronizing = false;
            void SyncSelection()
            {
                synchronizing = true;
                try
                {
                    var selectedId = isCategory ? _vm.SelectedCategoryId : _vm.SelectedTagId;
                    var index = options.ToList().FindIndex(option => string.Equals(option.Id, selectedId, StringComparison.Ordinal));
                    combo.SelectedIndex = string.IsNullOrWhiteSpace(selectedId)
                        ? 0
                        : index >= 0 ? index + 1 : -1;
                }
                finally
                {
                    synchronizing = false;
                }
            }
            void FillOptions()
            {
                synchronizing = true;
                try
                {
                    combo.ItemsSource = new[] { allLabel }.Concat(options.Select(option => $"{option.Name} ({option.ProfileCount})")).ToList();
                }
                finally
                {
                    synchronizing = false;
                }
                SyncSelection();
            }
            _headerBag.Add(Observe.Collection(options, FillOptions));
            _headerBag.Add(Observe.Props(_vm, SyncSelection,
                isCategory ? nameof(GalleryViewModel.SelectedCategoryId) : nameof(GalleryViewModel.SelectedTagId)));
            FillOptions();
            combo.SelectionChanged += (_, _) =>
            {
                if (synchronizing || combo.SelectedIndex < 0 || combo.SelectedIndex > options.Count) return;
                var selectedId = combo.SelectedIndex == 0 ? null : options[combo.SelectedIndex - 1].Id;
                if (isCategory) _vm.SelectedCategoryId = selectedId;
                else _vm.SelectedTagId = selectedId;
            };
            return combo;
        }

        var category = FacetFilter(isCategory: true);
        var tag = FacetFilter(isCategory: false);

        var favorites = new ToggleButton
        {
            Content = new IconView("icon.profile.favorite", 14, "danger"),
            Width = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            IsChecked = _vm.FavoritesOnly,
        };
        favorites.Click += (_, _) => _vm.FavoritesOnly = favorites.IsChecked == true;
        favorites.Tip(UI.T("Gallery.Favorites", "Favorites"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(favorites, UI.T("Gallery.Favorites", "Favorites"));
        _headerBag.Add(Observe.Props(_vm, () =>
        {
            favorites.IsChecked = _vm.FavoritesOnly;
            favorites.Background = ThemeRuntime.Current.Brush(_vm.FavoritesOnly ? "surfaceSelected" : "transparent");
            favorites.BorderBrush = ThemeRuntime.Current.Brush(_vm.FavoritesOnly ? "borderInteractive" : "borderSubtle");
        }, nameof(GalleryViewModel.FavoritesOnly)));

        var sort = new ComboBox { ItemsSource = _vm.SortOptions.Select(o => o.DisplayName).ToList(), Width = 148 };
        sort.Tip(UI.T("Gallery.Sort", "Sort"));
        sort.SelectedIndex = Math.Max(0, _vm.SortOptions.ToList().FindIndex(o => o.Order == _vm.SortOrder));
        sort.SelectionChanged += (_, _) =>
        {
            if (sort.SelectedIndex >= 0)
            {
                _vm.SortOrder = _vm.SortOptions[sort.SelectedIndex].Order;
            }
        };
        _headerBag.Add(Observe.Props(_vm, () =>
        {
            var selected = _vm.SortOptions.ToList().FindIndex(option => option.Order == _vm.SortOrder);
            sort.SelectedIndex = Math.Max(0, selected);
        }, nameof(GalleryViewModel.SortOrder)));

        ComboBox RatingFilter(Func<int?> read, Action<int?> write, string propertyName)
        {
            var combo = new ComboBox
            {
                ItemsSource = new[] { UI.T("Common.Any", "Any"), "1", "2", "3", "4", "5" },
                SelectedIndex = read() ?? 0,
                MinWidth = 74,
            };
            var synchronizing = false;
            combo.SelectionChanged += (_, _) =>
            {
                if (!synchronizing)
                {
                    write(combo.SelectedIndex <= 0 ? null : combo.SelectedIndex);
                }
            };
            _headerBag.Add(Observe.Props(_vm, () =>
            {
                synchronizing = true;
                combo.SelectedIndex = read() ?? 0;
                synchronizing = false;
            }, propertyName));
            return combo;
        }

        ToggleButton FilterToggle(string label, Func<bool> read, Action<bool> write, string propertyName)
        {
            var toggle = new ToggleButton { Content = label, IsChecked = read() };
            toggle.Click += (_, _) => write(toggle.IsChecked == true);
            _headerBag.Add(Observe.Props(_vm, () => toggle.IsChecked = read(), propertyName));
            return toggle;
        }

        FrameworkElement Field(string label, FrameworkElement control) =>
            UI.V(4, UI.Text(label, "micro", "textMuted"), control is Control native ? UI.DecoratedControl(native) : control);

        var minRating = RatingFilter(() => _vm.MinRating, value => _vm.MinRating = value, nameof(GalleryViewModel.MinRating));
        var maxRating = RatingFilter(() => _vm.MaxRating, value => _vm.MaxRating = value, nameof(GalleryViewModel.MaxRating));
        var images = FilterToggle(UI.T("Gallery.HasImages", "Images"), () => _vm.HasImages, value => _vm.HasImages = value, nameof(GalleryViewModel.HasImages));
        var videos = FilterToggle(UI.T("Gallery.HasVideos", "Videos"), () => _vm.HasVideos, value => _vm.HasVideos = value, nameof(GalleryViewModel.HasVideos));
        var models = FilterToggle(UI.T("Gallery.HasModels", "3D models"), () => _vm.HasModels, value => _vm.HasModels = value, nameof(GalleryViewModel.HasModels));
        var shared = FilterToggle(UI.T("Gallery.HasSharedMedia", "Shared media"), () => _vm.HasSharedMedia, value => _vm.HasSharedMedia = value, nameof(GalleryViewModel.HasSharedMedia));

        var profileKind = new ComboBox
        {
            ItemsSource = _vm.ProfileKindOptions.Select(option => option.DisplayName).ToList(),
            MinWidth = 132,
        };
        profileKind.SelectedIndex = Math.Max(0, _vm.ProfileKindOptions.ToList().FindIndex(option => option.Kind == _vm.ProfileKindFilter));
        var synchronizingProfileKind = false;
        profileKind.SelectionChanged += (_, _) =>
        {
            if (!synchronizingProfileKind && profileKind.SelectedIndex >= 0)
            {
                _vm.ProfileKindFilter = _vm.ProfileKindOptions[profileKind.SelectedIndex].Kind;
            }
        };
        _headerBag.Add(Observe.Props(_vm, () =>
        {
            synchronizingProfileKind = true;
            profileKind.SelectedIndex = Math.Max(0, _vm.ProfileKindOptions.ToList().FindIndex(option => option.Kind == _vm.ProfileKindFilter));
            synchronizingProfileKind = false;
        }, nameof(GalleryViewModel.ProfileKindFilter)));

        var related = UI.Button(
            _vm.RelatedToProfileButtonLabel,
            null,
            ButtonKind.Secondary,
            "icon.action.search",
            _vm.PickRelatedToProfileCommand);
        _headerBag.Add(Observe.Props(
            _vm,
            () => related.Content = _vm.RelatedToProfileButtonLabel,
            nameof(GalleryViewModel.RelatedToProfileButtonLabel),
            nameof(GalleryViewModel.RelatedToProfileName),
            nameof(GalleryViewModel.HasRelatedToProfileFilter)));

        var filterFlyout = new Flyout();
        var clear = UI.IconButton(
            "icon.action.close",
            UI.T("Gallery.ClearFilters", "Clear filters"),
            () => _vm.ClearFilters());
        var activeFilters = UI.Wrap(4);
        var activeSection = UI.V(5,
            UI.Text(UI.T("Gallery.MoreFilters", "More filters"), "micro", "textMuted"),
            activeFilters);
        var filterCount = UI.Badge("0", "accent");
        FrameworkElement? moreFiltersAnchor = null;
        var moreFilters = UI.Button(UI.T("Gallery.MoreFilters", "More filters"), () => filterFlyout.ShowAt(moreFiltersAnchor!), ButtonKind.Ghost);
        moreFiltersAnchor = moreFilters;
        moreFilters.Content = UI.H(5,
            new IconView("icon.action.more", 14, "textSecondary"),
            UI.Text(UI.T("Gallery.MoreFilters", "More filters"), "control"),
            filterCount);
        var filterFields = UI.V(12,
            UI.Grid("auto", "*,auto",
                UI.Text(UI.T("Gallery.MoreFilters", "More filters"), "section-title").At(0, 0),
                UI.IconButton("icon.action.close", UI.T("Common.Close", "Close"), () => filterFlyout.Hide()).At(0, 1)),
            activeSection,
            new ResponsiveForm(140, 8,
                Field(UI.T("Gallery.Category", "Category"), category),
                Field(UI.T("Gallery.Tag", "Tag"), tag)),
            UI.Divider(),
            Field(UI.T("Gallery.Filters.Profiles", "Profiles"), profileKind),
            related,
            new ResponsiveForm(100, 8,
                Field(UI.T("Gallery.MinRating", "Min rating"), minRating),
                Field(UI.T("Gallery.MaxRating", "Max rating"), maxRating)),
            UI.Divider(),
            UI.V(5, UI.Text(UI.T("Gallery.Filters.Media", "Media"), "micro", "textMuted"),
                UI.Wrap(5, images, videos, models, shared)));
        filterFields.Width = 360;
        var filterScroll = UI.Scroll(filterFields);
        filterScroll.MaxHeight = 440;
        filterFlyout.Content = filterScroll;
        filterFlyout.Opened += (_, _) =>
        {
            var width = _root.ActualWidth > 0 ? _root.ActualWidth : 720;
            var height = _root.ActualHeight > 0 ? _root.ActualHeight : 600;
            filterFields.Width = Math.Min(360, Math.Max(160, width - 56));
            filterScroll.MaxHeight = Math.Min(440, Math.Max(160, height - 64));
        };
        related.Click += (_, _) => filterFlyout.Hide();
        void UpdateActiveFilters()
        {
            activeFilters.Children.Clear();
            foreach (var filter in _vm.ActiveFilters)
            {
                var kind = filter.Kind;
                var chip = UI.Chip(filter.DisplayText + " ×", true, () => _vm.RemoveFilterCommand.Execute(kind));
                chip.MaxWidth = 320;
                chip.Tip(filter.DisplayText);
                activeFilters.Children.Add(chip);
            }
            var count = _vm.ActiveFilters.Count;
            activeSection.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            filterCount.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            filterCount.Child = UI.Text(count.ToString(System.Globalization.CultureInfo.CurrentCulture), "badge", "textOnAccent");
            clear.Visibility = count == 0 && !_vm.HasSearchText ? Visibility.Collapsed : Visibility.Visible;
            moreFilters.Tip(count == 0
                ? UI.T("Gallery.MoreFilters", "More filters")
                : string.Join(" · ", _vm.ActiveFilters.Select(filter => filter.DisplayText)));
        }
        _headerBag.Add(Observe.Collection(_vm.ActiveFilters, UpdateActiveFilters));
        _headerBag.Add(Observe.Props(_vm, UpdateActiveFilters, nameof(GalleryViewModel.HasSearchText)));
        var titleRow = UI.H(8,
            UI.Text(UI.T("Nav.Gallery", "Gallery"), "page-title"),
            _count.Align(vertical: VerticalAlignment.Center));
        var searchRow = UI.Grid("auto", "auto,*",
            new IconView("icon.action.search", 14, "textMuted").Align(vertical: VerticalAlignment.Center).Margin(0, 0, 6, 0).At(0, 0),
            UI.DecoratedControl(search).At(0, 1));
        searchRow.Width = 240;
        search.Width = double.NaN;
        search.MinWidth = 0;
        FrameworkElement toolbar;
        if (_layout.Toolbar is "feed" or "catalog")
        {
            favorites.Content = UI.T("Gallery.Favorites", "Favorites");
            favorites.Width = double.NaN;
            favorites.Padding = new Thickness(8, 4, 8, 4);
            searchRow.Width = double.NaN;
            searchRow.MaxWidth = 620;
            searchRow.HorizontalAlignment = HorizontalAlignment.Stretch;
            var inlineCategory = UI.DecoratedControl(FacetFilter(isCategory: true));
            var inlineTag = UI.DecoratedControl(FacetFilter(isCategory: false));
            SurfaceComposer.Detach(activeSection);
            toolbar = _layout.Toolbar == "feed"
                ? UI.V(8, UI.Wrap(8, titleRow, favorites, moreFilters, clear), searchRow, UI.Wrap(8, inlineCategory, inlineTag, sort), activeSection)
                : UI.V(6, UI.Wrap(8, titleRow, searchRow, sort, moreFilters, clear), UI.Wrap(8, inlineCategory, inlineTag, favorites), activeSection);
        }
        else
        {
            if (_layout.Toolbar == "drawer") searchRow.Width = 180;
            toolbar = UI.Wrap(8, titleRow, searchRow, sort, favorites, moreFilters, clear);
        }
        UpdateActiveFilters();
        var header = UI.Surface(UI.V(6, toolbar, _notice), Material.Grounded, 0, 0);
        header.BorderBrush = ThemeRuntime.Current.Brush("borderSubtle");
        header.BorderThickness = new Thickness(0, 0, 0, 1);
        header.Padding = new Thickness(14, 8, 14, 8);
        _notice.Visibility = string.IsNullOrWhiteSpace(_notice.Text) ? Visibility.Collapsed : Visibility.Visible;
        return header;
    }

    private FrameworkElement Pagination()
    {
        var pager = new PaginationBar(
            _vm,
            () => _vm.RangeText,
            () => _vm.PageIndex,
            () => _vm.TotalPages,
            GalleryViewModel.PageSizeOptions,
            () => _vm.PageSize,
            size => _vm.SetPageSizeCommand.Execute(size),
            page => _ = _vm.GoToPageAsync(page));
        Bag.Add(pager);
        return pager.View.Margin(14, 4, 14, 6);
    }

    private void UpdateState()
    {
        _count.Text = _vm.ResultCountText;
        var notice = _vm.ErrorNotice ?? (_vm.HasError ? _vm.ErrorMessage : null);
        _notice.Text = notice ?? string.Empty;
        _notice.Visibility = string.IsNullOrWhiteSpace(notice) ? Visibility.Collapsed : Visibility.Visible;
        _paginationHost.Visibility = !_vm.IsEmptyLibrary && !_vm.IsNoResults
            ? Visibility.Visible
            : Visibility.Collapsed;
        _empty.Children.Clear();
        if (_vm.IsEmptyLibrary || _vm.IsNoResults)
        {
            _empty.Visibility = Visibility.Visible;
            _empty.Children.Add(UI.V(12,
                new IconView("icon.navigation.gallery", 40, "textMuted").Align(HorizontalAlignment.Center),
                UI.Text(_vm.IsEmptyLibrary ? UI.T("Gallery.Empty", "Your collection is empty.") : _vm.NoResultsText, "section-title").Align(HorizontalAlignment.Center),
                _vm.IsEmptyLibrary
                    ? UI.Button(UI.T("Home.Empty.Import", "Import media"), () => Services.Navigation.ResetToTopLevel(new ImportRoute()), ButtonKind.Primary).Align(HorizontalAlignment.Center)
                    : UI.Button(UI.T("Gallery.ClearFilters", "Clear filters"), () => _vm.ClearFilters()).Align(HorizontalAlignment.Center)).Align(HorizontalAlignment.Center, VerticalAlignment.Center));
        }
        else
        {
            _empty.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------------------------------------------------------- cards

    private CompiledDefinition CardFor(GalleryCardViewModel card)
    {
        if (_cardByProfile.TryGetValue(card.ProfileId, out var cached))
        {
            return cached;
        }

        var state = new ProfilePresentationState(card.ProfileId, 0, null, card.Appearance);
        var context = new PresentationContext("gallery", card.ProfileId, null, state);
        var definition = ResolvePresentation(PresentationSlots.ProfileCard, context);
        _cardByProfile[card.ProfileId] = definition;
        return definition;
    }

    private string KindOf(object? data) => data is GalleryCardViewModel card
        ? $"{_presentationGeneration}:{CardFor(card).Ref}"
        : $"{_presentationGeneration}:empty";

    private FrameworkElement Create(string kind, object? data)
    {
        var definition = data is GalleryCardViewModel card ? CardFor(card) : _defaultCard;
        var host = new Grid();
        var visual = new CardVisual(definition)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        visual.AddCustomizationAction(_ =>
        {
            if (host.DataContext is GalleryCardViewModel item)
            {
                HoverVideoCoordinator.Shared.StopAll();
                var state = new ProfilePresentationState(item.ProfileId, 0, null, item.Appearance);
                Services.OpenCustomization(CustomizationCategories.Profile,
                    PresentationContext.ForProfile(state, "gallery"), PresentationSlots.ProfileCard);
            }
        });
        host.Children.Add(visual);
        host.SizeChanged += (_, _) => FitCardVisual(host, visual);
        UpdateHostGeometry(host, visual);

        host.Tapped += (_, _) =>
        {
            if (host.DataContext is GalleryCardViewModel item)
            {
                HoverVideoCoordinator.Shared.StopAll();
                _vm.SelectProfile(item.ProfileId);
                _vm.OpenProfile(item.ProfileId, CaptureOrigin());
            }
        };
        host.PointerEntered += (_, _) =>
        {
            if (host.DataContext is GalleryCardViewModel { HasBannerHover: true } item
                && (visual.Plan.UsesBanner || visual.Plan.Hover is "spotlight" or "banner")
                && visual.BannerMediaHost is { } mediaHost)
            {
                HoverVideoCoordinator.Shared.Play(mediaHost, item.BannerHoverPath, visual.Data?.BannerTransform);
            }
        };
        host.PointerExited += (_, _) =>
        {
            if (visual.BannerMediaHost is { } mediaHost)
            {
                HoverVideoCoordinator.Shared.Release(mediaHost);
            }
        };
        return host;
    }

    private void Bind(FrameworkElement element, object? data)
    {
        if (data is not GalleryCardViewModel card || element is not Grid host || host.Children[0] is not CardVisual visual)
        {
            return;
        }

        if (_cardObservers.Remove(host, out var previousObserver))
        {
            previousObserver.Dispose();
        }
        host.DataContext = card;
        UpdateHostGeometry(host, visual);
        Services.ProfileSnapshots.Warm(card.ProfileId);
        void UpdateCard() => ApplyCardData(visual, card);
        _cardObservers[host] = Observe.Props(
            card,
            UpdateCard,
            nameof(GalleryCardViewModel.IsSelected),
            nameof(GalleryCardViewModel.DisplayName),
            nameof(GalleryCardViewModel.CategoryName),
            nameof(GalleryCardViewModel.Tags),
            nameof(GalleryCardViewModel.Rating),
            nameof(GalleryCardViewModel.IsFavorite),
            nameof(GalleryCardViewModel.MediaCount),
            nameof(GalleryCardViewModel.RelatedProfileCount),
            nameof(GalleryCardViewModel.Appearance),
            nameof(GalleryCardViewModel.CoverAppearance),
            nameof(GalleryCardViewModel.CoverSource),
            nameof(GalleryCardViewModel.BannerImageSource),
            nameof(GalleryCardViewModel.BannerHoverPath));
        UpdateCard();
    }

    private void ApplyCardData(CardVisual visual, GalleryCardViewModel card)
    {
        var plan = visual.Plan;
        var coverWidth = (int)Math.Clamp(_layout.ItemMinWidth * 1.5, 160, 720);
        var data = CardDataFactory.From(card, Services.Presentation, coverWidth, plan.UsesBanner ? coverWidth * 2 : coverWidth);
        visual.Bind(data);
        visual.IsSelected = card.IsSelected;
    }

    private void UpdateHostGeometry(Grid host, CardVisual visual)
    {
        host.Width = double.NaN;
        host.Height = double.NaN;
        host.HorizontalAlignment = HorizontalAlignment.Stretch;

        var aspect = Math.Max(0.3, visual.Plan.Aspect);
        if (_layout.Primitive == CollectionPrimitives.SingleColumn)
        {
            var viewportWidth = Math.Max(320, _field.ActualWidth > 0 ? _field.ActualWidth : _root.ActualWidth);
            var viewportHeight = Math.Max(320, _field.ActualHeight > 0 ? _field.ActualHeight : 720);
            var availableWidth = Math.Max(180, viewportWidth - (2 * UI.PagePadding(viewportWidth)) - 8);
            var minWidth = Math.Min(_layout.ItemMinWidth, availableWidth);
            var maxHeight = Math.Min(520, Math.Max(280, viewportHeight * 0.72));
            host.Width = Math.Min(availableWidth, Math.Max(minWidth, Math.Min(_layout.ItemMaxWidth, maxHeight * aspect)));
            host.Height = host.Width / aspect;
            host.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else if (_layout.Primitive == CollectionPrimitives.List)
        {
            host.Height = _layout.RowHeight;
        }
        else if (_layout.Primitive == CollectionPrimitives.Carousel)
        {
            var viewportWidth = Math.Max(180, _field.ActualWidth > 0 ? _field.ActualWidth : _root.ActualWidth);
            var viewportHeight = Math.Max(240, _field.ActualHeight > 0 ? _field.ActualHeight : 540);
            host.Width = Math.Min(_layout.ItemMaxWidth, Math.Max(120, viewportWidth * 0.6));
            host.Height = Math.Min(Math.Max(120, _layout.RowHeight), Math.Max(160, viewportHeight * 0.52));
        }

        FitCardVisual(host, visual);
    }

    private void FitCardVisual(Grid host, CardVisual visual)
    {
        var width = host.ActualWidth;
        var height = host.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_layout.HeightPattern is not null)
        {
            visual.Width = width;
            visual.Height = height;
            return;
        }

        var aspect = Math.Max(0.3, visual.Plan.Aspect);
        if (width / height > aspect)
        {
            visual.Height = height;
            visual.Width = height * aspect;
        }
        else
        {
            visual.Width = width;
            visual.Height = width / aspect;
        }
    }

    public override void Dispose()
    {
        foreach (var observer in _cardObservers.Values.ToList())
        {
            observer.Dispose();
        }
        _cardObservers.Clear();
        HoverVideoCoordinator.Shared.StopAll();
        _factory.Clear();
        base.Dispose();
    }
    private void Recycle(FrameworkElement element)
    {
        if (element is not Grid host)
        {
            return;
        }

        if (host.Children.FirstOrDefault() is CardVisual { BannerMediaHost: { } mediaHost })
        {
            HoverVideoCoordinator.Shared.Release(mediaHost);
        }
        if (_cardObservers.Remove(host, out var observer))
        {
            observer.Dispose();
        }
        host.DataContext = null;
    }
}

/// <summary>A simple wrapping row for toolbars (few, fixed children — not for collections).</summary>
public sealed class VariableWrap : Panel
{
    private readonly double _spacing;

    public VerticalAlignment LineVerticalAlignment { get; set; } = VerticalAlignment.Center;

    public VariableWrap(double spacing, params UIElement[] children)
    {
        _spacing = spacing;
        foreach (var child in children)
        {
            Children.Add(child);
        }
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        double x = 0, y = 0, row = 0, width = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Windows.Foundation.Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            var childWidth = Math.Min(size.Width, availableSize.Width);
            var gap = x > 0 ? _spacing : 0;
            if (x > 0 && x + gap + childWidth > availableSize.Width)
            {
                x = 0;
                y += row + _spacing;
                row = 0;
                gap = 0;
            }
            x += gap + childWidth;
            row = Math.Max(row, size.Height);
            width = Math.Max(width, x);
        }
        // Intrinsic width lets nested filter groups share a row instead of each taking the viewport.
        return new Windows.Foundation.Size(width, y + row);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        double x = 0, y = 0, row = 0;
        var line = new List<(UIElement Child, double X, double Width, double Height)>();
        void ArrangeLine()
        {
            foreach (var item in line)
            {
                var offset = LineVerticalAlignment switch
                {
                    VerticalAlignment.Bottom => row - item.Height,
                    VerticalAlignment.Center => (row - item.Height) / 2,
                    _ => 0,
                };
                item.Child.Arrange(new Windows.Foundation.Rect(item.X, y + offset, item.Width, item.Height));
            }
            line.Clear();
        }
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            var childWidth = Math.Min(size.Width, finalSize.Width);
            var gap = x > 0 ? _spacing : 0;
            if (x > 0 && x + gap + childWidth > finalSize.Width)
            {
                ArrangeLine();
                x = 0;
                y += row + _spacing;
                row = 0;
                gap = 0;
            }
            x += gap;
            line.Add((child, x, childWidth, size.Height));
            x += childWidth;
            row = Math.Max(row, size.Height);
        }
        ArrangeLine();
        return finalSize;
    }
}
