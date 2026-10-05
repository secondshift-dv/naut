using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Design.Themes;

namespace Neuterradise.App.Ui;

internal sealed class CustomizationWorkspaceView
{
    private readonly Grid _modal;
    private readonly Border _inspector;
    private readonly Border _panel;
    private readonly FrameworkElement _footer;
    private readonly Grid _inspectorFooter = new();
    private readonly Grid _modalFooter = new();
    private readonly Button _cancelButton;
    private readonly Button _backButton;

    public CustomizationWorkspaceView(TextBlock subject, FrameworkElement tabs, FrameworkElement preview,
        FrameworkElement scope, FrameworkElement editor, TextBlock status, Button save, Action close, Action cancel, Action back,
        FrameworkElement inspector, bool cardOnly)
    {
        var header = UI.Grid("auto", "*,auto",
            UI.V(4, UI.Text(UI.T("Customize.Title", "Customize"), "section-title"), subject).At(0, 0),
            UI.IconButton("icon.action.close", UI.T("Common.Close", "Close"), close).At(0, 1));
        _cancelButton = UI.Button(UI.T("Customize.Cancel", "Cancel"), cancel, ButtonKind.Ghost);
        _backButton = UI.Button(UI.T("Common.Back", "Back"), back, ButtonKind.Secondary, "icon.action.back");
        _backButton.Visibility = Visibility.Collapsed;
        var actions = UI.Grid("auto", "auto,*,auto", _backButton.At(0, 0), UI.Wrap(6, _cancelButton, save).At(0, 2));
        var footer = UI.V(8, status, actions);
        _footer = footer;
        _inspectorFooter.Children.Add(footer);
        var body = new Grid();
        _inspector = UI.Surface(UI.Scroll(inspector), Material.Deep, 0, 12);
        body.Children.Add(_inspector);
        var shell = UI.Surface(UI.Grid("auto,*,auto", "*",
            header.Margin(0, 0, 0, 10).At(0), body.At(1), _inspectorFooter.Margin(0, 10, 0, 0).At(2)),
            Material.Deep, 0, 12);
        shell.Width = 320;
        shell.HorizontalAlignment = HorizontalAlignment.Right;
        View = new Grid();
        View.Children.Add(shell);
        InspectorShell = shell;
        shell.Visibility = cardOnly ? Visibility.Collapsed : Visibility.Visible;
        shell.Tapped += (_, args) => args.Handled = true;

        var modalHeader = UI.Grid("auto", "*,auto");
        modalHeader.Children.Add(UI.Text(cardOnly ? UI.T("Card.Customize", "Customize Card") : UI.T("Customize.Title", "Customize"), "section-title").At(0, 0));
        if (cardOnly)
            modalHeader.Children.Add(UI.IconButton("icon.action.close", UI.T("Common.Close", "Close"), close).At(0, 1));
        var modalFooter = UI.V(8);
        modalFooter.Children.Add(_modalFooter);
        var modalScroll = UI.Scroll(UI.V(10, tabs, preview, scope, editor));
        modalScroll.VerticalAlignment = VerticalAlignment.Top;
        _panel = UI.Surface(UI.Grid("auto,auto,auto", "*", modalHeader.At(0),
            modalScroll.Margin(0, 12, 0, 12).At(1), modalFooter.At(2)),
            Material.Deep, 16, 16);
        _panel.Width = 1040;
        _panel.MaxWidth = 1040;
        _panel.MaxHeight = 960;
        _panel.HorizontalAlignment = HorizontalAlignment.Center;
        _panel.VerticalAlignment = VerticalAlignment.Center;
        _modal = new Grid { Background = ThemeRuntime.Current.Brush("scrimStrong"), Visibility = Visibility.Collapsed };
        _panel.Tapped += (_, args) => args.Handled = true;
        _modal.Children.Add(_panel);
        View.Children.Add(_modal);
        View.Tapped += (_, args) =>
        {
            close();
            args.Handled = true;
        };
        void UpdateModalEnvelope()
        {
            _panel.Width = Math.Max(1, Math.Min(1040, View.ActualWidth - 32));
            _panel.MaxHeight = Math.Max(1, Math.Min(960, View.ActualHeight - 32));
            modalScroll.MaxHeight = Math.Max(0, _panel.MaxHeight - modalHeader.ActualHeight - modalFooter.ActualHeight - 58);
        }
        View.SizeChanged += (_, _) => UpdateModalEnvelope();
        modalHeader.SizeChanged += (_, _) => UpdateModalEnvelope();
        modalFooter.SizeChanged += (_, _) => UpdateModalEnvelope();
    }

    public Grid View { get; }
    private Border InspectorShell { get; }
    public bool ContainsPointer(Windows.Foundation.Point point, UIElement relativeTo)
    {
        var shell = _modal.Visibility == Visibility.Visible ? _panel : InspectorShell;
        var local = relativeTo.TransformToVisual(shell).TransformPoint(point);
        return local.X >= 0 && local.Y >= 0 && local.X <= shell.ActualWidth && local.Y <= shell.ActualHeight;
    }
    public void SetInspectorWidth(double width) => InspectorShell.Width = width;
    public void SetEditor(bool editor)
    {
        // One action bar follows the active form without creating a second transaction owner.
        SurfaceComposer.Detach(_footer);
        (editor ? _modalFooter : _inspectorFooter).Children.Add(_footer);
        _cancelButton.Visibility = editor ? Visibility.Collapsed : Visibility.Visible;
        _backButton.Visibility = editor && InspectorShell.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        _modal.Visibility = editor ? Visibility.Visible : Visibility.Collapsed;
    }
}
