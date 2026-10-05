using System.Text.Json;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;

namespace Neuterradise.App.SystemServices.Jobs;

/// <summary>
/// Durable authority for the single import the user has asked the scheduler to favor.
/// Import work is Media-owned, so all priority changes are resolved through import_items.
/// </summary>
public sealed class ImportPriorityOperations
{
    internal const string SettingKey = "scheduler.focusedImportUnit";

    private readonly SettingsReads _settings;
    private readonly JobWrites _jobs;

    public ImportPriorityOperations(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _settings = new SettingsReads(catalog);
        _jobs = new JobWrites(catalog);
    }

    public async Task<Guid?> GetFocusedImportUnitAsync(CancellationToken cancellationToken = default) =>
        Parse(await _settings.GetSettingValueAsync(SettingKey, cancellationToken).ConfigureAwait(false));

    public async Task<bool> FocusAsync(Guid importUnitId, CancellationToken cancellationToken = default)
    {
        if (importUnitId == Guid.Empty)
        {
            throw new ArgumentException("A focused import must have a stable identifier.", nameof(importUnitId));
        }

        var focused = await _jobs
            .ChangeFocusedImportUnitAsync(
                SettingKey,
                importUnitId,
                expectedCurrent: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        JobSignals.Raise();
        return focused == importUnitId;
    }

    public async Task ClearIfFocusedAsync(Guid importUnitId, CancellationToken cancellationToken = default)
    {
        if (importUnitId == Guid.Empty)
        {
            return;
        }

        await _jobs
            .ChangeFocusedImportUnitAsync(
                SettingKey,
                focusedImportUnitId: null,
                expectedCurrent: importUnitId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        JobSignals.Raise();
    }

    public async Task NormalizeAsync(Guid importUnitId, CancellationToken cancellationToken = default)
    {
        if (importUnitId == Guid.Empty)
        {
            return;
        }

        await _jobs
            .SetImportUnitPriorityAsync(importUnitId, JobPriorityPolicy.DefaultPriority, cancellationToken)
            .ConfigureAwait(false);
        JobSignals.Raise();
    }

    internal static Guid? Parse(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson)
            || string.Equals(valueJson.Trim(), "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var value = JsonSerializer.Deserialize<string>(valueJson);
            return Guid.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
