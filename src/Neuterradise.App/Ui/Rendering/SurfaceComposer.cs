using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Presentation;
using Windows.Foundation;

namespace Neuterradise.App.Ui;

public static class SurfaceComposer
{
    public static SurfaceNode? WithoutSemantic(SurfaceNode node, string excluded)
    {
        if (node.Kind == "semantic" && node.Semantic == excluded) return null;
        if (node.Children.Count == 0) return node;

        var children = new List<SurfaceNode>(node.Children.Count);
        var columns = node.Kind == "columns" && node.Columns.Count == node.Children.Count ? new List<string>(node.Children.Count) : null;
        for (var index = 0; index < node.Children.Count; index++)
        {
            var child = WithoutSemantic(node.Children[index], excluded);
            if (child is null) continue;
            children.Add(child);
            if (columns is not null) columns.Add(node.Columns[index]);
        }

        if (children.Count == 0) return null;
        return node with
        {
            Children = children,
            Columns = columns is null ? node.Columns : columns,
            Narrow = node.Narrow is { } narrow ? WithoutSemantic(narrow, excluded) : null,
        };
    }

    public static FrameworkElement Build(SurfaceNode node, Func<string, FrameworkElement?> semantic, double width,
        string? fallbackRegion = null) =>
        node.Modular ? new ResponsiveSurface(node, semantic, width, fallbackRegion) : Compose(node, semantic, width) ?? new Grid();

    private static FrameworkElement? Compose(SurfaceNode node, Func<string, FrameworkElement?> semantic, double width)
    {
        if (node.Kind == "semantic")
        {
            var element = semantic(node.Semantic!);
            if (element is not null) Detach(element);
            return element;
        }
        if (node.Kind == "divider") return UI.Divider();
        if (node.Kind == "spacer") return new Grid { Height = node.Spacing + node.Padding };
        var cells = node.Children.Select((child, index) => (Plan: child, Index: index,
            View: Compose(child, semantic, Math.Max(0, width - node.Padding * 2))))
            .Where(cell => cell.View is not null).ToArray();
        if (cells.Length == 0) return null;
        var children = cells.Select(cell => cell.View!).ToArray();
        if (node.Kind == "grid")
        {
            var rows = Occupied(node.Rows.Count, cells.Select(cell => (cell.Plan.Placement.Row, cell.Plan.Placement.RowSpan)));
            var columns = Occupied(node.Columns.Count, cells.Select(cell => (cell.Plan.Placement.Column, cell.Plan.Placement.ColumnSpan)));
            var grid = UI.Grid(string.Join(',', rows.Select(index => node.Rows[index])),
                string.Join(',', columns.Select(index => node.Columns[index])));
            grid.RowSpacing = grid.ColumnSpacing = node.Spacing;
            foreach (var (plan, _, view) in cells)
            {
                var cell = new Border { Child = view };
                Grid.SetRow(cell, Array.IndexOf(rows, plan.Placement.Row));
                Grid.SetColumn(cell, Array.IndexOf(columns, plan.Placement.Column));
                Grid.SetRowSpan(cell, plan.Placement.RowSpan);
                Grid.SetColumnSpan(cell, plan.Placement.ColumnSpan);
                grid.Children.Add(cell);
            }
            return node.Padding > 0 ? new Border { Child = grid, Padding = new Thickness(node.Padding) } : grid;
        }
        if (node.Kind == "columns" && width >= 800)
        {
            var tracks = string.Join(',', node.Columns.Count == node.Children.Count ? cells.Select(cell => node.Columns[cell.Index]) : Enumerable.Repeat("*", children.Length));
            var grid = UI.Grid("auto", tracks);
            for (var i = 0; i < children.Length; i++)
            {
                var cell = new Border
                {
                    Child = children[i],
                    Margin = new Thickness(i == 0 ? 0 : node.Spacing / 2, 0, i == children.Length - 1 ? 0 : node.Spacing / 2, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                grid.Children.Add(cell.At(0, i));
            }
            return node.Padding > 0 ? new Border { Child = grid, Padding = new Thickness(node.Padding) } : grid;
        }
        if (node.Kind == "overlay")
        {
            var grid = new Grid();
            foreach (var child in children) grid.Children.Add(child);
            return node.Padding > 0 ? new Border { Child = grid, Padding = new Thickness(node.Padding) } : grid;
        }
        var stack = UI.V(node.Spacing, children);
        return node.Kind == "panel" ? UI.Surface(stack, Material.Raised, 12, node.Padding)
            : node.Padding > 0 ? new Border { Child = stack, Padding = new Thickness(node.Padding) } : stack;
    }

    private static int[] Occupied(int count, IEnumerable<(int Start, int Span)> placements)
    {
        var used = new bool[count];
        foreach (var (start, span) in placements) Array.Fill(used, true, start, span);
        return Enumerable.Range(0, count).Where(index => used[index]).ToArray();
    }

    private sealed class ResponsiveSurface(SurfaceNode plan, Func<string, FrameworkElement?> region, double fallbackWidth,
        string? fallbackRegion) : Grid
    {
        private SurfaceNode? _arrangement;
        private bool _columns;

        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsFinite(availableSize.Width) ? availableSize.Width : fallbackWidth;
            var arrangement = width < plan.Breakpoint ? plan.Narrow ?? plan : plan;
            var columns = width >= 800;
            if (!ReferenceEquals(_arrangement, arrangement) || _columns != columns)
            {
                _arrangement = arrangement;
                _columns = columns;
                Children.Clear();
                var content = Compose(arrangement, region, width);
                if (fallbackRegion is { } id && !arrangement.HasRegion(id) && region(id) is { } fallback)
                {
                    Detach(fallback);
                    Children.Add(UI.V(12, content, fallback));
                }
                else if (content is not null) Children.Add(content);
            }
            return base.MeasureOverride(availableSize);
        }
    }

    public static void Detach(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel panel: panel.Children.Remove(element); break;
            case Border border when border.Child == element: border.Child = null; break;
            case ContentControl control when control.Content == element: control.Content = null; break;
        }
    }
}
