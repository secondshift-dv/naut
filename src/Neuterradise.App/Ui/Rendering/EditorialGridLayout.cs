using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Neuterradise.App.Ui;

/// <summary>Roomy caption-first media pages, distinct from the dense square Mosaic.</summary>
public sealed class EditorialGridLayout(double minimumWidth, double cardHeight, double spacing,
    int maximumColumns = 3, double? rowSpacing = null, bool authoredSizing = false, string? justification = null) : VirtualizingLayout
{
    private sealed class State
    {
        public readonly List<(UIElement Element, Rect Bounds)> Realized = [];
    }

    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args) => InvalidateMeasure();

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : minimumWidth;
        var ideal = authoredSizing ? minimumWidth : Math.Clamp(minimumWidth, 180, 260);
        var columns = Math.Clamp((int)Math.Floor((width + spacing) / (ideal + spacing)), 1, maximumColumns);
        var unit = Math.Min(ideal, Math.Max(1, (width - spacing * (columns - 1)) / columns));
        var legacyInset = Math.Max(0, (width - (columns * unit + spacing * (columns - 1))) / 2);
        var verticalSpacing = rowSpacing ?? spacing;
        var stride = cardHeight + verticalSpacing;
        var state = context.LayoutState as State ?? new State();
        context.LayoutState = state;
        state.Realized.Clear();
        var window = context.RealizationRect;
        for (var index = 0; index < context.ItemCount; index++)
        {
            var rowCount = Math.Min(columns, context.ItemCount - index / columns * columns);
            var remaining = Math.Max(0, width - (rowCount * unit + spacing * (rowCount - 1)));
            var inset = justification switch
            {
                "start" => 0,
                "center" => remaining / 2,
                "end" => remaining,
                _ => legacyInset,
            };
            var x = inset + index % columns * (unit + spacing);
            var y = index / columns * stride;
            var bounds = new Rect(x, y, unit, cardHeight);
            if (bounds.Bottom < window.Top || bounds.Top > window.Bottom) continue;
            var element = context.GetOrCreateElementAt(index);
            element.Measure(new Size(bounds.Width, bounds.Height));
            state.Realized.Add((element, bounds));
        }
        var rows = (int)Math.Ceiling(context.ItemCount / (double)columns);
        return new Size(width, rows == 0 ? 0 : rows * stride - verticalSpacing);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (context.LayoutState is State state)
            foreach (var (element, bounds) in state.Realized) element.Arrange(bounds);
        return finalSize;
    }
}
