using Microsoft.UI.Xaml;
using ToggleButton = Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Design.MediaLayouts;
using Neuterradise.App.Media;
using Neuterradise.App.Presentation;
using Neuterradise.App.Shell;

namespace Neuterradise.App.Ui;

public static class MediaGridViewportPolicy
{
    public static double Calculate(double cardHeight, double availableHeight, bool horizontal)
    {
        cardHeight = double.IsFinite(cardHeight) && cardHeight > 0 ? cardHeight : 160;
        availableHeight = double.IsFinite(availableHeight) && availableHeight > 0 ? availableHeight : 600;
        if (horizontal)
        {
            return Math.Clamp(cardHeight + 16, 180, Math.Min(420, Math.Max(180, availableHeight * 0.55)));
        }

        var rows = availableHeight switch
        {
            < 700 => 2,
            < 900 => 3,
            < 1200 => 4,
            _ => 5,
        };
        const double spacing = 10;
        var desired = cardHeight * rows + spacing * (rows - 1);
        var twoRowBaseline = Math.Clamp(cardHeight * 2 + spacing, 240, 340);
        var upper = Math.Clamp(availableHeight * 0.68, twoRowBaseline, 760);
        return Math.Clamp(desired, Math.Min(twoRowBaseline, upper), Math.Max(twoRowBaseline, upper));
    }
}

public sealed class MediaGridPanel : IDisposable
{
    private readonly AppServices _services;
    private readonly MediaGridViewModel _vm;
    private readonly Func<PresentationContext> _context;
    private readonly Func<string, PresentationContext, CompiledDefinition> _resolve;
    private readonly Grid _root = UI.Grid("auto,*,auto", "*");
    private readonly ItemsRepeater _repeater;
    private readonly ScrollViewer _scroll;
    private readonly PooledElementFactory _factory;
    private readonly TextBlock _state = UI.WrappedText(string.Empty, "body-muted");
    private readonly Border _stateHost = new();
    private readonly Disposables _bag = new();
    private readonly HashSet<MediaCardView> _views = [];
    private MediaLayoutDefinition _layout;
    private string _browse = "wall";

    public MediaGridPanel(AppServices services, MediaGridViewModel vm, Func<PresentationContext> context, Func<string, PresentationContext, CompiledDefinition>? resolve = null)
    {
        _services = services;
        _vm = vm;
        _context = context;
        _resolve = resolve ?? ((slot, current) => _services.Presentation.ResolveCompiled(slot, current));
        _layout = ResolveLayout();
        _factory = new PooledElementFactory(KindOf, Create, Bind, Recycle);
        var (scroll, repeater) = Repeaters.Virtualized(
            _factory,
            Repeaters.Grid(_layout.CardWidth, _layout.CardHeight, 10));
        _repeater = repeater;
        _scroll = scroll;
        ApplyBrowse();
        _scroll.Height = ViewportHeight();

        var stage = new Grid();
        stage.Children.Add(scroll);
        _stateHost.Child = UI.V(8,
            new IconView("icon.media.image", 28, "textMuted").Align(HorizontalAlignment.Center),
            _state.Align(HorizontalAlignment.Center));
        _stateHost.Padding = new Thickness(16);
        _stateHost.CornerRadius = new CornerRadius(10);
        _stateHost.Background = ThemeRuntime.Current.Brush("surface1");
        _stateHost.HorizontalAlignment = HorizontalAlignment.Center;
        _stateHost.VerticalAlignment = VerticalAlignment.Center;
        _stateHost.MaxWidth = 360;
        _stateHost.IsHitTestVisible = false;
        stage.Children.Add(_stateHost);
        _root.Children.Add(Toolbar().At(0));
        _root.Children.Add(stage.At(1));

        var pager = new PaginationBar(
            _vm,
            () => MediaSurfaceText.Range(_vm),
            () => _vm.CurrentPage,
            () => _vm.TotalPages,
            MediaGridViewModel.PageSizeOptions,
            () => _vm.PageSize,
            size => _vm.PageSize = size,
            _vm.GoToPage);
        _bag.Add(pager);
        _root.Children.Add(pager.View.Margin(0, 8, 0, 0).At(2));

        _repeater.ItemsSource = _vm.Cards;
        _bag.Add(Observe.Props(
            _vm,
            UpdateState,
            nameof(MediaGridViewModel.State),
            nameof(MediaGridViewModel.RangeText)));
        UpdateState();
        _root.SizeChanged += (_, _) =>
        {
            if (_browse == "workbench") RefreshLayout();
            else RefreshViewport();
        };
    }

    public FrameworkElement View => _root;

    /// <summary>
    /// Re-reads only this Profile's durable Media Layout. The card tree and media ItemsSource remain intact.
    /// </summary>
    public void RefreshLayout()
    {
        var previousBrowse = _browse;
        var next = ResolveLayout();
        if (next == _layout && previousBrowse == _browse)
        {
            RefreshViewport();
            return;
        }

        HoverVideoCoordinator.Shared.StopAll();
        _layout = next;
        ApplyBrowse();
        RefreshViewport();
        foreach (var view in _views)
        {
            view.ApplyLayout(_layout);
        }
    }

    private MediaLayoutDefinition ResolveLayout()
    {
        var plan = _resolve(PresentationSlots.ProfileMediaLayout, _context() with { Surface = "media" }).PlanAs<ProfileMediaLayoutPlan>();
        _browse = plan.Browse;
        return _browse == "workbench" && _root.ActualWidth > 0
            ? plan.Layout with { CardWidth = Math.Max(140, Math.Min(plan.Layout.CardWidth, _root.ActualWidth - 24)) }
            : plan.Layout;
    }

    private void ApplyBrowse()
    {
        _repeater.Layout = _browse switch
        {
            "workbench" => Repeaters.Stack(false, 8),
            "reel" => Repeaters.Stack(true, 10),
            _ => Repeaters.Grid(_layout.CardWidth, _layout.CardHeight, 10),
        };
        _scroll.HorizontalScrollMode = _browse == "reel" ? ScrollMode.Enabled : ScrollMode.Disabled;
        _scroll.HorizontalScrollBarVisibility = _browse == "reel" ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollMode = _browse == "reel" ? ScrollMode.Disabled : ScrollMode.Enabled;
    }

    private void RefreshViewport() => _scroll.Height = ViewportHeight();

    private double ViewportHeight()
    {
        var availableHeight = _root.XamlRoot?.Size.Height ?? 0;
        return MediaGridViewportPolicy.Calculate(
            _layout.CardHeight,
            availableHeight,
            _browse == "reel");
    }

    private FrameworkElement Toolbar()
    {
        var types = UI.Wrap(4);
        var typeButtons = new Dictionary<MediaTypeFilter, Button>();
        foreach (var value in Enum.GetValues<MediaTypeFilter>())
        {
            var choice = value;
            var button = UI.Button(MediaSurfaceText.TypeFilter(value), () => _vm.TypeFilter = choice, ButtonKind.Ghost);
            typeButtons.Add(value, button);
            types.Children.Add(button);
        }
        _bag.Add(Observe.Props(_vm, () =>
        {
            foreach (var (value, button) in typeButtons)
            {
                DetailLayout.Select(button, _vm.TypeFilter == value);
            }
        }, nameof(MediaGridViewModel.TypeFilter)));

        var relationValues = Enum.GetValues<MediaRelationFilter>();
        var relation = new ComboBox
        {
            ItemsSource = relationValues.Select(MediaSurfaceText.RelationFilter).ToList(),
            SelectedIndex = (int)_vm.RelationFilter,
        };
        var synchronizingRelation = false;
        relation.SelectionChanged += (_, _) =>
        {
            if (!synchronizingRelation && relation.SelectedIndex >= 0)
            {
                _vm.RelationFilter = (MediaRelationFilter)relation.SelectedIndex;
            }
        };
        _bag.Add(Observe.Props(_vm, () =>
        {
            synchronizingRelation = true;
            relation.SelectedIndex = (int)_vm.RelationFilter;
            synchronizingRelation = false;
        }, nameof(MediaGridViewModel.RelationFilter)));

        var sortValues = Enum.GetValues<MediaGridSort>();
        var sort = new ComboBox
        {
            ItemsSource = sortValues.Select(MediaSurfaceText.Sort).ToList(),
            SelectedIndex = (int)_vm.Sort,
        };
        var synchronizingSort = false;
        sort.SelectionChanged += (_, _) =>
        {
            if (!synchronizingSort && sort.SelectedIndex >= 0)
            {
                _vm.Sort = (MediaGridSort)sort.SelectedIndex;
            }
        };
        _bag.Add(Observe.Props(_vm, () =>
        {
            synchronizingSort = true;
            sort.SelectedIndex = (int)_vm.Sort;
            synchronizingSort = false;
        }, nameof(MediaGridViewModel.Sort)));

        var favorites = new ToggleButton
        {
            Content = UI.T("Gallery.Favorites", "Favorites"),
            IsChecked = _vm.IsFavoriteOnly,
        };
        favorites.Click += (_, _) => _vm.IsFavoriteOnly = favorites.IsChecked == true;
        _bag.Add(Observe.Props(
            _vm,
            () => favorites.IsChecked = _vm.IsFavoriteOnly,
            nameof(MediaGridViewModel.IsFavoriteOnly)));
        relation.MinWidth = 120;
        sort.MinWidth = 136;
        relation.Tip(UI.T("Inspector.Associations", "Associations"));
        sort.Tip(UI.T("Gallery.Sort", "Sort"));
        var filters = UI.Wrap(6, relation, sort, favorites);
        return UI.Wrap(8, types, filters).Margin(0, 0, 0, 10);
    }

    private void UpdateState()
    {
        _state.Text = _vm.State == MediaGridState.Ready
            ? string.Empty
            : MediaSurfaceText.State(_vm.State);
        _stateHost.Visibility = string.IsNullOrEmpty(_state.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static string KindOf(object? data) =>
        data is MediaGridCardViewModel card
            ? card.MediaType.ToString()
            : "empty";

    private FrameworkElement Create(string kind, object? data)
    {
        var view = new MediaCardView(
            _layout,
            card => _services.RunUserAction(
                _vm.ToggleFavoriteAsync(card),
                "ProfileSurface.ToggleFavoriteAsync",
                UI.T("Profile.FavoriteFailed", "The favorite state could not be saved.")),
            card => _vm.HandleCardPointerDown(card),
            card => _vm.HandleInspect(card));
        _views.Add(view);

        return view;
    }

    private void Bind(FrameworkElement element, object? data)
    {
        if (element is not MediaCardView view || data is not MediaGridCardViewModel card)
        {
            return;
        }

        view.ApplyLayout(_layout);
        view.Bind(card);
    }

    private static void Recycle(FrameworkElement element)
    {
        if (element is MediaCardView view)
        {
            view.Unbind();
        }
    }

    public void Dispose()
    {
        HoverVideoCoordinator.Shared.StopAll();
        foreach (var view in _views)
        {
            view.Dispose();
        }
        _views.Clear();
        _factory.Clear();
        _bag.Dispose();
    }
}
