using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Design.MediaLayouts;
using Neuterradise.App.Media;
using Neuterradise.App.Presentation;

namespace Neuterradise.App.Ui;

/// <summary>
/// Fixed media-card renderer shared by the live Profile grid and Customization previews.
/// Layout changes never replace this tree and never touch media queries, paging or hover assets.
/// </summary>
public sealed class MediaCardView : Grid, IDisposable
{
    private readonly Border _frame;
    private readonly Grid _mediaHost = new();
    private readonly Grid _playbackHost = new() { IsHitTestVisible = false };
    private readonly SkImageView _image = new();
    private readonly IconView _placeholder = new("icon.media.model", 32, "textMuted")
    {
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
    private readonly Border _innerFrame;
    private readonly Border _selectionOutline = new() { IsHitTestVisible = false, BorderThickness = new Thickness(2), Visibility = Visibility.Collapsed };
    private readonly Border _metadata;
    private readonly TextBlock[] _metadataLines;
    private MediaMetadataPlacement? _metadataPlacement;
    private readonly StackPanel _badges = UI.H(4);
    private readonly TextBlock _typeText = UI.Text(string.Empty, "caption", "onMediaSecondary");
    private readonly Border _typeBadge;
    private readonly Button _favorite;
    private readonly Button _inspect;
    private readonly Button _open;
    private readonly StackPanel _actions;
    private readonly StackPanel _controlRail;
    private readonly Grid _liveFooter = new();
    private readonly TextBlock _liveTitle = UI.Text(null, "caption", "onMediaPrimary", 1);
    private readonly StackPanel _liveFavoriteRail = UI.H(2);
    private readonly IconView _liveHeart = new("icon.profile.favorite", 14, "onMediaPrimary");
    private readonly LiveHeartOverlay _hearts = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private bool _live;
    private readonly ScaleTransform _scale = new();
    private readonly Action<MediaGridCardViewModel>? _onFavorite;
    private readonly Action<MediaGridCardViewModel>? _onOpen;
    private readonly Action<MediaGridCardViewModel>? _onInspect;
    private MediaGridCardViewModel? _card;
    private MediaLayoutDefinition _layout;
    private IReadOnlyDictionary<string, ComponentVisualStyle>? _appliedVisuals;
    private PropertyChangedEventHandler? _observer;

    public MediaCardView(
        MediaLayoutDefinition layout,
        Action<MediaGridCardViewModel>? onFavorite = null,
        Action<MediaGridCardViewModel>? onOpen = null,
        Action<MediaGridCardViewModel>? onInspect = null)
    {
        _layout = layout;
        _onFavorite = onFavorite;
        _onOpen = onOpen;
        _onInspect = onInspect;

        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });

        _image.HorizontalAlignment = HorizontalAlignment.Stretch;
        _image.VerticalAlignment = VerticalAlignment.Stretch;
        _playbackHost.Children.Add(_image);
        _mediaHost.Children.Add(_playbackHost);
        _mediaHost.Children.Add(_placeholder);
        _typeBadge = new Border
        {
            Child = _typeText,
            IsHitTestVisible = false,
            Background = ThemeRuntime.Current.Brush("mediaOverlay"),
            Padding = new Thickness(5, 2, 5, 2),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
        };
        _mediaHost.Children.Add(_typeBadge);

        _innerFrame = new Border
        {
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _mediaHost.Children.Add(_innerFrame);
        _mediaHost.Children.Add(_hearts);

        _metadataLines =
        [
            UI.Text(null, "caption", "textSecondary", 1),
            UI.Text(null, "micro", "textMuted", 1),
            UI.Text(null, "micro", "textMuted", 1),
        ];
        _metadata = new Border { Child = UI.V(1, _metadataLines) };

        _badges.HorizontalAlignment = HorizontalAlignment.Right;
        _badges.VerticalAlignment = VerticalAlignment.Top;

        _favorite = UI.IconButton("icon.action.favorite", UI.T("Card.Favorite", "Favorite"), null, 26);
        _favorite.HorizontalAlignment = HorizontalAlignment.Left;
        _favorite.VerticalAlignment = VerticalAlignment.Top;
        _favorite.Margin = new Thickness(6);
        _favorite.IsHitTestVisible = onFavorite is not null;
        _favorite.PointerPressed += (_, e) => e.Handled = true;
        _favorite.Click += (_, _) =>
        {
            if (_card is { } card)
            {
                _onFavorite?.Invoke(card);
            }
        };
        _mediaHost.Children.Add(_favorite);

        var inspect = _inspect = UI.IconButton("icon.status.info", UI.T("Inspector.Title", "Media info"), null, 28);
        inspect.IsHitTestVisible = onInspect is not null;
        inspect.PointerPressed += (_, e) => e.Handled = true;
        inspect.Click += (_, _) =>
        {
            if (_card is { } card)
            {
                _onInspect?.Invoke(card);
            }
        };

        var open = _open = UI.IconButton("icon.action.open", UI.T("Inspector.Open", "Open with default app"), null, 28);
        open.IsHitTestVisible = onOpen is not null;
        open.PointerPressed += (_, e) => e.Handled = true;
        open.Click += (_, _) =>
        {
            if (_card is { } card)
            {
                HoverVideoCoordinator.Shared.StopAll();
                _onOpen?.Invoke(card);
            }
        };

        var actions = _actions = UI.H(4, inspect, open);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.VerticalAlignment = VerticalAlignment.Top;
        actions.Visibility = onInspect is null && onOpen is null ? Visibility.Collapsed : Visibility.Visible;
        var controlRail = _controlRail = UI.V(4, actions, _badges);
        controlRail.HorizontalAlignment = HorizontalAlignment.Right;
        controlRail.VerticalAlignment = VerticalAlignment.Top;
        controlRail.Margin = new Thickness(6);
        _mediaHost.Children.Add(controlRail);
        _mediaHost.Children.Add(_selectionOutline);
        foreach (var button in new[] { _favorite, inspect, open })
        {
            button.Background = ThemeRuntime.Current.Brush("mediaOverlay");
            button.CornerRadius = new CornerRadius(6);
            if (button.Content is IconView actionIcon)
            {
                actionIcon.ColorToken = "onMediaPrimary";
            }
        }

        _liveFavoriteRail.HorizontalAlignment = HorizontalAlignment.Right;
        _liveFavoriteRail.VerticalAlignment = VerticalAlignment.Top;
        _liveFavoriteRail.Margin = new Thickness(4);
        _liveFavoriteRail.Visibility = Visibility.Collapsed;
        _liveHeart.VerticalAlignment = VerticalAlignment.Center;
        _liveFavoriteRail.Children.Add(_liveHeart);
        _mediaHost.Children.Add(_liveFavoriteRail);
        _liveFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _liveFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _liveFooter.Height = 26;
        _liveFooter.Background = ThemeRuntime.Current.Brush("#FF000000");
        _liveFooter.Visibility = Visibility.Collapsed;
        _liveTitle.Margin = new Thickness(6, 0, 0, 0);
        _liveTitle.VerticalAlignment = VerticalAlignment.Center;
        _liveFooter.Children.Add(_liveTitle);
        Children.Add(_liveFooter.At(1, 0));
        Grid.SetColumnSpan(_liveFooter, 2);

        _frame = new Border
        {
            Child = _mediaHost,
            Background = ThemeRuntime.Current.Brush("surface2"),
        };
        Children.Add(_frame.At(0, 0));

        RenderTransform = _scale;
        RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;

        ApplyLayout(layout);
    }

    public Grid MediaHost => _mediaHost;

    public void ApplyLayout(MediaLayoutDefinition layout)
    {
        MaxWidth = layout.CardWidth;
        MaxHeight = layout.CardHeight;
        _layout = layout;
        var theme = ThemeRuntime.Current;
        var side = layout.MetadataPlacement == MediaMetadataPlacement.Side;

        ColumnDefinitions[0].Width = side
            ? new GridLength(layout.MediaWidthFraction, GridUnitType.Star)
            : new GridLength(1, GridUnitType.Star);
        ColumnDefinitions[1].Width = side
            ? new GridLength(Math.Max(0.01, 1 - layout.MediaWidthFraction), GridUnitType.Star)
            : new GridLength(0);

        Grid.SetColumn(_frame, 0);
        Grid.SetColumnSpan(_frame, side ? 1 : 2);
        ConfigureMetadata(layout.MetadataPlacement);
        ConfigureLiveChrome(layout.MetadataPlacement == MediaMetadataPlacement.LiveOverlay);
        _hearts.ShowDecorativeHeart = layout.ShowDecorativeHeart;
        _liveHeart.Visibility = _live && layout.ShowDecorativeHeart ? Visibility.Visible : Visibility.Collapsed;
        ApplyControlVisuals(layout.Visuals);
        _selectionOutline.CornerRadius = new CornerRadius(_live ? 0 : Math.Max(0, layout.FrameRadius - layout.FrameThickness));

        var below = layout.MetadataPlacement == MediaMetadataPlacement.Below;
        _frame.CornerRadius = _live ? new CornerRadius(0) : below
            ? new CornerRadius(layout.FrameRadius, layout.FrameRadius, 0, 0)
            : new CornerRadius(layout.FrameRadius);
        _metadata.CornerRadius = below
            ? new CornerRadius(0, 0, layout.FrameRadius, layout.FrameRadius)
            : new CornerRadius(0);
        _image.CornerRadiusValue = _live ? 0 : Math.Max(0, layout.FrameRadius - layout.FrameThickness);
        _innerFrame.CornerRadius = new CornerRadius(Math.Max(0, layout.FrameRadius - 4));

        var layered = !_live && layout.FrameStyle is (MediaFrameStyle.Double or MediaFrameStyle.Gallery or MediaFrameStyle.Luminous or MediaFrameStyle.Neon);
        _innerFrame.Visibility = layered ? Visibility.Visible : Visibility.Collapsed;
        if (layered)
        {
            _innerFrame.Margin = new Thickness(Math.Max(3, layout.FrameThickness + 2));
            _innerFrame.BorderThickness = new Thickness(layout.FrameStyle == MediaFrameStyle.Gallery ? 2 : 1);
            _innerFrame.BorderBrush = theme.Brush(layout.FrameStyle switch
            {
                MediaFrameStyle.Neon => "accent",
                MediaFrameStyle.Luminous => "borderEnergy",
                MediaFrameStyle.Gallery => "borderDefault",
                _ => "borderSubtle",
            });
            _innerFrame.Opacity = Math.Clamp(0.55 + (layout.FrameGlow * 0.4), 0.55, 0.95);
        }

        UpdateVisual();
    }

    private void ApplyControlVisuals(IReadOnlyDictionary<string, ComponentVisualStyle>? visuals)
    {
        if (ReferenceEquals(_appliedVisuals, visuals)) return;
        var theme = ThemeRuntime.Current;
        void Apply(Button button, string id, double fallback)
        {
            var style = visuals?.GetValueOrDefault(id);
            if (style is null && _appliedVisuals?.GetValueOrDefault(id) is null) return;
            if (button.Content is IconView icon)
                icon.ApplyComponentStyle(style, theme.ControlFamily("icon-button").IconButtonSize is { } size
                    ? Math.Round(size * 0.5) : fallback);
        }
        Apply(_open, "open", 14);
        Apply(_inspect, "info", 14);
        Apply(_favorite, "favorite", 13);
        _liveHeart.ApplyComponentStyle(visuals?.GetValueOrDefault("favorite"), 14);
        _appliedVisuals = visuals;
    }

    private void ConfigureLiveChrome(bool live)
    {
        if (_live == live) return;
        _live = live;
        _hearts.Visibility = _liveFooter.Visibility = _liveFavoriteRail.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        if (live)
        {
            _actions.Children.Remove(_inspect);
            _actions.Children.Remove(_open);
            _mediaHost.Children.Remove(_favorite);
            _liveFavoriteRail.Children.Add(_favorite);
            _liveFooter.Children.Add(_inspect.At(0, 1));
            _mediaHost.Children.Add(_open);
            _open.HorizontalAlignment = HorizontalAlignment.Left;
            _open.VerticalAlignment = VerticalAlignment.Top;
            _open.Margin = new Thickness(4);
            _favorite.Margin = new Thickness(0);
            _inspect.Width = _inspect.Height = 24;
            _inspect.Margin = new Thickness(0);
            _inspect.VerticalAlignment = VerticalAlignment.Center;
            _actions.Visibility = Visibility.Collapsed;
            _controlRail.Margin = new Thickness(4, 34, 4, 0);
        }
        else
        {
            _liveFavoriteRail.Children.Remove(_favorite);
            _liveFooter.Children.Remove(_inspect);
            _mediaHost.Children.Remove(_open);
            _actions.Children.Add(_inspect);
            _actions.Children.Add(_open);
            _mediaHost.Children.Insert(_mediaHost.Children.IndexOf(_selectionOutline), _favorite);
            _favorite.Margin = new Thickness(6);
            _open.Margin = new Thickness(0);
            _inspect.Width = _inspect.Height = 28;
            _actions.Visibility = _onInspect is null && _onOpen is null ? Visibility.Collapsed : Visibility.Visible;
            _controlRail.Margin = new Thickness(6);
        }
    }

    private void ConfigureMetadata(MediaMetadataPlacement placement)
    {
        var live = placement == MediaMetadataPlacement.LiveOverlay;
        var overlay = live || placement is MediaMetadataPlacement.HoverOverlay or MediaMetadataPlacement.PersistentOverlay;
        if (_metadataPlacement != placement)
        {
            // Reparent only the single metadata subtree when the presentation placement changes.
            _mediaHost.Children.Remove(_metadata);
            Children.Remove(_metadata);
            _metadataPlacement = placement;
            if (overlay)
                _mediaHost.Children.Insert(_mediaHost.Children.IndexOf(_hearts) + 1, _metadata);
            else if (placement != MediaMetadataPlacement.Hidden)
                Children.Add(_metadata);
            Grid.SetRow(_metadata, placement == MediaMetadataPlacement.Below ? 1 : 0);
            Grid.SetColumn(_metadata, placement == MediaMetadataPlacement.Side ? 1 : 0);
            Grid.SetColumnSpan(_metadata, placement == MediaMetadataPlacement.Below ? 2 : 1);
            _metadata.HorizontalAlignment = HorizontalAlignment.Stretch;
            _metadata.VerticalAlignment = overlay ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
            _metadata.Padding = placement switch
            {
                MediaMetadataPlacement.LiveOverlay => new Thickness(6, 0, 25, 6),
                MediaMetadataPlacement.Side => new Thickness(10, 8, 8, 8),
                MediaMetadataPlacement.Below => new Thickness(8, 5, 8, 6),
                _ => new Thickness(8, 6, 8, 6),
            };
            for (var i = 0; i < _metadataLines.Length; i++)
                ThemeRuntime.Current.ApplyType(_metadataLines[i], i == 0 ? "caption" : "micro",
                    live ? "onMediaPrimary" : overlay ? i == 0 ? "onMediaPrimary" : "onMediaSecondary" : i == 0 ? "textSecondary" : "textMuted");
        }
        _metadata.Background = ThemeRuntime.Current.Brush(live ? "transparent" : overlay ? "mediaOverlay" : "surface2");
        _metadata.Opacity = placement == MediaMetadataPlacement.HoverOverlay ? 0 : 1;
    }

    public void Bind(MediaGridCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (ReferenceEquals(_card, card)) return;
        Unbind();
        _card = card;
        DataContext = card;
        _observer = (_, args) =>
        {
            // The favorite operation emits three aliases; only IsFavorite changes this Card.
            if (args.PropertyName is nameof(MediaGridCardViewModel.ShowFavoriteBadge)
                or nameof(MediaGridCardViewModel.IsVideoAndFavorite)) return;
            UiDispatch.Run(UpdateVisual);
        };
        card.PropertyChanged += _observer;
        UpdateVisual();
    }

    public void Unbind()
    {
        HoverVideoCoordinator.Shared.Release(_playbackHost);
        if (_card is not null && _observer is not null)
        {
            _card.PropertyChanged -= _observer;
        }

        _observer = null;
        _image.Source = null;
        _card = null;
        DataContext = null;
    }

    private void UpdateVisual()
    {
        if (_card is not { } card)
        {
            return;
        }

        var theme = ThemeRuntime.Current;
        _image.Transform = _layout.Fit == "fit"
            ? MediaTransformState.Default with { Fit = "fit" }
            : MediaTransformState.Default;
        _image.Source = card.ThumbnailSource ?? ImageRef.FromPath(card.ThumbnailPath, 360);

        var thickness = _live || _layout.FrameStyle == MediaFrameStyle.None ? 0 : _layout.FrameThickness;
        _frame.BorderBrush = theme.Brush(_layout.FrameColor.Replace("token:", string.Empty, StringComparison.Ordinal));
        _frame.BorderThickness = new Thickness(thickness);
        _metadata.BorderBrush = _frame.BorderBrush;
        _metadata.BorderThickness = _layout.MetadataPlacement == MediaMetadataPlacement.Below
            ? new Thickness(thickness, 0, thickness, thickness) : new Thickness(0);
        _selectionOutline.BorderBrush = theme.Brush(_layout.SelectedFrameColor.Replace("token:", string.Empty, StringComparison.Ordinal));
        _selectionOutline.Visibility = card.IsSelected ? Visibility.Visible : Visibility.Collapsed;

        if (_layout.MetadataPlacement != MediaMetadataPlacement.Hidden) UpdateMetadata(card);

        // Attention is immutable for a bound MediaGridItem; avoid rebuilding its badge
        // on every selection, favorite or thumbnail notification.
        if (card.HasAttention && _badges.Children.Count == 0)
            _badges.Children.Add(UI.Badge("!", "warning"));
        else if (!card.HasAttention && _badges.Children.Count != 0)
            _badges.Children.Clear();
        _placeholder.Visibility = card.IsModel && card.ThumbnailSource is null && string.IsNullOrWhiteSpace(card.ThumbnailPath)
            ? Visibility.Visible : Visibility.Collapsed;
        _typeText.Text = MediaSurfaceText.Type(card.MediaType);
        _typeBadge.Visibility = !_live && _layout.ShowTypeBadge && !card.IsImage
            && _layout.MetadataPlacement is not (MediaMetadataPlacement.HoverOverlay or MediaMetadataPlacement.PersistentOverlay)
            ? Visibility.Visible : Visibility.Collapsed;

        if (_favorite.Content is IconView icon)
        {
            icon.ColorToken = card.IsFavorite ? "#FF9A45" : "onMediaSecondary";
            icon.Selected = card.IsFavorite;
        }
        _favorite.Opacity = card.IsFavorite ? 1 : 0.72;
        if (_live)
        {
            _liveHeart.Filled = card.IsFavorite;
            _liveHeart.Selected = _layout.Visuals?.GetValueOrDefault("favorite") is not null && card.IsFavorite;
            _liveHeart.ColorToken = card.IsFavorite ? "#FFFF729B" : "onMediaSecondary";
        }
        var favoriteLabel = card.IsFavorite
            ? UI.T("Media.Favorite.Remove", "Remove from favorites")
            : UI.T("Media.Favorite.Add", "Mark as favorite");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_favorite, favoriteLabel);
        ToolTipService.SetToolTip(_favorite, favoriteLabel);
    }

    private void UpdateMetadata(MediaGridCardViewModel card)
    {
        var lines = _metadataLines;
        if (_live)
        {
            _liveTitle.Text = card.DisplayFileName ?? string.Empty;
            _liveTitle.Tip(_liveTitle.Text);
            var details = new List<string>(3);
            if (_layout.MetadataFields.Contains("dimensions") && card.PixelWidth is > 0 && card.PixelHeight is > 0)
                details.Add($"{card.PixelWidth} \u00D7 {card.PixelHeight}");
            if (_layout.MetadataFields.Contains("duration") && card.IsVideo && !string.IsNullOrWhiteSpace(card.FormattedDuration))
                details.Add(card.FormattedDuration);
            if (_layout.MetadataFields.Contains("type")) details.Add(MediaSurfaceText.Type(card.MediaType));
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i].Text = i < details.Count ? details[i] : string.Empty;
                lines[i].Visibility = i < details.Count ? Visibility.Visible : Visibility.Collapsed;
            }
            return;
        }

        lines[0].Text = _layout.MetadataFields.Contains("name")
            ? card.DisplayFileName ?? string.Empty
            : string.Empty;

        var detailParts = new List<string>();
        if (_layout.MetadataFields.Contains("type"))
        {
            detailParts.Add(MediaSurfaceText.Type(card.MediaType));
        }
        if (_layout.MetadataFields.Contains("format"))
        {
            var format = Path.GetExtension(card.DisplayFileName ?? string.Empty).TrimStart('.');
            detailParts.Add(string.IsNullOrWhiteSpace(format)
                ? MediaSurfaceText.Type(card.MediaType) : format.ToUpperInvariant());
        }
        if (_layout.MetadataFields.Contains("duration") && card.IsVideo && !string.IsNullOrWhiteSpace(card.FormattedDuration))
        {
            detailParts.Add(card.FormattedDuration);
        }
        if (_layout.MetadataFields.Contains("dimensions") && card.PixelWidth is > 0 && card.PixelHeight is > 0)
        {
            detailParts.Add($"{card.PixelWidth}×{card.PixelHeight}");
        }

        if (_layout.MetadataFields.Contains("added") && card.Item.AddedToLibraryAtUtc is { } added)
        {
            detailParts.Add(added.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture));
        }

        var stateParts = new List<string>();
        if (_layout.MetadataFields.Contains("favorite") && card.IsFavorite)
        {
            stateParts.Add(UI.T("Gallery.Favorites", "Favorites"));
        }

        var groups = new[] { lines[0].Text, string.Join(" · ", detailParts), string.Join(" · ", stateParts) }
            .Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
        var budget = Math.Min(_layout.MetadataLines, lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index].Text = index < budget && index < groups.Count
                ? index == budget - 1 ? string.Join(" · ", groups.Skip(index)) : groups[index]
                : string.Empty;
            lines[index].Visibility = string.IsNullOrWhiteSpace(lines[index].Text)
                ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_card is not { } card)
        {
            return;
        }

        if (_layout.HoverStyle == "lift" && !ReducedMotionAuthority.IsReduced)
        {
            _scale.ScaleX = _scale.ScaleY = ThemeRuntime.Current.Tokens.Number("motionCardLiftScale", 1.015);
        }

        if (_layout.MetadataPlacement == MediaMetadataPlacement.HoverOverlay)
        {
            _metadata.Opacity = 1;
        }

        if (card.IsVideo && card.HoverPath is { } hoverPath)
        {
            HoverVideoCoordinator.Shared.Play(_playbackHost, hoverPath);
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _scale.ScaleX = _scale.ScaleY = 1;
        if (_layout.MetadataPlacement == MediaMetadataPlacement.HoverOverlay)
        {
            _metadata.Opacity = 0;
        }

        HoverVideoCoordinator.Shared.Release(_playbackHost);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_card is not { } card)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            return;
        }

        if (_onOpen is not null)
        {
            HoverVideoCoordinator.Shared.StopAll();
            _onOpen(card);
        }
    }

    public new void Dispose() => Unbind();
}
