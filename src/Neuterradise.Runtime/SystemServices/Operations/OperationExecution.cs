using System.ComponentModel;
using System.IO;
using Microsoft.Data.Sqlite;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Operations;

/// <summary>
/// The single boundary that keeps expected failures from escaping into the UI layer as arbitrary
/// exceptions. Application command implementations wrap their body here; the guard
/// classifies cancellation and known infrastructure faults into canonical results, sends the raw
/// technical detail to structured diagnostics, and returns only safe user-facing text.
/// <para>
/// Programming faults (<see cref="ArgumentException"/>, <see cref="NullReferenceException"/> and
/// friends) are deliberately not swallowed: they are defects, not recoverable user outcomes.
/// </para>
/// </summary>
public sealed class OperationExecution
{
    private readonly StructuredDiagnostics? _diagnostics;
    private readonly IClock _clock;

    public OperationExecution(StructuredDiagnostics? diagnostics = null, IClock? clock = null)
    {
        _diagnostics = diagnostics;
        _clock = clock ?? new SystemClock();
    }

    /// <summary>Guard without a diagnostics sink; failures are still classified safely.</summary>
    public static OperationExecution WithoutDiagnostics { get; } = new();

    public async Task<OperationResult> RunAsync(
        OperationContext context,
        Func<OperationContext, Task<OperationResult>> body)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);

        if (context.CancellationResultOrNull() is { } preCancelled)
        {
            return preCancelled;
        }

        try
        {
            return await body(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return context.Cancelled();
        }
        catch (Exception exception) when (IsExpectedInfrastructureFailure(exception))
        {
            var classified = Classify(exception);
            Report(context, exception, classified);
            return OperationResult.FromError(classified.Status, classified.Error, context.OperationId);
        }
    }

    public async Task<OperationResult<T>> RunAsync<T>(
        OperationContext context,
        Func<OperationContext, Task<OperationResult<T>>> body)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);

        if (context.CancellationResultOrNull<T>() is { } preCancelled)
        {
            return preCancelled;
        }

        try
        {
            return await body(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return context.Cancelled<T>();
        }
        catch (Exception exception) when (IsExpectedInfrastructureFailure(exception))
        {
            var classified = Classify(exception);
            Report(context, exception, classified);
            return OperationResult<T>.FromError(classified.Status, classified.Error, context.OperationId);
        }
    }

    /// <summary>
    /// True for faults that represent the environment failing (catalog busy, file locked, Explorer
    /// refused to start), not a defect in the calling code.
    /// </summary>
    public static bool IsExpectedInfrastructureFailure(Exception exception) =>
        exception is SqliteException
            or IOException
            or UnauthorizedAccessException
            or Win32Exception
            or TimeoutException
            or InvalidOperationException;

    /// <summary>
    /// Maps one infrastructure fault onto a canonical status plus a safe, user-facing error.
    /// The exception message itself is never used as the user message.
    /// </summary>
    public static (OperationStatus Status, OperationError Error) Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            SqliteException sqlite when IsBusy(sqlite) => (
                OperationStatus.RetryableFailure,
                new OperationError(
                    OperationErrorCode.CatalogUnavailable,
                    "The Vault database is busy right now. Try that action again in a moment.")),

            SqliteException => (
                OperationStatus.TerminalFailure,
                new OperationError(
                    OperationErrorCode.CatalogWriteFailed,
                    "The Vault database refused this change, so nothing was saved.")),

            UnauthorizedAccessException => (
                OperationStatus.TerminalFailure,
                new OperationError(
                    OperationErrorCode.StorageAccessDenied,
                    "Windows denied access to a file this action needs. Nothing was changed.")),

            Win32Exception => (
                OperationStatus.TerminalFailure,
                new OperationError(
                    OperationErrorCode.ExternalProcessFailed,
                    "Windows could not start the external program for this action.")),

            TimeoutException => (
                OperationStatus.RetryableFailure,
                new OperationError(
                    OperationErrorCode.StorageUnavailable,
                    "That step took too long to respond and was stopped. You can try again.")),

            IOException => (
                OperationStatus.RetryableFailure,
                new OperationError(
                    OperationErrorCode.StorageUnavailable,
                    "A file this action needs is in use or unavailable. Close other apps and try again.")),

            _ => (
                OperationStatus.TerminalFailure,
                OperationError.Unexpected()),
        };
    }

    /// <summary>
    /// The safe, user-facing sentence for an unexpected exception that reached a presentation
    /// surface. Never returns provider text, a stack trace or an absolute internal path, so a
    /// surface can report a failure truthfully without leaking internals.
    /// </summary>
    public static string SafeMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is OperationCanceledException
            ? OperationError.CancelledUserMessage
            : Classify(exception).Error.UserMessage;
    }

    private static bool IsBusy(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6 or 261 or 262; // SQLITE_BUSY / SQLITE_LOCKED and their extended codes.

    private void Report(
        OperationContext context,
        Exception exception,
        (OperationStatus Status, OperationError Error) classified)
    {
        if (_diagnostics is null)
        {
            return;
        }

        _diagnostics.Write(new DiagnosticEvent(
            _clock.UtcNow,
            classified.Status.IsRetryable() ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
            Capability: "operation",
            Code: context.Kind,
            SafeErrorDetail: $"{classified.Error.ErrorCode}: {exception.GetType().Name}: {exception.Message}",
            ProfileId: context.ProfileId,
            MediaId: context.MediaId,
            IdentityId: context.IdentityId,
            FaceId: context.FaceId,
            ImportSessionId: context.ImportSessionId,
            ImportUnitId: context.ImportUnitId,
            ImportItemId: context.ImportItemId,
            JobId: context.JobId,
            OperationId: context.OperationId,
            StateTransition: classified.Status.ToCanonicalCode()));
    }
}
