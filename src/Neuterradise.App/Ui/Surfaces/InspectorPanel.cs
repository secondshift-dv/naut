using System.ComponentModel;
using Microsoft.UI.Xaml;
using ToggleButton = Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Media;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Resources;

namespace Neuterradise.App.Ui;

public sealed class InspectorPanel : IDisposable
{
    private readonly AppServices _services;
    private readonly MediaDetailViewModel _vm;
    private readonly Disposables _bag = new();
    private readonly StackPanel _tab = UI.V(8);
    private readonly Grid _preview = new() { Height = 200 };
    private readonly VariableWrap _imageControls = UI.Wrap(4);
    private readonly VariableWrap _modelControls = UI.Wrap(4);
    private readonly TextBlock _zoomText = UI.Text("100%", "caption");
    private string? _previewIdentity;
    private readonly TextBlock _title = UI.Text(null, "body-strong", maxLines: 1);
    private readonly Dictionary<string, Button> _tabs = [];
    private readonly Button _favorite;

    public InspectorPanel(AppServices services, MediaDetailViewModel vm, Action close, PresentationContext context)
    {
        _services = services;
        _vm = vm;

        _imageControls.Children.Add(UI.IconButton("icon.action.chevron", UI.T("Inspector.ZoomOut", "Zoom out"), null, 28, _vm.ZoomOutCommand));
        _imageControls.Children.Add(_zoomText.Align(vertical: VerticalAlignment.Center));
        _imageControls.Children.Add(UI.IconButton("icon.action.add", UI.T("Inspector.ZoomIn", "Zoom in"), null, 28, _vm.ZoomInCommand));
        _imageControls.Children.Add(UI.Button(UI.T("Inspector.Fit", "Fit"), null, ButtonKind.Ghost, command: _vm.FitCommand));
        _imageControls.Children.Add(UI.Button(UI.T("Inspector.ActualSize", "Actual size"), null, ButtonKind.Ghost, command: _vm.ActualSizeCommand));

        _modelControls.Children.Add(UI.Button(UI.T("Inspector.Rotate", "Rotate"), null, ButtonKind.Ghost, command: _vm.RotateCommand));
        _modelControls.Children.Add(UI.Button(UI.T("Inspector.ResetCamera", "Reset camera"), null, ButtonKind.Ghost, command: _vm.ResetCameraCommand));

        var tabs = UI.Grid("auto", "*,*,*");
        tabs.ColumnSpacing = 4;
        foreach (var name in new[] { "Info", "People", "Exif" })
        {
            var tabName = name;
            var button = UI.Button(UI.T("Inspector." + name, name), () => _vm.SelectTabCommand.Execute(tabName), ButtonKind.Ghost);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            _tabs.Add(name, button);
            tabs.Children.Add(button.At(0, tabs.Children.Count));
        }

        _favorite = UI.IconButton("icon.action.favorite", _vm.FavoriteToggleTooltip, null, 28, _vm.ToggleFavoriteCommand);
        var header = UI.Grid("auto", "*,auto",
            UI.V(2,
                UI.Text(UI.T("Inspector.Title", "Media info"), "micro", "textMuted"),
                _title).At(0, 0),
            UI.IconButton("icon.action.close", UI.T("Common.Close", "Close"), close, 28).At(0, 1));
        var previous = UI.IconButton("icon.action.back", UI.T("Common.Previous", "Previous"), null, 28, _vm.PreviousCommand);
        var next = UI.IconButton("icon.action.back", UI.T("Common.Next", "Next"), null, 28, _vm.NextCommand);
        if (next.Content is IconView nextIcon)
        {
            nextIcon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            nextIcon.RenderTransform = new ScaleTransform { ScaleX = -1 };
        }
        var navigation = UI.Grid("auto", "*,auto",
            UI.H(4, previous, next).At(0, 0),
            _favorite.At(0, 1));
        var previewFrame = UI.Surface(_preview, Material.Grounded, 10, 0);
        previewFrame.BorderBrush = ThemeRuntime.Current.Brush("borderSubtle");
        var previewControls = new Grid();
        _imageControls.HorizontalAlignment = HorizontalAlignment.Center;
        _modelControls.HorizontalAlignment = HorizontalAlignment.Center;
        previewControls.Children.Add(_imageControls);
        previewControls.Children.Add(_modelControls);
        View = new Border
        {
            Child = UI.Grid("auto,auto,auto,auto,auto,*", "*",
                header.Margin(12, 12, 12, 8).At(0),
                previewFrame.Margin(12, 0, 12, 6).At(1),
                previewControls.Margin(12, 0, 12, 6).At(2),
                navigation.Margin(12, 0, 12, 8).At(3),
                tabs.Margin(12, 0, 12, 8).At(4),
                UI.Scroll(_tab).Margin(12, 0, 12, 12).At(5)),
            Background = ThemeRuntime.Current.Brush("surface1"),
            BorderBrush = ThemeRuntime.Current.Brush("borderDefault"),
            BorderThickness = new Thickness(1, 0, 0, 0),
        };
        View.SizeChanged += (_, args) => _preview.Height = Math.Clamp(args.NewSize.Height * 0.34, 100, 220);
        _preview.SizeChanged += (_, _) => UpdateImagePreviewTransform();
        _bag.Add(Observe.Props(_vm, Render));
    }

    public FrameworkElement View { get; }

    private void Render()
    {
        _title.Text = _vm.OriginalName;
        _favorite.Tip(_vm.FavoriteToggleTooltip);
        if (_favorite.Content is IconView favoriteIcon)
        {
            favoriteIcon.ColorToken = _vm.IsFavorite ? "danger" : "textMuted";
        }
        foreach (var (name, button) in _tabs)
        {
            DetailLayout.Select(button, _vm.ActiveTab == name);
        }
        _zoomText.Text = _vm.ZoomText;
        _imageControls.Visibility = _vm.MediaType == MediaType.Image ? Visibility.Visible : Visibility.Collapsed;
        // Model previews are static thumbnails until a camera-capable renderer is available.
        _modelControls.Visibility = Visibility.Collapsed;

        var previewIdentity = string.Join("|", _vm.MediaType, _vm.PreviewFilePath, _vm.PresentationPreviewPath, _vm.HasHoverFile);
        if (!string.Equals(_previewIdentity, previewIdentity, StringComparison.Ordinal))
        {
            _previewIdentity = previewIdentity;
            _preview.Children.Clear();
            if (_vm.PresentationPreviewPath is { } thumbnailPath)
            {
                _preview.Children.Add(new SkImageView
                {
                    Source = new ImageRef(thumbnailPath, 1200),
                    Transform = MediaTransformState.Default with { Fit = "fit" },
                    CornerRadiusValue = 10,
                    PlaceholderToken = "surface2",
                });
            }

            if (_preview.Children.Count == 0)
            {
                var icon = _vm.MediaType switch
                {
                    MediaType.Video => "icon.media.video",
                    MediaType.Model => "icon.media.model",
                    _ => "icon.media.image",
                };
                _preview.Children.Add(UI.Surface(UI.V(8,
                    new IconView(icon, 38, "textMuted").Align(HorizontalAlignment.Center),
                    UI.Text(_vm.OriginalName, "control", maxLines: 1).Align(HorizontalAlignment.Center),
                    UI.Text(UI.T("Inspector.PreviewUnavailable", "Preview unavailable"), "caption", "textMuted").Align(HorizontalAlignment.Center)), Material.Raised, 10, 16));
            }
        }

        UpdateImagePreviewTransform();
        _tab.Children.Clear();
        _tab.Children.Add(_vm.ActiveTab switch
        {
            "People" => DetailLayout.Heading(UI.T("Inspector.Guidance.People.Title", "People & associations"),
                UI.T("Inspector.Guidance.People.Detail", "Shows the owning Profile and other Profiles linked to or detected in this media."), "icon.profile.related"),
            "Exif" => DetailLayout.Heading(UI.T("Inspector.Guidance.Exif.Title", "Original metadata"),
                UI.T("Inspector.Guidance.Exif.Detail", "Evidence from embedded metadata and the recorded import source: creator, source, time and tools. Import paths do not establish the upstream origin."), "icon.status.info"),
            _ => DetailLayout.Heading(UI.T("Inspector.Guidance.Summary.Title", "Media summary"),
                null, "icon.media.image"),
        });

        switch (_vm.ActiveTab)
        {
            case "People":
                if (_vm.IsPeopleLoading)
                {
                    _tab.Children.Add(UI.Text(UI.T("Inspector.LoadingPeople", "Loading people…"), "body-muted"));
                    break;
                }

                _tab.Children.Add(UI.Grid("auto", "*,auto",
                    Row(UI.T("Inspector.Owner", "Profile"), _vm.PrimaryProfileName).At(0, 0),
                    UI.Button(
                        UI.T("Common.Edit", "Edit"),
                        () => _services.RunUserAction(
                            OpenProfilePickerAsync(changePrimary: true),
                            "Inspector.ChangePrimaryProfile",
                            UI.T("Inspector.ProfilePickerFailed", "Profiles could not be loaded.")),
                        ButtonKind.Ghost).At(0, 1)));

                _tab.Children.Add(UI.Text(UI.T("Inspector.Associations", "Associations"), "body-strong").Margin(0, 8, 0, 0));
                foreach (var person in _vm.PeopleInMedia)
                {
                    _tab.Children.Add(UI.Grid("auto", "*,auto",
                        Row(person.DisplayName, MediaSurfaceText.AppearanceBasis(person.AppearanceBasis)).At(0, 0),
                        UI.IconButton(
                            "icon.action.delete",
                            UI.T("Inspector.RemoveAssociation", "Remove association"),
                            null,
                            26,
                            _vm.RemoveAssociationCommand,
                            (person.ProfileId, ProfileMediaRelation.Appears)).At(0, 1)));
                }
                foreach (var linked in _vm.LinkedProfiles)
                {
                    _tab.Children.Add(UI.Grid("auto", "*,auto",
                        Row(linked.DisplayName, UI.T("Inspector.ManualAssociation", "Associated")).At(0, 0),
                        UI.IconButton(
                            "icon.action.delete",
                            UI.T("Inspector.RemoveAssociation", "Remove association"),
                            null,
                            26,
                            _vm.RemoveAssociationCommand,
                            (linked.ProfileId, ProfileMediaRelation.Manual)).At(0, 1)));
                }
                _tab.Children.Add(UI.Button(
                    UI.T("Inspector.AddAssociation", "Add person association"),
                    () => _services.RunUserAction(
                        OpenProfilePickerAsync(changePrimary: false),
                        "Inspector.AddAssociation",
                        UI.T("Inspector.ProfilePickerFailed", "Profiles could not be loaded.")),
                    ButtonKind.Ghost,
                    "icon.action.add"));
                break;

            case "Exif":
                if (_vm.IsFileLoading)
                {
                    _tab.Children.Add(UI.Text(UI.T("Inspector.Metadata.Loading", "Reading original metadata…"), "body-muted"));
                    break;
                }

                foreach (var section in MediaProvenanceProjection.Build(_vm.FileDetailGroups))
                {
                    var title = section.GroupName switch
                    {
                        "Who" => UI.T("Inspector.Metadata.Who", "Who"),
                        "Where" => UI.T("Inspector.Metadata.Where", "Where"),
                        "When" => UI.T("Inspector.Metadata.When", "When"),
                        _ => UI.T("Inspector.Metadata.How", "How"),
                    };
                    AddExifSection(title, section.Rows);
                }
                if (_tab.Children.Count == 1)
                {
                    _tab.Children.Add(UI.Text(
                        UI.T("Inspector.Metadata.Empty", "No embedded provenance or recorded import source was found."),
                        "body-muted"));
                }
                _tab.Children.Add(UI.Button(
                    UI.T("Media.Exif.Refresh", "Read metadata again"),
                    null, ButtonKind.Secondary, "icon.action.restore", _vm.RefreshMetadataCommand));
                break;
            default:
                _tab.Children.Add(Row(UI.T("Inspector.FileName", "Filename"), _vm.OriginalName));
                _tab.Children.Add(Row(UI.T("Inspector.Type", "Type"), MediaSurfaceText.Type(_vm.MediaType)));
                switch (_vm.Info)
                {
                    case ImageMediaInfo image:
                        _tab.Children.Add(Row(UI.T("Inspector.Format", "Format"), image.Format));
                        if (image.PixelWidth > 0 && image.PixelHeight > 0)
                        {
                            _tab.Children.Add(Row(UI.T("Inspector.Dimensions", "Dimensions"), $"{image.PixelWidth}×{image.PixelHeight}"));
                        }
                        break;
                    case VideoMediaInfo video:
                        _tab.Children.Add(Row(UI.T("Inspector.Format", "Format"), video.Container));
                        if (video.PixelWidth > 0 && video.PixelHeight > 0)
                        {
                            _tab.Children.Add(Row(UI.T("Inspector.Dimensions", "Dimensions"), $"{video.PixelWidth}×{video.PixelHeight}"));
                        }
                        if (video.Duration > TimeSpan.Zero)
                        {
                            _tab.Children.Add(Row(UI.T("Inspector.Duration", "Duration"), video.Duration.ToString(@"h\:mm\:ss")));
                        }
                        _tab.Children.Add(Row(
                            UI.T("Inspector.Codec", "Codec"),
                            string.Join(" · ", new[] { video.VideoCodec, video.AudioCodec }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
                        break;
                    case ModelMediaInfo model:
                        _tab.Children.Add(Row(UI.T("Inspector.Format", "Format"), model.Format));
                        if (model.MeshCount is { } meshes)
                        {
                            _tab.Children.Add(Row(UI.T("Inspector.Meshes", "Meshes"), meshes.ToString(System.Globalization.CultureInfo.CurrentCulture)));
                        }
                        if (model.MaterialCount is { } materials)
                        {
                            _tab.Children.Add(Row(UI.T("Inspector.Materials", "Materials"), materials.ToString(System.Globalization.CultureInfo.CurrentCulture)));
                        }
                        break;
                }
                _tab.Children.Add(Row(UI.T("Inspector.Size", "Size"), _vm.ByteLengthText));
                _tab.Children.Add(Row(UI.T("Inspector.Added", "Added"), _vm.AddedToLibraryAtText));
                _tab.Children.Add(Row(UI.T("Inspector.Favorite", "Favorite"), _vm.IsFavorite ? UI.T("Common.Yes", "Yes") : UI.T("Common.No", "No")));
                _tab.Children.Add(Row(UI.T("Inspector.Status", "Status"), _vm.StatusText));
                if (_vm.Detail is { } detail)
                {
                    if (!string.IsNullOrWhiteSpace(detail.CurrentLibraryLocation))
                        _tab.Children.Add(Row(MediaSurfaceText.DetailField("Vault Location (current)"), detail.CurrentLibraryLocation));
                    if (!string.IsNullOrWhiteSpace(detail.TargetLibraryLocation))
                        _tab.Children.Add(Row(MediaSurfaceText.DetailField("Pending Target Location"), detail.TargetLibraryLocation));
                }
                if (_vm.HasActionFeedback)
                {
                    _tab.Children.Add(UI.WrappedText(MediaSurfaceText.InspectorFeedback(_vm.ActionFeedbackMessage), "caption", "accent"));
                }
                break;
        }
    }

    private async Task OpenProfilePickerAsync(bool changePrimary)
    {
        IReadOnlyList<ProfilePickerItem> candidates = await _vm
            .GetProfilePickerCandidatesAsync(_vm.OwnerProfile?.ProfileId)
            .ConfigureAwait(true);

        if (!changePrimary && _vm.PeopleInMedia.Count > 0)
        {
            var existing = _vm.PeopleInMedia.Select(person => person.ProfileId).ToHashSet();
            candidates = candidates.Where(candidate => !existing.Contains(candidate.ProfileId)).ToArray();
        }

        var request = new ProfilePickerOverlayRequest(
            candidates,
            picked =>
            {
                if (changePrimary)
                {
                    _vm.ChangePrimaryProfileCommand.Execute(picked.ProfileId);
                }
                else
                {
                    _vm.AddAssociationCommand.Execute((picked.ProfileId, ProfileMediaRelation.Appears));
                }
            },
            title: changePrimary
                ? UI.T("Inspector.ChangePrimaryProfile", "Change primary Profile")
                : UI.T("Inspector.AddAssociation", "Add person association"),
            prompt: changePrimary
                ? UI.T("Inspector.ChangePrimaryProfilePrompt", "Choose the new primary Profile for this media item:")
                : UI.T("Inspector.AddAssociationPrompt", "Choose a Profile that appears in this media item:"));

        _services.Overlay.Push(request);
    }

    private void UpdateImagePreviewTransform()
    {
        var image = _preview.Children.OfType<SkImageView>().FirstOrDefault();
        _imageControls.Visibility = _vm.MediaType == MediaType.Image && image is not null
            ? Visibility.Visible : Visibility.Collapsed;
        if (_vm.MediaType == MediaType.Image && image is not null)
        {
            ApplyImagePreviewTransform(image);
        }
    }

    private void ApplyImagePreviewTransform(SkImageView image)
    {
        var scale = _vm.IsActualSize ? GetActualImageScale() : _vm.ZoomLevel;
        image.Transform = MediaTransformState.Default with
        {
            Fit = "fit",
            Zoom = Math.Max(1.0, scale),
        };

        image.RenderTransform = scale < 1.0
            ? new ScaleTransform { ScaleX = scale, ScaleY = scale }
            : null;
        image.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
    }

    private double GetActualImageScale()
    {
        if (_vm.Info is not ImageMediaInfo image || image.PixelWidth <= 0 || image.PixelHeight <= 0)
        {
            return 1.0;
        }

        var slotWidth = _preview.ActualWidth > 1 ? _preview.ActualWidth : 360.0;
        var slotHeight = _preview.ActualHeight > 1 ? _preview.ActualHeight : 260.0;
        var fittedScale = Math.Min(slotWidth / image.PixelWidth, slotHeight / image.PixelHeight);
        return fittedScale > 0 ? 1.0 / fittedScale : 1.0;
    }

    private void AddExifSection(string title, IReadOnlyList<MediaFileDetailRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        _tab.Children.Add(UI.Text(title, "body-strong").Margin(0, 8, 0, 0));
        foreach (var row in rows)
        {
            _tab.Children.Add(Row(
                MediaSurfaceText.DetailField(row.Label),
                MediaSurfaceText.DetailValue(row.Label, row.Value)));
        }
    }

    private static FrameworkElement Row(string label, string? value) =>
        DetailLayout.Property(label, value);

    public void Dispose()
    {
        _bag.Dispose();
    }
}
