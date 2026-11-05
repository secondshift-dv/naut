using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Presentation;

namespace Neuterradise.App.Ui;

/// <summary>A pooled portrait with a square frame allocation and a separate readable caption.</summary>
public sealed class HomeProfileTile : Button
{
    private readonly CoverFrameView _cover = new() { FrameMode = "lite" };
    private readonly TextBlock _name = UI.Text(null, "control", maxLines: 1);
    private readonly TextBlock _count = UI.Text(null, "micro", "textMuted", 1);
    private readonly IconView _favorite = new("icon.profile.favorite", 12, "warning");
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

    public void Bind(Guid id, string name, string? cover, string count, bool favorite)
    {
        BindingVersion++;
        ProfileId = id;
        DataContext = id;
        _name.Text = name;
        _name.Tip(name);
        _count.Text = count;
        _favorite.Visibility = favorite ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, name);
        _cover.Plan = null;
        _cover.Appearance = null;
        _cover.Transform = MediaTransformState.Default;
        _cover.Source = ImageRef.FromPath(cover, 320);
    }

    public void Clear() => Bind(Guid.Empty, string.Empty, null, string.Empty, false);

    public void Apply(FramePlan frame, Neuterradise.App.Design.CoverFrames.CoverAppearance appearance, MediaTransformState transform)
    {
        _cover.Plan = frame;
        _cover.Appearance = appearance;
        _cover.Transform = transform;
    }
}
