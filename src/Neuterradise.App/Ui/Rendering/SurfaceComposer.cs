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
        var content = ComposeCore(node, semantic, width);
        if (content is null || node.ButtonStyle is not { } style) return content;
        var scope = new Border { Child = content };
        ThemeRuntime.Current.SetButtonScope(scope, style);
        return scope;
    }

    private static FrameworkElement? ComposeCore(SurfaceNode node, Func<string, FrameworkElement?> semantic, double width)
    {
        if (node.Kind == "semantic")
        {
            var element = semantic(node.Semantic!);
            if (element is null) return null;
            Detach(element);
            if (element is ProfileBannerFrame banner)
                banner.UseSceneGeometry(node.CanvasPlacement is not null || node.Placement.Height is not null || node.BannerAspect is not null);
            if (node.BannerAspect is { } aspect)
            {
                var region = new BannerAspectRegion(aspect, width) { MaxWidth = node.Placement.Width ?? double.PositiveInfinity };
                region.Children.Add(element);
                return region;
            }
            return node.CanvasPlacement is not null ? element : RegionHost(node.Semantic!, element, node.Placement);
        }
        if (node.Kind is "image" or "shape")
        {
            FrameworkElement visual = node.Kind == "image"
                ? new SkImageView { Source = ImageRef.FromPath(node.AssetPath, 1600), PlaceholderToken = "transparent" }
                : new Border { Background = ThemeRuntime.Current.Brush(node.Fill ?? "transparent") };
            visual.Opacity = node.Opacity;
            visual.IsHitTestVisible = false;
            return node.Motion is { } motion ? new SceneMotionView(visual, motion, node.Opacity) : visual;
        }
        if (node.Kind == "divider") return UI.Divider();
        if (node.Kind == "spacer") return new Grid { Height = node.Spacing + node.Padding };
        var cells = node.Children.Select((child, index) => (Plan: child, Index: index,
            View: Compose(child, semantic, Math.Max(0, width - node.Padding * 2))))
            .Where(cell => cell.View is not null).ToArray();
        if (cells.Length == 0) return null;
        var children = cells.Select(cell => cell.View!).ToArray();
        if (node.Kind == "canvas")
        {
            var canvas = new AdaptiveCanvas(node.CanvasAspect, width);
            foreach (var (plan, _, view) in cells.OrderBy(cell => cell.Plan.CanvasPlacement!.Order))
                canvas.Add(view!, plan.CanvasPlacement!);
            return node.Padding > 0 ? new Border { Child = canvas, Padding = new Thickness(node.Padding) } : canvas;
        }
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

    private static FrameworkElement RegionHost(string id, FrameworkElement element, NodeLayout placement)
    {
        if (id == "figure" && placement.StretchHeight)
            return new Border { Child = element, MinHeight = 320 };
        var height = placement.Height ?? (id switch { "figure" => 320.0, "artwork" => 260.0, _ => double.NaN });
        if (placement.Width is null && double.IsNaN(height)) return element;
        return new Border
        {
            Child = element,
            Height = height,
            MaxWidth = placement.Width ?? double.PositiveInfinity,
        };
    }

    private static int[] Occupied(int count, IEnumerable<(int Start, int Span)> placements)
    {
        var used = new bool[count];
        foreach (var (start, span) in placements) Array.Fill(used, true, start, span);
        return Enumerable.Range(0, count).Where(index => used[index]).ToArray();
    }

    private sealed class BannerAspectRegion(double aspect, double fallbackWidth) : Panel
    {
        private Size Extent(Size available)
        {
            var width = Math.Clamp(double.IsFinite(available.Width) ? available.Width : fallbackWidth, 0, 4096);
            var height = Math.Min(4096, width / aspect);
            if (double.IsFinite(available.Height)) height = Math.Min(height, Math.Max(0, available.Height));
            return new Size(Math.Min(width, height * aspect), height);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var size = Extent(availableSize);
            if (Children.Count != 0) Children[0].Measure(size);
            return size;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = Extent(finalSize);
            if (Children.Count != 0) Children[0].Arrange(new Rect((finalSize.Width - size.Width) / 2, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
            return finalSize;
        }
    }

    internal sealed class AdaptiveCanvas(double aspect, double fallbackWidth) : Panel
    {
        private readonly Dictionary<UIElement, ArtworkPlacement> _placements = [];

        public void Add(FrameworkElement child, ArtworkPlacement placement)
        {
            Children.Add(child);
            _placements.Add(child, placement);
        }

        private Size Extent(Size available)
        {
            var width = double.IsFinite(available.Width) ? available.Width : fallbackWidth;
            width = Math.Clamp(width, 0, 4096);
            var height = width / aspect;
            if (double.IsFinite(available.Height))
                height = Math.Min(height, Math.Max(0, available.Height));
            return new Size(width, Math.Min(height, 4096));
        }

        private static Rect Bounds(Size size, ArtworkPlacement placement)
        {
            var width = size.Width * placement.Width;
            var height = size.Height * placement.Height;
            var horizontal = placement.Horizontal switch { "center" => 0.5, "right" => 1, _ => 0 };
            var vertical = placement.Vertical switch { "center" => 0.5, "bottom" => 1, _ => 0 };
            return new Rect((size.Width - width) * horizontal + size.Width * placement.X,
                (size.Height - height) * vertical + size.Height * placement.Y, width, height);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var size = Extent(availableSize);
            for (var index = 0; index < Children.Count; index++)
            {
                var bounds = Bounds(size, _placements[Children[index]]);
                Children[index].Measure(new Size(bounds.Width, bounds.Height));
            }
            return size;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = Extent(finalSize);
            for (var index = 0; index < Children.Count; index++)
                Children[index].Arrange(Bounds(size, _placements[Children[index]]));
            if (Clip is not Microsoft.UI.Xaml.Media.RectangleGeometry geometry)
                Clip = geometry = new Microsoft.UI.Xaml.Media.RectangleGeometry();
            var clip = new Rect(0, 0, size.Width, size.Height);
            if (!geometry.Rect.Equals(clip)) geometry.Rect = clip;
            return finalSize;
        }
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
                    Children.Add(UI.V(12, content, RegionHost(id, fallback, new NodeLayout())));
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
