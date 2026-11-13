using Microsoft.Data.Sqlite;

namespace Neuterradise.App.SystemServices.Database.Reads;

internal sealed record ArtworkAsset(Guid Id, string RelativePath, long ByteLength);
internal sealed record ProfileArtworkAssets(ArtworkAsset? Cover, ArtworkAsset? BannerPoster, Guid? HoverId, ArtworkAsset? ReadyHover);

public sealed class MediaAssetReads
{
    private readonly CatalogDb _catalog;

    public MediaAssetReads(CatalogDb catalog) => _catalog = catalog;

    internal async Task<Dictionary<Guid, ProfileArtworkAssets>> GetPublicProfileArtworkAsync(
        IReadOnlyCollection<Guid> profileIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, ProfileArtworkAssets>(profileIds.Count);
        if (profileIds.Count == 0) return result;

        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        foreach (var id in profileIds)
            command.Parameters.AddWithValue($"$p{command.Parameters.Count}", DbGuid.Format(id));

        var keys = string.Join(", ", command.Parameters.Cast<SqliteParameter>().Select(p => p.ParameterName));
        command.CommandText = $"""
            SELECT p.profile_id,
                   cover.media_asset_id, cover.relative_path, cover.byte_length,
                   poster.media_asset_id, poster.relative_path, poster.byte_length,
                   hover.media_asset_id,
                   CASE WHEN hover.state = 'READY' THEN hover.media_asset_id END,
                   hover.relative_path, hover.byte_length
            FROM profiles p
            JOIN profile_appearance a ON a.profile_id = p.profile_id
            LEFT JOIN media_assets cover
              ON cover.media_asset_id = a.cover_media_asset_id AND cover.role = 'THUMBNAIL'
             AND cover.state = 'READY' AND EXISTS (
                SELECT 1 FROM profile_media pm JOIN media m ON m.media_id = pm.media_id
                WHERE pm.profile_id = p.profile_id AND pm.media_id = cover.media_id
                  AND pm.publication_import_unit_id IS NULL AND m.state = 'ACTIVE')
            LEFT JOIN media_assets hover
              ON hover.media_asset_id = a.banner_media_asset_id AND hover.role = 'HOVER'
             AND EXISTS (
                SELECT 1 FROM profile_media pm JOIN media m ON m.media_id = pm.media_id
                WHERE pm.profile_id = p.profile_id AND pm.media_id = hover.media_id
                  AND pm.publication_import_unit_id IS NULL AND m.state = 'ACTIVE')
            LEFT JOIN media_assets poster
              ON poster.media_id = hover.media_id AND poster.role = 'THUMBNAIL' AND poster.state = 'READY'
            WHERE p.profile_id IN ({keys}) AND p.visibility = 'PUBLISHED' AND p.trashed_at_ms IS NULL;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(DbGuid.Parse(reader.GetString(0)), new ProfileArtworkAssets(
                ReadArtwork(reader, 1), ReadArtwork(reader, 4), OptionalGuid(reader, 7), ReadArtwork(reader, 8)));
        }
        return result;
    }

    private static ArtworkAsset? ReadArtwork(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null
        : new ArtworkAsset(DbGuid.Parse(reader.GetString(ordinal)), reader.GetString(ordinal + 1), reader.GetInt64(ordinal + 2));

    public Task<MediaAssetRecord?> GetPublicMediaAssetAsync(Guid assetId, MediaAssetRole role,
        CancellationToken cancellationToken = default) =>
        ReadMediaAsync(assetId, role, null, cancellationToken);

    // Selection identity survives derivative repair so callers can resolve the source thumbnail.
    // Playback readiness remains the rendition resolver's responsibility.
    public async Task<MediaAssetRecord?> GetPublicSelectedMediaAsync(Guid profileId, Guid mediaMediaId,
        CancellationToken ct = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ma.media_asset_id, ma.media_id, ma.role, ma.contract_version, ma.state,
                   ma.relative_path, ma.byte_length, ma.sha256, ma.pixel_width,
                   ma.pixel_height, ma.duration_ms, ma.source_timestamp_ms
            FROM media_assets ma
            JOIN media m ON m.media_id = ma.media_id
            JOIN profile_media pm ON pm.media_id = m.media_id
            JOIN profiles p ON p.profile_id = pm.profile_id
            JOIN profile_appearance appearance ON appearance.profile_id = p.profile_id
            WHERE ma.media_asset_id = $asset AND m.state = 'ACTIVE'
              AND pm.profile_id = $profile AND pm.publication_import_unit_id IS NULL
              AND p.visibility = 'PUBLISHED' AND p.trashed_at_ms IS NULL
              AND (appearance.cover_media_asset_id = ma.media_asset_id
                   OR appearance.banner_media_asset_id = ma.media_asset_id)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$asset", DbGuid.Format(mediaMediaId));
        command.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadMedia(reader) : null;
    }

    public async Task<MediaAssetRecord?> GetOwnedMediaAssetAsync(Guid assetId, MediaAssetRole role,
        CancellationToken ct = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT aa.media_asset_id, aa.media_id, aa.role, aa.contract_version, aa.state,
                   aa.relative_path, aa.byte_length, aa.sha256, aa.pixel_width,
                   aa.pixel_height, aa.duration_ms, aa.source_timestamp_ms
            FROM media_assets aa JOIN media a ON a.media_id = aa.media_id
            WHERE aa.media_id = $asset AND aa.role = $role
              AND a.state IN ('CANDIDATE', 'ACTIVE');
            """;
        command.Parameters.AddWithValue("$asset", DbGuid.Format(assetId));
        command.Parameters.AddWithValue("$role", DbEnum.Format(role));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadMedia(reader) : null;
    }

    public Task<MediaAssetRecord?> GetImportCandidateAsync(Guid importUnitId, Guid assetId,
        MediaAssetRole role, CancellationToken cancellationToken = default)
    {
        if (importUnitId == Guid.Empty) throw new ArgumentException("Import Unit ID is required.", nameof(importUnitId));
        return ReadMediaAsync(assetId, role, importUnitId, cancellationToken);
    }

    private async Task<MediaAssetRecord?> ReadMediaAsync(Guid assetId, MediaAssetRole role,
        Guid? importUnitId, CancellationToken ct)
    {
        if (assetId == Guid.Empty) return null;
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = importUnitId is null ?
            """
            SELECT aa.media_asset_id, aa.media_id, aa.role, aa.contract_version, aa.state,
                   aa.relative_path, aa.byte_length, aa.sha256, aa.pixel_width,
                   aa.pixel_height, aa.duration_ms, aa.source_timestamp_ms
            FROM media_assets aa JOIN media a ON a.media_id = aa.media_id
            WHERE aa.media_id = $assetId AND aa.role = $role AND aa.state = 'READY'
              AND a.state = 'ACTIVE'
              AND EXISTS (SELECT 1 FROM profile_media pa JOIN profiles p ON p.profile_id = pa.profile_id
                          WHERE pa.media_id = a.media_id AND pa.publication_import_unit_id IS NULL
                            AND p.visibility = 'PUBLISHED' AND p.trashed_at_ms IS NULL);
            """ :
            """
            SELECT aa.media_asset_id, aa.media_id, aa.role, aa.contract_version, aa.state,
                   aa.relative_path, aa.byte_length, aa.sha256, aa.pixel_width,
                   aa.pixel_height, aa.duration_ms, aa.source_timestamp_ms
            FROM media_assets aa JOIN media a ON a.media_id = aa.media_id
            LEFT JOIN import_items i ON i.candidate_media_id = a.media_id
                AND i.import_unit_id = $importUnitId
            WHERE aa.media_id = $assetId AND aa.role = $role AND aa.state = 'READY'
              AND ((a.state = 'CANDIDATE' AND i.import_item_id IS NOT NULL)
                OR (a.state = 'ACTIVE' AND EXISTS (
                    SELECT 1 FROM profile_media pa WHERE pa.media_id = a.media_id
                      AND pa.publication_import_unit_id = $importUnitId)));
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        command.Parameters.AddWithValue("$role", DbEnum.Format(role));
        if (importUnitId is { } id) command.Parameters.AddWithValue("$importUnitId", DbGuid.Format(id));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadMedia(reader) : null;
    }

    public async Task<ProfileMediaSelection?> GetPublicSelectionAsync(Guid profileId,
        CancellationToken ct = default)
    {
        if (profileId == Guid.Empty) return null;
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.profile_id, a.cover_media_asset_id, a.banner_media_asset_id
            FROM profile_appearance a JOIN profiles p ON p.profile_id = a.profile_id
            WHERE a.profile_id = $profileId AND p.visibility = 'PUBLISHED'
              AND p.trashed_at_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new ProfileMediaSelection(DbGuid.Parse(reader.GetString(0)),
            OptionalGuid(reader, 1), OptionalGuid(reader, 2));
    }

    internal static MediaAssetRecord ReadMedia(SqliteDataReader r) => new(
        DbGuid.Parse(r.GetString(0)), DbGuid.Parse(r.GetString(1)),
        DbEnum.ParseMediaAssetRole(r.GetString(2)), r.GetInt32(3),
        DbEnum.ParseMediaAssetState(r.GetString(4)), r.GetString(5), r.GetInt64(6),
        r.GetString(7), OptionalInt(r, 8), OptionalInt(r, 9), OptionalLong(r, 10), OptionalLong(r, 11));

    private static Guid? OptionalGuid(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DbGuid.Parse(r.GetString(i));
    private static int? OptionalInt(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static long? OptionalLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}
