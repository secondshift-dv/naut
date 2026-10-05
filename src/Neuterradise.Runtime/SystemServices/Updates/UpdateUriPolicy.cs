namespace Neuterradise.App.SystemServices.Updates;

/// <summary>
/// Redirect authority for update metadata and payload downloads. Ordinary HTTPS sources may redirect
/// only inside the same authority. GitHub Release browser-download URLs may additionally redirect to
/// GitHub's dedicated release-asset CDN.
/// </summary>
public static class UpdateUriPolicy
{
    private const string GitHubHost = "github.com";
    private const string GitHubReleaseAssetHost = "release-assets.githubusercontent.com";
    private const string GitHubLegacyReleaseAssetHost = "release-media.githubusercontent.com";

    public static bool IsTrustedEffectiveUri(Uri requested, Uri effective)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(effective);

        if (!IsSafeHttps(requested) || !IsSafeHttps(effective))
        {
            return false;
        }

        if (Uri.Compare(
                requested,
                effective,
                UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) == 0)
        {
            return true;
        }

        return string.Equals(requested.Host, GitHubHost, StringComparison.OrdinalIgnoreCase)
            && IsGitHubReleaseDownloadPath(requested.AbsolutePath)
            && (
                string.Equals(effective.Host, GitHubReleaseAssetHost, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    effective.Host,
                    GitHubLegacyReleaseAssetHost,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeHttps(Uri uri) =>
        uri.IsAbsoluteUri
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsGitHubReleaseDownloadPath(string absolutePath) =>
        absolutePath.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase)
        || absolutePath.Contains("/releases/latest/download/", StringComparison.OrdinalIgnoreCase);
}
