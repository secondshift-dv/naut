using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Neuterradise.App.Ui;

/// <summary>Shortest-column packing with column-ordered viewport realization.</summary>
public sealed class MasonryLayout(double minimumWidth, double maximumWidth, int maximumColumns, double spacing,
    Func<int, double> aspectAt, double footerHeight = 0) : VirtualizingLayout
{
    private sealed class State
    {
        public double Width;
        public Rect[] Bounds = [];
        public List<int>[] Columns = [];
        public Size Extent;
        public List<(UIElement Element, Rect Bounds)> Realized = [];
    }

    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
    {
        context.LayoutState = null;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : minimumWidth;
        var state = context.LayoutState as State ?? new State();
        context.LayoutState = state;
        if (state.Width != width || state.Bounds.Length != context.ItemCount)
        {
            var capacity = Math.Clamp((int)((width + spacing) / (minimumWidth + spacing)), 1, Math.Max(1, maximumColumns));
            var columns = Math.Min(capacity, Math.Max(1, context.ItemCount));
            var itemWidth = Math.Min(maximumWidth, Math.Max(1, (width - spacing * (columns - 1)) / columns));
            var inset = Math.Max(0, (width - (columns * itemWidth + (columns - 1) * spacing)) / 2);
            var heights = new double[columns];
            state.Width = width;
            state.Bounds = new Rect[context.ItemCount];
            state.Columns = Enumerable.Range(0, columns).Select(_ => new List<int>()).ToArray();
            for (var index = 0; index < context.ItemCount; index++)
            {
                var column = Array.IndexOf(heights, heights.Min());
                var height = itemWidth / Math.Max(0.3, aspectAt(index)) + footerHeight;
                state.Bounds[index] = new Rect(inset + column * (itemWidth + spacing), heights[column], itemWidth, height);
                state.Columns[column].Add(index);
                heights[column] += height + spacing;
            }
            state.Extent = new Size(width, context.ItemCount == 0 ? 0 : heights.Max() - spacing);
        }

        state.Realized.Clear();
        var window = context.RealizationRect;
        var visible = new List<int>();
        foreach (var column in state.Columns)
        {
            var low = 0;
            var high = column.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (state.Bounds[column[middle]].Bottom < window.Top) low = middle + 1;
                else high = middle;
            }
            for (var item = low; item < column.Count; item++)
            {
                var index = column[item];
                if (state.Bounds[index].Top > window.Bottom) break;
                visible.Add(index);
            }
        }

        visible.Sort();
        foreach (var index in visible)
        {
            var bounds = state.Bounds[index];
            var element = context.GetOrCreateElementAt(index);
            element.Measure(new Size(bounds.Width, bounds.Height));
            state.Realized.Add((element, bounds));
        }
        return state.Extent;
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (context.LayoutState is State state)
            foreach (var (element, bounds) in state.Realized) element.Arrange(bounds);
        return finalSize;
    }
}
