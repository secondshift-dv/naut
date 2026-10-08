using Neuterradise.App.Import;
using Neuterradise.App.Import.Preparation;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed record MediaCapabilityRow(
    Guid MediaId,
    MediaCapability Capability,
    MediaCapabilityState State,
    Guid? JobId,
    string? SourceFingerprint);

public sealed record UnitCapabilitySummary(
    Guid UnitId,
    int TotalMedias,
    int MediasAllCapabilitiesTerminal,
    bool AllRequiredTerminal);

public sealed class CapabilityReads
{
    private readonly CatalogConnectionFactory _connectionFactory;

    public CapabilityReads(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
    }

    public CapabilityReads(CatalogConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<IReadOnlyList<MediaCapabilityRow>> GetMediaCapabilitiesAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT capability, state, job_id, source_fingerprint
            FROM media_capability_readiness
            WHERE media_id = $assetId;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var rows = new List<MediaCapabilityRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new MediaCapabilityRow(
                assetId,
                DbEnum.ParseMediaCapability(reader.GetString(0)),
                DbEnum.ParseMediaCapabilityState(reader.GetString(1)),
                reader.IsDBNull(2) ? null : DbGuid.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>
    /// Returns readiness over the complete eligible asset denominator, but only the canonical
    /// Required capability set gates readiness.
    /// </summary>
    public async Task<UnitCapabilitySummary> GetUnitCapabilitySummaryAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        var domainCommitted = await IsDomainCommittedAsync(unitId, cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH eligible_assets AS (
                SELECT DISTINCT CASE
                    WHEN ii.disposition = 'REUSED' AND ii.reused_media_id IS NOT NULL THEN ii.reused_media_id
                    ELSE ii.candidate_media_id
                END AS media_id
                FROM import_items ii
                WHERE ii.import_unit_id = $unitId
                  AND ii.disposition IN ('INCLUDED','REUSED')
                  AND ((ii.disposition='REUSED' AND ii.reused_media_id IS NOT NULL)
                    OR (ii.disposition='INCLUDED' AND ii.candidate_media_id IS NOT NULL))
            )
            SELECT ea.media_id, a.media_type, cr.capability, cr.state
            FROM eligible_assets ea
            JOIN media a ON a.media_id = ea.media_id
            LEFT JOIN media_capability_readiness cr ON cr.media_id = ea.media_id
            ORDER BY ea.media_id, cr.capability;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var media = new Dictionary<Guid, CapabilityMediaSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var assetId = DbGuid.Parse(reader.GetString(0));
            if (!media.TryGetValue(assetId, out var snapshot))
            {
                snapshot = new CapabilityMediaSnapshot(
                    DbEnum.ParseMediaType(reader.GetString(1)),
                    new Dictionary<MediaCapability, MediaCapabilityState>());
                media.Add(assetId, snapshot);
            }

            if (!reader.IsDBNull(2) && !reader.IsDBNull(3))
            {
                snapshot.States[DbEnum.ParseMediaCapability(reader.GetString(2))] =
                    DbEnum.ParseMediaCapabilityState(reader.GetString(3));
            }
        }

        var terminalMedias = 0;
        foreach (var snapshot in media.Values)
        {
            var required = CapabilityApplicability.GetRequiredForImportPhase(snapshot.MediaType, domainCommitted);
            if (required.All(capability =>
                    snapshot.States.TryGetValue(capability, out var state)
                    && state is MediaCapabilityState.Ready or MediaCapabilityState.NotApplicable))
            {
                terminalMedias++;
            }
        }

        return new UnitCapabilitySummary(
            unitId,
            media.Count,
            terminalMedias,
            media.Count > 0 && terminalMedias == media.Count);
    }

    /// <summary>
    /// True only when every Required capability has finished executing. READY and NOT_APPLICABLE
    /// satisfy readiness. FAILED is settled only when its backing job is no longer retryable or
    /// running; the failure then becomes a user-review blocker instead of an endless Preparing state.
    /// </summary>
    public async Task<bool> AreAllRequiredCapabilitiesSettledAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        var domainCommitted = await IsDomainCommittedAsync(unitId, cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH eligible_assets AS (
                SELECT DISTINCT CASE
                    WHEN ii.disposition = 'REUSED' AND ii.reused_media_id IS NOT NULL THEN ii.reused_media_id
                    ELSE ii.candidate_media_id
                END AS media_id
                FROM import_items ii
                WHERE ii.import_unit_id = $unitId
                  AND ii.disposition IN ('INCLUDED','REUSED')
                  AND ((ii.disposition='REUSED' AND ii.reused_media_id IS NOT NULL)
                    OR (ii.disposition='INCLUDED' AND ii.candidate_media_id IS NOT NULL))
            )
            SELECT ea.media_id, a.media_type, cr.capability, cr.state, j.state
            FROM eligible_assets ea
            JOIN media a ON a.media_id = ea.media_id
            LEFT JOIN media_capability_readiness cr ON cr.media_id = ea.media_id
            LEFT JOIN jobs j ON j.job_id = cr.job_id
            ORDER BY ea.media_id, cr.capability;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var media = new Dictionary<Guid, CapabilitySettlementSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var assetId = DbGuid.Parse(reader.GetString(0));
            if (!media.TryGetValue(assetId, out var snapshot))
            {
                snapshot = new CapabilitySettlementSnapshot(
                    DbEnum.ParseMediaType(reader.GetString(1)),
                    new Dictionary<MediaCapability, CapabilitySettlementState>());
                media.Add(assetId, snapshot);
            }

            if (!reader.IsDBNull(2) && !reader.IsDBNull(3))
            {
                var capability = DbEnum.ParseMediaCapability(reader.GetString(2));
                snapshot.States[capability] = new CapabilitySettlementState(
                    DbEnum.ParseMediaCapabilityState(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4));
            }
        }

        if (media.Count == 0)
        {
            return false;
        }

        foreach (var snapshot in media.Values)
        {
            foreach (var capability in CapabilityApplicability.GetRequiredForImportPhase(snapshot.MediaType, domainCommitted))
            {
                if (!snapshot.States.TryGetValue(capability, out var state))
                {
                    return false;
                }

                if (state.State is MediaCapabilityState.Ready or MediaCapabilityState.NotApplicable)
                {
                    continue;
                }

                if (state.State != MediaCapabilityState.Failed)
                {
                    return false;
                }

                if (state.JobState is not null
                    && state.JobState is not ("FAILED_TERMINAL" or "CANCELLED" or "SUCCEEDED"))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Returns media whose Required capability set contains a durable FAILED row. Optional
    /// profiling/search/similarity failure may degrade features but cannot fail the import.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetUnitFailedMediaIdsAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        var domainCommitted = await IsDomainCommittedAsync(unitId, cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH eligible_assets AS (
                SELECT DISTINCT CASE
                    WHEN ii.disposition = 'REUSED' AND ii.reused_media_id IS NOT NULL THEN ii.reused_media_id
                    ELSE ii.candidate_media_id
                END AS media_id
                FROM import_items ii
                WHERE ii.import_unit_id = $unitId
                  AND ii.disposition IN ('INCLUDED','REUSED')
                  AND ((ii.disposition='REUSED' AND ii.reused_media_id IS NOT NULL)
                    OR (ii.disposition='INCLUDED' AND ii.candidate_media_id IS NOT NULL))
            )
            SELECT DISTINCT cr.media_id, a.media_type, cr.capability
            FROM eligible_assets ea
            JOIN media a ON a.media_id = ea.media_id
            JOIN media_capability_readiness cr ON cr.media_id = ea.media_id
            WHERE cr.state = 'FAILED';
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var ids = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var assetId = DbGuid.Parse(reader.GetString(0));
            var mediaType = DbEnum.ParseMediaType(reader.GetString(1));
            var capability = DbEnum.ParseMediaCapability(reader.GetString(2));
            if (CapabilityApplicability.GetRequiredForImportPhase(mediaType, domainCommitted).Contains(capability))
            {
                ids.Add(assetId);
            }
        }

        return ids.Order().ToArray();
    }

    private async Task<bool> IsDomainCommittedAsync(Guid unitId, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT library_commit_state FROM import_units WHERE import_unit_id = $id;";
        command.Parameters.AddWithValue("$id", DbGuid.Format(unitId));
        var checkpoint = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return DbEnum.ParseImportCommitCheckpointOrDefault(checkpoint).HasReachedDomainCommit();
    }

    private sealed record CapabilityMediaSnapshot(
        Neuterradise.App.Media.MediaType MediaType,
        Dictionary<MediaCapability, MediaCapabilityState> States);

    private sealed record CapabilitySettlementSnapshot(
        Neuterradise.App.Media.MediaType MediaType,
        Dictionary<MediaCapability, CapabilitySettlementState> States);

    private sealed record CapabilitySettlementState(
        MediaCapabilityState State,
        string? JobState);

}
