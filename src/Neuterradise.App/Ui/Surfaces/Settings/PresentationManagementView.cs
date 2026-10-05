using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Localization;
using Neuterradise.App.Presentation;

namespace Neuterradise.App.Ui;

public sealed class PresentationManagementView
{
    private static readonly HashSet<string> Tabs = new(StringComparer.Ordinal)
    {
        "fonts",
        "frames",
        "backdrops",
        "effects",
        "control-styles",
        "packs",
    };

    private readonly AppServices _services;
    private readonly StackPanel _editor = UI.V(10);
    private readonly Action<string>? _tabChanged;
    private string _tab;

    public PresentationManagementView(AppServices services, string? selectedTab = null, Action<string>? tabChanged = null)
    {
        _services = services;
        _tabChanged = tabChanged;
        _tab = selectedTab is not null && Tabs.Contains(selectedTab) ? selectedTab : "fonts";
        Render();
    }

    public FrameworkElement View => _editor;

    private void Run(Task task, string context, string message) =>
        _services.RunUserAction(task, context, message);

    private void Render()
    {
        _editor.Children.Clear();
        var assets = UI.Wrap(6);
        var components = UI.Wrap(6);
        foreach (var (id, key, fallback) in new[]
        {
            ("fonts", "Settings.Presentation.Fonts", "Fonts"),
            ("frames", "Settings.Presentation.Frames", "Frames"),
            ("backdrops", "Settings.Presentation.Backdrops", "Backdrops"),
            ("effects", "Settings.Presentation.Effects", "Effects"),
            ("control-styles", "Settings.Presentation.ControlStyles", "Control Styles"),
            ("packs", "Settings.Presentation.PacksAdvanced", "Packs / Advanced"),
        })
        {
            var value = id;
            var tabs = id is "fonts" or "frames" or "backdrops" ? assets : components;
            tabs.Children.Add(UI.Chip(UI.T(key, fallback), _tab == value, () => SelectTab(value)));
        }

        _editor.Children.Add(UI.V(4,
            UI.Text(UI.T("Settings.Presentation.Assets", "Importable assets"), "micro", "textMuted"), assets,
            UI.Text(UI.T("Settings.Presentation.Components", "Pack components"), "micro", "textMuted").Margin(0, 4, 0, 0), components));
        if (_tab == "packs")
        {
            RenderPacksAdvanced();
            return;
        }

        RenderLibrary();
    }

    private void SelectTab(string tab)
    {
        if (_tab == tab)
        {
            return;
        }

        _tab = tab;
        _tabChanged?.Invoke(tab);
        Render();
    }

    private void RenderLibrary()
    {
        var actions = LibraryActions();
        if (actions.Children.Count > 0)
        {
            _editor.Children.Add(actions);
        }

        var definitions = DefinitionsForCurrentTab().ToList();
        var frameGrid = UI.Grid(string.Empty, "*,*,*");
        frameGrid.ColumnSpacing = 10;
        frameGrid.RowSpacing = 10;
        Panel cards = _tab == "frames" ? frameGrid : new VariableWrap(10);
        foreach (var definition in definitions)
        {
            var name = SurfaceText.PresentationName(definition);
            var description = SurfaceText.PresentationDescription(definition);
            var isDefault = PresentationSlots.All.Any(slot => slot.Default == definition.Ref);
            var badges = UI.Wrap(
                4,
                isDefault ? UI.Badge(UI.T("Settings.Presentation.Default", "Default"), "accent") : null,
                UI.Badge(
                    definition.Origin == PackOrigin.User
                        ? UI.T("Settings.Presentation.User", "User")
                        : UI.T("Settings.Presentation.BuiltIn", "Built-in"),
                    definition.Origin == PackOrigin.User ? "accent" : "neutral"),
                UI.Badge(KindLabel(definition.Kind)),
                definition.Plan is EffectPlan effect
                    ? UI.Badge(EffectChannelLabel(effect.Channel))
                    : null);

            var body = UI.V(
                7,
                LibraryPreview(definition),
                UI.Text(name, "card-title", maxLines: 2).Tip(name),
                string.IsNullOrWhiteSpace(description)
                    ? UI.Text(KindDescription(definition.Kind), "caption", "textMuted", maxLines: 2)
                    : UI.WrappedText(description, "caption"),
                badges);

            var card = UI.Surface(body, Material.Raised, 12, 12);
            if (_tab == "frames")
            {
                var index = cards.Children.Count;
                if (index % 3 == 0) frameGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(card, index / 3);
                Grid.SetColumn(card, index % 3);
            }
            else card.Width = 224;
            card.MinHeight = 218;
            cards.Children.Add(card);
        }

        FrameworkElement libraryBody = definitions.Count == 0
            ? UI.Guidance(
                UI.T("Settings.Presentation.Empty.Title", "Nothing here yet"),
                UI.T("Settings.Presentation.Empty.Library", "No presentation items are available in this library yet."))
            : cards;

        _editor.Children.Add(UI.Section(
            CurrentLibraryTitle(),
            CurrentLibraryDescription(),
            libraryBody));
    }

    private VariableWrap LibraryActions()
    {
        var actions = UI.Wrap(8);
        if (_tab == "fonts")
        {
            actions.Children.Add(UI.Button(
                UI.T("Settings.Presentation.AddFont", "Add font…"),
                () => Run(
                    ImportAssetAsync(font: true),
                    "PresentationManagementView.ImportFont",
                    UI.T("Customize.InstallFailed", "This pack can''t be installed")),
                ButtonKind.Secondary,
                "icon.action.add"));
        }

        if (_tab == "backdrops")
        {
            actions.Children.Add(UI.Button(
                UI.T("Settings.Presentation.AddBackdrop", "Add image or video…"),
                () => Run(
                    ImportAssetAsync(font: false),
                    "PresentationManagementView.ImportBackdrop",
                    UI.T("Customize.InstallFailed", "This pack can''t be installed")),
                ButtonKind.Secondary,
                "icon.action.add"));
        }

        if (_tab == "frames")
        {
            actions.Children.Add(UI.Button(
                UI.T("Customize.Frame.AddCustom", "Add custom frame…"),
                () => Run(
                    ImportCustomFrameAsync(),
                    "PresentationManagementView.ImportFrame",
                    UI.T("Customize.Frame.AddFailedDetail", "Could not add the frame.")),
                ButtonKind.Secondary,
                "icon.action.add"));
        }

        return actions;
    }

    private IEnumerable<CompiledDefinition> DefinitionsForCurrentTab()
    {
        var kind = _tab switch
        {
            "fonts" => DefinitionKinds.Typography,
            "frames" => DefinitionKinds.CoverFrame,
            "backdrops" => DefinitionKinds.Backdrop,
            "effects" => DefinitionKinds.Effect,
            "control-styles" => DefinitionKinds.ControlSkin,
            _ => string.Empty,
        };

        var definitions = _services.Presentation.DefinitionsOf(kind)
            .Where(definition => definition.Origin == PackOrigin.User || BuiltInPresentationCatalog.IsPrimary(definition.Ref))
            .Where(definition => _tab != "frames"
                || definition.Origin != PackOrigin.BuiltIn
                || definition.PlanAs<FramePlan>().Frame.Id != "none");
        if (_tab == "backdrops")
            return definitions
                .OrderBy(BackdropLibraryOrder)
                .ThenBy(SurfaceText.PresentationName, StringComparer.CurrentCultureIgnoreCase);
        if (_tab == "frames")
            return definitions
                .OrderBy(FrameChoicePreview.Order)
                .ThenBy(SurfaceText.PresentationName, StringComparer.CurrentCultureIgnoreCase);
        return definitions
            .OrderBy(definition => definition.Origin)
            .ThenBy(SurfaceText.PresentationName, StringComparer.CurrentCultureIgnoreCase);
    }

    private static int BackdropLibraryOrder(CompiledDefinition definition) => definition.Ref.DefinitionId switch
    {
        "builtin.neuterradise.backdrop.none" => 0,
        "builtin.neuterradise.backdrop.calm-yellow" => 1,
        "builtin.neuterradise.backdrop.calm-blue" => 2,
        "builtin.neuterradise.backdrop.gray" => 3,
        _ => definition.Origin == PackOrigin.User ? 100 : 50,
    };

    private string CurrentLibraryTitle() => _tab switch
    {
        "fonts" => UI.T("Settings.Presentation.Fonts", "Fonts"),
        "frames" => UI.T("Settings.Presentation.Frames", "Frames"),
        "backdrops" => UI.T("Settings.Presentation.Backdrops", "Backdrops"),
        "effects" => UI.T("Settings.Presentation.Effects", "Effects"),
        "control-styles" => UI.T("Settings.Presentation.ControlStyles", "Control Styles"),
        _ => UI.T("Settings.Presentation.Fonts", "Fonts"),
    };

    private string CurrentLibraryDescription() => _tab switch
    {
        "fonts" => UI.T("Settings.Presentation.Fonts.ImportHelp", "Add a local .ttf or .otf font. Choose it in Appearance → Fonts."),
        "frames" => UI.T("Settings.Presentation.Frames.ImportHelp", "Add a square .png or .webp, 256–4096 px, with a transparent center. Choose it in Profile Customize → Frame."),
        "backdrops" => UI.T("Settings.Presentation.Backdrops.ImportHelp", "Add .png, .jpg, .jpeg, .webp or .mp4. Choose it in Home or Profile Customize → Backdrop."),
        "effects" => UI.T("Settings.Presentation.Effects.PackHelp", "Built-in effects and installed pack effects. Add custom effects through Packs / Advanced; choose them in Customize."),
        "control-styles" => UI.T("Settings.Presentation.ControlStyles.PackHelp", "Styles for buttons, inputs and tabs. Add custom styles through Packs / Advanced."),
        _ => string.Empty,
    };

    private static string KindLabel(string kind) => kind switch
    {
        DefinitionKinds.Theme => UI.T("Customize.Theme", "Theme"),
        DefinitionKinds.Typography => UI.T("Customize.Typography", "Typography"),
        DefinitionKinds.ControlSkin => UI.T("Customize.ControlSkin", "Control Style"),
        DefinitionKinds.Backdrop => UI.T("Customize.Profile.Backdrop", "Backdrop"),
        DefinitionKinds.CoverFrame => UI.T("Customize.Profile.Frame", "Frame"),
        DefinitionKinds.Effect => UI.T("Settings.Presentation.Effects", "Effects"),
        DefinitionKinds.HomeLayout => UI.T("Customize.Home.Layout", "Home Layout"),
        DefinitionKinds.SpotlightStyle => UI.T("Customize.Home.Spotlight", "Spotlight"),
        DefinitionKinds.GalleryLayout => UI.T("Customize.Gallery.Layout", "Gallery Layout"),
        DefinitionKinds.ProfileCard => UI.T("Customize.Profile.Card", "Card"),
        DefinitionKinds.ProfileLayout => UI.T("Customize.Profile.Layout", "Profile Layout"),
        DefinitionKinds.ProfileMediaLayout => UI.T("Customize.Profile.MediaLayout", "Media Layout"),
        _ => kind,
    };

    private static string KindDescription(string kind) => kind switch
    {
        DefinitionKinds.Effect => UI.T("Customize.Definition.EffectDescription", "Animated visual effect."),
        DefinitionKinds.Backdrop => UI.T("Customize.Definition.BackdropDescription", "Background style."),
        DefinitionKinds.CoverFrame => UI.T("Customize.Definition.FrameDescription", "Cover frame style."),
        DefinitionKinds.Typography => UI.T("Customize.Definition.TypographyDescription", "Typography preset."),
        DefinitionKinds.ControlSkin => UI.T("Customize.Definition.ControlDescription", "Control appearance."),
        _ => UI.T("Customize.Definition.LayoutDescription", "Presentation layout."),
    };

    private FrameworkElement LibraryPreview(CompiledDefinition definition)
    {
        var theme = ThemeRuntime.Current;
        if (definition.Plan is FramePlan frame)
            return new Grid
            {
                Height = 92,
                Children = { FrameChoicePreview.Create(frame, 92).Align(HorizontalAlignment.Center, VerticalAlignment.Center) },
            };
        if (definition.Plan is EffectPlan effect)
            return new Border
            {
                Child = PresentationEffects.Preview(effect, 92),
                Height = 92,
                CornerRadius = new CornerRadius(8),
                Background = theme.Brush("canvas"),
            };
        if (definition.Plan is BackdropPlan backdrop)
            return new Border
            {
                Child = BackdropView.Preview(backdrop, 92),
                Height = 92,
                CornerRadius = new CornerRadius(8),
                Background = theme.Brush("canvas"),
            };
        if (definition.Plan is ThemePlan themePlan)
            return ThemePreviewView.Create(themePlan, 92);
        if (definition.Plan is TypographyPlan typography)
        {
            TextBlock Sample(string text, TypographyFace face, double size) => new()
            {
                Text = text,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(LocalPresentationFonts.Current.Register(face)),
                FontSize = size,
                Foreground = theme.Brush("textPrimary"),
            };
            var preview = UI.Surface(UI.V(3,
                Sample("Aa Naut", typography.Heading, 20),
                Sample("A Navigator for Your Things Worth Keeping", typography.Body, 11)),
                Material.Grounded, 8, 10);
            preview.Height = 92;
            return preview;
        }
        if (definition.Plan is ControlSkinPlan skin)
            return UI.ControlSheet(skin);

        var schematic = new Grid { Height = 92, Background = theme.Brush("surface2") };
        schematic.Children.Add(new Border
        {
            Width = 74,
            Height = 48,
            CornerRadius = new CornerRadius(10),
            Background = theme.Brush(definition.PreviewAccent ?? "surface3"),
            BorderBrush = theme.Brush("borderSubtle"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border { Child = schematic, Height = 92, CornerRadius = new CornerRadius(8) };
    }

    private static string EffectChannelLabel(string channel) => channel switch
    {
        "particles" => UI.T("Customize.Effect.Channel.Particles", "Particles"),
        "lighting" => UI.T("Customize.Effect.Channel.Light", "Light"),
        "surface" => UI.T("Customize.Effect.Channel.Surface", "Surface"),
        "interaction" => UI.T("Customize.Effect.Channel.Interaction", "Interaction"),
        _ => UI.T("Customize.Effect.Channel.Atmosphere", "Atmosphere"),
    };

    private void RenderPacksAdvanced()
    {
        var actions = UI.Wrap(
            8,
            UI.Button(
                UI.T("Customize.InstallFile", "Install from file…"),
                () => Run(
                    InstallAsync(folder: false),
                    "PresentationManagementView.InstallFileAsync",
                    UI.T("Customize.InstallFailed", "This pack can''t be installed")),
                ButtonKind.Primary,
                "icon.action.add"),
            UI.Button(
                UI.T("Customize.InstallFolder", "Install from folder…"),
                () => Run(
                    InstallAsync(folder: true),
                    "PresentationManagementView.InstallFolderAsync",
                    UI.T("Customize.InstallFailed", "This pack can''t be installed")),
                ButtonKind.Secondary));

        var list = UI.V(10);
        foreach (var pack in _services.Presentation.Packs.OrderBy(pack => pack.Origin))
        {
            var manifest = pack.Manifest;
            var errors = pack.Diagnostics.Count(diagnostic => diagnostic.IsError);
            if (pack.Origin == PackOrigin.BuiltIn)
            {
                list.Children.Add(UI.Surface(
                    UI.V(
                        4,
                        UI.H(
                            8,
                            UI.Text(UI.T("Settings.Presentation.BuiltInLibrary", "Naut Built-in Library"), "card-title"),
                            UI.Badge(UI.T("Settings.Presentation.ReadOnly", "Read-only"))),
                        UI.Text(
                            UI.T("Settings.Presentation.IncludedWithApp", "Included with the app"),
                            "caption",
                            "textMuted"),
                        errors > 0
                            ? UI.Badge(UI.F("Customize.Errors", "{0} problems", errors), "danger")
                            : null),
                    Material.Raised,
                    12,
                    14));
                continue;
            }

            var usages = _services.Presentation.CountUsages(manifest.PackId);
            var details = UI.V(
                3,
                UI.H(
                    8,
                    UI.Text(manifest.Name, "card-title"),
                    UI.Badge(UI.T("Customize.Installed", "Installed")),
                    errors > 0
                        ? UI.Badge(UI.F("Customize.Errors", "{0} problems", errors), "danger")
                        : null),
                manifest.Description is { Length: > 0 } description
                    ? UI.WrappedText(description, "body-muted")
                    : null,
                UI.Text(
                    UI.F(
                        "Settings.Presentation.AdvancedPackMeta",
                        "{0} · version {1} · {2} definitions · {3} assets · used in {4} places",
                        manifest.PackId,
                        manifest.Version,
                        manifest.Definitions.Count,
                        manifest.Assets.Count,
                        usages),
                    "caption"));

            var packId = manifest.PackId;
            var packActions = UI.Wrap(
                6,
                UI.Button(
                    UI.T("Customize.Export", "Export"),
                    () => Run(
                        ExportAsync(packId),
                        "PresentationManagementView.ExportAsync",
                        UI.T("Customize.ExportFailed", "The presentation pack could not be exported.")),
                    ButtonKind.Secondary),
                UI.Button(
                    UI.T("Customize.Remove", "Remove"),
                    () => Run(
                        RemoveAsync(packId, manifest.Name, usages),
                        "PresentationManagementView.RemoveAsync",
                        UI.T("Customize.RemoveFailed", "The presentation pack could not be removed.")),
                    ButtonKind.Destructive));

            var problems = UI.V(2);
            foreach (var diagnostic in pack.Diagnostics.Take(6))
            {
                problems.Children.Add(UI.WrappedText(
                    $"{diagnostic.Code} · {diagnostic.Subject}: {diagnostic.Detail}",
                    "caption",
                    diagnostic.IsError ? "danger" : "warning"));
            }

            list.Children.Add(UI.Surface(
                UI.V(
                    8,
                    UI.Grid("auto", "*,auto", details.At(0, 0), packActions.At(0, 1)),
                    problems),
                Material.Raised,
                12,
                14));
        }

        _editor.Children.Add(UI.Section(
            UI.T("Settings.Presentation.PacksAdvanced", "Packs / Advanced"),
            UI.T(
                "Settings.Presentation.PacksAdvanced.FormatHelp",
                "Install .ntpack or .zip, or a folder containing pack.json. Packs contain declarative layouts, themes, fonts, frames, backdrops, effects or control styles and their assets. Scripts and executables are not supported."),
            actions,
            list));
    }

    private async Task InstallAsync(bool folder)
    {
        string? source = folder
            ? await _services.PickFolderAsync().ConfigureAwait(true)
            : (await _services.PickFilesAsync(false, ".zip", ".ntpack").ConfigureAwait(true)).FirstOrDefault();
        if (source is null)
        {
            return;
        }

        var result = await _services.Presentation.InstallPackAsync(source, replaceExisting: false).ConfigureAwait(true);
        if (!result.Succeeded
            && result.Diagnostics.Any(diagnostic => diagnostic.Code == PackDiagnosticCodes.AlreadyInstalled)
            && await _services.ConfirmAsync(
                UI.T("Customize.ReplaceTitle", "Replace pack?"),
                UI.T("Customize.ReplaceBody", "A pack with this id is already installed. Replace it with this version?"),
                UI.T("Customize.Replace", "Replace")).ConfigureAwait(true))
        {
            result = await _services.Presentation.InstallPackAsync(source, replaceExisting: true).ConfigureAwait(true);
        }

        if (result.Succeeded)
        {
            _services.Toast(UI.F("Customize.Installed", "Installed", result.Pack?.Manifest.Name ?? string.Empty), "success");
        }
        else
        {
            var reason = result.Message
                ?? string.Join(
                    Environment.NewLine,
                    result.Diagnostics.Where(diagnostic => diagnostic.IsError).Take(4).Select(diagnostic => diagnostic.Detail));
            await _services.ConfirmAsync(
                UI.T("Customize.InstallFailed", "This pack can''t be installed"),
                reason,
                UI.T("Common.Ok", "OK")).ConfigureAwait(true);
        }

        Render();
    }

    private async Task ExportAsync(string packId)
    {
        var destination = await _services.PickSaveFileAsync(
            packId,
            ".zip",
            UI.T("Customize.PackFile", "Presentation pack")).ConfigureAwait(true);
        if (destination is null)
        {
            return;
        }

        try
        {
            await _services.Presentation.ExportPackAsync(packId, destination).ConfigureAwait(true);
            _services.Toast(UI.T("Customize.Exported", "Pack exported"), "success");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _services.Toast(exception.Message, "danger");
        }
    }

    private async Task RemoveAsync(string packId, string name, int usages)
    {
        var message = usages > 0
            ? UI.F(
                "Customize.RemoveUsed",
                "{0} is used in {1} places. Those places will return to their built-in look. Your Profiles and media are not affected.",
                name,
                usages)
            : UI.F("Customize.RemoveBody", "Remove {0}? Your Profiles and media are not affected.", name);
        if (!await _services.ConfirmAsync(
            UI.T("Customize.RemoveTitle", "Remove pack?"),
            message,
            UI.T("Customize.Remove", "Remove"),
            destructive: true).ConfigureAwait(true))
        {
            return;
        }

        var result = await _services.Presentation.RemovePackAsync(packId).ConfigureAwait(true);
        _services.Toast(
            result.Removed
                ? UI.T("Customize.Removed", "Pack removed")
                : result.Message ?? string.Empty,
            result.Removed ? "success" : "danger");
        PresentationBinder.ApplyGlobal(_services.Presentation);
        Render();
    }

    private async Task ImportCustomFrameAsync()
    {
        var source = (await _services.PickFilesAsync(false, ".png", ".webp").ConfigureAwait(true)).FirstOrDefault();
        if (source is null)
        {
            return;
        }

        try
        {
            using var package = CustomFrameImportBuilder.Build(source);
            var result = await _services.Presentation
                .InstallPackAsync(package.StagingPath, replaceExisting: false)
                .ConfigureAwait(true);

            if (!result.Succeeded
                && result.Diagnostics.Any(diagnostic => diagnostic.Code == PackDiagnosticCodes.AlreadyInstalled)
                && _services.Presentation.Find(package.Definition) is not null)
            {
                _services.Toast(UI.T("Customize.Installed", "Installed"), "success");
                Render();
                return;
            }

            if (!result.Succeeded)
            {
                await _services.ConfirmAsync(
                    UI.T("Customize.Frame.AddFailedTitle", "Custom frame could not be added"),
                    UI.T("Customize.Frame.AddFailedDetail", "The frame could not be installed. Check the image and try again."),
                    UI.T("Common.Ok", "OK")).ConfigureAwait(true);
                return;
            }

            var installed = _services.Presentation.Find(package.Definition);
            if (installed is null)
            {
                await _services.ConfirmAsync(
                    UI.T("Customize.Frame.AddFailedTitle", "Custom frame could not be added"),
                    UI.T("Customize.Frame.AddUnavailable", "The frame was installed but is not available yet. Reopen Customize and try again."),
                    UI.T("Common.Ok", "OK")).ConfigureAwait(true);
                return;
            }

            _services.Toast(UI.F("Customize.Frame.Added", "Added custom frame {0}.", package.DisplayName), "success");
            Render();
        }
        catch (Exception exception) when (exception is InvalidDataException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException)
        {
            await _services.ConfirmAsync(
                UI.T("Customize.Frame.AddFailedTitle", "Custom frame could not be added"),
                UI.T("Customize.Frame.AddFailedDetail", "The frame could not be installed. Check the image and try again."),
                UI.T("Common.Ok", "OK")).ConfigureAwait(true);
        }
    }

    private async Task ImportAssetAsync(bool font)
    {
        var source = (await _services.PickFilesAsync(
            false,
            font ? [".ttf", ".otf"] : [".png", ".jpg", ".jpeg", ".webp", ".mp4"]).ConfigureAwait(true)).FirstOrDefault();
        if (source is null)
        {
            return;
        }

        using var package = font
            ? PresentationAssetImportBuilder.Font(source)
            : PresentationAssetImportBuilder.Backdrop(source);
        var result = await _services.Presentation.InstallPackAsync(package.StagingPath, replaceExisting: false).ConfigureAwait(true);
        if (!result.Succeeded
            && !result.Diagnostics.Any(diagnostic => diagnostic.Code == PackDiagnosticCodes.AlreadyInstalled))
        {
            throw new InvalidDataException(
                result.Message
                ?? string.Join(
                    Environment.NewLine,
                    result.Diagnostics.Where(diagnostic => diagnostic.IsError).Select(diagnostic => diagnostic.Detail)));
        }

        _services.Toast(UI.T("Customize.Installed", "Installed"), "success");
        Render();
    }
}
