using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Neuterradise.App.Ui;

internal static class SidebarDismissal
{
    public static void Observe(FrameworkElement root, Func<Point, bool> outside, Action dismiss)
    {
        uint? captured = null;
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            if (!outside(args.GetCurrentPoint(root).Position)) return;
            // Capture the dismissal gesture so an underlying button cannot activate on release.
            if (root.CapturePointer(args.Pointer)) captured = args.Pointer.PointerId;
            args.Handled = true;
            dismiss();
        }), true);
        void Release(object sender, PointerRoutedEventArgs args)
        {
            if (captured != args.Pointer.PointerId) return;
            captured = null;
            root.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
        }
        root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Release), true);
        root.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Release), true);
    }
}
