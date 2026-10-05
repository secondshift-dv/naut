using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Import;

public sealed class ImportAssignmentReviewReads(CatalogDb catalog)
{
    private readonly CatalogDb _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public async Task<IReadOnlyList<ImportAssignmentReviewCluster>> GetPendingForUnknownProfileAsync(
        Guid unknownProfileId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ImportAssignmentReviewCluster>();
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.cluster_id,
                   c.source_directory,
                   c.candidate_profile_id,
                   p.display_name,
                   c.evidence_kind,
                   COUNT(DISTINCT COALESCE(ii.reused_media_id, ii.candidate_media_id)) AS member_count,
                   c.row_version
            FROM import_assignment_clusters c
            JOIN import_units iu ON iu.import_unit_id = c.import_unit_id
            JOIN import_assignment_cluster_items ci ON ci.cluster_id = c.cluster_id
            JOIN import_items ii ON ii.import_item_id = ci.import_item_id
            LEFT JOIN profiles p
              ON p.profile_id = c.candidate_profile_id
             AND p.kind = 'NORMAL'
             AND p.trashed_at_ms IS NULL
            WHERE iu.destination_profile_id = $profileId
              AND c.state = 'PENDING'
              AND COALESCE(ii.reused_media_id, ii.candidate_media_id) IS NOT NULL
            GROUP BY c.cluster_id, c.source_directory, c.candidate_profile_id,
                     p.display_name, c.evidence_kind, c.row_version
            ORDER BY c.created_at_ms, c.cluster_id;
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(unknownProfileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ImportAssignmentReviewCluster(
                DbGuid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : DbGuid.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt64(6)));
        }

        return result;
    }
}

public sealed record ImportAssignmentReviewCluster(
    Guid ClusterId,
    string? SourceDirectory,
    Guid? CandidateProfileId,
    string? CandidateProfileName,
    string EvidenceKind,
    int MemberCount,
    long RowVersion)
{
    public bool HasCandidate => CandidateProfileId is not null && !string.IsNullOrWhiteSpace(CandidateProfileName);
}
