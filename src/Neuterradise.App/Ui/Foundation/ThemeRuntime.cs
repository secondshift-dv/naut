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
    private int _appearanceRevision;
    private LinearGradientBrush? _glass;
    private readonly Dictionary<string, FontFamily> _fonts = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<Control, Action<Control>> _controls = new();
    private readonly ConditionalWeakTable<Border, Action<Border>> _controlBorders = new();
    public ControlSkinPlan? ControlSkin { get; private set; }
    private TypographyPlan? _typography;
    public TypeStyle TypeStyle(string role) =>
        _typography?.Styles.GetValueOrDefault(role)
        ?? TokenAuthority.Foundations.TypeStyles.GetValueOrDefault(role)
        ?? TokenAuthority.Foundations.TypeStyles["body"];
    public ControlSkinPlan ControlFamily(string family) => (ControlSkin ?? new ControlSkinPlan(
        Tokens.Number("radiusControl", 8), Tokens.Number("borderWeight", 1), 9, 4,
        Tokens.Number("densityControlHeight", 30), 8, "token:surface3", "token:surfaceHover",
        "token:surfacePressed", "token:borderDefault", "token:focus")).For(family);

    public void TrackControlFamily(Control control, string family, bool neutral = true, bool compact = false, ButtonKind kind = ButtonKind.Secondary, ControlSkinPlan? preview = null)
    {
        var originalWidth = control.Width;
        var originalHeight = control.Height;
        var originalIconSize = control is Button { Content: IconView initialIcon } ? initialIcon.Width : double.NaN;
        var originalWeight = control.FontWeight;
        var inputPaper = false;
        TrackControl(control, value =>
        {
            var skin = preview?.For(family) ?? ControlFamily(family);
            SolidColorBrush B(string reference) => ScopedBrush(value, reference);
            Brush F(ControlSkinPlan plan, string reference, bool pressed = false) =>
                PaperFor(value) is not null ? B(reference) : ControlFill(plan, reference, pressed);

            value.CornerRadius = new CornerRadius(skin.Radius);
            value.BorderThickness = ControlEdge(skin);
            value.FocusVisualPrimaryBrush = B(skin.Focus);
            value.FocusVisualPrimaryThickness = new Thickness(Math.Max(2, skin.BorderWidth));
            foreach (var prefix in new[] { "Button", "ToggleButton", "TextControl", "ComboBox" })
            {
                value.Resources[prefix + "BackgroundDisabled"] = B("surface2");
                value.Resources[prefix + "ForegroundDisabled"] = B("textDisabled");
                value.Resources[prefix + "BorderBrushDisabled"] = B("borderSubtle");
            }
            if (family == "input")
            {
                if (PaperFor(value) is not null || inputPaper)
                {
                    value.Foreground = B("textPrimary");
                    value.Resources["TextControlForeground"] = B("textPrimary");
                    value.Resources["TextControlForegroundPointerOver"] = B("textPrimary");
                    value.Resources["TextControlForegroundFocused"] = B("textPrimary");
                    value.Resources["ComboBoxForeground"] = B("textPrimary");
                    inputPaper = PaperFor(value) is not null;
                }
                foreach (var prefix in new[] { "TextControl", "ComboBox" })
                {
                    value.Resources[prefix + "Background"] = F(skin, skin.Fill);
                    value.Resources[prefix + "BackgroundPointerOver"] = F(skin, skin.HoverFill);
                    value.Resources[prefix + "BackgroundFocused"] = F(skin, skin.Fill);
                    value.Resources[prefix + "BorderBrushFocused"] = B(skin.Focus);
                    value.Resources[prefix + "BorderBrushPointerOver"] = B(skin.Focus);
                }
            }
            if (!compact)
            {
                value.MinHeight = skin.Height;
                if (family is not ("slider" or "toggle" or "scrollbar"))
                    value.Padding = new Thickness(skin.HorizontalPadding, skin.VerticalPadding, skin.HorizontalPadding, skin.VerticalPadding);
            }
            var type = TypeStyle("control");
            value.FontFamily = Font(type.FontRole);
            value.FontSize = type.Size;
            value.FontWeight = _typography?.Styles.ContainsKey("control") == true
                ? new Windows.UI.Text.FontWeight((ushort)type.Weight) : originalWeight;
            if (compact && value is Button { Content: IconView icon } && double.IsFinite(originalIconSize))
            {
                value.Width = skin.IconButtonSize ?? originalWidth;
                value.Height = skin.IconButtonSize ?? originalHeight;
                icon.Width = icon.Height = skin.IconButtonSize is { } iconSize ? Math.Round(iconSize * 0.5) : originalIconSize;
            }
            if (family == "slider")
            {
                value.Resources["SliderTrackFill"] = F(skin, skin.Fill);
                value.Resources["SliderThumbBackground"] = F(skin, "accent");
                value.Resources["SliderThumbBackgroundPointerOver"] = F(skin, "accentHover");
                value.Resources["SliderThumbBackgroundPressed"] = F(skin, "accentPressed", pressed: true);
                value.Resources["SliderThumbBorderBrush"] = B(skin.Border);
                value.Resources["SliderThumbCornerRadius"] = new CornerRadius(skin.Radius);
                foreach (var orientation in new[] { "Horizontal", "Vertical" })
                {
                    value.Resources["Slider" + orientation + "ThumbWidth"] = skin.ThumbWidth;
                    value.Resources["Slider" + orientation + "ThumbHeight"] = skin.ThumbHeight;
                }
                value.Resources["SliderInnerThumbWidth"] = Math.Max(6, skin.ThumbWidth - 6);
                value.Resources["SliderInnerThumbHeight"] = Math.Max(6, skin.ThumbHeight - 6);
                if (value.IsLoaded) ProjectSliderGeometry(value, skin);
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
                }) value.Resources[key] = B(reference);
                foreach (var prefix in new[] { "ToggleButtonBackground", "ToggleSwitchFillOff", "ToggleSwitchFillOn" })
                {
                    var on = prefix == "ToggleSwitchFillOn";
                    value.Resources[prefix] = F(skin, on ? "accent" : skin.Fill);
                    value.Resources[prefix + "PointerOver"] = F(skin, on ? "accentHover" : skin.HoverFill);
                    value.Resources[prefix + "Pressed"] = F(skin, on ? "accentPressed" : skin.PressedFill, pressed: true);
                }
            }
            if (neutral && family is not ("slider" or "toggle" or "scrollbar"))
            {
                value.Background = F(skin, skin.Fill);
                value.BorderBrush = B(skin.Border);
            }
            if (value is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
            {
                var primary = kind == ButtonKind.Primary;
                var danger = kind == ButtonKind.Destructive;
                var ghost = kind == ButtonKind.Ghost;
                value.Background = ghost ? B("transparent") : F(skin, primary ? "accent" : skin.Fill);
                value.BorderBrush = B(primary ? "accent" : danger ? "danger" : ghost ? "transparent" : skin.Border);
                value.Resources["ButtonBackground"] = ghost ? B("transparent") : F(skin, primary ? "accent" : skin.Fill);
                value.Resources["ButtonBackgroundPointerOver"] = F(skin, primary ? "accentHover" : skin.HoverFill);
                value.Resources["ButtonBackgroundPressed"] = F(skin, primary ? "accentPressed" : skin.PressedFill, pressed: true);
                value.Resources["ButtonBorderBrush"] = B(primary ? "accent" : danger ? "danger" : ghost ? "transparent" : skin.Border);
                value.Resources["ButtonForeground"] = B(primary ? "textOnAccent" : danger ? "textDanger" : "textPrimary");
                value.Resources["ButtonForegroundPointerOver"] = value.Resources["ButtonForeground"];
                value.Resources["ButtonForegroundPressed"] = value.Resources["ButtonForeground"];
                value.Resources["ButtonBorderBrushPointerOver"] = B(primary ? "accentHover" : danger ? "danger" : skin.Focus);
                value.Resources["ButtonBorderBrushPressed"] = B(primary ? "accentPressed" : danger ? "danger" : skin.Focus);
            }
        });
    }

    public static Thickness ControlEdge(ControlSkinPlan skin) => skin.Edge switch
    {
        "bottom" => new Thickness(0, 0, 0, Math.Max(1, skin.BorderWidth)),
        "leading" => new Thickness(Math.Max(1, skin.BorderWidth), 0, 0, 0),
        _ => new Thickness(skin.BorderWidth),
    };

    private static void ProjectSliderGeometry(DependencyObject root, ControlSkinPlan skin)
    {
        // Native template dimensions bind resources once; live style changes must update the
        // existing parts without replacing the Slider or disturbing its focus/value/drag state.
        if (root is Microsoft.UI.Xaml.Controls.Primitives.Thumb thumb && thumb.Name is "HorizontalThumb" or "VerticalThumb")
        {
            thumb.Width = skin.ThumbWidth;
            thumb.Height = skin.ThumbHeight;
        }
        if (root is FrameworkElement { Name: "SliderInnerThumb" } inner)
        {
            inner.Width = Math.Max(6, skin.ThumbWidth - 6);
            inner.Height = Math.Max(6, skin.ThumbHeight - 6);
            if (inner is Border border) border.CornerRadius = new CornerRadius(skin.Radius);
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            ProjectSliderGeometry(VisualTreeHelper.GetChild(root, index), skin);
    }

    public Brush SurfaceBrush(bool raised = false)
    {
        if (Theme.EffectiveMorphology.CardPersonality == ThemeCardPersonality.Glass) return GlassBrush();
        if (Theme.EffectiveMorphology.CardPersonality == ThemeCardPersonality.Flat) return Brush(raised ? "surface2" : "surface1");
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        brush.GradientStops.Add(new GradientStop { Color = Color(raised ? "surfaceElevated" : "surface2"), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Color("surface1"), Offset = 1 });
        return brush;
    }

    public Brush ControlFill(ControlSkinPlan skin, string reference, bool pressed = false)
    {
        if (skin.Material == "flat" || skin.Relief == 0) return Brush(reference);
        var baseColor = ResolveColor(reference);
        Windows.UI.Color Mix(Windows.UI.Color target, double amount) => Windows.UI.Color.FromArgb(baseColor.A,
            (byte)Math.Round(baseColor.R + (target.R - baseColor.R) * amount),
            (byte)Math.Round(baseColor.G + (target.G - baseColor.G) * amount),
            (byte)Math.Round(baseColor.B + (target.B - baseColor.B) * amount));
        var light = skin.Material == "luminous" ? Color("accent") : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        var high = Mix(light, skin.Relief);
        var low = Mix(Windows.UI.Color.FromArgb(255, 0, 0, 0), skin.Relief * .6);
        var inset = pressed || skin.Material == "inset";
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        brush.GradientStops.Add(new GradientStop { Color = inset ? low : high, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = baseColor, Offset = skin.Material == "bevel" ? .18 : .6 });
        brush.GradientStops.Add(new GradientStop { Color = inset ? high : low, Offset = 1 });
        return brush;
    }

    public void TrackControlBorder(Border border, Action<Border> apply)
    {
        if (!_controlBorders.TryGetValue(border, out _))
            border.Loaded += OnTrackedBorderLoaded;
        _controlBorders.AddOrUpdate(border, apply);
        apply(border);
    }

    private void OnTrackedBorderLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Border border && _controlBorders.TryGetValue(border, out var apply)) apply(border);
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
        if (!_controls.TryGetValue(control, out _))
        {
            control.Loaded += OnTrackedControlLoaded;
        }
        _controls.AddOrUpdate(control, apply);
        apply(control);
    }

    private void OnTrackedControlLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Control control && _controls.TryGetValue(control, out var apply)) apply(control);
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

    private readonly ConditionalWeakTable<IconView, object> _icons = new();
    private static readonly object IconRegistration = new();
    internal void RegisterIcon(IconView icon) => _icons.AddOrUpdate(icon, IconRegistration);
    internal void UnregisterIcon(IconView icon) => _icons.Remove(icon);

    private void NotifyChanged()
    {
        foreach (var pair in _icons)
            pair.Key.Invalidate();
        Changed?.Invoke();
    }

    public event Action? Changed;

    public ThemeDefinition Theme { get; private set; }

    public MaterialSpec Material { get; private set; }

    public ResolvedTokenSet Tokens { get; private set; }

    public bool ReducedMotion => ReducedMotionAuthority.IsReduced;

    public ElementTheme NativeElementTheme => Theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;

    // ---------------------------------------------------------------- lookups

    private readonly ConditionalWeakTable<FrameworkElement, ProfilePaperPlan> _paperScopes = new();

    private ProfilePaperPlan? PaperFor(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement root && _paperScopes.TryGetValue(root, out var paper)) return paper;
        return null;
    }

    public void SetPaperScope(FrameworkElement root, ProfilePaperPlan? paper)
    {
        _paperScopes.Remove(root);
        if (paper is not null) _paperScopes.Add(root, paper);
        Refresh(root);
        void Refresh(DependencyObject element)
        {
            if (element is TextBlock text && _typed.TryGetValue(text, out var type)) ApplyRegisteredType(text, type);
            if (element is Control control && _controls.TryGetValue(control, out var controlApply)) controlApply(control);
            if (element is FrameworkElement visual && _themeElements.TryGetValue(visual, out var themeApply)) themeApply(visual);
            if (element is IconView icon) icon.Invalidate();
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                Refresh(VisualTreeHelper.GetChild(element, i));
        }
    }

    private static string PaperReference(ProfilePaperPlan paper, string reference)
    {
        var token = reference.StartsWith("token:", StringComparison.Ordinal) ? reference[6..] : reference;
        return token switch
        {
            "canvas" or "surface1" => paper.Background,
            "surface2" or "surface3" or "surfaceElevated" => "#FFF2E8EE",
            "surfaceOverlay" or "surfaceSelected" or "surfaceHover" or "surfacePressed" => "#FFF1D7E5",
            "textPrimary" or "textInverse" => paper.Ink,
            "textSecondary" or "textMuted" => "#FF604D65",
            "textDisabled" => "#FF807484",
            "textOnAccent" => "#FFFFFFFF",
            "accent" or "accentHover" or "accentPressed" or "accentSecondary" or "textLink" or "focus" or "borderSelected" or "borderInteractive" => paper.Accent,
            "accentSoft" or "selection" or "selectionHover" => "#FFF1D7E5",
            "borderDefault" or "borderSubtle" or "borderStrong" => "#FFCCBCCB",
            "danger" or "dangerHover" or "textDanger" => "#FFBB1957",
            "warning" => "#FFB77900",
            "success" => "#FF23794C",
            "info" => "#FF256478",
            _ => reference,
        };
    }

    public SolidColorBrush ScopedBrush(DependencyObject element, string reference) =>
        Brush(PaperFor(element) is { } paper ? PaperReference(paper, reference) : reference);

    public ArgbColor ScopedArgb(DependencyObject element, string reference)
    {
        var mapped = PaperFor(element) is { } paper ? PaperReference(paper, reference) : reference;
        return Tokens.Resolve(mapped.StartsWith('#') || mapped.StartsWith("token:", StringComparison.Ordinal) ? mapped : "token:" + mapped);
    }

    public Brush ScopedControlFill(DependencyObject element, ControlSkinPlan skin, string reference) =>
        PaperFor(element) is not null ? ScopedBrush(element, reference) : ControlFill(skin, reference);

    public Brush ScopedSurfaceBrush(DependencyObject element, bool raised) =>
        PaperFor(element) is not null ? ScopedBrush(element, "canvas") : SurfaceBrush(raised);

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
        return _fonts[role] = new FontFamily(Tokens.Text(token));
    }

    public void ApplyType(TextBlock block, string role, string? colorToken = null)
    {
        if (!_typed.TryGetValue(block, out var registration))
            block.Loaded += OnTrackedTextLoaded;
        if (registration is null || registration.Role != role || registration.Color != colorToken)
            _typed.AddOrUpdate(block, registration = new TypeRegistration(role, colorToken));
        ApplyRegisteredType(block, registration);
    }

    private void OnTrackedTextLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBlock block && _typed.TryGetValue(block, out var registration)
            && (registration.AppliedRevision != _appearanceRevision || registration.Paper != PaperFor(block)))
            ApplyRegisteredType(block, registration);
    }

    private void ApplyRegisteredType(TextBlock block, TypeRegistration registration)
    {
        var style = TypeStyle(registration.Role);
        block.FontFamily = Font(style.FontRole);
        block.FontSize = style.Size;
        block.LineHeight = style.LineHeight;
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        block.FontWeight = new Windows.UI.Text.FontWeight((ushort)style.Weight);
        block.Foreground = ScopedBrush(block, registration.Color ?? style.ColorRole);
        registration.Paper = PaperFor(block);
        registration.AppliedRevision = _appearanceRevision;
        if (style.Trim)
        {
            block.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        if (style.Wrap)
        {
            block.TextWrapping = TextWrapping.WrapWholeWords;
        }

    }

    // ---------------------------------------------------------------- application

    /// <summary>Applies a resolved presentation (called on start, Apply, and during live preview).</summary>
    public void Apply(ThemePlan theme, TypographyPlan? typography = null, ControlSkinPlan? controls = null)
    {
        _appearanceRevision++;
        Theme = theme.Theme;
        Material = theme.Material;
        Tokens = TokenAuthority.Resolve(Theme, Material, ReducedMotion);
        ControlSkin = controls;
        _typography = typography;
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
        foreach (var (control, apply) in _controls)
            if (control.IsLoaded) apply(control);
        foreach (var (border, apply) in _controlBorders)
            if (border.IsLoaded) apply(border);
        NotifyChanged();
    }

    public void RefreshMotion()
    {
        Tokens = TokenAuthority.Resolve(Theme, Material, ReducedMotion);
        ResourceGovernor.Shared.SetReducedMotion(ReducedMotion);
        NotifyChanged();
    }

    private void ReapplyTypography()
    {
        // Weak registrations include detached or retired native elements. Updating those
        // dependency stores can fail; cached surfaces receive the current type on Loaded.
        foreach (var (block, registration) in _typed)
            if (block.IsLoaded) ApplyRegisteredType(block, registration);
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
            resources["CompactControlFontSize"] = TypeStyle("control").Size;
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
            resources["ScrollBarVerticalThumbMinWidth"] = Math.Max(9, scrollbar.ScrollbarWidth);
            resources["ScrollBarHorizontalThumbMinHeight"] = Math.Max(10, scrollbar.ScrollbarWidth);
            resources["ScrollBarCornerRadius"] = new CornerRadius(scrollbar.Radius);
            resources["ScrollBarThumbFill"] = ControlFill(scrollbar, "textSecondary");
            resources["ScrollBarThumbFillPointerOver"] = ControlFill(scrollbar, "textPrimary");
            resources["ScrollBarThumbFillPressed"] = ControlFill(scrollbar, "accent", pressed: true);
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

    public void TrackThemeElement<T>(T element, Action<T> apply) where T : FrameworkElement
    {
        if (!_themeElements.TryGetValue(element, out _))
            element.Loaded += OnTrackedThemeElementLoaded;
        _themeElements.AddOrUpdate(element, value => apply((T)value));
        apply(element);
    }

    private void OnTrackedThemeElementLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement element && _themeElements.TryGetValue(element, out var apply)) apply(element);
    }

    private void RefreshThemeElements()
    {
        foreach (var (element, apply) in _themeElements)
            if (element.IsLoaded) apply(element);
    }

    private sealed record TypeRegistration(string Role, string? Color)
    {
        public int AppliedRevision { get; set; } = -1;
        public ProfilePaperPlan? Paper { get; set; }
    }
}
