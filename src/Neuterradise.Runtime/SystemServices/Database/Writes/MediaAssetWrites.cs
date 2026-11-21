using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Jobs;

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
        if (role is MediaAssetRole.Hover or MediaAssetRole.Banner && sourceTimestampMs is null or < 0)
            throw new CatalogInvariantException("Video renditions require an authoritative source timestamp.");
        if (role == MediaAssetRole.Banner && contractVersion != 2)
            throw new CatalogInvariantException("Banner requires rendition contract 2.");
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
        if (role is (MediaAssetRole.Hover or MediaAssetRole.Banner) && mediaType != "VIDEO")
            throw new CatalogInvariantException("Only VIDEO can own Hover.");
        string? modelSourceHash = null;
        if (role == MediaAssetRole.ModelRender)
        {
            if (contractVersion != ModelRenderContainer.ContractVersion || sourceTimestampMs is not null
                || reader.GetString(5) != "ACTIVE" || mediaType != "MODEL"
                || reader.IsDBNull(3) || !ModelRenderEligibility.IsEligible(reader.GetString(3), reader.GetString(4))
                || reader.IsDBNull(2))
                throw new CatalogInvariantException("ModelRender requires a committed supported Model and contract 1.");
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
            path => _validator.ValidateAsync(path, role, ct, modelSourceHash, contractVersion),
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

    public async Task<bool> PublishModelRenderRefreshAsync(MediaAssetRecord expected, string sourceHash,
        string preparedTempPath, CancellationToken ct)
    {
        if (expected.Role != MediaAssetRole.ModelRender || expected.State != MediaAssetState.Ready)
            throw new ArgumentException("Refresh requires a READY ModelRender.", nameof(expected));
        var temp = RequireTemp(preparedTempPath, VaultPathArea.TempMediaAssets);
        var candidate = await _validator.ValidateAsync(temp, MediaAssetRole.ModelRender, ct, sourceHash).ConfigureAwait(false);
        if (candidate.NeedsRefresh) throw new InvalidDataException("The replacement ModelRender is obsolete.");

        await using var lease = await _catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false);
        await RecoverModelRenderSwapCoreAsync(expected.MediaId, ct).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);
        await using var update = transaction.CreateCommand("""
            UPDATE media_assets SET byte_length=$length, sha256=$hash, updated_at_ms=$now, row_version=row_version+1
            WHERE media_id=$media AND media_asset_id=$asset AND role='MODEL_RENDER' AND state='READY'
                AND relative_path=$path AND sha256=$oldHash AND byte_length=$oldLength AND contract_version=1
                AND EXISTS (SELECT 1 FROM media m WHERE m.media_id=$media AND m.state='ACTIVE'
                    AND m.trashed_at_ms IS NULL AND m.sha256=$source);
            """);
        update.Parameters.AddWithValue("$media", DbGuid.Format(expected.MediaId));
        update.Parameters.AddWithValue("$asset", DbGuid.Format(expected.MediaAssetId));
        update.Parameters.AddWithValue("$path", expected.RelativePath);
        update.Parameters.AddWithValue("$oldHash", expected.Sha256);
        update.Parameters.AddWithValue("$oldLength", expected.ByteLength);
        update.Parameters.AddWithValue("$source", sourceHash);
        update.Parameters.AddWithValue("$length", candidate.ByteLength);
        update.Parameters.AddWithValue("$hash", candidate.Sha256);
        update.Parameters.AddWithValue("$now", DbTime.Format(_time.GetUtcNow()));
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return false;

        var target = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, expected.RelativePath);
        var old = await _validator.ValidateAsync(target, MediaAssetRole.ModelRender, ct, sourceHash).ConfigureAwait(false);
        if (!old.NeedsRefresh || old.Sha256 != expected.Sha256 || old.ByteLength != expected.ByteLength)
            throw new InvalidDataException("The ModelRender changed while its replacement was prepared.");

        var backup = ModelRenderBackupPath(expected.MediaId);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        ct.ThrowIfCancellationRequested();
        try
        {
            File.Replace(temp, target, backup);
            transaction.QueueInvalidation(CatalogInvalidationDomain.ModelRender, expected.MediaId);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            await RecoverModelRenderSwapCoreAsync(expected.MediaId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        TryDeleteModelRenderBackup(backup);
        return true;
    }

    public async Task InspectModelRenderForRefreshAsync(Guid mediaId, string path, string sourceHash, CancellationToken ct)
    {
        var valid = await new ModelRenderValidationReceipts(_catalog)
            .ValidateAsync(path, sourceHash, ct, backgroundIo: true).ConfigureAwait(false);
        if (ModelRenderContainer.NeedsAlphaMipRefresh(valid.Header))
            await QueueModelRenderRefreshAsync(mediaId, valid.Sha256, ct).ConfigureAwait(false);
    }

    public async Task<bool> QueueModelRenderRefreshAsync(Guid mediaId, string expectedHash, CancellationToken ct)
    {
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO jobs(job_id,kind,lane,state,priority,owner_type,owner_id,attempt,max_attempts,checkpoint_json,created_at_ms)
            SELECT $id,'GenerateModelMediaAssets','MEDIA','PENDING',$priority,'Media',$media,0,$attempts,'{}',$now
            WHERE EXISTS (SELECT 1 FROM media_assets ma JOIN media m ON m.media_id=ma.media_id
                WHERE ma.media_id=$media AND ma.role='MODEL_RENDER' AND ma.state='READY' AND ma.sha256=$hash
                AND m.state='ACTIVE' AND m.trashed_at_ms IS NULL)
            AND NOT EXISTS (SELECT 1 FROM jobs WHERE owner_type='Media' AND owner_id=$media
                AND kind='GenerateModelMediaAssets' AND state IN ('PENDING','RUNNABLE','RUNNING','PAUSED',
                    'FAILED_RETRYABLE','WAITING_DEPENDENCY','WAITING_USER_INPUT'));
            """;
        insert.Parameters.AddWithValue("$id", DbGuid.Format(Guid.NewGuid()));
        insert.Parameters.AddWithValue("$media", DbGuid.Format(mediaId));
        insert.Parameters.AddWithValue("$hash", expectedHash);
        insert.Parameters.AddWithValue("$priority", JobPriorityPolicy.PriorityBackground);
        insert.Parameters.AddWithValue("$attempts", JobRetryPolicy.DefaultMaxAttempts);
        insert.Parameters.AddWithValue("$now", DbTime.Format(_time.GetUtcNow()));
        var queued = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        if (queued) JobSignals.Raise();
        return queued;
    }

    public async Task<bool> TryAcceptHoverContractAsync(Guid mediaId, CancellationToken ct = default)
    {
        var asset = await _catalog.MediaAssetReads.GetOwnedMediaAssetAsync(mediaId, MediaAssetRole.Hover, ct).ConfigureAwait(false);
        if (asset?.State != MediaAssetState.Ready || asset.ContractVersion != 1 || asset.SourceTimestampMs is null) return false;
        try
        {
            var path = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, asset.RelativePath);
            var validated = await _validator.ValidateAsync(path, MediaAssetRole.Hover, ct, contractVersion: 2).ConfigureAwait(false);
            if (validated.Sha256 != asset.Sha256 || validated.ByteLength != asset.ByteLength) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE media_assets SET contract_version=2,row_version=row_version+1,updated_at_ms=$now
            WHERE media_asset_id=$asset AND role='HOVER' AND state='READY' AND contract_version=1
              AND sha256=$sha AND byte_length=$bytes AND relative_path=$path;
            """;
        update.Parameters.AddWithValue("$asset", DbGuid.Format(asset.MediaAssetId));
        update.Parameters.AddWithValue("$sha", asset.Sha256);
        update.Parameters.AddWithValue("$bytes", asset.ByteLength);
        update.Parameters.AddWithValue("$path", asset.RelativePath);
        update.Parameters.AddWithValue("$now", DbTime.Format(_time.GetUtcNow()));
        return await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
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

    internal async Task RecoverModelRenderSwapsAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(ModelRenderBackupPath(Guid.Empty))!;
        if (!Directory.Exists(directory)) return;
        foreach (var backup in Directory.EnumerateFiles(directory, "*.nfig"))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(backup), "N", out var mediaId)) continue;
            await using var lease = await _catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false);
            await RecoverModelRenderSwapCoreAsync(mediaId, ct).ConfigureAwait(false);
        }
    }

    private string ModelRenderBackupPath(Guid mediaId) =>
        _catalog.Paths.ResolveContainedPath(VaultPathArea.TempMediaAssets, $"model-render-swaps/{mediaId:N}.nfig");

    private async Task RecoverModelRenderSwapCoreAsync(Guid mediaId, CancellationToken ct)
    {
        var backup = ModelRenderBackupPath(mediaId);
        if (!File.Exists(backup)) return;
        var current = await _catalog.MediaAssetReads.GetOwnedMediaAssetAsync(mediaId, MediaAssetRole.ModelRender, ct).ConfigureAwait(false);
        if (current is not { State: MediaAssetState.Ready }) return;
        var target = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, current.RelativePath);
        if (await MatchesModelRenderAsync(target, current, ct).ConfigureAwait(false))
        {
            TryDeleteModelRenderBackup(backup);
            return;
        }
        if (!await MatchesModelRenderAsync(backup, current, ct).ConfigureAwait(false))
            throw new InvalidDataException("Neither ModelRender copy matches the committed fingerprint.");

        ct.ThrowIfCancellationRequested();
        File.Move(backup, target, overwrite: true);
    }

    private static async Task<bool> MatchesModelRenderAsync(string path, MediaAssetRecord expected, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expected.ByteLength) return false;
        try
        {
            var valid = await ModelRenderContainer.ValidateAsync(path, ct: ct).ConfigureAwait(false);
            return valid.Sha256 == expected.Sha256 && valid.ByteLength == expected.ByteLength;
        }
        catch (InvalidDataException) { return false; }
    }

    private static void TryDeleteModelRenderBackup(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
