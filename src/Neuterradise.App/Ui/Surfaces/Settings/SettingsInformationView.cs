using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Presentation;
using Neuterradise.App.Settings;

namespace Neuterradise.App.Ui;

internal static class SettingsInformationView
{
    private static HyperlinkButton Link(string label, Uri uri) => new()
    {
        Content = UI.Text(label, "caption", "accent"),
        NavigateUri = uri,
        Padding = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static FrameworkElement InformationRow(string label, string value) => UI.Grid("auto", "160,*",
        UI.WrappedText(label, "caption", "textSecondary").Margin(0, 6, 12, 6).At(0, 0),
        UI.WrappedText(value, "body").Margin(0, 6, 0, 6).At(0, 1));

    public static FrameworkElement Build(SettingsViewModel vm, Disposables subscriptions, Action installFromZip)
    {
        var technicalDetails = UI.Surface(UI.V(0,
            InformationRow(UI.T("Settings.About.Runtime", "Runtime"), vm.RuntimeInfo),
            InformationRow(UI.T("Settings.About.Database", "Catalog schema"), vm.CatalogSchemaVersion),
            InformationRow(UI.T("Settings.About.PresentationContract", "Presentation contract"), PresentationContract.Version.ToString()),
            InformationRow(UI.T("Settings.About.WorkerProtocol", "Profiling protocol"), vm.ProfilingWorkerProtocolVersion),
            InformationRow(UI.T("Settings.About.FaceModels", "Face models"), vm.FaceModelInfo),
            InformationRow(UI.T("Settings.About.Rendering", "Renderer"), "Uno Platform · Skia Desktop · Win32")), Material.Grounded, 12, 10);
        technicalDetails.Visibility = Visibility.Collapsed;

        var components = UI.V(8);
        foreach (var component in vm.ThirdPartyComponents)
        {
            components.Children.Add(UI.Grid("auto", "*,auto,auto",
                UI.Text(component.Name, "body-strong").At(0, 0),
                Link(UI.T("Settings.About.Source", "Source"), component.Source).Margin(12, 0, 12, 0).At(0, 1),
                Link(component.License, component.LicensePage).At(0, 2)));
            components.Children.Add(UI.Divider());
        }
        if (vm.ThirdPartyComponents.Count == 0)
            components.Children.Add(UI.WrappedText(UI.T("Settings.About.NoticesUnavailable", "Third-party notices are unavailable in this deployment."), "caption"));
        var noticesPanel = UI.Surface(components, Material.Grounded, 12, 10);
        noticesPanel.Visibility = Visibility.Collapsed;

        var releaseLine = vm.PublishedRelease is { } release
            ? UI.F("Settings.About.Released", "Released {0}", release.PublishedAt.ToLocalTime().ToString("d MMM yyyy"))
            : UI.T("Settings.About.ReleaseUnavailable", "Release date unavailable");
        var product = UI.V(8,
            BrandAssets.Image(BrandAssets.PrimaryMarkUri, 72, "naut").Align(HorizontalAlignment.Center),
            UI.Text(vm.AppTitle, "page-title").Align(HorizontalAlignment.Center),
            UI.Text(UI.F("Settings.About.Version", "Version {0}", vm.AppVersion), "body").Align(HorizontalAlignment.Center),
            UI.Text(releaseLine, "caption", "textSecondary").Align(HorizontalAlignment.Center),
            UI.WrappedText(GitHubReleaseInformation.RepositoryTagline, "body-muted").Align(HorizontalAlignment.Center),
            Link($"{GitHubReleaseInformation.RepositoryOwnerDisplayName} · @{GitHubReleaseInformation.RepositoryOwnerLogin}", new Uri(GitHubReleaseInformation.RepositoryOwnerUrl)).Align(HorizontalAlignment.Center),
            Link(UI.T("Settings.About.Releases", "GitHub releases"), vm.PublishedRelease?.Page ?? new Uri(GitHubReleaseInformation.ReleasesUrl)).Align(HorizontalAlignment.Center));
        product.Margin = new Thickness(0, 8, 0, 12);

        var updateStatus = UI.Text(vm.UpdateStatusText, "body-muted");
        var updateCandidate = UI.Text(string.Empty, "body-strong");
        var updateError = UI.WrappedText(string.Empty, "caption", "danger");
        subscriptions.Add(Observe.Props(vm, () => updateStatus.Text = vm.UpdateStatusText,
            nameof(SettingsViewModel.UpdateStatusText)));
        subscriptions.Add(Observe.Props(vm, () =>
        {
            updateCandidate.Text = string.IsNullOrWhiteSpace(vm.UpdateCandidateVersion)
                ? string.Empty : UI.F("Settings.Update.Candidate", "Available version: {0}", vm.UpdateCandidateVersion);
            updateCandidate.Visibility = string.IsNullOrWhiteSpace(vm.UpdateCandidateVersion) ? Visibility.Collapsed : Visibility.Visible;
        }, nameof(SettingsViewModel.UpdateCandidateVersion)));
        subscriptions.Add(Observe.Props(vm, () =>
        {
            updateError.Text = vm.UpdateErrorText ?? string.Empty;
            updateError.Visibility = string.IsNullOrWhiteSpace(vm.UpdateErrorText) ? Visibility.Collapsed : Visibility.Visible;
        }, nameof(SettingsViewModel.UpdateErrorText)));

        var installZip = UI.Button(UI.T("Settings.Update.InstallZip", "Install from ZIP…"), installFromZip, ButtonKind.Ghost);
        subscriptions.Add(Observe.Props(vm, () => installZip.IsEnabled = vm.CanManageUpdates,
            nameof(SettingsViewModel.CanManageUpdates)));

        var updates = DetailLayout.Section(
            UI.T("Settings.Update.SectionTitle", "Updates"),
            UI.T("Settings.Update.Desc", "Keep naut current without changing Vault data."),
            "icon.navigation.import",
            updateStatus,
            updateCandidate,
            updateError,
            UI.Wrap(8,
                UI.Button(UI.T("Settings.Update.Check", "Check for updates"), null, ButtonKind.Secondary, command: vm.CheckForUpdatesCommand),
                UI.Button(UI.T("Settings.Update.DownloadInstall", "Download & Install"), null, ButtonKind.Primary, command: vm.InstallUpdateCommand),
                installZip));

        var information = DetailLayout.Section(
            UI.T("Settings.About.Information", "Information"),
            UI.T("Settings.About.InformationDesc", "Technical details and licenses are available when you need them."),
            "icon.status.info",
            UI.Wrap(8,
                UI.Button(UI.T("Settings.About.TechnicalDetails", "Technical details"), () => technicalDetails.Visibility = technicalDetails.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible, ButtonKind.Ghost),
                UI.Button(UI.T("Settings.Notices", "Third-party notices"), () => noticesPanel.Visibility = noticesPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible, ButtonKind.Ghost)),
            technicalDetails, noticesPanel);
        return UI.V(12, product, updates, information);
    }

}
