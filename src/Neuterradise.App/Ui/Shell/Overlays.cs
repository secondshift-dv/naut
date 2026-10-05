using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;
using Neuterradise.App.Shell;
using Windows.Storage.Pickers;

namespace Neuterradise.App.Ui;

/// <summary>Compact focused dialogs : fit 800 × 600, never substitute pages.</summary>
public sealed class DialogService(ProductRoot root)
{
    private readonly List<DialogEntry> _openDialogs = [];

    private sealed record DialogEntry(FrameworkElement View, Action? OnDismiss);

    public bool HasOpenDialog => _openDialogs.Count > 0;
    public event Action? Changed;

    public Task<bool> ConfirmAsync(string title, string message, string confirm, bool destructive, string? cancel = null)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmButton = UI.Button(confirm, null, destructive ? ButtonKind.Destructive : ButtonKind.Primary);
        var cancelButton = UI.Button(cancel ?? UI.T("Overlay.Cancel", "Cancel"));
        var body = UI.V(12,
            UI.Text(title, "section-title"),
            UI.Text(message, "body", maxLines: 12),
            destructive ? UI.WrappedText(UI.T("Dialog.CannotUndo", "This cannot be undone."), "caption", "danger") : null,
            UI.Wrap(6, cancelButton, confirmButton).Align(HorizontalAlignment.Right));
        var dialog = Show(body, 480, () => completion.TrySetResult(false));
        confirmButton.Click += (_, _) => Close(dialog, () => completion.TrySetResult(true));
        cancelButton.Click += (_, _) => Close(dialog, () => completion.TrySetResult(false));
        return completion.Task;
    }

    public Task<string?> PromptAsync(string title, string label, string initial, string confirm)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = UI.Input(label, initial);
        var ok = UI.Button(confirm, null, ButtonKind.Primary);
        var cancel = UI.Button(UI.T("Overlay.Cancel", "Cancel"));
        var dialog = Show(
            UI.V(12, UI.Text(title, "section-title"), input, UI.Wrap(6, cancel, ok).Align(HorizontalAlignment.Right)),
            440,
            () => completion.TrySetResult(null));
        ok.Click += (_, _) => Close(dialog, () => completion.TrySetResult(input.Text));
        cancel.Click += (_, _) => Close(dialog, () => completion.TrySetResult(null));
        return completion.Task;
    }

    public FrameworkElement Show(UIElement content, double width, Action? onDismiss = null, bool dismissOnScrim = false)
    {
        var theme = ThemeRuntime.Current;
        var scrim = new Grid { Background = theme.Brush("scrimMedium") };
        var scroll = UI.Scroll(content);
        scroll.VerticalAlignment = VerticalAlignment.Top;
        var card = UI.Surface(scroll, Material.Deep, theme.Tokens.Number("radiusSurface", 16), 14);
        card.Width = width;
        card.MaxWidth = width;
        card.MaxHeight = 520;
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        card.Margin = new Thickness(12);
        scrim.SizeChanged += (_, args) =>
        {
            card.MaxWidth = Math.Max(0, Math.Min(width, args.NewSize.Width - 24));
            card.Width = card.MaxWidth;
            card.MaxHeight = Math.Max(0, Math.Min(520, args.NewSize.Height - 24));
            scroll.MaxHeight = Math.Max(0, card.MaxHeight - card.Padding.Top - card.Padding.Bottom - card.BorderThickness.Top - card.BorderThickness.Bottom);
        };
        card.Tapped += (_, e) => e.Handled = true;
        if (dismissOnScrim && onDismiss is not null)
        {
            scrim.Tapped += (_, e) =>
            {
                e.Handled = true;
                Remove(scrim);
                onDismiss();
            };
        }
        scrim.Children.Add(card);
        root.OverlayLayer.Children.Add(scrim);
        root.OverlayLayer.Visibility = Visibility.Visible;
        _openDialogs.Add(new DialogEntry(scrim, onDismiss));
        Changed?.Invoke();
        return scrim;
    }

    public void Close(FrameworkElement dialog, Action? after = null)
    {
        Remove(dialog);
        after?.Invoke();
    }

    /// <summary>
    /// Handles shell Escape before overlays/customization. A modal without a structural-dismiss callback
    /// intentionally consumes Escape but remains open; callers that can be cancelled supply the callback.
    /// </summary>
    public bool TryDismissTop()
    {
        if (_openDialogs.Count == 0)
        {
            return false;
        }

        var entry = _openDialogs[^1];
        if (entry.OnDismiss is null)
        {
            return true;
        }

        Remove(entry.View);
        entry.OnDismiss();
        return true;
    }

    private void Remove(FrameworkElement dialog)
    {
        for (var index = _openDialogs.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(_openDialogs[index].View, dialog))
            {
                _openDialogs.RemoveAt(index);
                break;
            }
        }

        root.OverlayLayer.Children.Remove(dialog);
        if (root.OverlayLayer.Children.Count == 0)
        {
            root.OverlayLayer.Visibility = Visibility.Collapsed;
        }
        Changed?.Invoke();
    }
}

public sealed class OverlayPresenter(AppServices services, ProductRoot root)
{
    private OverlayRequest? _shown;
    private FrameworkElement? _dialog;

    public void Sync()
    {
        var current = services.Overlay.Current;
        if (ReferenceEquals(current, _shown))
        {
            return;
        }

        if (_dialog is not null)
        {
            root.Dialogs.Close(_dialog);
            _dialog = null;
        }

        _shown = current;
        if (current is null)
        {
            return;
        }

        if (current is ProfileCustomizationOverlayRequest customization)
        {
            _shown = null;
            services.Overlay.CloseTop();
            OpenProfileCustomization(customization);
            return;
        }

        _dialog = root.Dialogs.Show(
            Build(current),
            current is ProfilePickerOverlayRequest ? 520 : 480,
            current.IsDismissableByEscape ? () => services.Overlay.RequestEscapeClose() : null);
    }

    private void OpenProfileCustomization(ProfileCustomizationOverlayRequest request)
    {
        var state = request.ProfileState;
        var section = request.InitialSection switch
        {
            ProfileCustomizationSection.ProfileLayout => PresentationSlots.ProfileLayout,
            ProfileCustomizationSection.MediaLayout => PresentationSlots.ProfileMediaLayout,
            ProfileCustomizationSection.ProfileAppearance => PresentationSlots.ProfileCover,
            _ => PresentationSlots.ProfileLayout,
        };
        services.OpenCustomization(CustomizationCategories.Profile, PresentationContext.ForProfile(state), section, request);
    }

    private static string LocalizedPickerTitle(string title) => title switch
    {
        "Reassign Face to Profile" => UI.T("Faces.Picker.ReassignTitle", "Reassign Face to Profile"),
        "Assign Other Profile" => UI.T("Faces.Picker.AssignTitle", "Assign Other Profile"),
        "Find related profiles" => UI.T("Gallery.RelatedPicker.Title", "Find related profiles"),
        "Select Profile" => UI.T("SurfaceText.Select.Existing.Profile.7B23D2D3", "Choose a profile"),
        _ => title,
    };

    private static string LocalizedPickerPrompt(string prompt) => prompt switch
    {
        "Select a Profile to assign this face detection to:" => UI.T("Faces.Picker.AssignPrompt", "Choose a Profile to assign this face detection to:"),
        "Choose a Profile to assign this face detection to:" => UI.T("Faces.Picker.AssignPrompt", "Choose a Profile to assign this face detection to:"),
        "Choose a Profile. Gallery will show Profiles related to it." => UI.T("Gallery.RelatedPicker.Prompt", "Choose a Profile. Gallery will show Profiles related to it."),
        "Choose a Profile to link:" => UI.T("SurfaceText.Select.Existing.Profile.7B23D2D3", "Choose a Profile"),
        _ => prompt,
    };

    private UIElement Build(OverlayRequest request)
    {
        switch (request)
        {
            case ConfirmationOverlayRequest confirmation:
            {
                var confirm = UI.Button(confirmation.ConfirmLabel, () => services.Overlay.ConfirmTop(), confirmation.IsDestructive ? ButtonKind.Destructive : ButtonKind.Primary);
                var cancel = UI.Button(confirmation.CancelLabel, () => services.Overlay.CloseTop());
                return UI.V(12,
                    UI.Text(confirmation.Title, "section-title"),
                    UI.Text(confirmation.Message, "body", maxLines: 12),
                    confirmation.FormattedDestructiveWarning is { } warning ? UI.WrappedText(warning, "caption", "danger") : null,
                    UI.Wrap(6, cancel, confirm).Align(HorizontalAlignment.Right));
            }

            case ErrorDetailOverlayRequest error:
                return UI.V(12,
                    UI.Text(error.Title, "section-title"),
                    UI.Text(error.Message, "body", maxLines: 10),
                    error.Detail is { } detail ? UI.WrappedText(detail, "caption") : null,
                    UI.Button(UI.T("Overlay.Dismiss", "Dismiss"), () => services.Overlay.CloseTop(), ButtonKind.Primary).Align(HorizontalAlignment.Right));

            case ProfilePickerOverlayRequest picker:
            {
                var list = new StackPanel { Spacing = 4 };
                var search = UI.Input(UI.T("Picker.Search", "Search Profiles"));
                search.Text = picker.InitialSearchText;
                void Fill()
                {
                    list.Children.Clear();
                    foreach (var candidate in picker.Candidates.Where(c => string.IsNullOrWhiteSpace(search.Text) || c.DisplayName.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)).Take(200))
                    {
                        var row = UI.Button(candidate.CategoryName is null ? candidate.DisplayName : $"{candidate.DisplayName} · {candidate.CategoryName}", () =>
                        {
                            services.Overlay.CloseTop();
                            picker.OnProfileSelected(candidate);
                        }, ButtonKind.Ghost);
                        row.HorizontalAlignment = HorizontalAlignment.Stretch;
                        row.HorizontalContentAlignment = HorizontalAlignment.Left;
                        list.Children.Add(row);
                    }
                }

                search.TextChanged += (_, _) => Fill();
                Fill();
                return UI.V(10,
                    UI.Text(LocalizedPickerTitle(picker.Title), "section-title"),
                    UI.Text(LocalizedPickerPrompt(picker.Prompt), "body-muted"),
                    search,
                    UI.Scroll(list, maxHeight: 320),
                    UI.Button(UI.T("Overlay.Cancel", "Cancel"), () => services.Overlay.CloseTop()).Align(HorizontalAlignment.Right));
            }

            case AssociationEditorOverlayRequest associations:
            {
                var list = UI.V(6);
                foreach (var association in associations.Associations)
                {
                    var relation = association.RelationKind switch
                    {
                        "Owner" => UI.T("Profile.Relation.Owner", "Owner"),
                        "Appears In" => UI.T("Profile.Relation.Appears", "Appears In"),
                        "Manual" => UI.T("Profile.Relation.Manual", "Manual"),
                        _ => association.RelationKind,
                    };
                    list.Children.Add(UI.Grid("auto", "*,auto",
                        UI.Text($"{association.DisplayName} · {relation}", "body").At(0, 0),
                        associations.OnRemoveAssociation is null ? null : UI.Button(UI.T("Common.Remove", "Remove"), () =>
                        {
                            services.Overlay.CloseTop();
                            associations.OnRemoveAssociation(association.TargetProfileId);
                        }, ButtonKind.Ghost).At(0, 1)));
                }

                return UI.V(12,
                    UI.Text(associations.Title, "section-title"),
                    UI.Text(associations.SourceProfileDisplayName, "body-muted"),
                    list,
                    UI.Wrap(6,
                        associations.OnAddAssociationRequested is null ? null : UI.Button(UI.T("Common.Add", "Add"), () =>
                        {
                            services.Overlay.CloseTop();
                            associations.OnAddAssociationRequested();
                        }),
                        UI.Button(UI.T("Overlay.Dismiss", "Dismiss"), () => services.Overlay.CloseTop(), ButtonKind.Primary)).Align(HorizontalAlignment.Right));
            }

            default:
                return UI.V(12, UI.Text(request.Title, "section-title"), UI.Button(UI.T("Overlay.Dismiss", "Dismiss"), () => services.Overlay.CloseTop(), ButtonKind.Primary));
        }
    }
}

public static class Pickers
{
    private static void InitializeDesktopPicker(Window window, object picker)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(picker);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("The desktop picker requires an attached application window.");
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    public static async Task<IReadOnlyList<string>> PickFilesAsync(Window window, bool multiple, params string[] extensions)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        InitializeDesktopPicker(window, picker);
        foreach (var extension in extensions.Length == 0 ? ["*"] : extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        if (multiple)
        {
            var files = await picker.PickMultipleFilesAsync();
            return files is null ? [] : [.. files.Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p))];
        }

        var file = await picker.PickSingleFileAsync();
        return file is null || string.IsNullOrWhiteSpace(file.Path) ? [] : [file.Path];
    }

    public static async Task<string?> PickFolderAsync(Window window)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        InitializeDesktopPicker(window, picker);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static async Task<string?> PickSaveFileAsync(Window window, string suggestedName, string extension, string label)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedName, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        InitializeDesktopPicker(window, picker);
        picker.FileTypeChoices.Add(label, [extension]);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}

public static class StartupScreens
{
    public static FrameworkElement Splash(string status)
    {
        var theme = ThemeRuntime.Current;
        var root = new Grid { Background = theme.Brush("canvas") };
        root.Children.Add(new BackdropView { Plan = BackdropPlan.Still(), IsSurfaceActive = false });
        root.Children.Add(UI.V(16,
            BrandAssets.Lockup(64, 36, animateMark: true).Align(HorizontalAlignment.Center),
            UI.Text(status, "caption", "textSecondary", 2).Align(HorizontalAlignment.Center),
            new ProgressBar { IsIndeterminate = true, Width = 128, Height = 3 }
                .Align(HorizontalAlignment.Center)).Align(HorizontalAlignment.Center, VerticalAlignment.Center));
        return root;
    }

    public static FrameworkElement Failure(string title, string message, string nextStep, string code, Action? retry, Action openDiagnostics, Action exit, Action? primary = null, string? primaryLabel = null)
    {
        var theme = ThemeRuntime.Current;
        var panel = UI.Surface(UI.V(12,
            UI.H(10, new IconView("icon.status.warning", 24, "warning"), UI.Text(title, "page-title")),
            UI.Text(message, "body"),
            UI.Text(nextStep, "body-muted"),
            UI.Text(code, "caption"),
            UI.Wrap(6,
                UI.Button(UI.T("Startup.OpenDiagnostics", "Open diagnostics"), openDiagnostics),
                UI.Button(UI.T("Startup.Exit", "Exit"), exit),
                retry is null ? null : UI.Button(UI.T("Startup.Retry", "Try again"), retry, primary is null ? ButtonKind.Primary : ButtonKind.Secondary),
                primary is null ? null : UI.Button(primaryLabel ?? UI.T("Common.Ok", "OK"), primary, ButtonKind.Primary)).Align(HorizontalAlignment.Right)), Material.Deep, 16, 28);
        panel.MaxWidth = 560;
        panel.Margin = new Thickness(24);
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Center;
        var root = new Grid { Background = theme.Brush("canvas") };
        root.Children.Add(new BackdropView { Plan = BackdropPlan.Still() });
        root.Children.Add(panel);
        return root;
    }
}
