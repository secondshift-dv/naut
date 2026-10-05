using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Neuterradise.App.Ui;

/// <summary>Column packing requests only items intersecting the realization window.</summary>
public sealed class MasonryLayout(double minimumWidth, double maximumWidth, int maximumColumns, double spacing,
    IReadOnlyList<double> aspects) : VirtualizingLayout
{
    private sealed class State
    {
        public double Width;
        public Rect[] Bounds = [];
        public Size Extent;
        public double MaximumHeight;
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
            var columns = Math.Clamp((int)((width + spacing) / (minimumWidth + spacing)), 1, maximumColumns);
            var itemWidth = Math.Min(maximumWidth, Math.Max(1, (width - spacing * (columns - 1)) / columns));
            var heights = new double[columns];
            state.Width = width;
            state.Bounds = new Rect[context.ItemCount];
            state.MaximumHeight = 0;
            for (var index = 0; index < context.ItemCount; index++)
            {
                var column = Array.IndexOf(heights, heights.Min());
                var height = itemWidth / aspects[index % aspects.Count];
                state.Bounds[index] = new Rect(column * (itemWidth + spacing), heights[column], itemWidth, height);
                heights[column] += height + spacing;
                state.MaximumHeight = Math.Max(state.MaximumHeight, height);
            }
            state.Extent = new Size(width, Math.Max(0, heights.Max() - spacing));
        }
        state.Realized.Clear();
        var window = context.RealizationRect;
        var low = 0;
        var high = state.Bounds.Length;
        var start = Math.Max(0, window.Y - state.MaximumHeight);
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (state.Bounds[middle].Y < start) low = middle + 1; else high = middle;
        }
        for (var index = low; index < state.Bounds.Length; index++)
        {
            var bounds = state.Bounds[index];
            if (bounds.Y > window.Bottom) break;
            if (bounds.Bottom < window.Y) continue;
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
