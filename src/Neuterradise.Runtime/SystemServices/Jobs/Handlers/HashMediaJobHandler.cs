using System.IO;
using System.Text.Json;
using Neuterradise.App.Import.Preparation;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Jobs.Handlers;

public interface IHashMediaJobOperation : IAuthorizedJobOperation;

public sealed class HashMediaJobHandler : AuthorizedJobHandler
{
    public HashMediaJobHandler(IHashMediaJobOperation operation)
        : base("HashMedia", [JobLane.Cpu], "Media", operation, runOffCallingThread: true)
    {
    }
}

public sealed class CandidateHashJobOperation : IHashMediaJobOperation
{
    private const int _checkpointSchemaVersion = 1;
    private readonly ImportPreparationCoordinator _coordinator;

    public CandidateHashJobOperation(ImportPreparationCoordinator coordinator) =>
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public async Task<JobExecutionResult> ExecuteAsync(
        JobExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!TryReadCheckpoint(context.CheckpointJson, out var importItemId, out var alreadyCompleted, out var existingSha, out var existingLength))
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "HASH_CHECKPOINT_INVALID",
                "The versioned HashMedia checkpoint does not identify an ImportItem.");
        }

        try
        {
            var authorizedCandidateId = await _coordinator.ReadCandidateMediaIdAsync(
                    importItemId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (authorizedCandidateId != context.OwnerId)
            {
                return JobExecutionResult.Failed(
                    JobFailureClassification.AmbiguousPhysicalState,
                    "HASH_OWNER_MISMATCH",
                    "The ImportItem Candidate does not match the persisted job owner.");
            }

            if (alreadyCompleted && !string.IsNullOrWhiteSpace(existingSha) && existingLength >= 0)
            {
                await context.Progress.ReportProgressAsync(1, 1, "Hashed", cancellationToken)
                    .ConfigureAwait(false);
                return JobExecutionResult.Succeeded;
            }

            await context.Progress.ReportProgressAsync(0, 1, "Hashing", cancellationToken)
                .ConfigureAwait(false);
            var result = await _coordinator.PrepareCandidateAsync(importItemId, cancellationToken)
                .ConfigureAwait(false);
            if (result.CandidateMediaId != context.OwnerId)
            {
                return JobExecutionResult.Failed(
                    JobFailureClassification.AmbiguousPhysicalState,
                    "HASH_OWNER_MISMATCH",
                    "The ImportItem Candidate does not match the persisted job owner.");
            }

            var checkpoint = JsonSerializer.Serialize(new
            {
                schemaVersion = _checkpointSchemaVersion,
                importItemId = result.ImportItemId,
                candidateMediaId = result.CandidateMediaId,
                sha256 = result.Sha256,
                byteLength = result.ByteLength,
                completed = true,
            });
            await context.Checkpoints.UpdateCheckpointAsync(checkpoint, cancellationToken)
                .ConfigureAwait(false);
            await context.Progress.ReportProgressAsync(1, 1, "Hashed", cancellationToken)
                .ConfigureAwait(false);
            return JobExecutionResult.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return JobExecutionResult.Cancelled("Candidate hashing reached a safe cancellation boundary.");
        }
        catch (FileNotFoundException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.ContentMismatch,
                "HASH_SOURCE_MISSING",
                "The authorized Candidate source no longer exists.");
        }
        catch (UnauthorizedAccessException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.AccessTemporarilyDenied,
                "HASH_SOURCE_ACCESS_DENIED",
                "The authorized Candidate source cannot currently be read.");
        }
        catch (System.Security.SecurityException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.AccessTemporarilyDenied,
                "HASH_SOURCE_SECURITY_DENIED",
                "The authorized Candidate source cannot currently be read due to security permissions.");
        }
        catch (IOException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.TransientIo,
                "HASH_SOURCE_IO_FAILED",
                "The authorized Candidate source could not be read.");
        }
        catch (InvalidOperationException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "HASH_AUTHORITY_INVALID",
                "The persisted ImportItem/Candidate authority is incomplete.");
        }
    }

    private static bool TryReadCheckpoint(
        string? checkpointJson,
        out Guid importItemId,
        out bool completed,
        out string? sha256,
        out long byteLength)
    {
        importItemId = Guid.Empty;
        completed = false;
        sha256 = null;
        byteLength = -1;

        if (string.IsNullOrWhiteSpace(checkpointJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(checkpointJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("schemaVersion", out var schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.Number
                || !schemaVersion.TryGetInt32(out var version)
                || version != _checkpointSchemaVersion)
            {
                return false;
            }

            if (!root.TryGetProperty("importItemId", out var itemId)
                || itemId.ValueKind != JsonValueKind.String
                || !DomainId.TryParse(itemId.GetString(), out importItemId)
                || importItemId == Guid.Empty)
            {
                return false;
            }

            if (root.TryGetProperty("completed", out var completedProp) && completedProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                completed = completedProp.GetBoolean();
            }

            if (root.TryGetProperty("sha256", out var shaProp) && shaProp.ValueKind == JsonValueKind.String)
            {
                sha256 = shaProp.GetString();
            }

            if (root.TryGetProperty("byteLength", out var lenProp) && lenProp.TryGetInt64(out var len))
            {
                byteLength = len;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
