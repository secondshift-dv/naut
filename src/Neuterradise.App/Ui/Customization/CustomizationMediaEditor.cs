using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Gallery;
using Neuterradise.App.Home;
using Neuterradise.App.Media;
using Neuterradise.App.Localization;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Ui;

public sealed partial class CustomizationCenter
{
    private TextBlock? _framingFeedback;
    private bool _framingFeedbackBanner;
    private uint? _framingPointerId;

    private sealed record CustomizationMediaChoice(
        Guid MediaId,
        string? Preview,
        string Name,
        bool Recommended,
        bool VideoFrame,
        long? Timestamp,
        bool Current,
        double FocusX,
        double FocusY);

    // ---------------------------------------------------------------- Cover / Banner

    private bool IsBanner(SlotDescriptor descriptor) => descriptor.Editor == EditorKind.BannerEditor;

    private bool IsCanonical(SlotDescriptor descriptor) => descriptor.Id is PresentationSlots.ProfileCover or PresentationSlots.ProfileBanner;

    private FrameworkElement MediaEditor(SlotDescriptor descriptor)
    {
        if (_session.ProfileWorking is null)
        {
            return UI.Text(UI.T("Customize.ChooseProfileHint", "Choose a Profile above to edit this for that Profile."), "body-muted");
        }

        var banner = IsBanner(descriptor);
        _framingFeedbackBanner = banner;
        _framingFeedback = UI.Text(string.Empty, "micro", "textSecondary");
        UpdateFramingFeedback();
        var role = banner ? UI.T("Customize.Role.Banner", "Banner") : UI.T("Customize.Role.Cover", "Cover");
        var editor = UI.V(10,
            _framingFeedback,
            UI.Guidance(
                UI.F("Customize.Media.AdjustTitle", "Adjust {0}", role),
                UI.T("Customize.Media.AdjustDescription", "Use the mouse wheel over the large preview to zoom. Drag the preview to reposition it. Changes remain a preview until you choose Save changes.")));

        if (IsCanonical(descriptor))
        {
            editor.Children.Add(_subject is null
                ? UI.Text(UI.T("Customize.Loading.ProfileMedia", "Loading Profile media…"), "body-muted")
                : Candidates(banner));
        }

        return editor;
    }

    private FrameworkElement Candidates(bool banner)
    {
        var working = _session.ProfileWorking;
        var sources = working?.Sources;
        var items = (banner
            ? _subject!.BannerCandidates.Select(c => new CustomizationMediaChoice(
                c.MediaId,
                c.PreviewPath,
                c.DisplayTitle,
                c.IsRecommended,
                false,
                null,
                c.MediaId == sources?.BannerMediaId,
                c.SuggestedFocusX,
                c.SuggestedFocusY))
            : _subject!.CoverCandidates.Select(c => new CustomizationMediaChoice(
                c.MediaId,
                c.PreviewPath,
                c.FileName,
                c.IsRecommended,
                c.SourceKind == CoverVisualSourceKind.VideoFrame,
                c.TimestampMilliseconds,
                c.MediaId == sources?.CoverMediaId
                    && c.TimestampMilliseconds == working?.Overrides.CoverVideoTimestampMilliseconds,
                c.SuggestedCropX,
                c.SuggestedCropY)))
            .ToList();

        FrameworkElement BuildGrid(IReadOnlyList<CustomizationMediaChoice> choices)
        {
            var row = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
            var thumbnails = new List<SkImageView>();
            foreach (var candidate in choices)
            {
                var thumb = new SkImageView
                {
                    Source = ImageRef.FromPath(candidate.Preview, 240),
                    Height = banner ? 72 : 96,
                    CornerRadiusValue = 8,
                    PlaceholderToken = "surface2",
                };
                thumbnails.Add(thumb);
                var tile = UI.Surface(
                    UI.V(
                        4,
                        thumb,
                        UI.Text(candidate.Name, "micro", maxLines: 1),
                        candidate.Recommended
                            ? UI.Badge(UI.T("Customize.Recommended", "Suggested"), "accent")
                            : null),
                    Material.Raised,
                    10,
                    6);
                tile.BorderThickness = new Thickness(candidate.Current ? 2 : 1);
                tile.BorderBrush = ThemeRuntime.Current.Brush(candidate.Current ? "borderSelected" : "borderSubtle");
                var value = candidate;
                UI.DisableTextSelection(tile);
                tile.Tapped += (_, _) => SelectSource(
                    banner,
                    value.MediaId,
                    value.VideoFrame,
                    value.Timestamp,
                    value.Preview,
                    value.FocusX,
                    value.FocusY);
                row.Children.Add(tile);
            }

            void Reflow(double width)
            {
                var columns = Math.Clamp((int)Math.Floor((width + 6) / (banner ? 136 : 110)), 1, 5);
                if (row.ColumnDefinitions.Count != columns)
                {
                    row.ColumnDefinitions.Clear();
                    row.RowDefinitions.Clear();
                    for (var column = 0; column < columns; column++)
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    for (var index = 0; index < row.Children.Count; index++)
                    {
                        if (index % columns == 0)
                            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        Grid.SetRow(row.Children[index], index / columns);
                        Grid.SetColumn(row.Children[index], index % columns);
                    }
                }

                var artworkWidth = Math.Max(48, (width - (columns - 1) * 6) / columns - 14);
                foreach (var thumbnail in thumbnails)
                    thumbnail.Height = banner ? artworkWidth * 9 / 16 : artworkWidth;
            }

            Reflow(600);
            row.SizeChanged += (_, _) =>
            {
                if (row.ActualWidth > 0) Reflow(row.ActualWidth);
            };
            return row;
        }

        var root = UI.V(12);
        var suggested = items.Where(static candidate => candidate.Recommended).ToList();
        if (suggested.Count > 0)
        {
            root.Children.Add(UI.Section(
                UI.T("Customize.SuggestedMedia", "Suggested"),
                UI.T("Customize.SuggestedMediaNote", "Top-ranked prepared media for this Profile."),
                BuildGrid(suggested)));
        }

        const int pageSize = 24;
        var browseHost = new StackPanel { Spacing = 8 };
        var currentIndex = items.FindIndex(static candidate => candidate.Current);
        var page = currentIndex < 0 ? 0 : currentIndex / pageSize;

        void RenderBrowsePage()
        {
            browseHost.Children.Clear();
            if (items.Count == 0)
            {
                browseHost.Children.Add(UI.Text(
                    UI.T("Customize.NoEligibleMedia", "No prepared Profile media is eligible for this source."),
                    "body-muted"));
                return;
            }

            var pageCount = Math.Max(1, (items.Count + pageSize - 1) / pageSize);
            page = Math.Clamp(page, 0, pageCount - 1);
            var pageItems = items.Skip(page * pageSize).Take(pageSize).ToList();
            browseHost.Children.Add(BuildGrid(pageItems));

            if (pageCount > 1)
            {
                var previous = UI.Button(
                    UI.T("Common.Previous", "Previous"),
                    () =>
                    {
                        if (page <= 0) return;
                        page--;
                        RenderBrowsePage();
                    },
                    ButtonKind.Ghost);
                previous.IsEnabled = page > 0;
                var next = UI.Button(
                    UI.T("Common.Next", "Next"),
                    () =>
                    {
                        if (page >= pageCount - 1) return;
                        page++;
                        RenderBrowsePage();
                    },
                    ButtonKind.Ghost);
                next.IsEnabled = page < pageCount - 1;
                var navigation = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                navigation.Children.Add(previous);
                navigation.Children.Add(UI.Text(
                    UI.F("Customize.MediaPage", "{0} / {1}", page + 1, pageCount),
                    "caption",
                    "textSecondary"));
                navigation.Children.Add(next);
                browseHost.Children.Add(navigation);
            }
        }

        RenderBrowsePage();
        root.Children.Add(UI.Section(
            banner ? UI.T("Customize.BannerBrowseAll", "Browse all banner media")
                : UI.T("Customize.CoverBrowseAll", "Browse all cover media"),
            UI.T(
                "Customize.SourceNote",
                "Choosing new media only changes the preview; the source and its framing are saved together when you choose Save changes."),
            browseHost));
        return root;
    }
    /// <summary>
    /// Previews one Cover or Banner source without mutating the other role.
    /// Nothing is written until Save changes commits the preview; Cancel or Reset restores the committed source.
    /// </summary>
    private void SelectSource(
        bool banner,
        Guid assetId,
        bool videoFrame,
        long? timestamp,
        string? previewPath,
        double suggestedX,
        double suggestedY)
    {
        if (banner)
        {
            _bannerSource = ImageRef.FromPath(previewPath, 1600) ?? _bannerSource;
            _bannerVideoPath = _subject?.BannerCandidates
                .FirstOrDefault(candidate => candidate.MediaId == assetId)
                ?.PlaybackPath;
            _session.SelectBannerSource(assetId, suggestedX, suggestedY);
        }
        else
        {
            _coverSource = ImageRef.FromPath(previewPath, 800) ?? _coverSource;
            _session.SelectCoverSource(assetId, videoFrame, timestamp, suggestedX, suggestedY);
        }

    }
    private void AttachFraming(FrameworkElement? surface, bool banner)
    {
        if (surface is null)
        {
            return;
        }

        var active = banner ? _slot == PresentationSlots.ProfileBanner : _slot == PresentationSlots.ProfileCover;
        surface.Tag = banner ? "profile-banner-framing" : "profile-cover-framing";
        surface.IsHitTestVisible = active;
        if (!active)
        {
            return;
        }

        if (surface is Border border)
            border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        surface.PointerPressed -= FramingPointerPressed;
        surface.PointerMoved -= FramingPointerMoved;
        surface.PointerReleased -= FramingPointerReleased;
        surface.PointerCanceled -= FramingPointerReleased;
        surface.PointerCaptureLost -= FramingPointerReleased;
        surface.PointerWheelChanged -= FramingPointerWheelChanged;
        surface.PointerPressed += FramingPointerPressed;
        surface.PointerMoved += FramingPointerMoved;
        surface.PointerReleased += FramingPointerReleased;
        surface.PointerCanceled += FramingPointerReleased;
        surface.PointerCaptureLost += FramingPointerReleased;
        surface.PointerWheelChanged += FramingPointerWheelChanged;
    }

    private void OnCoverFramingSurfaceChanged(FrameworkElement? surface) => AttachFraming(surface, false);

    private void FramingPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement surface || !e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed) return;
        _dragging = surface.CapturePointer(e.Pointer);
        _framingPointerId = _dragging ? e.Pointer.PointerId : null;
        _dragPoint = e.GetCurrentPoint(surface).Position;
        UpdateFramingFeedback();
        e.Handled = _dragging;
    }

    private void FramingPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _framingPointerId != e.Pointer.PointerId || sender is not FrameworkElement surface) return;
        var point = e.GetCurrentPoint(surface).Position;
        var deltaX = point.X - _dragPoint.X;
        var deltaY = point.Y - _dragPoint.Y;
        var banner = string.Equals(surface.Tag as string, "profile-banner-framing", StringComparison.Ordinal);
        if (banner)
        {
            var pan = _profilePreviewHeader.GetBannerPanDelta(deltaX, deltaY);
            EditFraming(true, null, pan.X, pan.Y);
        }
        else if (surface is CoverFrameView cover)
        {
            var pan = cover.FocalDeltaForDrag(deltaX, deltaY);
            EditFraming(false, null, pan.X, pan.Y);
        }
        _dragPoint = point;
        e.Handled = true;
    }

    private void FramingPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_framingPointerId != e.Pointer.PointerId) return;
        var wasDragging = _dragging;
        _dragging = false;
        _framingPointerId = null;
        if (sender is FrameworkElement surface && wasDragging) surface.ReleasePointerCapture(e.Pointer);
        UpdateFramingFeedback();
    }

    private void FramingPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement surface) return;
        var banner = string.Equals(surface.Tag as string, "profile-banner-framing", StringComparison.Ordinal);
        var zoom = banner ? _session.ProfileWorking?.Overrides.BannerZoom ?? 1 : _session.ProfileWorking?.Overrides.Zoom ?? 1;
        EditFraming(banner, zoom + e.GetCurrentPoint(surface).Properties.MouseWheelDelta / 120.0 * 0.05, null, null);
        e.Handled = true;
    }

    private void EditFraming(bool banner, double? zoom, double? dx, double? dy)
    {
        if (_session.ProfileWorking is null) return;
        _framingEdit = true;
        try
        {
            if (banner)
                _session.EditBanner(o => o with
                {
                    BannerZoom = Math.Clamp(zoom ?? o.BannerZoom, 1, 2),
                    BannerFocusX = Math.Clamp(o.BannerFocusX + (dx ?? 0), 0, 1),
                    BannerFocusY = Math.Clamp(o.BannerFocusY + (dy ?? 0), 0, 1),
                });
            else
                _session.EditCover(o => o with
                {
                    Zoom = Math.Clamp(zoom ?? o.Zoom, 1, 4),
                    CropX = Math.Clamp(o.CropX + (dx ?? 0), 0, 1),
                    CropY = Math.Clamp(o.CropY + (dy ?? 0), 0, 1),
                });
        }
        finally { _framingEdit = false; }
        UpdateFramingFeedback();
    }

    private void UpdateFramingFeedback()
    {
        if (_framingFeedback is null || _session.ProfileWorking?.Overrides is not { } appearance) return;
        var zoom = _framingFeedbackBanner ? appearance.BannerZoom : appearance.Zoom;
        var x = _framingFeedbackBanner ? appearance.BannerFocusX : appearance.CropX;
        var y = _framingFeedbackBanner ? appearance.BannerFocusY : appearance.CropY;
        _framingFeedback.Text = $"{zoom:P0} · X {x:P0} · Y {y:P0}";
        if (_dragging) _framingFeedback.Text += " · " + UI.T("Customize.Media.Dragging", "Dragging");
    }

}
