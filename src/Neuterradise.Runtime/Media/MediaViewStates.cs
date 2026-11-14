using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;

namespace Neuterradise.App.Media;

public sealed record FigureCamera(
    [property: JsonRequired] float Yaw = 0.25f,
    [property: JsonRequired] float Pitch = 0.10f,
    [property: JsonRequired] float DistanceScale = 1f,
    [property: JsonRequired] float TargetOffsetX = 0f,
    [property: JsonRequired] float TargetOffsetY = 0f)
{
    [JsonIgnore]
    public bool IsFinite =>
        float.IsFinite(Yaw) && float.IsFinite(Pitch) && float.IsFinite(DistanceScale)
        && float.IsFinite(TargetOffsetX) && float.IsFinite(TargetOffsetY);

    public FigureCamera Clamp() => this with
    {
        Yaw = MathF.IEEERemainder(Yaw, MathF.Tau),
        Pitch = Math.Clamp(Pitch, 0.02f, 1.2f),
        DistanceScale = Math.Clamp(DistanceScale, 0.35f, 3f),
        TargetOffsetX = Math.Clamp(TargetOffsetX, -1.5f, 1.5f),
        TargetOffsetY = Math.Clamp(TargetOffsetY, -1.5f, 1.5f),
    };
}

public sealed record InspectorImageViewState(
    [property: JsonRequired] double Zoom = 1.0,
    [property: JsonRequired] bool ActualSize = false);

public sealed class MediaViewStates
{
    private const string KeyPrefix = "media-view.v1.";
    private static readonly TimeSpan CoalesceInterval = TimeSpan.FromMilliseconds(300);
    private readonly CatalogDb _catalog;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private Dictionary<ViewKey, object> _pending = [];
    private Dictionary<ViewKey, object> _writing = [];
    private bool _running;

    internal MediaViewStates(CatalogDb catalog, TimeProvider clock)
    {
        _catalog = catalog;
        _clock = clock;
    }

    public async Task<FigureCamera> ReadFigureAsync(Guid profileId, Guid mediaId, CancellationToken token = default)
    {
        var state = await ReadAsync<FigureCamera>(new(profileId, mediaId, true), token).ConfigureAwait(false);
        return state is { IsFinite: true } ? state.Clamp() : new();
    }

    public async Task<InspectorImageViewState> ReadImageAsync(Guid profileId, Guid mediaId, CancellationToken token = default)
    {
        var state = await ReadAsync<InspectorImageViewState>(new(profileId, mediaId, false), token).ConfigureAwait(false);
        return state is { Zoom: >= 0.25 and <= 4.0 } ? state : new();
    }

    public void SaveFigure(Guid profileId, Guid mediaId, FigureCamera state)
    {
        if (state.IsFinite) Enqueue(new(profileId, mediaId, true), state.Clamp());
    }

    public void SaveImage(Guid profileId, Guid mediaId, InspectorImageViewState state)
    {
        if (state.Zoom is >= 0.25 and <= 4.0) Enqueue(new(profileId, mediaId, false), state);
    }

    internal static string ProfileKeyPattern(Guid profileId) => $"{KeyPrefix}*.{DbGuid.Format(profileId)}.*";
    internal static string MediaKeyPattern(Guid mediaId) => $"{KeyPrefix}*.*.{DbGuid.Format(mediaId)}";

    private async Task<T?> ReadAsync<T>(ViewKey key, CancellationToken token) where T : class
    {
        try
        {
            using var admission = _catalog.MutationAdmission.Enter(nameof(MediaViewStates));
            await using var lease = await _catalog.WriteCoordinator.EnterAsync(token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_pending.TryGetValue(key, out var state) || _writing.TryGetValue(key, out state))
                    return (T)state;
            }

            var json = await _catalog.SettingsReads.GetSettingValueAsync(key.Setting, token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_pending.TryGetValue(key, out var state)) return (T)state;
            }
            return json is { Length: <= 512 } ? JsonSerializer.Deserialize<T>(json) : null;
        }
        catch (Exception exception) when (CatalogDb.IsDatabaseFailure(exception)
            || exception is JsonException or MutationAdmissionClosedException)
        {
            Trace.TraceWarning("Media view preference read failed: {0}", exception.GetType().Name);
            return null;
        }
    }

    private void Enqueue(ViewKey key, object state)
    {
        lock (_gate)
        {
            if (!_catalog.MutationAdmission.IsAccepting) return;
            _pending[key] = state;
            if (_running) return;
            _running = true;
            _ = PersistAsync();
        }
    }

    private async Task PersistAsync()
    {
        try
        {
            using var admission = _catalog.MutationAdmission.Enter(nameof(MediaViewStates));
            while (true)
            {
                await Task.Delay(CoalesceInterval, _clock).ConfigureAwait(false);
                lock (_gate)
                {
                    _writing = _pending;
                    _pending = [];
                }

                try
                {
                    await PersistBatchAsync(_writing).ConfigureAwait(false);
                }
                catch (Exception exception) when (CatalogDb.IsDatabaseFailure(exception))
                {
                    Trace.TraceWarning("Media view preference write failed: {0}", exception.GetType().Name);
                }

                lock (_gate)
                {
                    _writing.Clear();
                    if (_pending.Count > 0) continue;
                    _running = false;
                    return;
                }
            }
        }
        catch (MutationAdmissionClosedException)
        {
            lock (_gate)
            {
                _pending.Clear();
                _running = false;
            }
        }
    }

    private async Task PersistBatchAsync(Dictionary<ViewKey, object> batch)
    {
        await using var lease = await _catalog.WriteCoordinator.EnterAsync().ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync().ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);
        await using var exists = transaction.CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM profiles WHERE profile_id = $profile)
               AND EXISTS(SELECT 1 FROM media WHERE media_id = $media);
            """);
        var profile = exists.Parameters.Add("$profile", Microsoft.Data.Sqlite.SqliteType.Text);
        var media = exists.Parameters.Add("$media", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var (key, state) in batch)
        {
            profile.Value = DbGuid.Format(key.ProfileId);
            media.Value = DbGuid.Format(key.MediaId);
            if (await exists.ExecuteScalarAsync().ConfigureAwait(false) is not 1L) continue;
            await SettingsWrites.SetSettingInTransactionAsync(transaction, key.Setting,
                JsonSerializer.Serialize(state), null, _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private readonly record struct ViewKey(Guid ProfileId, Guid MediaId, bool Figure)
    {
        public string Setting =>
            $"{KeyPrefix}{(Figure ? "figure" : "image")}.{DbGuid.Format(ProfileId)}.{DbGuid.Format(MediaId)}";
    }
}
