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

    // Display-only cache under AppStateRoot. It never participates in signed-update trust.
    public static ReleaseInformation? ReadCached(string installedVersion, string appStateRoot)
    {
        var path = Path.Combine(appStateRoot, "published-release.json");
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > 2048) return null;
            return Parse(File.ReadAllBytes(path), installedVersion);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public static void Cache(ReleaseInformation release, string appStateRoot)
    {
        var path = Path.Combine(appStateRoot, "published-release.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(appStateRoot);
            var json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                tag_name = "v" + release.Version,
                draft = false,
                published_at = release.PublishedAt,
                html_url = release.Page.AbsoluteUri,
            });
            if (Parse(json, release.Version) != release) return;
            File.WriteAllBytes(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

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
            return await ParseAsync(stream, installedVersion, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Reads the bounded GitHub release response, including releases with large asset inventories.</summary>
    public static async Task<ReleaseInformation?> ParseAsync(
        Stream stream, string installedVersion, CancellationToken cancellationToken)
    {
        const int maximumResponseBytes = 8 * 1024 * 1024;
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > maximumResponseBytes) return null;
            await buffer.WriteAsync(bytes.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        return Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), installedVersion);
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
