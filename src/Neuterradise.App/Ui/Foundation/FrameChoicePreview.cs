using Microsoft.UI.Xaml;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;

namespace Neuterradise.App.Ui;

internal static class FrameChoicePreview
{
    public static int Order(CompiledDefinition definition) => definition.Origin == PackOrigin.User ? 1000 : definition.Ref.DefinitionId switch
    {
        "builtin.neuterradise.frame.none-rounded" => 0,
        "builtin.neuterradise.frame.none-circle" => 1,
        "builtin.neuterradise.frame.none-flower" => 2,
        _ => 10,
    };

    public static FrameworkElement Create(FramePlan plan, double size)
    {
        if (plan.OverlayAssetPath is { } artworkPath)
        {
            return new SkImageView
            {
                Source = new ImageRef(artworkPath, 256),
                Transform = MediaTransformState.Default with { Fit = "fit" },
                Width = size,
                Height = size,
                IsHitTestVisible = false,
            };
        }

        return new CoverFrameView
        {
            Width = size,
            Height = size,
            Plan = plan,
            Appearance = CardDataFactory.AppearanceFor(ProfileAppearanceOverrides.Default, plan, reduceMotion: true)
                with { CoverShadow = false },
            FrameMode = "full",
            ShowPlaceholderPerson = false,
            IsHitTestVisible = false,
        };
    }
}
