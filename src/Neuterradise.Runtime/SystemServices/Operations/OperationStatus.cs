namespace Neuterradise.App.SystemServices.Operations;

/// <summary>
/// The canonical outcome vocabulary of the internal application operation protocol.
/// Every command result maps onto exactly one of these classes, so presentation code never has to
/// interpret an exception to decide what happened.
/// </summary>
public enum OperationOutcome
{
    Succeeded,
    Cancelled,
    Conflict,
    ValidationFailed,
    RetryableFailure,
    TerminalFailure,
}

/// <summary>
/// The status a command reports. The members are the canonical outcomes plus the refinements the
/// product actually needs to drive different surfaces (a durable job accepted for background
/// processing, a missing target, and work parked for user attention). Every member classifies onto
/// exactly one <see cref="OperationOutcome"/> through <see cref="OperationStatusExtensions.ToOutcome"/>,
/// so the canonical contract stays single-valued.
/// </summary>
public enum OperationStatus
{
    /// <summary>The intent completed and its durable state is committed.</summary>
    Succeeded,

    /// <summary>
    /// The intent was accepted and durably recorded; bounded background work continues.
    /// This is a success class: the caller may show progress, never an error.
    /// </summary>
    AcceptedForProcessing,

    /// <summary>Cooperative cancellation was observed. Nothing partial was committed.</summary>
    Cancelled,

    /// <summary>The request was not valid for the current state and must be corrected.</summary>
    ValidationFailed,

    /// <summary>Concurrent state changed; the caller keeps its draft and decides how to proceed.</summary>
    Conflict,

    /// <summary>The named target does not exist. A terminal failure for this intent.</summary>
    NotFound,

    /// <summary>
    /// Durable work stopped in a state that a person must resolve. Nothing was guessed or overwritten.
    /// A terminal failure for this attempt; the parked state stays actionable.
    /// </summary>
    NeedsAttention,

    /// <summary>A transient condition; the same logical operation may be retried.</summary>
    RetryableFailure,

    /// <summary>The operation failed in a way that retrying cannot fix.</summary>
    TerminalFailure,
}

public static class OperationStatusExtensions
{
    /// <summary>
    /// Projects a status onto the canonical the operation contract outcome.
    /// </summary>
    public static OperationOutcome ToOutcome(this OperationStatus status) => status switch
    {
        OperationStatus.Succeeded => OperationOutcome.Succeeded,
        OperationStatus.AcceptedForProcessing => OperationOutcome.Succeeded,
        OperationStatus.Cancelled => OperationOutcome.Cancelled,
        OperationStatus.ValidationFailed => OperationOutcome.ValidationFailed,
        OperationStatus.Conflict => OperationOutcome.Conflict,
        OperationStatus.NotFound => OperationOutcome.TerminalFailure,
        OperationStatus.NeedsAttention => OperationOutcome.TerminalFailure,
        OperationStatus.RetryableFailure => OperationOutcome.RetryableFailure,
        OperationStatus.TerminalFailure => OperationOutcome.TerminalFailure,
        _ => OperationOutcome.TerminalFailure,
    };

    /// <summary>The stable machine-readable name of the canonical outcome.</summary>
    public static string ToCanonicalCode(this OperationStatus status) => status.ToOutcome() switch
    {
        OperationOutcome.Succeeded => "SUCCEEDED",
        OperationOutcome.Cancelled => "CANCELLED",
        OperationOutcome.Conflict => "CONFLICT",
        OperationOutcome.ValidationFailed => "VALIDATION_FAILED",
        OperationOutcome.RetryableFailure => "RETRYABLE_FAILURE",
        _ => "TERMINAL_FAILURE",
    };

    public static bool IsSuccess(this OperationStatus status) =>
        status.ToOutcome() == OperationOutcome.Succeeded;

    public static bool IsCancelled(this OperationStatus status) =>
        status.ToOutcome() == OperationOutcome.Cancelled;

    /// <summary>
    /// True when retrying the same logical operation is a legitimate offer to the user.
    /// </summary>
    public static bool IsRetryable(this OperationStatus status) =>
        status.ToOutcome() == OperationOutcome.RetryableFailure;

    /// <summary>
    /// True when the attempt failed and retrying it unchanged cannot help.
    /// </summary>
    public static bool IsTerminalFailure(this OperationStatus status) =>
        status.ToOutcome() == OperationOutcome.TerminalFailure;
}
