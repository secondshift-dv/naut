using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Neuterradise.App.Localization;
using Neuterradise.App.Presentation;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Resources;

namespace Neuterradise.App.Ui;

/// <summary>Bridges the runtime's <see cref="UiDispatch"/> to the Uno UI thread.</summary>
public sealed class DispatcherQueueSyncContext(DispatcherQueue queue) : EnqueueSynchronizationContext(
    () => queue.HasThreadAccess,
    callback => queue.TryEnqueue(() => callback()))
{
    public override SynchronizationContext CreateCopy() => this;
}

/// <summary>A bag of subscriptions released together when a surface is retired.</summary>
public sealed class Disposables : IDisposable
{
    private readonly List<IDisposable> _items = [];

    public T Add<T>(T item) where T : IDisposable
    {
        _items.Add(item);
        return item;
    }

    public void Add(Action release) => _items.Add(new ActionDisposable(release));

    public void Dispose()
    {
        foreach (var item in _items)
        {
            item.Dispose();
        }

        _items.Clear();
    }

    private sealed class ActionDisposable(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>
/// Type-checked property observation (compile-time lambdas instead of reflection bindings). The apply
/// callback runs immediately and then on the UI thread whenever one of the named properties changes.
/// </summary>
public static class Observe
{
    public static IDisposable Props(INotifyPropertyChanged? source, Action apply, params string[] properties)
    {
        apply();
        if (source is null)
        {
            return new Disposables();
        }

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (properties.Length == 0 || string.IsNullOrEmpty(args.PropertyName) || properties.Contains(args.PropertyName))
            {
                UiDispatch.Run(apply);
            }
        };
        source.PropertyChanged += handler;
        var bag = new Disposables();
        bag.Add(() => source.PropertyChanged -= handler);
        return bag;
    }

    public static IDisposable Collection(INotifyCollectionChanged? source, Action apply)
    {
        apply();
        if (source is null)
        {
            return new Disposables();
        }

        NotifyCollectionChangedEventHandler handler = (_, _) => UiDispatch.Run(apply);
        source.CollectionChanged += handler;
        var bag = new Disposables();
        bag.Add(() => source.CollectionChanged -= handler);
        return bag;
    }
}

public enum ButtonKind
{
    Signature,
    Primary,
    Secondary,
    Ghost,
    Destructive,
}

/// <summary>Material hierarchy .</summary>
public enum Material
{
    Grounded,
    Raised,
    Frost,
    Deep,
    Glass,
}

/// <summary>Code-first element builders. Keeps every surface on the same token vocabulary.</summary>
public static class UI
{
    private static readonly DependencyProperty ActionContentProperty = DependencyProperty.RegisterAttached(
        "ActionContent", typeof(bool), typeof(UI), new PropertyMetadata(false));

    public static void DisableTextSelection(FrameworkElement element) => element.SetValue(ActionContentProperty, true);

    public static string T(string key, string fallback) => SurfaceText.Get(key, fallback);

    public static string F(string key, string fallback, params object[] args) => SurfaceText.Format(key, fallback, args);

    public static TextBlock Text(string? text = null, string role = "body", string? color = null, int maxLines = 0)
    {
        var block = new TextBlock { Text = text ?? string.Empty };
        ThemeRuntime.Current.ApplyType(block, role, SemanticTextColor(color));
        block.Loaded += (_, _) =>
        {
            block.IsTextSelectionEnabled = true;
            for (var parent = VisualTreeHelper.GetParent(block); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
                    or Microsoft.UI.Xaml.Controls.Primitives.SelectorItem
                    or ComboBox or AutoSuggestBox or ToggleSwitch or Slider or MenuFlyoutItem or ToolTip or CardVisual or MediaCardView
                    || (bool)parent.GetValue(ActionContentProperty))
                {
                    block.IsTextSelectionEnabled = false;
                    break;
                }
            }
        };
        if (maxLines > 0)
        {
            block.MaxLines = maxLines;
            block.TextWrapping = maxLines > 1 ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap;
            block.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        return block;
    }

    public static TextBlock WrappedText(string? text = null, string role = "body", string? color = null)
    {
        var block = Text(text, role, color);
        block.TextWrapping = TextWrapping.WrapWholeWords;
        block.TextTrimming = TextTrimming.None;
        block.MaxLines = 0;
        return block;
    }

    private static string? SemanticTextColor(string? color) => color switch
    {
        "accent" => "textAccent",
        "info" => "textInfo",
        "success" => "textSuccess",
        "warning" => "textWarning",
        "danger" => "textDanger",
        _ => color,
    };

    public static StackPanel V(double spacing, params UIElement?[] children) => Stack(Orientation.Vertical, spacing, children);

    public static StackPanel H(double spacing, params UIElement?[] children) => Stack(Orientation.Horizontal, spacing, children);

    public static VariableWrap Wrap(double spacing, params UIElement?[] children) => new(spacing, children.OfType<UIElement>().ToArray());

    public static ResponsiveForm FormColumns(params UIElement[] children) => new(220, 8, children);

    public static StackPanel Stack(Orientation orientation, double spacing, params UIElement?[] children)
    {
        var panel = new StackPanel { Orientation = orientation, Spacing = spacing };
        foreach (var child in children)
        {
            if (child is not null)
            {
                panel.Children.Add(child);
            }
        }

        return panel;
    }

    /// <summary>Grid from track strings: "auto,*,2*,48".</summary>
    public static Grid Grid(string rows = "*", string columns = "*", params UIElement?[] children)
    {
        var grid = new Grid();
        foreach (var track in rows.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = Track(track.Trim()) });
        }

        foreach (var track in columns.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = Track(track.Trim()) });
        }

        foreach (var child in children)
        {
            if (child is not null)
            {
                grid.Children.Add(child);
            }
        }

        return grid;
    }

    public static GridLength Track(string track)
    {
        if (track == "auto")
        {
            return GridLength.Auto;
        }

        if (track.EndsWith('*'))
        {
            var factor = track.Length == 1 ? 1 : double.Parse(track[..^1], System.Globalization.CultureInfo.InvariantCulture);
            return new GridLength(factor, GridUnitType.Star);
        }

        return new GridLength(double.Parse(track, System.Globalization.CultureInfo.InvariantCulture));
    }

    public static T At<T>(this T element, int row, int column = 0, int rowSpan = 1, int columnSpan = 1) where T : FrameworkElement
    {
        Microsoft.UI.Xaml.Controls.Grid.SetRow(element, row);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(element, column);
        Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(element, rowSpan);
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(element, columnSpan);
        return element;
    }

    public static T Margin<T>(this T element, double uniform) where T : FrameworkElement
    {
        element.Margin = new Thickness(uniform);
        return element;
    }

    public static T Margin<T>(this T element, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        element.Margin = new Thickness(left, top, right, bottom);
        return element;
    }

    public static T Align<T>(this T element, HorizontalAlignment horizontal = HorizontalAlignment.Stretch, VerticalAlignment vertical = VerticalAlignment.Stretch) where T : FrameworkElement
    {
        element.HorizontalAlignment = horizontal;
        element.VerticalAlignment = vertical;
        return element;
    }

    public static T Size<T>(this T element, double? width = null, double? height = null) where T : FrameworkElement
    {
        if (width is { } w)
        {
            element.Width = w;
        }

        if (height is { } h)
        {
            element.Height = h;
        }

        return element;
    }

    public static T Tip<T>(this T element, string? text) where T : FrameworkElement
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            ToolTipService.SetToolTip(element, text);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, text);
        }

        return element;
    }

    public static T Visible<T>(this T element, bool visible) where T : UIElement
    {
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        return element;
    }

    public static Button Button(string label, Action? onClick = null, ButtonKind kind = ButtonKind.Secondary, string? icon = null, ICommand? command = null, object? parameter = null)
    {
        var theme = ThemeRuntime.Current;
        var foreground = kind switch
        {
            ButtonKind.Primary => "textOnAccent",
            ButtonKind.Destructive => "textDanger",
            _ => null,
        };
        UIElement content;
        Grid? signatureStage = null;
        if (kind == ButtonKind.Signature && icon is not null)
        {
            signatureStage = new Grid { Width = 20, Height = 20 };
            signatureStage.Children.Add(new IconView(icon, 16, "signatureGold").Align(HorizontalAlignment.Center, VerticalAlignment.Center));
            signatureStage.Children.Add(new Border
            {
                Width = 3,
                Height = 3,
                CornerRadius = new CornerRadius(2),
                Background = theme.Brush("signatureGold"),
                Opacity = 0.58,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            });
            signatureStage.Children.Add(new Border
            {
                Width = 2,
                Height = 2,
                CornerRadius = new CornerRadius(1),
                Background = theme.Brush("signatureGold"),
                Opacity = 0.36,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
            });
            content = H(7, signatureStage.Align(vertical: VerticalAlignment.Center), Text(label, "control", "textPrimary").Align(vertical: VerticalAlignment.Center));
        }
        else
        {
            content = icon is null
                ? (UIElement)Text(label, "control", foreground)
                : H(8, new IconView(icon, 16, foreground ?? "textPrimary").Align(vertical: VerticalAlignment.Center), Text(label, "control", foreground).Align(vertical: VerticalAlignment.Center));
        }
        if (content is FrameworkElement contentElement)
        {
            contentElement.VerticalAlignment = VerticalAlignment.Center;
        }

        var button = new Button
        {
            Content = content,
            MinHeight = theme.Tokens.Number("densityControlHeight", 32),
            Padding = new Thickness(theme.Tokens.Number("densityControlPaddingHorizontal", 9), theme.Tokens.Number("densityControlPaddingVertical", 4), theme.Tokens.Number("densityControlPaddingHorizontal", 9), theme.Tokens.Number("densityControlPaddingVertical", 4)),
            CornerRadius = new CornerRadius(theme.Tokens.Number("radiusControl", 8)),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Command = command,
            CommandParameter = parameter,
            KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden,
        };

        switch (kind)
        {
            case ButtonKind.Signature:
                button.MinHeight = Math.Min(button.MinHeight, 30);
                button.Padding = new Thickness(10, 3, 10, 3);
                button.Background = theme.Brush("signatureFill");
                button.BorderBrush = theme.Brush("signatureGold");
                button.Opacity = 0.94;
                break;
            case ButtonKind.Primary:
                button.Background = theme.Brush("accent");
                button.BorderBrush = theme.Brush("accent");
                break;
            case ButtonKind.Ghost:
                button.Background = theme.Brush("transparent");
                button.BorderBrush = theme.Brush("transparent");
                break;
            case ButtonKind.Destructive:
                button.Background = theme.Brush("surface3");
                button.BorderBrush = theme.Brush("danger");
                button.Foreground = theme.Brush("textDanger");
                break;
            default:
                button.Background = theme.Brush("surface3");
                button.BorderBrush = theme.Brush("borderDefault");
                break;
        }

        button.BorderThickness = new Thickness(theme.Tokens.Number("borderWeight", 1));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        AddPointerFeedback(button);
        if (kind != ButtonKind.Signature)
            theme.TrackControlFamily(button, "button", kind == ButtonKind.Secondary, kind: kind);
        if (icon is not null)
        {
            AttachSemanticIconMotion(button, icon);
            if (signatureStage is not null)
            {
                AttachSignatureAmbient(button, signatureStage);
            }
        }
        return button;
    }

    public static FrameworkElement DecoratedControl(Control control)
    {
        ThemeRuntime.Current.TrackNativeControls(control);
        control.Loaded += (_, _) => ThemeRuntime.Current.TrackNativeControls(control);
        return control;
    }

    public static Button Tab(string label, bool selected, Action onClick)
    {
        var tab = Button(label, onClick, selected ? ButtonKind.Primary : ButtonKind.Ghost);
        ThemeRuntime.Current.TrackControlFamily(tab, "tab", neutral: false,
            kind: selected ? ButtonKind.Primary : ButtonKind.Ghost);
        return tab;
    }

    public static FrameworkElement ControlSheet(ControlSkinPlan skin)
    {
        var theme = ThemeRuntime.Current;
        T Sample<T>(T control, string family, ButtonKind kind = ButtonKind.Secondary) where T : Control
        {
            theme.TrackControlFamily(control, family, neutral: kind == ButtonKind.Secondary, kind: kind, preview: skin);
            return control;
        }
        var primary = Sample(Button(T("Common.Open", "Open"), kind: ButtonKind.Primary), "button", ButtonKind.Primary);
        var secondary = Sample(Button(T("Common.Cancel", "Cancel")), "button");
        var chip = Sample(new Button { Content = "Aa" }, "chip");
        var tab = Sample(new Button { Content = "Aa" }, "tab", ButtonKind.Primary);
        var icon = Sample(IconButton("icon.action.add", T("Common.Add", "Add")), "icon-button", ButtonKind.Ghost);
        var input = Sample(Input("Aa · 123"), "input");
        var toggle = Sample(new ToggleSwitch { IsOn = true }, "toggle");
        var slider = Sample(new Slider { Minimum = 0, Maximum = 100, Value = 65, Width = 96 }, "slider");
        var scroll = Scroll(V(3, Text("Aa · 123"), Text("Aa · 123"), Text("Aa · 123")), maxHeight: 35);
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        var scrollbar = skin.For("scrollbar");
        scroll.Resources["ScrollBarThumbFill"] = theme.Brush(scrollbar.Border);
        scroll.Resources["ScrollBarThumbMinWidth"] = scrollbar.ScrollbarWidth;
        scroll.Resources["ScrollBarThumbMinHeight"] = scrollbar.ScrollbarWidth;
        var sheet = V(4, H(4, primary, secondary), H(4, chip, tab, icon), input, H(4, toggle, slider), scroll);
        sheet.IsHitTestVisible = false;
        return sheet.Margin(6);
    }

    public static Button IconButton(string iconKey, string tooltip, Action? onClick = null, double size = 28, ICommand? command = null, object? parameter = null)
    {
        var theme = ThemeRuntime.Current;
        var button = new Button
        {
            Content = new IconView(iconKey, Math.Round(size * 0.5), "textPrimary"),
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(theme.Tokens.Number("radiusControl", 8)),
            Background = theme.Brush("transparent"),
            BorderBrush = theme.Brush("transparent"),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Command = command,
            CommandParameter = parameter,
            KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden,
        };
        button.Tip(tooltip);
        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        AddPointerFeedback(button);
        theme.TrackControlFamily(button, "icon-button", neutral: false, compact: true, kind: ButtonKind.Ghost);
        AttachSemanticIconMotion(button, iconKey);
        return button;
    }

    private static void AddPointerFeedback(Button button)
    {
        var restingOpacity = button.Opacity;
        button.PointerEntered += (_, _) =>
        {
            restingOpacity = button.Opacity;
            if (button.IsEnabled) button.Opacity = restingOpacity * 0.88;
        };
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            if (button.IsEnabled) button.Opacity = restingOpacity * 0.72;
        }), true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            button.Opacity = restingOpacity;
        }), true);
        button.PointerExited += (_, _) => button.Opacity = restingOpacity;
    }

    public static void AttachSemanticIconMotion(Button button, string iconKey)
    {
        var icon = FindIcon(button.Content);
        if (icon is null)
        {
            return;
        }

        var transform = new CompositeTransform();
        icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        icon.RenderTransform = transform;

        void Apply(bool active)
        {
            var scale = 1.0;
            var translateX = 0.0;
            var translateY = 0.0;
            var rotation = 0.0;
            if (active && !ThemeRuntime.Current.ReducedMotion)
            {
                switch (iconKey)
                {
                    case "icon.navigation.home": translateY = -1; break;
                    case "icon.navigation.gallery": scale = 1.04; translateX = 0.6; break;
                    case "icon.navigation.import": translateY = 1.5; break;
                    case "icon.navigation.settings": rotation = 12; break;
                    case "icon.action.edit": rotation = -7; break;
                    case "icon.action.add":
                    case "icon.media.add": scale = 1.07; break;
                    case "icon.action.open-folder": translateX = 1.5; break;
                    case "icon.action.restore": rotation = -15; break;
                    case "icon.action.delete": translateY = -1; break;
                    case "icon.action.favorite":
                    case "icon.profile.favorite": scale = 1.08; break;
                    case "icon.navigation.customize": scale = 1.05; rotation = 5; break;
                    default: return;
                }
            }

            var duration = Math.Max(1, ThemeRuntime.Current.Duration("fast"));
            AnimateTransform(transform, "ScaleX", scale, duration);
            AnimateTransform(transform, "ScaleY", scale, duration);
            AnimateTransform(transform, "TranslateX", translateX, duration);
            AnimateTransform(transform, "TranslateY", translateY, duration);
            AnimateTransform(transform, "Rotation", rotation, duration);
        }

        button.PointerEntered += (_, _) => Apply(true);
        button.PointerExited += (_, _) => Apply(false);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Apply(false)), true);
    }

    public static void AnimateStatusIcon(IconView icon, string state)
    {
        if (ThemeRuntime.Current.ReducedMotion)
        {
            return;
        }

        var transform = new CompositeTransform();
        icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        icon.RenderTransform = transform;
        var duration = Math.Max(1, ThemeRuntime.Current.Duration("fast"));

        switch (state)
        {
            case "activity":
                AnimateTransform(transform, "ScaleX", 1.08, duration, autoReverse: true);
                AnimateTransform(transform, "ScaleY", 1.08, duration, autoReverse: true);
                break;
            case "success":
                AnimateTransform(transform, "ScaleX", 1.12, duration, autoReverse: true);
                AnimateTransform(transform, "ScaleY", 1.12, duration, autoReverse: true);
                break;
            case "warning":
                AnimateTransform(transform, "Rotation", 5, duration, autoReverse: true);
                break;
            case "error":
                AnimateTransform(transform, "TranslateX", 1.5, duration, autoReverse: true);
                break;
            default:
                AnimateTransform(transform, "ScaleX", 1.05, duration, autoReverse: true);
                AnimateTransform(transform, "ScaleY", 1.05, duration, autoReverse: true);
                break;
        }
    }

    private static void AttachSignatureAmbient(Button button, Grid stage)
    {
        var bubbles = stage.Children.OfType<Border>().Take(2).ToArray();
        var bubbleTransforms = bubbles.Select(bubble =>
        {
            var transform = new CompositeTransform();
            bubble.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            bubble.RenderTransform = transform;
            return transform;
        }).ToArray();

        void Pulse()
        {
            if (ThemeRuntime.Current.ReducedMotion || !button.IsEnabled)
            {
                return;
            }

            var duration = Math.Max(1, ThemeRuntime.Current.Duration("slow"));
            AnimateTransform(stage, "Opacity", 0.78, duration, autoReverse: true);
            if (bubbleTransforms.Length > 0)
            {
                AnimateTransform(bubbleTransforms[0], "TranslateY", -1.5, duration, autoReverse: true);
            }

            if (bubbleTransforms.Length > 1)
            {
                AnimateTransform(bubbleTransforms[1], "TranslateY", 1.2, duration, autoReverse: true);
            }
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2800) };
        timer.Tick += (_, _) => Pulse();
        button.Loaded += (_, _) =>
        {
            Pulse();
            timer.Start();
        };
        button.Unloaded += (_, _) =>
        {
            timer.Stop();
            stage.Opacity = 1;
            foreach (var transform in bubbleTransforms)
            {
                transform.TranslateY = 0;
            }
        };
    }

    private static IconView? FindIcon(object? content)
    {
        if (content is IconView icon)
        {
            return icon;
        }

        if (content is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (FindIcon(child) is { } nested)
                {
                    return nested;
                }
            }
        }

        if (content is Border border)
        {
            return FindIcon(border.Child);
        }

        return null;
    }

    private static void AnimateTransform(DependencyObject target, string property, double value, double durationMs, bool autoReverse = false)
    {
        var animation = new DoubleAnimation
        {
            To = value,
            AutoReverse = autoReverse,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    /// <summary>A surface with a material from the material hierarchy. Glass has body, edge and highlight.</summary>
    public static Border Surface(UIElement? child, Material material = Material.Grounded, double? radius = null, double padding = 0)
    {
        var theme = ThemeRuntime.Current;
        var border = new Border
        {
            Child = child,
            Padding = new Thickness(padding),
            CornerRadius = new CornerRadius(radius ?? theme.Tokens.Number("radiusCard", 12)),
            BorderThickness = new Thickness(theme.Tokens.Number("borderWeight", 1)),
        };
        var projectedBorder = border.BorderThickness;
        theme.TrackThemeElement(border, surface =>
        {
            if (surface.BorderThickness == projectedBorder)
            {
                projectedBorder = new Thickness(ThemeRuntime.Current.Tokens.Number("borderWeight", 1));
                surface.BorderThickness = projectedBorder;
            }
            if (radius is null) surface.CornerRadius = new CornerRadius(ThemeRuntime.Current.Tokens.Number("radiusCard", 12));
        });

        switch (material)
        {
            case Material.Raised:
                border.Background = theme.Brush("surface2");
                border.BorderBrush = theme.Brush("transparent");
                break;
            case Material.Frost:
                border.Background = theme.Brush("glassFill");
                border.BorderBrush = theme.Brush("glassBorder");
                break;
            case Material.Deep:
                border.Background = theme.Brush("glassDeepFill");
                border.BorderBrush = theme.Brush("glassBorder");
                break;
            case Material.Glass:
                border.Background = theme.GlassBrush();
                border.BorderBrush = theme.Brush("glassBorder");
                break;
            default:
                border.Background = theme.Brush("surface1");
                border.BorderBrush = theme.Brush("transparent");
                break;
        }

        return border;
    }

    public static Border Guidance(string title, string text, string tone = "accent")
    {
        var theme = ThemeRuntime.Current;
        var warning = string.Equals(tone, "warning", StringComparison.Ordinal);
        var indicator = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(theme.Tokens.Number("radiusSmall", 4)),
            Background = theme.Brush(warning ? "warning" : "accent"),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var content = V(
            4,
            Text(title, "body-strong", warning ? "textWarning" : "textPrimary"),
            WrappedText(text, "caption", "textSecondary"));
        var grid = Grid("auto", "auto,*",
            indicator.At(0, 0),
            content.Margin(10, 0, 0, 0).At(0, 1));

        return new Border
        {
            Child = grid,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(theme.Tokens.Number("radiusControl", 8)),
            Background = theme.Brush("surface2"),
            BorderBrush = theme.Brush("transparent"),
            BorderThickness = new Thickness(theme.Tokens.Number("borderWeight", 1)),
        };
    }

    public static ScrollViewer Scroll(UIElement content, bool horizontal = false, double? maxHeight = null)
    {
        var gutter = ThemeRuntime.Current.Tokens.Number("scrollbarContentGutter", 16);
        var scroll = new ScrollViewer
        {
            Content = content,
            Padding = horizontal ? new Thickness(0, 0, 0, gutter) : new Thickness(0, 0, gutter, 0),
            HorizontalScrollMode = horizontal ? ScrollMode.Enabled : ScrollMode.Disabled,
            HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalScrollMode = horizontal ? ScrollMode.Disabled : ScrollMode.Enabled,
            VerticalScrollBarVisibility = horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
        };
        if (maxHeight is { } height)
        {
            scroll.MaxHeight = height;
        }

        ScrollInteraction.Attach(scroll);
        scroll.Loaded += (_, _) => ThemeRuntime.Current.TrackNativeControls(scroll);
        return scroll;
    }

    public static Border Divider()
    {
        return new Border { Height = 1, Background = ThemeRuntime.Current.Brush("borderSubtle"), Margin = new Thickness(0, 4, 0, 4) };
    }

    public static FrameworkElement Chip(string text, bool selected = false, Action? onClick = null)
    {
        var theme = ThemeRuntime.Current;
        var content = Text(text, "control", selected ? "textOnAccent" : "textPrimary")
            .Align(HorizontalAlignment.Center, VerticalAlignment.Center);

        if (onClick is null)
        {
            var passive = new Border
            {
                Child = content,
                Padding = new Thickness(8, 3, 8, 3),
                MinHeight = theme.Tokens.Number("interactiveChipHeight", 30),
                CornerRadius = new CornerRadius(theme.Tokens.Number("radiusControl", 8)),
                Background = selected ? theme.Brush("accent") : theme.Brush("surface3"),
                BorderBrush = selected ? theme.Brush("accent") : theme.Brush("borderSubtle"),
                BorderThickness = new Thickness(1),
            };
            theme.TrackControlBorder(passive, ApplyChip);
            return passive;

            void ApplyChip(Border value)
            {
                var skin = ThemeRuntime.Current.ControlFamily("chip");
                value.CornerRadius = new CornerRadius(skin.Radius);
                value.Padding = new Thickness(skin.HorizontalPadding, skin.VerticalPadding, skin.HorizontalPadding, skin.VerticalPadding);
                value.MinHeight = skin.Height;
                value.BorderThickness = new Thickness(skin.BorderWidth);
                if (!selected)
                {
                    value.Background = ThemeRuntime.Current.Brush(skin.Fill);
                    value.BorderBrush = ThemeRuntime.Current.Brush(skin.Border);
                }
            }
        }

        // Actionable chips are real controls so Tab/Enter/Space, focus visuals and automation
        // semantics work without maintaining a second pointer-only interaction path.
        var chip = new Button
        {
            Content = content,
            Padding = new Thickness(8, 3, 8, 3),
            MinHeight = theme.Tokens.Number("interactiveChipHeight", 30),
            CornerRadius = new CornerRadius(theme.Tokens.Number("radiusControl", 8)),
            Background = selected ? theme.Brush("accent") : theme.Brush("surface3"),
            BorderBrush = selected ? theme.Brush("accent") : theme.Brush("borderSubtle"),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, text);
        chip.Click += (_, _) => onClick();
        theme.TrackControlFamily(chip, "chip", !selected, kind: selected ? ButtonKind.Primary : ButtonKind.Secondary);
        return chip;
    }

    public static Border Badge(string text, string tone = "neutral")
    {
        var theme = ThemeRuntime.Current;
        var (background, foreground) = tone switch
        {
            "accent" => ("accent", "textOnAccent"),
            "warning" => ("warning", "textInverse"),
            "danger" => ("danger", "textOnAccent"),
            "success" => ("success", "textInverse"),
            "glass" => ("glassStrip", "onMediaPrimary"),
            _ => ("surface3", "textSecondary"),
        };
        return new Border
        {
            Child = Text(text, "badge", foreground)
                .Align(HorizontalAlignment.Center, VerticalAlignment.Center),
            Padding = new Thickness(6, 1, 6, 1),
            MinHeight = theme.Tokens.Number("badgeHeight", 18),
            CornerRadius = new CornerRadius(4),
            Background = theme.Brush(background),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static TextBox Input(string placeholder, string? text = null, Action<string>? changed = null)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            Text = text ?? string.Empty,
            MinHeight = ThemeRuntime.Current.Tokens.Number("densityControlHeight", 32),
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(ThemeRuntime.Current.Tokens.Number("radiusControl", 8)),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, placeholder);
        ThemeRuntime.Current.TrackControlFamily(box, "input");
        if (changed is not null)
        {
            box.TextChanged += (_, _) => changed(box.Text);
        }

        return box;
    }

    /// <summary>Section header used by quiet utility surfaces.</summary>
    public static StackPanel Section(string title, string? description = null, params UIElement?[] content)
    {
        var header = V(2, Text(title, "section-title"), description is null ? null : Text(description, "body-muted"));
        var panel = V(6, header);
        foreach (var item in content)
        {
            if (item is not null)
            {
                panel.Children.Add(item);
            }
        }

        return panel;
    }

    /// <summary>Responsive page padding keeps 100% scale information-dense without crowding narrow windows.</summary>
    public static double PagePadding(double width) => width < 640 ? 10 : width < 1200 ? 14 : 18;

    public static SolidColorBrush Solid(ArgbColor color) => new(Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B));

    public static Windows.UI.Color ToColor(this ArgbColor color) => Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);
}
