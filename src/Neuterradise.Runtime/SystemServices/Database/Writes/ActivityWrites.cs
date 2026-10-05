using System.Text.Json;

namespace Neuterradise.App.SystemServices.Database.Writes;

public static class ActivityEventType
{

    public const string ProfileCreated = "ProfileCreated";

    public const string ProfileRenamed = "ProfileRenamed";

    public const string ProfileMetadataChanged = "ProfileMetadataChanged";

    public const string ProfileAppearanceChanged = "ProfileAppearanceChanged";

    public const string PrimaryProfileChanged = "PrimaryProfileChanged";

    public const string MediaAssociationAdded = "MediaAssociationAdded";

    public const string MediaAssociationRemoved = "MediaAssociationRemoved";

    public const string MediaMovedToTrash = "MediaMovedToTrash";

    public const string ProfileTrashed = "ProfileTrashed";

    public const string MediaRestored = "MediaRestored";

    public const string ProfileRestored = "ProfileRestored";

    public const string ManualRelatedAdded = "ManualRelatedAdded";

    public const string ManualRelatedRemoved = "ManualRelatedRemoved";

    public const string UnknownResolved = "UnknownResolved";

    public const string MediaPurged = "MediaPurged";

    public const string ProfilePurged = "ProfilePurged";

    public const string LibraryRepairCompleted = "LibraryRepairCompleted";

    public const string ThemeChanged = "ThemeChanged";
}

public sealed class ActivityWrites
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;

    public ActivityWrites(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
    }

    public async Task AppendAsync(
        ActivityEntryPersistence entry,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        await AppendInternalAsync(transaction, entry, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task AppendInternalAsync(
        CatalogTransaction transaction,
        ActivityEntryPersistence entry,
        CancellationToken cancellationToken)
    {
        Validate(entry);
        entry = entry with
        {
            OccurredAtUtc = DbTime.Parse(DbTime.Format(entry.OccurredAtUtc)),
        };

        var existing = await ReadEntryAsync(transaction, entry.ActivityId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing != entry)
            {
                throw new CatalogInvariantException(
                    $"Activity {entry.ActivityId:D} already records a different event fact.");
            }

            return;
        }

        await using var insert = transaction.CreateCommand(
            """
            INSERT INTO activity_log(
                activity_id, event_type, profile_id, media_id,
                import_unit_id, operation_id, payload_json, occurred_at_ms)
            VALUES (
                $activityId, $eventType, $profileId, $assetId,
                $importUnitId, $operationId, $payloadJson, $occurredAtMs);
            """);
        insert.Parameters.AddWithValue("$activityId", DbGuid.Format(entry.ActivityId));
        insert.Parameters.AddWithValue("$eventType", entry.EventType);
        AddNullableGuid(insert, "$profileId", entry.ProfileId);
        AddNullableGuid(insert, "$assetId", entry.MediaId);
        AddNullableGuid(insert, "$importUnitId", entry.ImportUnitId);
        AddNullableGuid(insert, "$operationId", entry.OperationId);
        insert.Parameters.AddWithValue("$payloadJson", entry.PayloadJson);
        insert.Parameters.AddWithValue("$occurredAtMs", DbTime.Format(entry.OccurredAtUtc));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.QueueInvalidation(CatalogInvalidationDomain.Activity);
    }

    private static async Task<ActivityEntryPersistence?> ReadEntryAsync(
        CatalogTransaction transaction,
        Guid activityId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT event_type, profile_id, media_id, import_unit_id,
                   operation_id, payload_json, occurred_at_ms
            FROM activity_log
            WHERE activity_id = $activityId;
            """);
        command.Parameters.AddWithValue("$activityId", DbGuid.Format(activityId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ActivityEntryPersistence(
            activityId,
            reader.GetString(0),
            reader.IsDBNull(1) ? null : DbGuid.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : DbGuid.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : DbGuid.Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : DbGuid.Parse(reader.GetString(4)),
            reader.GetString(5),
            DbTime.Parse(reader.GetInt64(6)));
    }

    private static void AddNullableGuid(
        Microsoft.Data.Sqlite.SqliteCommand command,
        string name,
        Guid? value) =>
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : DbGuid.Format(value.Value));

    private static void Validate(ActivityEntryPersistence entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureNonEmpty(entry.ActivityId, nameof(entry.ActivityId));
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.EventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.PayloadJson);
        foreach (var (value, name) in new[]
        {
            (entry.ProfileId, nameof(entry.ProfileId)),
            (entry.MediaId, nameof(entry.MediaId)),
            (entry.ImportUnitId, nameof(entry.ImportUnitId)),
            (entry.OperationId, nameof(entry.OperationId)),
        })
        {
            if (value == Guid.Empty)
            {
                throw new ArgumentException("A correlation identifier cannot be empty.", name);
            }
        }

        try
        {
            using var _ = JsonDocument.Parse(entry.PayloadJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Activity payload JSON must be valid.", nameof(entry), exception);
        }
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }
}

public sealed record ActivityEntryPersistence(
    Guid ActivityId,
    string EventType,
    Guid? ProfileId,
    Guid? MediaId,
    Guid? ImportUnitId,
    Guid? OperationId,
    string PayloadJson,
    DateTimeOffset OccurredAtUtc);
