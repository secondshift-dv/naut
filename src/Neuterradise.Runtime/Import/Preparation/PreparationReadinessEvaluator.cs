using System.IO;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.Import.Preparation;

public sealed record PreparationReadiness(
    bool IsReady,
    IReadOnlyList<VerificationBlocker> BlockingItems,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> PendingOptionalWork);

/// <summary>
/// Read-only pre-Stage-1 admission probe. It scans the complete Import Unit and requires
/// completed transfer for included new Medias. media preparation capability readiness has its own authority.
/// </summary>
public sealed class PreparationReadinessEvaluator
{
    private readonly CatalogConnectionFactory _connectionFactory;

    public PreparationReadinessEvaluator(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
    }

    public PreparationReadinessEvaluator(CatalogConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<PreparationReadiness> EvaluateUnitReadinessAsync(
        Guid unitId,
        bool verifySourceFilesOnDisk = false,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return MissingUnit(unitId);
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var unitCommand = connection.CreateCommand())
        {
            unitCommand.CommandText = "SELECT 1 FROM import_units WHERE import_unit_id = $unitId;";
            unitCommand.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            if (await unitCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                return MissingUnit(unitId);
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.import_item_id,
                   i.source_file_name,
                   i.source_path,
                   i.source_byte_length,
                   i.disposition,
                   i.candidate_media_id,
                   i.source_cleanup_error,
                   a.sha256,
                   tj.state
            FROM import_items i
            LEFT JOIN media a ON a.media_id = i.candidate_media_id
            LEFT JOIN jobs tj ON tj.owner_type = 'Media' AND tj.owner_id = i.candidate_media_id
                             AND tj.kind = 'TransferOriginal'
            WHERE i.import_unit_id = $unitId
            ORDER BY i.created_at_ms, i.import_item_id;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var blockers = new List<VerificationBlocker>();
        var eligibleCount = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var itemId = DbGuid.Parse(reader.GetString(0));
            var fileName = reader.GetString(1);
            var sourcePath = reader.GetString(2);
            var byteLength = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
            var disposition = DbEnum.ParseItemDisposition(reader.GetString(4));
            var candidateMediaId = reader.IsDBNull(5) ? (Guid?)null : DbGuid.Parse(reader.GetString(5));
            var cleanupError = reader.IsDBNull(6) ? null : reader.GetString(6);
            var sha256 = reader.IsDBNull(7) ? null : reader.GetString(7);
            var transferState = reader.IsDBNull(8) ? null : reader.GetString(8);

            if (disposition == ItemDisposition.Skipped)
            {
                continue;
            }

            if (disposition == ItemDisposition.Invalid)
            {
                blockers.Add(new VerificationBlocker(
                    itemId,
                    "ITEM_INVALID",
                    $"Item '{fileName}' cannot be admitted: {cleanupError ?? "integrity or format issue"}."));
                continue;
            }

            if (disposition == ItemDisposition.Reused)
            {
                eligibleCount++;
                continue;
            }

            if (disposition != ItemDisposition.Included)
            {
                continue;
            }

            eligibleCount++;

            if (candidateMediaId is null || byteLength is null or < 0 || string.IsNullOrWhiteSpace(sha256))
            {
                blockers.Add(new VerificationBlocker(
                    itemId,
                    "HASH_INCOMPLETE",
                    $"Hash admission is incomplete for '{fileName}'."));
                continue;
            }

            if (transferState != "SUCCEEDED")
            {
                blockers.Add(new VerificationBlocker(itemId, "TRANSFER_INCOMPLETE",
                    $"Original transfer is incomplete for '{fileName}'."));
                continue;
            }

            if (verifySourceFilesOnDisk && !File.Exists(sourcePath))
            {
                blockers.Add(new VerificationBlocker(
                    itemId,
                    "SOURCE_MISSING",
                    $"Source file not found at '{sourcePath}'."));
            }
        }

        if (eligibleCount == 0 && blockers.Count == 0)
        {
            blockers.Add(new VerificationBlocker(
                null,
                "NO_INCLUDED_ITEMS",
                "ImportUnit contains no included media items or reuse decisions."));
        }

        return new PreparationReadiness(
            IsReady: blockers.Count == 0,
            BlockingItems: blockers,
            Warnings: [],
            PendingOptionalWork: []);
    }

    /// <summary>
    /// Pure compatibility evaluation for callers that already have item/fingerprint snapshots.
    /// Job kinds, duplicate UI decisions, previews, metadata and face analysis are deliberately not
    /// readiness authorities here. They are ignored rather than reviving the retired preparation graph.
    /// </summary>
    public PreparationReadiness Evaluate(
        ImportUnitSummary unit,
        IReadOnlyList<ImportItemSummary> items,
        IReadOnlyDictionary<Guid, string?>? candidateFingerprints = null,
        IReadOnlyList<JobRecord>? jobs = null,
        bool verifySourceFilesOnDisk = false,
        IReadOnlyList<ImportDuplicateMatch>? duplicateMatches = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(items);
        _ = jobs;
        _ = duplicateMatches;

        var blockers = new List<VerificationBlocker>();
        var eligibleCount = 0;

        foreach (var item in items)
        {
            if (item.Disposition == ItemDisposition.Skipped)
            {
                continue;
            }

            if (item.Disposition == ItemDisposition.Invalid)
            {
                blockers.Add(new VerificationBlocker(
                    item.ItemId,
                    "ITEM_INVALID",
                    $"Item '{item.SourceFileName}' cannot be admitted: {item.SourceCleanupError ?? "integrity or format issue"}."));
                continue;
            }

            if (item.Disposition == ItemDisposition.Reused)
            {
                eligibleCount++;
                continue;
            }

            if (item.Disposition != ItemDisposition.Included)
            {
                continue;
            }

            eligibleCount++;

            var fingerprintReady = item.CandidateMediaId is { } candidateId
                && item.SourceByteLength is >= 0
                && (candidateFingerprints is null
                    || candidateFingerprints.TryGetValue(candidateId, out var sha256)
                       && !string.IsNullOrWhiteSpace(sha256));

            if (!fingerprintReady)
            {
                blockers.Add(new VerificationBlocker(
                    item.ItemId,
                    "HASH_INCOMPLETE",
                    $"Hash admission is incomplete for '{item.SourceFileName}'."));
                continue;
            }

            if (verifySourceFilesOnDisk
                && !string.IsNullOrWhiteSpace(item.SourcePath)
                && !File.Exists(item.SourcePath))
            {
                blockers.Add(new VerificationBlocker(
                    item.ItemId,
                    "SOURCE_MISSING",
                    $"Source file not found at '{item.SourcePath}'."));
            }
        }

        if (eligibleCount == 0 && blockers.Count == 0)
        {
            blockers.Add(new VerificationBlocker(
                null,
                "NO_INCLUDED_ITEMS",
                "ImportUnit contains no included media items or reuse decisions."));
        }

        return new PreparationReadiness(
            IsReady: blockers.Count == 0,
            BlockingItems: blockers,
            Warnings: [],
            PendingOptionalWork: []);
    }

    private static PreparationReadiness MissingUnit(Guid unitId) =>
        new(
            IsReady: false,
            BlockingItems:
            [
                new VerificationBlocker(
                    null,
                    "UNIT_NOT_FOUND",
                    $"ImportUnit {unitId:D} was not found.")
            ],
            Warnings: [],
            PendingOptionalWork: []);
}
