using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Operations;

/// <summary>
/// The immutable context every state-changing command receives.
/// <para>
/// One logical mutation keeps one <see cref="OperationId"/> across all of its internal steps and
/// across restarts, so durable work can ask whether that operation already committed instead of
/// creating a second logical operation. <see cref="StartedAtUtc"/> always comes from the canonical
/// clock: no command may stamp durable state from local time.
/// </para>
/// </summary>
public sealed record OperationContext
{
    private OperationContext(
        string kind,
        Guid operationId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        Kind = kind;
        OperationId = operationId;
        StartedAtUtc = startedAtUtc;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Stable canonical name of the intent, for example <c>PROFILE_SAVE_DRAFT</c>. Uppercase
    /// snake case; used as the receipt <c>operation_kind</c> and as the diagnostics code.
    /// </summary>
    public string Kind { get; }

    /// <summary>Unique per logical mutation; stable across retries of the same intent.</summary>
    public Guid OperationId { get; }

    /// <summary>Start instant from the canonical clock, always UTC.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Start instant as the persisted UTC epoch millisecond value.</summary>
    public long StartedAtUnixMilliseconds => DomainTime.ToUnixMilliseconds(StartedAtUtc);

    /// <summary>Cooperative cancellation only. Never used to abandon a durable commit midway.</summary>
    public CancellationToken CancellationToken { get; init; }

    public Guid? ProfileId { get; init; }

    public Guid? MediaId { get; init; }

    public Guid? IdentityId { get; init; }

    public Guid? FaceId { get; init; }

    public Guid? ImportSessionId { get; init; }

    public Guid? ImportUnitId { get; init; }

    public Guid? ImportItemId { get; init; }

    public Guid? JobId { get; init; }

    public Guid? TrashEntryId { get; init; }

    public bool IsCancellationRequested => CancellationToken.IsCancellationRequested;

    /// <summary>
    /// Starts a new logical operation. The kind is required so receipts and diagnostics can name
    /// the intent; the clock is required so no command reads an ambient wall clock.
    /// </summary>
    public static OperationContext Start(
        string kind,
        IClock clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new OperationContext(
            NormalizeKind(kind),
            DomainId.New(),
            clock.UtcNow.ToUniversalTime(),
            cancellationToken);
    }

    /// <inheritdoc cref="Start(string, IClock, CancellationToken)"/>
    public static OperationContext Start(
        string kind,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return Start(kind, timeProvider.AsClock(), cancellationToken);
    }

    /// <summary>
    /// Rebuilds the context of an operation that was already started and persisted, so a resumed or
    /// retried attempt keeps the original identity instead of fabricating a second operation.
    /// </summary>
    public static OperationContext Resume(
        string kind,
        Guid operationId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default) =>
        new(
            NormalizeKind(kind),
            DomainId.Require(operationId, DomainIdKind.Operation, nameof(operationId)),
            startedAtUtc.ToUniversalTime(),
            cancellationToken);

    /// <inheritdoc cref="Resume(string, Guid, DateTimeOffset, CancellationToken)"/>
    public static OperationContext Resume(
        string kind,
        Guid operationId,
        long startedAtUnixMilliseconds,
        CancellationToken cancellationToken = default)
    {
        if (!DomainTime.IsRepresentable(startedAtUnixMilliseconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(startedAtUnixMilliseconds),
                "The persisted operation start time is outside the representable range.");
        }

        return Resume(
            kind,
            operationId,
            DomainTime.FromUnixMilliseconds(startedAtUnixMilliseconds),
            cancellationToken);
    }

    /// <summary>
    /// Derives a context for an internal step of the same logical mutation. The OperationId,
    /// start instant and owner IDs are preserved; only the step name changes, so diagnostics can
    /// name the step without splitting the operation identity.
    /// </summary>
    public OperationContext ForStep(string stepKind) => WithKind(NormalizeKind(stepKind));

    /// <summary>Binds a narrower cancellation token to the same logical operation.</summary>
    public OperationContext WithCancellation(CancellationToken cancellationToken) =>
        this with { CancellationToken = cancellationToken };

    /// <summary>Canonical cancelled result for this operation.</summary>
    public OperationResult Cancelled(string? userMessage = null) =>
        OperationResult.Cancelled(userMessage, OperationId);

    /// <summary>Canonical cancelled result for this operation.</summary>
    public OperationResult<T> Cancelled<T>(string? userMessage = null) =>
        OperationResult<T>.Cancelled(userMessage, OperationId);

    /// <summary>
    /// Returns the canonical cancelled result when cancellation was already requested, so a command
    /// can stop before it starts durable work instead of throwing into the UI layer. Returns null
    /// when the operation may proceed; it never fabricates a success.
    /// </summary>
    public OperationResult? CancellationResultOrNull() =>
        IsCancellationRequested ? Cancelled() : null;

    /// <inheritdoc cref="CancellationResultOrNull()"/>
    public OperationResult<T>? CancellationResultOrNull<T>() =>
        IsCancellationRequested ? Cancelled<T>() : null;

    private OperationContext WithKind(string kind) =>
        new(kind, OperationId, StartedAtUtc, CancellationToken)
        {
            ProfileId = ProfileId,
            MediaId = MediaId,
            IdentityId = IdentityId,
            FaceId = FaceId,
            ImportSessionId = ImportSessionId,
            ImportUnitId = ImportUnitId,
            ImportItemId = ImportItemId,
            JobId = JobId,
            TrashEntryId = TrashEntryId,
        };

    private static string NormalizeKind(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return kind.Trim().ToUpperInvariant();
    }
}
