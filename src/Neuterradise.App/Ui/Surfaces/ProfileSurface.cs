using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using ToggleButton = Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Media;
using Neuterradise.App.Import.Intake;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Resources;
using Neuterradise.App.Trash;
using Neuterradise.App.Ui.Figure;

namespace Neuterradise.App.Ui;

public sealed class ProfileSurface : Surface, ITopBarCustomizableSurface
{
    private readonly ProfileDetailViewModel _vm;
    private readonly Grid _root = UI.Grid("*", "*,auto");
    private readonly StackPanel _page = UI.V(0);
    private readonly ScrollViewer _pageScroll;
    private readonly ProfileHeaderView _profileHeader = new();
    private readonly ProfileFigureView _figureView;
    private readonly ContentControl _modulesHost = new();
    private readonly ContentControl _profileActionsHost = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly Grid _inspectorHost = new() { Visibility = Visibility.Collapsed, Width = 360 };
    private readonly MediaGridPanel _media;
    private ProfileLayoutDefinition _layout;
    private SurfaceNode? _surface;
    private readonly BackdropView _backdrop = new() { ReadabilityGuard = true };
    private Disposables _inspectorBag = new();
    private bool _modulesRefreshQueued;
    private bool _modulesRefreshForce;
    private double _lastModulesLayoutWidth = -1;

    public ProfileSurface(AppServices services, ProfileDetailViewModel vm) : base(services)
    {
        _vm = vm;
        _figureView = new ProfileFigureView(services);
        _media = new MediaGridPanel(services, vm.MediaGrid, () => Context, ResolvePresentation);
        ProfileLayoutResolver.TryGetBuiltInPreset(ProfileLayoutResolver.FallbackPresetId, out _layout);

        _root.Background = ThemeRuntime.Current.Brush("canvas");
        PresentationEffects.SetContextScope(_root, () => Context);
        _page.Children.Add(_profileHeader);
        _page.Children.Add(_modulesHost);
        _page.Children.Add(_profileActionsHost);
        var main = new Grid();
        main.Children.Add(_backdrop);
        main.Children.Add(PresentationEffects.View(PresentationSlots.ProfileEffect, () => Context));
        _pageScroll = UI.Scroll(_page);
        _pageScroll.SizeChanged += (_, args) => QueueModulesRefreshForResize(args.NewSize.Width);
        main.Children.Add(_pageScroll);
        _root.Children.Add(main.At(0, 0));
        SidebarDismissal.Observe(_root, point =>
        {
            if (!_vm.IsInspectorOpen || Preview is not null) return false;
            var local = _root.TransformToVisual(_inspectorHost).TransformPoint(point);
            return local.X < 0 || local.Y < 0 || local.X > _inspectorHost.ActualWidth || local.Y > _inspectorHost.ActualHeight;
        }, _vm.CloseInspector);
        _root.Children.Add(_inspectorHost.At(0, 1));
        _root.SizeChanged += (_, args) =>
        {
            _inspectorHost.Width = Math.Min(320, Math.Max(240, _root.ActualWidth * 0.38));
            var floatingInspector = _root.ActualWidth < 840;
            _inspectorHost.At(0, floatingInspector ? 0 : 1, columnSpan: floatingInspector ? 2 : 1);
            _inspectorHost.HorizontalAlignment = HorizontalAlignment.Right;
            QueueModulesRefreshForResize(args.NewSize.Width);
        };

        Bag.Add(Observe.Props(
            _vm,
            ApplyCoreState,
            nameof(ProfileDetailViewModel.Profile),
            nameof(ProfileDetailViewModel.LayoutDefinition)));

        Bag.Add(Observe.Props(
            _vm,
            UpdateProfileHeader,
            nameof(ProfileDetailViewModel.DisplayName),
            nameof(ProfileDetailViewModel.CategoryName),
            nameof(ProfileDetailViewModel.Tags),
            nameof(ProfileDetailViewModel.Rating),
            nameof(ProfileDetailViewModel.IsFavorite),
            nameof(ProfileDetailViewModel.CoverSource),
            nameof(ProfileDetailViewModel.AppearanceOverrides),
            nameof(ProfileDetailViewModel.Status),
            nameof(ProfileDetailViewModel.ErrorMessage),
            nameof(ProfileDetailViewModel.FolderStatusMessage),
            nameof(ProfileDetailViewModel.RenameStatusMessage)));

        Bag.Add(Observe.Props(
            _vm,
            UpdateProfileHeader,
            nameof(ProfileDetailViewModel.BannerVideoPath),
            nameof(ProfileDetailViewModel.AppearanceOverrides)));
        Bag.Add(Observe.Props(
            _vm,
            UpdateProfileHeader,
            nameof(ProfileDetailViewModel.FigureMediaId),
            nameof(ProfileDetailViewModel.FigureThumbnailRelativePath),
            nameof(ProfileDetailViewModel.FigureModelRenderRelativePath),
            nameof(ProfileDetailViewModel.FigureModelRenderState),
            nameof(ProfileDetailViewModel.FigureSourceSha256)));
        Bag.Add(Observe.Props(
            _vm,
            _media.RefreshLayout,
            nameof(ProfileDetailViewModel.AppearanceOverrides)));

        Bag.Add(Observe.Props(_vm, QueueModulesRefresh,
            nameof(ProfileDetailViewModel.Overview),
            nameof(ProfileDetailViewModel.Notes),
            nameof(ProfileDetailViewModel.AppearanceOverrides)));
        Bag.Add(Observe.Collection(_vm.RelatedProfiles, QueueModulesRefresh));
        Bag.Add(Observe.Props(
            _vm.Related,
            QueueModulesRefresh,
            nameof(Neuterradise.App.RelatedProfiles.RelatedProfilesViewModel.StatusMessage)));
        Bag.Add(Observe.Collection(_vm.Faces, QueueModulesRefresh));
        Bag.Add(Observe.Collection(_vm.RecentMedia, QueueModulesRefresh));
        Bag.Add(Observe.Collection(_vm.AssignmentReviewClusters, QueueModulesRefresh));
        Bag.Add(Observe.Props(_vm, QueueModulesRefresh, nameof(ProfileDetailViewModel.UnknownStatusNotice)));
        Bag.Add(Observe.Props(_vm, UpdateInspectorAndLayout,
            nameof(ProfileDetailViewModel.Inspector),
            nameof(ProfileDetailViewModel.IsInspectorOpen)));

        ApplyCoreState();
    }

    public override FrameworkElement View => _root;

    public override Neuterradise.App.Shell.ScreenStateViewModel Model => _vm;

    public string TopBarCustomizeLabel => UI.T("Profile.Customize", "Customize Profile");

    public bool CanTopBarCustomize => _vm.Profile is not null;

    public void OpenTopBarCustomization() => _vm.OpenCustomizeOverlay();

    private ProfilePresentationState State => new(
        _vm.ProfileId,
        _vm.RowVersion,
        _vm.CurrentPresetId,
        _vm.AppearanceOverrides ?? ProfileAppearanceOverrides.Default);

    private PresentationContext Context => PreviewContext(PresentationContext.ForProfile(State));

    private ImageRef? _previewCover;
    private string? _previewBannerVideo;
    private bool _hasPreviewMedia;

    public Task RefreshCommittedAppearanceAsync() => _vm.RefreshAppearanceAsync();

    public void SetPreviewMedia(ImageRef? cover, string? bannerVideoPath)
    {
        var currentCover = _hasPreviewMedia ? _previewCover : _vm.CoverSource;
        var currentBanner = _hasPreviewMedia ? _previewBannerVideo : _vm.BannerVideoPath;
        static bool SameImageSource(ImageRef? left, ImageRef? right) =>
            left is null && right is null
            || left is not null && right is not null
                && StringComparer.OrdinalIgnoreCase.Equals(left.Path, right.Path)
                && StringComparer.Ordinal.Equals(left.ResourceKey, right.ResourceKey);
        var coverChanged = !SameImageSource(currentCover, cover);
        var bannerChanged = currentBanner != bannerVideoPath;

        _previewCover = cover;
        _previewBannerVideo = bannerVideoPath;
        _hasPreviewMedia = true;

        // Opening Customize publishes the committed sources once. Do not tear down/reparent the
        // identity header when that publication is visually identical to what is already live.
        if (!coverChanged && !bannerChanged) return;
        UpdateProfileHeader(forceIdentity: coverChanged);
    }

    protected override void RefreshPreviewPresentation(string? slot = null)
    {
        if (slot is not null)
        {
            switch (slot)
            {
                case PresentationSlots.ProfileEffect:
                case PresentationSlots.CardEffect:
                case PresentationSlots.ProfileCard:
                    return;
                case PresentationSlots.ProfileBackdrop:
                    _backdrop.Plan = ResolvePresentation(slot, Context).PlanAs<BackdropPlan>();
                    return;
                case PresentationSlots.ProfileMediaLayout:
                    _media.RefreshLayout();
                    return;
                case PresentationSlots.ProfileCover:
                case PresentationSlots.ProfileBanner:
                    _profileHeader.UpdateFraming(Context.Profile?.Overrides ?? State.Overrides);
                    return;
                case PresentationSlots.ProfileFrame:
                    UpdateProfileHeader();
                    return;
                case PresentationSlots.ProfileLayout:
                    UpdateProfileHeader();
                    QueueModulesRefresh();
                    return;
                default:
                    return;
            }
        }
        var previousLayout = _layout;
        if (Preview is null) _hasPreviewMedia = false;
        if (Preview is not null) _inspectorHost.Visibility = Visibility.Collapsed;
        else UpdateInspector();

        // Merely opening an unchanged PreviewSession must not rebuild/reparent the live Profile
        // header. A real layout change still forces identity geometry through UpdateProfileHeader.
        UpdateProfileHeader(forceIdentity: Preview is null);
        _media.RefreshLayout();
        if (Preview is null || previousLayout != _layout)
        {
            QueueModulesRefresh();
        }
    }

    protected override void OnActivated(AppRoute route)
    {
        if (route is ProfileRoute profileRoute && profileRoute.ProfileId == _vm.ProfileId)
        {
            if (profileRoute.Origin is not null)
            {
                _vm.MediaGrid.RestoreState(profileRoute.Origin);
            }

            if (profileRoute.InspectMediaId is { } inspectMediaId && inspectMediaId != Guid.Empty)
            {
                _vm.OpenInspector(inspectMediaId);
            }
            else
            {
                _vm.CloseInspector();
            }
        }

        _profileHeader.SetActive(true);
        _backdrop.IsSurfaceActive = true;
    }

    public void SetFigureOccluded(bool occluded) => _figureView.SetOccluded(occluded);

    protected override void OnSuspended()
    {
        _vm.CloseInspector();
        _profileHeader.SetActive(false);
        _backdrop.IsSurfaceActive = false;
        HoverVideoCoordinator.Shared.StopAll();
    }

    public override void OnPresentationChanged(PresentationChangedEventArgs args)
    {
        if (!args.PacksChanged && args.Slots.Count == 1 && args.Slots.First() is { } slot
            && (slot.StartsWith("profile.", StringComparison.Ordinal) || slot.StartsWith("effect.", StringComparison.Ordinal)))
        {
            RefreshPreviewPresentation(slot);
            return;
        }

        // Presentation/theme changes can affect frame tokens and module presentation, but Profile
        // layout authority stays in the resolved ProfileLayoutDefinition owned by the Profile state.
        UpdateProfileHeader();
        QueueModulesRefresh();
    }

    private void ApplyCoreState()
    {
        if (_vm.Profile is null)
        {
            var message = _vm.HasError
                ? _vm.ErrorMessage ?? UI.T("Profile.LoadFailed", "This Profile could not be opened.")
                : UI.T("Profile.Loading", "Opening Profile…");
            _figureView.SetSource(null);
            _profileHeader.Clear();
            _profileHeader.Visibility = Visibility.Collapsed;
            _modulesHost.Content = UI.Text(message, "body-muted", _vm.HasError ? "danger" : null, 4).Margin(20);
            _profileActionsHost.Content = null;
            Services.Root?.RefreshContextualActions();
            return;
        }

        var plan = ResolvePresentation(PresentationSlots.ProfileLayout, Context).PlanAs<ProfileLayoutPlan>();
        _layout = plan.Layout;
        _surface = plan.Surface;
        _backdrop.Plan = ResolvePresentation(PresentationSlots.ProfileBackdrop, Context).PlanAs<BackdropPlan>();
        _backdrop.AmbientImagePath = Preview is not null && _hasPreviewMedia ? _previewCover?.Path : _vm.CoverSource?.Path;
        _backdrop.AmbientVideoPath = Preview is not null && _hasPreviewMedia ? _previewBannerVideo : _vm.BannerVideoPath;
        _profileHeader.Visibility = Visibility.Visible;
        UpdateProfileHeader();
        RefreshModules();
        Services.Root?.RefreshContextualActions();
    }

    private void UpdateProfileHeader() => UpdateProfileHeader(forceIdentity: true);

    private void UpdateProfileHeader(bool forceIdentity)
    {
        if (_vm.Profile is null)
        {
            return;
        }

        var previousLayout = _layout;
        var plan = ResolvePresentation(PresentationSlots.ProfileLayout, Context).PlanAs<ProfileLayoutPlan>();
        _layout = plan.Layout;
        _surface = plan.Surface;
        _backdrop.Plan = ResolvePresentation(PresentationSlots.ProfileBackdrop, Context).PlanAs<BackdropPlan>();
        _backdrop.AmbientImagePath = Preview is not null && _hasPreviewMedia ? _previewCover?.Path : _vm.CoverSource?.Path;
        _backdrop.AmbientVideoPath = Preview is not null && _hasPreviewMedia ? _previewBannerVideo : _vm.BannerVideoPath;
        var frame = ResolvePresentation(
            PresentationSlots.ProfileFrame,
            Context).PlanAs<FramePlan>();
        var identityAlignment = _layout.IdentityAlignment;
        var figureSource = ResolveFigureSource();
        _figureView.SetSource(figureSource);
        _profileHeader.Apply(new ProfileHeaderViewState(
            _layout,
            Preview is not null && _hasPreviewMedia ? _previewCover : _vm.CoverSource,
            Preview is not null && _hasPreviewMedia ? _previewBannerVideo : _vm.BannerVideoPath,
            Context.Profile?.Overrides ?? _vm.AppearanceOverrides ?? ProfileAppearanceOverrides.Default,
            frame,
            () => Identity(identityAlignment),
            ReducedMotionAuthority.IsReduced, Context,
            FigureView: figureSource is null ? null : _figureView),
            rebuildIdentity: forceIdentity || previousLayout != _layout);
        _profileHeader.SetActive(IsActive);
    }

    private FigureSource? ResolveFigureSource()
    {
        if (_vm.FigureMediaId is not { } mediaId)
        {
            return null;
        }

        string? ResolveMediaAssetPath(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;
            try
            {
                return Services.Paths.ResolveVaultRelativePath(
                    Neuterradise.App.SystemServices.Storage.VaultPathArea.MediaAssets,
                    relativePath);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        var thumbnailPath = ResolveMediaAssetPath(_vm.FigureThumbnailRelativePath);
        var renderPath = _vm.FigureModelRenderState == MediaAssetState.Ready
            ? ResolveMediaAssetPath(_vm.FigureModelRenderRelativePath)
            : null;
        return new FigureSource(
            mediaId,
            renderPath,
            _vm.FigureSourceSha256,
            ImageRef.FromPath(thumbnailPath, 900));
    }

    private FrameworkElement Identity(ProfileLayoutIdentityAlignment alignment)
    {
        var actions = new VariableWrap(
            8,
            UI.Button(UI.T("Common.Back", "Back"), null, ButtonKind.Ghost, "icon.action.back", _vm.GoBackCommand),
            UI.Button(UI.T("Profile.Edit", "Edit details"), () => Services.RunUserAction(EditDetailsAsync(), "ProfileSurface.EditDetailsAsync", UI.T("Profile.EditFailed", "Profile details could not be saved.")), ButtonKind.Secondary, "icon.action.edit"),
            UI.Button(UI.T("Profile.PrivateNote", "Private note"), () => Services.RunUserAction(EditPrivateNoteAsync(), "ProfileSurface.EditPrivateNoteAsync", "Private note could not be saved."), ButtonKind.Secondary, "icon.action.note"),
            UI.Button(UI.T("Profile.AddMedia", "Add media"), () => Services.RunUserAction(AddMediaAsync(), "ProfileSurface.AddMediaAsync", UI.T("Profile.AddMediaFailed", "Media could not be added.")), ButtonKind.Secondary, "icon.media.add"),
            UI.Button(UI.T("Profile.OpenFolder", "Open folder"), null, ButtonKind.Ghost, "icon.action.open-folder", _vm.OpenProfileFolderCommand));
        var statusText = _vm.HasError
            ? _vm.ErrorMessage
            : _vm.FolderStatusMessage ?? _vm.RenameStatusMessage;
        FrameworkElement? status = string.IsNullOrWhiteSpace(statusText)
            ? null
            : UI.WrappedText(statusText, "caption", _vm.HasError ? "danger" : "accent");
        return ProfileIdentityView.Create(
            _vm.DisplayName, _vm.CategoryName, _vm.Tags, _vm.Rating, _vm.IsFavorite, alignment,
            actions: actions,
            status: status);
    }
    private void QueueModulesRefresh() => QueueModulesRefresh(force: true);

    private void QueueModulesRefreshForResize(double width)
    {
        if (width <= 0 || Math.Abs(width - _lastModulesLayoutWidth) < 1)
        {
            return;
        }

        QueueModulesRefresh(force: false);
    }

    private void QueueModulesRefresh(bool force)
    {
        _modulesRefreshForce |= force;
        if (_modulesRefreshQueued)
        {
            return;
        }

        _modulesRefreshQueued = true;
        var dispatcher = _root.DispatcherQueue;
        if (dispatcher is not null && dispatcher.TryEnqueue(() =>
            {
                var forceNow = _modulesRefreshForce;
                _modulesRefreshForce = false;
                _modulesRefreshQueued = false;
                RefreshModules(forceNow);
            }))
        {
            return;
        }

        var fallbackForce = _modulesRefreshForce;
        _modulesRefreshForce = false;
        _modulesRefreshQueued = false;
        RefreshModules(fallbackForce);
    }

    private double ModulesLayoutWidth()
    {
        // ScrollViewer.ViewportWidth includes the vertical scrollbar and can oscillate by the
        // scrollbar width when a rebuild changes content height. ActualWidth is the stable shell
        // allocation and therefore the correct responsive-layout authority.
        return Math.Max(
            180,
            _pageScroll.ActualWidth > 0
                ? _pageScroll.ActualWidth
                : _root.ActualWidth > 0 ? _root.ActualWidth : 1200);
    }

    private void RefreshModules(bool force = true)
    {
        if (_vm.Profile is null)
        {
            return;
        }

        var layoutWidth = ModulesLayoutWidth();
        if (!force && Math.Abs(layoutWidth - _lastModulesLayoutWidth) < 1)
        {
            return;
        }
        _lastModulesLayoutWidth = layoutWidth;

        SurfaceComposer.Detach(_profileHeader);
        SurfaceComposer.Detach(_media.View);
        _page.Children.Clear();
        if (_surface is null) _page.Children.Add(_profileHeader);
        _page.Children.Add(_modulesHost);
        _modulesHost.Content = Modules();
        _profileActionsHost.Content = _surface is null ? ProfileActions() : null;
        _page.Children.Add(_profileActionsHost);

        // Reparenting can transiently unload the native child. Reassert the logical route state
        // after the new surface tree is installed so a retained lease is made visible again.
        _profileHeader.SetActive(IsActive);
    }

    private FrameworkElement? ProfileActions()
    {
        if (_vm.IsUnknownProfile)
        {
            return null;
        }

        var note = UI.Text(
            UI.F(
                "Profile.Delete.Retention",
                "Moves this Profile to Trash for {0} days. Its media stays in the Vault.",
                ProfileTrashRetentionPolicy.ProfileRetentionDays),
            "caption",
            "textMuted",
            2);
        note.TextAlignment = TextAlignment.Center;
        note.HorizontalAlignment = HorizontalAlignment.Center;

        return UI.V(
                8,
                UI.Button(
                        UI.T("Profile.Delete", "Delete Profile"),
                        () => Services.RunUserAction(
                            ConfirmDeleteProfileAsync(),
                            "ProfileSurface.ConfirmDeleteProfileAsync",
                            UI.T("Profile.DeleteFailed", "Profile could not be moved to Trash.")),
                        ButtonKind.Destructive,
                        "icon.action.delete")
                    .Align(HorizontalAlignment.Center),
                note)
            .Margin(14, 8, 14, 20);
    }

    private void UpdateInspectorAndLayout()
    {
        UpdateInspector();
        QueueModulesRefresh();
    }

    private FrameworkElement Modules()
    {
        var viewportWidth = ModulesLayoutWidth();
        var contentViewportWidth = Math.Max(
            180,
            viewportWidth - _pageScroll.Padding.Left - _pageScroll.Padding.Right);
        var padding = UI.PagePadding(contentViewportWidth);
        const double gap = 12;
        var wrap = new VariableWrap(gap)
        {
            LineVerticalAlignment = VerticalAlignment.Top,
        };
        var regions = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal)
        {
            ["identity"] = _profileHeader,
            ["actions"] = ProfileActions() ?? new Grid(),
            ["statistics"] = UI.V(6, UI.Text(UI.T("Profile.Media", "Media"), "section-title"), UI.Text(_vm.MediaGrid.RangeText, "numeric")),
        };
        wrap.Margin = new Thickness(padding, 12, padding, 18);
        var contentWidth = _layout.ContentWidth == ProfileLayoutContentWidth.Wide ? 1440.0 : 1000.0;
        var available = Math.Min(contentWidth, Math.Max(180, contentViewportWidth - (padding * 2)));
        wrap.MaxWidth = contentWidth;
        wrap.HorizontalAlignment = HorizontalAlignment.Center;
        var density = _layout.SectionDensity switch { ProfileLayoutSectionDensity.Compact => 8.0, ProfileLayoutSectionDensity.Spacious => 16.0, _ => 12.0 };
        if (_vm.IsUnknownProfile && _vm.AssignmentReviewClusters.Count > 0)
        {
            var review = UI.Surface(
                AssignmentReview(),
                Material.Raised,
                ThemeRuntime.Current.Tokens.Number("radiusSurface", 16),
                density);
            review.Width = Math.Floor(available);
            wrap.Children.Add(review);
            regions["review"] = review;
        }

        var appearance = Context.Profile?.Overrides ?? _vm.AppearanceOverrides ?? ProfileAppearanceOverrides.Default;
        foreach (var module in _layout.Modules
            .Where(module => module.Visible
                && (module.Id != ProfileLayoutModuleId.RecentMedia || appearance.ShowRecentMedia))
            .OrderBy(module => module.Order))
        {
            FrameworkElement? content;
            try
            {
                content = module.Id switch
                {
                    ProfileLayoutModuleId.Overview => UI.V(10,
                        DetailLayout.Heading(UI.T("Profile.Overview", "Overview"), null, "icon.status.info"),
                        string.IsNullOrWhiteSpace(_vm.Overview)
                            ? UI.Text(UI.T("Profile.Overview.EmptyDetail", "Use Edit details to add a short description for this Profile."), "caption", "textMuted", 3)
                            : UI.Text(_vm.Overview, "body", maxLines: 8)),
                    ProfileLayoutModuleId.Related => Related(),
                    ProfileLayoutModuleId.Faces => _vm.HasFaces ? Faces() : null,
                    ProfileLayoutModuleId.RecentMedia => _vm.RecentMedia.Count > 0 ? RecentMedia() : null,
                    ProfileLayoutModuleId.Media => UI.V(10, DetailLayout.Heading(UI.T("Profile.Media", "Media"), null, "icon.media.image"), _media.View),
                    _ => null,
                };
            }
            catch (Exception exception)
            {
                Trace.TraceError("Profile module '{0}' failed to render: {1}", module.Id, exception);
                content = UI.Section(
                    module.Id == ProfileLayoutModuleId.Media ? UI.T("Profile.Media", "Media") : UI.T("Profile.Section", "Profile section"),
                    null,
                    UI.Guidance(
                        module.Id == ProfileLayoutModuleId.Media ? UI.T("Profile.Media.UnavailableTitle", "Media unavailable") : UI.T("Profile.Section.UnavailableTitle", "Section unavailable"),
                        module.Id == ProfileLayoutModuleId.Media
                            ? UI.T("Profile.Media.UnavailableDetail", "The Media section could not be rendered. Other Profile sections remain available.")
                            : UI.T("Profile.Section.UnavailableDetail", "This section could not be rendered. Other Profile sections remain available."),
                        "warning"));
            }

            if (content is null)
            {
                continue;
            }

            var columns = available < 720 ? 1 : module.Width switch
            {
                ProfileLayoutModuleWidth.Half => 2,
                ProfileLayoutModuleWidth.Third => available >= 960 ? 3 : 1,
                _ => 1,
            };
            var host = UI.Surface(
                content,
                module.Id == ProfileLayoutModuleId.Media ? Material.Grounded : Material.Raised,
                ThemeRuntime.Current.Tokens.Number("radiusSurface", 16),
                density);
            host.Width = Math.Floor((available - (gap * (columns - 1))) / columns);
            host.BorderBrush = ThemeRuntime.Current.Brush("borderSubtle");
            wrap.Children.Add(host);
            regions[module.Id switch
            {
                ProfileLayoutModuleId.RecentMedia => "recent-media",
                _ => module.Id.ToString().ToLowerInvariant(),
            }] = host;
        }
        if (_surface is not null)
        {
            foreach (var region in regions.Values) region.Width = double.NaN;
            // The surface owns the inset content allocation. Descendant desired sizes (including
            // a native Figure and media columns) cannot enlarge the page and trigger another rebuild.
            var composition = SurfaceComposer.Build(_surface, id => regions.GetValueOrDefault(id), available);
            composition.Width = available;
            composition.HorizontalAlignment = HorizontalAlignment.Center;
            return composition.Margin(padding, 12, padding, 18);
        }
        // Optional modules may leave a partial row; use its remaining width without reserving an absent column.
        var row = new List<FrameworkElement>();
        double rowWidth = 0;
        void FillRow()
        {
            if (row.Count > 0)
            {
                var scale = (available - gap * (row.Count - 1)) / row.Sum(item => item.Width);
                foreach (var item in row) item.Width = Math.Floor(item.Width * scale);
            }
            row.Clear();
            rowWidth = 0;
        }
        foreach (var host in wrap.Children.OfType<FrameworkElement>())
        {
            if (row.Count > 0 && rowWidth + gap + host.Width > available) FillRow();
            rowWidth += (row.Count > 0 ? gap : 0) + host.Width;
            row.Add(host);
        }
        FillRow();
        return wrap;
    }

    private FrameworkElement AssignmentReview()
    {
        var rows = UI.V(10);
        foreach (var cluster in _vm.AssignmentReviewClusters)
        {
            var location = string.IsNullOrWhiteSpace(cluster.SourceDirectory)
                ? UI.T("Assignment.Cluster.UnknownSource", "same import context")
                : Path.GetFileName(cluster.SourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var hint = cluster.HasCandidate
                ? UI.F("Assignment.Cluster.Candidate", "{0} items from {1}; suggested Profile: {2}", cluster.MemberCount, location, cluster.CandidateProfileName!)
                : UI.F("Assignment.Cluster.Unresolved", "{0} items from {1} need one assignment decision", cluster.MemberCount, location);
            var actions = UI.Wrap(6);
            if (cluster.CandidateProfileId is { } candidateId && cluster.HasCandidate)
            {
                actions.Children.Add(UI.Button(
                    UI.F("Assignment.Cluster.UseCandidate", "Assign all to {0}", cluster.CandidateProfileName!),
                    () => Services.RunUserAction(
                        _vm.AcceptAssignmentClusterAsync(cluster, candidateId),
                        "ProfileSurface.AcceptAssignmentClusterAsync",
                        UI.T("Assignment.Cluster.Failed", "The grouped assignment could not be applied.")),
                    ButtonKind.Primary));
            }

            actions.Children.Add(UI.Button(
                UI.T("Assignment.Cluster.ChooseOther", "Choose another Profile…"),
                () => Services.RunUserAction(
                    _vm.OpenAssignmentClusterPickerAsync(cluster),
                    "ProfileSurface.OpenAssignmentClusterPickerAsync",
                    UI.T("Assignment.Cluster.PickerFailed", "Profiles could not be loaded.")),
                cluster.HasCandidate ? ButtonKind.Ghost : ButtonKind.Primary));
            actions.Children.Add(UI.Button(
                UI.T("Assignment.Cluster.KeepUnknown", "Keep unassigned"),
                () => Services.RunUserAction(
                    _vm.KeepAssignmentClusterUnknownAsync(cluster),
                    "ProfileSurface.KeepAssignmentClusterUnknownAsync",
                    UI.T("Assignment.Cluster.Failed", "The grouped assignment could not be applied.")),
                ButtonKind.Ghost));
            rows.Children.Add(UI.Surface(UI.V(6, UI.Text(hint, "body"), actions), Material.Grounded));
        }

        return UI.Section(
            UI.T("Assignment.Cluster.Title", "Grouped assignment review"),
            UI.T("Assignment.Cluster.Description", "Each row contains media with the same import evidence. Suggestions are hints, never certainty."),
            rows,
            string.IsNullOrWhiteSpace(_vm.UnknownStatusNotice) ? null : UI.Text(_vm.UnknownStatusNotice, "caption"));
    }

    private FrameworkElement Related()
    {
        var row = new VariableWrap(8);
        foreach (var related in _vm.RelatedProfiles.Take(24))
        {
            var detail = related.SharedMediaCount > 0 ? UI.F("Profile.SharedMedia", "{0} shared", related.SharedMediaCount) : related.ManualRelation ? UI.T("Profile.Linked", "linked") : UI.F("Profile.Faces", "{0} faces", related.ConfirmedFaceCount);
            var open = DetailLayout.Item(related.RelatedDisplayName, detail, "icon.profile.person",
                () => Services.Navigation.Navigate(new ProfileRoute(related.RelatedProfileId)));
            if (related.ManualRelation)
            {
                row.Children.Add(UI.H(4, open,
                    UI.IconButton("icon.action.delete", UI.T("Profile.Related.Remove", "Remove relation"), () => _vm.Related.RemoveManualRelationCommand.Execute(related), 24)));
            }
            else
            {
                row.Children.Add(open);
            }
        }

        if (_vm.RelatedProfiles.Count == 0)
        {
            row.Children.Add(UI.Text(UI.T("Profile.Related.Empty", "No related Profiles yet."), "body-muted"));
        }

        var status = _vm.Related.HasStatusMessage
            ? UI.WrappedText(_vm.Related.StatusMessage, "caption", "danger")
            : null;
        var add = UI.Button(UI.T("Profile.Related.Add", "Add related Profile"), null, ButtonKind.Ghost, "icon.action.add", _vm.Related.OpenAddRelationPickerCommand);
        return UI.V(10,
            DetailLayout.Heading(UI.T("Profile.Related", "Related"), null, "icon.profile.related"),
            row, status, add.Align(HorizontalAlignment.Left));
    }

    private FrameworkElement RecentMedia()
    {
        var row = new VariableWrap(8);
        foreach (var item in _vm.RecentMedia.Take(12))
        {
            var icon = item.MediaType switch
            {
                MediaType.Video => "icon.media.video",
                MediaType.Model => "icon.media.model",
                _ => "icon.media.image",
            };
            var label = string.IsNullOrWhiteSpace(item.DisplayFileName)
                ? MediaSurfaceText.Type(item.MediaType)
                : item.DisplayFileName;
            row.Children.Add(DetailLayout.Item(label, MediaSurfaceText.Type(item.MediaType), icon, () => _vm.OpenMediaDetail(item.MediaId)));
        }

        return UI.V(10,
            DetailLayout.Heading(UI.T("Profile.RecentMedia", "Recent media"),
                UI.F("Profile.RecentMediaCount", "{0} recent items", _vm.RecentMedia.Count), "icon.media.image"),
            row);
    }

    private FrameworkElement Faces()
    {
        var row = new VariableWrap(8);
        foreach (var face in _vm.Faces.Where(f => f.IsUnresolved).Take(12))
        {
            FrameworkElement crop;
            if (face.HasFaceCrop)
            {
                crop = new SkImageView { Source = ImageRef.FromPath(face.FaceCropPath, 160), Width = 72, Height = 72, Circle = true };
            }
            else
            {
                var fallback = UI.Surface(new IconView("icon.profile.face", 24), Material.Grounded, 36, 0);
                fallback.Width = 72;
                fallback.Height = 72;
                fallback.Tip(face.FaceCropFallbackText);
                crop = fallback;
            }

            // Keep the four face decisions explicit; suggestions never imply automatic identity.
            var actions = UI.H(4,
                // ConfirmFaceAsync already targets this Profile's id.
                UI.IconButton("icon.status.success", UI.T("Faces.AssignThis", "Assign to this Profile"),
                    () => Services.RunUserAction(_vm.ConfirmFaceAsync(face), "ProfileSurface.ConfirmFaceAsync",
                        UI.T("Faces.ConfirmFailed", "The face could not be confirmed.")), 26),
                // Reassign through the Profile picker rather than mutating identity locally.
                UI.IconButton("icon.profile.assign", UI.T("Faces.AssignOther", "Assign to another Profile"),
                    () => Services.RunUserAction(_vm.ChangeFaceProfileCommand.ExecuteAsync(face), "ProfileSurface.ChangeFaceProfile",
                        UI.T("Faces.AssignFailed", "The face could not be assigned.")), 26),
                // Rejection is the explicit Ignore / Not a person decision.
                UI.IconButton("icon.status.error", UI.T("Faces.Reject", "Not this person"),
                    () => Services.RunUserAction(_vm.RejectFaceAsync(face), "ProfileSurface.RejectFaceAsync",
                        UI.T("Faces.RejectFailed", "The face could not be rejected.")), 26),
                // Keep source media reachable while reviewing the detection.
                UI.IconButton("icon.media.open", UI.T("Faces.OpenMedia", "Open media"),
                    () => _vm.OpenMediaDetailCommand.Execute(face.MediaId), 26));

            var review = UI.Surface(UI.V(6,
                crop.Align(HorizontalAlignment.Center),
                UI.Text(face.StatusBadgeText, "caption", "textSecondary", 1).Align(HorizontalAlignment.Center),
                actions.Align(HorizontalAlignment.Center)), Material.Raised, 10, 10);
            review.Width = 144;
            review.BorderBrush = ThemeRuntime.Current.Brush("borderSubtle");
            row.Children.Add(review);
        }

        return UI.V(10,
            DetailLayout.Heading(UI.T("Profile.FacesTitle", "People in this media"),
                UI.F("Profile.FacesPending", "{0} waiting for review", _vm.UnresolvedFaceCount), "icon.profile.face"),
            row,
            UI.Button(UI.T("Profile.ReviewAll", "Review all faces"), null, ButtonKind.Ghost, command: _vm.OpenFaceReviewWorkspaceCommand)
                .Align(HorizontalAlignment.Left));
    }

    private async Task AddMediaAsync()
    {
        var files = await Services.PickFilesAsync(true).ConfigureAwait(true);
        if (files.Count > 0)
        {
            await Services.ImportAsync(files, _vm.ProfileId, IntakeOrigin.ProfileAddMedia).ConfigureAwait(true);
        }
    }

    private async Task EditPrivateNoteAsync()
    {
        var note = new TextBox
        {
            Header = UI.T("Profile.Notes", "Private notes"),
            PlaceholderText = UI.T("Profile.PrivateNote.Placeholder", "Only visible inside this Profile."),
            Text = _vm.Notes ?? string.Empty,
            MaxLength = Math.Max(ProfileRules.MaximumPrivateNotesLength, _vm.Notes?.Length ?? 0),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            MinHeight = 180,
        };
        var status = UI.WrappedText(string.Empty, "caption", "danger");
        status.Visibility = Visibility.Collapsed;
        var save = UI.Button(UI.T("Common.Save", "Save"), null, ButtonKind.Primary);
        var counter = UI.Text(string.Empty, "caption", "textSecondary");
        void UpdateNoteCount()
        {
            counter.Text = UI.F("Profile.PrivateNote.Count", "{0:N0} / {1:N0} characters", note.Text.Length, ProfileRules.MaximumPrivateNotesLength);
            save.IsEnabled = ProfileRules.IsPrivateNotesValid(note.Text);
        }
        note.TextChanged += (_, _) => UpdateNoteCount();
        UpdateNoteCount();
        FrameworkElement? dialog = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CloseNote()
        {
            completion.TrySetResult();
        }

        var close = UI.IconButton(
            "icon.action.close",
            UI.T("Common.Close", "Close"),
            () =>
            {
                if (dialog is not null)
                {
                    Services.Root!.Dialogs.Close(dialog, CloseNote);
                }
            });
        var header = UI.Grid(
            "auto",
            "*,auto",
            UI.V(
                2,
                UI.Text(UI.T("Profile.PrivateNote", "Private note"), "section-title"),
                UI.Text(UI.T("Profile.PrivateNote.Description", "A private note for this Profile. It does not appear in presentation layouts."), "caption", "textSecondary", 2)).At(0, 0),
            close.At(0, 1));

        dialog = Services.Root!.Dialogs.Show(
            UI.V(
                14,
                header,
                UI.Guidance(
                    UI.T("Profile.PrivateNote.GuidanceTitle", "Private information"),
                    UI.T("Profile.PrivateNote.GuidanceDetail", "Use this for context you want to keep with the Profile without showing it in the Profile presentation.")),
                note,
                counter,
                status,
                save.Align(HorizontalAlignment.Right)),
            560,
            onDismiss: CloseNote,
            dismissOnScrim: true);

        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            status.Visibility = Visibility.Collapsed;
            note.IsEnabled = false;
            var saved = await _vm.SavePrivateNotesAsync(note.Text).ConfigureAwait(true);
            if (saved)
            {
                Services.Root!.Dialogs.Close(dialog, CloseNote);
                return;
            }

            status.Text = _vm.FolderStatusMessage ?? UI.T("Profile.PrivateNote.SaveFailed", "Private note could not be saved.");
            status.Visibility = Visibility.Visible;
            note.IsEnabled = true;
            UpdateNoteCount();
        };

        await completion.Task.ConfigureAwait(true);
    }

    private async Task EditDetailsAsync()
    {
        await _vm.EnsureMetadataVocabularyLoadedAsync().ConfigureAwait(true);
        _vm.BeginEditMetadata();

        var name = new TextBox
        {
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            PlaceholderText = UI.T("Profile.Name", "Name"),
            Text = _vm.EditDisplayName,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        name.TextChanged += (_, _) => _vm.EditDisplayName = name.Text;

        var category = new ComboBox
        {
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            ItemsSource = _vm.AvailableCategories.Select(c => c.DisplayName).ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 220,
        };
        category.SelectedIndex = _vm.AvailableCategories.ToList()
            .FindIndex(c => c.CategoryId == _vm.EditCategoryId);
        category.SelectionChanged += (_, _) =>
            _vm.EditCategoryId = category.SelectedIndex < 0
                ? null
                : _vm.AvailableCategories[category.SelectedIndex].CategoryId;

        var selectedTags = new VariableWrap(6);
        void FillSelectedTags()
        {
            selectedTags.Children.Clear();
            foreach (var tag in _vm.EditTags)
            {
                selectedTags.Children.Add(UI.Chip(tag.DisplayName, true, () =>
                {
                    _vm.RemoveEditTag(tag);
                    FillSelectedTags();
                }));
            }

            if (selectedTags.Children.Count == 0)
            {
                selectedTags.Children.Add(UI.Text(
                    UI.T("Profile.Tags.Empty", "No tags selected."),
                    "caption",
                    "textMuted"));
            }
        }
        FillSelectedTags();

        var tagSuggestions = UI.Wrap(6);
        var tagInput = new TextBox
        {
            Header = UI.T("Profile.TagLabel", "Tags"),
            PlaceholderText = UI.T("Profile.TagPlaceholder", "Type a tag. Press Enter or comma to add."),
            Text = _vm.TagSearchText,
        };
        var synchronizingTagInput = false;
        tagInput.TextChanged += (_, _) =>
        {
            if (synchronizingTagInput)
            {
                return;
            }

            _vm.TagSearchText = tagInput.Text;
            FillTagSuggestions();
        };
        tagInput.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
            {
                return;
            }

            e.Handled = true;
            TaskObserver.Observe(
                _vm.OnTagInputEnterAsync().ContinueWith(_ =>
                {
                    UiDispatch.Run(() =>
                    {
                        synchronizingTagInput = true;
                        tagInput.Text = _vm.TagSearchText;
                        synchronizingTagInput = false;
                        FillSelectedTags();
                        FillTagSuggestions();
                    });
                }),
                "ProfileSurface.OnTagInputEnterAsync");
        };

       void FillTagSuggestions()
        {
            tagSuggestions.Children.Clear();
            foreach (var suggestion in _vm.TagSearchResults.Take(8))
            {
                tagSuggestions.Children.Add(UI.Chip(suggestion.DisplayName, onClick: () =>
                {
                    _vm.AddEditTag(suggestion);
                    synchronizingTagInput = true;
                    tagInput.Text = _vm.TagSearchText;
                    synchronizingTagInput = false;
                    FillSelectedTags();
                    FillTagSuggestions();
                }));
            }
        }
        FillTagSuggestions();

        var ratingRow = UI.H(4);
        var ratingIcons = new List<IconView>();
        for (var i = 1; i <= 5; i++)
        {
            var value = i;
            ratingRow.Children.Add(UI.IconButton("icon.profile.rating", $"{value}", () =>
            {
                _vm.EditRating = _vm.EditRating == value ? null : value;
                UpdateRatingIcons();
            }, 30));
            if (ratingRow.Children[^1] is Button ratingButton
                && ratingButton.Content is IconView ratingIcon)
            {
                ratingIcons.Add(ratingIcon);
            }
        }

        void UpdateRatingIcons()
        {
            for (var index = 0; index < ratingIcons.Count; index++)
            {
                ratingIcons[index].ColorToken =
                    _vm.EditRating is { } rating && rating >= index + 1
                        ? "warning"
                        : "textMuted";
            }
        }
        UpdateRatingIcons();

        var favoriteToggle = new ToggleSwitch
        {
            Header = UI.T("Profile.Favorite", "Favorite"),
            IsOn = _vm.EditIsFavorite,
        };
        favoriteToggle.Toggled += (_, _) => _vm.EditIsFavorite = favoriteToggle.IsOn;

        var overview = new TextBox
        {
            Header = UI.T("Profile.Overview", "Overview"),
            PlaceholderText = UI.T(
                "Profile.Overview.Placeholder",
                "Write a short description that should appear on the Profile."),
            Text = _vm.EditOverview ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            MinHeight = 80,
        };
        overview.TextChanged += (_, _) => _vm.EditOverview = overview.Text;

        var identityGrid = UI.FormColumns(
            UI.V(5, UI.Text(UI.T("Profile.Name", "Name"), "control"), name),
            UI.V(5, UI.Text(UI.T("Profile.Category", "Category"), "control"), category));

        var preferenceGrid = UI.FormColumns(
            UI.V(
                5,
                UI.Text(UI.T("Profile.Rating", "Rating"), "control"),
                UI.Text(UI.T("Profile.Rating.Help", "Used for your own Profile ranking."), "caption", "textMuted", 2),
                ratingRow).At(0, 0),
            UI.V(
                5,
                UI.Text(UI.T("Profile.Favorite", "Favorite"), "control"),
                UI.Text(UI.T("Profile.Favorite.Help", "Marks this Profile as a favorite in collections."), "caption", "textMuted", 2),
                favoriteToggle).At(0, 1));

        var identitySection = DetailLayout.Section(
            UI.T("Profile.IdentitySection.Title", "Identity & classification"),
            UI.T("Profile.IdentitySection.Description", "The information that identifies and groups this Profile."),
            "icon.profile.person",
            identityGrid, tagInput, tagSuggestions,
            UI.V(5, UI.Text(UI.T("Profile.Tags.Selected", "Selected tags"), "caption", "textSecondary"), selectedTags));
        var detailsSection = DetailLayout.Section(
            UI.T("Profile.DetailsSection.Title", "Profile details"),
            UI.T("Profile.DetailsSection.Description", "Rating, favorite state, and the Overview shown on the Profile page."),
            "icon.action.edit",
            preferenceGrid, overview);

        FrameworkElement? dialog = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void DismissEdit()
        {
            _vm.CancelEditMetadata();
            completion.TrySetResult();
        }

        var status = UI.WrappedText(string.Empty, "caption", "danger");
        status.Visibility = Visibility.Collapsed;
        var save = UI.Button(UI.T("Common.Save", "Save"), null, ButtonKind.Primary);
        var close = UI.IconButton(
            "icon.action.close",
            UI.T("Common.Close", "Close"),
            () =>
            {
                if (dialog is not null)
                {
                    Services.Root!.Dialogs.Close(dialog, DismissEdit);
                }
            });
        var header = UI.Grid(
            "auto",
            "*,auto",
            UI.V(
                2,
                UI.Text(UI.T("Profile.Edit", "Edit details"), "section-title"),
                UI.Text(UI.T("Profile.Edit.Description", "Edit Profile identity and descriptive metadata."), "caption", "textSecondary", 2)).At(0, 0),
            close.At(0, 1));

        dialog = Services.Root!.Dialogs.Show(
            UI.V(
                10,
                header,
                identitySection,
                detailsSection,
                status,
                save.Align(HorizontalAlignment.Right)),
            720,
            onDismiss: DismissEdit,
            dismissOnScrim: true);

        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            status.Visibility = Visibility.Collapsed;
            await _vm.SaveMetadataAsync().ConfigureAwait(true);
            if (!_vm.IsEditingMetadata)
            {
                Services.Root!.Dialogs.Close(dialog, () => completion.TrySetResult());
                return;
            }

            status.Text = _vm.FolderStatusMessage
                ?? UI.T("Profile.Metadata.Failed", "Profile metadata could not be updated.");
            status.Visibility = Visibility.Visible;
            save.IsEnabled = true;
        };

        await completion.Task.ConfigureAwait(true);
    }
    private async Task ConfirmDeleteProfileAsync()
    {
        if (_vm.IsUnknownProfile || Services.Root is null)
        {
            return;
        }

        FrameworkElement? dialog = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var status = UI.WrappedText(string.Empty, "caption", "danger");
        status.Visibility = Visibility.Collapsed;

        void Dismiss() => completion.TrySetResult();

        var cancel = UI.Button(UI.T("Overlay.Cancel", "Cancel"), null, ButtonKind.Ghost);
        var confirm = UI.Button(
            UI.T("Profile.Delete.Confirm", "Move to Trash"),
            null,
            ButtonKind.Destructive,
            "icon.action.delete");

        cancel.Click += (_, _) =>
        {
            if (dialog is not null)
            {
                Services.Root.Dialogs.Close(dialog, Dismiss);
            }
        };

        confirm.Click += async (_, _) =>
        {
            confirm.IsEnabled = false;
            cancel.IsEnabled = false;
            status.Visibility = Visibility.Collapsed;

            var moved = await _vm.MoveToTrashAsync().ConfigureAwait(true);
            if (!moved)
            {
                status.Text = _vm.FolderStatusMessage
                    ?? UI.T("Profile.DeleteFailed", "Profile could not be moved to Trash.");
                status.Visibility = Visibility.Visible;
                confirm.IsEnabled = true;
                cancel.IsEnabled = true;
                return;
            }

            if (dialog is not null)
            {
                Services.Root.Dialogs.Close(dialog, Dismiss);
            }

            Services.Toast(
                UI.F(
                    "Profile.Delete.Moved",
                    "Profile moved to Trash. It can be restored for {0} days.",
                    ProfileTrashRetentionPolicy.ProfileRetentionDays),
                "success");
            Services.Navigation.ResetToTopLevel(new GalleryRoute());
        };

        dialog = Services.Root.Dialogs.Show(
            UI.V(
                14,
                UI.Text(UI.T("Profile.Delete.Title", "Move Profile to Trash?"), "section-title"),
                UI.Text(
                    UI.F(
                        "Profile.Delete.Message",
                        "This removes {0} from the active Vault. The Profile can be restored for {1} days, then it is permanently deleted automatically. Its media stays in the Vault. Settings > Trash can permanently delete it sooner.",
                        _vm.DisplayName,
                        ProfileTrashRetentionPolicy.ProfileRetentionDays),
                    "body",
                    maxLines: 8),
                status,
                UI.Wrap(6, cancel, confirm).Align(HorizontalAlignment.Right)),
            560,
            onDismiss: Dismiss,
            dismissOnScrim: true);

        await completion.Task.ConfigureAwait(true);
    }

    public void CloseInspectorForCustomization() => _vm.CloseInspector();

    private void UpdateInspector()
    {
        _inspectorBag.Dispose();
        _inspectorBag = new Disposables();
        _inspectorHost.Children.Clear();
        if (Preview is not null || !_vm.IsInspectorOpen || _vm.Inspector is not { } inspector)
        {
            _inspectorHost.Visibility = Visibility.Collapsed;
            return;
        }

        _inspectorHost.Visibility = Visibility.Visible;
        var panel = new InspectorPanel(Services, inspector, () => _vm.CloseInspector(), Context);
        _inspectorBag.Add(panel);
        _inspectorHost.Children.Add(panel.View);
    }

    public override void Dispose()
    {
        OnSuspended();
        _inspectorBag.Dispose();
        _media.Dispose();
        _figureView.Dispose();
        _profileHeader.Dispose();
        base.Dispose();
    }
}
