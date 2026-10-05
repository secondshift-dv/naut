namespace Neuterradise.App.SystemServices.Operations;

/// <summary>
/// The canonical result of an application command or query.
/// <para>
/// The positional shape is deliberately flat so callers can refine a committed result with
/// <c>result with { UserMessage = ... }</c> without rebuilding an error object. <see cref="ErrorCode"/>
/// and <see cref="UserMessage"/> are the safe, user-facing pair; <see cref="SafeDetail"/> carries an
/// extra sanitized sentence when a surface can explain more. Raw SQLite text, absolute internal paths
/// and stack traces never travel in any of these fields — they belong in structured diagnostics.
/// </para>
/// </summary>
public sealed record OperationResult<T>(
    OperationStatus Status,
    T? Value,
    string? ErrorCode,
    string? UserMessage,
    Guid? OperationId = null,
    string? SafeDetail = null)
{
    /// <summary>The operation contract canonical outcome class of this result.</summary>
    public OperationOutcome Outcome => Status.ToOutcome();

    /// <summary>The stable canonical code (SUCCEEDED/CANCELLED/CONFLICT/...).</summary>
    public string CanonicalCode => Status.ToCanonicalCode();

    public bool IsSuccess => Status.IsSuccess();

    public bool IsCancelled => Status.IsCancelled();

    public bool IsRetryable => Status.IsRetryable();

    public bool IsTerminalFailure => Status.IsTerminalFailure();

    /// <summary>The failure carried by this result, or null when the intent succeeded.</summary>
    public OperationError? Error => ErrorCode is null
        ? null
        : new OperationError(ErrorCode, UserMessage ?? OperationError.GenericUserMessage, SafeDetail);

    public static OperationResult<T> Success(T value, Guid? operationId = null) =>
        new(OperationStatus.Succeeded, value, null, null, operationId);

    public static OperationResult<T> Accepted(T? value = default, Guid? operationId = null, string? userMessage = null) =>
        new(OperationStatus.AcceptedForProcessing, value, null, userMessage, operationId);

    /// <summary>Cooperative cancellation was observed; nothing partial was committed.</summary>
    public static OperationResult<T> Cancelled(string? userMessage = null, Guid? operationId = null) =>
        new(
            OperationStatus.Cancelled,
            default,
            OperationErrorCode.OperationCancelled,
            userMessage ?? OperationError.CancelledUserMessage,
            operationId);

    public static OperationResult<T> Validation(string errorCode, string userMessage, string? safeDetail = null) =>
        new(OperationStatus.ValidationFailed, default, errorCode, userMessage, null, safeDetail);

    public static OperationResult<T> Conflict(string errorCode, string userMessage, string? safeDetail = null) =>
        new(OperationStatus.Conflict, default, errorCode, userMessage, null, safeDetail);

    public static OperationResult<T> NotFound(string errorCode, string userMessage, string? safeDetail = null) =>
        new(OperationStatus.NotFound, default, errorCode, userMessage, null, safeDetail);

    public static OperationResult<T> NeedsAttention(
        string errorCode,
        string userMessage,
        Guid? operationId = null,
        string? safeDetail = null) =>
        new(OperationStatus.NeedsAttention, default, errorCode, userMessage, operationId, safeDetail);

    /// <summary>A transient condition; retrying the same logical operation is legitimate.</summary>
    public static OperationResult<T> Retryable(
        string errorCode,
        string userMessage,
        Guid? operationId = null,
        string? safeDetail = null) =>
        new(OperationStatus.RetryableFailure, default, errorCode, userMessage, operationId, safeDetail);

    /// <summary>The attempt failed in a way retrying unchanged cannot fix.</summary>
    public static OperationResult<T> Terminal(
        string errorCode,
        string userMessage,
        Guid? operationId = null,
        string? safeDetail = null) =>
        new(OperationStatus.TerminalFailure, default, errorCode, userMessage, operationId, safeDetail);

    /// <summary>Terminal failure spelled the way existing operation code names it.</summary>
    public static OperationResult<T> Failed(string errorCode, string userMessage, string? safeDetail = null) =>
        Terminal(errorCode, userMessage, null, safeDetail);

    public static OperationResult<T> FromError(
        OperationStatus status,
        OperationError error,
        Guid? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(status, default, error.ErrorCode, error.UserMessage, operationId, error.SafeDetail);
    }

    /// <summary>Drops the value and keeps the canonical outcome for void commands.</summary>
    public OperationResult WithoutValue() =>
        new(Status, ErrorCode, UserMessage, OperationId, SafeDetail);
}

/// <summary>
/// The canonical result of an application command that carries no value.
/// </summary>
public sealed record OperationResult(
    OperationStatus Status,
    string? ErrorCode,
    string? UserMessage,
    Guid? OperationId = null,
    string? SafeDetail = null)
{
    public OperationOutcome Outcome => Status.ToOutcome();

    public string CanonicalCode => Status.ToCanonicalCode();

    public bool IsSuccess => Status.IsSuccess();

    public bool IsCancelled => Status.IsCancelled();

    public bool IsRetryable => Status.IsRetryable();

    public bool IsTerminalFailure => Status.IsTerminalFailure();

    public OperationError? Error => ErrorCode is null
        ? null
        : new OperationError(ErrorCode, UserMessage ?? OperationError.GenericUserMessage, SafeDetail);

    public static OperationResult Success(Guid? operationId = null) =>
        new(OperationStatus.Succeeded, null, null, operationId);

    public static OperationResult Accepted(Guid? operationId = null, string? userMessage = null) =>
        new(OperationStatus.AcceptedForProcessing, null, userMessage, operationId);

    public static OperationResult Cancelled(string? userMessage = null, Guid? operationId = null) =>
        new(
            OperationStatus.Cancelled,
            OperationErrorCode.OperationCancelled,
            userMessage ?? OperationError.CancelledUserMessage,
            operationId);

    public static OperationResult Validation(string errorCode, string userMessage, string? safeDetail = null) =>
        new(OperationStatus.ValidationFailed, errorCode, userMessage, null, safeDetail);

    public static OperationResult Conflict(string errorCode, string userMessage, string? safeDetail = null) =>
        new(OperationStatus.Conflict, errorCode, userMessage, null, safeDetail);

    public static OperationResult NotFound(string errorCode, string userMessage, string? safeDetail = null) =>
        new(OperationStatus.NotFound, errorCode, userMessage, null, safeDetail);

    public static OperationResult NeedsAttention(
        string errorCode,
        string userMessage,
        Guid? operationId = null,
        string? safeDetail = null) =>
        new(OperationStatus.NeedsAttention, errorCode, userMessage, operationId, safeDetail);

    public static OperationResult Retryable(
        string errorCode,
        string userMessage,
        Guid? operationId = null,
        string? safeDetail = null) =>
        new(OperationStatus.RetryableFailure, errorCode, userMessage, operationId, safeDetail);

    public static OperationResult Terminal(
        string errorCode,
        string userMessage,
        Guid? operationId = null,
        string? safeDetail = null) =>
        new(OperationStatus.TerminalFailure, errorCode, userMessage, operationId, safeDetail);

    public static OperationResult Failed(string errorCode, string userMessage, string? safeDetail = null) =>
        Terminal(errorCode, userMessage, null, safeDetail);

    public static OperationResult FromError(
        OperationStatus status,
        OperationError error,
        Guid? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(status, error.ErrorCode, error.UserMessage, operationId, error.SafeDetail);
    }
}
