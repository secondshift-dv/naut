using Microsoft.Data.Sqlite;
using Neuterradise.App.Faces;
using Neuterradise.App.Profiles;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed class FaceReads
{

    public const int MaximumIdentityCandidates = 5;

    private readonly CatalogConnectionFactory _connectionFactory;

    public FaceReads(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
    }

    public FaceReads(CatalogConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<FaceReviewReadModel?> GetFaceReviewAsync(
        Guid faceId,
        CancellationToken cancellationToken = default)
    {
        if (faceId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT fd.face_id, fd.media_id, fd.detection_key, fd.bounding_box_json,
                   fd.suggested_identity_id, si.profile_id AS suggested_profile_id, sp.display_name AS suggested_profile_name,
                   fd.confirmed_identity_id, ci.profile_id AS confirmed_profile_id, cp.display_name AS confirmed_profile_name,
                   fd.confidence, fd.decision_state, fd.model_id, fd.model_version,
                   fd.created_at_ms, fd.updated_at_ms, fd.row_version,
                   fd.sampled_timestamp_ms, fd.suggested_candidates_json
            FROM face_detections fd
            JOIN media a ON a.media_id = fd.media_id AND a.state = 'ACTIVE'
            LEFT JOIN identities si ON fd.suggested_identity_id = si.identity_id
            LEFT JOIN profiles sp ON si.profile_id = sp.profile_id
            LEFT JOIN identities ci ON fd.confirmed_identity_id = ci.identity_id
            LEFT JOIN profiles cp ON ci.profile_id = cp.profile_id
            WHERE fd.face_id = $faceId;
            """;
        command.Parameters.AddWithValue("$faceId", DbGuid.Format(faceId));

        var single = new List<FaceReviewReadModel>(1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            single.Add(ReadFaceReview(reader));
        }

        var resolved = await ResolveIdentityCandidateProfilesAsync(connection, single, cancellationToken)
            .ConfigureAwait(false);
        return resolved[0];
    }

    public async Task<IReadOnlyList<FaceReviewReadModel>> GetFaceReviewsForMediaAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT fd.face_id, fd.media_id, fd.detection_key, fd.bounding_box_json,
                   fd.suggested_identity_id, si.profile_id AS suggested_profile_id, sp.display_name AS suggested_profile_name,
                   fd.confirmed_identity_id, ci.profile_id AS confirmed_profile_id, cp.display_name AS confirmed_profile_name,
                   fd.confidence, fd.decision_state, fd.model_id, fd.model_version,
                   fd.created_at_ms, fd.updated_at_ms, fd.row_version,
                   fd.sampled_timestamp_ms, fd.suggested_candidates_json
            FROM face_detections fd
            JOIN media a ON a.media_id = fd.media_id AND a.state = 'ACTIVE'
            LEFT JOIN identities si ON fd.suggested_identity_id = si.identity_id
            LEFT JOIN profiles sp ON si.profile_id = sp.profile_id
            LEFT JOIN identities ci ON fd.confirmed_identity_id = ci.identity_id
            LEFT JOIN profiles cp ON ci.profile_id = cp.profile_id
            WHERE fd.media_id = $assetId
            ORDER BY fd.created_at_ms ASC, fd.face_id ASC;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var list = new List<FaceReviewReadModel>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(ReadFaceReview(reader));
            }
        }

        return await ResolveIdentityCandidateProfilesAsync(connection, list, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FaceReviewReadModel>> GetUnresolvedFaceReviewsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT fd.face_id, fd.media_id, fd.detection_key, fd.bounding_box_json,
                   fd.suggested_identity_id, si.profile_id AS suggested_profile_id, sp.display_name AS suggested_profile_name,
                   fd.confirmed_identity_id, ci.profile_id AS confirmed_profile_id, cp.display_name AS confirmed_profile_name,
                   fd.confidence, fd.decision_state, fd.model_id, fd.model_version,
                   fd.created_at_ms, fd.updated_at_ms, fd.row_version,
                   fd.sampled_timestamp_ms, fd.suggested_candidates_json
            FROM face_detections fd
            JOIN media a ON a.media_id = fd.media_id AND a.state = 'ACTIVE'
            LEFT JOIN identities si ON fd.suggested_identity_id = si.identity_id
            LEFT JOIN profiles sp ON si.profile_id = sp.profile_id
            LEFT JOIN identities ci ON fd.confirmed_identity_id = ci.identity_id
            LEFT JOIN profiles cp ON ci.profile_id = cp.profile_id
            WHERE fd.decision_state IN ('UNKNOWN', 'SUGGESTED')
              AND EXISTS (
                  SELECT 1 FROM profile_media pa
                  JOIN profiles rp ON rp.profile_id = pa.profile_id
                  WHERE pa.media_id = fd.media_id
                    AND pa.publication_import_unit_id IS NULL
                    AND (rp.kind <> 'NORMAL' OR (rp.visibility = 'PUBLISHED' AND rp.trashed_at_ms IS NULL))
              )
            ORDER BY fd.created_at_ms DESC, fd.face_id DESC
            LIMIT {limit};
            """;

        var list = new List<FaceReviewReadModel>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(ReadFaceReview(reader));
            }
        }

        return await ResolveIdentityCandidateProfilesAsync(connection, list, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FaceReviewReadModel>> GetFaceReviewsForProfileAsync(
        Guid profileId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            return [];
        }

        limit = Math.Clamp(limit, 1, 500);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT fd.face_id, fd.media_id, fd.detection_key, fd.bounding_box_json,
                   fd.suggested_identity_id, si.profile_id AS suggested_profile_id, sp.display_name AS suggested_profile_name,
                   fd.confirmed_identity_id, ci.profile_id AS confirmed_profile_id, cp.display_name AS confirmed_profile_name,
                   fd.confidence, fd.decision_state, fd.model_id, fd.model_version,
                   fd.created_at_ms, fd.updated_at_ms, fd.row_version,
                   fd.sampled_timestamp_ms, fd.suggested_candidates_json
            FROM face_detections fd
            JOIN media a ON a.media_id = fd.media_id AND a.state = 'ACTIVE'
            LEFT JOIN identities si ON fd.suggested_identity_id = si.identity_id
            LEFT JOIN profiles sp ON si.profile_id = sp.profile_id
            LEFT JOIN identities ci ON fd.confirmed_identity_id = ci.identity_id
            LEFT JOIN profiles cp ON ci.profile_id = cp.profile_id
            WHERE fd.decision_state IN ('UNKNOWN', 'SUGGESTED')
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
              AND (si.profile_id = $profileId OR ci.profile_id = $profileId
                   OR fd.media_id IN (
                       SELECT pa.media_id FROM profile_media pa
                       JOIN profiles rp ON rp.profile_id = pa.profile_id
                       WHERE pa.profile_id = $profileId
                         AND pa.publication_import_unit_id IS NULL
                         AND (rp.kind <> 'NORMAL' OR (rp.visibility = 'PUBLISHED' AND rp.trashed_at_ms IS NULL))
                   ))
            ORDER BY fd.created_at_ms DESC, fd.face_id DESC
            LIMIT {limit};
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        var list = new List<FaceReviewReadModel>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(ReadFaceReview(reader));
            }
        }

        return await ResolveIdentityCandidateProfilesAsync(connection, list, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FaceReviewReadModel>> GetFaceReviewsForUnitAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT fd.face_id, fd.media_id, fd.detection_key, fd.bounding_box_json,
                   fd.suggested_identity_id, si.profile_id AS suggested_profile_id, sp.display_name AS suggested_profile_name,
                   fd.confirmed_identity_id, ci.profile_id AS confirmed_profile_id, cp.display_name AS confirmed_profile_name,
                   fd.confidence, fd.decision_state, fd.model_id, fd.model_version,
                   fd.created_at_ms, fd.updated_at_ms, fd.row_version,
                   fd.sampled_timestamp_ms, fd.suggested_candidates_json
            FROM face_detections fd
            JOIN import_items ii ON (fd.media_id = ii.candidate_media_id OR fd.media_id = ii.reused_media_id)
            LEFT JOIN identities si ON fd.suggested_identity_id = si.identity_id
            LEFT JOIN profiles sp ON si.profile_id = sp.profile_id
            LEFT JOIN identities ci ON fd.confirmed_identity_id = ci.identity_id
            LEFT JOIN profiles cp ON ci.profile_id = cp.profile_id
            WHERE ii.import_unit_id = $unitId AND ii.disposition = 'INCLUDED'
            ORDER BY ii.created_at_ms ASC, fd.created_at_ms ASC, fd.face_id ASC;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var list = new List<FaceReviewReadModel>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(ReadFaceReview(reader));
            }
        }

        return await ResolveIdentityCandidateProfilesAsync(connection, list, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AppearanceFaceEvidence>>>
        GetAppearanceFaceEvidenceAsync(
            IReadOnlyCollection<Guid> assetIds,
            Guid? targetProfileId,
            CancellationToken cancellationToken = default)
    {
        var empty = new Dictionary<Guid, IReadOnlyList<AppearanceFaceEvidence>>();
        if (assetIds is null || assetIds.Count == 0)
        {
            return empty;
        }

        var distinct = assetIds.Where(static id => id != Guid.Empty).Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return empty;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var targetIdentities = new HashSet<Guid>();
        if (targetProfileId is { } profileId && profileId != Guid.Empty)
        {
            await using var identityCommand = connection.CreateCommand();
            identityCommand.CommandText =
                "SELECT identity_id FROM identities WHERE profile_id = $profileId AND is_active = 1;";
            identityCommand.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            await using var identityReader = await identityCommand
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await identityReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                targetIdentities.Add(DbGuid.Parse(identityReader.GetString(0)));
            }
        }

        var parameters = distinct.Select((_, index) => $"$asset{index}").ToArray();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT fd.media_id, fd.face_id, fd.sampled_timestamp_ms, fd.confidence,
                   fd.decision_state, fd.confirmed_identity_id, fd.suggested_candidates_json,
                   fd.bounding_box_json, am.width, am.height
            FROM face_detections fd
            LEFT JOIN media_metadata am ON am.media_id = fd.media_id
            WHERE fd.decision_state <> 'REJECTED'
              AND fd.media_id IN ({string.Join(", ", parameters)})
            ORDER BY fd.media_id, fd.sampled_timestamp_ms, fd.face_id;
            """;
        for (var index = 0; index < distinct.Length; index++)
        {
            command.Parameters.AddWithValue(parameters[index], DbGuid.Format(distinct[index]));
        }

        var grouped = new Dictionary<Guid, List<AppearanceFaceEvidence>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var assetId = DbGuid.Parse(reader.GetString(0));
            var faceId = DbGuid.Parse(reader.GetString(1));
            var sampledTimestamp = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
            var confidence = reader.IsDBNull(3) ? 0d : reader.GetDouble(3);
            var decision = DbEnum.ParseFaceDecisionState(reader.GetString(4));
            var confirmedIdentityId = reader.IsDBNull(5) ? (Guid?)null : DbGuid.Parse(reader.GetString(5));
            var evidence = FaceSuggestionEvidenceV1.TryParse(reader.IsDBNull(6) ? null : reader.GetString(6));
            var bounds = FaceBoundingBox.TryParse(reader.IsDBNull(7) ? null : reader.GetString(7));
            var frameWidth = reader.IsDBNull(8) ? (long?)null : reader.GetInt64(8);
            var frameHeight = reader.IsDBNull(9) ? (long?)null : reader.GetInt64(9);

            var isConfirmedForTarget = decision == FaceDecisionState.Confirmed
                && confirmedIdentityId is { } confirmed
                && targetIdentities.Contains(confirmed);

            double? targetSimilarity = null;
            if (targetIdentities.Count > 0 && evidence is not null)
            {
                foreach (var candidate in evidence.Candidates)
                {
                    if (targetIdentities.Contains(candidate.IdentityId)
                        && (targetSimilarity is null || candidate.Similarity > targetSimilarity))
                    {
                        targetSimilarity = candidate.Similarity;
                    }
                }
            }

            var relativeArea = 0d;
            double? normalizedX = null;
            double? normalizedY = null;
            double? normalizedWidth = null;
            double? normalizedHeight = null;
            double? normalizedCenterX = null;
            double? normalizedCenterY = null;
            if (bounds is not null && frameWidth is > 0 && frameHeight is > 0)
            {
                relativeArea = (double)bounds.Width * bounds.Height / (frameWidth.Value * frameHeight.Value);
                normalizedX = Math.Clamp((double)bounds.X / frameWidth.Value, 0, 1);
                normalizedY = Math.Clamp((double)bounds.Y / frameHeight.Value, 0, 1);
                normalizedWidth = Math.Clamp((double)bounds.Width / frameWidth.Value, 0, 1);
                normalizedHeight = Math.Clamp((double)bounds.Height / frameHeight.Value, 0, 1);
                normalizedCenterX = Math.Clamp(normalizedX.Value + normalizedWidth.Value / 2, 0, 1);
                normalizedCenterY = Math.Clamp(normalizedY.Value + normalizedHeight.Value / 2, 0, 1);
            }

            if (!grouped.TryGetValue(assetId, out var faces))
            {
                faces = [];
                grouped[assetId] = faces;
            }

            faces.Add(new AppearanceFaceEvidence(
                faceId,
                sampledTimestamp,
                confidence,
                targetSimilarity,
                isConfirmedForTarget,
                relativeArea,
                normalizedCenterX,
                normalizedCenterY,
                normalizedX,
                normalizedY,
                normalizedWidth,
                normalizedHeight));
        }

        return grouped.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<AppearanceFaceEvidence>)pair.Value);
    }

    /// <summary>
    /// Bulk face review read by IDs. One bounded SQL query for all requested face IDs.
    /// </summary>
    public async Task<IReadOnlyList<FaceReviewReadModel>> GetFaceReviewsByIdsAsync(
        IReadOnlyCollection<Guid> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (faceIds is null || faceIds.Count == 0)
        {
            return [];
        }

        var distinct = faceIds.Where(static id => id != Guid.Empty).Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var parameters = distinct.Select((_, index) => $"$face{index}").ToArray();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT fd.face_id, fd.media_id, fd.detection_key, fd.bounding_box_json,
                   fd.suggested_identity_id, si.profile_id AS suggested_profile_id, sp.display_name AS suggested_profile_name,
                   fd.confirmed_identity_id, ci.profile_id AS confirmed_profile_id, cp.display_name AS confirmed_profile_name,
                   fd.confidence, fd.decision_state, fd.model_id, fd.model_version,
                   fd.created_at_ms, fd.updated_at_ms, fd.row_version,
                   fd.sampled_timestamp_ms, fd.suggested_candidates_json
            FROM face_detections fd
            JOIN media a ON a.media_id = fd.media_id AND a.state = 'ACTIVE'
            LEFT JOIN identities si ON fd.suggested_identity_id = si.identity_id
            LEFT JOIN profiles sp ON si.profile_id = sp.profile_id
            LEFT JOIN identities ci ON fd.confirmed_identity_id = ci.identity_id
            LEFT JOIN profiles cp ON ci.profile_id = cp.profile_id
            WHERE fd.face_id IN ({string.Join(", ", parameters)});
            """;
        for (var index = 0; index < distinct.Length; index++)
        {
            command.Parameters.AddWithValue(parameters[index], DbGuid.Format(distinct[index]));
        }

        var list = new List<FaceReviewReadModel>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(ReadFaceReview(reader));
            }
        }

        return await ResolveIdentityCandidateProfilesAsync(connection, list, cancellationToken)
            .ConfigureAwait(false);
    }

    private static FaceReviewReadModel ReadFaceReview(SqliteDataReader reader)
    {
        var faceId = DbGuid.Parse(reader.GetString(0));
        var assetId = DbGuid.Parse(reader.GetString(1));
        var detectionKey = reader.GetString(2);
        var bboxJson = reader.GetString(3);
        var suggestedIdentityId = reader.IsDBNull(4) ? (Guid?)null : DbGuid.Parse(reader.GetString(4));
        var suggestedProfileId = reader.IsDBNull(5) ? (Guid?)null : DbGuid.Parse(reader.GetString(5));
        var suggestedProfileName = reader.IsDBNull(6) ? null : reader.GetString(6);
        var confirmedIdentityId = reader.IsDBNull(7) ? (Guid?)null : DbGuid.Parse(reader.GetString(7));
        var confirmedProfileId = reader.IsDBNull(8) ? (Guid?)null : DbGuid.Parse(reader.GetString(8));
        var confirmedProfileName = reader.IsDBNull(9) ? null : reader.GetString(9);
        var confidence = reader.IsDBNull(10) ? (double?)null : reader.GetDouble(10);
        var decision = DbEnum.ParseFaceDecisionState(reader.GetString(11));
        var modelId = reader.GetString(12);
        var modelVersion = reader.GetString(13);
        var createdAt = DbTime.Parse(reader.GetInt64(14));
        var updatedAt = DbTime.Parse(reader.GetInt64(15));
        var rowVersion = reader.GetInt64(16);
        var sampledTimestamp = reader.IsDBNull(17) ? (long?)null : reader.GetInt64(17);
        var evidence = FaceSuggestionEvidenceV1.TryParse(reader.IsDBNull(18) ? null : reader.GetString(18));

        var candidates = evidence is null
            ? []
            : evidence.Candidates
                .Where(entry => entry.Similarity >= evidence.Threshold)
                .OrderByDescending(static entry => entry.Similarity)
                .ThenBy(static entry => entry.IdentityId.ToString("D"), StringComparer.Ordinal)
                .Take(MaximumIdentityCandidates)
                .Select((entry, index) => new FaceIdentityCandidate(
                    entry.IdentityId,
                    ProfileId: null,
                    ProfileDisplayName: null,
                    entry.Similarity,
                    Rank: index + 1))
                .ToArray();

        if (decision != FaceDecisionState.Confirmed)
        {
            confirmedIdentityId = null;
            confirmedProfileId = null;
            confirmedProfileName = null;
        }

        return new FaceReviewReadModel(
            faceId,
            assetId,
            detectionKey,
            bboxJson,
            suggestedIdentityId,
            suggestedProfileId,
            suggestedProfileName,
            confirmedIdentityId,
            confirmedProfileId,
            confirmedProfileName,
            confidence,
            decision,
            modelId,
            modelVersion,
            createdAt,
            updatedAt,
            rowVersion,
            sampledTimestamp,
            candidates,
            evidence?.BankSignature);
    }

    private static async Task<IReadOnlyList<FaceReviewReadModel>> ResolveIdentityCandidateProfilesAsync(
        SqliteConnection connection,
        List<FaceReviewReadModel> rows,
        CancellationToken cancellationToken)
    {
        var identityIds = rows
            .SelectMany(static row => row.Candidates)
            .Select(static candidate => candidate.IdentityId)
            .Distinct()
            .ToArray();
        if (identityIds.Length == 0)
        {
            return rows;
        }

        var parameters = identityIds
            .Select((_, index) => $"$identity{index}")
            .ToArray();

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.identity_id, i.profile_id, p.display_name
            FROM identities i
            JOIN profiles p ON p.profile_id = i.profile_id
            WHERE i.is_active = 1
              AND p.trashed_at_ms IS NULL
              AND p.display_name IS NOT NULL
              AND i.identity_id IN ({string.Join(", ", parameters)});
            """;
        for (var index = 0; index < identityIds.Length; index++)
        {
            command.Parameters.AddWithValue(parameters[index], DbGuid.Format(identityIds[index]));
        }

        var byIdentity = new Dictionary<Guid, (Guid ProfileId, string DisplayName)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                byIdentity[DbGuid.Parse(reader.GetString(0))] =
                    (DbGuid.Parse(reader.GetString(1)), reader.GetString(2));
            }
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Candidates.Count == 0)
            {
                continue;
            }

            var resolved = row.Candidates
                .Where(candidate => byIdentity.ContainsKey(candidate.IdentityId))
                .Select((candidate, rank) =>
                {
                    var profile = byIdentity[candidate.IdentityId];
                    return candidate with
                    {
                        ProfileId = profile.ProfileId,
                        ProfileDisplayName = profile.DisplayName,
                        Rank = rank + 1,
                    };
                })
                .ToArray();

            rows[index] = row with { IdentityCandidates = resolved };
        }

        return rows;
    }

    private const string AuthoritativeSampleSource =
        """
        FROM identity_samples s
        JOIN identities i ON s.identity_id = i.identity_id AND i.is_active = 1
        JOIN profiles p ON i.profile_id = p.profile_id
        JOIN face_detections f ON s.face_id = f.face_id
        WHERE p.kind = 'NORMAL'
          AND p.trashed_at_ms IS NULL
          AND f.decision_state = 'CONFIRMED'
          AND f.confirmed_identity_id = s.identity_id
        """;

    public async Task<EmbeddingSpaceInventory> ListPopulatedEmbeddingSpacesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT DISTINCT s.embedding_space_key
            {AuthoritativeSampleSource}
            ORDER BY s.embedding_space_key;
            """;

        var spaces = new List<EmbeddingSpaceKey>();
        var unreadable = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var raw = reader.GetString(0);
            if (EmbeddingSpaceKey.TryParse(raw, out var space))
            {
                spaces.Add(space);
            }
            else
            {

                unreadable.Add(raw);
            }
        }

        return new EmbeddingSpaceInventory(spaces, unreadable);
    }

    public async Task<IReadOnlyList<IdentitySampleRecord>> LoadAuthoritativeSamplesAsync(
        EmbeddingSpaceKey space,
        CancellationToken cancellationToken = default)
    {
        EmbeddingSpaceKey.EnsureValid(space, nameof(space));

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            WITH ranked AS (
                SELECT s.identity_sample_id, s.identity_id, i.profile_id, s.face_id,
                       f.media_id, s.embedding, s.model_id, s.model_version,
                       s.confirmed_at_ms,
                       ROW_NUMBER() OVER (
                           PARTITION BY s.identity_id
                           ORDER BY COALESCE(f.confidence, 0) DESC,
                                    s.confirmed_at_ms DESC, s.identity_sample_id) AS sample_rank
                {AuthoritativeSampleSource}
                  AND s.embedding_space_key = $space
            )
            SELECT identity_sample_id, identity_id, profile_id, face_id, media_id,
                   embedding, model_id, model_version, confirmed_at_ms
            FROM ranked WHERE sample_rank <= 32
            ORDER BY profile_id, identity_id, sample_rank;
            """;
        command.Parameters.AddWithValue("$space", space.Canonical);

        return await ReadSampleRecordsAsync(command, space, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IdentitySampleSummary>> ListIdentitySampleSummariesAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT s.identity_sample_id,
                   s.identity_id,
                   i.profile_id,
                   s.face_id,
                   f.media_id,
                   s.embedding_space_key,
                   s.model_id,
                   s.model_version,
                   s.confirmed_at_ms
            {AuthoritativeSampleSource}
              AND i.profile_id = $profileId
            ORDER BY s.confirmed_at_ms, s.identity_sample_id;
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        var summaries = new List<IdentitySampleSummary>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!EmbeddingSpaceKey.TryParse(reader.GetString(5), out var space))
            {

                continue;
            }

            summaries.Add(new IdentitySampleSummary(
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1)),
                DbGuid.Parse(reader.GetString(2)),
                DbGuid.Parse(reader.GetString(3)),
                DbGuid.Parse(reader.GetString(4)),
                space,
                reader.GetString(6),
                reader.GetString(7),
                DbTime.Parse(reader.GetInt64(8))));
        }

        return summaries;
    }

    private static async Task<IReadOnlyList<IdentitySampleRecord>> ReadSampleRecordsAsync(
        SqliteCommand command,
        EmbeddingSpaceKey space,
        CancellationToken cancellationToken)
    {
        var records = new List<IdentitySampleRecord>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new IdentitySampleRecord(
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1)),
                DbGuid.Parse(reader.GetString(2)),
                DbGuid.Parse(reader.GetString(3)),
                DbGuid.Parse(reader.GetString(4)),
                reader.GetFieldValue<byte[]>(5),
                space,
                reader.GetString(6),
                reader.GetString(7),
                DbTime.Parse(reader.GetInt64(8))));
        }

        return records;
    }
}
