using Neuterradise.App.Media.Model;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed class ProfileReads
{
    private readonly CatalogConnectionFactory _connectionFactory;

    public ProfileReads(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
    }

    public ProfileReads(CatalogConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<ProfileHeaderReadModel?> GetHeaderAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.profile_id, p.kind, p.display_name, p.unknown_sequence,
                   p.profile_storage_token, p.category_id, c.name AS category_name,
                   p.rating, p.is_favorite, p.overview, p.notes,
                   (SELECT identity_id FROM identities WHERE profile_id = p.profile_id AND is_active = 1 LIMIT 1) AS identity_id,
                   (SELECT ma.media_id FROM media_assets ma
                    WHERE ma.media_asset_id = pa.cover_media_asset_id) AS cover_source_media_id,
                   (SELECT ma.media_id FROM media_assets ma
                    WHERE ma.media_asset_id = pa.banner_media_asset_id) AS banner_source_media_id,
                   (
                       SELECT COUNT(*)
                       FROM profile_media pa
                       JOIN media a ON pa.media_id = a.media_id AND pa.publication_import_unit_id IS NULL
                       WHERE pa.profile_id = p.profile_id
                         AND pa.relation_type = 'OWNER'
                         AND a.state = 'ACTIVE'
                   ) AS active_owned_count,
                   p.created_at_ms, p.updated_at_ms, p.trashed_at_ms, p.row_version
            FROM profiles p
            LEFT JOIN categories c ON p.category_id = c.category_id
            LEFT JOIN profile_appearance pa ON p.profile_id = pa.profile_id
            WHERE p.profile_id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var kind = DbEnum.ParseProfileKind(reader.GetString(1));
        var displayName = reader.IsDBNull(2)
            ? (reader.IsDBNull(3) ? "Unknown" : $"Unknown {reader.GetInt64(3)}")
            : reader.GetString(2);
        var unknownSeq = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
        var storageToken = reader.IsDBNull(4) ? null : reader.GetString(4);
        var categoryId = reader.IsDBNull(5) ? null : reader.GetString(5);
        var categoryName = reader.IsDBNull(6) ? null : reader.GetString(6);
        var rating = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
        var isFavorite = reader.GetInt32(8) == 1;
        var overview = reader.IsDBNull(9) ? null : reader.GetString(9);
        var notes = reader.IsDBNull(10) ? null : reader.GetString(10);
        var identityId = reader.IsDBNull(11) ? (Guid?)null : DbGuid.Parse(reader.GetString(11));
        var coverMediaId = reader.IsDBNull(12) ? (Guid?)null : DbGuid.Parse(reader.GetString(12));
        var bannerMediaId = reader.IsDBNull(13) ? (Guid?)null : DbGuid.Parse(reader.GetString(13));
        var activeOwnedCount = reader.GetInt64(14);
        var createdAt = DbTime.Parse(reader.GetInt64(15));
        var updatedAt = DbTime.Parse(reader.GetInt64(16));
        var trashedAt = reader.IsDBNull(17) ? (DateTimeOffset?)null : DbTime.Parse(reader.GetInt64(17));
        var rowVersion = reader.GetInt64(18);
        var tags = await LoadTagsForProfileAsync(connection, profileId, cancellationToken).ConfigureAwait(false);

        return new ProfileHeaderReadModel(
            profileId,
            kind,
            displayName,
            storageToken,
            categoryId,
            categoryName,
            tags,
            rating,
            isFavorite,
            overview,
            notes,
            identityId,
            coverMediaId,
            bannerMediaId,
            unknownSeq,
            activeOwnedCount,
            createdAt,
            updatedAt,
            trashedAt,
            rowVersion);
    }

    public async Task<ProfileFolderReadModel?> GetFolderAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, kind, display_name, unknown_sequence,
                   profile_storage_token, current_managed_relative_path,
                   target_managed_relative_path, path_state,
                   reconciliation_operation_id, row_version
            FROM profiles
            WHERE profile_id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var kind = DbEnum.ParseProfileKind(reader.GetString(1));
        var displayName = reader.IsDBNull(2)
            ? (reader.IsDBNull(3) ? "Unknown" : $"Unknown {reader.GetInt64(3)}")
            : reader.GetString(2);
        var storageToken = reader.IsDBNull(4) ? null : reader.GetString(4);
        var currentPath = reader.IsDBNull(5) ? null : reader.GetString(5);
        var targetPath = reader.IsDBNull(6) ? null : reader.GetString(6);
        var pathState = DbEnum.ParseManagedPathState(reader.GetString(7));
        var operationId = reader.IsDBNull(8) ? null : reader.GetString(8);
        var rowVersion = reader.GetInt64(9);

        return new ProfileFolderReadModel(
            profileId,
            kind,
            displayName,
            storageToken,
            currentPath,
            targetPath,
            pathState,
            operationId,
            rowVersion);
    }

    public async Task<ProfileDetailReadModel?> GetDetailAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.profile_id, p.kind, p.display_name, p.unknown_sequence,
                   p.profile_storage_token, p.category_id, c.name AS category_name,
                   p.rating, p.is_favorite, p.overview, p.notes,
                   (SELECT identity_id FROM identities WHERE profile_id = p.profile_id AND is_active = 1 LIMIT 1) AS identity_id,
                   (
                       SELECT COUNT(*)
                       FROM identity_samples s
                       JOIN identities i ON s.identity_id = i.identity_id
                       WHERE i.profile_id = p.profile_id AND i.is_active = 1
                   ) AS identity_sample_count,
                   (SELECT ma.media_id FROM media_assets ma
                    WHERE ma.media_asset_id = pa.cover_media_asset_id) AS cover_source_media_id,
                   (SELECT ma.media_id FROM media_assets ma
                    WHERE ma.media_asset_id = pa.banner_media_asset_id) AS banner_source_media_id,
                   (
                       SELECT COUNT(*)
                       FROM profile_media pa
                       JOIN media a ON pa.media_id = a.media_id AND pa.publication_import_unit_id IS NULL
                       WHERE pa.profile_id = p.profile_id
                         AND pa.relation_type = 'OWNER'
                         AND a.state = 'ACTIVE'
                   ) AS active_owned_count,
                   pa.layout_preset_id, pa.overrides_json,
                   p.created_at_ms, p.updated_at_ms, p.trashed_at_ms, p.row_version,
                   (SELECT m.media_type FROM media_assets ma JOIN media m ON m.media_id = ma.media_id
                    WHERE ma.media_asset_id = pa.banner_media_asset_id) AS banner_media_type,
                   pa.cover_media_asset_id, pa.banner_media_asset_id,
                   (SELECT ma.relative_path FROM media_assets ma
                    WHERE ma.media_asset_id = pa.cover_media_asset_id) AS cover_asset_relative_path,
                   (SELECT ma.relative_path FROM media_assets ma
                    WHERE ma.media_asset_id = pa.banner_media_asset_id) AS banner_asset_relative_path,
                   pa.figure_media_id AS figure_media_id,
                   (SELECT relative_path FROM media_assets WHERE media_id = pa.figure_media_id AND role = 'THUMBNAIL' AND state = 'READY') AS figure_thumbnail_path,
                   (SELECT relative_path FROM media_assets WHERE media_id = pa.figure_media_id AND role = 'MODEL_RENDER' AND state = 'READY' AND contract_version = 1) AS figure_render_path,
                   (SELECT state FROM media_assets WHERE media_id = pa.figure_media_id AND role = 'MODEL_RENDER') AS figure_render_state,
                   (SELECT sha256 FROM media WHERE media_id = pa.figure_media_id) AS figure_source_sha256
            FROM profiles p
            LEFT JOIN categories c ON p.category_id = c.category_id
            LEFT JOIN profile_appearance pa ON p.profile_id = pa.profile_id
            WHERE p.profile_id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var kind = DbEnum.ParseProfileKind(reader.GetString(1));
        var displayName = reader.IsDBNull(2)
            ? (reader.IsDBNull(3) ? "Unknown" : $"Unknown {reader.GetInt64(3)}")
            : reader.GetString(2);
        var unknownSeq = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
        var storageToken = reader.IsDBNull(4) ? null : reader.GetString(4);
        var categoryId = reader.IsDBNull(5) ? null : reader.GetString(5);
        var categoryName = reader.IsDBNull(6) ? null : reader.GetString(6);
        var rating = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
        var isFavorite = reader.GetInt32(8) == 1;
        var overview = reader.IsDBNull(9) ? null : reader.GetString(9);
        var notes = reader.IsDBNull(10) ? null : reader.GetString(10);
        var identityId = reader.IsDBNull(11) ? (Guid?)null : DbGuid.Parse(reader.GetString(11));
        var sampleCount = reader.GetInt32(12);
        var coverMediaId = reader.IsDBNull(13) ? (Guid?)null : DbGuid.Parse(reader.GetString(13));
        var bannerMediaId = reader.IsDBNull(14) ? (Guid?)null : DbGuid.Parse(reader.GetString(14));
        var activeOwnedCount = reader.GetInt64(15);
        var layoutPresetId = reader.IsDBNull(16) ? null : reader.GetString(16);
        var overridesJson = reader.IsDBNull(17) ? null : reader.GetString(17);
        var createdAt = DbTime.Parse(reader.GetInt64(18));
        var updatedAt = DbTime.Parse(reader.GetInt64(19));
        var trashedAt = reader.IsDBNull(20) ? (DateTimeOffset?)null : DbTime.Parse(reader.GetInt64(20));
        var rowVersion = reader.GetInt64(21);
        var bannerMediaType = reader.IsDBNull(22)
            ? (MediaType?)null
            : DbEnum.ParseMediaType(reader.GetString(22));
        var coverMediaAssetId = reader.IsDBNull(23) ? (Guid?)null : DbGuid.Parse(reader.GetString(23));
        var bannerMediaAssetId = reader.IsDBNull(24) ? (Guid?)null : DbGuid.Parse(reader.GetString(24));
        var coverAssetRelativePath = reader.IsDBNull(25) ? null : reader.GetString(25);
        var bannerAssetRelativePath = reader.IsDBNull(26) ? null : reader.GetString(26);

        // One tag query returns both TagId+name and display-name list.
        var tagAssignments = await LoadTagAssignmentsForProfileAsync(connection, profileId, cancellationToken)
            .ConfigureAwait(false);
        var tags = tagAssignments.Select(t => t.DisplayName).ToList();

        return new ProfileDetailReadModel(
            profileId,
            kind,
            displayName,
            storageToken,
            categoryId,
            categoryName,
            tags,
            rating,
            isFavorite,
            overview,
            notes,
            identityId,
            sampleCount,
            coverMediaId,
            bannerMediaId,
            unknownSeq,
            activeOwnedCount,
            layoutPresetId,
            overridesJson,
            createdAt,
            updatedAt,
            trashedAt,
            rowVersion,
            tagAssignments,
            BannerMediaType: bannerMediaType,
            CoverMediaAssetId: coverMediaAssetId,
            BannerMediaAssetId: bannerMediaAssetId,
            CoverAssetRelativePath: coverAssetRelativePath,
            BannerAssetRelativePath: bannerAssetRelativePath,
            FigureMediaId: ReadFigureId(reader), FigureThumbnailRelativePath: ReadFigureString(reader,"figure_thumbnail_path"),
            FigureModelRenderRelativePath: ReadFigureString(reader,"figure_render_path"), FigureModelRenderState: ReadFigureState(reader),
            FigureSourceSha256: ReadFigureString(reader,"figure_source_sha256"));
    }

    public async Task<ProfileAppearanceReadModel?> GetAppearanceAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.profile_id,
                   p.row_version,
                   pa.layout_preset_id,
                   pa.overrides_json,
                   cover_asset.media_id AS cover_media_id,
                   banner_asset.media_id AS banner_media_id,
                   banner_media.media_type AS banner_media_type,
                   pa.cover_media_asset_id,
                   cover_asset.relative_path AS cover_asset_relative_path,
                   pa.banner_media_asset_id,
                   banner_asset.relative_path AS banner_asset_relative_path,
                   pa.figure_media_id AS figure_media_id,
                   (SELECT relative_path FROM media_assets WHERE media_id = pa.figure_media_id AND role = 'THUMBNAIL' AND state = 'READY') AS figure_thumbnail_path,
                   (SELECT relative_path FROM media_assets WHERE media_id = pa.figure_media_id AND role = 'MODEL_RENDER' AND state = 'READY' AND contract_version = 1) AS figure_render_path,
                   (SELECT state FROM media_assets WHERE media_id = pa.figure_media_id AND role = 'MODEL_RENDER') AS figure_render_state,
                   (SELECT sha256 FROM media WHERE media_id = pa.figure_media_id) AS figure_source_sha256
            FROM profiles p
            LEFT JOIN profile_appearance pa ON pa.profile_id = p.profile_id
            LEFT JOIN media_assets cover_asset ON cover_asset.media_asset_id = pa.cover_media_asset_id
            LEFT JOIN media_assets banner_asset ON banner_asset.media_asset_id = pa.banner_media_asset_id
            LEFT JOIN media banner_media ON banner_media.media_id = banner_asset.media_id
            WHERE p.profile_id = $profileId
              AND p.visibility = 'PUBLISHED'
              AND p.trashed_at_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProfileAppearanceReadModel(
            DbGuid.Parse(reader.GetString(0)),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? (Guid?)null : DbGuid.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? (Guid?)null : DbGuid.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? (MediaType?)null : DbEnum.ParseMediaType(reader.GetString(6)),
            reader.IsDBNull(7) ? (Guid?)null : DbGuid.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? (Guid?)null : DbGuid.Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            ReadFigureId(reader),ReadFigureString(reader,"figure_thumbnail_path"),ReadFigureString(reader,"figure_render_path"),
            ReadFigureState(reader),ReadFigureString(reader,"figure_source_sha256"));
    }

    private static string? ReadFigureString(SqliteDataReader reader,string field) => reader.IsDBNull(reader.GetOrdinal(field)) ? null : reader.GetString(reader.GetOrdinal(field));
    private static Guid? ReadFigureId(SqliteDataReader reader) => ReadFigureString(reader,"figure_media_id") is { } id ? DbGuid.Parse(id) : null;
    private static MediaAssetState? ReadFigureState(SqliteDataReader reader) => ReadFigureString(reader,"figure_render_state") is { } state ? DbEnum.ParseMediaAssetState(state) : null;

    public async Task<IReadOnlyList<ProfileFigureCandidate>> GetFigureCandidatesAsync(Guid profileId,CancellationToken ct = default)
    {
        var candidates = new List<ProfileFigureCandidate>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.media_id, coalesce(m.original_file_name,m.current_managed_file_name,''), coalesce(m.byte_length,0),
                   thumb.relative_path, render.relative_path, render.state, render.contract_version,
                   m.sha256, coalesce(m.current_managed_file_name,m.original_file_name,''), m.dependency_status
            FROM media m JOIN profiles p ON p.profile_id = $profile
            LEFT JOIN media_assets thumb ON thumb.media_id = m.media_id AND thumb.role = 'THUMBNAIL' AND thumb.state = 'READY'
            LEFT JOIN media_assets render ON render.media_id = m.media_id AND render.role = 'MODEL_RENDER'
            WHERE p.kind = 'NORMAL' AND p.visibility = 'PUBLISHED' AND p.trashed_at_ms IS NULL
              AND m.media_type = 'MODEL' AND m.state = 'ACTIVE' AND m.trashed_at_ms IS NULL
              AND EXISTS (SELECT 1 FROM profile_media pm WHERE pm.profile_id = p.profile_id AND pm.media_id = m.media_id AND pm.publication_import_unit_id IS NULL)
              AND NOT EXISTS (SELECT 1 FROM trash_entries te WHERE te.entity_type = 'MEDIA' AND te.entity_id = m.media_id AND te.state IN ('PENDING','EXECUTING'))
            ORDER BY m.added_to_library_at_ms DESC,m.created_at_ms DESC,m.media_id LIMIT 128;
            """;
        command.Parameters.AddWithValue("$profile",DbGuid.Format(profileId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!ModelRenderEligibility.IsEligible(reader.GetString(8),reader.GetString(9)) || reader.IsDBNull(7)) continue;
            var state = reader.IsDBNull(5) ? (MediaAssetState?)null : DbEnum.ParseMediaAssetState(reader.GetString(5));
            var version = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);
            var ready = state == MediaAssetState.Ready && version == 1;
            candidates.Add(new(DbGuid.Parse(reader.GetString(0)),reader.GetString(1),reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),reader.IsDBNull(4) ? null : reader.GetString(4),state,version,
                reader.GetString(7),ready ? null : state == MediaAssetState.NeedsRepair ? "NEEDS_REPAIR" : "PREPARING",ready));
        }
        return candidates;
    }
    public async Task<IReadOnlyList<ProfileFigurePrewarmCandidate>> GetFigurePrewarmCandidatesAsync(
        int maxCount = 2,
        CancellationToken ct = default)
    {
        maxCount = Math.Clamp(maxCount, 1, 8);
        var candidates = new List<ProfileFigurePrewarmCandidate>(maxCount);
        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.media_id, render.relative_path, m.sha256
            FROM profile_appearance pa
            JOIN profiles p ON p.profile_id = pa.profile_id
            JOIN media m ON m.media_id = pa.figure_media_id
            JOIN media_assets render ON render.media_id = m.media_id
                AND render.role = 'MODEL_RENDER'
                AND render.state = 'READY'
                AND render.contract_version = 1
            WHERE pa.figure_media_id IS NOT NULL
              AND p.kind = 'NORMAL' AND p.visibility = 'PUBLISHED' AND p.trashed_at_ms IS NULL
              AND m.media_type = 'MODEL' AND m.state = 'ACTIVE' AND m.trashed_at_ms IS NULL
              AND m.sha256 IS NOT NULL AND m.dependency_status = 'SELF_CONTAINED'
              AND lower(coalesce(m.current_managed_file_name,m.original_file_name,'')) LIKE '%.glb'
              AND EXISTS (
                  SELECT 1 FROM profile_media pm
                  WHERE pm.profile_id = p.profile_id
                    AND pm.media_id = m.media_id
                    AND pm.publication_import_unit_id IS NULL)
              AND NOT EXISTS (
                  SELECT 1 FROM trash_entries te
                  WHERE te.entity_type = 'MEDIA'
                    AND te.entity_id = m.media_id
                    AND te.state IN ('PENDING','EXECUTING'))
            GROUP BY m.media_id, render.relative_path, m.sha256
            ORDER BY MAX(p.updated_at_ms) DESC, MAX(m.added_to_library_at_ms) DESC, m.media_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", maxCount);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            candidates.Add(new(
                DbGuid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return candidates;
    }

    public async Task<ProfileMediaPage> GetMediaPageAsync(
        Guid profileId,
        ProfileMediaFilter filter = ProfileMediaFilter.All,
        MediaType? mediaType = null,
        int pageSize = 50,
        string? continuationToken = null,
        CancellationToken cancellationToken = default,
        bool isFavoriteOnly = false,
        MediaGridSort sort = MediaGridSort.NewestFirst,
        int pageIndex = 1)
    {
        if (profileId == Guid.Empty)
        {
            return new ProfileMediaPage([], null, false, 0);
        }

        pageSize = Math.Clamp(pageSize, 1, 500);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var parameters = new List<(string Name, object Value)>
        {
            ("$profileId", DbGuid.Format(profileId))
        };

        string relationFilter;
        switch (filter)
        {
            case ProfileMediaFilter.Owned:
                relationFilter = "AND pa.relation_type = 'OWNER'";
                break;
            case ProfileMediaFilter.AppearsIn:
                relationFilter = "AND pa.relation_type = 'APPEARS'";
                break;
            case ProfileMediaFilter.Manual:
                relationFilter = "AND pa.relation_type = 'MANUAL'";
                break;
            case ProfileMediaFilter.All:
            default:
                relationFilter = "";
                break;
        }

        string typeFilter = "";
        if (mediaType.HasValue)
        {
            typeFilter = "AND a.media_type = $mediaType";
            parameters.Add(("$mediaType", DbEnum.Format(mediaType.Value)));
        }

        string favoriteFilter = isFavoriteOnly ? "AND a.is_favorite = 1" : "";

        string cursorCondition = "";
        if (!string.IsNullOrWhiteSpace(continuationToken)
            && TryParseMediaCursor(continuationToken, out var cursorTime, out var cursorMediaId))
        {
            cursorCondition = "AND (coalesce(a.added_to_library_at_ms, a.created_at_ms) < $cursorTime OR (coalesce(a.added_to_library_at_ms, a.created_at_ms) = $cursorTime AND a.media_id < $cursorMediaId))";
            parameters.Add(("$cursorTime", cursorTime));
            parameters.Add(("$cursorMediaId", DbGuid.Format(cursorMediaId)));
        }

        var orderByClause = sort switch
        {
            MediaGridSort.OldestFirst => "ORDER BY coalesce(a.added_to_library_at_ms, a.created_at_ms) ASC, a.media_id ASC",
            MediaGridSort.NameAscending => "ORDER BY coalesce(a.current_managed_file_name, a.original_file_name, '') ASC, a.media_id ASC",
            MediaGridSort.CapturedNewestFirst => "ORDER BY CASE WHEN am.captured_at_ms IS NULL THEN 1 ELSE 0 END, am.captured_at_ms DESC, a.media_id DESC",
            MediaGridSort.SizeLargestFirst => "ORDER BY coalesce(a.byte_length, 0) DESC, a.media_id DESC",
            _ => "ORDER BY coalesce(a.added_to_library_at_ms, a.created_at_ms) DESC, a.media_id DESC"
        };

        var offset = string.IsNullOrWhiteSpace(continuationToken) && pageIndex > 1
            ? (pageIndex - 1) * pageSize
            : 0;
        var offsetClause = offset > 0 ? $"OFFSET {offset}" : "";

        // Windowed COUNT(*) OVER() avoids a separate COUNT round-trip for most pages.
        var sql = $"""
            SELECT a.media_id, a.media_type,
                   CASE
                       WHEN max(CASE WHEN pa.relation_type = 'OWNER' THEN 1 ELSE 0 END) = 1 THEN 'OWNER'
                       WHEN max(CASE WHEN pa.relation_type = 'APPEARS' THEN 1 ELSE 0 END) = 1 THEN 'APPEARS'
                       ELSE 'MANUAL'
                   END AS relation_type,
                   a.current_managed_relative_path, a.current_managed_file_name,
                   a.created_at_ms, a.added_to_library_at_ms,
                   a.path_state, am.width, am.height, am.duration_ms, a.sha256,
                   a.is_favorite,
                   COUNT(*) OVER() AS total_count,
                   a.original_file_name,
                   thumbnail.relative_path AS thumbnail_relative_path,
                   hover.relative_path AS hover_relative_path
            FROM profile_media pa
            JOIN media a ON pa.media_id = a.media_id AND pa.publication_import_unit_id IS NULL
            LEFT JOIN media_metadata am ON am.media_id = a.media_id
            LEFT JOIN media_assets thumbnail
              ON thumbnail.media_id = a.media_id
             AND thumbnail.role = 'THUMBNAIL'
             AND thumbnail.state = 'READY'
            LEFT JOIN media_assets hover
              ON hover.media_id = a.media_id
             AND hover.role = 'HOVER'
             AND hover.state = 'READY'
            WHERE pa.profile_id = $profileId
              AND a.state = 'ACTIVE'
              {relationFilter}
              {typeFilter}
              {favoriteFilter}
              {cursorCondition}
            GROUP BY a.media_id
            {orderByClause}
            LIMIT {pageSize + 1} {offsetClause};
            """;

        var items = new List<ProfileMediaItemReadModel>();
        int totalCount = 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var isFirstRow = true;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (isFirstRow)
                {
                    totalCount = reader.GetInt32(13);
                    isFirstRow = false;
                }

                var assetId = DbGuid.Parse(reader.GetString(0));
                var mType = DbEnum.ParseMediaType(reader.GetString(1));
                var relType = DbEnum.ParseProfileMediaRelation(reader.GetString(2));
                var relPath = reader.IsDBNull(3) ? null : reader.GetString(3);
                var fileName = reader.IsDBNull(4) ? null : reader.GetString(4);
                var createdAt = DbTime.Parse(reader.GetInt64(5));
                var addedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : DbTime.Parse(reader.GetInt64(6));
                var pathState = reader.IsDBNull(7)
                    ? ManagedPathState.None
                    : DbEnum.ParseManagedPathState(reader.GetString(7));
                var width = reader.IsDBNull(8) ? (int?)null : (int)reader.GetInt64(8);
                var height = reader.IsDBNull(9) ? (int?)null : (int)reader.GetInt64(9);
                var durationMs = reader.IsDBNull(10) ? (int?)null : (int)reader.GetInt64(10);
                var fingerprint = reader.IsDBNull(11) ? null : reader.GetString(11);
                var isFavorite = !reader.IsDBNull(12) && reader.GetInt64(12) != 0;
                var originalFileName = reader.IsDBNull(14) ? null : reader.GetString(14);
                var thumbnailRelativePath = reader.IsDBNull(15) ? null : reader.GetString(15);
                var hoverRelativePath = reader.IsDBNull(16) ? null : reader.GetString(16);

                items.Add(new ProfileMediaItemReadModel(
                    assetId,
                    mType,
                    relType,
                    relPath,
                    fileName,
                    createdAt,
                    addedAt,
                    width,
                    height,
                    durationMs,
                    pathState,
                    fingerprint,
                    isFavorite,
                    originalFileName,
                    thumbnailRelativePath,
                    hoverRelativePath));
            }
        }

        // Empty page fallback — only run COUNT when windowed total is unavailable.
        if (items.Count == 0 && pageIndex > 1)
        {
            // Out-of-range page: windowed count is unavailable, do a lightweight COUNT.
            var countSql = $"""
                SELECT COUNT(DISTINCT a.media_id)
                FROM profile_media pa
                JOIN media a ON pa.media_id = a.media_id AND pa.publication_import_unit_id IS NULL
                WHERE pa.profile_id = $profileId
                  AND a.state = 'ACTIVE'
                  {relationFilter}
                  {typeFilter}
                  {favoriteFilter};
                """;
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = countSql;
            foreach (var (name, value) in parameters)
            {
                countCommand.Parameters.AddWithValue(name, value);
            }
            var countObj = await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            totalCount = countObj is not null && countObj is not DBNull ? Convert.ToInt32(countObj, CultureInfo.InvariantCulture) : 0;
        }
        // First page with zero rows: totalCount stays 0 (already correct from windowed or default).

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        string? nextPageToken = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            var timeMs = (last.AddedToLibraryAtUtc ?? last.CreatedAtUtc).ToUnixTimeMilliseconds();
            nextPageToken = $"{timeMs}:{last.MediaId:D}";
        }

        return new ProfileMediaPage(items, nextPageToken, hasMore, totalCount);
    }

    public async Task<IReadOnlyList<UnknownProfileSummary>> GetUnknownProfilesAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.profile_id, p.unknown_sequence, p.profile_storage_token,
                   (
                       SELECT COUNT(*)
                       FROM profile_media pa
                       JOIN media a ON pa.media_id = a.media_id AND pa.publication_import_unit_id IS NULL
                       WHERE pa.profile_id = p.profile_id
                         AND pa.relation_type = 'OWNER'
                         AND a.state = 'ACTIVE'
                   ) AS active_owned_count,
                   p.created_at_ms, p.updated_at_ms, p.trashed_at_ms, p.row_version
            FROM profiles p
            WHERE p.kind = 'UNKNOWN' AND p.trashed_at_ms IS NULL
            ORDER BY p.unknown_sequence ASC
            LIMIT {limit};
            """;

        var list = new List<UnknownProfileSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var profileId = DbGuid.Parse(reader.GetString(0));
            var seq = reader.GetInt64(1);
            var token = reader.IsDBNull(2) ? null : reader.GetString(2);
            var activeOwnedCount = reader.GetInt64(3);
            var createdAt = DbTime.Parse(reader.GetInt64(4));
            var updatedAt = DbTime.Parse(reader.GetInt64(5));
            var trashedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : DbTime.Parse(reader.GetInt64(6));
            var rowVersion = reader.GetInt64(7);

            list.Add(new UnknownProfileSummary(
                profileId,
                seq,
                $"Unknown {seq}",
                token,
                activeOwnedCount,
                createdAt,
                updatedAt,
                trashedAt,
                rowVersion));
        }

        return list;
    }

    public async Task<IReadOnlyList<ProfileMediaRelationEntry>> GetRelationsAsync(
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
        command.CommandText = """
            SELECT media_id, relation_type, created_at_ms, provenance_key
            FROM profile_media
            WHERE profile_id = $profileId
            ORDER BY created_at_ms DESC, media_id ASC;
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        var list = new List<ProfileMediaRelationEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var assetId = DbGuid.Parse(reader.GetString(0));
            var relType = DbEnum.ParseProfileMediaRelation(reader.GetString(1));
            var createdAt = DbTime.Parse(reader.GetInt64(2));
            var provenance = reader.IsDBNull(3) ? null : reader.GetString(3);

            list.Add(new ProfileMediaRelationEntry(
                profileId,
                assetId,
                relType,
                createdAt,
                provenance));
        }

        return list;
    }

    public async Task<ProfileHeaderReadModel?> GetProfileByStorageTokenAsync(
        string storageToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT profile_id FROM profiles WHERE profile_storage_token = $token;";
        command.Parameters.AddWithValue("$token", storageToken.Trim());

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            return null;
        }

        return await GetHeaderAsync(DbGuid.Parse((string)result), cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseMediaCursor(string token, out long timeMs, out Guid assetId)
    {
        timeMs = 0;
        assetId = Guid.Empty;
        var parts = token.Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        return long.TryParse(parts[0], out timeMs) && DomainId.TryParse(parts[1], out assetId);
    }

    // ── Shared filtered-media SQL semantics ────────────────────────

    /// <summary>
    /// Returns the SQL WHERE fragment (without leading AND) for the profile-media relation/type/favorite
    /// filters used by both GetMediaPageAsync and the adjacency query. Parameters are added to
    /// <paramref name="parameters"/>.
    /// </summary>
    private static string BuildProfileMediaFilterClauses(
        ProfileMediaFilter filter,
        MediaType? mediaType,
        bool isFavoriteOnly,
        List<(string Name, object Value)> parameters)
    {
        var clauses = new List<string>
        {
            "pa.profile_id = $profileId",
            "pa.publication_import_unit_id IS NULL",
            "a.state = 'ACTIVE'"
        };

        switch (filter)
        {
            case ProfileMediaFilter.Owned:
                clauses.Add("pa.relation_type = 'OWNER'");
                break;
            case ProfileMediaFilter.AppearsIn:
                clauses.Add("pa.relation_type = 'APPEARS'");
                break;
            case ProfileMediaFilter.Manual:
                clauses.Add("pa.relation_type = 'MANUAL'");
                break;
        }

        if (mediaType.HasValue)
        {
            clauses.Add("a.media_type = $mediaType");
            parameters.Add(("$mediaType", DbEnum.Format(mediaType.Value)));
        }

        if (isFavoriteOnly)
        {
            clauses.Add("a.is_favorite = 1");
        }

        return string.Join(" AND ", clauses);
    }

    /// <summary>
    /// Returns the SQL ORDER BY fragment for the given sort, always ending with asset identity
    /// for deterministic tie-breaking.
    /// </summary>
    private static string BuildProfileMediaOrderClause(MediaGridSort sort) => sort switch
    {
        MediaGridSort.OldestFirst => "coalesce(a.added_to_library_at_ms, a.created_at_ms) ASC, a.media_id ASC",
        MediaGridSort.NameAscending => "coalesce(a.current_managed_file_name, a.original_file_name, '') ASC, a.media_id ASC",
        MediaGridSort.CapturedNewestFirst => "CASE WHEN am.captured_at_ms IS NULL THEN 1 ELSE 0 END, am.captured_at_ms DESC, a.media_id DESC",
        MediaGridSort.SizeLargestFirst => "coalesce(a.byte_length, 0) DESC, a.media_id DESC",
        _ => "coalesce(a.added_to_library_at_ms, a.created_at_ms) DESC, a.media_id DESC"
    };

    // ── Adjacent media for previous/next navigation ───────────────────────

    /// <summary>
    /// Returns the previous and next asset IDs adjacent to <paramref name="currentMediaId"/>
    /// within the same filtered/sorted Profile media set. Uses LAG/LEAD window functions
    /// so it does not load the full media list into C#.
    /// </summary>
    public async Task<(Guid? PreviousMediaId, Guid? NextMediaId)> GetAdjacentMediaIdsAsync(
        Guid profileId,
        Guid currentMediaId,
        ProfileMediaFilter filter = ProfileMediaFilter.All,
        MediaType? mediaType = null,
        bool isFavoriteOnly = false,
        MediaGridSort sort = MediaGridSort.NewestFirst,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty || currentMediaId == Guid.Empty)
        {
            return (null, null);
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var parameters = new List<(string Name, object Value)>
        {
            ("$profileId", DbGuid.Format(profileId)),
            ("$currentMediaId", DbGuid.Format(currentMediaId)),
        };

        var filterClauses = BuildProfileMediaFilterClauses(filter, mediaType, isFavoriteOnly, parameters);
        var orderExpr = BuildProfileMediaOrderClause(sort);

        var sql = $"""
            WITH filtered AS (
                SELECT DISTINCT a.media_id
                FROM profile_media pa
                JOIN media a ON pa.media_id = a.media_id
                WHERE {filterClauses}
            ),
            ordered AS (
                SELECT
                    f.media_id,
                    LAG(f.media_id) OVER (ORDER BY {orderExpr}) AS previous_media_id,
                    LEAD(f.media_id) OVER (ORDER BY {orderExpr}) AS next_media_id
                FROM filtered f
                JOIN media a ON a.media_id = f.media_id
                LEFT JOIN media_metadata am ON am.media_id = f.media_id
            )
            SELECT previous_media_id, next_media_id
            FROM ordered
            WHERE media_id = $currentMediaId;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (null, null);
        }

        Guid? previous = reader.IsDBNull(0) ? null : DbGuid.Parse(reader.GetString(0));
        Guid? next = reader.IsDBNull(1) ? null : DbGuid.Parse(reader.GetString(1));
        return (previous, next);
    }

    public async Task<IReadOnlyList<ProfileTagAssignment>> GetProfileTagAssignmentsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await LoadTagAssignmentsForProfileAsync(connection, profileId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ProfileTagAssignment>> LoadTagAssignmentsForProfileAsync(
        SqliteConnection connection,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT t.tag_id, t.name
            FROM profile_tags pt
            JOIN tags t ON pt.tag_id = t.tag_id
            WHERE pt.profile_id = $profileId
            ORDER BY t.normalized_name;
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        var assignments = new List<ProfileTagAssignment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            assignments.Add(new ProfileTagAssignment(reader.GetString(0), reader.GetString(1)));
        }

        return assignments;
    }

    private static async Task<IReadOnlyList<string>> LoadTagsForProfileAsync(
        SqliteConnection connection,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.name
            FROM profile_tags pt
            JOIN tags t ON pt.tag_id = t.tag_id
            WHERE pt.profile_id = $profileId
            ORDER BY t.name ASC;
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        var tags = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tags.Add(reader.GetString(0));
        }

        return tags;
    }

    public async Task<IReadOnlyList<ProfilePickerItem>> GetProfilePickerCandidatesAsync(
        Guid? excludeProfileId = null,
        int maxCount = 50,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        var sql = """
            SELECT p.profile_id, p.display_name, p.unknown_sequence, c.name AS category_name
            FROM profiles p
            LEFT JOIN categories c ON p.category_id = c.category_id
            WHERE p.kind = 'NORMAL' AND p.visibility = 'PUBLISHED' AND p.trashed_at_ms IS NULL
            """;
        if (excludeProfileId.HasValue && excludeProfileId.Value != Guid.Empty)
        {
            sql += " AND p.profile_id != $excludeId";
            command.Parameters.AddWithValue("$excludeId", DbGuid.Format(excludeProfileId.Value));
        }
        sql += " ORDER BY p.display_name ASC LIMIT $limit;";
        command.CommandText = sql;
        command.Parameters.AddWithValue("$limit", Math.Clamp(maxCount, 1, 200));

        var list = new List<ProfilePickerItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = DbGuid.Parse(reader.GetString(0));
            var displayName = reader.IsDBNull(1)
                ? (reader.IsDBNull(2) ? "Unknown" : $"Unknown {reader.GetInt64(2)}")
                : reader.GetString(1);
            var categoryName = reader.IsDBNull(3) ? null : reader.GetString(3);
            list.Add(new ProfilePickerItem(id, displayName, categoryName));
        }

        return list;
    }
}
