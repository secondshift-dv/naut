namespace Neuterradise.App.SystemServices.Jobs;

public enum JobFailureKind
{
    Retryable,
    Terminal,
}

public sealed record JobExecutionResult
{
    private JobExecutionResult(
        bool succeeded,
        JobFailureKind? failureKind,
        JobFailureClassification? classification,
        string? errorCode,
        string? errorDetailSafe)
    {
        IsSucceeded = succeeded;
        FailureKind = failureKind;
        Classification = classification;
        ErrorCode = errorCode;
        ErrorDetailSafe = errorDetailSafe;
    }

    public static JobExecutionResult Succeeded { get; } = new(true, null, null, null, null);

    public bool IsSucceeded { get; }

    public JobFailureKind? FailureKind { get; }

    public JobFailureClassification? Classification { get; }

    public string? ErrorCode { get; }

    public string? ErrorDetailSafe { get; }

    public static JobExecutionResult Failed(JobFailureKind kind, string errorCode, string? errorDetailSafe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return new JobExecutionResult(false, kind, null, errorCode, errorDetailSafe);
    }

    public static JobExecutionResult Failed(
        JobFailureClassification classification,
        string errorCode,
        string? errorDetailSafe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        var kind = JobRetryPolicy.IsRetryable(classification)
            ? JobFailureKind.Retryable
            : JobFailureKind.Terminal;
        return new JobExecutionResult(false, kind, classification, errorCode, errorDetailSafe);
    }

    public static JobExecutionResult Cancelled(string? errorDetailSafe = null) =>
        new(false,
            JobFailureKind.Terminal,
            JobFailureClassification.Cancelled,
            CancellationErrorCode,
            errorDetailSafe);

    /// <summary>
    /// The handler stopped at a safe boundary because the owning import was paused.
    /// Not a failure — checkpoint and attempt count are preserved.
    /// </summary>
    public static JobExecutionResult Paused(string? errorDetailSafe = null) =>
        new(false,
            JobFailureKind.Terminal,
            JobFailureClassification.Paused,
            PauseErrorCode,
            errorDetailSafe);

    public static JobExecutionResult Yielded() =>
        new(false, JobFailureKind.Terminal, JobFailureClassification.Yielded,
            YieldErrorCode, null);

    public const string CancellationErrorCode = "CANCELLED";

    public const string PauseErrorCode = "IMPORT_PAUSED";
    public const string YieldErrorCode = "PRIORITY_YIELD";
}

public interface IJobHandler
{
    Task<JobExecutionResult> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken);
}

public interface IAuthorizedJobOperation
{
    Task<JobExecutionResult> ExecuteAsync(
        JobExecutionContext context,
        CancellationToken cancellationToken);
}

public abstract class AuthorizedJobHandler : IJobHandler
{
    private readonly string _kind;
    private readonly HashSet<JobLane> _lanes;
    private readonly string _ownerType;
    private readonly IAuthorizedJobOperation _operation;
    private readonly bool _runOffCallingThread;

    protected AuthorizedJobHandler(
        string kind,
        IEnumerable<JobLane> lanes,
        string ownerType,
        IAuthorizedJobOperation operation,
        bool runOffCallingThread)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(lanes);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerType);
        ArgumentNullException.ThrowIfNull(operation);

        _kind = kind;
        _lanes = new HashSet<JobLane>(lanes);
        if (_lanes.Count == 0)
        {
            throw new ArgumentException("At least one execution lane is required.", nameof(lanes));
        }

        _ownerType = ownerType;
        _operation = operation;
        _runOffCallingThread = runOffCallingThread;
    }

    public async Task<JobExecutionResult> ExecuteAsync(
        JobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(context.Kind, _kind, StringComparison.Ordinal))
        {
            return Invalid("JOB_KIND_MISMATCH", "The persisted job kind does not match this handler.");
        }

        if (!_lanes.Contains(context.Lane))
        {
            return Invalid("JOB_LANE_MISMATCH", "The persisted job lane is not authorized for this handler.");
        }

        if (!string.Equals(context.OwnerType, _ownerType, StringComparison.Ordinal)
            || context.OwnerId == Guid.Empty)
        {
            return Invalid("JOB_OWNER_MISMATCH", "The persisted stable owner is not authorized for this handler.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_runOffCallingThread)
        {
            return await Task.Run(
                    () => _operation.ExecuteAsync(context, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await _operation.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static JobExecutionResult Invalid(string code, string detail) =>
        JobExecutionResult.Failed(
            JobFailureClassification.DeterministicInvalidInput,
            code,
            detail);
}

public sealed class JobHandlerRegistry
{
    private readonly Dictionary<string, IJobHandler> _handlers = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public JobHandlerRegistry Register(string kind, IJobHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            var trimmed = kind.Trim();
            if (_handlers.ContainsKey(trimmed))
            {
                throw new InvalidOperationException($"A job handler for '{trimmed}' is already registered.");
            }

            _handlers.Add(trimmed, handler);
        }

        return this;
    }

    public IJobHandler Get(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        lock (_gate)
        {
            var trimmed = kind.Trim();
            if (!_handlers.TryGetValue(trimmed, out var handler))
            {
                throw new InvalidOperationException($"No job handler is registered for '{trimmed}'.");
            }

            return handler;
        }
    }

    public const string UnknownJobKindErrorCode = "UNKNOWN_JOB_KIND";

    public bool TryGet(string? kind, out IJobHandler handler)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            handler = null!;
            return false;
        }

        lock (_gate)
        {
            return _handlers.TryGetValue(kind.Trim(), out handler!);
        }
    }

    public IReadOnlyCollection<string> RegisteredKinds
    {
        get
        {
            lock (_gate)
            {
                return _handlers.Keys.ToArray();
            }
        }
    }
}
