using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.SystemServices.Resources;

namespace Neuterradise.App.Ui;

public static class ScrollInteraction
{
    private static readonly ConditionalWeakTable<ScrollViewer, Offset> Attached = new();

    public static void Attach(ScrollViewer viewer)
    {
        if (Attached.TryGetValue(viewer, out _)) return;
        var offset = new Offset { Horizontal = viewer.HorizontalOffset, Vertical = viewer.VerticalOffset };
        Attached.Add(viewer, offset);
        viewer.ViewChanged += (_, _) =>
        {
            if (Math.Abs(viewer.HorizontalOffset - offset.Horizontal) < 0.01 &&
                Math.Abs(viewer.VerticalOffset - offset.Vertical) < 0.01) return;
            offset.Horizontal = viewer.HorizontalOffset;
            offset.Vertical = viewer.VerticalOffset;
            ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Scroll);
        };
    }

    private sealed class Offset
    {
        public double Horizontal { get; set; }
        public double Vertical { get; set; }
    }
}
