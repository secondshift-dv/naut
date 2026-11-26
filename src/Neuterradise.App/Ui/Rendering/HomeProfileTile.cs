using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Presentation;

namespace Neuterradise.App.Ui;

/// <summary>A pooled portrait with a square frame allocation and a separate readable caption.</summary>
public sealed class HomeProfileTile : Button
{
    private readonly CoverFrameView? _cover;
    private readonly SkImageView? _flatCover;
    private Border? _hoverCaption;
    private ScaleTransform? _hoverScale;
    private readonly TextBlock _name = UI.Text(null, "control", maxLines: 1);
    private readonly TextBlock _count = UI.Text(null, "micro", "textMuted", 1);
    private readonly IconView? _favorite;
    public Guid ProfileId { get; private set; }
    public int BindingVersion { get; private set; }

    public HomeProfileTile(string style, double width, double height, Action<Guid>? navigate = null)
    {
        Width = width;
        Height = height;
        Padding = new Thickness(4);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Background = ThemeRuntime.Current.Brush("transparent");
        BorderThickness = new Thickness(0);
        if (style == "cover-only")
        {
            Padding = new Thickness(0);
            MinWidth = MinHeight = 0;
            _flatCover = new SkImageView { PlaceholderToken = "surface2", CornerRadiusValue = 5 };
            _flatCover.IsHitTestVisible = false;
            var labelsOnly = UI.V(1, _name, _count);
            _hoverCaption = new Border
            {
                Child = labelsOnly, Background = ThemeRuntime.Current.Brush("surfaceOverlay"),
                Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            };
            var bodyOnly = new Grid { IsHitTestVisible = false };
            bodyOnly.Children.Add(_flatCover);
            bodyOnly.Children.Add(_hoverCaption);
            _hoverScale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
            bodyOnly.RenderTransform = _hoverScale;
            bodyOnly.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            Content = bodyOnly;
            PointerEntered += (_, _) => SetCoverFocus(true);
            PointerExited += (_, _) => SetCoverFocus(false);
            GotFocus += (_, _) => SetCoverFocus(true);
            LostFocus += (_, _) => SetCoverFocus(false);
            Click += (_, _) => { if (ProfileId != Guid.Empty) navigate?.Invoke(ProfileId); };
            return;
        }
        _cover = new CoverFrameView { FrameMode = "lite" };
        _favorite = new IconView("icon.profile.favorite", 12, "warning");
        var portrait = style is "strip" or "compact";
        var size = portrait ? height - 8 : Math.Min(width - 8, height - 48);
        _cover.Width = _cover.Height = Math.Max(16, size);
        _cover.HorizontalAlignment = HorizontalAlignment.Center;
        _cover.VerticalAlignment = VerticalAlignment.Center;
        var caption = UI.V(2, _name, _count);
        var labels = UI.Grid("auto", "*,auto");
        labels.ColumnSpacing = 4;
        labels.Children.Add(caption.At(0, 0));
        labels.Children.Add(_favorite.At(0, 1));
        _favorite.VerticalAlignment = VerticalAlignment.Top;
        var body = portrait ? UI.Grid("*", "auto,*") : UI.Grid("*,auto", "*");
        body.ColumnSpacing = 10;
        body.RowSpacing = 4;
        body.Children.Add(_cover.At(0, 0));
        body.Children.Add(labels.At(portrait ? 0 : 1, portrait ? 1 : 0));
        labels.VerticalAlignment = VerticalAlignment.Center;
        body.IsHitTestVisible = false;
        Content = body;
        Click += (_, _) => { if (ProfileId != Guid.Empty) navigate?.Invoke(ProfileId); };
    }

    public void Bind(Guid id, string name, string? cover, string count, bool favorite, MediaTransformState? coverTransform = null)
    {
        BindingVersion++;
        ProfileId = id;
        DataContext = id;
        _name.Text = name;
        _name.Tip(name);
        _count.Text = count;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, name);
        if (_flatCover is { } flat)
        {
            SetCoverFocus(false);
            flat.Transform = coverTransform ?? MediaTransformState.Default;
            flat.Source = ImageRef.FromPath(cover, 320);
        }
        else if (_cover is { } frame && _favorite is { } badge)
        {
            badge.Selected = favorite;
            badge.Visibility = favorite ? Visibility.Visible : Visibility.Collapsed;
            frame.Plan = null;
            frame.Appearance = null;
            frame.Transform = coverTransform ?? MediaTransformState.Default;
            frame.Source = ImageRef.FromPath(cover, 320);
        }
    }

    private void SetCoverFocus(bool active)
    {
        if (_hoverCaption is null || _hoverScale is null) return;
        _hoverCaption.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        var scale = active && !ReducedMotionAuthority.IsReduced ? 1.10 : 1;
        _hoverScale.ScaleX = _hoverScale.ScaleY = scale;
    }

    public void Clear() => Bind(Guid.Empty, string.Empty, null, string.Empty, false);

    internal void ApplyCoverTransform(MediaTransformState transform)
    {
        if (_flatCover is { } flat) flat.Transform = transform;
        else if (_cover is { } cover) cover.Transform = transform;
    }

    public void Apply(FramePlan frame, Neuterradise.App.Design.CoverFrames.CoverAppearance appearance, MediaTransformState transform)
    {
        if (_cover is { } cover)
        {
            cover.Plan = frame;
            cover.Appearance = appearance;
        }
        ApplyCoverTransform(transform);
    }
}
