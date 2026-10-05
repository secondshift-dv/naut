using Microsoft.Data.Sqlite;
using Neuterradise.App.Activity;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed class ActivityReads
{
    private readonly CatalogConnectionFactory _connectionFactory;

    public ActivityReads(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
    }

    public ActivityReads(CatalogConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<ActivityPage> GetActivityPageAsync(
        Guid? profileId = null,
        Guid? assetId = null,
        int pageSize = 50,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 500);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var clauses = new List<string>();
        var parameters = new List<(string Name, object Value)>();

        if (profileId.HasValue && profileId.Value != Guid.Empty)
        {
            clauses.Add("l.profile_id = $profileId");
            parameters.Add(("$profileId", DbGuid.Format(profileId.Value)));
        }

        if (assetId.HasValue && assetId.Value != Guid.Empty)
        {
            clauses.Add("l.media_id = $assetId");
            parameters.Add(("$assetId", DbGuid.Format(assetId.Value)));
        }

        // Exclude activity entries tied to normal DRAFT (unpublished) Profiles.
        clauses.Add("(l.profile_id IS NULL OR p.kind <> 'NORMAL' OR p.visibility = 'PUBLISHED')");

        if (!string.IsNullOrWhiteSpace(continuationToken)
            && TryParseCursor(continuationToken, out var cursorTime, out var cursorId))
        {
            clauses.Add("(l.occurred_at_ms < $cursorTime OR (l.occurred_at_ms = $cursorTime AND l.activity_id < $cursorId))");
            parameters.Add(("$cursorTime", cursorTime));
            parameters.Add(("$cursorId", DbGuid.Format(cursorId)));
        }

        var whereClause = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";
        var sql = $"""
            SELECT l.activity_id, l.event_type, l.profile_id, l.media_id,
                   l.import_unit_id, l.operation_id, l.payload_json, l.occurred_at_ms,
                   coalesce(p.display_name,
                            CASE WHEN p.kind = 'UNKNOWN' AND p.unknown_sequence IS NOT NULL
                                 THEN 'Unknown #' || p.unknown_sequence END) AS profile_display_name,
                   coalesce(a.current_managed_file_name, a.original_file_name, a.source_display_name) AS media_display_name,
                   u.source_display_name AS import_display_name
            FROM activity_log l
            LEFT JOIN profiles p ON p.profile_id = l.profile_id
            LEFT JOIN media a ON a.media_id = l.media_id
            LEFT JOIN import_units u ON u.import_unit_id = l.import_unit_id
            {whereClause}
            ORDER BY l.occurred_at_ms DESC, l.activity_id DESC
            LIMIT {pageSize + 1};
            """;

        var items = new List<ActivityItemReadModel>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadActivityItem(reader));
            }
        }

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        string? nextPageToken = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            nextPageToken = $"{last.OccurredAtUtc.ToUnixTimeMilliseconds()}:{last.ActivityId:D}";
        }

        return new ActivityPage(items, nextPageToken, hasMore);
    }

    public async Task<IReadOnlyList<ActivityItemReadModel>> GetRecentEntriesAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT l.activity_id, l.event_type, l.profile_id, l.media_id,
                   l.import_unit_id, l.operation_id, l.payload_json, l.occurred_at_ms,
                   coalesce(p.display_name,
                            CASE WHEN p.kind = 'UNKNOWN' AND p.unknown_sequence IS NOT NULL
                                 THEN 'Unknown #' || p.unknown_sequence END) AS profile_display_name,
                   coalesce(a.current_managed_file_name, a.original_file_name, a.source_display_name) AS media_display_name,
                   u.source_display_name AS import_display_name
            FROM activity_log l
            LEFT JOIN profiles p ON p.profile_id = l.profile_id
            LEFT JOIN media a ON a.media_id = l.media_id
            LEFT JOIN import_units u ON u.import_unit_id = l.import_unit_id
            WHERE (l.profile_id IS NULL OR p.kind <> 'NORMAL' OR p.visibility = 'PUBLISHED')
            ORDER BY l.occurred_at_ms DESC, l.activity_id DESC
            LIMIT {limit};
            """;

        var list = new List<ActivityItemReadModel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadActivityItem(reader));
        }

        return list;
    }

    private static ActivityItemReadModel ReadActivityItem(SqliteDataReader reader)
    {
        var activityId = DbGuid.Parse(reader.GetString(0));
        var eventType = reader.GetString(1);
        var profileId = reader.IsDBNull(2) ? (Guid?)null : DbGuid.Parse(reader.GetString(2));
        var assetId = reader.IsDBNull(3) ? (Guid?)null : DbGuid.Parse(reader.GetString(3));
        var importUnitId = reader.IsDBNull(4) ? (Guid?)null : DbGuid.Parse(reader.GetString(4));
        var operationId = reader.IsDBNull(5) ? (Guid?)null : DbGuid.Parse(reader.GetString(5));
        var payloadJson = reader.GetString(6);
        var occurredAt = DbTime.Parse(reader.GetInt64(7));
        var profileDisplayName = reader.IsDBNull(8) ? null : reader.GetString(8);
        var assetDisplayName = reader.IsDBNull(9) ? null : reader.GetString(9);
        var importDisplayName = reader.IsDBNull(10) ? null : reader.GetString(10);

        return new ActivityItemReadModel(
            activityId,
            eventType,
            profileId,
            assetId,
            importUnitId,
            operationId,
            payloadJson,
            occurredAt,
            profileDisplayName,
            assetDisplayName,
            importDisplayName);
    }

    private static bool TryParseCursor(string token, out long timeMs, out Guid id)
    {
        timeMs = 0;
        id = Guid.Empty;
        var parts = token.Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        return long.TryParse(parts[0], out timeMs) && DomainId.TryParse(parts[1], out id);
    }

    public async Task<HomeChronicleReadModel> GetMonthlyChronicleSummaryAsync(
        DateTimeOffset startOfMonthUtc,
        DateTimeOffset endOfMonthUtc,
        CancellationToken cancellationToken = default)
    {
        var startMs = startOfMonthUtc.ToUnixTimeMilliseconds();
        var endMs = endOfMonthUtc.ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(DISTINCT a.media_id)
                 FROM media a
                 JOIN profile_media pa ON pa.media_id = a.media_id AND pa.relation_type = 'OWNER'
                 JOIN profiles p ON p.profile_id = pa.profile_id
                 WHERE a.state = 'ACTIVE'
                   AND a.added_to_library_at_ms >= $startMs AND a.added_to_library_at_ms < $endMs
                   AND pa.publication_import_unit_id IS NULL
                   AND (p.kind <> 'NORMAL' OR p.visibility = 'PUBLISHED')
                ) AS media_committed_count,
                (SELECT COUNT(*) FROM profiles
                 WHERE kind = 'NORMAL' AND visibility = 'PUBLISHED' AND trashed_at_ms IS NULL
                   AND created_at_ms >= $startMs AND created_at_ms < $endMs
                ) AS profiles_created_count,
                (SELECT COUNT(*)
                 FROM face_detections fd
                 JOIN profile_media pa ON pa.media_id = fd.media_id AND pa.relation_type = 'OWNER'
                 JOIN profiles p ON p.profile_id = pa.profile_id
                 WHERE fd.decision_state = 'CONFIRMED'
                   AND fd.updated_at_ms >= $startMs AND fd.updated_at_ms < $endMs
                   AND pa.publication_import_unit_id IS NULL
                   AND (p.kind <> 'NORMAL' OR p.visibility = 'PUBLISHED')
                ) AS faces_confirmed_count,
                (SELECT COUNT(*)
                 FROM related_profile_summary rps
                 JOIN profiles pl ON pl.profile_id = rps.profile_id_low
                 JOIN profiles ph ON ph.profile_id = rps.profile_id_high
                 WHERE rps.rank_score > 0
                   AND (rps.shared_media_count > 0 OR rps.confirmed_face_count > 0 OR rps.manual_relation = 1)
                   AND ((rps.last_evidence_at_ms >= $startMs AND rps.last_evidence_at_ms < $endMs) OR (rps.updated_at_ms >= $startMs AND rps.updated_at_ms < $endMs))
                   AND (pl.kind <> 'NORMAL' OR pl.visibility = 'PUBLISHED')
                   AND (ph.kind <> 'NORMAL' OR ph.visibility = 'PUBLISHED')
                ) AS related_pairs_count;
            """;
        command.Parameters.AddWithValue("$startMs", startMs);
        command.Parameters.AddWithValue("$endMs", endMs);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new HomeChronicleReadModel(0, 0, 0, 0, startOfMonthUtc, endOfMonthUtc);
        }

        var mediaCount = reader.GetInt64(0);
        var profilesCount = reader.GetInt64(1);
        var facesCount = reader.GetInt64(2);
        var relatedPairsCount = reader.GetInt64(3);

        return new HomeChronicleReadModel(
            mediaCount,
            profilesCount,
            facesCount,
            relatedPairsCount,
            startOfMonthUtc,
            endOfMonthUtc);
    }
}

public sealed record HomeChronicleReadModel(
    long MediaCommittedCount,
    long ProfilesCreatedCount,
    long FacesConfirmedCount,
    long DurableRelatedPairsCount,
    DateTimeOffset StartOfMonthUtc,
    DateTimeOffset EndOfMonthUtc);
