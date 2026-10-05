using System.Text.Json;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Trash;

/// <summary>
/// Reverses only media ownership handoffs that are still attributable to the Profile Trash operation.
/// Later user ownership changes remain authoritative. Managed-path work always converges through
/// the persisted storage-operation authority before Restore finalizes.
/// </summary>
public sealed class ProfileOwnershipRestoreCoordinator
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly MediaOperations _mediaOperations;
    private readonly PathReconciler _pathReconciler;

    public ProfileOwnershipRestoreCoordinator(
        CatalogDb catalog,
        MediaOperations? mediaOperations = null,
        PathReconciler? pathReconciler = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _mediaOperations = mediaOperations ?? new MediaOperations(catalog);
        _pathReconciler = pathReconciler ?? new PathReconciler(
            catalog.Paths,
            catalog.ProfileWrites,
            catalog.MediaWrites,
            new ManagedFileVerifier(),
            new WindowsVolumeIdentityProvider());
    }

    internal async Task<ProfileOwnershipRestoreSummary> RestoreAsync(
        ProfileTrashPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var returned = 0;
        var preserved = 0;

        foreach (var disposition in plan.SelectedDispositions
                     .Where(static item => item.Kind == ProfileOwnedMediaDispositionKind.ChangeOwner)
                     .OrderBy(static item => item.MediaId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (disposition.NewOwnerProfileId is not { } deleteAssignedOwnerProfileId)
            {
                return ProfileOwnershipRestoreSummary.Failed(
                    returned,
                    preserved,
                    OperationErrorCode.MediaOwnerConflict,
                    "A Profile Trash ownership handoff has no recorded destination owner.");
            }

            var state = await ReadOwnershipStateAsync(disposition.MediaId, cancellationToken)
                .ConfigureAwait(false);
            if (state is null || state.State != MediaState.Active)
            {
                preserved++;
                continue;
            }

            if (state.OwnerCount != 1
                || state.OwnerProfileId is null
                || state.OwnerRelationCreatedAtMilliseconds is null)
            {
                return ProfileOwnershipRestoreSummary.Failed(
                    returned,
                    preserved,
                    OperationErrorCode.MediaOwnerConflict,
                    "An active media item no longer has exactly one authoritative owner.");
            }

            if (state.OwnerProfileId == plan.ProfileId)
            {
                var convergence = await ConvergeManagedPathAsync(
                        disposition.MediaId,
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (convergence.Failure is not null)
                {
                    return ProfileOwnershipRestoreSummary.Failed(
                        returned,
                        preserved,
                        convergence.Failure.ErrorCode,
                        convergence.Failure.Message);
                }

                returned++;
                continue;
            }

            if (state.OwnerProfileId != deleteAssignedOwnerProfileId)
            {
                preserved++;
                continue;
            }

            if (!await IsDeleteOwnershipStillAuthoritativeAsync(
                    plan,
                    disposition,
                    state,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                preserved++;
                continue;
            }

            var deleteRelocation = await ConvergeManagedPathAsync(
                    disposition.MediaId,
                    state,
                    cancellationToken)
                .ConfigureAwait(false);
            if (deleteRelocation.Failure is not null)
            {
                return ProfileOwnershipRestoreSummary.Failed(
                    returned,
                    preserved,
                    deleteRelocation.Failure.ErrorCode,
                    deleteRelocation.Failure.Message);
            }

            state = deleteRelocation.State
                ?? await ReadOwnershipStateAsync(disposition.MediaId, cancellationToken)
                    .ConfigureAwait(false);
            if (state is null || state.State != MediaState.Active)
            {
                preserved++;
                continue;
            }

            if (state.OwnerCount != 1
                || state.OwnerProfileId is null
                || state.OwnerRelationCreatedAtMilliseconds is null)
            {
                return ProfileOwnershipRestoreSummary.Failed(
                    returned,
                    preserved,
                    OperationErrorCode.MediaOwnerConflict,
                    "An active media item lost its authoritative owner while Profile Restore was running.");
            }

            if (state.OwnerProfileId != deleteAssignedOwnerProfileId)
            {
                preserved++;
                continue;
            }

            if (!await IsDeleteOwnershipStillAuthoritativeAsync(
                    plan,
                    disposition,
                    state,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                preserved++;
                continue;
            }

            var transfer = await _mediaOperations.ChangePrimaryProfileAsync(
                    new ChangePrimaryProfileRequest(
                        disposition.MediaId,
                        plan.ProfileId,
                        state.RowVersion),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!transfer.IsSuccess)
            {
                var raced = await ReadOwnershipStateAsync(disposition.MediaId, cancellationToken)
                    .ConfigureAwait(false);
                if (raced is null || raced.State != MediaState.Active)
                {
                    preserved++;
                    continue;
                }

                if (raced.OwnerCount == 1
                    && raced.OwnerProfileId is { } racedOwner
                    && racedOwner != deleteAssignedOwnerProfileId)
                {
                    if (racedOwner == plan.ProfileId)
                    {
                        var convergence = await ConvergeManagedPathAsync(
                                disposition.MediaId,
                                raced,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (convergence.Failure is not null)
                        {
                            return ProfileOwnershipRestoreSummary.Failed(
                                returned,
                                preserved,
                                convergence.Failure.ErrorCode,
                                convergence.Failure.Message);
                        }

                        returned++;
                    }
                    else
                    {
                        preserved++;
                    }

                    continue;
                }

                return ProfileOwnershipRestoreSummary.Failed(
                    returned,
                    preserved,
                    transfer.ErrorCode ?? OperationErrorCode.MediaOwnerConflict,
                    transfer.UserMessage ?? "A media owner could not be restored safely.");
            }

            var restoredState = await ReadOwnershipStateAsync(disposition.MediaId, cancellationToken)
                .ConfigureAwait(false);
            if (restoredState is null
                || restoredState.State != MediaState.Active
                || restoredState.OwnerCount != 1
                || restoredState.OwnerProfileId != plan.ProfileId)
            {
                return ProfileOwnershipRestoreSummary.Failed(
                    returned,
                    preserved,
                    OperationErrorCode.MediaOwnerConflict,
                    "A media owner changed while Profile Restore was returning it to the restored Profile.");
            }

            var restoredRelocation = await ConvergeManagedPathAsync(
                    disposition.MediaId,
                    restoredState,
                    cancellationToken)
                .ConfigureAwait(false);
            if (restoredRelocation.Failure is not null)
            {
                return ProfileOwnershipRestoreSummary.Failed(
                    returned,
                    preserved,
                    restoredRelocation.Failure.ErrorCode,
                    restoredRelocation.Failure.Message);
            }

            returned++;
        }

        return new ProfileOwnershipRestoreSummary(returned, preserved, null);
    }

    private async Task<OwnerRelocationConvergence> ConvergeManagedPathAsync(
        Guid mediaId,
        ProfileRestoreMediaOwnershipState state,
        CancellationToken cancellationToken)
    {
        if (state.PathState == ManagedPathState.None)
        {
            return new OwnerRelocationConvergence(state, null);
        }

        StorageOperationResult result;
        if (string.Equals(state.ReconciliationKind, "OWNER_RELOCATION", StringComparison.Ordinal))
        {
            result = await _pathReconciler
                .ReconcileOwnerRelocationAsync(mediaId, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (string.Equals(state.ReconciliationKind, "PROFILE_RENAME", StringComparison.Ordinal)
                 && state.OwnerProfileId is { } ownerProfileId)
        {
            result = await _pathReconciler
                .ReconcileProfileRenameAsync(ownerProfileId, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            return OwnerRelocationConvergence.Failed(
                OperationErrorCode.ProfilePathReconciliationBlocked,
                "A media item has a pending managed-path operation that Profile Restore cannot safely classify.");
        }

        if (!result.IsSuccess)
        {
            return OwnerRelocationConvergence.Failed(
                OperationErrorCode.ProfilePathReconciliationBlocked,
                result.SafeErrorDetail
                ?? "A media file relocation could not be completed safely during Profile Restore.");
        }

        var refreshed = await ReadOwnershipStateAsync(mediaId, cancellationToken).ConfigureAwait(false);
        if (refreshed is null || refreshed.PathState != ManagedPathState.None)
        {
            return OwnerRelocationConvergence.Failed(
                OperationErrorCode.ProfilePathReconciliationBlocked,
                "A media file relocation did not converge to a stable managed path during Profile Restore.");
        }

        return new OwnerRelocationConvergence(refreshed, null);
    }

    private async Task<bool> IsDeleteOwnershipStillAuthoritativeAsync(
        ProfileTrashPlan plan,
        ProfileOwnedMediaDisposition disposition,
        ProfileRestoreMediaOwnershipState state,
        CancellationToken cancellationToken)
    {
        if (disposition.NewOwnerProfileId is not { } assignedOwnerProfileId
            || state.OwnerProfileId != assignedOwnerProfileId
            || state.OwnerRelationCreatedAtMilliseconds is not { } currentOwnerCreatedAt)
        {
            return false;
        }

        var receipt = plan.OwnerTransferReceipts
            .FirstOrDefault(item => item.MediaId == disposition.MediaId);
        if (receipt is not null)
        {
            return receipt.AssignedOwnerProfileId == assignedOwnerProfileId
                && receipt.OwnerRelationCreatedAtMilliseconds == currentOwnerCreatedAt;
        }

        return await IsLegacyDeleteOwnershipHandoffAttributableAsync(
                plan,
                disposition.MediaId,
                assignedOwnerProfileId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> IsLegacyDeleteOwnershipHandoffAttributableAsync(
        ProfileTrashPlan plan,
        Guid mediaId,
        Guid assignedOwnerProfileId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT payload_json
            FROM activity_log
            WHERE event_type = $eventType
              AND media_id = $mediaId
              AND occurred_at_ms >= $preparedAt
            ORDER BY occurred_at_ms, activity_id;
            """;
        command.Parameters.AddWithValue("$eventType", ActivityEventType.PrimaryProfileChanged);
        command.Parameters.AddWithValue("$mediaId", DbGuid.Format(mediaId));
        command.Parameters.AddWithValue("$preparedAt", DbTime.Format(plan.PreparedAtUtc));

        var ownerChanges = new List<(Guid PreviousOwner, Guid NewOwner)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0)
                || !TryReadOwnerChangePayload(reader.GetString(0), out var previousOwner, out var newOwner))
            {
                return false;
            }

            ownerChanges.Add((previousOwner, newOwner));
            if (ownerChanges.Count > 1)
            {
                return false;
            }
        }

        return ownerChanges.Count == 1
            && ownerChanges[0].PreviousOwner == plan.ProfileId
            && ownerChanges[0].NewOwner == assignedOwnerProfileId;
    }

    private static bool TryReadOwnerChangePayload(
        string payloadJson,
        out Guid previousOwnerProfileId,
        out Guid newOwnerProfileId)
    {
        previousOwnerProfileId = Guid.Empty;
        newOwnerProfileId = Guid.Empty;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("previousOwnerProfileId", out var previous)
                || !root.TryGetProperty("newOwnerProfileId", out var next)
                || previous.ValueKind != JsonValueKind.String
                || next.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            previousOwnerProfileId = DbGuid.Parse(previous.GetString()!);
            newOwnerProfileId = DbGuid.Parse(next.GetString()!);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return false;
        }
    }

    private async Task<ProfileRestoreMediaOwnershipState?> ReadOwnershipStateAsync(
        Guid mediaId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT m.state, m.row_version, m.path_state,
                   (SELECT kind FROM storage_operations operation
                    WHERE operation.operation_id = m.reconciliation_operation_id) AS reconciliation_kind,
                   (SELECT COUNT(*) FROM profile_media owner
                    WHERE owner.media_id = m.media_id AND owner.relation_type = 'OWNER') AS owner_count,
                   (SELECT owner.profile_id FROM profile_media owner
                    WHERE owner.media_id = m.media_id AND owner.relation_type = 'OWNER'
                    ORDER BY owner.profile_id LIMIT 1) AS owner_profile_id,
                   (SELECT owner.created_at_ms FROM profile_media owner
                    WHERE owner.media_id = m.media_id AND owner.relation_type = 'OWNER'
                    ORDER BY owner.profile_id LIMIT 1) AS owner_created_at_ms
            FROM media m
            WHERE m.media_id = $mediaId;
            """;
        command.Parameters.AddWithValue("$mediaId", DbGuid.Format(mediaId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProfileRestoreMediaOwnershipState(
            DbEnum.ParseMediaState(reader.GetString(0)),
            reader.GetInt64(1),
            DbEnum.ParseManagedPathState(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            checked((int)reader.GetInt64(4)),
            reader.IsDBNull(5) ? null : DbGuid.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetInt64(6));
    }

    private sealed record ProfileRestoreMediaOwnershipState(
        MediaState State,
        long RowVersion,
        ManagedPathState PathState,
        string? ReconciliationKind,
        int OwnerCount,
        Guid? OwnerProfileId,
        long? OwnerRelationCreatedAtMilliseconds);

    private sealed record OwnerRelocationConvergence(
        ProfileRestoreMediaOwnershipState? State,
        ProfileOwnershipRestoreFailure? Failure)
    {
        public static OwnerRelocationConvergence Failed(string errorCode, string message) =>
            new(null, new ProfileOwnershipRestoreFailure(errorCode, message));
    }
}

internal sealed record ProfileOwnershipRestoreFailure(
    string ErrorCode,
    string Message);

internal sealed record ProfileOwnershipRestoreSummary(
    int ReturnedOwnedMediaCount,
    int PreservedChangedOwnershipCount,
    ProfileOwnershipRestoreFailure? Failure)
{
    public static ProfileOwnershipRestoreSummary Failed(
        int returnedOwnedMediaCount,
        int preservedChangedOwnershipCount,
        string errorCode,
        string message) =>
        new(
            returnedOwnedMediaCount,
            preservedChangedOwnershipCount,
            new ProfileOwnershipRestoreFailure(errorCode, message));
}
