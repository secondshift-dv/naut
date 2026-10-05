using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Presentation;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Resources;

namespace Neuterradise.App.Ui;

/// <summary>
/// The Uno projection of the canonical token authority. One mutable brush per semantic token is shared
/// by every element, so a theme change recolours the live tree in place; nothing is rebuilt. Typography
/// and icon changes re-apply through weak registries. This is the only place Uno resources are written.
/// </summary>
public sealed class ThemeRuntime
{
    private static ThemeRuntime _current = new();

    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<TextBlock, TypeRegistration> _typed = new();
    private readonly List<WeakReference<TextBlock>> _typedList = [];
    private LinearGradientBrush? _glass;
    private readonly Dictionary<string, FontFamily> _fonts = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<Control, Action<Control>> _controls = new();
    private readonly List<WeakReference<Control>> _controlList = [];
    private readonly ConditionalWeakTable<Border, Action<Border>> _controlBorders = new();
    private readonly List<WeakReference<Border>> _controlBorderList = [];
    public ControlSkinPlan? ControlSkin { get; private set; }
    public ControlSkinPlan ControlFamily(string family) => (ControlSkin ?? new ControlSkinPlan(
        Tokens.Number("radiusControl", 8), Tokens.Number("borderWeight", 1), 9, 4,
        Tokens.Number("densityControlHeight", 30), 8, "token:surface3", "token:surfaceHover",
        "token:surfacePressed", "token:borderDefault", "token:focus")).For(family);

    public void TrackControlFamily(Control control, string family, bool neutral = true, bool compact = false, ButtonKind kind = ButtonKind.Secondary, ControlSkinPlan? preview = null)
    {
        TrackControl(control, value =>
        {
            var skin = preview?.For(family) ?? ControlFamily(family);
            value.CornerRadius = new CornerRadius(skin.Radius);
            value.BorderThickness = new Thickness(skin.BorderWidth);
            value.FocusVisualPrimaryBrush = Brush(skin.Focus);
            if (!compact)
            {
                value.MinHeight = skin.Height;
                if (family is not ("slider" or "toggle" or "scrollbar"))
                    value.Padding = new Thickness(skin.HorizontalPadding, skin.VerticalPadding, skin.HorizontalPadding, skin.VerticalPadding);
            }
            value.FontFamily = Font("body");
            if (family == "slider")
            {
                value.Resources["SliderTrackFill"] = Brush(skin.Fill);
                value.Resources["SliderThumbBorderBrush"] = Brush(skin.Border);
                value.Resources["SliderThumbCornerRadius"] = new CornerRadius(skin.Radius);
                value.Resources["SliderThumbWidth"] = Math.Clamp(skin.Radius * 2 + 8, 12, 24);
                value.Resources["SliderThumbHeight"] = Math.Clamp(skin.Radius * 2 + 8, 12, 24);
            }
            if (family == "toggle")
            {
                foreach (var (key, reference) in new[]
                {
                    ("ToggleButtonBackground", skin.Fill), ("ToggleButtonBackgroundPointerOver", skin.HoverFill),
                    ("ToggleButtonBackgroundPressed", skin.PressedFill), ("ToggleButtonBorderBrush", skin.Border),
                    ("ToggleButtonBackgroundChecked", "accent"), ("ToggleButtonBackgroundCheckedPointerOver", "accentHover"),
                    ("ToggleButtonBackgroundCheckedPressed", "accentPressed"), ("ToggleButtonForegroundChecked", "textOnAccent"),
                    ("ToggleButtonForegroundCheckedPointerOver", "textOnAccent"), ("ToggleButtonForegroundCheckedPressed", "textOnAccent"),
                    ("ToggleSwitchFillOff", skin.Fill), ("ToggleSwitchFillOffPointerOver", skin.HoverFill),
                    ("ToggleSwitchFillOffPressed", skin.PressedFill), ("ToggleSwitchStrokeOff", skin.Border),
                }) value.Resources[key] = Brush(reference);
            }
            if (neutral && family is not ("slider" or "toggle" or "scrollbar"))
            {
                value.Background = Brush(skin.Fill);
                value.BorderBrush = Brush(skin.Border);
            }
            if (value is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
            {
                var primary = kind == ButtonKind.Primary;
                var danger = kind == ButtonKind.Destructive;
                var ghost = kind == ButtonKind.Ghost;
                value.Resources["ButtonBackground"] = Brush(primary ? "accent" : ghost ? "transparent" : skin.Fill);
                value.Resources["ButtonBackgroundPointerOver"] = Brush(primary ? "accentHover" : skin.HoverFill);
                value.Resources["ButtonBackgroundPressed"] = Brush(primary ? "accentPressed" : skin.PressedFill);
                value.Resources["ButtonBorderBrush"] = Brush(primary ? "accent" : danger ? "danger" : ghost ? "transparent" : skin.Border);
                value.Resources["ButtonForeground"] = Brush(primary ? "textOnAccent" : danger ? "textDanger" : "textPrimary");
                value.Resources["ButtonForegroundPointerOver"] = value.Resources["ButtonForeground"];
                value.Resources["ButtonForegroundPressed"] = value.Resources["ButtonForeground"];
                value.Resources["ButtonBorderBrushPointerOver"] = value.Resources["ButtonBorderBrush"];
                value.Resources["ButtonBorderBrushPressed"] = value.Resources["ButtonBorderBrush"];
            }
        });
    }

    public void TrackControlBorder(Border border, Action<Border> apply)
    {
        if (!_controlBorders.TryGetValue(border, out _)) _controlBorderList.Add(new WeakReference<Border>(border));
        _controlBorders.AddOrUpdate(border, apply);
        apply(border);
    }

    public void TrackNativeControls(DependencyObject root)
    {
        if (root is Control control && !_controls.TryGetValue(control, out _))
        {
            var family = control switch
            {
                TextBox or ComboBox or AutoSuggestBox => "input",
                ToggleSwitch or Microsoft.UI.Xaml.Controls.Primitives.ToggleButton => "toggle",
                Slider => "slider",
                _ => null,
            };
            if (family is not null) TrackControlFamily(control, family);
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            TrackNativeControls(VisualTreeHelper.GetChild(root, index));
    }
    public double ControlRadius => ControlSkin?.Radius ?? Tokens.Number("radiusControl", 8);
    public double ControlHeight => ControlSkin?.Height ?? Tokens.Number("densityControlHeight", 30);
    public double ControlBorderWidth => ControlSkin?.BorderWidth ?? Tokens.Number("borderWeight", 1);
    public Thickness ControlPadding => new(ControlSkin?.HorizontalPadding ?? 9, ControlSkin?.VerticalPadding ?? 4,
        ControlSkin?.HorizontalPadding ?? 9, ControlSkin?.VerticalPadding ?? 4);
    public void TrackControl(Control control, Action<Control> apply)
    {
        if (!_controls.TryGetValue(control, out _)) _controlList.Add(new WeakReference<Control>(control));
        _controls.AddOrUpdate(control, apply);
        apply(control);
    }

    public ThemeRuntime()
    {
        var theme = new ThemeDefinition(1, "naut-startup", "naut", false,
            new ThemeColors(
                "#FFFDF8F3", "#FFFFFFFF", "#FFF4EFEF", "#FFE9E7F2", "#FFFFFFFF", "#FFFDF8F3",
                "#FFE9E7F2", "#FFD9DAEB", "#FFE2E6F8", "#FF2E3763", "#FF3F466E", "#FF646B89",
                "#FF969BB0", "#FFFDF8F3", "#FFFDF8F3", "#FF2E3763", "#202E3763", "#402E3763",
                "#802E3763", "#FF8EA1E9", "#FF8EA1E9", "#FF8EA1E9", "#FF2E3763", "#FF3F466E",
                "#FF242C52", "#FFB8A7E3", "#408EA1E9", "#608EA1E9", "#FF3F466E", "#FF287044",
                "#FF8A5B12", "#FFA93543", "#FF902B37"),
            new ThemeTypography("Segoe UI Variable Display", "Segoe UI Variable Display", "Segoe UI Variable Text", "Cascadia Mono"),
            new ThemeShape(3, 5, 8, 12, 16), new ThemeMotion(120, 180, 280, 500), new ThemeBackground(ThemeBackgroundKind.RadialGlow));
        Theme = theme;
        Material = MaterialSpec.Neutral;
        Tokens = TokenAuthority.Resolve(theme, Material, ReducedMotionAuthority.IsReduced);
        PushBrushes();
    }

    public static ThemeRuntime Current => _current;

    public static void Install(ThemeRuntime runtime) => _current = runtime;

    public event Action? Changed;

    public ThemeDefinition Theme { get; private set; }

    public MaterialSpec Material { get; private set; }

    public ResolvedTokenSet Tokens { get; private set; }

    public bool ReducedMotion => ReducedMotionAuthority.IsReduced;

    public ElementTheme NativeElementTheme => Theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;

    // ---------------------------------------------------------------- lookups

    public SolidColorBrush Brush(string token)
    {
        if (!_brushes.TryGetValue(token, out var brush))
        {
            brush = new SolidColorBrush(ResolveColor(token));
            _brushes[token] = brush;
        }

        return brush;
    }

    public Windows.UI.Color Color(string token) => ResolveColor(token);

    public ArgbColor Argb(string reference) => reference.StartsWith('#') ? Tokens.Resolve(reference) : Tokens.Resolve("token:" + reference);

    /// <summary>Glass body: tint with a subtle top highlight .</summary>
    public Brush GlassBrush()
    {
        if (_glass is null)
        {
            _glass = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
            _glass.GradientStops.Add(new GradientStop { Offset = 0 });
            _glass.GradientStops.Add(new GradientStop { Offset = 0.35 });
            _glass.GradientStops.Add(new GradientStop { Offset = 1 });
            UpdateGlass();
        }

        return _glass;
    }

    public double Duration(string kind) => Tokens.Number(kind switch
    {
        "fast" => "durationFast",
        "slow" => "durationSlow",
        "cinematic" => "durationCinematic",
        _ => "durationNormal",
    });

    public FontFamily Font(string role)
    {
        if (_fonts.TryGetValue(role, out var selected)) return selected;
        var token = role switch
        {
            "mono" => "fontMono",
            "display" => "fontDisplay",
            "heading" => "fontHeading",
            _ => "fontBody",
        };
        return new FontFamily(Tokens.Text(token));
    }

    public void ApplyType(TextBlock block, string role, string? colorToken = null)
    {
        var style = TokenAuthority.Foundations.TypeStyles.TryGetValue(role, out var s)
            ? s
            : TokenAuthority.Foundations.TypeStyles["body"];
        block.FontFamily = Font(style.FontRole);
        block.FontSize = style.Size;
        block.LineHeight = style.LineHeight;
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        block.FontWeight = new Windows.UI.Text.FontWeight((ushort)style.Weight);
        block.Foreground = Brush(colorToken ?? style.ColorRole);
        if (style.Trim)
        {
            block.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        if (style.Wrap)
        {
            block.TextWrapping = TextWrapping.WrapWholeWords;
        }

        if (!_typed.TryGetValue(block, out _))
        {
            _typed.Add(block, new TypeRegistration(role, colorToken));
            _typedList.Add(new WeakReference<TextBlock>(block));
        }
        else
        {
            _typed.AddOrUpdate(block, new TypeRegistration(role, colorToken));
        }
    }

    // ---------------------------------------------------------------- application

    /// <summary>Applies a resolved presentation (called on start, Apply, and during live preview).</summary>
    public void Apply(ThemePlan theme, TypographyPlan? typography = null, ControlSkinPlan? controls = null)
    {
        Theme = theme.Theme;
        Material = theme.Material;
        Tokens = TokenAuthority.Resolve(Theme, Material, ReducedMotion);
        ControlSkin = controls;
        _fonts.Clear();
        LocalPresentationFonts.Current.BeginSelection();
        if (typography is not null)
        {
            foreach (var (role, face) in new[] { ("display", typography.Display), ("heading", typography.Heading), ("body", typography.Body), ("mono", typography.Mono) })
                _fonts[role] = new FontFamily(LocalPresentationFonts.Current.Register(face));
        }
        PushBrushes();
        ReapplyTypography();
        RefreshThemeElements();
        _controlList.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in _controlList)
            if (reference.TryGetTarget(out var control) && _controls.TryGetValue(control, out var apply)) apply(control);
        _controlBorderList.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in _controlBorderList)
            if (reference.TryGetTarget(out var border) && _controlBorders.TryGetValue(border, out var apply)) apply(border);
        Changed?.Invoke();
    }

    public void RefreshMotion()
    {
        Tokens = TokenAuthority.Resolve(Theme, Material, ReducedMotion);
        ResourceGovernor.Shared.SetReducedMotion(ReducedMotion);
        Changed?.Invoke();
    }

    private void ReapplyTypography()
    {
        _typedList.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in _typedList)
        {
            if (reference.TryGetTarget(out var block) && _typed.TryGetValue(block, out var registration))
            {
                ApplyType(block, registration.Role, registration.Color);
            }
        }
    }

    private Windows.UI.Color ResolveColor(string token)
    {
        if (token.StartsWith("token:", StringComparison.Ordinal)) token = token[6..];
        if (token == "transparent")
        {
            return Windows.UI.Color.FromArgb(0, 0, 0, 0);
        }

        var color = token.StartsWith('#') ? Tokens.Resolve(token) : Tokens.Color(token);
        return Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private void PushBrushes()
    {
        foreach (var (token, brush) in _brushes)
        {
            brush.Color = ResolveColor(token);
        }

        UpdateGlass();
        PushControlResources();
    }

    private void UpdateGlass()
    {
        if (_glass is null)
        {
            return;
        }

        _glass.GradientStops[0].Color = Blend("glassFill", "glassHighlight", 0.5);
        _glass.GradientStops[1].Color = Color("glassFill");
        _glass.GradientStops[2].Color = Color("glassDeepFill");
    }

    private Windows.UI.Color Blend(string a, string b, double t)
    {
        var blended = ArgbColor.Lerp(Tokens.Color(a), Tokens.Color(b), t);
        return Windows.UI.Color.FromArgb(blended.A, blended.R, blended.G, blended.B);
    }

    /// <summary>WinUI lightweight styling: every stock control picks up the token brushes.</summary>
    private void PushControlResources()
    {
        if (Application.Current?.Resources is not { } resources)
        {
            return;
        }

        void Set(string key, string token) => resources[key] = Brush(token);

        try
        {
            Set("ApplicationPageBackgroundThemeBrush", "canvas");
            Set("ButtonBackground", "surface3");
            Set("ButtonBackgroundPointerOver", "surfaceHover");
            Set("ButtonBackgroundPressed", "surfacePressed");
            Set("ButtonBackgroundDisabled", "surface2");
            Set("ButtonForeground", "textPrimary");
            Set("ButtonForegroundPointerOver", "textPrimary");
            Set("ButtonForegroundPressed", "textPrimary");
            Set("ButtonForegroundDisabled", "textDisabled");
            Set("ButtonBorderBrush", "borderDefault");
            Set("ButtonBorderBrushPointerOver", "borderInteractive");
            Set("ButtonBorderBrushPressed", "borderStrong");
            Set("ButtonBorderBrushDisabled", "borderSubtle");
            Set("AccentButtonBackground", "accent");
            Set("AccentButtonBackgroundPointerOver", "accentHover");
            Set("AccentButtonBackgroundPressed", "accentPressed");
            Set("AccentButtonBackgroundDisabled", "surface2");
            Set("AccentButtonForeground", "textOnAccent");
            Set("AccentButtonForegroundPointerOver", "textOnAccent");
            Set("AccentButtonForegroundPressed", "textOnAccent");
            Set("AccentButtonForegroundDisabled", "textDisabled");
            Set("TextControlBackground", "surface2");
            Set("TextControlBackgroundPointerOver", "surface3");
            Set("TextControlBackgroundFocused", "surface2");
            Set("TextControlBackgroundDisabled", "surface2");
            Set("TextControlForeground", "textPrimary");
            Set("TextControlForegroundPointerOver", "textPrimary");
            Set("TextControlForegroundFocused", "textPrimary");
            Set("TextControlForegroundDisabled", "textDisabled");
            Set("TextControlBorderBrush", "borderDefault");
            Set("TextControlBorderBrushPointerOver", "borderInteractive");
            Set("TextControlBorderBrushFocused", "focus");
            Set("TextControlBorderBrushDisabled", "borderSubtle");
            Set("TextControlPlaceholderForeground", "textMuted");
            Set("TextControlPlaceholderForegroundFocused", "textMuted");
            Set("TextControlPlaceholderForegroundPointerOver", "textMuted");
            Set("TextControlPlaceholderForegroundDisabled", "textDisabled");
            Set("ComboBoxBackground", "surface2");
            Set("ComboBoxBackgroundPointerOver", "surface3");
            Set("ComboBoxBackgroundPressed", "surfacePressed");
            Set("ComboBoxBackgroundDisabled", "surface2");
            Set("ComboBoxForeground", "textPrimary");
            Set("ComboBoxForegroundPointerOver", "textPrimary");
            Set("ComboBoxForegroundPressed", "textPrimary");
            Set("ComboBoxForegroundDisabled", "textDisabled");
            Set("ComboBoxBorderBrush", "borderDefault");
            Set("ComboBoxBorderBrushPointerOver", "borderInteractive");
            Set("ComboBoxBorderBrushPressed", "borderStrong");
            Set("ComboBoxBorderBrushDisabled", "borderSubtle");
            Set("ComboBoxDropDownBackground", "surfaceOverlay");
            Set("ComboBoxDropDownBorderBrush", "borderDefault");
            Set("ComboBoxItemForeground", "textPrimary");
            Set("ComboBoxItemForegroundPointerOver", "textPrimary");
            Set("ComboBoxItemForegroundPressed", "textPrimary");
            Set("ComboBoxItemForegroundSelected", "textPrimary");
            Set("ComboBoxItemForegroundSelectedPointerOver", "textPrimary");
            Set("ComboBoxItemForegroundSelectedPressed", "textPrimary");
            Set("ComboBoxItemForegroundDisabled", "textDisabled");
            Set("ComboBoxItemBackgroundPointerOver", "surfaceHover");
            Set("ComboBoxItemBackgroundPressed", "surfacePressed");
            Set("ComboBoxItemBackgroundSelected", "surfaceSelected");
            Set("ComboBoxItemBackgroundSelectedPointerOver", "surfaceHover");
            Set("ComboBoxItemBackgroundSelectedPressed", "surfacePressed");
            Set("ToggleSwitchFillOff", "surface3");
            Set("ToggleSwitchFillOffPointerOver", "surfaceHover");
            Set("ToggleSwitchFillOffPressed", "surfacePressed");
            Set("ToggleSwitchFillOffDisabled", "surface2");
            Set("ToggleSwitchFillOn", "accent");
            Set("ToggleSwitchFillOnPointerOver", "accentHover");
            Set("ToggleSwitchFillOnPressed", "accentPressed");
            Set("ToggleSwitchFillOnDisabled", "surface2");
            Set("ToggleSwitchStrokeOff", "borderStrong");
            Set("ToggleSwitchStrokeOffPointerOver", "borderInteractive");
            Set("ToggleSwitchStrokeOffPressed", "borderStrong");
            Set("ToggleSwitchStrokeOffDisabled", "borderSubtle");
            Set("ToggleSwitchKnobFillOff", "textSecondary");
            Set("ToggleSwitchKnobFillOffPointerOver", "textPrimary");
            Set("ToggleSwitchKnobFillOffPressed", "textPrimary");
            Set("ToggleSwitchKnobFillOffDisabled", "textDisabled");
            Set("ToggleSwitchKnobFillOn", "textOnAccent");
            Set("ToggleSwitchKnobFillOnPointerOver", "textOnAccent");
            Set("ToggleSwitchKnobFillOnPressed", "textOnAccent");
            Set("ToggleSwitchKnobFillOnDisabled", "textDisabled");
            Set("ToggleSwitchContentForeground", "textPrimary");
            Set("ToggleSwitchContentForegroundPointerOver", "textPrimary");
            Set("ToggleSwitchContentForegroundDisabled", "textDisabled");
            Set("SliderTrackValueFill", "accent");
            Set("SliderTrackValueFillPointerOver", "accentHover");
            Set("SliderTrackFill", "surface3");
            Set("SliderThumbBackground", "accent");
            Set("SliderThumbBackgroundPointerOver", "accentHover");
            Set("CheckBoxForegroundUnchecked", "textPrimary");
            Set("CheckBoxForegroundChecked", "textPrimary");
            Set("CheckBoxCheckBackgroundFillChecked", "accent");
            Set("CheckBoxCheckBackgroundStrokeUnchecked", "borderStrong");
            Set("RadioButtonForeground", "textPrimary");
            Set("RadioButtonOuterEllipseCheckedFill", "accent");
            Set("RadioButtonOuterEllipseCheckedStroke", "accent");
            Set("ScrollBarThumbFill", "borderStrong");
            Set("ScrollBarThumbFillPointerOver", "textMuted");
            Set("ScrollBarThumbFillPressed", "textSecondary");
            Set("ToolTipBackground", "surfaceOverlay");
            Set("ToolTipForeground", "textPrimary");
            Set("ToolTipBorderBrush", "borderDefault");
            Set("ProgressBarForeground", "accent");
            Set("ProgressBarBackground", "surface3");
            Set("ProgressRingForeground", "accent");
            Set("FlyoutPresenterBackground", "surfaceOverlay");
            Set("MenuFlyoutPresenterBackground", "surfaceOverlay");
            Set("MenuFlyoutItemForeground", "textPrimary");
            Set("MenuFlyoutItemBackgroundPointerOver", "surfaceHover");
            Set("ListViewItemBackgroundPointerOver", "surfaceHover");
            Set("ListViewItemBackgroundPressed", "surfacePressed");
            Set("ListViewItemBackgroundSelected", "surfaceSelected");
            Set("ListViewItemBackgroundSelectedPointerOver", "surfaceHover");
            Set("ListViewItemBackgroundSelectedPressed", "surfacePressed");
            Set("ListViewItemForeground", "textPrimary");
            Set("ListViewItemForegroundPointerOver", "textPrimary");
            Set("ListViewItemForegroundSelected", "textPrimary");
            Set("ListViewItemForegroundSelectedPointerOver", "textPrimary");
            Set("ListViewItemForegroundDisabled", "textDisabled");
            Set("TextFillColorPrimaryBrush", "textPrimary");
            Set("TextFillColorSecondaryBrush", "textSecondary");
            Set("TextFillColorDisabledBrush", "textDisabled");
            Set("FocusStrokeColorOuterBrush", "focus");
            Set("SystemControlFocusVisualPrimaryBrush", "focus");
            resources["CompactControlHeight"] = Tokens.Number("densityControlHeight", 28);
            resources["CompactControlPadding"] = new Thickness(
                Tokens.Number("densityControlPaddingHorizontal", 9),
                Tokens.Number("densityControlPaddingVertical", 3),
                Tokens.Number("densityControlPaddingHorizontal", 9),
                Tokens.Number("densityControlPaddingVertical", 3));
            resources["CompactControlFontSize"] = TokenAuthority.Foundations.TypeStyles["control"].Size;
            resources["CompactControlFontFamily"] = Font("body");
            resources["ControlCornerRadius"] = new CornerRadius(Tokens.Number("radiusControl", 8));
            resources["OverlayCornerRadius"] = new CornerRadius(Tokens.Number("radiusCard", 12));
            foreach (var family in new[] { "button", "icon-button", "chip", "tab", "input", "toggle", "slider", "scrollbar" })
            {
                var skin = ControlFamily(family);
                var key = "ControlSet." + family + ".";
                resources[key + "Height"] = skin.Height;
                resources[key + "Padding"] = new Thickness(skin.HorizontalPadding, skin.VerticalPadding, skin.HorizontalPadding, skin.VerticalPadding);
                resources[key + "Radius"] = new CornerRadius(skin.Radius);
                resources[key + "BorderWidth"] = new Thickness(skin.BorderWidth);
                resources[key + "Focus"] = Brush(skin.Focus);
                foreach (var prefix in family switch
                {
                    "button" => new[] { "Button" },
                    "input" => new[] { "TextControl", "ComboBox" },
                    "toggle" => new[] { "ToggleButton" },
                    _ => Array.Empty<string>(),
                })
                {
                    resources[prefix + "Background"] = Brush(skin.Fill);
                    resources[prefix + "BackgroundPointerOver"] = Brush(skin.HoverFill);
                    resources[prefix + "BackgroundPressed"] = Brush(skin.PressedFill);
                    resources[prefix + "BorderBrush"] = Brush(skin.Border);
                    if (family == "input")
                    {
                        resources[prefix + "BorderBrushFocused"] = Brush(skin.Focus);
                        resources[prefix + "BackgroundFocused"] = Brush(skin.Fill);
                    }
                }
            }
            var scrollbar = ControlFamily("scrollbar");
            resources["ScrollBarThumbMinWidth"] = scrollbar.ScrollbarWidth;
            resources["ScrollBarThumbMinHeight"] = scrollbar.ScrollbarWidth;
            resources["ScrollBarMinWidth"] = scrollbar.ScrollbarWidth;
            resources["ScrollBarMinHeight"] = scrollbar.ScrollbarWidth;
            resources["ScrollBarThumbFill"] = Brush(scrollbar.Border);
            resources["ScrollBarThumbFillPointerOver"] = Brush(scrollbar.HoverFill);
            resources["ScrollBarThumbFillPressed"] = Brush(scrollbar.PressedFill);
            resources["SliderTrackFill"] = Brush(ControlFamily("slider").Fill);
            resources["SliderThumbCornerRadius"] = new CornerRadius(ControlFamily("slider").Radius);
            resources["ToggleSwitchFillOff"] = Brush(ControlFamily("toggle").Fill);
            resources["ToggleSwitchFillOffPointerOver"] = Brush(ControlFamily("toggle").HoverFill);
            resources["ToggleSwitchStrokeOff"] = Brush(ControlFamily("toggle").Border);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Control resource projection failed: {0}", exception.GetType().Name);
        }
    }

    private readonly ConditionalWeakTable<FrameworkElement, Action<FrameworkElement>> _themeElements = new();
    private readonly List<WeakReference<FrameworkElement>> _themeElementList = [];

    public void TrackThemeElement<T>(T element, Action<T> apply) where T : FrameworkElement
    {
        if (!_themeElements.TryGetValue(element, out _)) _themeElementList.Add(new WeakReference<FrameworkElement>(element));
        _themeElements.AddOrUpdate(element, value => apply((T)value));
        apply(element);
    }

    private void RefreshThemeElements()
    {
        _themeElementList.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in _themeElementList)
            if (reference.TryGetTarget(out var element) && _themeElements.TryGetValue(element, out var apply)) apply(element);
    }

    private sealed record TypeRegistration(string Role, string? Color);
}
