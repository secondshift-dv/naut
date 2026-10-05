using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Neuterradise.App.Ui;

/// <summary>Equal form columns become stacked fields when their readable minimum no longer fits.</summary>
public sealed class ResponsiveForm : Panel
{
    private readonly double _minimumColumnWidth;
    private readonly double _spacing;

    public ResponsiveForm(double minimumColumnWidth, double spacing, params UIElement[] children)
    {
        _minimumColumnWidth = minimumColumnWidth;
        _spacing = spacing;
        foreach (var child in children) Children.Add(child);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = Children.OfType<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToArray();
        if (visible.Length == 0) return new Size(0, 0);
        var width = double.IsInfinity(availableSize.Width)
            ? (_minimumColumnWidth + _spacing) * visible.Length - _spacing
            : availableSize.Width;
        var columns = ColumnCount(width, visible.Length);
        var cellWidth = Math.Max(0, (width - _spacing * (columns - 1)) / columns);
        double height = 0;
        for (var start = 0; start < visible.Length; start += columns)
        {
            double rowHeight = 0;
            for (var index = start; index < Math.Min(start + columns, visible.Length); index++)
            {
                visible[index].Measure(new Size(cellWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, visible[index].DesiredSize.Height);
            }
            height += rowHeight + (start > 0 ? _spacing : 0);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Children.OfType<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToArray();
        if (visible.Length == 0) return finalSize;
        var columns = ColumnCount(finalSize.Width, visible.Length);
        var cellWidth = Math.Max(0, (finalSize.Width - _spacing * (columns - 1)) / columns);
        double y = 0;
        for (var start = 0; start < visible.Length; start += columns)
        {
            double rowHeight = 0;
            for (var index = start; index < Math.Min(start + columns, visible.Length); index++)
            {
                var child = visible[index];
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                child.Arrange(new Rect((index - start) * (cellWidth + _spacing), y, cellWidth, child.DesiredSize.Height));
            }
            y += rowHeight + _spacing;
        }
        return finalSize;
    }

    private int ColumnCount(double width, int count) =>
        Math.Clamp((int)Math.Floor((width + _spacing) / (_minimumColumnWidth + _spacing)), 1, count);

}
