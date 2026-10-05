using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Neuterradise.App.Ui;

internal enum LivingNavigationRole
{
    Vault,
    Home,
    Gallery,
    Import,
    Settings,
    Customize,
}

/// <summary>
/// Shared interaction authority for Naut's six high-attention top-bar controls.
/// Each role keeps one semantic motion language, a short hover bubble burst,
/// deterministic selected/pressed states, and a Reduced Motion fallback.
/// </summary>
internal sealed class LivingNavigationButton : Button
{
    private readonly LivingNavigationRole _role;
    private readonly bool _signature;
    private readonly Grid _iconStage;
    private readonly IconView _baseIcon;
    private readonly IconView _detailIcon;
    private readonly CompositeTransform _stageTransform = new();
    private readonly CompositeTransform _baseTransform = new();
    private readonly CompositeTransform _detailTransform = new();
    private readonly Border[] _bubbles;
    private readonly CompositeTransform[] _bubbleTransforms;
    private readonly TextBlock _label;
    private readonly TextBlock? _secondaryLabel;
    private readonly Border? _vaultBadge;
    private bool _selected;
    private bool _pointerOver;
    private bool _compact;

    public LivingNavigationButton(
        LivingNavigationRole role,
        string label,
        Action onClick,
        bool signature = false,
        string? secondaryLabel = null)
    {
        _role = role;
        _signature = signature;

        var theme = ThemeRuntime.Current;
        var (baseKey, detailKey) = IconKeys(role);
        _baseIcon = new IconView(baseKey, 18, signature ? "signatureGold" : "textSecondary")
        {
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = _baseTransform,
        };
        _detailIcon = new IconView(detailKey, 18, signature ? "signatureGold" : "textMuted")
        {
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = _detailTransform,
        };

        _iconStage = new Grid
        {
            Width = 24,
            Height = 24,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = _stageTransform,
        };
        _iconStage.Children.Add(_baseIcon.Align(HorizontalAlignment.Center, VerticalAlignment.Center));
        _iconStage.Children.Add(_detailIcon.Align(HorizontalAlignment.Center, VerticalAlignment.Center));

        _bubbles =
        [
            Bubble(3.2, HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, 1, 1)),
            Bubble(2.4, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(2, 2, 0, 0)),
            Bubble(1.8, HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 5, 4, 0)),
        ];
        _bubbleTransforms = new CompositeTransform[_bubbles.Length];
        for (var index = 0; index < _bubbles.Length; index++)
        {
            var transform = new CompositeTransform();
            _bubbles[index].RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            _bubbles[index].RenderTransform = transform;
            _bubbleTransforms[index] = transform;
            _iconStage.Children.Add(_bubbles[index]);
        }

        _label = UI.Text(
            label,
            role is LivingNavigationRole.Vault ? "micro" : "control",
            role is LivingNavigationRole.Vault ? "textMuted" : signature ? "textPrimary" : "textSecondary",
            1);
        _label.VerticalAlignment = VerticalAlignment.Center;
        if (role is LivingNavigationRole.Vault && !string.IsNullOrWhiteSpace(secondaryLabel))
        {
            _secondaryLabel = UI.Text(secondaryLabel, "control", "textPrimary", 1);
            _secondaryLabel.MaxWidth = 150;
            _secondaryLabel.VerticalAlignment = VerticalAlignment.Center;
            _secondaryLabel.TextTrimming = TextTrimming.CharacterEllipsis;
            _vaultBadge = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = theme.Brush("navSelectedBackground"),
                Child = _iconStage,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Content = UI.H(8, _vaultBadge, UI.V(0, _label, _secondaryLabel))
                .Align(HorizontalAlignment.Left, VerticalAlignment.Center);
        }
        else
        {
            Content = UI.H(8, _iconStage, _label).Align(HorizontalAlignment.Center, VerticalAlignment.Center);
        }

        Height = theme.Tokens.Number(role is LivingNavigationRole.Vault ? "chromeHeight" : "topNavHeight", 40) - 4;
        Padding = role switch
        {
            LivingNavigationRole.Vault => new Thickness(8, 0, 10, 0),
            LivingNavigationRole.Customize => new Thickness(10, 0, 10, 0),
            _ => new Thickness(10, 0, 10, 0),
        };
        CornerRadius = new CornerRadius(theme.Tokens.Number(
            role is LivingNavigationRole.Vault ? "radiusControl" : "navCornerRadius",
            role is LivingNavigationRole.Vault ? 8 : 6));
        BorderThickness = new Thickness(0);
        BorderBrush = theme.Brush(signature ? "signatureGold" : "navIndicator");
        Background = theme.Brush(signature ? "signatureFill" : "transparent");
        HorizontalContentAlignment = role is LivingNavigationRole.Vault
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        Opacity = signature ? 0.94 : 1;

        AutomationProperties.SetName(this, label);
        ToolTipService.SetToolTip(this, label);
        Click += (_, _) => onClick();
        PointerEntered += (_, _) =>
        {
            _pointerOver = true;
            ApplyVisualState();
            ApplySemanticMotion(active: true);
            BurstBubbles();
        };
        PointerExited += (_, _) =>
        {
            _pointerOver = false;
            ApplyVisualState();
            ApplySemanticMotion(active: false);
            ResetBubbles();
            ResetPressed();
        };
        AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => ApplyPressed()), true);
        AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            ResetPressed();
            ApplySemanticMotion(active: _pointerOver);
        }), true);

        theme.TrackControl(this, _ => ApplyVisualState());
        ApplyVisualState();
    }

    public string Label
    {
        get => _label.Text;
        set
        {
            _label.Text = value;
            AutomationProperties.SetName(this, value);
            ToolTipService.SetToolTip(this, value);
        }
    }

    public void SetSecondaryMaxWidth(double width)
    {
        if (_secondaryLabel is not null)
        {
            _secondaryLabel.MaxWidth = Math.Max(48, width);
        }
    }

    public void SetSelected(bool selected)
    {
        _selected = selected;
        ApplyVisualState();
    }

    public void SetCompact(bool compact)
    {
        if (_compact == compact)
        {
            return;
        }

        _compact = compact;
        _label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Padding = compact ? new Thickness(8, 0, 8, 0) : _role switch
        {
            LivingNavigationRole.Vault => new Thickness(8, 0, 10, 0),
            LivingNavigationRole.Customize => new Thickness(10, 0, 10, 0),
            _ => new Thickness(10, 0, 10, 0),
        };
    }

    private Border Bubble(double size, HorizontalAlignment horizontal, VerticalAlignment vertical, Thickness margin)
    {
        var token = _signature ? "signatureGold" : "accent";
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            BorderThickness = new Thickness(0.8),
            BorderBrush = ThemeRuntime.Current.Brush(token),
            Background = ThemeRuntime.Current.Brush("transparent"),
            Opacity = 0,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Margin = margin,
            IsHitTestVisible = false,
        };
    }

    private void ApplyVisualState()
    {
        var theme = ThemeRuntime.Current;
        CornerRadius = new CornerRadius(theme.Tokens.Number(
            _role is LivingNavigationRole.Vault ? "radiusHero" : "navCornerRadius", _role is LivingNavigationRole.Vault ? 18 : 6));
        foreach (var bubble in _bubbles)
        {
            bubble.BorderBrush = theme.Brush(_signature ? "signatureGold" : "accent");
        }

        if (_signature)
        {
            Background = theme.Brush("signatureFill");
            BorderBrush = theme.Brush("signatureGold");
            BorderThickness = new Thickness(1);
            Opacity = _pointerOver ? 1 : 0.94;
            _baseIcon.ColorToken = "signatureGold";
            _detailIcon.ColorToken = "signatureGold";
            return;
        }

        var selected = _selected;
        var hover = _pointerOver && !selected;
        Background = theme.Brush(selected
            ? "navSelectedBackground"
            : hover
                ? (_role == LivingNavigationRole.Vault ? "surfaceHover" : "navHoverBackground")
                : _role == LivingNavigationRole.Vault ? "surface2" : "transparent");

        if (_role == LivingNavigationRole.Vault)
        {
            BorderThickness = new Thickness(1);
            BorderBrush = theme.Brush(selected || hover ? "borderInteractive" : "borderSubtle");
            if (_vaultBadge is not null) _vaultBadge.Background = theme.Brush(hover ? "navHoverBackground" : "navSelectedBackground");
        }
        else
        {
            BorderBrush = theme.Brush("navIndicator");
            var outline = selected ? theme.Tokens.Number("navSelectedBorderThickness", 0) : 0;
            BorderThickness = new Thickness(
                selected ? Math.Max(outline, theme.Tokens.Number("navAccentEdgeWidth", 0)) : 0,
                outline,
                outline,
                selected ? Math.Max(outline, theme.Tokens.Number("navIndicatorHeight", 0)) : 0);
        }

        _baseIcon.ColorToken = selected ? "accent" : "textSecondary";
        _detailIcon.ColorToken = selected || hover ? "accent" : "textMuted";
        _label.Foreground = theme.Brush(_role is LivingNavigationRole.Vault
            ? "textMuted"
            : selected ? "textPrimary" : "textSecondary");
        if (_secondaryLabel is not null)
        {
            _secondaryLabel.Foreground = theme.Brush("textPrimary");
        }
    }

    private void ApplySemanticMotion(bool active)
    {
        var reduce = ThemeRuntime.Current.ReducedMotion;
        var duration = Math.Max(1, ThemeRuntime.Current.Duration("fast"));
        var baseScale = 1.0;
        var baseX = 0.0;
        var baseY = 0.0;
        var baseRotation = 0.0;
        var detailScale = 1.0;
        var detailX = 0.0;
        var detailY = 0.0;
        var detailRotation = 0.0;

        if (active && !reduce)
        {
            switch (_role)
            {
                case LivingNavigationRole.Vault:
                    baseScale = 1.045;
                    detailRotation = -7;
                    break;
                case LivingNavigationRole.Home:
                    baseY = -0.8;
                    detailScale = 1.08;
                    break;
                case LivingNavigationRole.Gallery:
                    baseX = 0.55;
                    detailX = -0.8;
                    detailY = -0.65;
                    detailRotation = -3;
                    break;
                case LivingNavigationRole.Import:
                    detailY = 1.8;
                    baseScale = 1.02;
                    break;
                case LivingNavigationRole.Settings:
                    baseRotation = 13;
                    detailScale = 1.055;
                    break;
                case LivingNavigationRole.Customize:
                    baseRotation = 16;
                    detailScale = 1.075;
                    detailRotation = -5;
                    break;
            }
        }

        Animate(_baseTransform, "ScaleX", baseScale, duration);
        Animate(_baseTransform, "ScaleY", baseScale, duration);
        Animate(_baseTransform, "TranslateX", baseX, duration);
        Animate(_baseTransform, "TranslateY", baseY, duration);
        Animate(_baseTransform, "Rotation", baseRotation, duration);
        Animate(_detailTransform, "ScaleX", detailScale, duration);
        Animate(_detailTransform, "ScaleY", detailScale, duration);
        Animate(_detailTransform, "TranslateX", detailX, duration);
        Animate(_detailTransform, "TranslateY", detailY, duration);
        Animate(_detailTransform, "Rotation", detailRotation, duration);
    }

    private void ApplyPressed()
    {
        if (!IsEnabled || ThemeRuntime.Current.ReducedMotion)
        {
            return;
        }

        var duration = Math.Max(1, ThemeRuntime.Current.Duration("fast") * 0.6);
        Animate(_stageTransform, "ScaleX", 0.95, duration);
        Animate(_stageTransform, "ScaleY", 0.95, duration);
        foreach (var bubble in _bubbles)
        {
            bubble.Opacity = 0;
        }
    }

    private void ResetPressed()
    {
        var duration = Math.Max(1, ThemeRuntime.Current.Duration("fast"));
        Animate(_stageTransform, "ScaleX", 1, duration);
        Animate(_stageTransform, "ScaleY", 1, duration);
    }

    private void BurstBubbles()
    {
        if (ThemeRuntime.Current.ReducedMotion || !IsEnabled)
        {
            return;
        }

        var baseDuration = Math.Max(220, ThemeRuntime.Current.Duration("normal"));
        for (var index = 0; index < _bubbles.Length; index++)
        {
            var bubble = _bubbles[index];
            var transform = _bubbleTransforms[index];
            transform.TranslateX = 0;
            transform.TranslateY = 0;
            var startOpacity = index == 0 ? 0.72 : 0.52;
            bubble.Opacity = startOpacity;

            var duration = baseDuration + index * 55;
            var rise = -4.5 - index * 1.15;
            var drift = index switch
            {
                0 => 0.9,
                1 => -0.7,
                _ => 0.45,
            };

            var storyboard = new Storyboard();
            AddAnimation(storyboard, bubble, "Opacity", startOpacity, 0, duration);
            AddAnimation(storyboard, transform, "TranslateY", 0, rise, duration);
            AddAnimation(storyboard, transform, "TranslateX", 0, drift, duration);
            storyboard.Completed += (_, _) =>
            {
                bubble.Opacity = 0;
                transform.TranslateX = 0;
                transform.TranslateY = 0;
            };
            storyboard.Begin();
        }
    }

    private void ResetBubbles()
    {
        foreach (var bubble in _bubbles)
        {
            bubble.Opacity = 0;
        }

        foreach (var transform in _bubbleTransforms)
        {
            transform.TranslateX = 0;
            transform.TranslateY = 0;
        }
    }

    private static (string Base, string Detail) IconKeys(LivingNavigationRole role) => role switch
    {
        LivingNavigationRole.Vault => ("icon.vault.identity", "icon.vault.identity.detail"),
        LivingNavigationRole.Home => ("icon.navigation.home", "icon.navigation.home.detail"),
        LivingNavigationRole.Gallery => ("icon.navigation.gallery", "icon.navigation.gallery.detail"),
        LivingNavigationRole.Import => ("icon.navigation.import", "icon.navigation.import.detail"),
        LivingNavigationRole.Settings => ("icon.navigation.settings", "icon.navigation.settings.detail"),
        LivingNavigationRole.Customize => ("icon.navigation.customize", "icon.navigation.customize.detail"),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static void Animate(DependencyObject target, string property, double value, double durationMs)
    {
        var storyboard = new Storyboard();
        AddAnimation(storyboard, target, property, null, value, durationMs);
        storyboard.Begin();
    }

    private static void AddAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string property,
        double? from,
        double to,
        double durationMs,
        bool autoReverse = false)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            AutoReverse = autoReverse,
            Duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(1, durationMs))),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (from is not null)
        {
            animation.From = from.Value;
        }

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
}
