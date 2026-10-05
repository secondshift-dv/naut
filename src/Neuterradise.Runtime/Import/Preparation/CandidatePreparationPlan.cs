using System.Security.Cryptography;
using System.Text;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs;
using static Neuterradise.App.SystemServices.Jobs.JobPriorityPolicy;

namespace Neuterradise.App.Import.Preparation;

/// <summary>
/// Pre-Stage-1 Media jobs. HashMedia investigates possible duplicates; TransferOriginal
/// establishes Vault staging bytes and content identity before materialization.
/// </summary>
public sealed record CandidatePreparationPlan(JobDefinition HashJob)
{
    public static CandidatePreparationPlan Create(
        Guid importItemId,
        Guid candidateMediaId,
        string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);

        var hashJob = new JobDefinition(
            DeriveHashJobId(candidateMediaId),
            Kind: "HashMedia",
            Lane: JobLane.Cpu,
            State: JobState.Pending,
            Priority: PriorityBackground,
            OwnerType: "Media",
            OwnerId: candidateMediaId,
            MaxAttempts: JobRetryPolicy.DefaultMaxAttempts,
            CheckpointJson: $"{{\"schemaVersion\":1,\"importItemId\":\"{importItemId:D}\",\"sourcePath\":\"{EscapeJson(sourcePath)}\"}}");

        return new CandidatePreparationPlan(hashJob);
    }

    public static JobDefinition CreateTransfer(Guid importItemId, Guid candidateMediaId)
    {
        return new JobDefinition(
            DeriveTransferJobId(candidateMediaId),
            Kind: "TransferOriginal",
            Lane: JobLane.Io,
            State: JobState.Pending,
            Priority: PriorityBackground,
            OwnerType: "Media",
            OwnerId: candidateMediaId,
            MaxAttempts: JobRetryPolicy.DefaultMaxAttempts,
            CheckpointJson: $"{{\"schemaVersion\":1,\"importItemId\":\"{importItemId:D}\",\"candidateMediaId\":\"{candidateMediaId:D}\"}}");
    }

    public static Guid DeriveHashJobId(Guid candidateMediaId) =>
        DerivePreStageJobId(candidateMediaId, "HashMedia");

    public static Guid DeriveTransferJobId(Guid candidateMediaId) =>
        DerivePreStageJobId(candidateMediaId, "TransferOriginal");

    private static Guid DerivePreStageJobId(Guid ownerId, string jobKind)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes($"{ownerId:D}:{jobKind}"));
        var guidBytes = new byte[16];
        Array.Copy(bytes, guidBytes, 16);
        return new Guid(guidBytes);
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
