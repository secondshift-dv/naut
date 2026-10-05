using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Media.Model;

namespace Neuterradise.App.SystemServices.Database.Writes;

public sealed class MediaAssetWrites
{
    private readonly CatalogDb _catalog;
    private readonly MediaAssetFileValidator _validator;
    private readonly TimeProvider _time;

    public MediaAssetWrites(CatalogDb catalog, MediaAssetFileValidator? validator = null,
        TimeProvider? timeProvider = null)
    {
        _catalog = catalog;
        _validator = validator ?? new MediaAssetFileValidator();
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<MediaAssetRecord> PublishMediaAssetAsync(Guid assetId, MediaAssetRole role,
        int contractVersion, string preparedTempPath, long? sourceTimestampMs = null,
        CancellationToken ct = default)
    {
        if (contractVersion <= 0) throw new ArgumentOutOfRangeException(nameof(contractVersion));
        var temp = RequireTemp(preparedTempPath, VaultPathArea.TempMediaAssets);
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var owner = transaction.CreateCommand(
            "SELECT media_storage_token, media_type, sha256, current_managed_file_name, dependency_status, state FROM media WHERE media_id = $id AND state IN ('CANDIDATE','ACTIVE');");
        owner.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
        await using var reader = await owner.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new CatalogInvariantException("Artifact Media is not live.");
        var token = new MediaStorageToken(reader.GetString(0));
        var mediaType = reader.GetString(1);
        if (role == MediaAssetRole.Hover && mediaType != "VIDEO")
            throw new CatalogInvariantException("Only VIDEO can own Hover.");
        string? modelSourceHash = null;
        if (role == MediaAssetRole.ModelRender)
        {
            if (contractVersion != ModelRenderContainer.ContractVersion || sourceTimestampMs is not null
                || reader.GetString(5) != "ACTIVE" || mediaType != "MODEL"
                || reader.IsDBNull(3) || !ModelRenderEligibility.IsEligible(reader.GetString(3), reader.GetString(4))
                || reader.IsDBNull(2))
                throw new CatalogInvariantException("ModelRender requires a committed self-contained GLB and contract 1.");
            modelSourceHash = reader.GetString(2);
        }
        await reader.DisposeAsync().ConfigureAwait(false);

        var relative = new ManagedPathPlanner(_catalog.Paths.Root).PlanMediaAsset(token, role);
        var target = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, relative);
        await using var existing = transaction.CreateCommand(
            "SELECT state FROM media_assets WHERE media_id = $asset AND role = $role;");
        existing.Parameters.AddWithValue("$asset", DbGuid.Format(assetId));
        existing.Parameters.AddWithValue("$role", DbEnum.Format(role));
        var existingState = await existing.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (existingState == "READY")
            throw new CatalogInvariantException("READY artifact must be marked for repair before replacement.");
        var validated = await PublishAndValidateAsync(temp, target,
            path => _validator.ValidateAsync(path, role, ct, modelSourceHash),
            existingState == "NEEDS_REPAIR", ct).ConfigureAwait(false);
        var now = DbTime.Format(_time.GetUtcNow());
        var artifactId = Guid.NewGuid();
        await using var write = transaction.CreateCommand("""
            INSERT INTO media_assets(media_asset_id, media_id, role, contract_version, state,
                relative_path, byte_length, sha256, pixel_width, pixel_height, duration_ms,
                source_timestamp_ms, created_at_ms, updated_at_ms)
            VALUES($artifact, $asset, $role, $version, 'READY', $path, $bytes, $sha,
                $width, $height, $duration, $sourceTime, $now, $now)
            ON CONFLICT(media_id, role) DO UPDATE SET contract_version = excluded.contract_version,
                state = 'READY', relative_path = excluded.relative_path,
                byte_length = excluded.byte_length, sha256 = excluded.sha256,
                pixel_width = excluded.pixel_width, pixel_height = excluded.pixel_height,
                duration_ms = excluded.duration_ms, source_timestamp_ms = excluded.source_timestamp_ms,
                updated_at_ms = excluded.updated_at_ms, row_version = row_version + 1;
            """);
        write.Parameters.AddWithValue("$artifact", DbGuid.Format(artifactId));
        write.Parameters.AddWithValue("$asset", DbGuid.Format(assetId));
        write.Parameters.AddWithValue("$role", DbEnum.Format(role));
        write.Parameters.AddWithValue("$version", contractVersion);
        write.Parameters.AddWithValue("$path", relative);
        write.Parameters.AddWithValue("$bytes", validated.ByteLength);
        write.Parameters.AddWithValue("$sha", validated.Sha256);
        write.Parameters.AddWithValue("$width", (object?)validated.PixelWidth ?? DBNull.Value);
        write.Parameters.AddWithValue("$height", (object?)validated.PixelHeight ?? DBNull.Value);
        write.Parameters.AddWithValue("$duration", (object?)validated.DurationMs ?? DBNull.Value);
        write.Parameters.AddWithValue("$sourceTime", (object?)sourceTimestampMs ?? DBNull.Value);
        write.Parameters.AddWithValue("$now", now);
        await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using var read = transaction.CreateCommand("SELECT media_asset_id FROM media_assets WHERE media_id = $asset AND role = $role;");
        read.Parameters.AddWithValue("$asset", DbGuid.Format(assetId));
        read.Parameters.AddWithValue("$role", DbEnum.Format(role));
        var persistedId = DbGuid.Parse((string)(await read.ExecuteScalarAsync(ct).ConfigureAwait(false))!);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new MediaAssetRecord(persistedId, assetId, role, contractVersion,
            MediaAssetState.Ready, relative, validated.ByteLength, validated.Sha256,
            validated.PixelWidth, validated.PixelHeight, validated.DurationMs, sourceTimestampMs);
    }

    public async Task MarkMediaNeedsRepairAsync(Guid assetId, MediaAssetRole role,
        CancellationToken ct = default)
    {
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE media_assets SET state = 'NEEDS_REPAIR', updated_at_ms = $now,
                row_version = row_version + 1 WHERE media_id = $asset AND role = $role;
            """;
        command.Parameters.AddWithValue("$asset", DbGuid.Format(assetId));
        command.Parameters.AddWithValue("$role", DbEnum.Format(role));
        command.Parameters.AddWithValue("$now", DbTime.Format(_time.GetUtcNow()));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private string RequireTemp(string path, VaultPathArea area)
    {
        var root = _catalog.Paths.GetAreaRoot(area);
        var full = Path.GetFullPath(path);
        if (!RootPathRules.IsStrictDescendant(root, full))
            throw new ArgumentException("MediaAsset input must be operation-owned Vault temp bytes.", nameof(path));
        var relative = Path.GetRelativePath(root, full);
        return _catalog.Paths.ResolveContainedPath(area, relative);
    }

    private static async Task<ValidatedArtifactFile> PublishAndValidateAsync(string temp,
        string target, Func<string, Task<ValidatedArtifactFile>> validate, bool replace,
        CancellationToken ct)
    {
        var prepared = await validate(temp).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target) && !replace)
        {
            // A crash after the atomic move can leave validated bytes before the catalog commit.
            // Adopt only an exact canonical rebuild; changed or unrelated bytes remain preserved.
            var recovered = await validate(target).ConfigureAwait(false);
            if (prepared != recovered)
                throw new IOException("The existing MediaAsset target differs from the canonical prepared bytes.");
            ct.ThrowIfCancellationRequested();
            File.Delete(temp);
            return recovered;
        }
        File.Move(temp, target, overwrite: replace);
        var published = await validate(target).ConfigureAwait(false);
        if (prepared != published) throw new InvalidDataException("MediaAsset bytes changed during publication.");
        ct.ThrowIfCancellationRequested();
        return published;
    }
}
