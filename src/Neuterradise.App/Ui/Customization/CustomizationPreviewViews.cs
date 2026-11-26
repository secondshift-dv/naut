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
using Neuterradise.App.Ui.Figure;

namespace Neuterradise.App.Ui;

public sealed partial class CustomizationCenter
{
    private CoverFrameView? _focusedCover;

    // ---------------------------------------------------------------- live preview

    private void UpdatePreviewHeight(double rootHeight)
    {
        if (_figureEditorOpen)
        {
            _preview.MinHeight = 0;
            _preview.Height = _session.ProfileWorking?.Sources?.FigureMediaId is null ? 160 : 336;
            return;
        }
        var focusedArtwork = _slot is PresentationSlots.ProfileCover or PresentationSlots.ProfileBanner or PresentationSlots.ProfileFrame;
        _preview.MinHeight = focusedArtwork ? 220 : 180;
        _preview.Height = focusedArtwork
            ? Math.Clamp(rootHeight * 0.44, 220, 440)
            : Math.Clamp(rootHeight * 0.36, 180, 280);
    }

    private void RenderPreview()
    {
        if ((!_dedicatedEditor || _slot is not (PresentationSlots.ProfileCover or PresentationSlots.ProfileFrame))
            && _focusedCover is not null)
            _focusedCover.Source = null;
        if (!_dedicatedEditor)
        {
            _profilePreviewHeader.SetActive(false);
            _preview.Children.Clear();
            _preview.Visibility = Visibility.Collapsed;
            return;
        }
        UpdatePreviewHeight(_root.ActualHeight > 0 ? _root.ActualHeight : 720);
        _preview.Children.Clear();
        _preview.Visibility = Visibility.Visible;
        var theme = ThemeRuntime.Current;
        var frame = new Border { CornerRadius = new CornerRadius(14), BorderBrush = theme.Brush("borderSubtle"), BorderThickness = new Thickness(1), Background = theme.Brush("canvas") };
        var stage = new Grid();
        frame.Child = stage;
        _preview.Children.Add(frame);
        var profileHeaderActive = false;
        if (_figureEditorOpen)
            profileHeaderActive = ProfileHeaderPreview(stage);
        else if (_slot is PresentationSlots.ProfileCard or PresentationSlots.CardEffect)
        {
            if (_subject is null)
                stage.Children.Add(UI.Text(UI.T("Customize.Loading.ProfileCard", "Loading this Profile's card…"), "body-muted").Align(HorizontalAlignment.Center, VerticalAlignment.Center));
            else AddCard(stage, 340, HorizontalAlignment.Center);
        }
        else if (_slot == PresentationSlots.ProfileMediaLayout)
        {
            if (_subject?.PreviewMediaCard is { } mediaCard)
            {
                var layout = _session.ResolveCompiled(PresentationSlots.ProfileMediaLayout).PlanAs<ProfileMediaLayoutPlan>().Layout;
                var visual = new MediaCardView(layout);
                visual.Bind(mediaCard);
                visual.Width = Math.Min(layout.CardWidth, 420);
                visual.Height = visual.Width * layout.CardHeight / Math.Max(1, layout.CardWidth);
                var host = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                host.Children.Add(visual);
                stage.Children.Add(host);
            }
            else profileHeaderActive = ProfileHeaderPreview(stage);
        }
        else if (_slot is PresentationSlots.ProfileCover or PresentationSlots.ProfileFrame)
            FocusedCoverPreview(stage);
        else if (_slot == PresentationSlots.ProfileBanner)
            profileHeaderActive = FocusedBannerPreview(stage);

        if (!profileHeaderActive) _profilePreviewHeader.SetActive(false);
    }

    private void FocusedCoverPreview(Grid stage)
    {
        var plan = _session.ResolveCompiled(PresentationSlots.ProfileFrame).PlanAs<FramePlan>();
        var appearance = _session.ProfileWorking?.Overrides ?? ProfileAppearanceOverrides.Default;
        var cover = _focusedCover ??= new CoverFrameView();
        cover.Source = _coverSource;
        cover.Transform = MediaTransformState.FromCover(appearance);
        cover.Plan = plan;
        cover.Appearance = CardDataFactory.AppearanceFor(appearance, plan, ReducedMotionAuthority.IsReduced) with
        {
            DetailLevel = CoverFrameDetailLevel.Full,
        };
        cover.FrameMode = "full";
        cover.HorizontalAlignment = HorizontalAlignment.Center;
        cover.VerticalAlignment = VerticalAlignment.Center;
        SurfaceComposer.Detach(cover);
        AttachFraming(cover, false);
        void Fit()
        {
            var size = Math.Max(1, Math.Min(stage.ActualWidth, stage.ActualHeight) - 16);
            cover.Width = cover.Height = size;
        }
        cover.Width = cover.Height = Math.Max(1, _preview.Height - 16);
        stage.SizeChanged += (_, _) => Fit();
        stage.Children.Add(cover);
    }

    private bool FocusedBannerPreview(Grid stage)
    {
        if (_session.ProfileWorking is not { } profile) return false;
        SyncBannerPreviewFromWorkingSource();
        var plan = _session.ResolveCompiled(PresentationSlots.ProfileLayout).PlanAs<ProfileLayoutPlan>();
        var layout = plan.Layout with { BannerMode = ProfileLayoutBannerMode.Contained };
        var frame = _session.ResolveCompiled(PresentationSlots.ProfileFrame).PlanAs<FramePlan>();
        SurfaceComposer.Detach(_profilePreviewHeader);
        var additional = ActiveAdditionalBanner();
        if (additional is null) _additionalBannerSlotId = null;
        var candidate = additional is null ? null : ActiveBannerCandidate();
        var slotAppearance = additional is null ? null : ActiveBannerAppearance();
        var focusedArtwork = plan.BannerArtwork;
        if (focusedArtwork?.Layers.Any(layer => layer.Kind == "banner") == true)
            focusedArtwork = focusedArtwork with { Layers = focusedArtwork.Layers.Where(layer => layer.Kind != "banner").ToArray() };
        _profilePreviewHeader.Apply(new ProfileHeaderViewState(layout, null,
            additional is null ? _bannerVideoPath : candidate?.PlaybackPath, profile.Overrides,
            frame, () => new Grid(), ReducedMotionAuthority.IsReduced, _session.LiveContext,
            BannerPoster: additional is null ? _bannerSource : ImageRef.FromPath(candidate?.PreviewPath, 1600),
            BannerArtwork: additional is null ? focusedArtwork : null,
            BannerPrimaryMediaId: profile.Sources?.BannerMediaId,
            FocusedBanner: true, FocusedTransform: slotAppearance?.Transform,
            FocusedEffect: slotAppearance is null ? null : _services.Presentation.ResolveBannerSlotEffect(slotAppearance)));
        _profilePreviewHeader.HorizontalAlignment = HorizontalAlignment.Stretch;
        _profilePreviewHeader.VerticalAlignment = VerticalAlignment.Stretch;
        stage.Children.Add(_profilePreviewHeader);
        AttachFraming(_profilePreviewHeader.BannerFramingSurface, true);
        _profilePreviewHeader.SetActive(true);
        return true;
    }

    private CardData? SampleData()
    {
        if (_category == CustomizationCategories.Home && _services.Root?.SampleHomeCard() is { } homeCard)
        {
            return homeCard;
        }
        if (_category == CustomizationCategories.Gallery && _services.Root?.SampleCard(null) is { } galleryCard)
        {
            return CardDataFactory.From(galleryCard, _services.Presentation, 360, 720);
        }

        var frame = _session.ResolveCompiled(PresentationSlots.ProfileFrame).PlanAs<FramePlan>();

        if (_subject is { } subject && _session.ProfileWorking is { } working && working.ProfileId == subject.Detail.ProfileId
            && _session.ResolveMedia() is { } subjectMedia)
        {
            return new CardData(
                subject.Detail.ProfileId,
                subject.Detail.DisplayName,
                subject.Detail.CategoryName,
                subject.Detail.Tags.ToList(),
                subject.Detail.Rating,
                subject.Detail.IsFavorite,
                subject.MediaItems.Count,
                0,
                subject.Detail.Overview,
                _coverSource,
                _bannerSource,
                CardDataFactory.AppearanceFor(working.Overrides, frame, ReducedMotionAuthority.IsReduced),
                subjectMedia.Cover,
                subjectMedia.Banner,
                frame) { AssignedTags = subject.Detail.AssignedTags, PresentationAppearance = working.Overrides, BannerHoverPath = _bannerVideoPath };
        }

        var card = _services.Root?.SampleCard(_session.Context.ProfileId);
        if (card is null)
        {
            return null;
        }

        var data = CardDataFactory.From(card, _services.Presentation, 480, 900);
        if (_session.ProfileWorking is { } profile && profile.ProfileId == data.ProfileId && _session.ResolveMedia() is { } media)
        {
            data = data with { CoverTransform = media.Cover, BannerTransform = media.Banner };
        }

        return _session.ProfileWorking is { } current && current.ProfileId == data.ProfileId && _session.ResolveMedia() is { } resolved
            ? data with
            {
                Cover = _coverSource,
                Banner = _bannerSource,
                Appearance = CardDataFactory.AppearanceFor(current.Overrides, frame, ReducedMotionAuthority.IsReduced),
                CoverTransform = resolved.Cover,
                BannerTransform = resolved.Banner,
                Frame = frame,
                PresentationAppearance = current.Overrides,
                BannerHoverPath = _bannerVideoPath,
            }
            : data with { Frame = frame };
    }

    private void AddCard(Grid stage, double width, HorizontalAlignment alignment)
    {
        if (SampleData() is not { } data)
        {
            stage.Children.Add(UI.Text(UI.T("Customize.NoSample", "Add a Profile to see a live preview here."), "body-muted").Align(HorizontalAlignment.Center, VerticalAlignment.Center));
            return;
        }

        var visual = new CardVisual(_session.ResolveCompiled(PresentationSlots.ProfileCard),
            () => _session.ResolveCompiled(PresentationSlots.CardEffect).PlanAs<EffectPlan>());
        visual.Bind(data);
        visual.Width = Math.Min(width, 420);
        visual.Height = visual.Width / Math.Max(0.4, visual.Plan.Aspect);
        visual.HorizontalAlignment = alignment;
        visual.VerticalAlignment = VerticalAlignment.Center;
        visual.Margin = new Thickness(24, 12, 24, 12);
        stage.Children.Add(visual);
    }

    private bool ProfileHeaderPreview(Grid stage)
    {
        if (_session.ProfileWorking is not { } profile)
        {
            stage.Children.Add(UI.Text(
                UI.T("Customize.ChooseProfileHint", "Choose a Profile above to edit this for that Profile."),
                "body-muted").Align(HorizontalAlignment.Center, VerticalAlignment.Center));
            return false;
        }

        SyncBannerPreviewFromWorkingSource();
        var profilePlan = _session.ResolveCompiled(PresentationSlots.ProfileLayout).PlanAs<ProfileLayoutPlan>();
        var layout = _figureEditorOpen ? profilePlan.Layout with
        {
            BannerMode = ProfileLayoutBannerMode.None,
            CoverPlacement = ProfileLayoutCoverPlacement.InlineLeft,
            CoverSize = ProfileLayoutCoverSize.Small,
            IdentityAlignment = ProfileLayoutIdentityAlignment.Left,
        } : profilePlan.Layout;
        var framePlan = _session.ResolveCompiled(PresentationSlots.ProfileFrame)
            .PlanAs<FramePlan>();
        FrameworkElement IdentityPreview()
        {
            var model = _subject?.PreviewModel;
            return ProfileIdentityView.Create(
                model?.DisplayName ?? _subject?.Detail.DisplayName ?? _launchRequest?.ProfileDisplayName ?? string.Empty,
                model?.CategoryName ?? _subject?.Detail.CategoryName,
                model?.TagList ?? _subject?.Detail.Tags ?? [],
                model?.Rating ?? _subject?.Detail.Rating,
                model?.IsFavorite ?? _subject?.Detail.IsFavorite ?? false,
                layout.IdentityAlignment, assignedTags: _subject?.Detail.AssignedTags,
                showTags: layout.Id != "portrait" || string.IsNullOrWhiteSpace(_bannerVideoPath),
                visuals: profilePlan.Visuals);
        }

        var figurePreview = UpdateFigurePreviewSource();
        _profilePreviewHeader.Apply(new ProfileHeaderViewState(
            layout,
            _coverSource,
            _bannerVideoPath,
            profile.Overrides,
            framePlan,
            IdentityPreview,
            ReducedMotionAuthority.IsReduced, _session.LiveContext,
            FigureView: figurePreview,
            FigureSize: _figureEditorOpen ? 240 : 300, Tags: _subject?.Detail.AssignedTags, BannerPoster: _bannerSource,
            BannerArtwork: profilePlan.BannerArtwork,
            BannerAuthority: !_figureEditorOpen && profilePlan.BannerArtwork?.Layers.Any(layer => layer.Kind == "banner") == true
                ? _services.Runtime.MediaResources : null,
            BannerPrimaryMediaId: _session.ProfileWorking?.Sources?.BannerMediaId));
        _profilePreviewHeader.SetActive(true);
        AttachFraming(_profilePreviewHeader.CoverFramingSurface, false);
        AttachFraming(_profilePreviewHeader.BannerFramingSurface, true);
        _profilePreviewHeader.HorizontalAlignment = HorizontalAlignment.Stretch;
        _profilePreviewHeader.VerticalAlignment = VerticalAlignment.Top;
        SurfaceComposer.Detach(_profilePreviewHeader);
        stage.Children.Add(new BackdropView
        {
            Plan = _session.ResolveCompiled(PresentationSlots.ProfileBackdrop).PlanAs<BackdropPlan>(),
            AmbientImagePath = _coverSource?.Path, AmbientVideoPath = _bannerVideoPath,
            IsSurfaceActive = true, ReadabilityGuard = true,
        });
        stage.Children.Add(PresentationEffects.View(PresentationSlots.ProfileEffect, () => Context, stage,
            () => _session.ResolveCompiled(PresentationSlots.ProfileEffect).PlanAs<EffectPlan>()));
        stage.Children.Add(UI.Scroll(_profilePreviewHeader));
        return true;
    }

    private string? HiddenSlotPreviewNotice()
    {
        if (_session.ProfileWorking is null || _slot != PresentationSlots.ProfileCover)
        {
            return null;
        }

        var layout = _session.ResolveCompiled(PresentationSlots.ProfileLayout)
            .PlanAs<ProfileLayoutPlan>()
            .Layout;
        var bannerHidden = layout.BannerMode == ProfileLayoutBannerMode.None;
        var frameHidden = layout.CoverDetailLevel == CoverFrameDetailLevel.Hidden
            || layout.CoverPlacement == ProfileLayoutCoverPlacement.None;
        return (bannerHidden, frameHidden) switch
        {
            (true, true) => UI.T("Customize.PreviewNotice.BannerAndFrameHidden", "This layout hides the Banner and Frame. Saving keeps your changes. To show them, choose a layout that includes them."),
            (true, false) => UI.T("Customize.PreviewNotice.BannerHidden", "This layout hides the Banner. Saving keeps your choice. To show it, choose a layout with a Banner."),
            (false, true) => UI.T("Customize.PreviewNotice.FrameHidden", "This layout hides the Frame. Saving keeps your choice. To show it, choose a layout with a Frame."),
            _ => null,
        };
    }
}
