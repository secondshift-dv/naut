using System.Text.Json;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Lifecycle;

public sealed class SessionMarkerStore
{
    private readonly AppStatePaths _paths;
    public SessionMarkerStore(AppStatePaths paths) => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public async Task<SessionMarker?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_paths.Root, "session-state.json");
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SessionMarker>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUncleanAsync(Guid sessionId, Guid generation, IReadOnlyList<Guid> nonterminalOperations, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_paths.Root, "session-state.json");
        var marker = new SessionMarker(sessionId, generation, DateTimeOffset.UtcNow, "unclean", nonterminalOperations);
        var temp = path + ".partial";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(marker), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task WriteCleanAsync(Guid sessionId, Guid generation, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_paths.Root, "session-state.json");
        var marker = new SessionMarker(sessionId, generation, DateTimeOffset.UtcNow, "clean");
        var temp = path + ".partial";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(marker), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}

public sealed record SessionMarker(Guid SessionId, Guid Generation, DateTimeOffset WrittenAtUtc, string State, IReadOnlyList<Guid>? NonterminalOperationIds = null);
