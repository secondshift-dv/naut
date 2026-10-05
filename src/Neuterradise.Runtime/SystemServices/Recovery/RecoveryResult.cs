namespace Neuterradise.App.SystemServices.Recovery;

public enum RecoveryOutcome
{
    NoAction,
    Completed,
    Resumed,
    Requeued,
    NeedsAttention,
    Fatal,
}

public enum RecoverySafetyClass
{
    Advisory = 0,
    RetryableMaintenance = 1,
    BlocksAffectedCapability = 2,
    BlocksWritableStartup = 3,
    Fatal = 4,
}

public static class RecoverySafetyPolicy
{
    public static RecoverySafetyClass Classify(RecoveryFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        if (finding.Outcome == RecoveryOutcome.Fatal)
        {
            return RecoverySafetyClass.Fatal;
        }

        if (finding.Outcome != RecoveryOutcome.NeedsAttention)
        {
            return finding.Outcome == RecoveryOutcome.NoAction
                ? RecoverySafetyClass.Advisory
                : RecoverySafetyClass.RetryableMaintenance;
        }

        var isManagedPathAuthority =
            (string.Equals(finding.EntityType, "Profile", StringComparison.Ordinal)
             && finding.Code.StartsWith("PROFILE_RENAME_", StringComparison.Ordinal))
            || (string.Equals(finding.EntityType, "Media", StringComparison.Ordinal)
                && finding.Code.StartsWith("OWNER_RELOCATION_", StringComparison.Ordinal));

        if (isManagedPathAuthority
            && (finding.Code.EndsWith("_AMBIGUOUS", StringComparison.Ordinal)
                || finding.Code.EndsWith("_INCOMPLETE", StringComparison.Ordinal)
                || finding.Code.EndsWith("_NEEDS_ATTENTION", StringComparison.Ordinal)))
        {
            return RecoverySafetyClass.BlocksWritableStartup;
        }

        if (isManagedPathAuthority)
        {
            return RecoverySafetyClass.BlocksAffectedCapability;
        }

        return RecoverySafetyClass.BlocksAffectedCapability;
    }
}

public sealed record RecoveryResult(IReadOnlyList<RecoveryFinding> Findings)
{
    public static RecoveryResult NoAction { get; } = new(Array.Empty<RecoveryFinding>());

    public bool HasFatal => Findings.Any(finding => RecoverySafetyPolicy.Classify(finding) == RecoverySafetyClass.Fatal);

    public bool BlocksWritableStartup => Findings.Any(
        finding => RecoverySafetyPolicy.Classify(finding) >= RecoverySafetyClass.BlocksWritableStartup);
}

public sealed class RecoveryFailedException : Exception
{
    public RecoveryFailedException(RecoveryResult result)
        : base("Startup recovery encountered persisted authority that does not permit writable startup.")
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public RecoveryResult Result { get; }
}
