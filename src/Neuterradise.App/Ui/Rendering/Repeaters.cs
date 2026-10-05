using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ElementFactoryGetArgs = Microsoft.UI.Xaml.Controls.ElementFactoryGetArgs;
using ElementFactoryRecycleArgs = Microsoft.UI.Xaml.Controls.ElementFactoryRecycleArgs;
using Layout = Microsoft.UI.Xaml.Controls.Layout;

namespace Neuterradise.App.Ui;

/// <summary>
/// Element factory for <see cref="ItemsRepeater"/>: only visible items plus a small buffer are realized,
/// elements are pooled per template kind and rebound on reuse, never rebuilt per data change.
///
/// Uno's ItemsRepeater accepts exactly three ItemTemplate shapes: <see cref="DataTemplate"/>,
/// <see cref="DataTemplateSelector"/>, or an <c>IElementFactoryShim</c>. The public way to supply the
/// last one is to derive from <see cref="ElementFactory"/> and override its Core methods. Implementing
/// <c>Microsoft.UI.Xaml.IElementFactory</c> directly compiles but is rejected at runtime with
/// <c>ArgumentException("ItemTemplate")</c>.
/// </summary>
public sealed class PooledElementFactory : ElementFactory
{
    private const int MaxPooledPerKind = 48;

    private readonly Func<object?, string> _kindOf;
    private readonly Func<string, object?, FrameworkElement> _create;
    private readonly Action<FrameworkElement, object?> _bind;
    private readonly Action<FrameworkElement>? _recycle;
    private readonly Dictionary<string, Stack<FrameworkElement>> _pools = new(StringComparer.Ordinal);

    public PooledElementFactory(
        Func<object?, string> kindOf,
        Func<string, object?, FrameworkElement> create,
        Action<FrameworkElement, object?> bind,
        Action<FrameworkElement>? recycle = null)
    {
        _kindOf = kindOf;
        _create = create;
        _bind = bind;
        _recycle = recycle;
    }

    protected override UIElement GetElementCore(ElementFactoryGetArgs args)
    {
        var data = args.Data;
        var kind = _kindOf(data);
        FrameworkElement element;
        if (_pools.TryGetValue(kind, out var pool) && pool.Count > 0)
        {
            element = pool.Pop();
        }
        else
        {
            element = _create(kind, data);
            element.Tag = kind;
        }

        _bind(element, data);
        return element;
    }

    protected override void RecycleElementCore(ElementFactoryRecycleArgs args)
    {
        if (args.Element is not FrameworkElement element || element.Tag is not string kind)
        {
            return;
        }

        _recycle?.Invoke(element);
        if (!_pools.TryGetValue(kind, out var pool))
        {
            pool = new Stack<FrameworkElement>();
            _pools[kind] = pool;
        }

        if (pool.Count < MaxPooledPerKind)
        {
            pool.Push(element);
        }
    }

    /// <summary>Drops pooled elements (after a card plan or theme-structural change).</summary>
    public void Clear() => _pools.Clear();
}

public static class Repeaters
{
    /// <summary>
    /// A virtualized repeater inside its scroller (virtualization requires the scroll host). The repeater
    /// is named after its creating surface member so a rejected template or layout failure names the exact
    /// repeater in startup diagnostics instead of a bare ItemTemplate exception.
    /// </summary>
    public static (ScrollViewer Scroll, ItemsRepeater Repeater) Virtualized(
        ElementFactory factory,
        Layout layout,
        bool horizontal = false,
        [CallerFilePath] string callerFile = "",
        [CallerMemberName] string callerMember = "")
    {
        var identity = $"{Path.GetFileNameWithoutExtension(callerFile)}.{callerMember}";
        var repeater = new ItemsRepeater { Name = identity, Layout = layout, VerticalCacheLength = 1, HorizontalCacheLength = 1 };
        try
        {
            repeater.ItemTemplate = factory;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"ItemsRepeater '{identity}' rejected its ItemTemplate ({factory.GetType().FullName}).", exception);
        }

        var scroll = UI.Scroll(repeater, horizontal);
        return (scroll, repeater);
    }

    public static UniformGridLayout Grid(double minWidth, double minHeight, double spacing, int maxColumns = 12) => new()
    {
        MinItemWidth = minWidth,
        MinItemHeight = minHeight,
        MinColumnSpacing = spacing,
        MinRowSpacing = spacing,
        ItemsStretch = UniformGridLayoutItemsStretch.Uniform,
        MaximumRowsOrColumns = maxColumns,
    };

    public static StackLayout Stack(bool horizontal, double spacing) => new()
    {
        Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical,
        Spacing = spacing,
    };
}
