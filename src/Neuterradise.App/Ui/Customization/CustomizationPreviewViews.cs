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
    // ---------------------------------------------------------------- live preview

    private void UpdatePreviewHeight(double rootHeight)
    {
        if (_figureEditorOpen)
        {
            _preview.MinHeight = 0;
            _preview.Height = _session.ProfileWorking?.Sources?.FigureMediaId is null ? 160 : 336;
            return;
        }
        var framingPreview = _category == CustomizationCategories.Profile
            && (_figureEditorOpen || _slot is PresentationSlots.ProfileCover or PresentationSlots.ProfileBanner);
        _preview.MinHeight = framingPreview ? 360 : 180;
        _preview.Height = framingPreview ? double.NaN : Math.Clamp(rootHeight * 0.36, 180, 280);
    }

    private void RenderPreview()
    {
        _profilePreviewHeader.SetActive(false);
        if (!_dedicatedEditor)
        {
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
        switch (_category)
        {
            case CustomizationCategories.Home:
                stage.Children.Add(new HomePreviewView(_session.ResolveCompiled(PresentationSlots.HomeLayout).PlanAs<HomeLayoutPlan>(),
                    _session.ResolveCompiled(PresentationSlots.HomeBackdrop).PlanAs<BackdropPlan>(),
                    _session.ResolveCompiled(PresentationSlots.HomeSpotlight).PlanAs<SpotlightPlan>(), SampleData(), compact: false));
                break;
            case CustomizationCategories.Gallery:
                stage.Children.Add(GalleryLayoutOptionPreview(_session.ResolveCompiled(PresentationSlots.GalleryLayout).PlanAs<GalleryLayoutPlan>()));
                break;
            case CustomizationCategories.Profile:
                if (!_figureEditorOpen && _slot is (PresentationSlots.ProfileCard or PresentationSlots.CardEffect))
                {
                    if (_subject is null)
                        stage.Children.Add(UI.Text(UI.T("Customize.Loading.ProfileCard", "Loading this Profile's card…"), "body-muted").Align(HorizontalAlignment.Center, VerticalAlignment.Center));
                    else AddCard(stage, 340, HorizontalAlignment.Center);
                }
                else if (!_figureEditorOpen && _slot is (PresentationSlots.ProfileMediaLayout or PresentationSlots.MediaEffect)
                    && _subject?.PreviewMediaCard is { } mediaCard)
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
                else if (!_figureEditorOpen && _slot == PresentationSlots.ProfileFrame)
                    stage.Children.Add(DefinitionOptionPreview(PresentationSlots.Get(_slot), _session.ResolveCompiled(_slot))!);
                else ProfilePreview(stage);
                break;

        }
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
                frame);
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

    private void ProfilePreview(Grid stage)
    {
        if (_session.ProfileWorking is not { } profile)
        {
            stage.Children.Add(UI.Text(
                UI.T("Customize.ChooseProfileHint", "Choose a Profile above to edit this for that Profile."),
                "body-muted").Align(HorizontalAlignment.Center, VerticalAlignment.Center));
            return;
        }

        SyncBannerPreviewFromWorkingSource();
        var profilePlan = _session.ResolveCompiled(PresentationSlots.ProfileLayout).PlanAs<ProfileLayoutPlan>();
        var resolvedLayout = profilePlan.Layout;
        var layout = PreviewLayoutForActiveSlot(resolvedLayout);
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
                layout.IdentityAlignment);
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
            FigureSize: _figureEditorOpen ? 240 : 300));
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
        if (!_figureEditorOpen && _slot == PresentationSlots.ProfileLayout && profilePlan.Surface is { } surface)
        {
            FrameworkElement? Region(string id)
            {
                if (id == "identity") return _profilePreviewHeader;
                if (id == "recent-media" && !profile.Overrides.ShowRecentMedia) return null;
                return UI.Surface(UI.Text(id.Replace('-', ' '), "caption"), Material.Raised, 8, 12);
            }
            var composition = SurfaceComposer.Build(surface, Region, 1100);
            composition.Width = 1100;
            stage.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = composition });
        }
        else if (_figureEditorOpen)
            stage.Children.Add(UI.Scroll(_profilePreviewHeader));
        else if (_slot is PresentationSlots.ProfileCover or PresentationSlots.ProfileBanner)
            stage.Children.Add(_profilePreviewHeader);
        else stage.Children.Add(UI.Scroll(_profilePreviewHeader));
    }

    private ProfileLayoutDefinition PreviewLayoutForActiveSlot(ProfileLayoutDefinition layout)
    {
        if (_figureEditorOpen)
        {
            return layout with
            {
                BannerMode = ProfileLayoutBannerMode.None,
                CoverPlacement = ProfileLayoutCoverPlacement.InlineLeft,
                CoverSize = ProfileLayoutCoverSize.Small,
                IdentityAlignment = ProfileLayoutIdentityAlignment.Left,
            };
        }
        if (_slot is not (PresentationSlots.ProfileCover or PresentationSlots.ProfileBanner))
        {
            return layout;
        }

        if (layout.BannerMode == ProfileLayoutBannerMode.None)
        {
            layout = layout with { BannerMode = ProfileLayoutBannerMode.Contained };
        }

        if (_slot == PresentationSlots.ProfileBanner) return layout;

        if (layout.CoverDetailLevel == CoverFrameDetailLevel.Hidden
            || layout.CoverPlacement == ProfileLayoutCoverPlacement.None)
        {
            layout = layout with
            {
                CoverPlacement = layout.CoverPlacement == ProfileLayoutCoverPlacement.None
                    ? ProfileLayoutCoverPlacement.InlineLeft
                    : layout.CoverPlacement,
                CoverSize = layout.CoverPlacement == ProfileLayoutCoverPlacement.None
                    ? ProfileLayoutCoverSize.Medium
                    : layout.CoverSize,
                CoverDetailLevel = CoverFrameDetailLevel.Full,
            };
        }

        return layout;
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
            (true, true) => UI.T("Customize.PreviewNotice.BannerAndFrameHidden", "This Profile Layout hides the Banner and Cover Frame. You can edit them here, but choose a layout that displays them to make them visible."),
            (true, false) => UI.T("Customize.PreviewNotice.BannerHidden", "This Profile Layout hides the Banner. You can edit it here, but choose a layout with a Banner to make it visible."),
            (false, true) => UI.T("Customize.PreviewNotice.FrameHidden", "This Profile Layout hides the Cover Frame. You can edit it here, but choose a layout that displays the frame to make it visible."),
            _ => null,
        };
    }
}
