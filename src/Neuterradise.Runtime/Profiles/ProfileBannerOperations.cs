using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Profiles;

public sealed record ProfileBannerEntry(Guid MediaId, int SortOrder, string Origin, long CreatedAtMs, long UpdatedAtMs);

/// <summary>Catalog-owned Banner membership, durable Add subscriptions and rendition readiness.</summary>
public sealed class ProfileBannerOperations(CatalogDb catalog, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    public static bool FitsHover(int? width, int? height) => width is > 0 && height is > 0
        && Math.Max(width.Value, height.Value) <= 854 && Math.Min(width.Value, height.Value) <= 480;

    public async Task<IReadOnlyList<ProfileBannerEntry>> ReadEntriesAsync(Guid profileId, CancellationToken ct = default)
    {
        await using var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT media_id,sort_order,origin,created_at_ms,updated_at_ms FROM profile_banner_entries WHERE profile_id=$profile ORDER BY sort_order,created_at_ms,media_id;";
        command.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
        var result = new List<ProfileBannerEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(new(DbGuid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
        return result;
    }

    public async Task<MediaAssetRecord?> ResolveRenditionAsync(Guid profileId, Guid mediaId, Guid? importUnitId = null, CancellationToken ct = default)
    {
        var source = await catalog.MediaReads.GetMediaSourceAsync(mediaId, ct).ConfigureAwait(false);
        if (source?.ResolveManagedPath(catalog.Paths) is not { } original || !File.Exists(original)) return null;
        await using var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT metadata.width,metadata.height FROM media m JOIN media_metadata metadata ON metadata.media_id=m.media_id
            JOIN profile_media pm ON pm.media_id=m.media_id JOIN profiles p ON p.profile_id=pm.profile_id
            WHERE m.media_id=$media AND pm.profile_id=$profile AND m.state='ACTIVE' AND m.trashed_at_ms IS NULL
              AND p.kind='NORMAL' AND p.trashed_at_ms IS NULL
              AND (pm.publication_import_unit_id IS NULL OR pm.publication_import_unit_id=$unit
                   OR (p.visibility='DRAFT' AND EXISTS (SELECT 1 FROM import_units u WHERE u.destination_profile_id=p.profile_id
                       AND u.import_unit_id=pm.publication_import_unit_id AND u.destination_kind='NEW_NORMAL')));
            """;
        command.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$media", DbGuid.Format(mediaId));
        command.Parameters.AddWithValue("$unit", importUnitId is { } unit ? DbGuid.Format(unit) : DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var small = FitsHover(reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1));
        await reader.DisposeAsync().ConfigureAwait(false);
        var hover = await catalog.MediaAssetReads.GetOwnedMediaAssetAsync(mediaId, MediaAssetRole.Hover, ct).ConfigureAwait(false);
        if (!Available(hover) || hover!.SourceTimestampMs is null) return null;
        var banner = await catalog.MediaAssetReads.GetOwnedMediaAssetAsync(mediaId, MediaAssetRole.Banner, ct).ConfigureAwait(false);
        if (Available(banner) && banner!.SourceTimestampMs == hover.SourceTimestampMs) return banner;
        return small && hover.ContractVersion >= 2 && FitsHover(hover.PixelWidth, hover.PixelHeight) ? hover : null;
    }

    public bool Available(MediaAssetRecord? asset)
    {
        if (asset?.State != MediaAssetState.Ready || !MediaAssetContracts.IsSupported(asset.Role, asset.ContractVersion)) return false;
        try
        {
            var path = catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, asset.RelativePath);
            return File.Exists(path) && new FileInfo(path).Length == asset.ByteLength;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return false; }
    }

    internal async Task<bool> IsSmallSourceAsync(Guid mediaId, CancellationToken ct)
    {
        await using var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT width,height FROM media_metadata WHERE media_id=$media;";
        command.Parameters.AddWithValue("$media", DbGuid.Format(mediaId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            && FitsHover(reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1));
    }

    internal async Task<Dictionary<Guid, Guid>> EnsureInitialAsync(Guid unitId, CancellationToken ct)
    {
        await using var unitLease = await catalog.ImportUnitMutations.EnterAsync(unitId, ct).ConfigureAwait(false);
        var selected = new Dictionary<Guid, Guid>();
        var ranked = new List<Guid>();
        Guid profileId;
        await using (var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var destination = connection.CreateCommand();
            destination.CommandText = "SELECT u.destination_profile_id FROM import_units u JOIN profiles p ON p.profile_id=u.destination_profile_id WHERE u.import_unit_id=$unit AND u.destination_kind='NEW_NORMAL' AND p.visibility='DRAFT' AND p.trashed_at_ms IS NULL;";
            destination.Parameters.AddWithValue("$unit", DbGuid.Format(unitId));
            if (await destination.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string profile) return selected;
            profileId = DbGuid.Parse(profile);
            await using var candidates = connection.CreateCommand();
            candidates.CommandText = """
                SELECT DISTINCT m.media_id,metadata.width,metadata.height,
                    COALESCE((SELECT MAX(CASE WHEN i.profile_id=$profile THEN 2.0 ELSE 1.0 END+COALESCE(f.confidence,0))
                        FROM face_detections f LEFT JOIN identities i ON i.identity_id=f.confirmed_identity_id WHERE f.media_id=m.media_id),0),m.created_at_ms
                FROM media m JOIN media_metadata metadata ON metadata.media_id=m.media_id
                JOIN profile_media pm ON pm.media_id=m.media_id
                WHERE pm.profile_id=$profile AND pm.publication_import_unit_id=$unit AND m.media_type='VIDEO'
                  AND m.state='ACTIVE' AND m.trashed_at_ms IS NULL;
                """;
            candidates.Parameters.AddWithValue("$profile", profile);
            candidates.Parameters.AddWithValue("$unit", DbGuid.Format(unitId));
            await using var reader = await candidates.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var sources = new List<(Guid Id, double Score, long Created)>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                sources.Add((DbGuid.Parse(reader.GetString(0)), ProfileAppearanceRules.SourceCandidateScore(
                    Convert.ToDouble(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture),
                    reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2)), reader.GetInt64(4)));
            ranked.AddRange(sources.OrderByDescending(item => item.Score).ThenBy(item => item.Created).ThenBy(item => item.Id).Select(item => item.Id));
        }
        var entries = await ReadEntriesAsync(profileId, ct).ConfigureAwait(false);
        var count = entries.Count(entry => entry.Origin == "INITIAL");
        var attempted = entries.Select(entry => entry.MediaId).ToHashSet();
        await using (var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT r.media_id,r.job_id,j.state FROM profile_banner_requests r JOIN jobs j ON j.job_id=r.job_id WHERE r.profile_id=$profile AND r.origin='INITIAL';";
            command.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var media = DbGuid.Parse(reader.GetString(0));
                attempted.Add(media);
                if (reader.GetString(2) is "PENDING" or "RUNNABLE" or "RUNNING" or "PAUSED" or "FAILED_RETRYABLE" or "WAITING_DEPENDENCY" or "WAITING_USER_INPUT")
                {
                    selected[media] = DbGuid.Parse(reader.GetString(1));
                    count++;
                }
            }
        }
        foreach (var media in ranked.Where(media => !attempted.Contains(media)))
        {
            if (count >= 3) break;
            selected[media] = await ScheduleAsync(profileId, media, "INITIAL", ranked.IndexOf(media), ct).ConfigureAwait(false);
            count++;
        }
        return selected;
    }

    public async Task<Guid?> AddAsync(Guid profileId, Guid mediaId, CancellationToken ct = default)
    {
        using var admission = catalog.MutationAdmission.Enter(nameof(AddAsync));
        var hover = await catalog.MediaAssetReads.GetPublicMediaAssetAsync(mediaId, MediaAssetRole.Hover, ct).ConfigureAwait(false);
        if (!Available(hover) || hover!.SourceTimestampMs is null)
            throw new InvalidDataException("BANNER_HOVER_UNAVAILABLE");
        // Relation eligibility is checked independently of the derivative's public visibility.
        var eligible = (await new ProfileMediaCandidateReads(catalog).ReadAllEligibleAsync(profileId, ct).ConfigureAwait(false))
            .BannerHovers.Any(candidate => candidate.MediaId == mediaId);
        if (!eligible) throw new InvalidDataException("BANNER_PROFILE_RELATION_UNAVAILABLE");
        var source = await catalog.MediaReads.GetMediaSourceAsync(mediaId, ct).ConfigureAwait(false);
        if (source?.ResolveManagedPath(catalog.Paths) is not { } path || !File.Exists(path))
            throw new InvalidDataException("BANNER_ORIGINAL_MISSING");
        if (hover.ContractVersion == 1 && await IsSmallSourceAsync(mediaId, ct).ConfigureAwait(false))
            await new MediaAssetWrites(catalog).TryAcceptHoverContractAsync(mediaId, ct).ConfigureAwait(false);
        var ready = await ResolveRenditionAsync(profileId, mediaId, ct: ct).ConfigureAwait(false);
        if (ready is not null)
        {
            await CommitMembershipAsync(profileId, mediaId, "ADDED", null, ct).ConfigureAwait(false);
            return null;
        }
        var jobId = await ScheduleAsync(profileId, mediaId, "ADDED", null, ct).ConfigureAwait(false);
        if (await ResolveRenditionAsync(profileId, mediaId, ct: ct).ConfigureAwait(false) is not null)
        {
            await CompleteRequestsAsync(mediaId, ct).ConfigureAwait(false);
            return null;
        }
        return jobId;
    }

    internal async Task<Guid> ScheduleAsync(Guid profileId, Guid mediaId, string origin, int? order, CancellationToken ct)
    {
        await using var lease = await catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false);
        await using var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, catalog.WriteCoordinator);
        await using var read = transaction.CreateCommand("SELECT job_id FROM jobs WHERE owner_id=$media AND kind='GenerateBanner' AND state IN ('PENDING','RUNNABLE','RUNNING','PAUSED','FAILED_RETRYABLE','WAITING_DEPENDENCY','WAITING_USER_INPUT') ORDER BY created_at_ms LIMIT 1;");
        read.Parameters.AddWithValue("$media", DbGuid.Format(mediaId));
        var existing = await read.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        var job = existing is null ? Guid.NewGuid() : DbGuid.Parse(existing);
        var now = DbTime.Format(_time.GetUtcNow());
        if (existing is null)
        {
            await using var insert = transaction.CreateCommand("""
                INSERT INTO jobs(job_id,kind,lane,state,priority,owner_type,owner_id,attempt,max_attempts,checkpoint_json,created_at_ms)
                VALUES($job,'GenerateBanner','MEDIA','PENDING',$priority,'Media',$media,0,3,'{"schemaVersion":1,"role":"BANNER","contractVersion":2}',$now);
                """);
            insert.Parameters.AddWithValue("$job", DbGuid.Format(job));
            insert.Parameters.AddWithValue("$media", DbGuid.Format(mediaId));
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$priority", JobPriorityPolicy.PriorityVisible);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using var subscribe = transaction.CreateCommand("""
            INSERT INTO profile_banner_requests(profile_id,media_id,job_id,origin,sort_order)
            VALUES($profile,$media,$job,$origin,COALESCE($sort,(SELECT COALESCE(MAX(sort_order),-1)+1 FROM (SELECT sort_order FROM profile_banner_entries WHERE profile_id=$profile UNION ALL SELECT sort_order FROM profile_banner_requests WHERE profile_id=$profile))))
            ON CONFLICT(profile_id,media_id) DO UPDATE SET job_id=excluded.job_id;
            """);
        subscribe.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
        subscribe.Parameters.AddWithValue("$media", DbGuid.Format(mediaId));
        subscribe.Parameters.AddWithValue("$job", DbGuid.Format(job));
        subscribe.Parameters.AddWithValue("$origin", origin);
        subscribe.Parameters.AddWithValue("$sort", (object?)order ?? DBNull.Value);
        await subscribe.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        JobSignals.Raise();
        return job;
    }

    internal async Task<bool> CompleteRequestsAsync(Guid mediaId, CancellationToken ct)
    {
        var requests = new List<(Guid Profile, string Origin, int Order)>();
        await using (var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT profile_id,origin,sort_order FROM profile_banner_requests WHERE media_id=$media;";
            command.Parameters.AddWithValue("$media", DbGuid.Format(mediaId));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                requests.Add((DbGuid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2)));
        }
        var complete = true;
        foreach (var request in requests)
        {
            if (await ResolveRenditionAsync(request.Profile, mediaId, ct: ct).ConfigureAwait(false) is not null)
                await CommitMembershipAsync(request.Profile, mediaId, request.Origin, request.Order, ct).ConfigureAwait(false);
            else complete = false;
        }
        return complete;
    }

    internal async Task CommitMembershipAsync(Guid profileId, Guid mediaId, string origin, int? order, CancellationToken ct)
    {
        if (await ResolveRenditionAsync(profileId, mediaId, ct: ct).ConfigureAwait(false) is null)
            throw new InvalidDataException("BANNER_NOT_READY");
        await using (var lease = await catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false))
        await using (var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (var transaction = CatalogTransaction.Begin(connection, catalog.WriteCoordinator))
        {
            await using var insert = transaction.CreateCommand("""
                INSERT INTO profile_banner_entries(profile_id,media_id,sort_order,origin,created_at_ms,updated_at_ms)
                VALUES($profile,$media,COALESCE($sort,(SELECT COALESCE(MAX(sort_order),-1)+1 FROM (SELECT sort_order FROM profile_banner_entries WHERE profile_id=$profile UNION ALL SELECT sort_order FROM profile_banner_requests WHERE profile_id=$profile))),$origin,$now,$now)
                ON CONFLICT(profile_id,media_id) DO NOTHING;
                """);
            insert.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
            insert.Parameters.AddWithValue("$media", DbGuid.Format(mediaId));
            insert.Parameters.AddWithValue("$sort", (object?)order ?? DBNull.Value);
            insert.Parameters.AddWithValue("$origin", origin);
            insert.Parameters.AddWithValue("$now", DbTime.Format(_time.GetUtcNow()));
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            transaction.QueueInvalidation(new CatalogInvalidation(Guid.Empty, [profileId], CatalogInvalidationDomain.Appearance, 0));
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        var result = await new ProfileManifestWriter(catalog.Paths).RegenerateManifestAsync(catalog, profileId, ct).ConfigureAwait(false);
        if (result.Status != StorageOperationStatus.Success) throw new IOException("BANNER_MANIFEST_PENDING");
        await using var finalLease = await catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false);
        await using var finalConnection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var finish = finalConnection.CreateCommand();
        finish.CommandText = "DELETE FROM profile_banner_requests WHERE profile_id=$profile AND media_id=$media AND origin='ADDED';";
        finish.Parameters.AddWithValue("$profile", DbGuid.Format(profileId));
        finish.Parameters.AddWithValue("$media", DbGuid.Format(mediaId));
        await finish.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
