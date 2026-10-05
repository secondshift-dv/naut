using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Presentation;

/// <summary>One persisted presentation selection/state (document 01 §10).</summary>
public sealed record PresentationBinding(
    ScopeKind ScopeKind,
    string ScopeId,
    string Slot,
    DefinitionRef? Definition,
    string? StateJson,
    int SchemaVersion,
    long UpdatedAtMs,
    long RowVersion);

public readonly record struct BindingKey(ScopeKind ScopeKind, string ScopeId, string Slot);

/// <summary>A requested change. <see cref="Remove"/> resets the scope to inherit from its parent.</summary>
public sealed record BindingChange(
    ScopeKind ScopeKind,
    string ScopeId,
    string Slot,
    DefinitionRef? Definition,
    string? StateJson,
    bool Remove = false)
{
    public BindingKey Key => new(ScopeKind, ScopeId, Slot);

    public static BindingChange Reset(ScopeKind scopeKind, string scopeId, string slot) => new(scopeKind, scopeId, slot, null, null, Remove: true);
}

/// <summary>Immutable snapshot of every binding. Preview transactions derive copies from it; nothing mutates it.</summary>
public sealed class BindingSet
{
    public static BindingSet Empty { get; } = new(ImmutableDictionary<BindingKey, PresentationBinding>.Empty);

    private readonly ImmutableDictionary<BindingKey, PresentationBinding> _bindings;

    private BindingSet(ImmutableDictionary<BindingKey, PresentationBinding> bindings) => _bindings = bindings;

    public static BindingSet From(IEnumerable<PresentationBinding> bindings) =>
        new(bindings.ToImmutableDictionary(b => new BindingKey(b.ScopeKind, b.ScopeId, b.Slot)));

    public IEnumerable<PresentationBinding> All => _bindings.Values;

    public int Count => _bindings.Count;

    public PresentationBinding? Get(ScopeKind scopeKind, string scopeId, string slot) =>
        _bindings.TryGetValue(new BindingKey(scopeKind, scopeId, slot), out var binding) ? binding : null;

    public BindingSet Apply(BindingChange change)
    {
        if (change.Remove)
        {
            return new(_bindings.Remove(change.Key));
        }

        var existing = _bindings.TryGetValue(change.Key, out var current) ? current : null;
        var next = new PresentationBinding(
            change.ScopeKind,
            change.ScopeId,
            change.Slot,
            change.Definition,
            change.StateJson,
            PresentationContract.BindingSchemaVersion,
            Environment.TickCount64,
            existing?.RowVersion ?? 0);
        return new(_bindings.SetItem(change.Key, next));
    }

    public BindingSet Apply(IEnumerable<BindingChange> changes)
    {
        var set = this;
        foreach (var change in changes)
        {
            set = set.Apply(change);
        }

        return set;
    }

    /// <summary>The changes that turn <paramref name="baseline"/> into this set.</summary>
    public IReadOnlyList<BindingChange> DiffFrom(BindingSet baseline)
    {
        var changes = new List<BindingChange>();
        foreach (var (key, binding) in _bindings)
        {
            if (!baseline._bindings.TryGetValue(key, out var old)
                || old.Definition != binding.Definition
                || !string.Equals(old.StateJson, binding.StateJson, StringComparison.Ordinal))
            {
                changes.Add(new BindingChange(key.ScopeKind, key.ScopeId, key.Slot, binding.Definition, binding.StateJson));
            }
        }

        foreach (var key in baseline._bindings.Keys)
        {
            if (!_bindings.ContainsKey(key))
            {
                changes.Add(BindingChange.Reset(key.ScopeKind, key.ScopeId, key.Slot));
            }
        }

        return changes;
    }

    public IEnumerable<PresentationBinding> UsingPack(string packId) =>
        _bindings.Values.Where(b => b.Definition?.PackId == packId);
}

/// <summary>
/// SQLite persistence for bindings and the pack registry. Writes go through the catalog write
/// coordinator in one transaction; Apply is atomic across every changed slot. Presentation writes never
/// touch domain tables and publish a PRESENTATION invalidation after commit.
/// </summary>
public sealed class PresentationBindingStore
{
    public const string InvalidationDomain = "PRESENTATION";

    private readonly CatalogDb _catalog;
    private readonly TimeProvider _time;

    public PresentationBindingStore(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<BindingSet> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT scope_kind, scope_id, slot, definition_pack_id, definition_id, state_json,
                   schema_version, updated_at_ms, row_version
            FROM presentation_bindings;
            """;
        var list = new List<PresentationBinding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!TryParseScope(reader.GetString(0), out var scope))
            {
                continue;
            }

            DefinitionRef? definition = reader.IsDBNull(3) || reader.IsDBNull(4)
                ? null
                : new DefinitionRef(reader.GetString(3), reader.GetString(4));
            var state = reader.IsDBNull(5) ? null : reader.GetString(5);
            list.Add(new PresentationBinding(scope, reader.GetString(1), reader.GetString(2), definition, state,
                reader.GetInt32(6), reader.GetInt64(7), reader.GetInt64(8)));
        }

        return BindingSet.From(list);
    }

    /// <summary>Validates and persists every change atomically. Returns the committed set.</summary>
    public async Task<BindingSet> ApplyAsync(IReadOnlyList<BindingChange> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var change in changes)
        {
            Validate(change);
        }

        if (changes.Count > 0)
        {
            await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);
            await ApplyInTransactionAsync(transaction, changes, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task ApplyInTransactionAsync(
        CatalogTransaction transaction,
        IReadOnlyList<BindingChange> changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var change in changes)
        {
            Validate(change);
        }

        if (changes.Count == 0)
        {
            return;
        }

        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var change in changes)
        {
            if (change.Remove)
            {
                await using var delete = transaction.CreateCommand(
                    "DELETE FROM presentation_bindings WHERE scope_kind = $kind AND scope_id = $id AND slot = $slot;");
                AddKey(delete, change);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            await using var upsert = transaction.CreateCommand(
                """
                INSERT INTO presentation_bindings(scope_kind, scope_id, slot, definition_pack_id, definition_id,
                                                  state_json, schema_version, updated_at_ms, row_version)
                VALUES ($kind, $id, $slot, $pack, $def, $state, $schema, $now, 0)
                ON CONFLICT(scope_kind, scope_id, slot) DO UPDATE SET
                    definition_pack_id = excluded.definition_pack_id,
                    definition_id = excluded.definition_id,
                    state_json = excluded.state_json,
                    schema_version = excluded.schema_version,
                    updated_at_ms = excluded.updated_at_ms,
                    row_version = presentation_bindings.row_version + 1;
                """);
            AddKey(upsert, change);
            upsert.Parameters.AddWithValue("$pack", (object?)change.Definition?.PackId ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$def", (object?)change.Definition?.DefinitionId ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$state", (object?)change.StateJson ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$schema", PresentationContract.BindingSchemaVersion);
            upsert.Parameters.AddWithValue("$now", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.QueueInvalidation(InvalidationDomain);
    }

    /// <summary>Deletes a retired slot from durable presentation state.</summary>
    public async Task DeleteRetiredSlotAsync(string slot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slot))
        {
            throw new ArgumentException("A retired presentation slot is required.", nameof(slot));
        }

        await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);
        await using var delete = transaction.CreateCommand("DELETE FROM presentation_bindings WHERE slot = $slot;");
        delete.Parameters.AddWithValue("$slot", slot);
        if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            transaction.QueueInvalidation(InvalidationDomain);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task MigrateLegacyThemeAsync(string? configurationTheme, Func<string, DefinitionRef?> resolve, CancellationToken cancellationToken)
    {
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);
        await using var marker = transaction.CreateCommand("SELECT value_json FROM settings WHERE key = 'presentation.theme-migrated';");
        var completed = await marker.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        if (!completed)
        {
            await using var existing = transaction.CreateCommand("SELECT COUNT(*) FROM presentation_bindings WHERE scope_kind = 'GLOBAL' AND scope_id = $scope AND slot = $slot;");
            existing.Parameters.AddWithValue("$scope", ScopeIds.Global);
            existing.Parameters.AddWithValue("$slot", PresentationSlots.Theme);
            if (Convert.ToInt64(await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
            {
                await using var legacy = transaction.CreateCommand("SELECT value_json FROM settings WHERE key = 'app.theme';");
                string? theme = null;
                if (await legacy.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json)
                {
                    try { theme = JsonSerializer.Deserialize<string>(json); }
                    catch (JsonException) { }
                }
                var reference = resolve(theme ?? "") ?? resolve(configurationTheme ?? "");
                if (reference is { } selected)
                    await ApplyInTransactionAsync(transaction, [new(ScopeKind.Global, ScopeIds.Global, PresentationSlots.Theme, selected, null)], cancellationToken).ConfigureAwait(false);
            }
            await using var complete = transaction.CreateCommand("INSERT INTO settings(key, value_json, updated_at_ms) VALUES ('presentation.theme-migrated', 'true', $now);");
            complete.Parameters.AddWithValue("$now", _time.GetUtcNow().ToUnixTimeMilliseconds());
            await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        // The receipt survives Reset, so a retired setting can never resurrect a selection.
        await using var delete = transaction.CreateCommand("DELETE FROM settings WHERE key = 'app.theme';");
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyDictionary<string, string>> ReadRecordedPackHashesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pack_id, content_hash FROM presentation_packs WHERE origin = 'USER';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hashes[reader.GetString(0)] = reader.GetString(1);
        }

        return hashes;
    }

    public async Task RecordPackAsync(InstalledPack pack, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);
        await using var upsert = transaction.CreateCommand(
            """
            INSERT INTO presentation_packs(pack_id, version, name, origin, content_hash, contract_version, state,
                                           diagnostics_json, installed_at_ms, updated_at_ms)
            VALUES ($id, $version, $name, $origin, $hash, $contract, $state, $diagnostics, $now, $now)
            ON CONFLICT(pack_id) DO UPDATE SET
                version = excluded.version, name = excluded.name, origin = excluded.origin,
                content_hash = excluded.content_hash, contract_version = excluded.contract_version,
                state = excluded.state, diagnostics_json = excluded.diagnostics_json, updated_at_ms = excluded.updated_at_ms;
            """);
        upsert.Parameters.AddWithValue("$id", pack.PackId);
        upsert.Parameters.AddWithValue("$version", pack.Manifest.Version);
        upsert.Parameters.AddWithValue("$name", pack.Manifest.Name);
        upsert.Parameters.AddWithValue("$origin", pack.Origin == PackOrigin.BuiltIn ? "BUILTIN" : "USER");
        upsert.Parameters.AddWithValue("$hash", pack.ContentHash);
        upsert.Parameters.AddWithValue("$contract", Math.Max(1, pack.Manifest.MinContractVersion));
        upsert.Parameters.AddWithValue("$state", pack.IsUsable ? "ACTIVE" : "INVALID");
        upsert.Parameters.AddWithValue("$diagnostics", pack.Diagnostics.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(pack.Diagnostics));
        upsert.Parameters.AddWithValue("$now", now);
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.QueueInvalidation(InvalidationDomain);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ForgetPackAsync(string packId, CancellationToken cancellationToken = default)
    {
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);
        await ForgetPackInTransactionAsync(transaction, packId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ForgetPackInTransactionAsync(
        CatalogTransaction transaction,
        string packId,
        CancellationToken cancellationToken)
    {
        await using var delete = transaction.CreateCommand("DELETE FROM presentation_packs WHERE pack_id = $id;");
        delete.Parameters.AddWithValue("$id", packId);
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.QueueInvalidation(InvalidationDomain);
    }

    public static string FormatScope(ScopeKind scope) => scope switch
    {
        ScopeKind.Global => "GLOBAL",
        ScopeKind.Surface => "SURFACE",
        ScopeKind.Profile => "PROFILE",
        ScopeKind.Item => "ITEM",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static bool TryParseScope(string text, out ScopeKind scope)
    {
        switch (text)
        {
            case "GLOBAL": scope = ScopeKind.Global; return true;
            case "SURFACE": scope = ScopeKind.Surface; return true;
            case "PROFILE": scope = ScopeKind.Profile; return true;
            case "ITEM": scope = ScopeKind.Item; return true;
            default: scope = default; return false;
        }
    }

    private static void AddKey(SqliteCommand command, BindingChange change)
    {
        command.Parameters.AddWithValue("$kind", FormatScope(change.ScopeKind));
        command.Parameters.AddWithValue("$id", change.ScopeId);
        command.Parameters.AddWithValue("$slot", change.Slot);
    }

    private static void Validate(BindingChange change)
    {
        if (!PresentationSlots.TryGet(change.Slot, out var slot))
        {
            throw new ArgumentException($"Unknown presentation slot '{change.Slot}'.");
        }

        if (!slot.AllowsScope(change.ScopeKind))
        {
            throw new ArgumentException($"Slot '{change.Slot}' cannot be set at {change.ScopeKind} scope.");
        }

        if (string.IsNullOrWhiteSpace(change.ScopeId) || change.ScopeId.Length > 128)
        {
            throw new ArgumentException("A binding scope id is required.");
        }

        if (change.Remove)
        {
            return;
        }

        if (change.Definition is null && change.StateJson is null)
        {
            throw new ArgumentException("A binding needs a definition or a state payload.");
        }

        if (change.StateJson is { } json)
        {
            if (json.Length > 16 * 1024)
            {
                throw new ArgumentException("A presentation state payload may not exceed 16 KB.");
            }

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("A presentation state payload must be a JSON object.");
            }
        }
    }
}
