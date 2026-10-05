namespace Neuterradise.App.SystemServices.Storage;

public enum VaultTransitionKind
{
    Rename,
    Move,
    Change,
}

public enum VaultMoveMode
{
    SameVolume,
    CrossVolume,
}

public enum VaultChangeTargetKind
{
    ExistingVault,
    EmptyFolder,
}

public enum VaultTransitionPhase
{
    Prepared,
    RuntimeRetired,
    Copying,
    DestinationVerified,
    FilesystemApplied,
    ConfigurationCommitted,
    TargetBootstrapSucceeded,
}

public sealed record VaultTransitionRecord(
    Guid OperationId,
    VaultTransitionKind Kind,
    VaultTransitionPhase Phase,
    string SourceRoot,
    string TargetRoot,
    long StartedAtUnixMs,
    long UpdatedAtUnixMs,
    string? LastError = null,
    VaultMoveMode? MoveMode = null,
    string? StagingRoot = null,
    long? TotalBytes = null,
    int? TotalFiles = null,
    long? CompletedBytes = null,
    int? CompletedFiles = null,
    VaultChangeTargetKind? ChangeTargetKind = null,
    Guid? VaultId = null);

public sealed record VaultRenamePlan(
    Guid OperationId,
    string SourceRoot,
    string TargetRoot,
    string TargetName);

public sealed record VaultMovePlan(
    Guid OperationId,
    string SourceRoot,
    string TargetRoot,
    string TargetParent,
    string VaultName,
    Guid VaultId,
    VaultMoveMode Mode,
    string? StagingRoot);

public sealed record VaultChangePlan(
    Guid OperationId,
    string SourceRoot,
    string TargetRoot,
    VaultChangeTargetKind TargetKind);

public sealed record VaultMoveProgress(
    string Stage,
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    string? CurrentRelativePath = null);

public sealed class VaultSuccessfulBootstrapProof
{
    internal VaultSuccessfulBootstrapProof(string vaultRoot, Guid vaultId, Guid sessionId)
    {
        VaultRoot = RootPathRules.NormalizeRoot(vaultRoot, nameof(vaultRoot));
        VaultId = vaultId != Guid.Empty
            ? vaultId
            : throw new ArgumentException("Vault id cannot be empty.", nameof(vaultId));
        SessionId = sessionId != Guid.Empty
            ? sessionId
            : throw new ArgumentException("Session id cannot be empty.", nameof(sessionId));
    }

    public string VaultRoot { get; }

    public Guid VaultId { get; }

    public Guid SessionId { get; }
}

public sealed class VaultRuntimeRetirementProof
{
    internal VaultRuntimeRetirementProof(string vaultRoot, Guid vaultId, Guid sessionId)
    {
        VaultRoot = RootPathRules.NormalizeRoot(vaultRoot, nameof(vaultRoot));
        VaultId = vaultId != Guid.Empty
            ? vaultId
            : throw new ArgumentException("Vault id cannot be empty.", nameof(vaultId));
        SessionId = sessionId != Guid.Empty
            ? sessionId
            : throw new ArgumentException("Session id cannot be empty.", nameof(sessionId));
    }

    public string VaultRoot { get; }

    public Guid VaultId { get; }

    public Guid SessionId { get; }
}

public sealed record VaultTransitionCommandResult(
    bool Succeeded,
    string? ErrorCode,
    string? SafeErrorDetail,
    VaultRenamePlan? RenamePlan = null,
    VaultMovePlan? MovePlan = null,
    VaultChangePlan? ChangePlan = null)
{
    public static VaultTransitionCommandResult Success() =>
        new(true, null, null);

    public static VaultTransitionCommandResult Success(VaultRenamePlan plan) =>
        new(true, null, null, RenamePlan: plan);

    public static VaultTransitionCommandResult Success(VaultMovePlan plan) =>
        new(true, null, null, MovePlan: plan);

    public static VaultTransitionCommandResult Success(VaultChangePlan plan) =>
        new(true, null, null, ChangePlan: plan);

    public static VaultTransitionCommandResult Failed(string code, string detail) =>
        new(false, code, detail);
}

public sealed record VaultTransitionRecoveryResult(
    bool Succeeded,
    bool Recovered,
    string? EffectiveRoot,
    string? ErrorCode,
    string? SafeErrorDetail)
{
    public static VaultTransitionRecoveryResult NoTransition() =>
        new(true, false, null, null, null);

    public static VaultTransitionRecoveryResult Success(string root) =>
        new(true, true, root, null, null);

    public static VaultTransitionRecoveryResult Failed(string code, string detail) =>
        new(false, false, null, code, detail);
}

public enum VaultNameViolation
{
    None,
    EmptyOrTooLong,
    SurroundingWhitespace,
    DirectorySeparator,
    InvalidCharacter,
    TrailingPeriod,
    ReservedDeviceName,
}

public static class VaultNameRules
{
    public const int MaxLength = 120;
    public const string ForbiddenCharactersDisplay = @"\ / < > : "" | ? *";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static VaultNameViolation Validate(string? requestedName, out string normalized)
    {
        normalized = requestedName?.Trim() ?? string.Empty;

        if (normalized.Length is < 1 or > MaxLength)
        {
            return VaultNameViolation.EmptyOrTooLong;
        }

        if (!string.Equals(requestedName, normalized, StringComparison.Ordinal))
        {
            return VaultNameViolation.SurroundingWhitespace;
        }

        if (normalized.Contains(Path.DirectorySeparatorChar)
            || normalized.Contains(Path.AltDirectorySeparatorChar))
        {
            return VaultNameViolation.DirectorySeparator;
        }

        if (normalized.EndsWith(".", StringComparison.Ordinal))
        {
            return VaultNameViolation.TrailingPeriod;
        }

        if (normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || normalized.Any(char.IsControl))
        {
            return VaultNameViolation.InvalidCharacter;
        }

        var deviceStem = normalized.Split('.', 2)[0];
        if (ReservedNames.Contains(deviceStem))
        {
            return VaultNameViolation.ReservedDeviceName;
        }

        return VaultNameViolation.None;
    }

    public static bool TryNormalize(string? requestedName, out string normalized, out string? error)
    {
        var violation = Validate(requestedName, out normalized);
        error = violation switch
        {
            VaultNameViolation.None => null,
            VaultNameViolation.EmptyOrTooLong => $"Vault name must contain 1 to {MaxLength} characters.",
            VaultNameViolation.SurroundingWhitespace => "Vault name cannot begin or end with spaces.",
            VaultNameViolation.DirectorySeparator => @"Vault name cannot contain \ or / because they are directory separators.",
            VaultNameViolation.InvalidCharacter => $@"Vault name cannot contain Windows-reserved filename characters ({ForbiddenCharactersDisplay}) or control characters.",
            VaultNameViolation.TrailingPeriod => "Vault name cannot end with a period.",
            VaultNameViolation.ReservedDeviceName => "That Vault name is reserved by Windows.",
            _ => "That Vault name cannot be used.",
        };
        return violation == VaultNameViolation.None;
    }
}
