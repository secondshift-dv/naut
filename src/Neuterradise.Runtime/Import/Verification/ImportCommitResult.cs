namespace Neuterradise.App.Import.Verification;

public enum ImportCommitOutcome
{

    Committed,

    CommittedWithCleanupAttention,

    Blocked,

    Conflict,

    Cancelled,
}

public sealed record ImportCommitResult(
    Guid ImportUnitId,
    Guid? CommitOperationId,
    ImportCommitOutcome Outcome,
    ImportCommitCheckpoint Checkpoint,
    Guid? DestinationProfileId,
    bool DestinationProfileCreated,
    IReadOnlyList<Guid> ActivatedMediaIds,
    IReadOnlyList<Guid> ReusedMediaIds,
    IReadOnlyList<Guid> RetiredCandidateIds,
    IReadOnlyList<Guid> SourceCleanupAttentionItemIds,
    IReadOnlyList<VerificationBlocker> Blockers)
{
    public bool LibraryCommitted =>
        Outcome is ImportCommitOutcome.Committed or ImportCommitOutcome.CommittedWithCleanupAttention;

    public static ImportCommitResult BlockedBy(
        Guid unitId,
        Guid? operationId,
        IReadOnlyList<VerificationBlocker> blockers) =>
        new(unitId, operationId, ImportCommitOutcome.Blocked, ImportCommitCheckpoint.DecisionValidated,
            null, false, [], [], [], [], blockers);

    public static ImportCommitResult ConflictOn(Guid unitId) =>
        new(unitId, null, ImportCommitOutcome.Conflict, ImportCommitCheckpoint.DecisionValidated,
            null, false, [], [], [], [], []);

    public static ImportCommitResult CancelledAt(
        Guid unitId,
        Guid? operationId,
        ImportCommitCheckpoint checkpoint) =>
        new(unitId, operationId, ImportCommitOutcome.Cancelled, checkpoint,
            null, false, [], [], [], [], []);
}
