using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;

namespace Neuterradise.App.Ui;

/// <summary>Semantic slot values for one Profile card. Built from the Gallery/Home read models, never from tables.</summary>
public sealed record CardData(
    Guid ProfileId,
    string Name,
    string? Category,
    IReadOnlyList<string> Tags,
    int? Rating,
    bool IsFavorite,
    long MediaCount,
    int RelatedCount,
    string? Overview,
    ImageRef? Cover,
    ImageRef? Banner,
    CoverAppearance Appearance,
    MediaTransformState CoverTransform,
    MediaTransformState BannerTransform,
    FramePlan? Frame)
{
    public IReadOnlyList<ProfileTagAssignment> AssignedTags { get; init; } = [];
    public bool Has(string slot) => slot switch
    {
        SemanticSlots.ProfileCategory => !string.IsNullOrWhiteSpace(Category),
        SemanticSlots.ProfileTags => Tags.Count > 0,
        SemanticSlots.ProfileRating => Rating is > 0,
        SemanticSlots.ProfileFavorite => IsFavorite,
        SemanticSlots.ProfileOverview => !string.IsNullOrWhiteSpace(Overview),
        SemanticSlots.ProfileCover => Cover is not null,
        SemanticSlots.ProfileBanner => Banner is not null || Cover is not null,
        SemanticSlots.ProfileRelatedCount => RelatedCount > 0,
        _ => true,
    };

    public string TextFor(string slot) => slot switch
    {
        SemanticSlots.ProfileName => Name,
        SemanticSlots.ProfileCategory => Category ?? string.Empty,
        SemanticSlots.ProfileTags => string.Join(" · ", Tags.Take(4)),
        SemanticSlots.ProfileOverview => Overview ?? string.Empty,
        SemanticSlots.ProfileRating => Rating is { } r ? string.Create(CultureInfo.CurrentCulture, $"{r} / 5") : string.Empty,
        SemanticSlots.ProfileMediaCount => MediaCount == 1 ? UI.T("Card.OneItem", "1 item") : UI.F("Card.Items", "{0} items", MediaCount),
        SemanticSlots.ProfileRelatedCount => RelatedCount == 1 ? UI.T("Card.OneConnection", "1 connection") : UI.F("Card.Connections", "{0} connections", RelatedCount),
        _ => string.Empty,
    };
}

/// <summary>
/// A realized Profile card. Its element tree is built ONCE from the compiled plan's composition
/// primitives; <see cref="Bind"/> only updates property values. Ordinary data changes and container
/// recycling never clear or reparent the tree (the WPF card's destructive recomposition is gone).
/// </summary>
public sealed class CardVisual : Grid
{
    private readonly List<Action<CardData>> _binders = [];
    private readonly List<(FrameworkElement Element, string Slot)> _conditions = [];
    private readonly List<CoverFrameView> _frames = [];
    private readonly List<FrameworkElement> _hoverReveal = [];
    private readonly List<FrameworkElement> _hoverResting = [];
    private FrameworkElement? _hoverBanner;
    private readonly ScaleTransform _scale = new() { ScaleX = 1, ScaleY = 1 };
    private readonly Grid _content = new();
    private bool _buildingArtBase;
    private Grid? _bannerMediaHost;
    private bool _selected;
    private bool _hovered;
    private readonly EffectView _effect;

    public CardVisual(CompiledDefinition definition, Func<EffectPlan?>? previewEffect = null)
    {
        Definition = definition;
        Plan = definition.PlanAs<CardPlan>();
        RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        RenderTransform = _scale;
        ThemeRuntime.Current.TrackThemeElement(this, card =>
        {
            var theme = ThemeRuntime.Current;
            var color = theme.Color("surface1");
            card.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
                (byte)Math.Round(color.A * theme.Tokens.Number("cardSurfaceOpacity", 1)), color.R, color.G, color.B));
            card.BorderThickness = new Thickness(theme.Tokens.Number("cardBorderThickness", 1));
            card.CornerRadius = new CornerRadius(theme.Tokens.Number("radiusCard", 12));
            card.PaintOutline();
            if (card.Children.FirstOrDefault() is Border clip) clip.CornerRadius = card.CornerRadius;
        });

        // Preserve composition geometry in both planes, with artwork below the single effect layer.
        _buildingArtBase = true;
        var artBase = Build(Plan.Root);
        artBase.IsHitTestVisible = false;
        _buildingArtBase = false;
        _effect = PresentationEffects.View(PresentationSlots.CardEffect, () => new PresentationContext(ProfileId: Data?.ProfileId), this, previewEffect);
        _content.Children.Add(artBase);
        _content.Children.Add(_effect);
        _content.Children.Add(Build(Plan.Root));
        Children.Add(new Border { CornerRadius = CornerRadius, Child = _content });

        PointerEntered += (_, _) => SetHover(true);
        PointerExited += (_, _) => SetHover(false);
        PointerCanceled += (_, _) => SetHover(false);

    }

    public CompiledDefinition Definition { get; }

    public CardPlan Plan { get; }

    public CardData? Data { get; private set; }

    public Panel? BannerMediaHost => _bannerMediaHost;

    public bool IsSelected
    {
        set
        {
            _selected = value;
            _effect.VisualPriority = _selected ? PresentationVisualPriority.ActiveCard : PresentationVisualPriority.VisibleCard;
            _effect.Refresh();
            PaintOutline();
        }
    }

    public void Bind(CardData data)
    {
        Data = data;
        _effect.Refresh();
        foreach (var binder in _binders)
        {
            binder(data);
        }

        foreach (var (element, slot) in _conditions)
        {
            element.Visibility = data.Has(slot) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void PaintOutline()
    {
        BorderBrush = ThemeRuntime.Current.Brush(_selected ? "borderSelected" : _hovered ? "borderInteractive" : "borderSubtle");
    }

    private void SetHover(bool hovered)
    {
        _hovered = hovered;
        PaintOutline();
        foreach (var frame in _frames)
        {
            frame.IsHovered = hovered;
        }

        if (Plan.Hover == "spotlight")
        {
            void SetOpacity(FrameworkElement element, double value)
            {
                if (ThemeRuntime.Current.ReducedMotion) element.Opacity = value;
                else Animate(element, "Opacity", value);
            }
            if (_hoverBanner is not null) SetOpacity(_hoverBanner, hovered ? 1 : 0);
            foreach (var frame in _frames) SetOpacity(frame, hovered ? 0 : 1);
            foreach (var reveal in _hoverReveal) SetOpacity(reveal, hovered ? 1 : 0);
            foreach (var resting in _hoverResting) SetOpacity(resting, hovered ? 0 : 1);
        }

        if (Plan.Hover is "lift" or "sheen" && !ThemeRuntime.Current.ReducedMotion)
        {
            var target = hovered ? ThemeRuntime.Current.Tokens.Number("motionCardLiftScale", 1.03) : 1.0;
            Animate(_scale, "ScaleX", target);
            Animate(_scale, "ScaleY", target);
        }
    }

    private static void Animate(DependencyObject target, string property, double to)
    {
        var duration = ThemeRuntime.Current.Duration("fast");
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(1, duration))),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    // ---------------------------------------------------------------- composition → elements (once)

    private FrameworkElement Build(CompositionNode node)
    {
        var theme = ThemeRuntime.Current;
        FrameworkElement element;
        switch (node)
        {
            case StackNode stack:
            {
                var panel = new StackPanel
                {
                    Orientation = stack.Orientation == "horizontal" ? Orientation.Horizontal : Orientation.Vertical,
                    Spacing = stack.Spacing,
                };
                foreach (var child in stack.Children)
                {
                    panel.Children.Add(Build(child));
                }

                element = stack.Background is not null || stack.CornerRadius is not null || stack.Padding > 0
                    ? new Border
                    {
                        Child = panel,
                        Padding = new Thickness(stack.Padding),
                        Background = !_buildingArtBase || stack.Background is null ? null : theme.Brush(TokenName(stack.Background)),
                        CornerRadius = new CornerRadius(theme.Tokens.ResolveRadius(stack.CornerRadius)),
                    }
                    : panel;
                break;
            }

            case GridNode gridNode:
            {
                var grid = UI.Grid(string.Join(",", gridNode.Rows), string.Join(",", gridNode.Columns));
                grid.RowSpacing = gridNode.Spacing;
                grid.ColumnSpacing = gridNode.Spacing;
                foreach (var child in gridNode.Children)
                {
                    grid.Children.Add(Build(child));
                }

                element = grid;
                break;
            }

            case OverlayNode overlay:
            {
                var grid = new Grid();
                foreach (var child in overlay.Children)
                {
                    grid.Children.Add(Build(child));
                }

                element = grid;
                break;
            }

            case TextNode text:
            {
                if (text.Slot == SemanticSlots.ProfileTags)
                {
                    var capsules = TagCapsules.Create([]);
                    _binders.Add(data => TagCapsules.Update(capsules, data.AssignedTags, 4));
                    element = capsules;
                    break;
                }
                var block = UI.Text(null, text.TypeRole, TokenName(text.Color), text.MaxLines);
                block.TextAlignment = text.TextAlignment switch
                {
                    "center" => TextAlignment.Center,
                    "right" => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };
                if (text.Slot is { } slot)
                {
                    _binders.Add(data =>
                    {
                        block.Text = data.TextFor(slot);
                        ToolTipService.SetToolTip(block, block.Text);
                    });
                }
                else if (text.TextKey is { } key)
                {
                    block.Text = UI.T(key, key);
                }

                if (Plan.Hover == "sheen" && text.OnMedia
                    && text.Slot is SemanticSlots.ProfileName or SemanticSlots.ProfileCategory)
                {
                    var outlined = new Grid();
                    var strokes = new List<TextBlock>(4);
                    foreach (var (x, y) in new[] { (-0.9, 0.0), (0.9, 0.0), (0.0, -0.9), (0.0, 0.9) })
                    {
                        var stroke = UI.Text(null, text.TypeRole, "warning", text.MaxLines);
                        stroke.TextAlignment = block.TextAlignment;
                        stroke.Opacity = 0.7;
                        stroke.IsHitTestVisible = false;
                        stroke.RenderTransform = new TranslateTransform { X = x, Y = y };
                        outlined.Children.Add(stroke);
                        strokes.Add(stroke);
                    }
                    if (text.Slot is { } outlinedSlot)
                        _binders.Add(data =>
                        {
                            foreach (var stroke in strokes) stroke.Text = data.TextFor(outlinedSlot);
                        });
                    outlined.Children.Add(block);
                    element = outlined;
                }
                else if (Plan.Hover == "sheen" && text.OnMedia
                    && text.Slot == SemanticSlots.ProfileMediaCount)
                {
                    element = new Border
                    {
                        Child = block,
                        Background = theme.Brush("mediaOverlay"),
                        BorderBrush = theme.Brush("glassBorder"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(4, 2, 4, 2),
                    };
                }
                else element = block;
                break;
            }

            case ImageNode image:
            {
                var baseArt = image.Ambient;
                if (baseArt != _buildingArtBase)
                {
                    element = new Border();
                    break;
                }
                var view = new SkImageView
                {
                    CornerRadiusValue = theme.Tokens.ResolveRadius(image.CornerRadius),
                    Circle = image.Shape == "circle",
                };
                theme.TrackThemeElement(view, imageView => imageView.CornerRadiusValue = ThemeRuntime.Current.Tokens.ResolveRadius(image.CornerRadius));
                if (image.Ambient)
                {
                    view.Blur = 18;
                }

                _binders.Add(data =>
                {
                    var isBanner = image.Slot == SemanticSlots.ProfileBanner;
                    var primary = isBanner ? data.Banner : data.Cover;
                    var useFallback = primary is null && image.Fallback == "identity";
                    view.Source = useFallback ? (isBanner ? data.Cover : data.Banner) : primary;
                    var transform = isBanner != useFallback ? data.BannerTransform : data.CoverTransform;
                    view.Transform = transform with
                    {
                        Fit = image.Stretch switch
                        {
                            "uniform" => "fit",
                            "fill" => "stretch",
                            _ => "fill",
                        },
                    };

                    if (image.Shape == "cover-shape")
                    {
                        view.Circle = data.Appearance.Shape == CoverShape.Circle;
                    }
                });
                if (image.Slot == SemanticSlots.ProfileBanner
                    || (!_buildingArtBase && image.Slot == SemanticSlots.ProfileCover && Plan.Hover is "spotlight" or "banner"))
                {
                    _bannerMediaHost = new Grid();
                    _bannerMediaHost.Children.Add(view);
                    element = _bannerMediaHost;
                    if (!_buildingArtBase && Plan.Hover == "spotlight" && image.Slot == SemanticSlots.ProfileBanner)
                        _hoverBanner = element;
                }
                else
                {
                    element = view;
                }
                break;
            }

            case FrameNode frame:
            {
                if (_buildingArtBase)
                {
                    element = new Border { Width = frame.Size, Height = frame.Size };
                    break;
                }
                var view = new CoverFrameView { FrameMode = Plan.FrameMode, Width = frame.Size, Height = frame.Size };
                _frames.Add(view);
                _binders.Add(data =>
                {
                    view.Source = data.Cover;
                    view.Transform = data.CoverTransform;
                    view.Plan = data.Frame;
                    view.Appearance = data.Appearance;
                });
                element = view;
                break;
            }

            case BadgeNode badge:
                element = BuildBadge(badge);
                break;

            case ScrimNode scrim:
            {
                var border = new Border { IsHitTestVisible = false };
                void Paint()
                {
                    var from = ThemeRuntime.Current.Color(TokenName(scrim.From));
                    var to = ThemeRuntime.Current.Color(TokenName(scrim.To));
                    if (scrim.Direction == "radial")
                    {
                        var radial = new RadialGradientBrush();
                        radial.GradientStops.Add(new GradientStop { Color = from, Offset = 0 });
                        radial.GradientStops.Add(new GradientStop { Color = to, Offset = 1 });
                        border.Background = radial;
                        return;
                    }

                    var (start, end) = scrim.Direction switch
                    {
                        "to-bottom" => ((0.5, 0.0), (0.5, 1.0)),
                        "to-left" => ((1.0, 0.5), (0.0, 0.5)),
                        "to-right" => ((0.0, 0.5), (1.0, 0.5)),
                        _ => ((0.5, 1.0), (0.5, 0.0)),
                    };
                    var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(start.Item1, start.Item2), EndPoint = new Windows.Foundation.Point(end.Item1, end.Item2) };
                    brush.GradientStops.Add(new GradientStop { Color = from, Offset = 0 });
                    brush.GradientStops.Add(new GradientStop { Color = to, Offset = 1 });
                    border.Background = brush;
                }

                Paint();
                ThemeRuntime.Current.Changed += Paint;
                border.Unloaded += (_, _) => ThemeRuntime.Current.Changed -= Paint;
                border.Loaded += (_, _) =>
                {
                    ThemeRuntime.Current.Changed -= Paint;
                    ThemeRuntime.Current.Changed += Paint;
                    Paint();
                };
                element = border;
                break;
            }

            case ShapeNode shape:
                element = new Border
                {
                    Background = shape.Fill is null ? null : theme.Brush(TokenName(shape.Fill)),
                    BorderBrush = shape.Stroke is null ? null : theme.Brush(TokenName(shape.Stroke)),
                    BorderThickness = new Thickness(shape.Stroke is null ? 0 : shape.StrokeThickness),
                    CornerRadius = new CornerRadius(theme.Tokens.ResolveRadius(shape.CornerRadius)),
                };
                break;

            case IconNode icon:
                element = new IconView(icon.Key, icon.Size, TokenName(icon.Color));
                break;

            default:
                element = new Grid();
                break;
        }

        if (element is Border shapedBorder && node is StackNode or ShapeNode)
        {
            var radius = node is StackNode stack ? stack.CornerRadius : ((ShapeNode)node).CornerRadius;
            theme.TrackThemeElement(shapedBorder, value => value.CornerRadius = new CornerRadius(ThemeRuntime.Current.Tokens.ResolveRadius(radius)));
        }
        ApplyLayout(element, node.Layout);
        if (!_buildingArtBase && Plan.Hover == "spotlight")
        {
            if (ReferenceEquals(element, _hoverBanner)) element.Opacity = 0;
            if (node is StackNode && node.Layout.Opacity < 0.5) _hoverReveal.Add(element);
            if (node is TextNode { Slot: SemanticSlots.ProfileName, TypeRole: "card-title" }
                && node.Layout.VerticalAlignment == "bottom")
                _hoverResting.Add(element);
        }
        if (_buildingArtBase && node is TextNode or BadgeNode or IconNode
            || !_buildingArtBase && node is ScrimNode or ShapeNode)
            element.Opacity = 0;
        return element;
    }

    private FrameworkElement BuildBadge(BadgeNode badge)
    {
        var theme = ThemeRuntime.Current;
        var onMedia = badge.Style == "glass";
        var textColor = onMedia || Plan.Hover == "sheen" && badge.Style == "text"
            ? "onMediaPrimary" : badge.Style == "solid" ? "textOnAccent" : "textSecondary";
        var icon = badge.Slot switch
        {
            SemanticSlots.ProfileFavorite => "icon.profile.favorite",
            SemanticSlots.ProfileRating => "icon.profile.rating",
            SemanticSlots.ProfileMediaCount => "icon.media.image",
            SemanticSlots.ProfileRelatedCount => "icon.profile.related",
            SemanticSlots.ProfileCategory => "icon.profile.category",
            _ => null,
        };
        var label = UI.Text(null, "badge", textColor);
        var badgeIcon = icon is null ? null : new IconView(icon, 12, textColor);
        var content = UI.H(4, badgeIcon, label);
        _binders.Add(data =>
        {
            label.Text = badge.Slot == SemanticSlots.ProfileFavorite
                ? UI.T("Card.Favorite", "Favorite") : data.TextFor(badge.Slot);
            if (badgeIcon is not null)
                badgeIcon.ColorToken = badge.Slot switch
                {
                    SemanticSlots.ProfileFavorite when data.IsFavorite => "danger",
                    SemanticSlots.ProfileFavorite when Plan.Hover == "sheen" => "warning",
                    SemanticSlots.ProfileRating when data.Rating is > 0 => "warning",
                    _ => textColor,
                };
        });
        if (badge.Slot == SemanticSlots.ProfileFavorite)
        {
            label.Visibility = Visibility.Collapsed;
            content.Tip(UI.T("Card.Favorite", "Favorite"));
        }

        return badge.Style == "text"
            ? content
            : new Border
            {
                Child = content,
                Padding = new Thickness(6, 2, 6, 2),
                CornerRadius = new CornerRadius(6),
                Background = badge.Style switch
                {
                    "glass" => theme.Brush("mediaOverlay"),
                    "solid" => theme.Brush("accent"),
                    _ => theme.Brush("transparent"),
                },
                BorderBrush = badge.Style == "outline" ? theme.Brush("borderDefault") : null,
                BorderThickness = new Thickness(badge.Style == "outline" ? 1 : 0),
            };
    }

    private void ApplyLayout(FrameworkElement element, NodeLayout layout)
    {
        element.Margin = new Thickness(layout.MarginLeft, layout.MarginTop, layout.MarginRight, layout.MarginBottom);
        if (layout.Width is { } width)
        {
            element.Width = width;
        }

        if (layout.Height is { } height)
        {
            element.Height = height;
        }

        element.HorizontalAlignment = layout.HorizontalAlignment switch
        {
            "left" => HorizontalAlignment.Left,
            "center" => HorizontalAlignment.Center,
            "right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Stretch,
        };
        element.VerticalAlignment = layout.VerticalAlignment switch
        {
            "top" => VerticalAlignment.Top,
            "center" => VerticalAlignment.Center,
            "bottom" => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Stretch,
        };
        Grid.SetRow(element, layout.Row);
        Grid.SetColumn(element, layout.Column);
        Grid.SetRowSpan(element, layout.RowSpan);
        Grid.SetColumnSpan(element, layout.ColumnSpan);
        element.Opacity = layout.Opacity;
        if (layout.VisibleWhen is { } slot)
        {
            _conditions.Add((element, slot));
        }
    }

    private static string TokenName(string reference) =>
        reference.StartsWith("token:", StringComparison.Ordinal) ? reference[6..] : reference;

}

public static class CardDataFactory
{
    public static CardData From(Neuterradise.App.Gallery.GalleryCardViewModel card, PresentationRuntime? runtime, int coverWidth, int bannerWidth)
    {
        var overrides = card.Appearance;
        var profile = new ProfilePresentationState(card.ProfileId, 0, null, overrides);
        var media = runtime?.ResolveMedia(profile) ?? profile.Media;
        var frame = runtime?.ResolvePlan<FramePlan>(PresentationSlots.ProfileFrame, PresentationContext.ForProfile(profile, "gallery"));
        return new CardData(
            card.ProfileId,
            card.DisplayName,
            card.CategoryName,
            card.Tags,
            card.Rating,
            card.IsFavorite,
            card.MediaCount,
            card.RelatedProfileCount,
            null,
            card.CoverSource is { } cover ? cover with { DecodeWidth = coverWidth } : null,
            card.BannerImageSource is { } banner ? banner with { DecodeWidth = bannerWidth } : null,
            AppearanceFor(overrides, frame, ThemeRuntime.Current.ReducedMotion),
            media.Cover,
            media.Banner,
            frame) { AssignedTags = card.AssignedTags };
    }

    /// <summary>Profile Cover appearance with the resolved frame definition (built-in or pack-defined).</summary>
    public static CoverAppearance AppearanceFor(Neuterradise.App.Profiles.ProfileAppearanceOverrides overrides, FramePlan? frame, bool reduceMotion)
    {
        var request = overrides.ToCoverAppearanceRequest();
        if (frame is not null && CoverFrameCatalog.TryGetFrame(frame.Frame.Id, out _))
        {
            request = request with
            {
                FrameId = frame.Frame.Id,
                Shape = CoverFrameCatalog.ClosestSupportedShape(frame.Frame,
                    Enum.TryParse<CoverShape>(request.Shape, true, out var shape) ? shape : CoverFrameCatalog.FallbackShape).ToString(),
                FrameAnimation = request.FrameAnimation ?? (frame.Frame.SupportsAnimation ? CoverFrameAnimation.Glow.ToString() : null),
            };
        }

        var resolved = CoverFrameCatalog.Resolve(request, reduceMotion).Appearance;
        return frame is not null && !CoverFrameCatalog.TryGetFrame(frame.Frame.Id, out _)
            ? resolved with
            {
                Frame = frame.Frame,
                FrameAnimation = !reduceMotion && frame.OverlayAssetPath is null && frame.Frame.SupportsAnimation
                    ? CoverFrameAnimation.Glow : CoverFrameAnimation.None,
            }
            : resolved;
    }
}
