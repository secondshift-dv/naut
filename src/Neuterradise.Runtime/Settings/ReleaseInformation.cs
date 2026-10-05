using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Neuterradise.App.Settings;

public sealed record ReleaseInformation(string Version, DateTimeOffset PublishedAt, Uri Page);

public static class GitHubReleaseInformation
{
    public const string RepositoryOwnerLogin = "secondshift-dv";
    public const string RepositoryOwnerDisplayName = "Second Shift";
    public const string RepositoryOwnerUrl = "https://github.com/secondshift-dv";
    public const string RepositoryTagline = "A Navigator for Your Things Worth Keeping";
    public const string ReleasesUrl = "https://github.com/secondshift-dv/naut/releases";

    public static async Task<ReleaseInformation?> ReadAsync(string installedVersion, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("naut/" + installedVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        foreach (var tag in new[] { "v" + installedVersion, installedVersion })
        {
            using var response = await client.GetAsync(
                "https://api.github.com/repos/secondshift-dv/naut/releases/tags/" + Uri.EscapeDataString(tag),
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > 256 * 1024) return null;
                await buffer.WriteAsync(bytes.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            return Parse(buffer.ToArray(), installedVersion);
        }
        return null;
    }

    public static ReleaseInformation? Parse(ReadOnlyMemory<byte> json, string installedVersion)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (!release.TryGetProperty("tag_name", out var tag)
            || tag.GetString()?.TrimStart('v') != installedVersion
            || !release.TryGetProperty("draft", out var draft) || draft.GetBoolean()
            || !release.TryGetProperty("published_at", out var published) || !published.TryGetDateTimeOffset(out var date)
            || !release.TryGetProperty("html_url", out var page)
            || !Uri.TryCreate(page.GetString(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com"
            || !uri.AbsolutePath.StartsWith("/secondshift-dv/naut/releases/tag/", StringComparison.Ordinal)) return null;
        return new ReleaseInformation(installedVersion, date, uri);
    }
}
