using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;

namespace Neuterradise.App.Faces;

internal sealed record FaceReviewQueueCursor(long CreatedAtMs, Guid FaceId);

internal sealed record FaceReviewQueuePage(
    IReadOnlyList<FaceReviewReadModel> Items,
    FaceReviewQueueCursor? NextCursor);

/// <summary>
/// Keyset reader for the human face-review exception queue. It reads only unresolved detections and
/// never relies on OFFSET, so confirming/rejecting rows while the queue is open cannot shift later
/// pages underneath the user. A scoped queue includes unresolved faces suggested/confirmed for the
/// Profile as well as unresolved faces on media related to that Profile.
/// </summary>
internal sealed class FaceReviewQueueReads
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly FaceReads _faceReads;

    public FaceReviewQueueReads(CatalogDb catalog, FaceReads faceReads)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _faceReads = faceReads ?? throw new ArgumentNullException(nameof(faceReads));
    }

    public async Task<FaceReviewQueuePage> GetPageAsync(
        Guid? profileId,
        int pageSize = 50,
        FaceReviewQueueCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        var normalizedProfileId = profileId is { } id && id != Guid.Empty ? id : (Guid?)null;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var cursorClause = cursor is null
            ? string.Empty
            : """
              AND (fd.created_at_ms < $cursorCreatedAt
                   OR (fd.created_at_ms = $cursorCreatedAt AND fd.face_id < $cursorFaceId))
              """;

        // Publication scope is shared by the global queue and Profile-scoped review.
        string profileClause;
        if (normalizedProfileId is null)
        {
            // Global queue: require public relation + relation Profile publicly valid.
            profileClause = """
                AND EXISTS (
                    SELECT 1 FROM profile_media pa
                    JOIN profiles rp ON rp.profile_id = pa.profile_id
                    WHERE pa.media_id = fd.media_id
                      AND pa.publication_import_unit_id IS NULL
                      AND (rp.kind <> 'NORMAL' OR (rp.visibility = 'PUBLISHED' AND rp.trashed_at_ms IS NULL))
                )
                """;
        }
        else
        {
            // Scoped queue: the target Profile must be public and the FaceDetection's Media must
            // participate in at least one public relation before suggestion/confirmation membership
            // can make the face visible on a normal product surface.
            profileClause = """
              AND EXISTS (
                  SELECT 1 FROM profiles target
                  WHERE target.profile_id = $profileId
                    AND (target.kind <> 'NORMAL' OR (target.visibility = 'PUBLISHED' AND target.trashed_at_ms IS NULL))
              )
              AND EXISTS (
                  SELECT 1
                  FROM profile_media public_pa
                  JOIN profiles public_profile ON public_profile.profile_id = public_pa.profile_id
                  WHERE public_pa.media_id = fd.media_id
                    AND public_pa.publication_import_unit_id IS NULL
                    AND (public_profile.kind <> 'NORMAL' OR (public_profile.visibility = 'PUBLISHED' AND public_profile.trashed_at_ms IS NULL))
              )
              AND (
                    si.profile_id = $profileId
                 OR ci.profile_id = $profileId
                 OR EXISTS (
                        SELECT 1
                        FROM profile_media pa
                        JOIN profiles rp ON rp.profile_id = pa.profile_id
                        WHERE pa.media_id = fd.media_id
                          AND pa.profile_id = $profileId
                          AND pa.publication_import_unit_id IS NULL
                          AND (rp.kind <> 'NORMAL' OR (rp.visibility = 'PUBLISHED' AND rp.trashed_at_ms IS NULL))
                    )
              )
              """;
        }

        command.CommandText = $"""
            SELECT fd.face_id, fd.created_at_ms
            FROM face_detections fd
            JOIN media a ON a.media_id = fd.media_id AND a.state = 'ACTIVE'
            LEFT JOIN identities si ON fd.suggested_identity_id = si.identity_id
            LEFT JOIN identities ci ON fd.confirmed_identity_id = ci.identity_id
            WHERE fd.decision_state IN ('UNKNOWN', 'SUGGESTED')
            {profileClause}
            {cursorClause}
            ORDER BY fd.created_at_ms DESC, fd.face_id DESC
            LIMIT {pageSize + 1};
            """;

        if (normalizedProfileId is { } scopedProfileId)
        {
            command.Parameters.AddWithValue("$profileId", DbGuid.Format(scopedProfileId));
        }

        if (cursor is { } current)
        {
            command.Parameters.AddWithValue("$cursorCreatedAt", current.CreatedAtMs);
            command.Parameters.AddWithValue("$cursorFaceId", DbGuid.Format(current.FaceId));
        }

        var rows = new List<(Guid FaceId, long CreatedAtMs)>(pageSize + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((DbGuid.Parse(reader.GetString(0)), reader.GetInt64(1)));
            }
        }

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        // Read review details in one bulk query to avoid per-face N+1 database work.
        var faceIds = rows.Select(r => r.FaceId).ToArray();
        var bulkResults = await _faceReads.GetFaceReviewsByIdsAsync(faceIds, cancellationToken)
            .ConfigureAwait(false);

        // Reorder to match keyset page order.
        var byId = bulkResults.ToDictionary(r => r.FaceId);
        var items = new List<FaceReviewReadModel>(rows.Count);
        foreach (var (faceId, _) in rows)
        {
            if (byId.TryGetValue(faceId, out var detail)
                && detail.DecisionState is FaceDecisionState.Unknown or FaceDecisionState.Suggested)
            {
                items.Add(detail);
            }
        }

        FaceReviewQueueCursor? next = null;
        if (hasMore && rows.Count > 0)
        {
            var last = rows[^1];
            next = new FaceReviewQueueCursor(last.CreatedAtMs, last.FaceId);
        }

        return new FaceReviewQueuePage(items, next);
    }
}
