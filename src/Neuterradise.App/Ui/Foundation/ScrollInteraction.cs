using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Neuterradise.App.SystemServices.Resources;

namespace Neuterradise.App.Ui;

public static class ScrollInteraction
{
    private static readonly ConditionalWeakTable<ScrollViewer, State> Attached = new();

    public static void Attach(ScrollViewer viewer)
    {
        if (Attached.TryGetValue(viewer, out _)) return;
        var state = new State(viewer);
        Attached.Add(viewer, state);
        viewer.ViewChanged += (_, _) =>
        {
            state.NotifyScroll();
            state.QueueIndicators();
        };
        viewer.SizeChanged += (_, _) => state.QueueIndicators();
        if (viewer.Content is Microsoft.UI.Xaml.FrameworkElement content)
            content.SizeChanged += (_, _) => state.QueueIndicators();
        viewer.Loaded += (_, _) => state.EnsureThumb();
        viewer.Unloaded += (_, _) => state.ReleaseThumb();
        viewer.PointerEntered += (_, _) => state.EnsureThumb();
        viewer.PointerExited += (_, _) => state.EnsureThumb();
        viewer.LayoutUpdated += (_, _) =>
        {
            if (state.NeedsThumb) state.QueueIndicators();
        };
    }

    public static void ConfigureAxis(ScrollViewer viewer, bool horizontal)
    {
        Attach(viewer);
        var state = Attached.GetValue(viewer, static current => new State(current));
        state.ReleaseThumb();
        state.Horizontal = horizontal;
        state.NeedsThumb = true;
        viewer.HorizontalScrollMode = horizontal ? ScrollMode.Enabled : ScrollMode.Disabled;
        viewer.VerticalScrollMode = horizontal ? ScrollMode.Disabled : ScrollMode.Enabled;

        // Hidden reserves the same track allocation as Visible, avoiding a measure loop
        // when scroll extent grows across a breakpoint. Disabled releases the unused axis.
        viewer.HorizontalScrollBarVisibility = horizontal
            ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        viewer.VerticalScrollBarVisibility = horizontal
            ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        state.QueueIndicators();
    }

    private sealed class State(ScrollViewer viewer)
    {
        private double _lastHorizontal = viewer.HorizontalOffset;
        private double _lastVertical = viewer.VerticalOffset;
        private bool _indicatorsQueued;
        private Thumb? _thumb;

        public bool Horizontal { get; set; }
        public bool NeedsThumb { get; set; } = true;

        public void EnsureThumb()
        {
            NeedsThumb = true;
            QueueIndicators();
        }

        public void NotifyScroll()
        {
            if (Math.Abs(viewer.HorizontalOffset - _lastHorizontal) < 0.01
                && Math.Abs(viewer.VerticalOffset - _lastVertical) < 0.01) return;
            _lastHorizontal = viewer.HorizontalOffset;
            _lastVertical = viewer.VerticalOffset;
            ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Scroll);
        }

        public void ReleaseThumb()
        {
            _thumb = null;
            NeedsThumb = true;
        }

        private static void KeepNativeIndicatorVisible(Thumb thumb, string name)
        {
            var horizontal = name == "HorizontalThumb";
            var axis = horizontal ? "Horizontal" : "Vertical";
            // Uno's ScrollViewer NoIndicator storyboard fades the Thumb to zero even when
            // ScrollBarVisibility is Visible. Leave native scrolling and hover animations
            // intact; remove only the auto-hide animations on the active scrollbar.
            for (Microsoft.UI.Xaml.DependencyObject? ancestor = thumb;
                ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            {
                if (ancestor is not Microsoft.UI.Xaml.FrameworkElement host) continue;
                var group = VisualStateManager.GetVisualStateGroups(host)
                    .FirstOrDefault(candidate => candidate.Name == "ScrollingIndicatorStates"
                        && candidate.States.Any(state => state.Name == "NoIndicator"
                            && (state.Storyboard?.Children.Count ?? 0) > 0));
                var animation = group?.States.FirstOrDefault(state => state.Name == "NoIndicator")
                    ?.Storyboard;
                if (animation is null) continue;
                animation.Stop();
                for (var i = animation.Children.Count - 1; i >= 0; i--)
                {
                    var timeline = animation.Children[i];
                    var target = Storyboard.GetTargetName(timeline);
                    var property = Storyboard.GetTargetProperty(timeline);
                    if ((target == name && (property == "Opacity"
                            || property == (horizontal ? "Height" : "Width")))
                        || (target == axis + "Root" && property == "Visibility")
                        || (target == axis + "ThumbTransform"
                            && property == (horizontal ? "Y" : "X")))
                        animation.Children.RemoveAt(i);
                }
                break;
            }
        }

        private bool RevealThumb(Microsoft.UI.Xaml.DependencyObject root, string name)
        {
            if (root is Thumb thumb && thumb.Name == name && thumb.ActualWidth > 0
                && thumb.ActualHeight > 0 && thumb.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
            {
                if (!ReferenceEquals(_thumb, thumb))
                {
                    _thumb = thumb;
                    KeepNativeIndicatorVisible(thumb, name);
                }
                if (thumb.Opacity < 1) thumb.Opacity = 1;
                return true;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (RevealThumb(VisualTreeHelper.GetChild(root, i), name)) return true;
            return false;
        }

        public void QueueIndicators()
        {
            if (_indicatorsQueued || !viewer.IsLoaded) return;
            _indicatorsQueued = true;
            if (viewer.DispatcherQueue?.TryEnqueue(() =>
                {
                    _indicatorsQueued = false;
                    if (!viewer.IsLoaded) return;
                    var overflow = Horizontal ? viewer.ScrollableWidth : viewer.ScrollableHeight;
                    var desired = overflow > 1
                        ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden;
                    if (Horizontal)
                    {
                        if (viewer.HorizontalScrollBarVisibility != desired)
                            viewer.HorizontalScrollBarVisibility = desired;
                    }
                    else if (viewer.VerticalScrollBarVisibility != desired)
                    {
                        viewer.VerticalScrollBarVisibility = desired;
                    }
                    NeedsThumb = overflow > 1 && !RevealThumb(
                        viewer, Horizontal ? "HorizontalThumb" : "VerticalThumb");
                }) != true)
                _indicatorsQueued = false;
        }
    }
}
