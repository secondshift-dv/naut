using System.Net.Http;
using System.Text.Json;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Updates;
using Neuterradise.Release.Contracts;

namespace Neuterradise.App.Presentation;

public sealed class OfficialPackOffer
{
    internal OfficialPackOffer(InstalledPack installed, OfficialPackEntry entry)
    {
        PackId = installed.PackId;
        Name = installed.Manifest.Name;
        InstalledVersion = installed.Manifest.Version;
        AvailableVersion = entry.Version;
        Bytes = entry.Bytes;
        Entry = entry;
        PreviousHash = installed.ContentHash;
    }

    public string PackId { get; }
    public string Name { get; }
    public string InstalledVersion { get; }
    public string AvailableVersion { get; }
    public long Bytes { get; }
    internal OfficialPackEntry Entry { get; }
    internal string PreviousHash { get; }
}

internal sealed record OfficialPackPredecessor(string Version, string ContentHash);
internal sealed record OfficialPackEntry(string Slug, string PackId, string Version, string File,
    string Sha256, long Bytes, string ContentHash, int MinContractVersion, OfficialPackPredecessor[] Predecessors);

public sealed record OfficialPackCheckResult(IReadOnlyList<OfficialPackOffer> Offers, string? Error = null);

/// <summary>Explicit, signed updates for exact official pack generations. No private pack identity
/// or Vault data is sent to the feed; the local store owns validation and transactional replacement.</summary>
public sealed class OfficialPackUpdater
{
    private readonly HttpClient http;
    private readonly AppStatePaths appState;
    private readonly UpdatePublisherSignatureVerifier _verifier;

    public OfficialPackUpdater(HttpClient http, AppStatePaths appState)
        : this(http, appState, new UpdatePublisherSignatureVerifier()) { }

    internal OfficialPackUpdater(HttpClient http, AppStatePaths appState, UpdatePublisherSignatureVerifier verifier)
    {
        this.http = http;
        this.appState = appState;
        _verifier = verifier;
    }
    private static readonly Uri Feed = new("https://secondshift-dv.github.io/naut/packs/checksums.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record Registry(int SchemaVersion, OfficialPackEntry[] Packs);

    public async Task<OfficialPackCheckResult> CheckAsync(PresentationRuntime runtime, CancellationToken ct = default)
    {
        try
        {
            var metadata = await UpdateCheckService.ReadTrustedAsync(http, Feed, 4 * 1024 * 1024, "Pack registry", ct).ConfigureAwait(false);
            if (!metadata.Success || metadata.Bytes is null) return new([], metadata.Error);
            var signature = await UpdateCheckService.ReadTrustedAsync(http, new Uri(Feed, "checksums-signature.json"),
                64 * 1024, "Pack registry signature", ct).ConfigureAwait(false);
            if (!signature.Success || signature.Bytes is null) return new([], signature.Error);
            var verified = _verifier.Verify(metadata.Bytes, signature.Bytes);
            if (!verified.Verified) return new([], verified.Reason);
            var entries = ParseRegistry(metadata.Bytes);
            var offers = new List<OfficialPackOffer>();
            foreach (var pack in runtime.Packs)
            {
                if (pack.Origin != PackOrigin.User || pack.IsStandaloneAsset || !pack.IsUsable
                    || !Version.TryParse(pack.Manifest.Version, out var installed)) continue;
                var entry = entries.FirstOrDefault(candidate => candidate.PackId == pack.PackId);
                if (entry is null || entry.MinContractVersion > PresentationContract.Version
                    || Version.Parse(entry.Version) <= installed
                    || !entry.Predecessors.Any(previous => previous.Version == pack.Manifest.Version
                        && previous.ContentHash == pack.ContentHash)) continue;
                offers.Add(new(pack, entry));
            }
            return new(offers);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException
            or FormatException or InvalidDataException or ArgumentException)
        {
            return new([], "The official pack registry is unavailable or invalid.");
        }
    }

    internal static IReadOnlyList<OfficialPackEntry> ParseRegistry(byte[] bytes)
    {
        var registry = JsonSerializer.Deserialize<Registry>(bytes, JsonOptions);
        if (registry is not { SchemaVersion: 1, Packs.Length: > 0 and <= 64 })
            throw new InvalidDataException("Invalid official pack registry.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in registry.Packs)
        {
            if (entry is null || !PackValidator.IsValidId(entry.PackId) || !ids.Add(entry.PackId)
                || !PackValidator.IsValidId(entry.Slug) || entry.Slug.Contains('.')
                || !Version.TryParse(entry.Version, out var version)
                || entry.File != $"{entry.Slug}-{entry.Version}.ntpack"
                || !UpdateManifestAuthority.IsLowerSha256(entry.Sha256) || !UpdateManifestAuthority.IsLowerSha256(entry.ContentHash)
                || entry.Bytes <= 0 || entry.Bytes > PackValidator.MaxPackBytes + 16L * 1024 * 1024
                || entry.MinContractVersion < 1 || entry.Predecessors is not { Length: <= 128 })
                throw new InvalidDataException("Invalid official pack identity.");
            var predecessors = new HashSet<string>(StringComparer.Ordinal);
            foreach (var previous in entry.Predecessors)
                if (previous is null || !Version.TryParse(previous.Version, out var old) || old >= version
                    || !predecessors.Add(previous.Version) || !UpdateManifestAuthority.IsLowerSha256(previous.ContentHash))
                    throw new InvalidDataException("Invalid official pack predecessor.");
        }
        return registry.Packs;
    }

    public async Task<PackInstallResult> ApplyAsync(PresentationRuntime runtime, OfficialPackOffer offer,
        CancellationToken ct = default, IProgress<UpdateProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var entry = offer.Entry;
        var operation = Guid.NewGuid();
        var directory = appState.GetUpdateToolsPath(operation);
        try
        {
            var download = await new UpdateDownloadService(http, appState).DownloadAsync(
                new Uri(Feed, entry.File), operation, entry.Bytes, entry.Sha256, ct, progress).ConfigureAwait(false);
            if (!download.IsSuccess || download.PayloadPath is null)
                return new(false, null, [], download.SafeError);
            return await runtime.InstallOfficialPackAsync(download.PayloadPath,
                new(entry.PackId, offer.InstalledVersion, offer.PreviousHash, entry.Version,
                    entry.ContentHash, entry.MinContractVersion), ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(Path.Combine(directory, "payload.partial"));
                File.Delete(Path.Combine(directory, "payload.zip"));
                if (Directory.Exists(directory)) Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("Official pack staging cleanup deferred: {0}", ex.GetType().Name);
            }
        }
    }
}
