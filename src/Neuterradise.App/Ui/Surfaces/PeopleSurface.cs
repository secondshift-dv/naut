using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Faces;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Lifecycle;

namespace Neuterradise.App.Ui;

/// <summary>
/// People & Face Review: optional post-import enrichment with explicit human confirmation. Suggestions
/// never control media ownership; this surface only records face-identity decisions through the existing
/// face decision operations.
/// </summary>
public sealed class PeopleSurface : Surface
{
    private readonly FaceReviewViewModel _vm;
    private readonly Grid _root = new();
    private readonly StackPanel _list = UI.V(8);
    private readonly TextBlock _state = UI.Text(string.Empty, "body-muted");
    private readonly Border _profilingNotice = new() { Visibility = Visibility.Collapsed };
    private readonly Border _emptyState = new() { Visibility = Visibility.Collapsed };
    private readonly Dictionary<Guid, ReviewRow> _rows = new();

    public PeopleSurface(AppServices services, FaceReviewViewModel vm) : base(services)
    {
        _vm = vm;
        _root.Background = ThemeRuntime.Current.Brush("canvas");
        _state.Visibility = Visibility.Collapsed;
        var bulkAssign = UI.Button(UI.T("Faces.AssignAll", "Assign all to this profile"), null, ButtonKind.Secondary, "icon.profile.assign", _vm.AssignAllToThisProfileCommand);
        bulkAssign.HorizontalAlignment = HorizontalAlignment.Left;
        var scopeContext = UI.WrappedText(string.Empty, "caption", "textSecondary");
        void UpdateScopeContext()
        {
            scopeContext.Text = _vm.IsScoped && !string.IsNullOrWhiteSpace(_vm.ScopedProfileDisplayName)
                ? $"{UI.T("Common.Profile", "Profile")}: {_vm.ScopedProfileDisplayName}"
                : string.Empty;
            scopeContext.Visibility = string.IsNullOrWhiteSpace(scopeContext.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        UpdateScopeContext();
        var headerText = UI.V(
            2,
            UI.Text(UI.T("Faces.Title", "People in your media"), "page-title", maxLines: 2),
            UI.WrappedText(UI.T("Faces.Desc", "Confirm who appears in your photos and videos. Suggestions are hints, never certainty."), "body-muted"),
            scopeContext);
        headerText.HorizontalAlignment = HorizontalAlignment.Stretch;
        var headerRow = UI.Grid(
            "auto",
            "auto,*",
            UI.IconButton("icon.action.back", UI.T("Common.Back", "Back"), () => Services.Navigation.GoBack()).At(0, 0),
            headerText.Margin(10, 0, 0, 0).At(0, 1));
        var header = UI.V(6, headerRow, _vm.IsScoped ? bulkAssign : null);
        header.HorizontalAlignment = HorizontalAlignment.Stretch;
        _emptyState.Background = ThemeRuntime.Current.Brush("surface1");
        _emptyState.CornerRadius = new CornerRadius(14);
        _emptyState.Padding = new Thickness(16);

        var page = UI.V(12,
            header,
            _profilingNotice,
            _state,
            _emptyState,
            _list);
        page.MaxWidth = 1100;
        page.HorizontalAlignment = HorizontalAlignment.Stretch;
        _root.Children.Add(UI.Scroll(page.Margin(14, 12, 14, 24)));
        Bag.Add(Observe.Collection(_vm.Reviews, SyncRows));
        Bag.Add(Observe.Props(_vm, UpdateScopeContext, nameof(FaceReviewViewModel.ScopedProfileDisplayName)));
        Bag.Add(Observe.Props(_vm, SyncRows,
            nameof(FaceReviewViewModel.StatusMessage),
            nameof(FaceReviewViewModel.Status),
            nameof(FaceReviewViewModel.ErrorMessage)));
        Bag.Add(Observe.Props(Services.Status, UpdateProfilingNotice, nameof(ShellStatusViewModel.ProfilingState)));
        SyncRows();
        UpdateProfilingNotice();
    }

    public override FrameworkElement View => _root;

    public override Neuterradise.App.Shell.ScreenStateViewModel Model => _vm;

    protected override void OnActivated(AppRoute route) =>
        Services.RunUserAction(_vm.LoadReviewsAsync(), "PeopleSurface.LoadReviewsAsync", UI.T("Faces.LoadFailed", "Face reviews could not be loaded."));

    private void UpdateProfilingNotice()
    {
        if (Services.Status.ProfilingState == ProfilingInitializationState.Unavailable)
        {
            _profilingNotice.Visibility = Visibility.Visible;
            _profilingNotice.Background = ThemeRuntime.Current.Brush("surface2");
            _profilingNotice.CornerRadius = new CornerRadius(12);
            _profilingNotice.Padding = new Thickness(16, 12, 16, 12);
            _profilingNotice.Child = UI.V(4,
                new IconView("icon.profile.face", 20, "warning"),
                UI.Text(
                    UI.T("Faces.ProfilingUnavailable.Title", "Face detection is not available right now"),
                    "body-strong"),
                UI.Text(
                    UI.T("Faces.ProfilingUnavailable.Message", "The face analysis engine could not start. People suggestions will not appear for imported media. You can still assign people manually from each Profile. This does not affect your media or Vault."),
                    "body-muted", maxLines: 4));
        }
        else
        {
            _profilingNotice.Visibility = Visibility.Collapsed;
        }
    }

    private void SyncRows()
    {
        var desiredIds = _vm.Reviews.Select(static review => review.FaceId).ToHashSet();
        foreach (var staleId in _rows.Keys.Where(id => !desiredIds.Contains(id)).ToList())
        {
            _rows[staleId].Dispose();
            _rows.Remove(staleId);
        }

        foreach (var review in _vm.Reviews.Take(200))
        {
            if (!_rows.ContainsKey(review.FaceId))
            {
                _rows.Add(review.FaceId, new ReviewRow(Services, _vm, review));
            }
        }

        _list.Children.Clear();
        foreach (var review in _vm.Reviews.Take(200))
        {
            if (_rows.TryGetValue(review.FaceId, out var row))
            {
                _list.Children.Add(row.View);
            }
        }

        if (_vm.Reviews.Count > 0)
        {
            _emptyState.Visibility = Visibility.Collapsed;
            _state.Text = _vm.StatusMessage ?? string.Empty;
            _state.Visibility = string.IsNullOrWhiteSpace(_state.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            return;
        }

        if (_vm.HasError)
        {
            _emptyState.Visibility = Visibility.Collapsed;
            _state.Text = UI.F(
                "Faces.LoadErrorDetail",
                "Face review could not be loaded.",
                _vm.ErrorMessage ?? _vm.StatusMessage ?? UI.T("Faces.LoadFailed", "Face reviews could not be loaded."));
            _state.Visibility = Visibility.Visible;
            return;
        }

        _state.Visibility = Visibility.Collapsed;
        _emptyState.Visibility = Visibility.Visible;
        var actions = _vm.ProfileId is { } profileId && profileId != Guid.Empty
            ? UI.Wrap(6,
                UI.Button(
                    UI.T("Faces.Empty.OpenProfile", "Open Profile"),
                    () => Services.Navigation.Navigate(new ProfileRoute(profileId)),
                    ButtonKind.Primary,
                    "icon.profile.person"),
                UI.Button(
                    UI.T("Faces.Empty.Import", "Import media"),
                    () => Services.Navigation.ResetToTopLevel(new ImportRoute()),
                    ButtonKind.Secondary,
                    "icon.navigation.import"))
            : UI.Wrap(6,
                UI.Button(
                    UI.T("Faces.Empty.Browse", "Browse Profiles"),
                    () => Services.Navigation.ResetToTopLevel(new GalleryRoute()),
                    ButtonKind.Primary,
                    "icon.navigation.gallery"),
                UI.Button(
                    UI.T("Faces.Empty.Import", "Import media"),
                    () => Services.Navigation.ResetToTopLevel(new ImportRoute()),
                    ButtonKind.Secondary,
                    "icon.navigation.import"));

        actions.HorizontalAlignment = HorizontalAlignment.Left;
        var emptyContent = UI.V(
            10,
            new IconView("icon.profile.face", 32, "textMuted").Align(HorizontalAlignment.Left),
            UI.Text(UI.T("Faces.Empty.Title", "Nothing needs face review"), "section-title").Align(HorizontalAlignment.Left),
            UI.WrappedText(
                UI.T(
                    "Faces.Empty.Detail",
                    "Face review appears after imported photos or videos contain detectable faces. Face analysis is optional and never blocks your media or Profile."),
                "body-muted"),
            actions);
        emptyContent.HorizontalAlignment = HorizontalAlignment.Stretch;
        _emptyState.Child = emptyContent;
    }

    public override void Dispose()
    {
        foreach (var row in _rows.Values)
        {
            row.Dispose();
        }
        _rows.Clear();
        base.Dispose();
    }

    private sealed class ReviewRow : IDisposable
    {
        private readonly AppServices _services;
        private readonly FaceReviewViewModel _owner;
        private readonly FaceReviewItemViewModel _review;
        private readonly Border _host = new();
        private readonly PropertyChangedEventHandler _propertyChanged;

        public ReviewRow(AppServices services, FaceReviewViewModel owner, FaceReviewItemViewModel review)
        {
            _services = services;
            _owner = owner;
            _review = review;
            _host.CornerRadius = new CornerRadius(14);
            _propertyChanged = (_, _) => UiDispatch.Run(Render);
            _review.PropertyChanged += _propertyChanged;
            Render();
        }

        public FrameworkElement View => _host;

        private void Render()
        {
            FrameworkElement crop;
            if (_review.HasFaceCrop)
            {
                crop = new SkImageView
                {
                    Source = ImageRef.FromPath(_review.FaceCropPath, 240),
                    Width = 80,
                    Height = 80,
                    CornerRadiusValue = 12
                };
            }
            else
            {
                var fallback = UI.Surface(
                    UI.V(4,
                        new IconView("icon.profile.face", 28),
                        UI.Text(_review.FaceCropFallbackText, "micro", maxLines: 2)),
                    Material.Grounded,
                    12,
                    8);
                fallback.Width = 80;
                fallback.Height = 80;
                crop = fallback;
            }
            var candidates = new VariableWrap(8);
            foreach (var candidate in _review.Candidates.Take(5))
            {
                if (candidate.ProfileId is { } profileId)
                {
                    candidates.Children.Add(UI.Chip(
                        $"{candidate.ProfileDisplayName} {candidate.RankText}",
                        onClick: () => _services.RunUserAction(
                            _owner.AssignOtherFaceAsync(_review, profileId),
                            "PeopleSurface.AssignOtherFaceAsync",
                            UI.T("Faces.AssignFailed", "The face could not be assigned."))));
                }
            }

            var actions = UI.Wrap(6);
            var suggestedName = _review.SelectedCandidate?.ProfileDisplayName
                ?? _review.SuggestedProfileDisplayName;
            var hasSuggestion = _owner.CanConfirmFace(_review)
                && !string.IsNullOrWhiteSpace(suggestedName);

            if (_owner.IsScoped)
            {
                actions.Children.Add(UI.Button(
                    UI.T("Faces.AssignThis", "Assign to this Profile"),
                    null,
                    ButtonKind.Primary,
                    "icon.status.success",
                    _owner.AssignToThisProfileCommand,
                    parameter: _review));
                actions.Children.Add(UI.Button(
                    UI.T("Faces.ChooseAnother", "Choose another person"),
                    null,
                    ButtonKind.Ghost,
                    "icon.profile.assign",
                    _owner.AssignOtherWithPickerCommand,
                    parameter: _review));
                actions.Children.Add(UI.Button(
                    UI.T("Faces.IgnoreNotPerson", "Ignore / Not a person"),
                    () => _services.RunUserAction(
                        _owner.RejectFaceAsync(_review),
                        "PeopleSurface.RejectFaceAsync",
                        UI.T("Faces.RejectFailed", "The face could not be rejected.")),
                    ButtonKind.Secondary,
                    "icon.status.error"));
            }
            else if (hasSuggestion)
            {
                actions.Children.Add(UI.Button(
                    UI.F("Faces.ConfirmAs", "Confirm as {0}", suggestedName!),
                    () => _services.RunUserAction(
                        _owner.ConfirmFaceAsync(_review),
                        "PeopleSurface.ConfirmFaceAsync",
                        UI.T("Faces.ConfirmFailed", "The face could not be confirmed.")),
                    ButtonKind.Primary,
                    "icon.status.success"));
                actions.Children.Add(UI.Button(
                    UI.T("Faces.ChooseAnother", "Choose another person"),
                    null,
                    ButtonKind.Ghost,
                    "icon.profile.assign",
                    _owner.AssignOtherWithPickerCommand,
                    parameter: _review));
                actions.Children.Add(UI.Button(
                    UI.F("Faces.NotPerson", "Not {0}", suggestedName!),
                    () => _services.RunUserAction(
                        _owner.RejectFaceAsync(_review),
                        "PeopleSurface.RejectFaceAsync",
                        UI.T("Faces.RejectFailed", "The face could not be rejected.")),
                    ButtonKind.Secondary,
                    "icon.status.error"));
            }
            else
            {
                actions.Children.Add(UI.Button(
                    UI.T("Faces.AssignPerson", "Assign person"),
                    null,
                    ButtonKind.Primary,
                    "icon.profile.assign",
                    _owner.AssignOtherWithPickerCommand,
                    parameter: _review));
                actions.Children.Add(UI.Button(
                    UI.T("Faces.IgnoreNotPerson", "Ignore / Not a person"),
                    () => _services.RunUserAction(
                        _owner.RejectFaceAsync(_review),
                        "PeopleSurface.RejectFaceAsync",
                        UI.T("Faces.RejectFailed", "The face could not be rejected.")),
                    ButtonKind.Secondary,
                    "icon.status.error"));
            }

            actions.Children.Add(UI.Button(UI.T("Faces.OpenMedia", "Open media"), null, ButtonKind.Ghost, "icon.media.open", _owner.OpenMediaCommand, _review.MediaId));
            var rowTitle = UI.Grid(
                "auto",
                "*,auto",
                UI.Text(_review.TargetProfileDisplayName, "card-title", maxLines: 2).At(0, 0),
                UI.Badge(_review.StatusBadgeText).At(0, 1));
            rowTitle.ColumnSpacing = 8;
            var body = UI.V(6,
                rowTitle,
                UI.WrappedText(_review.DetectionConfidenceNotice, "caption"),
                _review.HasCandidates ? UI.WrappedText(_review.CandidateSummaryText, "caption") : null,
                candidates,
                actions);
            body.HorizontalAlignment = HorizontalAlignment.Stretch;
            _host.Child = UI.Grid("auto", "auto,*", crop.At(0, 0), body.Margin(16, 0, 0, 0).At(0, 1));
            _host.Background = ThemeRuntime.Current.Brush("surface2");
            _host.Padding = new Thickness(14);
        }

        public void Dispose() => _review.PropertyChanged -= _propertyChanged;
    }
}
