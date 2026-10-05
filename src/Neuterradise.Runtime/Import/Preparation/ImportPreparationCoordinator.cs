using System.IO;
using System.Security.Cryptography;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Resources;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Import.Preparation;

public sealed record CandidatePreparationResult(
    Guid ImportItemId,
    Guid CandidateMediaId,
    string Sha256,
    long ByteLength,
    bool IsExactDuplicate,
    ExactDuplicateMatch? DuplicateMatch);

public sealed record UnitPreparationResult(
    Guid UnitId,
    int TotalItemCount,
    int PreparedCount,
    int ExactDuplicateCount,
    PreparationReadiness Readiness);

/// <summary>
/// Canonical pre-Stage-1 admission authority for duplicate investigation and original transfer.
/// Metadata, derivatives, and face analysis belong to media preparation after materialization.
/// </summary>
public sealed class ImportPreparationCoordinator
{
    private readonly CatalogDb _catalog;
    private readonly ImportWrites _importWrites;
    private readonly ImportReads _reads;
    private readonly ExactDuplicateDetector _duplicateDetector;
    private readonly PreparationReadinessEvaluator _readinessEvaluator;
    private readonly JobWrites _jobWrites;
    private readonly SchedulerReads _schedulerReads;

    public ImportPreparationCoordinator(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _importWrites = new ImportWrites(catalog, timeProvider);
        _reads = catalog.ImportReads;
        _duplicateDetector = new ExactDuplicateDetector(catalog);
        _readinessEvaluator = new PreparationReadinessEvaluator(catalog);
        _jobWrites = new JobWrites(catalog, timeProvider);
        _schedulerReads = new SchedulerReads(catalog);
    }

    private async Task<(string? Sha256, long? ByteLength)> ReadStoredFingerprintAsync(
        Guid candidateMediaId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256, byte_length FROM media WHERE media_id = $id;";
        command.Parameters.AddWithValue("$id", DbGuid.Format(candidateMediaId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (null, null);
        }

        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    public async Task<Guid?> ReadCandidateMediaIdAsync(
        Guid importItemId,
        CancellationToken cancellationToken = default)
    {
        var item = await _reads.GetItemSummaryAsync(importItemId, cancellationToken)
            .ConfigureAwait(false);
        return item?.CandidateMediaId;
    }

    /// <summary>
    /// Investigates a same-length duplicate candidate before any Vault write. Model package
    /// identity remains part of exact duplicate and materialization semantics.
    /// </summary>
    public async Task<CandidatePreparationResult> PrepareCandidateAsync(
        Guid importItemId,
        CancellationToken cancellationToken = default)
    {
        var item = await _reads.GetItemSummaryAsync(importItemId, cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
        {
            throw new InvalidOperationException($"ImportItem {importItemId:D} was not found.");
        }

        if (!item.CandidateMediaId.HasValue)
        {
            throw new InvalidOperationException($"ImportItem {importItemId:D} has no allocated CandidateMediaId.");
        }

        var fileInfo = new FileInfo(item.SourcePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException($"Source file not found at '{item.SourcePath}'.", item.SourcePath);
        }

        var byteLength = fileInfo.Length;
        var lastWriteMs = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        string sha256;
        {
            using (await ResourceGovernor.Shared.AcquireAsync(ResourceClass.DiskHeavy, cancellationToken).ConfigureAwait(false))
            await using (var stream = new FileStream(
                item.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                useAsync: true))
            {
                var identity = SourceIdentityHelper.CaptureIdentity(item.SourcePath);
                if (identity is null || !SourceIdentityHelper.VerifyIdentity(
                        identity, stream.SafeFileHandle, item.SourcePath))
                    throw new InvalidOperationException("Source identity could not be established for duplicate investigation.");
                var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                if (stream.Length != byteLength || !SourceIdentityHelper.VerifyIdentity(
                        identity, stream.SafeFileHandle, item.SourcePath))
                    throw new InvalidOperationException("Source changed during duplicate investigation.");
                sha256 = Convert.ToHexStringLower(hashBytes);
            }

            await _importWrites.UpdateCandidateFingerprintAsync(
                    item.CandidateMediaId.Value,
                    sha256,
                    byteLength,
                    cancellationToken)
                .ConfigureAwait(false);
            await _importWrites.UpdateItemSourceDetailsAsync(
                    importItemId,
                    byteLength,
                    lastWriteMs,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var bundleSha256 = await SaveModelPackageIdentityAsync(
            item, item.CandidateMediaId.Value, sha256, byteLength, cancellationToken).ConfigureAwait(false);

        var dupResult = await _duplicateDetector.DetectDuplicateAsync(
                sha256,
                byteLength,
                item.CandidateMediaId.Value,
                bundleSha256,
                cancellationToken)
            .ConfigureAwait(false);

        if (!dupResult.IsLibraryReuseEligible)
        {
            await _jobWrites.CreateJobAsync(
                CandidatePreparationPlan.CreateTransfer(importItemId, item.CandidateMediaId.Value),
                cancellationToken).ConfigureAwait(false);
        }

        return new CandidatePreparationResult(
            importItemId,
            item.CandidateMediaId.Value,
            sha256,
            byteLength,
            dupResult.IsExactDuplicate,
            dupResult.Match);
    }

    public async Task RecordTransferredCandidateAsync(
        Guid importItemId, Guid candidateMediaId, string sha256, long byteLength,
        long sourceLastWriteMs, CancellationToken cancellationToken)
    {
        var item = await _reads.GetItemSummaryAsync(importItemId, cancellationToken).ConfigureAwait(false);
        if (item?.CandidateMediaId != candidateMediaId)
            throw new InvalidOperationException("Transfer owner no longer matches the Import item.");

        await _importWrites.UpdateCandidateFingerprintAsync(candidateMediaId, sha256, byteLength, cancellationToken)
            .ConfigureAwait(false);
        await _importWrites.UpdateItemSourceDetailsAsync(importItemId, byteLength, sourceLastWriteMs, cancellationToken)
            .ConfigureAwait(false);
        await SaveModelPackageIdentityAsync(item, candidateMediaId, sha256, byteLength, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ScheduleIncludedTransferAsync(Guid importItemId, CancellationToken cancellationToken)
    {
        var item = await _reads.GetItemSummaryAsync(importItemId, cancellationToken).ConfigureAwait(false);
        if (item?.CandidateMediaId is not { } candidateId || item.Disposition != ItemDisposition.Included)
            throw new InvalidOperationException("Included transfer requires an admitted Candidate item.");
        await _jobWrites.CreateJobAsync(CandidatePreparationPlan.CreateTransfer(
            importItemId, candidateId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> SaveModelPackageIdentityAsync(
        ImportItemSummary item, Guid candidateMediaId, string sha256, long byteLength,
        CancellationToken cancellationToken)
    {
        if (item.MediaType != MediaType.Model) return null;
        var discovery = await Task.Run(
            () => ModelPackageDiscovery.Discover(item.SourcePath, sha256, byteLength), cancellationToken)
            .ConfigureAwait(false);
        await _catalog.MediaWrites.UpdateMediaPackageIdentityAsync(candidateMediaId,
            discovery.DependencyStatus, discovery.DiscoveryState, discovery.BundleSha256, cancellationToken)
            .ConfigureAwait(false);
        if (discovery.Components.Count > 0)
        {
            var records = discovery.Components.Select(c => new MediaComponentRecord(
                candidateMediaId, c.RelativePath, c.NormalizedRelativePath, c.Role, c.Sha256,
                c.ByteLength, c.OriginalSourcePath,
                SourceIdentityJson: SourceIdentityHelper.CaptureIdentity(
                    c.OriginalSourcePath ?? Path.Combine(
                        Path.GetDirectoryName(item.SourcePath) ?? string.Empty,
                        c.RelativePath.Replace("/", "\\"))),
                SourceCleanupState: SourceCleanupState.SourcePresent)).ToList();
            await _catalog.MediaWrites.SaveMediaComponentsAsync(candidateMediaId, records, cancellationToken)
                .ConfigureAwait(false);
        }
        return discovery.BundleSha256;
    }

    /// <summary>
    /// Normal intake and Retry enter the same duplicate gate and transfer admission path.
    /// </summary>
    public async Task<UnitPreparationResult> PrepareUnitAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        var unit = await _reads.GetUnitSummaryAsync(unitId, cancellationToken)
            .ConfigureAwait(false);
        if (unit is null)
        {
            throw new InvalidOperationException($"ImportUnit {unitId:D} was not found.");
        }

        await ScheduleAdmissionHashJobsAsync(unitId, cancellationToken).ConfigureAwait(false);
        var readiness = await ResolveUnitStateAsync(unitId, cancellationToken).ConfigureAwait(false);
        var counts = await ReadAdmissionCountsAsync(unitId, cancellationToken).ConfigureAwait(false);

        return new UnitPreparationResult(
            unitId,
            counts.TotalCount,
            counts.PreparedCount,
            ExactDuplicateCount: 0,
            Readiness: readiness);
    }

    /// <summary>
    /// Owns the durable INTAKE to PREPARING handoff and schedules duplicate investigation or
    /// TransferOriginal. The item read is unbounded so retry and recovery see the complete unit.
    /// </summary>
    public async Task<int> ScheduleAdmissionHashJobsAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        var unit = await _reads.GetUnitSummaryAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null
            || unit.State is ImportUnitState.Cancelled or ImportUnitState.FailedTerminal
            || unit.State.IsUnitCommitted())
        {
            return 0;
        }

        if (unit.State == ImportUnitState.Intake)
        {
            await _importWrites
                .UpdateUnitStateAsync(unitId, ImportUnitState.Preparing, cancellationToken)
                .ConfigureAwait(false);
        }

        var items = await ReadAdmissionCandidatesAsync(unitId, cancellationToken).ConfigureAwait(false);
        var existingTransfers = (await _schedulerReads.GetJobsForImportUnitMediasAsync(
            unitId, cancellationToken).ConfigureAwait(false))
            .Where(job => job.Kind == "TransferOriginal")
            .Select(job => job.OwnerId).ToHashSet();
        foreach (var item in items)
        {
            if (existingTransfers.Contains(item.CandidateMediaId)) continue;
            var source = new FileInfo(item.SourcePath);
            if (!source.Exists)
            {
                await _jobWrites.CreateJobAsync(CandidatePreparationPlan.Create(
                    item.ImportItemId, item.CandidateMediaId, item.SourcePath).HashJob,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            var sameSizeActive = await _duplicateDetector.HasActiveWithLengthAsync(
                source.Length, item.CandidateMediaId, cancellationToken).ConfigureAwait(false);
            var stored = await ReadStoredFingerprintAsync(item.CandidateMediaId, cancellationToken)
                .ConfigureAwait(false);
            if (sameSizeActive && stored.Sha256 is null)
            {
                await _jobWrites.CreateJobAsync(CandidatePreparationPlan.Create(
                    item.ImportItemId, item.CandidateMediaId, item.SourcePath).HashJob,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (stored.Sha256 is null || !(await _duplicateDetector.DetectDuplicateAsync(
                         stored.Sha256, stored.ByteLength ?? -1, item.CandidateMediaId,
                         cancellationToken: cancellationToken).ConfigureAwait(false)).IsLibraryReuseEligible)
            {
                await _jobWrites.CreateJobAsync(CandidatePreparationPlan.CreateTransfer(
                    item.ImportItemId, item.CandidateMediaId), cancellationToken).ConfigureAwait(false);
            }
        }

        return items.Count;
    }

    private async Task<IReadOnlyList<AdmissionCandidate>> ReadAdmissionCandidatesAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        var items = new List<AdmissionCandidate>();
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.import_item_id, i.candidate_media_id, i.source_path
            FROM import_items i
            JOIN media a ON a.media_id = i.candidate_media_id
            WHERE i.import_unit_id = $unitId
              AND i.candidate_media_id IS NOT NULL
              AND i.disposition NOT IN ('SKIPPED','INVALID','REUSED')
              AND a.state <> 'RETIRED'
            ORDER BY i.created_at_ms, i.import_item_id;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new AdmissionCandidate(
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1)),
                reader.GetString(2)));
        }

        return items;
    }

    /// <summary>
    /// Read-only, complete-dataset admission probe; it never mutates ImportUnit lifecycle state.
    /// </summary>
    public Task<PreparationReadiness> ResolveUnitStateAsync(
        Guid unitId,
        CancellationToken cancellationToken = default) =>
        _readinessEvaluator.EvaluateUnitReadinessAsync(
            unitId,
            verifySourceFilesOnDisk: false,
            cancellationToken);

    /// <summary>
    /// Compatibility duplicate-decision entry. Persisting the decision is allowed; deciding
    /// ReadyForVerification is not. Finalizer/MediaPreparation remain the lifecycle authorities.
    /// </summary>
    public async Task<PreparationReadiness> ApplyDuplicateDecisionAsync(
        Guid importItemId,
        DuplicateDecision decision,
        Guid? reusedMediaId = null,
        CancellationToken cancellationToken = default)
    {
        var item = await _reads.GetItemSummaryAsync(importItemId, cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
        {
            throw new InvalidOperationException($"ImportItem {importItemId:D} was not found.");
        }

        await _importWrites.ApplyDuplicateDecisionAsync(
                importItemId,
                DbEnum.DispositionFor(decision),
                decision,
                reusedMediaId,
                cancellationToken)
            .ConfigureAwait(false);

        if (decision == DuplicateDecision.Include)
        {
            await ScheduleIncludedTransferAsync(importItemId, cancellationToken).ConfigureAwait(false);
        }

        return await _readinessEvaluator
            .EvaluateUnitReadinessAsync(item.UnitId, verifySourceFilesOnDisk: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AdmissionCounts> ReadAdmissionCountsAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE
                       WHEN i.disposition = 'REUSED' THEN 1
                       WHEN i.disposition = 'INCLUDED' AND a.sha256 IS NOT NULL THEN 1
                       ELSE 0
                   END), 0)
            FROM import_items i
            LEFT JOIN media a ON a.media_id = i.candidate_media_id
            WHERE i.import_unit_id = $unitId;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new AdmissionCounts(0, 0);
        }

        return new AdmissionCounts(reader.GetInt32(0), reader.GetInt32(1));
    }

    private sealed record AdmissionCandidate(
        Guid ImportItemId,
        Guid CandidateMediaId,
        string SourcePath);

    private sealed record AdmissionCounts(int TotalCount, int PreparedCount);
}
