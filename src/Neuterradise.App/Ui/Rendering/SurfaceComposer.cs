using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Presentation;

namespace Neuterradise.App.Ui;

public static class SurfaceComposer
{
    public static FrameworkElement Build(SurfaceNode node, Func<string, FrameworkElement?> semantic, double width)
    {
        if (node.Kind == "semantic")
        {
            var element = semantic(node.Semantic!) ?? new Grid();
            Detach(element);
            return element;
        }
        if (node.Kind == "divider") return UI.Divider();
        if (node.Kind == "spacer") return new Grid { Height = node.Spacing + node.Padding };
        var children = node.Children.Select(child => Build(child, semantic, width)).ToArray();
        if (node.Kind == "columns" && width >= 800)
        {
            var tracks = string.Join(',', node.Columns.Count == children.Length ? node.Columns : Enumerable.Repeat("*", children.Length));
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
