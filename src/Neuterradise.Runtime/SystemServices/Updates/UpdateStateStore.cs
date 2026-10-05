using System.Text.Json;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Updates;

public enum UpdateStartupCutoff
{
    // Persisted as numeric JSON by the existing schema. Never renumber these values.
    None = 0,
    HandoffPending = 1,
    CatalogWriteStarted = 2,
    RecoveryRequired = 3,
    Completed = 4,
    AbortedNoMutation = 5,
    CleanupPending = 6
}

public sealed record UpdateState(
    Guid OperationId,
    UpdateStartupCutoff Cutoff,
    string? BackupPath,
    DateTimeOffset UpdatedAtUtc,
    string? AbortReason = null)
{
    public bool MayAutoRollbackBinary =>
        Cutoff is UpdateStartupCutoff.None or UpdateStartupCutoff.HandoffPending;

    public bool IsTerminal =>
        Cutoff is UpdateStartupCutoff.Completed or UpdateStartupCutoff.AbortedNoMutation;

    public bool BlocksStartup => Cutoff == UpdateStartupCutoff.RecoveryRequired;
}

public sealed class UpdateStateStore
{
    private readonly AppStatePaths _paths;

    public UpdateStateStore(AppStatePaths paths) =>
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public async Task SaveAsync(
        UpdateState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);

        var path = _paths.UpdateStateFilePath;
        var temp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            temp,
            JsonSerializer.Serialize(state),
            cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<UpdateAuthorityReadResult<UpdateState>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var path = _paths.UpdateStateFilePath;
        try
        {
            if (!File.Exists(path))
                return UpdateAuthorityReadResult<UpdateState>.Missing();

            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer
                .DeserializeAsync<UpdateState>(
                    stream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false)
                ?? throw new FormatException("Persisted update state is empty.");

            ValidateState(state);
            return UpdateAuthorityReadResult<UpdateState>.Valid(state);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
            or NotSupportedException
            or FormatException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return UpdateAuthorityReadResult<UpdateState>.Corrupt(
                $"Persisted update state is corrupt or unreadable ({exception.GetType().Name}).");
        }
    }

    public async Task<UpdateState?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return read.Status switch
        {
            UpdateAuthorityReadStatus.Missing => null,
            UpdateAuthorityReadStatus.Valid => read.Value,
            UpdateAuthorityReadStatus.Corrupt => throw new FormatException(
                read.SafeError ?? "Persisted update state is corrupt or unreadable."),
            _ => throw new InvalidOperationException("Unknown update state read status."),
        };
    }

    public Task PublishHandoffPendingAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        SaveAsync(
            new UpdateState(
                operationId,
                UpdateStartupCutoff.HandoffPending,
                null,
                DateTimeOffset.UtcNow),
            cancellationToken);

    public Task PublishCatalogWriteStartedAsync(
        Guid operationId,
        string? backupPath,
        CancellationToken cancellationToken = default) =>
        SaveAsync(
            new UpdateState(
                operationId,
                UpdateStartupCutoff.CatalogWriteStarted,
                backupPath,
                DateTimeOffset.UtcNow),
            cancellationToken);

    public Task PublishCleanupPendingAsync(
        Guid operationId,
        string? backupPath,
        CancellationToken cancellationToken = default) =>
        SaveAsync(
            new UpdateState(
                operationId,
                UpdateStartupCutoff.CleanupPending,
                backupPath,
                DateTimeOffset.UtcNow),
            cancellationToken);

    public Task PublishCompletedAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        SaveAsync(
            new UpdateState(
                operationId,
                UpdateStartupCutoff.Completed,
                null,
                DateTimeOffset.UtcNow),
            cancellationToken);

    public Task PublishRecoveryRequiredAsync(
        Guid operationId,
        string? backupPath,
        CancellationToken cancellationToken = default) =>
        SaveAsync(
            new UpdateState(
                operationId,
                UpdateStartupCutoff.RecoveryRequired,
                backupPath,
                DateTimeOffset.UtcNow),
            cancellationToken);

    public Task PublishAbortedNoMutationAsync(
        Guid operationId,
        string? reason,
        CancellationToken cancellationToken = default) =>
        SaveAsync(
            new UpdateState(
                operationId,
                UpdateStartupCutoff.AbortedNoMutation,
                null,
                DateTimeOffset.UtcNow,
                reason),
            cancellationToken);

    public async Task RetirePendingStateAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsValid)
            return;

        var current = read.Value!;
        if (current.OperationId != operationId || current.IsTerminal)
            return;

        if (current.Cutoff is UpdateStartupCutoff.HandoffPending or UpdateStartupCutoff.None)
        {
            await PublishAbortedNoMutationAsync(
                operationId,
                "Handoff retired before replacement began.",
                cancellationToken).ConfigureAwait(false);
        }

        // CatalogWriteStarted, CleanupPending, and RecoveryRequired are deliberately
        // not promoted here. They require startup evidence, cleanup convergence,
        // or explicit recovery respectively.
    }

    private static void ValidateState(UpdateState state)
    {
        if (state.OperationId == Guid.Empty)
            throw new FormatException("Persisted update state has an invalid operation id.");
        if (!Enum.IsDefined(state.Cutoff))
            throw new FormatException("Persisted update state has an unknown cutoff.");
        if (state.UpdatedAtUtc == default)
            throw new FormatException("Persisted update state has no update timestamp.");
        if (state.Cutoff == UpdateStartupCutoff.AbortedNoMutation
            && string.IsNullOrWhiteSpace(state.AbortReason))
        {
            throw new FormatException("Aborted update state is missing its reason.");
        }
    }
}
