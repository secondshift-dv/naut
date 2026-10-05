using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Profiles;

public sealed record ProfileMediaCandidate(
    Guid MediaId,
    Guid MediaAssetId,
    MediaAssetRole Role,
    MediaType MediaType,
    string RelativePath,
    string DisplayName,
    long? SourceTimestampMs,
    long? DurationMs,
    double Score,
    int Rank);

public sealed record ProfileMediaCandidates(
    IReadOnlyList<ProfileMediaCandidate> Covers,
    IReadOnlyList<ProfileMediaCandidate> BannerHovers);

/// <summary>Ranks existing, validated MediaAssets. This operation does not create media bytes.</summary>
public sealed class ProfileMediaCandidateReads
{
    private readonly CatalogDb _catalog;

    public ProfileMediaCandidateReads(CatalogDb catalog) => _catalog = catalog;

    public const int MaximumCoverCandidates = 6;
    public const int MaximumBannerCandidates = 12;

    public Task<ProfileMediaCandidates> ReadAllEligibleAsync(
        Guid profileId,
        CancellationToken ct = default,
        Guid? importUnitId = null) =>
        ReadAsync(profileId, ct, importUnitId, int.MaxValue, int.MaxValue);

    public async Task<ProfileMediaCandidates> ReadAsync(
        Guid profileId,
        CancellationToken ct = default,
        Guid? importUnitId = null,
        int coverLimit = MaximumCoverCandidates,
        int bannerLimit = MaximumBannerCandidates)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ma.media_asset_id, ma.media_id, ma.role, m.media_type,
                   ma.relative_path, COALESCE(m.current_managed_file_name, m.original_file_name, m.media_id),
                   ma.source_timestamp_ms, ma.duration_ms, ma.pixel_width, ma.pixel_height,
                   COALESCE((SELECT MAX(CASE WHEN identity.profile_id = $profile THEN 2.0 ELSE 1.0 END
                                     + COALESCE(face.confidence, 0))
                             FROM face_detections face
                             LEFT JOIN identities identity ON identity.identity_id = face.confirmed_identity_id
                             WHERE face.media_id = m.media_id), 0),
                   m.created_at_ms
            FROM media m
            JOIN media_assets ma ON ma.media_id = m.media_id
            WHERE m.state = 'ACTIVE'
              AND m.trashed_at_ms IS NULL AND ma.state = 'READY'
              AND EXISTS (SELECT 1 FROM profile_media pm
                          WHERE pm.profile_id = $profile AND pm.media_id = m.media_id
                            AND (pm.publication_import_unit_id IS NULL
                                 OR ($unit IS NOT NULL AND pm.publication_import_unit_id = $unit)))
              AND EXISTS (SELECT 1 FROM profiles p WHERE p.profile_id = $profile
                          AND p.trashed_at_ms IS NULL)
            ;
            """;
        command.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$unit", importUnitId is { } unit
            ? DbGuid.Format(unit) : DBNull.Value);
        var all = new List<(ProfileMediaCandidate Candidate, long CreatedAt)>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var role = DbEnum.ParseMediaAssetRole(reader.GetString(2));
            var mediaType = DbEnum.ParseMediaType(reader.GetString(3));
            if (role == MediaAssetRole.Thumbnail && mediaType is not (MediaType.Image or MediaType.Video)) continue;
            if (role == MediaAssetRole.Hover && mediaType != MediaType.Video) continue;
            if (role is not (MediaAssetRole.Thumbnail or MediaAssetRole.Hover)) continue;
            var relativePath = reader.GetString(4);
            if (!ArtifactExists(relativePath)) continue;
            var width = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);
            var height = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
            var resolution = Math.Min(width, height);
            var score = Convert.ToDouble(reader.GetValue(10), System.Globalization.CultureInfo.InvariantCulture)
                * 100 + Math.Min(resolution, 1600) / 40d;
            var candidate = new ProfileMediaCandidate(
                DbGuid.Parse(reader.GetString(1)), DbGuid.Parse(reader.GetString(0)), role,
                mediaType, relativePath, reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7), score, 0);
            all.Add((candidate, reader.GetInt64(11)));
        }

        static List<ProfileMediaCandidate> Rank(
            IEnumerable<(ProfileMediaCandidate Candidate, long CreatedAt)> source,
            int limit)
        {
            var selected = source
                .OrderByDescending(static item => item.Candidate.Score)
                .ThenBy(static item => item.CreatedAt)
                .ThenBy(static item => item.Candidate.MediaAssetId)
                .Take(Math.Max(0, limit));
            return selected
                .Select((item, index) => item.Candidate with { Rank = index + 1 })
                .ToList();
        }

        bool ArtifactExists(string relativePath)
        {
            try
            {
                var path = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, relativePath);
                return File.Exists(path);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
        }

        var thumbs = Rank(
            all.Where(static item => item.Candidate.Role == MediaAssetRole.Thumbnail),
            coverLimit);
        var hovers = Rank(
            all.Where(static item => item.Candidate.Role == MediaAssetRole.Hover),
            bannerLimit);
        return new ProfileMediaCandidates(thumbs, hovers);
    }
}
