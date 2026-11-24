using System.Buffers;
using System.Diagnostics;
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
            var governor = ResourceGovernor.Shared;
            using (await governor.AcquireAsync(ResourceClass.DiskHeavy, cancellationToken).ConfigureAwait(false))
            using (var disk = await governor.AcquireImportDiskAsync(item.SourcePath, null, cancellationToken).ConfigureAwait(false))
            await using (var stream = new FileStream(
                item.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var identity = SourceIdentityHelper.CaptureIdentity(item.SourcePath);
                if (identity is null || !SourceIdentityHelper.VerifyIdentity(
                        identity, stream.SafeFileHandle, item.SourcePath))
                    throw new InvalidOperationException("Source identity could not be established for duplicate investigation.");
                var priority = disk.BulkPriority;
                if (priority != DiskIoPriority.Normal) WindowsDiskTelemetry.SetPriority(stream.SafeFileHandle, priority);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                const int readSize = 256 * 1024;
                var buffer = ArrayPool<byte>.Shared.Rent(readSize);
                try
                {
                    var budget = disk.BytesBetweenYields;
                    while (true)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var read = await stream.ReadAsync(buffer.AsMemory(0, readSize), cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        governor.ReportTransferIoLatency(Stopwatch.GetElapsedTime(started));
                        hash.AppendData(buffer, 0, read);
                        budget -= read;
                        if (budget <= 0)
                        {
                            await disk.YieldAsync(cancellationToken).ConfigureAwait(false);
                            var updatedPriority = disk.BulkPriority;
                            if (updatedPriority != priority)
                            {
                                WindowsDiskTelemetry.SetPriority(stream.SafeFileHandle, updatedPriority);
                                priority = updatedPriority;
                            }
                            budget = disk.BytesBetweenYields;
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                if (stream.Length != byteLength || !SourceIdentityHelper.VerifyIdentity(
                        identity, stream.SafeFileHandle, item.SourcePath))
                    throw new InvalidOperationException("Source changed during duplicate investigation.");
                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
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
            await using var mutation = await _catalog.ImportUnitMutations
                .EnterAsync(item.UnitId, cancellationToken).ConfigureAwait(false);
            await _jobWrites.TryCreateImportAdmissionJobAsync(item.UnitId,
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

    internal async Task ScheduleAutomaticTransferAsync(Guid unitId, Guid itemId, Guid candidateId, CancellationToken cancellationToken)
    {
        await _jobWrites.TryCreateImportAdmissionJobAsync(unitId,
            CandidatePreparationPlan.CreateTransfer(itemId, candidateId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> SaveModelPackageIdentityAsync(
        ImportItemSummary item, Guid candidateMediaId, string sha256, long byteLength,
        CancellationToken cancellationToken)
    {
        if (item.MediaType != MediaType.Model) return null;
        using var disk = await ResourceGovernor.Shared
            .AcquireImportDiskAsync(item.SourcePath, null, cancellationToken).ConfigureAwait(false);
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
        await ScheduleAdmissionHashJobsAsync(unitId, requireUnit: true, cancellationToken).ConfigureAwait(false);
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
    internal async Task PrepareIntakeUnitAsync(Guid unitId, CancellationToken cancellationToken)
    {
        using var admission = _catalog.MutationAdmission.Enter(nameof(PrepareIntakeUnitAsync));
        await ScheduleAdmissionHashJobsAsync(unitId, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> ScheduleAdmissionHashJobsAsync(Guid unitId, CancellationToken cancellationToken = default) =>
        ScheduleAdmissionHashJobsAsync(unitId, requireUnit: false, cancellationToken);

    private async Task<int> ScheduleAdmissionHashJobsAsync(
        Guid unitId,
        bool requireUnit,
        CancellationToken cancellationToken)
    {
        var unit = await _reads.GetUnitSummaryAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null && requireUnit)
            throw new InvalidOperationException($"ImportUnit {unitId:D} was not found.");
        if (!CanScheduleAdmission(unit)) return 0;

        if (unit!.State == ImportUnitState.Intake)
        {
            try
            {
                await _importWrites.UpdateUnitStateAsync(unitId, ImportUnitState.Preparing,
                    expectedState: ImportUnitState.Intake, expectedRowVersion: null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CatalogConcurrencyConflictException)
            {
                var current = await _reads.GetUnitSummaryAsync(unitId, cancellationToken).ConfigureAwait(false);
                if (!CanScheduleAdmission(current)) return 0;
                if (current!.State == ImportUnitState.Intake) throw;
            }
        }

        var items = await ReadAdmissionCandidatesAsync(unitId, cancellationToken).ConfigureAwait(false);
        var repeatedLengths = items.Where(item => item.SourceByteLength.HasValue)
            .GroupBy(item => item.SourceByteLength!.Value).Where(group => group.Skip(1).Any())
            .Select(group => group.Key).ToHashSet();
        var existingTransfers = (await _schedulerReads.GetJobsForImportUnitMediasAsync(
            unitId, cancellationToken).ConfigureAwait(false))
            .Where(job => job.Kind == "TransferOriginal")
            .Select(job => job.OwnerId).ToHashSet();
        foreach (var item in items)
        {
            await using var mutation = await _catalog.ImportUnitMutations.EnterAsync(unitId, cancellationToken).ConfigureAwait(false);
            if (existingTransfers.Contains(item.CandidateMediaId)) continue;
            var source = new FileInfo(item.SourcePath);
            JobDefinition? definition = null;
            if (!source.Exists)
            {
                definition = CandidatePreparationPlan.Create(item.ImportItemId, item.CandidateMediaId, item.SourcePath).HashJob;
            }
            else
            {
                var needsHash = item.Sha256 is null && (repeatedLengths.Contains(source.Length)
                    || (source.Length == item.SourceByteLength
                        ? item.HasActiveWithLength
                        : await _duplicateDetector.HasActiveWithLengthAsync(
                            source.Length, item.CandidateMediaId, cancellationToken).ConfigureAwait(false)));
                if (needsHash)
                {
                    definition = CandidatePreparationPlan.Create(item.ImportItemId, item.CandidateMediaId, item.SourcePath).HashJob;
                }
                else if (item.Sha256 is null || !(await _duplicateDetector.DetectDuplicateAsync(
                             item.Sha256, item.ByteLength ?? -1, item.CandidateMediaId,
                             item.BundleSha256, cancellationToken).ConfigureAwait(false)).IsLibraryReuseEligible)
                {
                    definition = CandidatePreparationPlan.CreateTransfer(item.ImportItemId, item.CandidateMediaId);
                }
            }

            if (definition is not null && !await _jobWrites.TryCreateImportAdmissionJobAsync(
                    unitId, definition, cancellationToken).ConfigureAwait(false))
                break;
        }

        return items.Count;
    }

    private static bool CanScheduleAdmission(ImportUnitSummary? unit) =>
        unit is not null && unit.State is not (ImportUnitState.Cancelled or ImportUnitState.FailedTerminal)
        && !unit.State.IsUnitCommitted();

    private async Task<IReadOnlyList<AdmissionCandidate>> ReadAdmissionCandidatesAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        var items = new List<AdmissionCandidate>();
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.import_item_id, i.candidate_media_id, i.source_path, i.source_byte_length,
                   a.sha256, a.byte_length, a.bundle_sha256,
                   (
                       EXISTS (SELECT 1 FROM media active WHERE active.state = 'ACTIVE'
                           AND active.byte_length = i.source_byte_length AND active.media_id <> a.media_id)
                       OR EXISTS (
                           SELECT 1 FROM import_units peer JOIN import_items peer_item ON peer_item.import_unit_id = peer.import_unit_id
                           WHERE peer.import_session_id = own_unit.import_session_id
                             AND peer.import_unit_id <> own_unit.import_unit_id
                             AND peer.destination_kind = 'EXISTING_NORMAL' AND own_unit.destination_kind = 'EXISTING_NORMAL'
                             AND peer.destination_profile_id = own_unit.destination_profile_id
                             AND json_extract(peer.verification_draft_json, '$.publishWhenReady') = 1
                             AND json_extract(own_unit.verification_draft_json, '$.publishWhenReady') = 1
                             AND peer_item.source_byte_length = i.source_byte_length
                             AND peer_item.disposition = 'INCLUDED'
                       )
                   )
            FROM import_items i
            JOIN import_units own_unit ON own_unit.import_unit_id = i.import_unit_id
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
                reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetBoolean(7)));
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
        string SourcePath,
        long? SourceByteLength,
        string? Sha256,
        long? ByteLength,
        string? BundleSha256,
        bool HasActiveWithLength);

    private sealed record AdmissionCounts(int TotalCount, int PreparedCount);
}
