namespace Neuterradise.App.SystemServices.Recovery;

public sealed record RecoveryFinding(
    Guid CorrelationId,
    string EntityType,
    Guid EntityId,
    RecoveryOutcome Outcome,
    string Code,
    string SafeDetail);
