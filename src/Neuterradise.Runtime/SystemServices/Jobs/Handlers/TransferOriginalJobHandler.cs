using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Neuterradise.App.Import.Preparation;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Jobs.Handlers;

public sealed class TransferOriginalJobHandler : AuthorizedJobHandler
{
    internal static Guid ProcessNonce { get; } = Guid.NewGuid();

    public TransferOriginalJobHandler(CatalogDb catalog, ImportPreparationCoordinator preparation)
        : base("TransferOriginal", [JobLane.Io], "Media",
            new TransferOriginalOperation(catalog, preparation), runOffCallingThread: false)
    {
    }
}

internal sealed class TransferOriginalOperation : IAuthorizedJobOperation
{
    private const int BufferSize = 1024 * 1024;
    private const long CheckpointInterval = 16L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly CatalogDb _catalog;
    private readonly ImportPreparationCoordinator _preparation;

    public TransferOriginalOperation(CatalogDb catalog, ImportPreparationCoordinator preparation)
    {
        _catalog = catalog;
        _preparation = preparation;
    }

    public async Task<JobExecutionResult> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        TransferCheckpoint? checkpoint;
        try { checkpoint = JsonSerializer.Deserialize<TransferCheckpoint>(context.CheckpointJson!, JsonOptions); }
        catch (JsonException) { checkpoint = null; }
        if (checkpoint is null || checkpoint.SchemaVersion != 1 || checkpoint.ImportItemId == Guid.Empty
            || checkpoint.CandidateMediaId != context.OwnerId)
            return Failed("TRANSFER_CHECKPOINT_INVALID", "Transfer job identity is invalid.");

        var item = await _catalog.ImportReads.GetItemSummaryAsync(checkpoint.ImportItemId, cancellationToken)
            .ConfigureAwait(false);
        if (item?.CandidateMediaId != context.OwnerId)
            return Failed("TRANSFER_OWNER_MISMATCH", "Transfer owner no longer matches the Import item.");

        var token = await _catalog.MediaWrites.GetStorageTokenAsync(context.OwnerId, cancellationToken)
            .ConfigureAwait(false);
        var partialPath = _catalog.Paths.ResolveStagedOriginalPartialPath(
            item.UnitId, new MediaStorageToken(token));
        var relativePath = Path.GetRelativePath(_catalog.Paths.Root, partialPath).Replace('\\', '/');
        if (checkpoint.PartialRelativePath is not null
            && !string.Equals(checkpoint.PartialRelativePath, relativePath, StringComparison.Ordinal))
            return Failed("TRANSFER_PATH_MISMATCH", "Transfer checkpoint identifies a different partial path.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
            RootPathRules.RejectExistingReparsePoints(_catalog.Paths.Root, partialPath);
            await using var source = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var identity = SourceIdentityHelper.CaptureIdentity(item.SourcePath);
            if (identity is null || !SourceIdentityHelper.VerifyIdentity(identity, source.SafeFileHandle, item.SourcePath))
                return Failed("SOURCE_IDENTITY_UNAVAILABLE", "The source file object could not be identified.");
            var length = source.Length;
            if (checkpoint.SourceIdentity is not null
                && (!string.Equals(checkpoint.SourceIdentity, identity, StringComparison.Ordinal)
                    || checkpoint.ExpectedLength != length))
                return Failed("SOURCE_CHANGED", "The source file object changed since the safe checkpoint.");

            await using var partial = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var safeOffset = checkpoint.SourceIdentity is null ? 0 : checkpoint.SafeOffset;
            if (safeOffset < 0 || safeOffset > length || partial.Length < safeOffset)
                return Failed("TRANSFER_PARTIAL_SHORT", "The partial file is shorter than its safe checkpoint.");
            // Bytes after the durable checkpoint may have reached disk before a crash.
            if (partial.Length > safeOffset) partial.SetLength(safeOffset);

            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                if (safeOffset > 0)
                {
                    partial.Position = 0;
                    var remaining = safeOffset;
                    while (remaining > 0)
                    {
                        var read = await partial.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferSize, remaining)),
                            cancellationToken).ConfigureAwait(false);
                        if (read == 0) return Failed("TRANSFER_PARTIAL_SHORT", "The safe partial prefix could not be read.");
                        hash.AppendData(buffer, 0, read);
                        remaining -= read;
                    }
                    if (!string.Equals(Convert.ToHexStringLower(hash.GetCurrentHash()), checkpoint.PrefixSha256,
                            StringComparison.Ordinal))
                        return Failed("TRANSFER_PREFIX_MISMATCH", "The safe partial prefix does not match its checkpoint.");
                }

                source.Position = safeOffset;
                partial.Position = safeOffset;
                var written = safeOffset;
                var lastCheckpoint = safeOffset;
                while (written < length && !cancellationToken.IsCancellationRequested)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferSize, length - written)),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0) return Failed("SOURCE_CHANGED", "The source ended before its expected length.");
                    await partial.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    written += read;
                    await context.Progress.ReportProgressAsync(written, length, "Transferring", cancellationToken)
                        .ConfigureAwait(false);
                    if (written - lastCheckpoint >= CheckpointInterval)
                    {
                        await SaveCheckpointAsync(context, partial, item.UnitId, identity, length,
                            written, relativePath, hash).ConfigureAwait(false);
                        lastCheckpoint = written;
                    }
                }

                await SaveCheckpointAsync(context, partial, item.UnitId, identity, length,
                    written, relativePath, hash).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    return JobExecutionResult.Cancelled("Transfer stopped after a durable safe checkpoint.");
                if (!SourceIdentityHelper.VerifyIdentity(identity, source.SafeFileHandle, item.SourcePath)
                    || source.Length != length)
                    return Failed("SOURCE_CHANGED", "The source file object changed during transfer.");

                var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
                var existing = await ReadExpectedFingerprintAsync(context.OwnerId, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null && (existing.Value.Length != length
                    || !string.Equals(existing.Value.Sha, sha, StringComparison.Ordinal)))
                    return Failed("SOURCE_CHANGED", "The transferred source differs from its duplicate investigation.");

                await _preparation.RecordTransferredCandidateAsync(checkpoint.ImportItemId, context.OwnerId,
                    sha, length, new DateTimeOffset(File.GetLastWriteTimeUtc(item.SourcePath)).ToUnixTimeMilliseconds(),
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    await new ImportMediaPreparationCoordinator(_catalog, _catalog.Paths)
                        .ScheduleForCurrentPhaseAsync(item.UnitId,
                            cancellationToken: cancellationToken, onlyMediaId: context.OwnerId)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return JobExecutionResult.Cancelled(
                        "Transfer completed durably; downstream scheduling stopped because cancellation was requested.");
                }
                catch (Exception ex)
                {
                    // Transfer is already durable. The finalizer retries idempotent preparation
                    // scheduling; a scheduling fault must not falsify completed transfer bytes.
                    System.Diagnostics.Trace.TraceWarning(
                        "Candidate preparation scheduling deferred for {0:D}: {1}",
                        context.OwnerId, ex.GetType().Name);
                }
                return JobExecutionResult.Succeeded;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return JobExecutionResult.Cancelled(
                "Transfer stopped at the last durable checkpoint after cancellation was requested.");
        }
        catch (FileNotFoundException) { return Failed("TRANSFER_SOURCE_MISSING", "The source file is missing."); }
        catch (UnauthorizedAccessException) { return Failed("TRANSFER_ACCESS_DENIED", "Transfer file access was denied."); }
        catch (IOException) { return JobExecutionResult.Failed(JobFailureClassification.TransientIo,
            "TRANSFER_IO_FAILED", "Transfer I/O could not complete."); }
    }

    private async Task<(string Sha, long Length)?> ReadExpectedFingerprintAsync(Guid assetId, CancellationToken token)
    {
        await using var connection = await _catalog.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256, byte_length FROM media WHERE media_id = $id";
        command.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) && !reader.IsDBNull(0)
            ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    private static async Task SaveCheckpointAsync(JobExecutionContext context, FileStream partial,
        Guid unitId, string identity, long length, long offset, string relativePath, IncrementalHash hash)
    {
        // The catalog can name only bytes already flushed to the Vault volume.
        await partial.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        partial.Flush(flushToDisk: true);
        var json = JsonSerializer.Serialize(new TransferCheckpoint
        {
            SchemaVersion = 1, ImportItemId = ReadItemId(context.CheckpointJson!),
            CandidateMediaId = context.OwnerId, ImportUnitId = unitId,
            SourceIdentity = identity, ExpectedLength = length, SafeOffset = offset,
            PrefixSha256 = Convert.ToHexStringLower(hash.GetCurrentHash()),
            PartialRelativePath = relativePath, ProcessNonce = TransferOriginalJobHandler.ProcessNonce,
        });
        await context.Checkpoints.UpdateCheckpointAsync(json, CancellationToken.None).ConfigureAwait(false);
    }

    private static Guid ReadItemId(string json) =>
        JsonSerializer.Deserialize<TransferCheckpoint>(json, JsonOptions)!.ImportItemId;

    private static JobExecutionResult Failed(string code, string message) =>
        JobExecutionResult.Failed(JobFailureClassification.AmbiguousPhysicalState, code, message);

    private sealed record TransferCheckpoint
    {
        public int SchemaVersion { get; init; }
        public Guid ImportItemId { get; init; }
        public Guid CandidateMediaId { get; init; }
        public Guid ImportUnitId { get; init; }
        public string? SourceIdentity { get; init; }
        public long ExpectedLength { get; init; }
        public long SafeOffset { get; init; }
        public string? PrefixSha256 { get; init; }
        public string? PartialRelativePath { get; init; }
        public Guid ProcessNonce { get; init; }
    }
}
