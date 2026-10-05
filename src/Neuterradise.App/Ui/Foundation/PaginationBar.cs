using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Neuterradise.App.Ui;

/// <summary>A shared, surface-centred pager for Gallery and Profile Media.</summary>
public sealed class PaginationBar : IDisposable
{
    private readonly INotifyPropertyChanged _source;
    private readonly Func<string> _range;
    private readonly Func<int> _page;
    private readonly Func<int> _pages;
    private readonly Action<int> _goToPage;
    private readonly StackPanel _navigation = UI.H(4);
    private readonly TextBlock _rangeText = UI.Text(string.Empty, "metadata");
    private readonly ComboBox _pageSize;
    private readonly Border _balancer = new();
    private bool _syncing;
    private bool _compact;

    public PaginationBar(
        INotifyPropertyChanged source,
        Func<string> range,
        Func<int> page,
        Func<int> pages,
        IReadOnlyList<int> pageSizes,
        Func<int> pageSize,
        Action<int> setPageSize,
        Action<int> goToPage)
    {
        _source = source;
        _range = range;
        _page = page;
        _pages = pages;
        _goToPage = goToPage;
        _pageSize = new ComboBox
        {
            ItemsSource = pageSizes,
            SelectedItem = pageSize(),
            Width = 64,
            MinHeight = 28,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pageSize.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _pageSize.SelectedItem is int value)
            {
                setPageSize(value);
            }
        };

        var left = UI.H(
            10,
            _rangeText.Align(vertical: VerticalAlignment.Center),
            UI.Text(UI.T("Pager.PageSize", "Per page"), "caption", "textMuted").Align(vertical: VerticalAlignment.Center),
            _pageSize);
        left.VerticalAlignment = VerticalAlignment.Center;
        _navigation.VerticalAlignment = VerticalAlignment.Center;
        left.SizeChanged += (_, _) => _balancer.Width = left.ActualWidth;
        View = UI.Grid("auto,auto", "*,auto,*",
            left.At(0, 0),
            _navigation.At(0, 1),
            _balancer.Align(HorizontalAlignment.Right, VerticalAlignment.Center).At(0, 2));
        View.SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 620;
            left.At(0, 0, columnSpan: narrow ? 3 : 1);
            _navigation.At(narrow ? 1 : 0, narrow ? 0 : 1, columnSpan: narrow ? 3 : 1);
            _navigation.HorizontalAlignment = HorizontalAlignment.Center;
            _balancer.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            var compact = args.NewSize.Width < 420;
            if (_compact != compact)
            {
                _compact = compact;
                Refresh();
            }
        };
        _source.PropertyChanged += OnChanged;
        Refresh();
    }

    public FrameworkElement View { get; }

    private void OnChanged(object? sender, PropertyChangedEventArgs args) => UiDispatch.Run(Refresh);

    private void Refresh()
    {
        _syncing = true;
        _rangeText.Text = _range();
        _syncing = false;
        _navigation.Children.Clear();

        var current = Math.Clamp(_page(), 1, Math.Max(1, _pages()));
        var total = Math.Max(1, _pages());
        _navigation.Children.Add(PageButton("«", UI.T("Pager.First", "First"), 1, current > 1));
        _navigation.Children.Add(PageButton("‹", UI.T("Pager.Previous", "Previous"), current - 1, current > 1));

        foreach (var value in (_compact ? new[] { current } : VisiblePages(current, total)))
        {
            if (value == 0)
            {
                var ellipsis = UI.Text("…", "control");
                ellipsis.Width = 28;
                ellipsis.TextAlignment = TextAlignment.Center;
                ellipsis.HorizontalAlignment = HorizontalAlignment.Center;
                ellipsis.VerticalAlignment = VerticalAlignment.Center;
                _navigation.Children.Add(ellipsis);
                continue;
            }

            if (value == current)
            {
                var selectedText = UI.Text(
                    current.ToString(System.Globalization.CultureInfo.CurrentCulture),
                    "control",
                    "textOnAccent");
                selectedText.TextAlignment = TextAlignment.Center;
                selectedText.HorizontalAlignment = HorizontalAlignment.Center;
                selectedText.VerticalAlignment = VerticalAlignment.Center;
                _navigation.Children.Add(new Border
                {
                    Child = selectedText,
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(8),
                    Background = ThemeRuntime.Current.Brush("accent"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            else
            {
                _navigation.Children.Add(PageButton(
                    value.ToString(System.Globalization.CultureInfo.CurrentCulture),
                    UI.F("Pager.GoTo", "Go to page {0}", value),
                    value,
                    true));
            }
        }

        _navigation.Children.Add(PageButton("›", UI.T("Pager.Next", "Next"), current + 1, current < total));
        _navigation.Children.Add(PageButton("»", UI.T("Pager.Last", "Last"), total, current < total));
    }
    private Button PageButton(string text, string tooltip, int page, bool enabled)
    {
        var button = UI.Button(text, () => _goToPage(page), ButtonKind.Ghost);
        button.IsEnabled = enabled;
        button.Width = 28;
        button.MinWidth = 28;
        button.Height = 28;
        button.MinHeight = 28;
        button.Padding = new Thickness(0);
        button.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private static IReadOnlyList<int> VisiblePages(int current, int total)
    {
        if (total <= 7)
        {
            return Enumerable.Range(1, total).ToArray();
        }

        var values = new List<int> { 1 };
        var start = Math.Max(2, current - 1);
        var end = Math.Min(total - 1, current + 1);
        if (start > 2) values.Add(0);
        for (var page = start; page <= end; page++) values.Add(page);
        if (end < total - 1) values.Add(0);
        values.Add(total);
        return values;
    }

    public void Dispose() => _source.PropertyChanged -= OnChanged;
}
