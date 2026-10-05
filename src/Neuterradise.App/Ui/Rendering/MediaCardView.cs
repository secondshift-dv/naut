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
    private readonly Border _metadataOverlay;
    private readonly Border _metadataBelow;
    private readonly Border _metadataSide;
    private readonly StackPanel _overlayText;
    private readonly StackPanel _belowText;
    private readonly StackPanel _sideText;
    private readonly StackPanel _badges = UI.H(4);
    private readonly TextBlock _typeText = UI.Text(string.Empty, "caption", "onMediaSecondary");
    private readonly Border _typeBadge;
    private readonly Button _favorite;
    private readonly ScaleTransform _scale = new();
    private readonly Action<MediaGridCardViewModel>? _onFavorite;
    private readonly Action<MediaGridCardViewModel>? _onOpen;
    private readonly Action<MediaGridCardViewModel>? _onInspect;
    private MediaGridCardViewModel? _card;
    private MediaLayoutDefinition _layout;
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

        _overlayText = MetadataText(onMedia: true);
        _metadataOverlay = new Border
        {
            Child = _overlayText,
            Padding = new Thickness(8, 6, 8, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = ThemeRuntime.Current.Brush("mediaOverlay"),
            Visibility = Visibility.Collapsed,
        };
        _mediaHost.Children.Add(_metadataOverlay);

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

        var inspect = UI.IconButton("icon.status.info", UI.T("Inspector.Title", "Media info"), null, 28);
        inspect.IsHitTestVisible = onInspect is not null;
        inspect.PointerPressed += (_, e) => e.Handled = true;
        inspect.Click += (_, _) =>
        {
            if (_card is { } card)
            {
                _onInspect?.Invoke(card);
            }
        };

        var open = UI.IconButton("icon.action.open", UI.T("Inspector.Open", "Open with default app"), null, 28);
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

        var actions = UI.H(4, inspect, open);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.VerticalAlignment = VerticalAlignment.Top;
        actions.Visibility = onInspect is null && onOpen is null ? Visibility.Collapsed : Visibility.Visible;
        var controlRail = UI.V(4, actions, _badges);
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

        _frame = new Border
        {
            Child = _mediaHost,
            Background = ThemeRuntime.Current.Brush("surface2"),
        };
        Children.Add(_frame.At(0, 0));

        _belowText = MetadataText(onMedia: false);
        _metadataBelow = new Border
        {
            Child = _belowText,
            Padding = new Thickness(3, 6, 3, 0),
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_metadataBelow.At(1, 0));
        Grid.SetColumnSpan(_metadataBelow, 2);

        _sideText = MetadataText(onMedia: false);
        _metadataSide = new Border
        {
            Child = _sideText,
            Padding = new Thickness(10, 8, 8, 8),
            Background = ThemeRuntime.Current.Brush("surface2"),
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_metadataSide.At(0, 1));

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
        _metadataSide.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        _metadataBelow.Visibility = layout.MetadataPlacement == MediaMetadataPlacement.Below
            ? Visibility.Visible
            : Visibility.Collapsed;

        var overlayVisible = layout.MetadataPlacement is MediaMetadataPlacement.HoverOverlay or MediaMetadataPlacement.PersistentOverlay;
        _metadataOverlay.Visibility = overlayVisible ? Visibility.Visible : Visibility.Collapsed;
        _metadataOverlay.Opacity = layout.MetadataPlacement == MediaMetadataPlacement.HoverOverlay ? 0 : 1;
        _metadataOverlay.Background = theme.Brush("mediaOverlay");
        _selectionOutline.CornerRadius = new CornerRadius(Math.Max(0, layout.FrameRadius - layout.FrameThickness));

        _frame.CornerRadius = new CornerRadius(layout.FrameRadius);
        _image.CornerRadiusValue = Math.Max(0, layout.FrameRadius - layout.FrameThickness);
        _innerFrame.CornerRadius = new CornerRadius(Math.Max(0, layout.FrameRadius - 4));

        var layered = layout.FrameStyle is MediaFrameStyle.Double or MediaFrameStyle.Gallery or MediaFrameStyle.Luminous or MediaFrameStyle.Neon;
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

    public void Bind(MediaGridCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        Unbind();
        _card = card;
        DataContext = card;
        _observer = (_, _) => UiDispatch.Run(UpdateVisual);
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

        var thickness = _layout.FrameStyle == MediaFrameStyle.None ? 0 : _layout.FrameThickness;
        _frame.BorderBrush = theme.Brush(_layout.FrameColor.Replace("token:", string.Empty, StringComparison.Ordinal));
        _frame.BorderThickness = new Thickness(thickness);
        _selectionOutline.BorderBrush = theme.Brush(_layout.SelectedFrameColor.Replace("token:", string.Empty, StringComparison.Ordinal));
        _selectionOutline.Visibility = card.IsSelected ? Visibility.Visible : Visibility.Collapsed;

        UpdateMetadata(_overlayText, card);
        UpdateMetadata(_belowText, card);
        UpdateMetadata(_sideText, card);

        _badges.Children.Clear();
        _placeholder.Visibility = card.IsModel && card.ThumbnailSource is null && string.IsNullOrWhiteSpace(card.ThumbnailPath)
            ? Visibility.Visible : Visibility.Collapsed;
        _typeText.Text = MediaSurfaceText.Type(card.MediaType);
        _typeBadge.Visibility = _layout.ShowTypeBadge && !card.IsImage
            && _layout.MetadataPlacement is not (MediaMetadataPlacement.HoverOverlay or MediaMetadataPlacement.PersistentOverlay)
            ? Visibility.Visible : Visibility.Collapsed;

        if (card.HasAttention)
        {
            _badges.Children.Add(UI.Badge("!", "warning"));
        }

        if (_favorite.Content is IconView icon)
        {
            icon.ColorToken = card.IsFavorite ? "danger" : "onMediaSecondary";
        }
        _favorite.Opacity = card.IsFavorite ? 1 : 0.72;
    }

    private void UpdateMetadata(StackPanel panel, MediaGridCardViewModel card)
    {
        var lines = panel.Children.OfType<TextBlock>().ToList();
        if (lines.Count < 2)
        {
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
        var budget = Math.Min(_layout.MetadataLines, lines.Count);
        for (var index = 0; index < lines.Count; index++)
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
            _metadataOverlay.Opacity = 1;
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
            _metadataOverlay.Opacity = 0;
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

    private static StackPanel MetadataText(bool onMedia) => UI.V(
        1,
        UI.Text(null, "caption", onMedia ? "onMediaPrimary" : "textSecondary", 1),
        UI.Text(null, "micro", onMedia ? "onMediaSecondary" : "textMuted", 1),
        UI.Text(null, "micro", onMedia ? "onMediaSecondary" : "textMuted", 1));

    public new void Dispose() => Unbind();
}
