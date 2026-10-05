using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Shell;
using Neuterradise.App.Presentation;

namespace Neuterradise.App.Ui;

/// <summary>Explicit first-run folder selection before any Vault bootstrap or mutation.</summary>
public sealed class VaultWelcomeView : Grid
{
    public VaultWelcomeView(
        Func<Task<string?>> browseFolder,
        Func<string, Task<string?>> useFolder,
        Action exit)
    {
        Background = ThemeRuntime.Current.Brush("canvas");
        Children.Add(new BackdropView { Plan = BackdropPlan.Still(), IsSurfaceActive = false });

        var theme = ThemeRuntime.Current;
        var location = UI.Input(UI.T("Startup.Vault.Location", "Choose a Vault folder"));
        location.IsReadOnly = true;
        location.Foreground = theme.Brush("textPrimary");
        location.Resources["TextControlForeground"] = theme.Brush("textPrimary");
        location.Resources["TextControlForegroundPointerOver"] = theme.Brush("textPrimary");
        location.Resources["TextControlForegroundFocused"] = theme.Brush("textPrimary");
        location.Resources["TextControlPlaceholderForeground"] = theme.Brush("textMuted");

        var error = UI.WrappedText(string.Empty, "caption", "danger");
        error.Visibility = Visibility.Collapsed;
        var busy = false;
        Button? use = null;
        Button? browse = null;

        void Update()
        {
            var hasLocation = !string.IsNullOrWhiteSpace(location.Text);
            if (use is not null)
            {
                use.IsEnabled = !busy && hasLocation;
            }
        }

        async Task RunAsync(Func<Task<string?>> action)
        {
            if (busy)
            {
                return;
            }

            busy = true;
            error.Visibility = Visibility.Collapsed;
            browse!.IsEnabled = false;
            Update();
            try
            {
                var message = await action();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    error.Text = message;
                    error.Visibility = Visibility.Visible;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                error.Text = UI.T(
                    "Startup.Vault.ActionFailed",
                    "The Vault could not be opened. Check the location and try again.") + " " + exception.Message;
                error.Visibility = Visibility.Visible;
            }
            finally
            {
                busy = false;
                browse!.IsEnabled = true;
                Update();
            }
        }

        browse = UI.Button(
            UI.T("Startup.Vault.Browse", "Browse"),
            () => TaskObserver.Observe(
                RunAsync(async () =>
                {
                    var selected = await browseFolder();
                    if (!string.IsNullOrWhiteSpace(selected))
                    {
                        location.Text = selected;
                    }

                    Update();
                    return null;
                }),
                "VaultWelcome.Browse"),
            ButtonKind.Secondary,
            "icon.action.open-folder");

        use = UI.Button(
            UI.T("Startup.Vault.UseFolder", "Use this folder"),
            () => TaskObserver.Observe(
                RunAsync(() => useFolder(location.Text)),
                "VaultWelcome.UseFolder"),
            ButtonKind.Primary);
        use.HorizontalAlignment = HorizontalAlignment.Stretch;

        var locationRow = UI.Grid("auto", "*,auto", location.At(0, 0), browse.At(0, 1));
        locationRow.ColumnSpacing = 8;

        var content = UI.V(
            18,
            BrandAssets.Lockup(54, 30, animateMark: false),
            UI.V(
                8,
                UI.WrappedText(
                    UI.T("Startup.Vault.WelcomeTitle", "A home for your collection"),
                    "display",
                    "textPrimary"),
                UI.WrappedText(
                    UI.T(
                        "Startup.Vault.WelcomeBody",
                        "Keep your photos, videos and 3D models together in a Vault on your computer."),
                    "body",
                    "textSecondary")),
            UI.V(
                6,
                UI.Text(UI.T("Startup.Vault.LocationLabel", "Where to keep it"), "control"),
                locationRow,
                UI.WrappedText(
                    UI.T(
                        "Startup.Vault.LocationHint",
                        "Choose an empty folder for a new Vault, or select an existing naut Vault."),
                    "caption",
                    "textSecondary")),
            UI.WrappedText(
                UI.T(
                    "Startup.Vault.LocalNote",
                    "Your originals stay where they are. Imported media is copied into this Vault."),
                "caption",
                "textSecondary"),
            error,
            use,
            UI.Button(UI.T("Startup.Exit", "Exit"), exit, ButtonKind.Ghost));

        var panel = UI.Surface(
            content,
            Material.Deep,
            ThemeRuntime.Current.Tokens.Number("radiusHero", 20),
            28);
        panel.MaxWidth = 600;
        panel.Margin = new Thickness(24);
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(UI.Scroll(panel));
        Update();
    }
}
