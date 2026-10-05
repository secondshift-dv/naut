using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Neuterradise.App.Activity;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Maintenance;
using Neuterradise.App.Localization;
using Neuterradise.App.Presentation;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Trash;

namespace Neuterradise.App.Ui;

/// <summary>
/// Settings: the quietest surface. Visible prose is resolved at the presentation boundary so a warm
/// language switch never exposes renderer-neutral diagnostic strings from the backing view models.
/// </summary>
public sealed class SettingsSurface : Surface
{
    private readonly SettingsViewModel _vm;
    private readonly ExplorerLocationProvider _explorer;
    private readonly Grid _root = UI.Grid("auto,*", "168,*");
    private readonly StackPanel _rail = UI.V(1);
    private readonly ScrollViewer _content;
    private readonly StackPanel _page = UI.V(12);
    private Disposables _sectionBag = new();
    private bool _hasRendered;
    private SettingsSection _renderedSection;
    private SettingsSubsection? _renderedSubsection;
    private string _appearanceTab = "theme";
    private string _presentationTab = "fonts";

    public SettingsSurface(AppServices services, SettingsViewModel vm) : base(services)
    {
        _vm = vm;
        _explorer = new ExplorerLocationProvider(services.Paths);
        _root.Background = ThemeRuntime.Current.Brush("canvas");

        var railScroll = UI.Scroll(_rail.Margin(6, 8, 6, 8));
        railScroll.Padding = new Thickness(0);
        var railSurface = new Border
        {
            Child = railScroll,
            Background = ThemeRuntime.Current.Brush("surface1"),
            BorderBrush = ThemeRuntime.Current.Brush("borderSubtle"),
            BorderThickness = new Thickness(0, 0, 1, 0)
        };
        _root.Children.Add(railSurface.At(0, 0, rowSpan: 2));
        _page.MaxWidth = 880;
        _page.HorizontalAlignment = HorizontalAlignment.Left;
        _content = UI.Scroll(_page.Margin(16, 12, 16, 20));
        _content.SizeChanged += (_, _) => UpdateContentEnvelope();
        _root.Children.Add(_content.At(0, 1, rowSpan: 2));
        _root.SizeChanged += (_, _) =>
        {
            var narrow = _root.ActualWidth < 620;
            _root.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(_root.ActualWidth < 960 ? 144 : 168);
            _root.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            railSurface.At(0, 0, rowSpan: narrow ? 1 : 2, columnSpan: narrow ? 2 : 1);
            _content.At(narrow ? 1 : 0, narrow ? 0 : 1, rowSpan: narrow ? 1 : 2, columnSpan: narrow ? 2 : 1);
            _rail.Orientation = narrow ? Orientation.Horizontal : Orientation.Vertical;
            railScroll.HorizontalScrollMode = narrow ? ScrollMode.Enabled : ScrollMode.Disabled;
            railScroll.HorizontalScrollBarVisibility = narrow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            railScroll.VerticalScrollMode = narrow ? ScrollMode.Disabled : ScrollMode.Enabled;
            foreach (var entry in _rail.Children.OfType<FrameworkElement>())
            {
                if (entry is Border) entry.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
                else entry.MaxWidth = narrow ? 180 : double.PositiveInfinity;
            }
        };

        Bag.Add(Observe.Props(_vm, Render,
            nameof(SettingsViewModel.ActiveSection),
            nameof(SettingsViewModel.ActiveSubsection),
            nameof(SettingsViewModel.CurrentLanguage),
            nameof(SettingsViewModel.ManagedStorageFormatted),
            nameof(SettingsViewModel.TotalProfilesCount),
            nameof(SettingsViewModel.ActiveMediaCount),
            nameof(SettingsViewModel.UnresolvedFaceCount),
            nameof(SettingsViewModel.UpdateStatusText),
            nameof(SettingsViewModel.UpdateErrorText),
            nameof(SettingsViewModel.UpdateCandidateVersion),
            nameof(SettingsViewModel.CanInstallUpdate),
            nameof(SettingsViewModel.UpdateFeedUrl),
            nameof(SettingsViewModel.PublishedRelease),
            nameof(SettingsViewModel.ThirdPartyComponents),
            nameof(SettingsViewModel.Status),
            nameof(SettingsViewModel.ErrorMessage)));
        ThemeRuntime.Current.Changed += OnThemeChanged;
        Bag.Add(() => ThemeRuntime.Current.Changed -= OnThemeChanged);
        Render();
    }

    private void UpdateContentEnvelope()
    {
        var viewportWidth = _content.ViewportWidth > 0
            ? _content.ViewportWidth
            : _content.ActualWidth;
        if (viewportWidth <= 0)
        {
            return;
        }

        var available = Math.Max(
            0,
            viewportWidth
            - _content.Padding.Left
            - _content.Padding.Right
            - _page.Margin.Left
            - _page.Margin.Right);
        _page.Width = Math.Min(_page.MaxWidth, available);
    }

    public override FrameworkElement View => _root;

    private void OnThemeChanged() => UiDispatch.Run(Render);

    public override ScreenStateViewModel Model => _vm;

    public override void OnPresentationChanged(PresentationChangedEventArgs args)
    {
        if (args.PacksChanged || args.Slots.Any(slot => slot.StartsWith("appearance.", StringComparison.Ordinal)))
        {
            Render();
        }
    }

    protected override void OnActivated(AppRoute route)
    {
        var target = route switch
        {
            TrashRoute => new SettingsRoute(SettingsSection.Trash),
            VaultHealthRoute => new SettingsRoute(SettingsSection.Vault, SettingsSubsection.VaultHealth),
            SettingsRoute settings => settings,
            _ => new SettingsRoute(),
        };
        _vm.ApplyRoute(target);
    }

    private void RenderRail()
    {
        _rail.Children.Clear();
        var theme = ThemeRuntime.Current;
        var order = new[]
        {
            SettingsSection.Appearance, SettingsSection.Language, SettingsSection.Presentation,
            SettingsSection.Vault, SettingsSection.PeopleOrganization, SettingsSection.Activity, SettingsSection.Trash,
            SettingsSection.About,
        };
        foreach (var key in order)
        {
            var item = _vm.RailItems.First(item => item.Key == key);
            if (key is SettingsSection.Vault or SettingsSection.About)
            {
                var divider = UI.Divider();
                divider.Margin = new Thickness(8, 8, 8, 8);
                divider.Visibility = _root.ActualWidth < 620 ? Visibility.Collapsed : Visibility.Visible;
                _rail.Children.Add(divider);
            }
            var selected = item.Key == _vm.ActiveSection;
            var entry = new Button
            {
                Content = UI.Text(item.Title, "control", maxLines: 2),
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(8),
                Background = selected ? theme.Brush("surfaceSelected") : theme.Brush("transparent"),
                BorderBrush = theme.Brush("transparent"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Tag = item.Key,
                MaxWidth = _root.ActualWidth < 620 ? 180 : double.PositiveInfinity,
            };
            entry.Tip(item.Description);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(entry, item.Title);
            entry.Click += (_, _) => Services.Navigation.Navigate(new SettingsRoute(item.Key));
            _rail.Children.Add(entry);
        }
    }

    private void Render()
    {
        var preserveScroll = _hasRendered
            && _renderedSection == _vm.ActiveSection
            && _renderedSubsection == _vm.ActiveSubsection;
        var previousOffset = _content.VerticalOffset;

        RenderRail();
        _root.Background = ThemeRuntime.Current.Brush("canvas");
        _sectionBag.Dispose();
        _sectionBag = new Disposables();
        _page.Children.Clear();

        var title = _vm.RailItems.FirstOrDefault(r => r.Key == _vm.ActiveSection)?.Title
            ?? UI.T("Nav.Settings", "Settings");
        _page.Children.Add(UI.Text(title, "page-title"));

        if (_vm.HasError && !string.IsNullOrWhiteSpace(_vm.ErrorMessage))
        {
            _page.Children.Add(UI.Surface(
                UI.WrappedText(_vm.ErrorMessage, "body", "danger"),
                Material.Deep,
                10,
                12));
        }

        UIElement section = (_vm.ActiveSection, _vm.ActiveSubsection) switch
        {
            (SettingsSection.Appearance, _) => Appearance(),
            (SettingsSection.Presentation, _) => new PresentationManagementView(
                Services,
                _presentationTab,
                tab => _presentationTab = tab).View,
            (SettingsSection.Vault, SettingsSubsection.VaultHealth) => Health(_vm.Health),
            (SettingsSection.Vault, _) => Vault(),
            (SettingsSection.PeopleOrganization, SettingsSubsection.Categories) => OrganizationCategories(),
            (SettingsSection.PeopleOrganization, SettingsSubsection.Tags) => OrganizationTags(),
            (SettingsSection.PeopleOrganization, _) => PeopleOrganization(),
            (SettingsSection.Activity, _) => Activity(_vm.Activity),
            (SettingsSection.Language, _) => Language(),
            (SettingsSection.Trash, _) => Trash(_vm.Trash),
            (SettingsSection.About, _) => About(),
            _ => Appearance(),
        };
        _page.Children.Add(section);

        var targetOffset = preserveScroll ? previousOffset : 0;
        _content.ChangeView(null, targetOffset, null, true);
        _content.DispatcherQueue.TryEnqueue(() => _content.ChangeView(null, targetOffset, null, true));
        _renderedSection = _vm.ActiveSection;
        _renderedSubsection = _vm.ActiveSubsection;
        _hasRendered = true;
    }

    private UIElement Appearance()
    {
        var tabs = UI.Wrap(6);
        foreach (var (id, key, fallback) in new[]
        {
            ("theme", "Settings.Appearance.Theme", "Theme"),
            ("typography", "Settings.Presentation.Fonts", "Font"),
        })
        {
            var tab = id;
            tabs.Children.Add(UI.Chip(UI.T(key, fallback), _appearanceTab == tab, () =>
            {
                if (_appearanceTab == tab)
                {
                    return;
                }

                _appearanceTab = tab;
                Render();
            }));
        }

        UIElement section = _appearanceTab switch
        {
            "typography" => AppearanceDefinitionSection(PresentationSlots.Typography),
            _ => AppearanceDefinitionSection(PresentationSlots.Theme),
        };

        return UI.V(12, tabs, section);
    }

    private FrameworkElement AppearanceDefinitionSection(string slot)
    {
        var descriptor = PresentationSlots.Get(slot);
        var selected = Services.Presentation.Resolve(slot, PresentationContext.Global).Definition;
        var wrap = new VariableWrap(10);
        foreach (var definition in Services.Presentation.DefinitionsOf(descriptor.Kind!)
            .Where(d => d.Ref == selected || BuiltInPresentationCatalog.IsPrimary(d.Ref))
            .Where(d => d.Plan is not EffectPlan effect || effect.Supports(slot)))
        {
            var reference = definition.Ref;
            var isSelected = selected == reference;
            var displayName = SurfaceText.PresentationName(definition);
            var displayDescription = SurfaceText.PresentationDescription(definition);
            var descriptionText = displayDescription is { Length: > 0 } description
                ? UI.WrappedText(description, "caption")
                : null;
            var labels = UI.V(2,
                UI.H(4,
                    UI.Text(displayName, "control", maxLines: 1).Tip(displayName),
                    isSelected ? UI.Badge(UI.T("Common.Selected", "Selected"), "accent") : null),
                descriptionText,
                UI.H(4,
                    definition.Origin == PackOrigin.User
                        ? UI.Badge(UI.T("Customize.FromPack", "Pack"))
                        : null,
                    AppearancePerformanceLabel(definition) is { } performance
                        ? UI.Badge(performance)
                        : null,
                    definition.Diagnostics.Count > 0
                        ? UI.Badge(UI.T("Customize.Warnings", "Warnings"), "warning")
                        : null));

            var card = new Button
            {
                Content = UI.Surface(
                    UI.V(8, AppearanceDefinitionPreview(slot, definition), labels),
                    Material.Raised,
                    12,
                    10),
                Width = 176,
                MinHeight = slot == PresentationSlots.ControlSkin ? 270 : 190,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = ThemeRuntime.Current.Brush("transparent"),
                BorderBrush = ThemeRuntime.Current.Brush(isSelected ? "borderSelected" : "borderSubtle"),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, displayName);
            ToolTipService.SetToolTip(
                card,
                string.IsNullOrWhiteSpace(displayDescription)
                    ? displayName
                    : $"{displayName}\n{displayDescription}");
            card.Click += (_, _) =>
            {
                if (isSelected)
                {
                    return;
                }

                Services.RunUserAction(
                    ApplyAppearanceDefinitionAsync(slot, reference),
                    "SettingsSurface.ApplyAppearanceDefinitionAsync",
                    UI.T("Customize.ApplyFailed", "Could not apply. Nothing was changed."));
            };
            wrap.Children.Add(card);
        }

        var title = descriptor.Id == PresentationSlots.Typography
            ? UI.T("Settings.Presentation.Fonts", "Font")
            : UI.T(descriptor.LabelKey, descriptor.LabelFallback);
        var sectionDescription = descriptor.Id == PresentationSlots.Theme
            ? UI.T("Settings.Theme.Desc", "Colour, material and shape.")
            : descriptor.Id == PresentationSlots.Typography
                ? UI.T("Settings.Presentation.Fonts.Desc", "Choose the font used by naut.")
                : descriptor.DescriptionFallback;
        return UI.Section(title, sectionDescription, wrap);
    }

    private FrameworkElement AppearanceDefinitionPreview(string slot, CompiledDefinition definition)
    {
        var theme = ThemeRuntime.Current;
        if (definition.Plan is TypographyPlan typography)
        {
            TextBlock Sample(string text, TypographyFace face, double size) => new()
            {
                Text = text, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(LocalPresentationFonts.Current.Register(face)),
                FontSize = size, Foreground = theme.Brush("textPrimary"), TextWrapping = TextWrapping.Wrap,
            };
            return UI.V(3, Sample("Aa · 123", typography.Heading, 20), Sample("日本語 · 한국어 · 中文", typography.Body, 14),
                Sample("0x20  01:23", typography.Mono, 12)).Margin(8);
        }
        if (definition.Plan is ControlSkinPlan skin)
            return UI.ControlSheet(skin);
        if (definition.Plan is EffectPlan effect)
            return PresentationEffects.Preview(effect, 82);
        if (slot == PresentationSlots.Theme)
            return ThemePreviewView.Create(definition.PlanAs<ThemePlan>(), 82);

        var accent = definition.PreviewAccent is { } previewAccent
            && ThemeColorText.TryParse(previewAccent, out var parsed)
                ? UI.Solid(parsed)
                : theme.Brush("surface2");
        var host = new Grid
        {
            Height = 82,
            Background = accent,
        };

        FrameworkElement content = slot switch
        {
            _ => UI.Text(SurfaceText.PresentationName(definition), "caption", maxLines: 1),
        };
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        host.Children.Add(content);
        return new Border { Child = host, CornerRadius = new CornerRadius(8) };
    }

    private static string? AppearancePerformanceLabel(CompiledDefinition definition) =>
        definition.Performance.Tier switch
        {
            "static" => UI.T("Customize.Performance.Static", "Static"),
            "heavy" => UI.T("Customize.Performance.Demanding", "More demanding"),
            _ => null,
        };

    private async Task ApplyAppearanceDefinitionAsync(string slot, DefinitionRef reference)
    {
        if (Services.Presentation.Resolve(slot, PresentationContext.Global).Definition == reference)
        {
            return;
        }

        var session = Services.Presentation.BeginPreview(PresentationContext.Global);
        session.Select(slot, ScopeKind.Global, reference);
        var result = await session.ApplyAsync().ConfigureAwait(true);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                result.Message ?? UI.T("Customize.ApplyFailed", "Could not apply. Nothing was changed."));
        }

        Services.Toast(UI.T("Customize.Applied", "Customization applied"), "success");
    }

    private UIElement SystemHealthSummary()
    {
        var metrics = new VariableWrap(
            6,
            UI.Badge(UI.F("Settings.Health.Profiles", "{0} Profiles", _vm.TotalProfilesCount)),
            UI.Badge(UI.F("Settings.Health.Media", "{0} media", _vm.ActiveMediaCount)),
            UI.Badge(UI.F("Settings.Health.PendingFaces", "{0} faces to review", _vm.UnresolvedFaceCount),
                _vm.UnresolvedFaceCount > 0 ? "warning" : "success"));

        return UI.Section(
            UI.T("Settings.SystemHealth.Title", "System health"),
            UI.T(
                "Settings.SystemHealth.Desc",
                "Vault integrity, storage and repair diagnostics. Open details only when you need them."),
            metrics,
            UI.Button(
                UI.T("Settings.VaultHealth.Open", "Open Vault health"),
                () => Services.Navigation.Navigate(
                    new SettingsRoute(SettingsSection.Vault, SettingsSubsection.VaultHealth)),
                ButtonKind.Secondary,
                "icon.status.health"));
    }

    private UIElement Language()
    {
        var theme = ThemeRuntime.Current;
        var choices = UI.V(6);
        choices.HorizontalAlignment = HorizontalAlignment.Stretch;
        foreach (var language in _vm.AvailableLanguages)
        {
            var selected = _vm.IsLanguageSelected(language.Code);
            var flag = BrandAssets.Image(
                $"ms-appx:///Assets/Flags/{language.FlagAssetName}.png",
                28,
                language.NativeName);
            flag.Height = 18;

            var check = UI.Text(selected ? "✓" : string.Empty, "control", selected ? "accent" : null);
            check.Width = 20;
            check.TextAlignment = TextAlignment.Center;

            var content = UI.Grid(
                "auto",
                "auto,*,auto",
                flag.At(0, 0),
                UI.Text(language.NativeName, "control").At(0, 1),
                check.At(0, 2));
            content.ColumnSpacing = 10;

            var row = new Button
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinHeight = 44,
                Padding = new Thickness(12, 8, 12, 8),
                CornerRadius = new CornerRadius(theme.Tokens.Number("radiusControl", 8)),
                Background = theme.Brush(selected ? "surface3" : "surface2"),
                BorderBrush = theme.Brush(selected ? "borderSelected" : "borderSubtle"),
                BorderThickness = new Thickness(2),
                Command = _vm.SelectLanguageCommand,
                CommandParameter = language.Code,
                KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, language.NativeName);
            choices.Children.Add(row);
        }

        return UI.V(
            12,
            UI.Text(
                UI.T("Settings.Language.Desc", "Choose the language used by naut."),
                "body-muted"),
            choices);
    }

    private UIElement PeopleOrganization() => UI.V(
        16,
        UI.Section(
            UI.T("Settings.People", "People & faces"),
            UI.F("Settings.People.Pending", "{0} faces are waiting for review.", _vm.UnresolvedFaceCount),
            UI.Button(
                UI.T("Settings.OpenFaceReview", "Review faces"),
                null,
                ButtonKind.Primary,
                "icon.profile.face",
                _vm.NavigateToFaceReviewCommand)),
        UI.Section(
            UI.T("Settings.Organization.Title", "Organization"),
            UI.T(
                "Settings.Organization.Desc",
                "Create, rename, merge and safely delete categories and tags. Deleting never removes Profiles or media."),
            new VariableWrap(
                8,
                UI.Button(
                    UI.T("Settings.Categories.Title", "Categories"),
                    () => Services.Navigation.Navigate(
                        new SettingsRoute(SettingsSection.PeopleOrganization, SettingsSubsection.Categories)),
                    ButtonKind.Secondary, "icon.profile.category"),
                UI.Button(
                    UI.T("Settings.Tags.Title", "Tags"),
                    () => Services.Navigation.Navigate(
                        new SettingsRoute(SettingsSection.PeopleOrganization, SettingsSubsection.Tags)),
                    ButtonKind.Secondary, "icon.profile.tag"))));

    private UIElement OrganizationCategories()
    {
        var list = UI.V(8);
        var message = UI.WrappedText(string.Empty, "caption");
        var warning = UI.WrappedText(string.Empty, "caption", "danger");
        var addName = new TextBox
        {
            PlaceholderText = UI.T("Settings.Categories.New", "New category name"),
            Text = _vm.NewCategoryName,
            MinWidth = 260
        };
        addName.TextChanged += (_, _) => _vm.NewCategoryName = addName.Text;

        void Fill()
        {
            list.Children.Clear();
            foreach (var item in _vm.Categories)
            {
                var edit = new TextBox { Text = item.Name, MinWidth = 220 };
                item.EditName = item.Name;
                edit.TextChanged += (_, _) => item.EditName = edit.Text;
                var row = UI.Grid("auto", "*,auto",
                    UI.V(2,
                        edit,
                        UI.Text(UI.F("Settings.Organization.Usage", "Used by {0} Profiles", item.UsageCount), "caption")).At(0, 0),
                    UI.H(6,
                        UI.Button(UI.T("Common.Save", "Save"), null, ButtonKind.Ghost, "icon.action.edit", command: _vm.CommitRenameCategoryCommand, parameter: item),
                        UI.Button(UI.T("Common.Delete", "Delete"), null, ButtonKind.Destructive, "icon.action.delete", command: _vm.DeleteCategoryCommand, parameter: item)).At(0, 1));
                list.Children.Add(UI.Surface(row, Material.Grounded, 10, 10));
            }
        }

        void FillStatus()
        {
            message.Text = _vm.OrganizationMessage is null
                ? string.Empty
                : UI.T("Settings.Organization.Saved", "Organization changes saved.");
            message.Visibility = string.IsNullOrWhiteSpace(message.Text) ? Visibility.Collapsed : Visibility.Visible;

            warning.Text = _vm.OrganizationWarningMessage is null
                ? string.Empty
                : UI.T("Settings.Organization.Failed", "The organization change could not be saved. Resolve the conflict and try again.");
            warning.Visibility = string.IsNullOrWhiteSpace(warning.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        Fill();
        FillStatus();
        _sectionBag.Add(Observe.Collection(_vm.Categories, Fill));
        _sectionBag.Add(Observe.Props(
            _vm,
            FillStatus,
            nameof(SettingsViewModel.OrganizationMessage),
            nameof(SettingsViewModel.OrganizationWarningMessage)));

        return UI.V(12,
            UI.Button(
                UI.T("Common.Back", "Back"),
                () => Services.Navigation.Navigate(new SettingsRoute(SettingsSection.PeopleOrganization)),
                ButtonKind.Ghost, "icon.action.back"),
            UI.Wrap(6,
                addName,
                UI.Button(UI.T("Common.Add", "Add"), null, ButtonKind.Primary, "icon.action.add", command: _vm.CreateCategoryCommand)),
            message,
            warning,
            list);
    }

    private UIElement OrganizationTags()
    {
        var list = UI.V(8);
        var message = UI.WrappedText(string.Empty, "caption");
        var warning = UI.WrappedText(string.Empty, "caption", "danger");
        var addName = new TextBox
        {
            PlaceholderText = UI.T("Settings.Tags.New", "New tag name (comma to add multiple)"),
            Text = _vm.NewTagName,
            MinWidth = 260
        };
        addName.TextChanged += (_, _) => _vm.NewTagName = addName.Text;
        // K04.2: Enter commits the tag(s).
        addName.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                TaskObserver.Observe(_vm.CreateTagAsync(), "SettingsSurface.CreateTagAsync");
            }
        };

        void Fill()
        {
            list.Children.Clear();
            foreach (var item in _vm.Tags)
            {
                var edit = new TextBox { Text = item.Name, MinWidth = 220 };
                item.EditName = item.Name;
                edit.TextChanged += (_, _) => item.EditName = edit.Text;
                var row = UI.Grid("auto", "*,auto",
                    UI.V(2,
                        edit,
                        UI.Text(UI.F("Settings.Organization.Usage", "Used by {0} Profiles", item.UsageCount), "caption")).At(0, 0),
                    UI.H(6,
                        UI.Button(UI.T("Common.Save", "Save"), null, ButtonKind.Ghost, "icon.action.edit", command: _vm.CommitRenameTagCommand, parameter: item),
                        UI.Button(UI.T("Common.Delete", "Delete"), null, ButtonKind.Destructive, "icon.action.delete", command: _vm.DeleteTagCommand, parameter: item)).At(0, 1));
                list.Children.Add(UI.Surface(row, Material.Grounded, 10, 10));
            }
        }

        void FillStatus()
        {
            message.Text = _vm.OrganizationMessage is null
                ? string.Empty
                : UI.T("Settings.Organization.Saved", "Organization changes saved.");
            message.Visibility = string.IsNullOrWhiteSpace(message.Text) ? Visibility.Collapsed : Visibility.Visible;

            warning.Text = _vm.OrganizationWarningMessage is null
                ? string.Empty
                : UI.T("Settings.Organization.Failed", "The organization change could not be saved. Resolve the conflict and try again.");
            warning.Visibility = string.IsNullOrWhiteSpace(warning.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        Fill();
        FillStatus();
        _sectionBag.Add(Observe.Collection(_vm.Tags, Fill));
        _sectionBag.Add(Observe.Props(
            _vm,
            FillStatus,
            nameof(SettingsViewModel.OrganizationMessage),
            nameof(SettingsViewModel.OrganizationWarningMessage)));

        return UI.V(12,
            UI.Button(
                UI.T("Common.Back", "Back"),
                () => Services.Navigation.Navigate(new SettingsRoute(SettingsSection.PeopleOrganization)),
                ButtonKind.Ghost, "icon.action.back"),
            UI.Wrap(6,
                addName,
                UI.Button(UI.T("Common.Add", "Add"), null, ButtonKind.Primary, "icon.action.add", command: _vm.CreateTagCommand)),
            message,
            warning,
            list);
    }

    private UIElement Vault() => UI.V(
        16,
        SystemHealthSummary(),
        new VaultSettingsPanel(Services, _vm).Build());

    private UIElement About() => SettingsInformationView.Build(_vm, () => Services.RunUserAction(
        InstallFromZipAsync(), "Settings.InstallFromZip", UI.T("Settings.Update.LocalFailed", "The local update could not be installed.")));

    private async Task InstallFromZipAsync()
    {
        var selected = await Services.PickFilesAsync(false, ".zip").ConfigureAwait(true);
        if (selected.Count == 0)
        {
            return;
        }

        var archivePath = selected[0];
        var inspection = await _vm.InspectLocalUpdateAsync(archivePath).ConfigureAwait(true);
        if (!inspection.IsAccepted)
        {
            Services.Toast(
                inspection.SafeError ?? UI.T("Settings.Update.LocalInvalid", "The selected ZIP is not an installable naut update."),
                "warning");
            return;
        }

        var confirmed = await Services.ConfirmAsync(
            UI.T("Settings.Update.LocalConfirmTitle", "Install local update?"),
            UI.F(
                "Settings.Update.LocalConfirmBody",
                "Install naut {0} ({1}) from the selected ZIP? The application will close and restart. Your Vault will not be modified.",
                inspection.ProductVersion ?? "?",
                inspection.RuntimeIdentifier ?? "?"),
            UI.T("Settings.Update.LocalConfirm", "Install update")).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(inspection.PayloadSha256))
        {
            Services.Toast(
                UI.T("Settings.Update.LocalInvalid", "The selected ZIP is not an installable naut update."),
                "warning");
            return;
        }

        var result = await _vm.InstallLocalUpdateAsync(
            archivePath,
            inspection.PayloadSha256,
            userConfirmed: true).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                result.SafeError ?? UI.T("Settings.Update.LocalFailed", "The local update could not be installed."));
        }
    }

    private UIElement Activity(ActivityViewModel activity) => SettingsActivityView.Build(activity, _sectionBag);

    private UIElement Trash(TrashViewModel trash)
    {
        var list = UI.V(6);
        var confirm = UI.V(8);
        var feedback = UI.WrappedText(string.Empty, "caption", "accent");
        var loadMore = UI.Button(
            UI.T("Common.LoadMore", "Load more"),
            null,
            ButtonKind.Ghost,
            command: trash.LoadMoreCommand);

        void Fill()
        {
            list.Children.Clear();
            foreach (var item in trash.Items)
            {
                var isProfile = string.Equals(
                    item.EntityType,
                    "Profile",
                    StringComparison.OrdinalIgnoreCase);
                var typeLabel = isProfile
                    ? UI.T("Settings.Trash.Profile", "Profile")
                    : UI.T("Settings.Trash.Media", "Media");
                string retentionText;
                if (isProfile)
                {
                    var permanentDeletionAt =
                        ProfileTrashRetentionPolicy.ProfilePermanentDeletionAt(item.TrashedAtUtc);
                    var remaining = permanentDeletionAt - DateTimeOffset.UtcNow;
                    var remainingDays = Math.Max(1, (int)Math.Ceiling(remaining.TotalDays));
                    retentionText = remaining <= TimeSpan.Zero
                        ? UI.T("Trash.Retention.Due", "Scheduled for automatic permanent deletion.")
                        : UI.F(
                            "Trash.Retention.Remaining",
                            "Deletes automatically in {0} day(s).",
                            remainingDays);
                }
                else
                {
                    retentionText = UI.T(
                        "Trash.Media.Retention",
                        "Media stays in Trash until you restore or permanently delete it.");
                }

                var restoreInProgress = item.State is TrashEntryState.RestoreExecuting
                    or TrashEntryState.RestoreFinalizing;
                if (restoreInProgress)
                {
                    retentionText = UI.T(
                        "Trash.Restore.Pending",
                        "Restore is still incomplete. Try again; naut will also continue it safely the next time it starts.");
                }

                var restoreButton = UI.Button(
                    restoreInProgress
                        ? UI.T("Trash.Restore.Retry", "Retry restore")
                        : UI.T("Trash.Restore", "Restore"),
                    null,
                    command: trash.RestoreItemCommand,
                    parameter: item);
                var deleteButton = UI.Button(
                    UI.T("Trash.Delete", "Delete permanently"),
                    null,
                    ButtonKind.Destructive,
                    command: trash.PurgeItemCommand,
                    parameter: item);
                deleteButton.IsEnabled = !restoreInProgress;

                var itemActions = UI.Wrap(6, restoreButton, deleteButton);
                itemActions.HorizontalAlignment = HorizontalAlignment.Right;
                var itemDetails = UI.V(2,
                    UI.Text(item.DisplayName, "body-strong", maxLines: 1),
                    UI.Text($"{typeLabel} · {item.TrashedAtUtc.ToLocalTime():g}", "caption"),
                    UI.WrappedText(retentionText, "caption", "textMuted"));
                list.Children.Add(UI.Surface(
                    new ResponsiveForm(280, 8, itemDetails, itemActions),
                    Material.Grounded,
                    10,
                    12));
            }

            confirm.Children.Clear();
            if (trash.IsPurgeConfirmationActive)
            {
                confirm.Children.Add(UI.Surface(
                    UI.V(8,
                        UI.WrappedText(trash.PurgeConfirmationTitle ?? string.Empty, "body-strong"),
                        UI.WrappedText(trash.PurgeConfirmationMessage ?? string.Empty, "body"),
                        UI.Wrap(8,
                            UI.Button(UI.T("Overlay.Cancel", "Cancel"), null, command: trash.CancelPurgeCommand),
                            UI.Button(trash.PurgeConfirmationConfirmLabel, null, ButtonKind.Destructive, command: trash.ConfirmPurgeCommand))),
                    Material.Deep,
                    12,
                    14));
            }

            feedback.Text = trash.HasError
                ? trash.ErrorMessage ?? string.Empty
                : trash.ActionMessage ?? string.Empty;
            feedback.Foreground = ThemeRuntime.Current.Brush(trash.HasError ? "textDanger" : "textAccent");
            feedback.Visibility = string.IsNullOrWhiteSpace(feedback.Text) ? Visibility.Collapsed : Visibility.Visible;
            loadMore.Visibility = trash.HasMoreItems ? Visibility.Visible : Visibility.Collapsed;
        }

        _sectionBag.Add(Observe.Collection(trash.Items, Fill));
        _sectionBag.Add(Observe.Props(trash, Fill));
        Fill();

        return UI.V(12,
            UI.WrappedText(
                UI.F(
                    "Trash.Desc",
                    "Items here can be restored. Permanent deletion asks first.",
                    ProfileTrashRetentionPolicy.ProfileRetentionDays),
                "body-muted"),
            feedback,
            confirm,
            list,
            loadMore,
            UI.Button(UI.T("Trash.Empty", "Empty Trash"), null, ButtonKind.Destructive, command: trash.EmptyTrashCommand));
    }

    private UIElement Health(LibraryHealthViewModel health)
    {
        var findings = UI.V(8);
        var status = UI.Text(string.Empty, "section-title");
        var metrics = UI.Text(string.Empty, "body");
        var feedback = UI.WrappedText(string.Empty, "caption", "accent");
        var repair = UI.V(8);

        void Fill()
        {
            status.Text = !health.HasScanResults
                ? UI.T("Settings.Health.NoResults", "Run a check to see current findings.")
                : health.HealthFindingCount == 0
                ? UI.T("Health.Status.Healthy", "Healthy")
                : UI.T("Health.Status.Attention", "Needs attention");
            metrics.Text = UI.F(
                "Health.Metrics",
                "{0} media · {1} catalog · {2} / {3} cache · {4} trash",
                health.ManagedMediaBytesFormatted,
                health.DatabaseBytesFormatted,
                health.CacheTotalBytesFormatted,
                health.CacheQuotaBytesFormatted,
                health.TrashBytesFormatted)
                + $"\nMedia assets {health.MediaAssetBytesFormatted} · Temporary/staging {health.TemporaryBytesFormatted}";
            feedback.Text = health.HasError
                ? health.ErrorMessage ?? string.Empty
                : health.ActionFeedbackMessage ?? string.Empty;
            feedback.Foreground = ThemeRuntime.Current.Brush(health.HasError ? "textDanger" : "textAccent");
            feedback.Visibility = string.IsNullOrWhiteSpace(feedback.Text) ? Visibility.Collapsed : Visibility.Visible;

            findings.Children.Clear();
            foreach (var group in health.FindingGroups)
            {
                foreach (var finding in group.Findings)
                {
                    findings.Children.Add(UI.Grid("auto", "*,auto",
                        UI.V(3,
                            UI.Text(finding.Code, "micro", "textMuted"),
                            UI.WrappedText(finding.Summary, "body")).At(0, 0),
                        finding.RepairAvailable
                            ? UI.Button(UI.T("Health.Repair", "Repair…"), () =>
                            {
                                health.SelectFindingCommand.Execute(finding);
                                health.PrepareRepairCommand.Execute(null);
                            }).At(0, 1)
                            : null));
                }
            }

            repair.Children.Clear();
            if (health.HasPendingRepair)
            {
                repair.Children.Add(UI.Surface(
                    UI.V(8,
                        UI.WrappedText(health.RepairConfirmationText, "body"),
                        UI.H(8,
                            UI.Button(UI.T("Overlay.Cancel", "Cancel"), null, command: health.CancelRepairCommand),
                            UI.Button(UI.T("Health.Confirm", "Repair"), null, ButtonKind.Primary, command: health.ConfirmRepairCommand))),
                    Material.Deep,
                    12,
                    14));
            }
        }

        _sectionBag.Add(Observe.Props(health, Fill));
        _sectionBag.Add(Observe.Collection(health.FindingGroups, Fill));
        Fill();

        return UI.V(12,
            status,
            metrics,
            feedback,
            UI.H(8,
                UI.Button(UI.T("Health.Check", "Check Vault"), null, ButtonKind.Primary, command: health.RunHealthCheckCommand),
                UI.Button(UI.T("Health.Deep", "Deep check"), null, command: health.RunDeepHealthCheckCommand),
                UI.Button(UI.T("Health.ClearCache", "Clear cache"), null, ButtonKind.Ghost, command: health.ClearCacheCommand),
                UI.Button(UI.T("Settings.Health.ClearResults", "Clear results"), null, ButtonKind.Ghost, command: health.ClearResultsCommand)),
            UI.Text(UI.T("Health.CacheNote", "The cache only holds regenerable previews. Clearing it never touches your media."), "caption"),
            repair,
            findings,
            PresentationHealth());
    }

    private FrameworkElement PresentationHealth()
    {
        var diagnostics = UI.V(6);
        foreach (var pack in Services.Presentation.Packs)
            foreach (var diagnostic in pack.Diagnostics)
                diagnostics.Children.Add(UI.WrappedText($"{pack.Manifest.Name} · {diagnostic.Code} · {diagnostic.Subject}: {diagnostic.Detail}",
                    "caption", diagnostic.IsError ? "danger" : "warning"));
        if (diagnostics.Children.Count == 0) diagnostics.Children.Add(UI.Text(UI.T("Settings.Presentation.Healthy", "Presentation packs are ready."), "caption"));
        return UI.Section(UI.T("Settings.Presentation.Title", "Presentation"), null, diagnostics);
    }

    public override void Dispose()
    {
        _sectionBag.Dispose();
        base.Dispose();
    }
}
