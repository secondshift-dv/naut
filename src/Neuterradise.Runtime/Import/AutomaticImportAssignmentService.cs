using System.Security.Cryptography;
using System.Text;
using Neuterradise.App.Faces;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Import;

/// <summary>
/// Resolves one import unit from durable duplicate ownership plus persisted People evidence.
/// Folder/file names are never identity evidence. Unknown faces are clustered only by compatible
/// SFace embeddings from this import batch.
/// </summary>
public sealed class AutomaticImportAssignmentService
{
    private readonly CatalogDb _catalog;
    private readonly AutomaticMediaAssignmentPolicy _policy;

    public AutomaticImportAssignmentService(
        CatalogDb catalog,
        AutomaticMediaAssignmentPolicy? policy = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _policy = policy ?? new AutomaticMediaAssignmentPolicy();
    }

    public async Task<ImportAssignmentAssessment> AssessAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
            throw new ArgumentException("Import unit id cannot be empty.", nameof(unitId));

        await using var connection = await _catalog.ConnectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var items = new Dictionary<Guid, ItemEvidence>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT import_item_id, COALESCE(reused_media_id, candidate_media_id)
                FROM import_items
                WHERE import_unit_id = $unitId
                  AND disposition IN ('INCLUDED', 'REUSED')
                  AND COALESCE(reused_media_id, candidate_media_id) IS NOT NULL
                ORDER BY created_at_ms, import_item_id;
                """;
            command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var itemId = DbGuid.Parse(reader.GetString(0));
                items[itemId] = new ItemEvidence(itemId, DbGuid.Parse(reader.GetString(1)));
            }
        }

        if (items.Count == 0)
            return new ImportAssignmentAssessment(
                ImportAssignmentDecision.Unknown, [], 0, 0);

        var ownersByItem = items.Keys.ToDictionary(static id => id, static _ => new HashSet<Guid>());
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT DISTINCT ii.import_item_id, pm.profile_id
                FROM import_items ii
                JOIN profile_media pm
                  ON pm.media_id = COALESCE(ii.reused_media_id, ii.candidate_media_id)
                 AND pm.relation_type = 'OWNER'
                JOIN profiles p
                  ON p.profile_id = pm.profile_id
                 AND p.kind = 'NORMAL'
                 AND p.trashed_at_ms IS NULL
                WHERE ii.import_unit_id = $unitId
                  AND ii.disposition IN ('INCLUDED', 'REUSED');
                """;
            command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var itemId = DbGuid.Parse(reader.GetString(0));
                if (ownersByItem.TryGetValue(itemId, out var owners))
                    owners.Add(DbGuid.Parse(reader.GetString(1)));
            }
        }

        var faces = new List<FaceObservation>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT ii.import_item_id,
                       fd.face_id,
                       fd.embedding,
                       fd.embedding_space_key,
                       fd.decision_state,
                       confirmed.profile_id,
                       fd.suggested_identity_id,
                       suggested.profile_id,
                       fd.suggested_candidates_json
                FROM import_items ii
                JOIN face_detections fd
                  ON fd.media_id = COALESCE(ii.reused_media_id, ii.candidate_media_id)
                LEFT JOIN identities confirmed_identity
                  ON confirmed_identity.identity_id = fd.confirmed_identity_id
                 AND confirmed_identity.is_active = 1
                LEFT JOIN profiles confirmed
                  ON confirmed.profile_id = confirmed_identity.profile_id
                 AND confirmed.kind = 'NORMAL'
                 AND confirmed.trashed_at_ms IS NULL
                LEFT JOIN identities suggested_identity
                  ON suggested_identity.identity_id = fd.suggested_identity_id
                 AND suggested_identity.is_active = 1
                LEFT JOIN profiles suggested
                  ON suggested.profile_id = suggested_identity.profile_id
                 AND suggested.kind = 'NORMAL'
                 AND suggested.trashed_at_ms IS NULL
                WHERE ii.import_unit_id = $unitId
                  AND ii.disposition IN ('INCLUDED', 'REUSED')
                ORDER BY ii.import_item_id, fd.face_id;
                """;
            command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var itemId = DbGuid.Parse(reader.GetString(0));
                if (!items.ContainsKey(itemId))
                    continue;

                var faceId = DbGuid.Parse(reader.GetString(1));
                var decisionState = reader.GetString(4);
                Guid? profileId = null;
                double? similarity = null;
                var confirmed = false;

                if (string.Equals(decisionState, "CONFIRMED", StringComparison.Ordinal)
                    && !reader.IsDBNull(5))
                {
                    profileId = DbGuid.Parse(reader.GetString(5));
                    confirmed = true;
                }
                else if (!reader.IsDBNull(6) && !reader.IsDBNull(7) && !reader.IsDBNull(8))
                {
                    var identityId = DbGuid.Parse(reader.GetString(6));
                    var suggestion = FaceSuggestionEvidenceV1.TryParse(reader.GetString(8));
                    var ordered = (suggestion?.Candidates ?? [])
                        .OrderByDescending(static candidate => candidate.Similarity)
                        .ThenBy(static candidate => candidate.IdentityId)
                        .ToArray();
                    var top = ordered.FirstOrDefault();
                    var second = ordered.Skip(1).FirstOrDefault();
                    if (top is not null
                        && top.IdentityId == identityId
                        && top.Similarity >= AutomaticMediaAssignmentPolicy.StrongFaceSimilarity
                        && (second is null
                            || top.Similarity - second.Similarity
                                >= AutomaticMediaAssignmentPolicy.StrongFaceMargin))
                    {
                        profileId = DbGuid.Parse(reader.GetString(7));
                        similarity = top.Similarity;
                    }
                }

                EmbeddingSpaceKey? space = null;
                float[]? embedding = null;
                if (!reader.IsDBNull(2)
                    && !reader.IsDBNull(3)
                    && EmbeddingSpaceKey.TryParse(reader.GetString(3), out var parsedSpace))
                {
                    var blob = reader.GetFieldValue<byte[]>(2);
                    if (FaceEmbedding.TryFromBlob(blob, parsedSpace, out var parsedEmbedding, out _))
                    {
                        space = parsedSpace;
                        embedding = parsedEmbedding;
                    }
                }

                faces.Add(new FaceObservation(
                    itemId, faceId, profileId, similarity, confirmed, space, embedding));
            }
        }

        var evidence = new List<ImportAssignmentEvidenceItem>(items.Count);
        foreach (var item in items.Values)
        {
            var owners = ownersByItem[item.ItemId];
            var durableOwner = owners.Count == 1 ? owners.Single() : (Guid?)null;

            var itemFaces = faces.Where(face => face.ItemId == item.ItemId).ToArray();
            var strongProfiles = itemFaces
                .Where(static face => face.ProfileId.HasValue)
                .Select(static face => face.ProfileId!.Value)
                .Distinct()
                .ToArray();
            var faceAmbiguous = strongProfiles.Length > 1 || owners.Count > 1;
            var faceProfile = strongProfiles.Length == 1 ? strongProfiles[0] : (Guid?)null;
            var faceSimilarity = faceProfile is { } selected
                ? itemFaces.Where(face => face.ProfileId == selected && face.Similarity.HasValue)
                    .Select(static face => face.Similarity!.Value)
                    .DefaultIfEmpty()
                    .Max()
                : (double?)null;
            var faceConfirmed = faceProfile is { } confirmedProfile
                && itemFaces.Any(face => face.ProfileId == confirmedProfile && face.IsConfirmed);

            evidence.Add(new ImportAssignmentEvidenceItem(
                item.ItemId,
                durableOwner,
                faceProfile,
                faceSimilarity,
                faceConfirmed,
                faceAmbiguous));
        }

        var clusters = BuildClusters(items.Values, faces, ownersByItem);
        var decision = _policy.EvaluateBatch(evidence);
        var supported = evidence.Count(item => _policy.Resolve(
            null,
            item.DurableOwnerProfileId,
            item.FaceProfileId,
            item.FaceSimilarity,
            item.FaceIsConfirmed,
            item.FaceIsAmbiguous).IsAuthoritative);

        await PersistClustersAsync(
            unitId,
            decision.IsAuthoritative ? [] : clusters,
            cancellationToken).ConfigureAwait(false);

        return new ImportAssignmentAssessment(
            decision,
            clusters,
            items.Count,
            supported);
    }

    private static IReadOnlyList<ImportAssignmentCluster> BuildClusters(
        IEnumerable<ItemEvidence> items,
        IReadOnlyList<FaceObservation> faces,
        IReadOnlyDictionary<Guid, HashSet<Guid>> ownersByItem)
    {
        var result = new List<ImportAssignmentCluster>();

        foreach (var group in faces
                     .Where(static face => face.ProfileId.HasValue)
                     .GroupBy(static face => face.ProfileId!.Value)
                     .OrderBy(static group => group.Key))
        {
            var itemIds = group.Select(static face => face.ItemId).Distinct().Order().ToArray();
            var similarity = group.Where(static face => face.Similarity.HasValue)
                .Select(static face => face.Similarity!.Value)
                .DefaultIfEmpty()
                .Max();
            result.Add(new ImportAssignmentCluster(
                $"profile:{group.Key:D}",
                group.Key,
                ImportAssignmentClusterEvidence.FaceMatch,
                itemIds,
                similarity > 0 ? similarity : null));
        }

        var conflictingItems = items
            .Where(item =>
            {
                var faceProfiles = faces
                    .Where(face => face.ItemId == item.ItemId && face.ProfileId.HasValue)
                    .Select(static face => face.ProfileId!.Value)
                    .Distinct()
                    .Count();
                return faceProfiles > 1 || ownersByItem[item.ItemId].Count > 1;
            })
            .Select(static item => item.ItemId)
            .Order()
            .ToArray();
        if (conflictingItems.Length > 0)
        {
            result.Add(new ImportAssignmentCluster(
                StableKey("conflict", conflictingItems),
                null,
                ImportAssignmentClusterEvidence.Conflicting,
                conflictingItems));
        }

        var unknownFaces = faces
            .Where(static face => !face.ProfileId.HasValue
                && face.Space.HasValue
                && face.Embedding is { Length: > 0 })
            .OrderBy(static face => face.FaceId)
            .ToArray();
        var unknownClusters = new List<UnknownFaceCluster>();
        foreach (var face in unknownFaces)
        {
            UnknownFaceCluster? best = null;
            var bestScore = double.NegativeInfinity;
            foreach (var cluster in unknownClusters)
            {
                if (cluster.Space != face.Space!.Value)
                    continue;
                var score = FaceEmbedding.CosineSimilarity(
                    cluster.Space,
                    cluster.RepresentativeEmbedding,
                    face.Space.Value,
                    face.Embedding!);
                if (score >= AutomaticMediaAssignmentPolicy.StrongFaceSimilarity && score > bestScore)
                {
                    best = cluster;
                    bestScore = score;
                }
            }

            if (best is null)
            {
                unknownClusters.Add(new UnknownFaceCluster(
                    face.Space!.Value,
                    face.Embedding!,
                    [face.FaceId],
                    [face.ItemId]));
            }
            else
            {
                best.FaceIds.Add(face.FaceId);
                best.ItemIds.Add(face.ItemId);
            }
        }

        foreach (var cluster in unknownClusters)
        {
            var itemIds = cluster.ItemIds.Distinct().Order().ToArray();
            result.Add(new ImportAssignmentCluster(
                StableKey("unknown-face", cluster.FaceIds),
                null,
                ImportAssignmentClusterEvidence.UnknownFace,
                itemIds));
        }

        var itemsWithFaces = faces.Select(static face => face.ItemId).ToHashSet();
        var noFaceItems = items
            .Where(item => !itemsWithFaces.Contains(item.ItemId))
            .Select(static item => item.ItemId)
            .Order()
            .ToArray();
        if (noFaceItems.Length > 0)
        {
            result.Add(new ImportAssignmentCluster(
                StableKey("no-face", noFaceItems),
                null,
                ImportAssignmentClusterEvidence.NoFace,
                noFaceItems));
        }

        return result;
    }

    private async Task PersistClustersAsync(
        Guid unitId,
        IReadOnlyList<ImportAssignmentCluster> clusters,
        CancellationToken cancellationToken)
    {
        var now = DbTime.Format(DateTimeOffset.UtcNow);
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);

        var stalePendingClusterIds = new HashSet<Guid>();
        await using (var pending = transaction.CreateCommand("""
            SELECT cluster_id FROM import_assignment_clusters
            WHERE import_unit_id = $unitId AND state = 'PENDING';
            """))
        {
            pending.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var reader = await pending.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                stalePendingClusterIds.Add(DbGuid.Parse(reader.GetString(0)));
        }

        foreach (var cluster in clusters)
        {
            var clusterId = StableClusterId(unitId, cluster.Key);
            await using var command = transaction.CreateCommand("""
                INSERT INTO import_assignment_clusters(
                    cluster_id, import_unit_id, cluster_key, source_directory,
                    candidate_profile_id, evidence_kind, state, created_at_ms, updated_at_ms)
                VALUES ($clusterId, $unitId, $clusterKey, NULL,
                        $candidateProfileId, $evidenceKind, 'PENDING', $now, $now)
                ON CONFLICT(import_unit_id, cluster_key) DO UPDATE SET
                    candidate_profile_id = excluded.candidate_profile_id,
                    evidence_kind = excluded.evidence_kind,
                    updated_at_ms = excluded.updated_at_ms,
                    row_version = import_assignment_clusters.row_version + 1
                WHERE import_assignment_clusters.state = 'PENDING';
                """);
            command.Parameters.AddWithValue("$clusterId", DbGuid.Format(clusterId));
            command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            command.Parameters.AddWithValue("$clusterKey", cluster.Key);
            command.Parameters.AddWithValue(
                "$candidateProfileId",
                cluster.CandidateProfileId is { } candidate ? DbGuid.Format(candidate) : DBNull.Value);
            command.Parameters.AddWithValue("$evidenceKind", Format(cluster.Evidence));
            command.Parameters.AddWithValue("$now", now);

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                continue;

            stalePendingClusterIds.Remove(clusterId);
            await using (var clear = transaction.CreateCommand(
                "DELETE FROM import_assignment_cluster_items WHERE cluster_id = $clusterId;"))
            {
                clear.Parameters.AddWithValue("$clusterId", DbGuid.Format(clusterId));
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var itemId in cluster.ItemIds.Distinct())
            {
                await using var member = transaction.CreateCommand("""
                    INSERT INTO import_assignment_cluster_items(cluster_id, import_item_id)
                    VALUES ($clusterId, $itemId);
                    """);
                member.Parameters.AddWithValue("$clusterId", DbGuid.Format(clusterId));
                member.Parameters.AddWithValue("$itemId", DbGuid.Format(itemId));
                await member.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var staleClusterId in stalePendingClusterIds)
        {
            await using var remove = transaction.CreateCommand("""
                DELETE FROM import_assignment_clusters
                WHERE cluster_id = $clusterId AND state = 'PENDING';
                """);
            remove.Parameters.AddWithValue("$clusterId", DbGuid.Format(staleClusterId));
            await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Format(ImportAssignmentClusterEvidence evidence) => evidence switch
    {
        ImportAssignmentClusterEvidence.FaceMatch => "FACE_MATCH",
        ImportAssignmentClusterEvidence.UnknownFace => "UNKNOWN_FACE",
        ImportAssignmentClusterEvidence.Conflicting => "CONFLICTING",
        ImportAssignmentClusterEvidence.NoFace => "NO_FACE",
        _ => throw new ArgumentOutOfRangeException(nameof(evidence), evidence, null)
    };

    private static Guid StableClusterId(Guid unitId, string clusterKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{unitId:D}|{clusterKey}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string StableKey(string prefix, IEnumerable<Guid> ids)
    {
        var normalized = string.Join("|", ids.Distinct().Order().Select(static id => id.ToString("D")));
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{prefix}|{normalized}")));
        return $"{prefix}:{hash[..16]}";
    }

    private sealed record ItemEvidence(Guid ItemId, Guid MediaId);

    private sealed record FaceObservation(
        Guid ItemId,
        Guid FaceId,
        Guid? ProfileId,
        double? Similarity,
        bool IsConfirmed,
        EmbeddingSpaceKey? Space,
        float[]? Embedding);

    private sealed class UnknownFaceCluster(
        EmbeddingSpaceKey space,
        float[] representativeEmbedding,
        List<Guid> faceIds,
        List<Guid> itemIds)
    {
        public EmbeddingSpaceKey Space { get; } = space;
        public float[] RepresentativeEmbedding { get; } = representativeEmbedding;
        public List<Guid> FaceIds { get; } = faceIds;
        public List<Guid> ItemIds { get; } = itemIds;
    }
}
