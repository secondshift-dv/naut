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
    string? AbortReason = null,
    string? HandoffSha256 = null,
    string? ManifestAuthoritySha256 = null,
    UpdateStartupCutoff? RecoveryOrigin = null)
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

    public Task SaveAsync(UpdateState state, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(state, false, cancellationToken);

    internal Task PublishVerifiedRecoveryAsync(UpdateState state, CancellationToken cancellationToken) =>
        SaveCoreAsync(state, true, cancellationToken);

    private async Task SaveCoreAsync(UpdateState state, bool verifiedRecovery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);

        var previous = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            if (previous.OperationId != state.OperationId)
            {
                if (!previous.IsTerminal || state.Cutoff != UpdateStartupCutoff.HandoffPending)
                    throw new InvalidOperationException("A different update operation still owns recovery authority.");
            }
            else
            {
                var allowed = previous.Cutoff == state.Cutoff ||
                    ((previous.Cutoff, state.Cutoff) switch
                    {
                        (UpdateStartupCutoff.None or UpdateStartupCutoff.HandoffPending,
                            UpdateStartupCutoff.CatalogWriteStarted or UpdateStartupCutoff.AbortedNoMutation or UpdateStartupCutoff.RecoveryRequired) => true,
                        (UpdateStartupCutoff.CatalogWriteStarted,
                            UpdateStartupCutoff.CleanupPending or UpdateStartupCutoff.RecoveryRequired) => true,
                        (UpdateStartupCutoff.CleanupPending, UpdateStartupCutoff.Completed or UpdateStartupCutoff.RecoveryRequired) => true,
                        (UpdateStartupCutoff.Completed, UpdateStartupCutoff.CleanupPending) => true,
                        (UpdateStartupCutoff.AbortedNoMutation, UpdateStartupCutoff.RecoveryRequired) => true,
                        _ => false
                    });
                if (verifiedRecovery && previous.Cutoff == UpdateStartupCutoff.RecoveryRequired && previous.HandoffSha256 is not null)
                    allowed = (previous.RecoveryOrigin, state.Cutoff) switch
                    {
                        (UpdateStartupCutoff.HandoffPending, UpdateStartupCutoff.AbortedNoMutation or UpdateStartupCutoff.CatalogWriteStarted) => true,
                        (UpdateStartupCutoff.CatalogWriteStarted, UpdateStartupCutoff.CatalogWriteStarted) => true,
                        _ => false
                    };
                if (!allowed)
                    throw new InvalidOperationException("Update cutoff cannot move backwards.");
                if ((state.HandoffSha256 is not null && state.HandoffSha256 != previous.HandoffSha256) ||
                    (state.ManifestAuthoritySha256 is not null && state.ManifestAuthoritySha256 != previous.ManifestAuthoritySha256))
                    throw new InvalidOperationException("Update approval cannot be changed.");
                state = state with { HandoffSha256 = previous.HandoffSha256,
                    ManifestAuthoritySha256 = previous.ManifestAuthoritySha256,
                    RecoveryOrigin = state.Cutoff == UpdateStartupCutoff.RecoveryRequired
                        ? previous.Cutoff == UpdateStartupCutoff.RecoveryRequired ? previous.RecoveryOrigin
                          : previous.Cutoff is UpdateStartupCutoff.HandoffPending or UpdateStartupCutoff.CatalogWriteStarted ? previous.Cutoff : null
                        : null };
            }
        }

        ValidateState(state);
        var path = _paths.UpdateStateFilePath;
        await Neuterradise.Release.Contracts.UpdatePathGuard.WriteAtomicAsync(path,
            JsonSerializer.SerializeToUtf8Bytes(state), cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken = default,
        string? handoffSha256 = null,
        string? manifestAuthoritySha256 = null) =>
        SaveAsync(
            new UpdateState(
                operationId,
                UpdateStartupCutoff.HandoffPending,
                null,
                DateTimeOffset.UtcNow,
                HandoffSha256: handoffSha256,
                ManifestAuthoritySha256: manifestAuthoritySha256),
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

    private static void ValidateState(UpdateState state)
    {
        if ((state.HandoffSha256 is null) != (state.ManifestAuthoritySha256 is null) ||
            (state.HandoffSha256 is not null &&
             (!Neuterradise.Release.Contracts.UpdateManifestAuthority.IsLowerSha256(state.HandoffSha256) ||
              !Neuterradise.Release.Contracts.UpdateManifestAuthority.IsLowerSha256(state.ManifestAuthoritySha256!))))
            throw new FormatException("Persisted update approval digests are invalid.");
        if (state.RecoveryOrigin is not null && (state.Cutoff != UpdateStartupCutoff.RecoveryRequired ||
            state.RecoveryOrigin is not (UpdateStartupCutoff.HandoffPending or UpdateStartupCutoff.CatalogWriteStarted)))
            throw new FormatException("Persisted update recovery origin is invalid.");
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
