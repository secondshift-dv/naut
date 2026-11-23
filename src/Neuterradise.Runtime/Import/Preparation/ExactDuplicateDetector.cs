using Microsoft.Data.Sqlite;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Import.Preparation;

public enum ExactDuplicateMatchKind
{
    None,
    ActiveDuplicate,
    CandidateDuplicate,
    TrashedDuplicate
}

public sealed record ExactDuplicateMatch(
    Guid ExistingMediaId,
    MediaState MediaState,
    long ByteLength,
    string Sha256,
    string? CurrentManagedFileName,
    string? CurrentManagedRelativePath,
    Guid? OwnerProfileId,
    string? OwnerProfileDisplayName,
    Guid? CandidateImportUnitId = null,
    string? CandidateSourcePath = null)
{
    public bool IsLibraryReuseEligible => MediaState == MediaState.Active;
    public bool IsCandidateDuplicate => MediaState == MediaState.Candidate;
    public bool IsTrashedDuplicate => MediaState == MediaState.Trashed;
}

public sealed record ExactDuplicateResult(
    bool IsExactDuplicate,
    ExactDuplicateMatchKind MatchKind,
    ExactDuplicateMatch? Match)
{
    public bool IsLibraryReuseEligible => MatchKind == ExactDuplicateMatchKind.ActiveDuplicate;
    public bool IsCandidateDuplicate => MatchKind == ExactDuplicateMatchKind.CandidateDuplicate;
    public bool IsTrashedDuplicate => MatchKind == ExactDuplicateMatchKind.TrashedDuplicate;

    public static ExactDuplicateResult None { get; } =
        new(false, ExactDuplicateMatchKind.None, null);
}

public sealed class ExactDuplicateDetector
{
    private readonly CatalogConnectionFactory _connectionFactory;

    public ExactDuplicateDetector(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
    }

    public async Task<bool> HasActiveWithLengthAsync(
        long byteLength, Guid excludeMediaId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM media
                          WHERE state = 'ACTIVE' AND byte_length = $length AND media_id <> $excluded);
            """;
        command.Parameters.AddWithValue("$length", byteLength);
        command.Parameters.AddWithValue("$excluded", DbGuid.Format(excludeMediaId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public async Task<ExactDuplicateResult> DetectDuplicateAsync(
        string sha256,
        long byteLength,
        Guid? excludeMediaId = null,
        string? bundleSha256 = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sha256) || byteLength < 0)
        {
            return ExactDuplicateResult.None;
        }

        var normalizedSha = sha256.Trim().ToLowerInvariant();
        if (normalizedSha.Length != 64)
        {
            return ExactDuplicateResult.None;
        }

        var normalizedBundleSha = string.IsNullOrWhiteSpace(bundleSha256)
            ? null
            : bundleSha256.Trim().ToLowerInvariant();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.media_id, a.state, a.byte_length, a.sha256,
                   a.current_managed_file_name, a.current_managed_relative_path,
                   pa.profile_id, p.display_name,
                   ii.import_unit_id, ii.source_path
            FROM media a
            LEFT JOIN profile_media pa ON pa.media_id = a.media_id AND pa.relation_type = $ownerRelation
            LEFT JOIN profiles p ON p.profile_id = pa.profile_id
            LEFT JOIN import_items ii ON ii.candidate_media_id = a.media_id
            WHERE (
                    ($bundleSha IS NOT NULL AND a.bundle_sha256 = $bundleSha
                        AND a.dependency_discovery_state = 'COMPLETE'
                        AND a.dependency_status IN ('COMPLETE','SELF_CONTAINED'))
                    OR
                    ($bundleSha IS NULL AND a.bundle_sha256 IS NULL
                        AND a.sha256 = $sha256 AND a.byte_length = $byteLength)
                  )
              AND a.state IN ($activeState, $candidateState, $trashedState)
              AND ($excludeMediaId IS NULL OR a.media_id <> $excludeMediaId)
            ORDER BY 
              (CASE 
                 WHEN a.state = $activeState THEN 0 
                 WHEN a.state = $candidateState THEN 1 
                 ELSE 2 
               END),
               a.added_to_library_at_ms ASC,
               a.created_at_ms ASC, a.media_id ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$bundleSha", (object?)normalizedBundleSha ?? DBNull.Value);
        command.Parameters.AddWithValue("$sha256", normalizedSha);
        command.Parameters.AddWithValue("$byteLength", byteLength);
        command.Parameters.AddWithValue("$ownerRelation", DbEnum.Format(ProfileMediaRelation.Owner));
        command.Parameters.AddWithValue("$activeState", DbEnum.Format(MediaState.Active));
        command.Parameters.AddWithValue("$candidateState", DbEnum.Format(MediaState.Candidate));
        command.Parameters.AddWithValue("$trashedState", DbEnum.Format(MediaState.Trashed));
        command.Parameters.AddWithValue(
            "$excludeMediaId",
            excludeMediaId.HasValue ? DbGuid.Format(excludeMediaId.Value) : DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ExactDuplicateResult.None;
        }

        var state = DbEnum.ParseMediaState(reader.GetString(1));
        var match = new ExactDuplicateMatch(
            DbGuid.Parse(reader.GetString(0)),
            state,
            reader.GetInt64(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : DbGuid.Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : DbGuid.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9));
        var kind = state switch
        {
            MediaState.Active => ExactDuplicateMatchKind.ActiveDuplicate,
            MediaState.Candidate => ExactDuplicateMatchKind.CandidateDuplicate,
            _ => ExactDuplicateMatchKind.TrashedDuplicate,
        };
        return new ExactDuplicateResult(true, kind, match);
    }
}
