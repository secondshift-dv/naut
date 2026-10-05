using System.Globalization;
using Microsoft.Data.Sqlite;
using Neuterradise.App.Gallery;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed class GalleryReads
{

    public const int MaxSearchSuggestions = 12;

    private readonly CatalogConnectionFactory _connectionFactory;

    public GalleryReads(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
    }

    public GalleryReads(CatalogConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    // ── Shared SQL fragments ────────────────────────────────────────────────────
    // Publication-semantics CTEs used by both gallery page and spotlight queries.
    // Using identical definitions ensures a single source of truth for what counts
    // as "published, active, publicly visible media".

    private const string OwnedCountCte = """
        owned_counts AS (
            SELECT pa.profile_id, COUNT(*) AS active_owned_count
            FROM profile_media pa
            JOIN media a ON pa.media_id = a.media_id
            WHERE pa.relation_type = 'OWNER'
              AND pa.publication_import_unit_id IS NULL
              AND a.state = 'ACTIVE'
            GROUP BY pa.profile_id
        )
        """;

    private const string RelatedCountCte = """
        related_counts AS (
            SELECT pid AS profile_id, COUNT(*) AS related_count
            FROM (
                SELECT profile_id_low AS pid FROM related_profile_summary WHERE rank_score > 0
                UNION ALL
                SELECT profile_id_high AS pid FROM related_profile_summary WHERE rank_score > 0
            )
            GROUP BY pid
        )
        """;

    public async Task<GalleryPage> GetGalleryPageAsync(
        GalleryQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new GalleryQuery();
        var pageSize = GalleryPageSizes.Normalize(query.PageSize);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var (whereClause, whereParams) = BuildWhereClause(query);

        // Always include the CTEs for the count query so it can reference oc/rc if the
        // WHERE clause or sort needs them (e.g. HasMedia, HasRelatedEvidence, MediaCountDesc).
        long totalCount;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $"""
                WITH {OwnedCountCte},
                     {RelatedCountCte}
                SELECT COUNT(*)
                FROM profiles p
                LEFT JOIN owned_counts oc ON oc.profile_id = p.profile_id
                LEFT JOIN related_counts rc ON rc.profile_id = p.profile_id
                {whereClause};
                """;
            foreach (var (name, value) in whereParams)
            {
                countCommand.Parameters.AddWithValue(name, value);
            }
            totalCount = Convert.ToInt64(
                await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        var totalPages = totalCount <= 0 ? 1 : (int)((totalCount + pageSize - 1) / pageSize);
        var pageIndex = Math.Clamp(query.PageIndex < 1 ? 1 : query.PageIndex, 1, totalPages);
        var offset = (long)(pageIndex - 1) * pageSize;

        var orderClause = BuildOrderClause(query.SortOrder);

        var sql = $"""
            WITH {OwnedCountCte},
                 {RelatedCountCte}
            SELECT p.profile_id, p.kind, p.display_name, p.unknown_sequence,
                   p.profile_storage_token, p.category_id, c.name AS category_name,
                   p.rating, p.is_favorite, p.cover_media_id, banner_prepared.media_id AS banner_media_id,
                   p.created_at_ms, p.updated_at_ms, p.row_version,
                   COALESCE(oc.active_owned_count, 0) AS active_owned_count,
                   COALESCE(rc.related_count, 0) AS related_count,
                   pa_app.overrides_json,
                   cover_asset.sha256 AS cover_fingerprint,
                   banner_prepared.sha256 AS banner_fingerprint,
                   banner_source.media_type AS banner_media_type,
                   banner_prepared.relative_path AS banner_asset_relative_path,
                   banner_prepared.media_asset_id AS banner_media_asset_id
            FROM profiles p
            LEFT JOIN owned_counts oc ON oc.profile_id = p.profile_id
            LEFT JOIN related_counts rc ON rc.profile_id = p.profile_id
            LEFT JOIN categories c ON p.category_id = c.category_id
            LEFT JOIN profile_appearance pa_app ON p.profile_id = pa_app.profile_id
            LEFT JOIN media cover_asset ON cover_asset.media_id = p.cover_media_id
            LEFT JOIN media_assets banner_prepared ON banner_prepared.media_asset_id = pa_app.banner_media_asset_id
            LEFT JOIN media banner_source ON banner_source.media_id = banner_prepared.media_id
            {whereClause}
            {orderClause}
            LIMIT $pageSize OFFSET $pageOffset;
            """;

        var items = new List<GalleryProfileSummary>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            foreach (var (name, value) in whereParams)
            {
                command.Parameters.AddWithValue(name, value);
            }
            command.Parameters.AddWithValue("$pageSize", pageSize);
            command.Parameters.AddWithValue("$pageOffset", offset);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadGalleryProfileSummary(reader));
            }
        }

        if (items.Count > 0)
        {
            var profileIdMap = items.ToDictionary(item => item.ProfileId);
            var tagMap = await LoadTagsForProfilesAsync(connection, profileIdMap.Keys, cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (tagMap.TryGetValue(item.ProfileId, out var tags))
                {
                    items[i] = item with { Tags = tags };
                }
            }
        }

        return new GalleryPage(items, pageIndex, pageSize, totalCount);
    }

    public async Task<GalleryProfileSummary?> GetGalleryProfileSummaryAsync(
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
        command.CommandText = $"""
            WITH {OwnedCountCte},
                 {RelatedCountCte}
            SELECT p.profile_id, p.kind, p.display_name, p.unknown_sequence,
                   p.profile_storage_token, p.category_id, c.name AS category_name,
                   p.rating, p.is_favorite, p.cover_media_id, banner_prepared.media_id AS banner_media_id,
                   p.created_at_ms, p.updated_at_ms, p.row_version,
                   COALESCE(oc.active_owned_count, 0) AS active_owned_count,
                   COALESCE(rc.related_count, 0) AS related_count,
                   pa_app.overrides_json,
                   cover_asset.sha256 AS cover_fingerprint,
                   banner_prepared.sha256 AS banner_fingerprint,
                   banner_source.media_type AS banner_media_type,
                   banner_prepared.relative_path AS banner_asset_relative_path,
                   banner_prepared.media_asset_id AS banner_media_asset_id
            FROM profiles p
            LEFT JOIN owned_counts oc ON oc.profile_id = p.profile_id
            LEFT JOIN related_counts rc ON rc.profile_id = p.profile_id
            LEFT JOIN categories c ON p.category_id = c.category_id
            LEFT JOIN profile_appearance pa_app ON p.profile_id = pa_app.profile_id
            LEFT JOIN media cover_asset ON cover_asset.media_id = p.cover_media_id
            LEFT JOIN media_assets banner_prepared ON banner_prepared.media_asset_id = pa_app.banner_media_asset_id
            LEFT JOIN media banner_source ON banner_source.media_id = banner_prepared.media_id
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

        var summary = ReadGalleryProfileSummary(reader);
        var tags = await LoadTagsForProfileAsync(connection, profileId, cancellationToken).ConfigureAwait(false);
        return summary with { Tags = tags };
    }

    public async Task<IReadOnlyList<HomeSpotlightCandidateReadModel>> GetSpotlightCandidatesAsync(
        int limit = 12,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 50);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var sql = $"""
            WITH {OwnedCountCte},
                 {RelatedCountCte}
            SELECT p.profile_id, p.kind, p.display_name, p.unknown_sequence,
                   p.profile_storage_token, p.category_id, c.name AS category_name,
                   p.rating, p.is_favorite, p.cover_media_id, banner_prepared.media_id AS banner_media_id,
                   p.created_at_ms, p.updated_at_ms, p.row_version,
                   COALESCE(oc.active_owned_count, 0) AS active_owned_count,
                   COALESCE(rc.related_count, 0) AS related_count,
                   pa_app.overrides_json,
                   cover_asset.sha256 AS cover_fingerprint,
                   banner_prepared.sha256 AS banner_fingerprint,
                   banner_source.media_type AS banner_media_type,
                   banner_prepared.relative_path AS banner_asset_relative_path,
                   banner_prepared.media_asset_id AS banner_media_asset_id,
                   p.overview
            FROM profiles p
            LEFT JOIN owned_counts oc ON oc.profile_id = p.profile_id
            LEFT JOIN related_counts rc ON rc.profile_id = p.profile_id
            LEFT JOIN categories c ON p.category_id = c.category_id
            LEFT JOIN profile_appearance pa_app ON p.profile_id = pa_app.profile_id
            LEFT JOIN media cover_asset ON cover_asset.media_id = p.cover_media_id
            LEFT JOIN media_assets banner_prepared ON banner_prepared.media_asset_id = pa_app.banner_media_asset_id
            LEFT JOIN media banner_source ON banner_source.media_id = banner_prepared.media_id
            WHERE p.trashed_at_ms IS NULL
              AND p.kind = 'NORMAL'
              AND p.visibility = 'PUBLISHED'
            ORDER BY
              (CASE WHEN p.cover_media_id IS NOT NULL OR pa_app.banner_media_asset_id IS NOT NULL THEN 1 ELSE 0 END) DESC,
              p.is_favorite DESC,
              (CASE WHEN p.rating IS NOT NULL THEN 1 ELSE 0 END) DESC,
              p.rating DESC,
              p.updated_at_ms DESC,
              p.profile_id ASC
            LIMIT {limit};
            """;

        var list = new List<HomeSpotlightCandidateReadModel>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var summary = ReadGalleryProfileSummary(reader);
                var overview = reader.IsDBNull(22) ? null : reader.GetString(22);
                list.Add(new HomeSpotlightCandidateReadModel(summary, overview));
            }
        }

        if (list.Count > 0)
        {
            var profileIdMap = list.ToDictionary(item => item.Summary.ProfileId);
            var tagMap = await LoadTagsForProfilesAsync(connection, profileIdMap.Keys, cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (tagMap.TryGetValue(item.Summary.ProfileId, out var tags))
                {
                    list[i] = item with
                    {
                        Summary = item.Summary with { Tags = tags }
                    };
                }
            }
        }

        return list;
    }

    public async Task<GalleryFacets> GetGalleryFacetsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Category facet counts — set-based, joining the published profile set.
        var categories = new List<GalleryFilterOption>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.category_id, c.name, COUNT(p.profile_id) AS profile_count
                FROM categories c
                JOIN profiles p ON p.category_id = c.category_id
                WHERE p.trashed_at_ms IS NULL
                  AND p.visibility = 'PUBLISHED'
                GROUP BY c.category_id, c.name
                ORDER BY c.name ASC;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                categories.Add(new GalleryFilterOption(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }

        // Tag facet counts — set-based, joining published profiles.
        var tags = new List<GalleryFilterOption>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT t.tag_id, t.name, COUNT(pt.profile_id) AS profile_count
                FROM tags t
                JOIN profile_tags pt ON pt.tag_id = t.tag_id
                JOIN profiles p ON p.profile_id = pt.profile_id
                WHERE p.trashed_at_ms IS NULL
                  AND p.visibility = 'PUBLISHED'
                GROUP BY t.tag_id, t.name
                ORDER BY t.name ASC;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tags.Add(new GalleryFilterOption(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }

        return new GalleryFacets(categories, tags);
    }

    public async Task<IReadOnlyList<GallerySearchSuggestion>> GetSearchSuggestionsAsync(
        string? searchText,
        int limit = MaxSearchSuggestions,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return [];
        }

        var bound = Math.Clamp(limit, 1, MaxSearchSuggestions);
        var pattern = $"%{EscapeLike(searchText.Trim())}%";

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Single UNION ALL query replaces three sequential commands.
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT 'Profile' AS kind, p.profile_id AS id, p.display_name AS name, p.unknown_sequence AS seq
            FROM profiles p
            WHERE p.trashed_at_ms IS NULL
              AND p.kind = 'NORMAL'
              AND p.visibility = 'PUBLISHED'
              AND p.display_name LIKE $search ESCAPE '\'
            UNION ALL
            SELECT 'Category' AS kind, c.category_id AS id, c.name AS name, NULL AS seq
            FROM categories c
            WHERE c.name LIKE $search ESCAPE '\'
            UNION ALL
            SELECT 'Tag' AS kind, t.tag_id AS id, t.name AS name, NULL AS seq
            FROM tags t
            WHERE t.name LIKE $search ESCAPE '\'
            ORDER BY kind, name ASC
            LIMIT {bound};
            """;
        command.Parameters.AddWithValue("$search", pattern);

        var suggestions = new List<GallerySearchSuggestion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var kind = reader.GetString(0) switch
            {
                "Profile" => GallerySuggestionKind.Profile,
                "Category" => GallerySuggestionKind.Category,
                _ => GallerySuggestionKind.Tag,
            };
            var id = reader.GetString(1);
            var name = reader.IsDBNull(2) ? id : reader.GetString(2);

            if (kind == GallerySuggestionKind.Profile)
            {
                var profileId = DbGuid.Parse(id);
                var displayName = reader.IsDBNull(2)
                    ? (reader.IsDBNull(3) ? "Unknown" : $"Unknown {reader.GetInt64(3)}")
                    : reader.GetString(2);
                suggestions.Add(new GallerySearchSuggestion(kind, displayName, null, profileId));
            }
            else
            {
                suggestions.Add(new GallerySearchSuggestion(kind, name, id));
            }
        }

        return suggestions;
    }

    // ── Shared reader ───────────────────────────────────────────────────────────

    private static GalleryProfileSummary ReadGalleryProfileSummary(SqliteDataReader reader)
    {
        var profileId = DbGuid.Parse(reader.GetString(0));
        var kind = DbEnum.ParseProfileKind(reader.GetString(1));
        var displayName = reader.IsDBNull(2)
            ? (reader.IsDBNull(3) ? "Unknown" : $"Unknown {reader.GetInt64(3)}")
            : reader.GetString(2);
        var storageToken = reader.IsDBNull(4) ? null : reader.GetString(4);
        var categoryId = reader.IsDBNull(5) ? null : reader.GetString(5);
        var categoryName = reader.IsDBNull(6) ? null : reader.GetString(6);
        var rating = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
        var isFavorite = reader.GetInt32(8) == 1;
        var coverMediaId = reader.IsDBNull(9) ? (Guid?)null : DbGuid.Parse(reader.GetString(9));
        var bannerMediaId = reader.IsDBNull(10) ? (Guid?)null : DbGuid.Parse(reader.GetString(10));
        var createdAt = DbTime.Parse(reader.GetInt64(11));
        var updatedAt = DbTime.Parse(reader.GetInt64(12));
        var rowVersion = reader.GetInt64(13);
        var activeOwnedCount = reader.GetInt64(14);
        var relatedCount = reader.GetInt32(15);
        var appearance = reader.IsDBNull(16)
            ? ProfileAppearanceOverrides.Default
            : ProfileAppearanceOverrides.Parse(reader.GetString(16));
        var cardVariantId = appearance.ProfileCardVariantId;
        var coverFingerprint = reader.IsDBNull(17) ? null : reader.GetString(17);
        var bannerFingerprint = reader.IsDBNull(18) ? null : reader.GetString(18);
        var bannerMediaType = reader.IsDBNull(19)
            ? (Neuterradise.App.Media.MediaType?)null
            : DbEnum.ParseMediaType(reader.GetString(19));
        var bannerAssetRelativePath = reader.IsDBNull(20) ? null : reader.GetString(20);
        var bannerMediaAssetId = reader.IsDBNull(21) ? (Guid?)null : DbGuid.Parse(reader.GetString(21));

        return new GalleryProfileSummary(
            profileId,
            kind,
            displayName,
            storageToken,
            categoryId,
            categoryName,
            Array.Empty<string>(),
            rating,
            isFavorite,
            coverMediaId,
            bannerMediaId,
            activeOwnedCount,
            relatedCount,
            createdAt,
            updatedAt,
            rowVersion,
            cardVariantId,
            appearance,
            coverFingerprint,
            bannerFingerprint,
            bannerMediaType,
            bannerAssetRelativePath,
            bannerMediaAssetId);
    }

    // ── Where / Order ───────────────────────────────────────────────────────────

    private static (string WhereClause, List<(string Name, object Value)> Parameters) BuildWhereClause(
        GalleryQuery query)
    {
        var clauses = new List<string> { "p.trashed_at_ms IS NULL" };
        var parameters = new List<(string Name, object Value)>();

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {

            clauses.Add("""
                (
                    p.display_name LIKE $search ESCAPE '\'
                    OR EXISTS (
                        SELECT 1 FROM categories sc
                        WHERE sc.category_id = p.category_id AND sc.name LIKE $search ESCAPE '\'
                    )
                    OR EXISTS (
                        SELECT 1 FROM profile_tags spt
                        JOIN tags st ON spt.tag_id = st.tag_id
                        WHERE spt.profile_id = p.profile_id AND st.name LIKE $search ESCAPE '\'
                    )
                )
                """);
            parameters.Add(("$search", $"%{EscapeLike(query.SearchText.Trim())}%"));
        }

        switch (query.KindFilter)
        {
            case GalleryProfileKindFilter.NormalOnly:
                clauses.Add("p.kind = 'NORMAL'");
                break;

            case GalleryProfileKindFilter.UnknownOnly:
                clauses.Add("p.kind = 'UNKNOWN'");
                break;

            case GalleryProfileKindFilter.All:
            default:
                break;
        }

        // Import-created profiles remain DRAFT until publication (not part of canonical commit).
        // User-facing queries must not surface them as completed Profiles.
        clauses.Add("p.visibility = 'PUBLISHED'");

        if (!string.IsNullOrWhiteSpace(query.CategoryId))
        {
            clauses.Add("p.category_id = $categoryId");
            parameters.Add(("$categoryId", query.CategoryId));
        }

        if (!string.IsNullOrWhiteSpace(query.TagId))
        {
            clauses.Add("p.profile_id IN (SELECT pt.profile_id FROM profile_tags pt WHERE pt.tag_id = $tagId)");
            parameters.Add(("$tagId", query.TagId));
        }

        if (query.IsFavorite.HasValue)
        {
            clauses.Add("p.is_favorite = $isFavorite");
            parameters.Add(("$isFavorite", query.IsFavorite.Value ? 1 : 0));
        }

        if (query.MinRating.HasValue)
        {
            clauses.Add("p.rating >= $minRating");
            parameters.Add(("$minRating", query.MinRating.Value));
        }

        if (query.MaxRating.HasValue)
        {
            clauses.Add("p.rating <= $maxRating");
            parameters.Add(("$maxRating", query.MaxRating.Value));
        }

        if (query.HasMedia.HasValue)
        {
            if (query.HasMedia.Value)
            {
                clauses.Add("oc.active_owned_count > 0");
            }
            else
            {
                clauses.Add("COALESCE(oc.active_owned_count, 0) = 0");
            }
        }

        if (query.HasRelatedEvidence.HasValue)
        {
            if (query.HasRelatedEvidence.Value)
            {
                clauses.Add("COALESCE(rc.related_count, 0) > 0");
            }
            else
            {
                clauses.Add("COALESCE(rc.related_count, 0) = 0");
            }
        }

        AddMediaTypeClause(clauses, parameters, query.HasImages, "IMAGE", "$hasImage");
        AddMediaTypeClause(clauses, parameters, query.HasVideos, "VIDEO", "$hasVideo");
        AddMediaTypeClause(clauses, parameters, query.HasModels, "MODEL", "$hasModel");

        if (query.RelatedToProfileId.HasValue && query.RelatedToProfileId.Value != Guid.Empty)
        {

            clauses.Add("""
                (
                    p.profile_id <> $relatedTo
                    AND EXISTS (
                        SELECT 1 FROM related_profile_summary rrs
                        WHERE rrs.rank_score > 0
                          AND (
                                (rrs.profile_id_low = p.profile_id AND rrs.profile_id_high = $relatedTo)
                             OR (rrs.profile_id_high = p.profile_id AND rrs.profile_id_low = $relatedTo)
                          )
                    )
                )
                """);
            parameters.Add(("$relatedTo", DbGuid.Format(query.RelatedToProfileId.Value)));
        }

        AddRelatedEvidenceClause(clauses, query.HasSharedMedia, "rvs.shared_media_count > 0");

        var whereClause = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";
        return (whereClause, parameters);
    }

    private static void AddMediaTypeClause(
        List<string> clauses,
        List<(string Name, object Value)> parameters,
        bool? filter,
        string mediaType,
        string parameterName)
    {
        if (!filter.HasValue)
        {
            return;
        }

        var existence = $"""
            EXISTS (
                SELECT 1 FROM profile_media pa
                JOIN media a ON pa.media_id = a.media_id AND pa.publication_import_unit_id IS NULL
                WHERE pa.profile_id = p.profile_id
                  AND pa.relation_type = 'OWNER'
                  AND a.state = 'ACTIVE'
                  AND a.media_type = {parameterName}
            )
            """;

        clauses.Add(filter.Value ? existence : $"NOT {existence}");
        parameters.Add((parameterName, mediaType));
    }

    private static void AddRelatedEvidenceClause(List<string> clauses, bool? filter, string evidencePredicate)
    {
        if (!filter.HasValue)
        {
            return;
        }

        var existence = $"""
            EXISTS (
                SELECT 1 FROM related_profile_summary rvs
                WHERE (rvs.profile_id_low = p.profile_id OR rvs.profile_id_high = p.profile_id)
                  AND {evidencePredicate}
            )
            """;

        clauses.Add(filter.Value ? existence : $"NOT {existence}");
    }

    /// <summary>
    /// Builds a stable ORDER BY clause for the requested sort order. Every branch ends with
    /// p.profile_id as a deterministic tie-breaker so paged OFFSET/LIMIT traversal never
    /// duplicates or skips rows across pages, even when sorted values repeat.
    /// </summary>
    private static string BuildOrderClause(GallerySortOrder sortOrder) => sortOrder switch
    {
        GallerySortOrder.DisplayNameAsc =>
            "ORDER BY coalesce(p.display_name, '') ASC, p.profile_id ASC",
        GallerySortOrder.DisplayNameDesc =>
            "ORDER BY coalesce(p.display_name, '') DESC, p.profile_id DESC",
        GallerySortOrder.UpdatedAtDesc =>
            "ORDER BY p.updated_at_ms DESC, p.profile_id DESC",
        GallerySortOrder.UpdatedAtAsc =>
            "ORDER BY p.updated_at_ms ASC, p.profile_id ASC",
        GallerySortOrder.RatingDesc =>
            "ORDER BY coalesce(p.rating, -1) DESC, p.updated_at_ms DESC, p.profile_id DESC",
        GallerySortOrder.RatingAsc =>
            "ORDER BY coalesce(p.rating, -1) ASC, p.updated_at_ms DESC, p.profile_id ASC",
        GallerySortOrder.MediaCountDesc =>
            "ORDER BY COALESCE(oc.active_owned_count, 0) DESC, p.profile_id DESC",
        GallerySortOrder.AddedToLibraryDesc =>
            "ORDER BY p.created_at_ms DESC, p.profile_id DESC",
        _ => "ORDER BY p.created_at_ms DESC, p.profile_id DESC",
    };

    // ── Tags ────────────────────────────────────────────────────────────────────

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

    private static async Task<Dictionary<Guid, List<string>>> LoadTagsForProfilesAsync(
        SqliteConnection connection,
        IEnumerable<Guid> profileIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<Guid, List<string>>();
        var ids = profileIds.Select(DbGuid.Format).Distinct().ToList();
        if (ids.Count == 0)
        {
            return map;
        }

        await using var command = connection.CreateCommand();
        var inClause = string.Join(",", ids.Select((_, idx) => $"$id{idx}"));
        command.CommandText = $"""
            SELECT pt.profile_id, t.name
            FROM profile_tags pt
            JOIN tags t ON pt.tag_id = t.tag_id
            WHERE pt.profile_id IN ({inClause})
            ORDER BY t.name ASC;
            """;
        for (var i = 0; i < ids.Count; i++)
        {
            command.Parameters.AddWithValue($"$id{i}", ids[i]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var profileId = DbGuid.Parse(reader.GetString(0));
            var tagName = reader.GetString(1);
            if (!map.TryGetValue(profileId, out var list))
            {
                list = [];
                map[profileId] = list;
            }
            list.Add(tagName);
        }

        return map;
    }

    private static string EscapeLike(string input) =>
        input.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);
}

public sealed record HomeSpotlightCandidateReadModel(
    GalleryProfileSummary Summary,
    string? OverviewExcerpt);
