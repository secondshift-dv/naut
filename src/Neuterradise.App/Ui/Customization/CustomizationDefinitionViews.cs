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
    private FrameworkElement DefinitionChooser(SlotDescriptor descriptor, bool compact = false)
    {
        var selected = _session.Resolve(descriptor.Id).Definition;
        Panel wrap = UI.V(compact || _cardOnly ? 4 : 6);
        var definitions = _services.Presentation.DefinitionsOf(descriptor.Kind!)
            .Where(definition => IsMeaningfulDefinitionChoice(descriptor, definition, selected))
            .Where(definition => descriptor.Id != PresentationSlots.ProfileFrame
                || definition.Origin != PackOrigin.BuiltIn || definition.PlanAs<FramePlan>().Frame.Id != "none");
        if (descriptor.Kind == DefinitionKinds.Backdrop)
            definitions = definitions
                .OrderBy(BackdropChoiceOrder)
                .ThenBy(SurfaceText.PresentationName, StringComparer.CurrentCultureIgnoreCase);
        if (descriptor.Id == PresentationSlots.ProfileFrame)
            definitions = definitions
                .OrderBy(FrameChoicePreview.Order)
                .ThenBy(SurfaceText.PresentationName, StringComparer.CurrentCultureIgnoreCase);
        foreach (var definition in definitions)
        {
            var reference = definition.Ref;
            wrap.Children.Add(CustomizationChoiceView.Create(SurfaceText.PresentationName(definition),
                DefinitionPreviewStage(descriptor, definition), selected == reference,
                descriptor.Default == reference, definition.Origin == PackOrigin.User,
                () =>
                {
                    _session.Select(descriptor.Id, descriptor.Scopes.Single(), reference);
                },
                DefinitionDescription(descriptor, definition),
                squarePreview: descriptor.Id == PresentationSlots.ProfileFrame));
        }

        return wrap;
    }

    private static int BackdropChoiceOrder(CompiledDefinition definition) => definition.Ref.DefinitionId switch
    {
        "builtin.neuterradise.backdrop.none" => 0,
        "builtin.neuterradise.backdrop.calm-yellow" => 1,
        "builtin.neuterradise.backdrop.calm-blue" => 2,
        "builtin.neuterradise.backdrop.gray" => 3,
        _ => definition.Origin == PackOrigin.User ? 100 : 50,
    };

    private FrameworkElement EffectChooser(SlotDescriptor descriptor)
    {
        return DefinitionChooser(descriptor);
    }

    private static string DefinitionDescription(SlotDescriptor descriptor, CompiledDefinition definition)
    {
        var description = SurfaceText.PresentationDescription(definition);
        if (!string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        if (descriptor.Id == PresentationSlots.ProfileFrame)
        {
            return FrameDescription(definition.PlanAs<FramePlan>().Frame.Family);
        }

        return descriptor.Kind switch
        {
            DefinitionKinds.Effect => UI.T("Customize.Definition.EffectDescription", "Animated visual effect."),
            DefinitionKinds.Backdrop => UI.T("Customize.Definition.BackdropDescription", "Background style."),
            DefinitionKinds.CoverFrame => UI.T("Customize.Definition.FrameDescription", "Cover frame style."),
            DefinitionKinds.Typography => UI.T("Customize.Definition.TypographyDescription", "Typography preset."),
            DefinitionKinds.ControlSkin => UI.T("Customize.Definition.ControlDescription", "Control appearance."),
            _ => UI.T("Customize.Definition.LayoutDescription", "Presentation layout."),
        };
    }

    private static bool IsMeaningfulDefinitionChoice(SlotDescriptor descriptor, CompiledDefinition definition, DefinitionRef? selected) =>
        PresentationRuntime.SupportsSlot(definition, descriptor.Id)
        && (definition.Origin == PackOrigin.User
            || (descriptor.Kind == DefinitionKinds.Effect
                ? CuratedBuiltInEffects.Contains(definition.Ref.DefinitionId)
                : descriptor.Kind == DefinitionKinds.Backdrop
                    ? definition.Ref.DefinitionId is "builtin.neuterradise.backdrop.none" or "builtin.neuterradise.backdrop.gray"
                        or "builtin.neuterradise.backdrop.calm-yellow" or "builtin.neuterradise.backdrop.calm-blue"
                    : definition.Ref == selected || BuiltInPresentationCatalog.IsPrimary(definition.Ref)));

    private FrameworkElement DefinitionPreviewStage(SlotDescriptor descriptor, CompiledDefinition definition)
    {
        if (descriptor.Id == PresentationSlots.ProfileFrame)
        {
            return FrameChoicePreview.Create(definition.PlanAs<FramePlan>(), 132);
        }

        var stage = new Grid
        {
            Width = 210,
            Height = 150,
            Background = ThemeRuntime.Current.Brush("canvas"),
        };
        var preview = DefinitionOptionPreview(descriptor, definition);
        if (preview is not null)
        {
            var selected = _session.Resolve(descriptor.Id).Definition == definition.Ref;
            if (preview is EffectView effect) effect.SetPreviewSelected(selected);
            if (preview is BackdropView backdrop) backdrop.SetPreviewSelected(selected);
            preview.HorizontalAlignment = HorizontalAlignment.Center;
            preview.VerticalAlignment = VerticalAlignment.Center;
            stage.Children.Add(new Viewbox { Width = 210, Height = 150, Stretch = Stretch.Uniform, Child = preview });
            return stage;
        }

        stage.Children.Add(new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(18),
            Background = ThemeRuntime.Current.Brush(definition.PreviewAccent ?? "surface3"),
            BorderBrush = ThemeRuntime.Current.Brush("borderSubtle"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return stage;
    }

    private FrameworkElement? DefinitionOptionPreview(SlotDescriptor descriptor, CompiledDefinition definition)
    {
        if (definition.Plan is BackdropPlan backdrop)
        {
            var preview = BackdropView.Preview(backdrop, 132, PresentationVisualPriority.VisiblePreview);
            preview.Width = 210;
            return preview;
        }
        if (definition.Plan is EffectPlan effect)
        {
            var preview = PresentationEffects.Preview(effect, 132, PresentationVisualPriority.VisiblePreview);
            preview.Width = 210;
            return preview;
        }
        if (definition.Plan is HomeLayoutPlan home)
            return new HomePreviewView(home, _session.ResolveCompiled(PresentationSlots.HomeBackdrop).PlanAs<BackdropPlan>(),
                _session.ResolveCompiled(PresentationSlots.HomeSpotlight).PlanAs<SpotlightPlan>(), SampleData(), compact: true);
        if (definition.Plan is SpotlightPlan spotlight)
            return new HomePreviewView(_session.ResolveCompiled(PresentationSlots.HomeLayout).PlanAs<HomeLayoutPlan>(),
                _session.ResolveCompiled(PresentationSlots.HomeBackdrop).PlanAs<BackdropPlan>(), spotlight, SampleData(), compact: true);
        if (descriptor.Id == PresentationSlots.GalleryLayout)
        {
            return GalleryLayoutOptionPreview(definition.PlanAs<GalleryLayoutPlan>());
        }
        if (descriptor.Id == PresentationSlots.ProfileLayout)
        {
            return ProfileLayoutOptionPreview(definition.PlanAs<ProfileLayoutPlan>().Layout);
        }
        if (descriptor.Id == PresentationSlots.ProfileMediaLayout
            && _subject?.PreviewMediaCard is { } mediaCard)
        {
            var layout = definition.PlanAs<ProfileMediaLayoutPlan>().Layout;
            var visual = new MediaCardView(layout);
            visual.Bind(mediaCard);
            var aspect = Math.Max(0.4, layout.CardWidth / layout.CardHeight);
            visual.Width = Math.Max(280, 260 * aspect);
            visual.Height = visual.Width / aspect;
            visual.IsHitTestVisible = false;
            return new Viewbox { Width = 204, Height = 138, Stretch = Stretch.Uniform, Child = visual };
        }
        if (descriptor.Id == PresentationSlots.ProfileFrame)
        {
            var plan = definition.PlanAs<FramePlan>();
            var appearance = CardDataFactory.AppearanceFor(
                _session.ProfileWorking?.Overrides ?? ProfileAppearanceOverrides.Default,
                plan,
                ReducedMotionAuthority.IsReduced);
            var stage = new Grid
            {
                Width = 210,
                Height = 132,
                Background = ThemeRuntime.Current.Brush("canvas"),
            };
            stage.Children.Add(new CoverFrameView
            {
                Source = _coverSource,
                Transform = _session.ResolveMedia()?.Cover ?? MediaTransformState.Default,
                Plan = plan,
                Appearance = appearance,
                FrameMode = "full",
                Width = 104,
                Height = 104,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            });
            return stage;
        }
        if (descriptor.Id == PresentationSlots.ProfileCard && SampleData() is { } data)
        {
            var visual = new CardVisual(definition);
            visual.Bind(data);
            var aspect = Math.Max(0.4, visual.Plan.Aspect);
            visual.Width = Math.Max(280, 260 * aspect);
            visual.Height = visual.Width / aspect;
            visual.IsHitTestVisible = false;
            return new Viewbox { Width = 204, Height = 138, Stretch = Stretch.Uniform, Child = visual };
        }

        return null;
    }

    private FrameworkElement GalleryLayoutOptionPreview(GalleryLayoutPlan layout)
    {
        var stage = new Grid
        {
            Width = 210,
            Height = 132,
            Background = ThemeRuntime.Current.Brush("canvas"),
        };
        var sample = _services.Root?.SampleCard(null);
        if (sample is null)
        {
            stage.Children.Add(
                UI.Text(UI.T("Customize.NoSample", "Add a Profile to see a live preview here."), "micro", "textMuted")
                    .Align(HorizontalAlignment.Center, VerticalAlignment.Center));
            return stage;
        }

        var profile = new ProfilePresentationState(sample.ProfileId, 0, null, sample.Appearance);
        var context = new PresentationContext("gallery", sample.ProfileId, null, profile);
        var definition = _services.Presentation.ResolveCompiled(PresentationSlots.ProfileCard, context);
        var data = CardDataFactory.From(sample, _services.Presentation, 320, 640);
        var aspect = Math.Max(0.45, definition.PlanAs<CardPlan>().Aspect);
        var isSingleColumn = layout.Primitive is CollectionPrimitives.SingleColumn or CollectionPrimitives.List;
        var isShelf = layout.Primitive == CollectionPrimitives.Shelves;
        var collection = isSingleColumn
            ? UI.V(5)
            : UI.H(Math.Clamp(layout.Spacing / 3, 4, 8));
        collection.HorizontalAlignment = HorizontalAlignment.Center;
        collection.VerticalAlignment = VerticalAlignment.Center;

        var count = isSingleColumn ? 2 : 3;
        for (var index = 0; index < count; index++)
        {
            var visual = new CardVisual(definition);
            visual.Bind(data with
            {
                Name = index == 0 ? data.Name : UI.F("Customize.Sample.Profile", "Profile {0}", index + 1),
                IsFavorite = index == 0 ? data.IsFavorite : index % 2 == 0,
            });

            var targetHeight = layout.HeightPattern is { } pattern ? 210 / pattern[index % pattern.Count] : isSingleColumn ? 180 : isShelf ? 220 : 260;
            visual.Width = Math.Max(280, targetHeight * aspect);
            visual.Height = layout.HeightPattern is null ? visual.Width / aspect : targetHeight;
            visual.IsHitTestVisible = false;
            collection.Children.Add(visual);
        }

        stage.Children.Add(new Viewbox { Width = 198, Height = 120, Stretch = Stretch.Uniform, Child = collection });
        return stage;
    }

    private FrameworkElement SplitProfileLayoutOptionPreview(ProfileLayoutDefinition layout, ThemeRuntime theme)
    {
        var stage = UI.Grid("*", "*,*");
        stage.Width = 210;
        stage.Height = 150;
        stage.Background = theme.Brush("canvas");

        var banner = new Border
        {
            Child = _bannerSource is null
                ? null
                : new SkImageView
                {
                    Source = _bannerSource,
                    Transform = MediaTransformState.Default,
                    CornerRadiusValue = 8,
                    IsHitTestVisible = false,
                },
            Background = theme.Brush("surface3"),
            BorderBrush = theme.Brush("borderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(5),
        };

        var cover = new Border
        {
            Child = _coverSource is null
                ? null
                : new SkImageView
                {
                    Source = _coverSource,
                    Transform = MediaTransformState.Default,
                    CornerRadiusValue = 8,
                    IsHitTestVisible = false,
                },
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Background = theme.Brush("accentSoft"),
            BorderBrush = theme.Brush("accent"),
            BorderThickness = new Thickness(1),
        };
        var identity = UI.V(
            4,
            UI.H(6, cover, UI.V(
                2,
                UI.Text(_subject?.Detail.DisplayName ?? _launchRequest?.ProfileDisplayName ?? "Profile", "micro", maxLines: 1),
                UI.Text(_subject?.Detail.CategoryName ?? string.Empty, "micro", "textMuted", 1))),
            new Border
            {
                Height = 18,
                CornerRadius = new CornerRadius(4),
                Background = theme.Brush("surface2"),
            });
        identity.Margin = new Thickness(8);
        identity.VerticalAlignment = VerticalAlignment.Center;

        var identityPanel = UI.Surface(identity, Material.Frost, 8);
        identityPanel.Margin = new Thickness(5);
        var bannerOnLeft = layout.Id != "split-right";
        stage.Children.Add(banner.At(0, bannerOnLeft ? 0 : 1));
        stage.Children.Add(identityPanel.At(0, bannerOnLeft ? 1 : 0));
        return stage;
    }

    private FrameworkElement ProfileLayoutOptionPreview(ProfileLayoutDefinition layout)
    {
        var theme = ThemeRuntime.Current;
        if (layout.BannerMode == ProfileLayoutBannerMode.Split)
        {
            return SplitProfileLayoutOptionPreview(layout, theme);
        }

        var stage = new Grid
        {
            Width = 210,
            Height = 150,
            Background = theme.Brush("canvas"),
        };
        var bannerHeight = layout.Height == ProfileLayoutHeight.Low ? 44 : layout.Height == ProfileLayoutHeight.Medium ? 58 : 70;

        if (layout.BannerMode != ProfileLayoutBannerMode.None)
        {
            var banner = new Border
            {
                Child = _bannerSource is null
                    ? null
                    : new SkImageView
                    {
                        Source = _bannerSource,
                        Transform = MediaTransformState.Default,
                        CornerRadiusValue = layout.BannerMode == ProfileLayoutBannerMode.Contained ? 8 : 2,
                        IsHitTestVisible = false,
                    },
                Background = theme.Brush("surface3"),
                BorderBrush = theme.Brush("borderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(layout.BannerMode == ProfileLayoutBannerMode.Contained ? 8 : 2),
                Height = bannerHeight,
                VerticalAlignment = VerticalAlignment.Top,
            };
            if (layout.BannerMode == ProfileLayoutBannerMode.Contained)
            {
                banner.Margin = new Thickness(8, 7, 8, 0);
            }
            else if (layout.BannerMode == ProfileLayoutBannerMode.Split)
            {
                banner.Width = 96;
                banner.HorizontalAlignment = layout.CoverPlacement == ProfileLayoutCoverPlacement.Right
                    ? HorizontalAlignment.Left
                    : HorizontalAlignment.Right;
            }
            else
            {
                banner.HorizontalAlignment = HorizontalAlignment.Stretch;
            }

            stage.Children.Add(banner);
        }

        var coverSize = layout.CoverSize switch
        {
            ProfileLayoutCoverSize.Small => 25,
            ProfileLayoutCoverSize.Medium => 31,
            ProfileLayoutCoverSize.Large => 37,
            _ => 43,
        };
        if (layout.CoverPlacement != ProfileLayoutCoverPlacement.None)
        {
            var cover = new Border
            {
                Child = _coverSource is null
                    ? null
                    : new SkImageView
                    {
                        Source = _coverSource,
                        Transform = MediaTransformState.Default,
                        CornerRadiusValue = layout.CoverDetailLevel == CoverFrameDetailLevel.Hidden ? 6 : 10,
                        IsHitTestVisible = false,
                    },
                Width = coverSize,
                Height = coverSize,
                CornerRadius = new CornerRadius(layout.CoverDetailLevel == CoverFrameDetailLevel.Hidden ? 6 : 10),
                Background = theme.Brush("accentSoft"),
                BorderBrush = theme.Brush(layout.CoverDetailLevel == CoverFrameDetailLevel.Hidden ? "borderSubtle" : "accent"),
                BorderThickness = new Thickness(layout.CoverDetailLevel == CoverFrameDetailLevel.Full ? 2 : 1),
                VerticalAlignment = VerticalAlignment.Top,
            };
            cover.HorizontalAlignment = layout.CoverPlacement is ProfileLayoutCoverPlacement.InlineCenter or ProfileLayoutCoverPlacement.BottomCenterOverlap
                ? HorizontalAlignment.Center
                : layout.CoverPlacement == ProfileLayoutCoverPlacement.Right
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left;
            var coverTop = layout.BannerMode == ProfileLayoutBannerMode.None
                ? 12
                : Math.Max(8, bannerHeight - (coverSize * 0.35));
            cover.Margin = cover.HorizontalAlignment switch
            {
                HorizontalAlignment.Left => new Thickness(12, coverTop, 0, 0),
                HorizontalAlignment.Right => new Thickness(0, coverTop, 12, 0),
                _ => new Thickness(0, coverTop, 0, 0),
            };
            stage.Children.Add(cover);
        }

        var identity = UI.V(
            3,
            UI.Text(
                _subject?.Detail.DisplayName ?? _launchRequest?.ProfileDisplayName ?? "Profile",
                "micro",
                maxLines: 1),
            UI.Text(_subject?.Detail.CategoryName ?? string.Empty, "micro", "textMuted", 1));
        identity.VerticalAlignment = VerticalAlignment.Top;
        var identityTop = layout.BannerMode == ProfileLayoutBannerMode.None
            ? 62
            : Math.Max(bannerHeight + 8, bannerHeight + (coverSize * 0.65) + 4);
        identity.Measure(new Windows.Foundation.Size(186, double.PositiveInfinity));
        stage.Height = Math.Max(stage.Height, identityTop + identity.DesiredSize.Height + 8 + 18 + 6);
        identity.Margin = new Thickness(12, identityTop, 12, 0);
        identity.HorizontalAlignment = layout.IdentityAlignment switch
        {
            ProfileLayoutIdentityAlignment.Center => HorizontalAlignment.Center,
            ProfileLayoutIdentityAlignment.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
        stage.Children.Add(identity);

        var modules = new Grid
        {
            Height = 18,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 8, 6),
        };
        modules.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        modules.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modules.Children.Add(new Border
        {
            Margin = new Thickness(0, 0, 3, 0),
            CornerRadius = new CornerRadius(4),
            Background = theme.Brush("surface2"),
        }.At(0, 0));
        modules.Children.Add(new Border
        {
            Margin = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Background = theme.Brush("surface3"),
        }.At(0, 1));
        stage.Children.Add(modules);
        return stage;
    }
    private static string? FrameUsageText(CompiledDefinition definition)
    {
        return definition.Kind switch
        {
            DefinitionKinds.ProfileLayout => ProfileLayoutFrameUsage(definition.PlanAs<ProfileLayoutPlan>().Layout),
            DefinitionKinds.ProfileCard => CardFrameUsage(definition.PlanAs<CardPlan>()),
            DefinitionKinds.SpotlightStyle => definition.PlanAs<SpotlightPlan>().ShowCover
                ? UI.T("Customize.FrameUsage.Full", "Frame: full.")
                : UI.T("Customize.FrameUsage.None", "Frame: none."),
            DefinitionKinds.HomeLayout => UI.T("Customize.FrameUsage.FollowsSpotlight", "Frame: follows the selected Spotlight style."),
            _ => null,
        };
    }

    private static string ProfileLayoutFrameUsage(ProfileLayoutDefinition layout)
    {
        if (layout.CoverPlacement == ProfileLayoutCoverPlacement.None
            || layout.CoverDetailLevel == CoverFrameDetailLevel.Hidden)
        {
            return UI.T("Customize.FrameUsage.Hidden", "Frame: hidden.");
        }

        return layout.CoverDetailLevel == CoverFrameDetailLevel.Compact
            ? UI.T("Customize.FrameUsage.Compact", "Frame: compact.")
            : UI.T("Customize.FrameUsage.Full", "Frame: full.");
    }

    private static string CardFrameUsage(CardPlan plan)
    {
        if (!ContainsFrameNode(plan.Root) || string.Equals(plan.FrameMode, "none", StringComparison.Ordinal))
        {
            return UI.T("Customize.FrameUsage.None", "Frame: none.");
        }

        return string.Equals(plan.FrameMode, "lite", StringComparison.Ordinal)
            ? UI.T("Customize.FrameUsage.Compact", "Frame: compact.")
            : UI.T("Customize.FrameUsage.Full", "Frame: full.");
    }

    private static bool ContainsFrameNode(CompositionNode node) => node switch
    {
        FrameNode => true,
        StackNode stack => stack.Children.Any(ContainsFrameNode),
        GridNode grid => grid.Children.Any(ContainsFrameNode),
        OverlayNode overlay => overlay.Children.Any(ContainsFrameNode),
        _ => false,
    };



    private static string FrameDescription(Neuterradise.App.Design.CoverFrames.CoverFrameFamily family) => family switch
    {
        Neuterradise.App.Design.CoverFrames.CoverFrameFamily.None => UI.T("Customize.FrameDescription.None", "Cover without a frame."),
        Neuterradise.App.Design.CoverFrames.CoverFrameFamily.Champion or Neuterradise.App.Design.CoverFrames.CoverFrameFamily.Mythic or Neuterradise.App.Design.CoverFrames.CoverFrameFamily.Legendary or Neuterradise.App.Design.CoverFrames.CoverFrameFamily.Royal => UI.T("Customize.FrameDescription.Ornamented", "Ornamented frame with gem accents."),
        Neuterradise.App.Design.CoverFrames.CoverFrameFamily.Cyber or Neuterradise.App.Design.CoverFrames.CoverFrameFamily.Hud => UI.T("Customize.FrameDescription.Technical", "Technical ring with etched marks."),
        Neuterradise.App.Design.CoverFrames.CoverFrameFamily.Arcane or Neuterradise.App.Design.CoverFrames.CoverFrameFamily.Celestial or Neuterradise.App.Design.CoverFrames.CoverFrameFamily.NeonCircuit or Neuterradise.App.Design.CoverFrames.CoverFrameFamily.Holographic => UI.T("Customize.FrameDescription.Luminous", "Luminous animated frame."),
        _ => UI.T("Customize.FrameDescription.Decorative", "Decorative frame around the Cover."),
    };

}
