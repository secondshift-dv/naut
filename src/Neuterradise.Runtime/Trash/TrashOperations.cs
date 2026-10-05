using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Trash;

public sealed class TrashOperations
{
    private readonly CatalogDb _catalog;
    private readonly TrashCoordinator _trashCoordinator;
    private readonly RestoreExecutor _restoreExecutor;
    private readonly ProfileTrashDispositionPlanner _profileTrashPlanner;
    private readonly TimeProvider _timeProvider;

    public TrashOperations(
        CatalogDb catalog,
        TrashCoordinator? trashCoordinator = null,
        RestoreExecutor? restoreExecutor = null,
        ManagedMoveExecutor? moveExecutor = null,
        MediaOperations? mediaOperations = null,
        TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _timeProvider = timeProvider ?? TimeProvider.System;

        moveExecutor ??= new ManagedMoveExecutor(
            catalog.Paths,
            new WindowsVolumeIdentityProvider(),
            new ManagedFileVerifier(),
            new MediaWrites(catalog));

        mediaOperations ??= new MediaOperations(catalog, _timeProvider);

        _trashCoordinator = trashCoordinator ?? new TrashCoordinator(
            catalog,
            moveExecutor,
            mediaOperations,
            _timeProvider);

        _restoreExecutor = restoreExecutor ?? new RestoreExecutor(
            catalog,
            moveExecutor,
            _timeProvider,
            profileOwnershipRestore: new ProfileOwnershipRestoreCoordinator(
                catalog,
                mediaOperations));
        _profileTrashPlanner = new ProfileTrashDispositionPlanner(catalog);
    }

    public async Task<OperationResult<MediaTrashOutcome>> MoveMediaToTrashAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(MoveMediaToTrashAsync));
        if (assetId == Guid.Empty)
        {
            return OperationResult<MediaTrashOutcome>.Validation(
                OperationErrorCode.MediaNotFound,
                "A stable media identifier is required to move media to Trash.");
        }

        await using (var connection = await _catalog.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var activeEntry = await TrashCoordinator.ReadActiveTrashEntryAsync(
                connection, TrashEntityType.Media, assetId, cancellationToken).ConfigureAwait(false);
            if (activeEntry is not null && activeEntry.State == TrashEntryState.InTrash)
            {
                var asset = await TrashCoordinator.ReadMediaTrashStateAsync(connection, transaction: null, assetId, cancellationToken)
                    .ConfigureAwait(false);
                var plan = MediaTrashPlan.FromJson(activeEntry.PlanJson);
                return OperationResult<MediaTrashOutcome>.Success(
                    new MediaTrashOutcome(
                        activeEntry.TrashEntryId,
                        assetId,
                        activeEntry.RecoveryRelativePath ?? string.Empty,
                        asset?.RowVersion ?? activeEntry.RowVersion),
                    plan?.OperationId);
            }
        }

        var prepareResult = await _trashCoordinator.PrepareMediaTrashAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (!prepareResult.IsSuccess || prepareResult.Value is null)
        {
            return new OperationResult<MediaTrashOutcome>(
                prepareResult.Status,
                default,
                prepareResult.ErrorCode,
                prepareResult.UserMessage,
                prepareResult.OperationId);
        }

        return await _trashCoordinator.ExecuteMediaTrashAsync(prepareResult.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<ProfileTrashOutcome>> MoveProfileToTrashAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            return OperationResult<ProfileTrashOutcome>.Validation(
                OperationErrorCode.ProfileNotFound,
                "A stable Profile identifier is required.");
        }

        await using (var connection = await _catalog.ConnectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var activeEntry = await TrashCoordinator.ReadActiveTrashEntryAsync(
                connection,
                TrashEntityType.Profile,
                profileId,
                cancellationToken).ConfigureAwait(false);
            if (activeEntry is not null && activeEntry.State == TrashEntryState.InTrash)
            {
                return await CommitTrashProfileAsync(
                    profileId,
                    [],
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var prepare = await PrepareTrashProfileAsync(profileId, cancellationToken)
            .ConfigureAwait(false);
        if (!prepare.IsSuccess || prepare.Value is null)
        {
            return new OperationResult<ProfileTrashOutcome>(
                prepare.Status,
                default,
                prepare.ErrorCode,
                prepare.UserMessage,
                prepare.OperationId);
        }

        var dispositions = await _profileTrashPlanner
            .BuildAsync(prepare.Value, cancellationToken)
            .ConfigureAwait(false);
        return await CommitTrashProfileAsync(
            prepare.Value,
            dispositions,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<ProfileTrashPlan>> PrepareTrashProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(PrepareTrashProfileAsync));
        if (profileId == Guid.Empty)
        {
            return OperationResult<ProfileTrashPlan>.Validation(
                OperationErrorCode.ProfileNotFound,
                "A stable Profile identifier is required.");
        }

        return await _trashCoordinator.PrepareProfileTrashAsync(profileId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<ProfileTrashOutcome>> CommitTrashProfileAsync(
        ProfileTrashPlan plan,
        IReadOnlyList<ProfileOwnedMediaDisposition> dispositions,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(CommitTrashProfileAsync));
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispositions);

        return await _trashCoordinator.CommitProfileTrashAsync(plan, dispositions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<ProfileTrashOutcome>> CommitTrashProfileAsync(
        Guid profileId,
        IReadOnlyList<ProfileOwnedMediaDisposition> dispositions,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(CommitTrashProfileAsync));
        if (profileId == Guid.Empty)
        {
            return OperationResult<ProfileTrashOutcome>.Validation(
                OperationErrorCode.ProfileNotFound,
                "A stable Profile identifier is required.");
        }

        ArgumentNullException.ThrowIfNull(dispositions);

        await using (var connection = await _catalog.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var activeEntry = await TrashCoordinator.ReadActiveTrashEntryAsync(
                connection, TrashEntityType.Profile, profileId, cancellationToken).ConfigureAwait(false);
            if (activeEntry is not null && activeEntry.State == TrashEntryState.InTrash)
            {
                var profile = await TrashCoordinator.ReadProfileTrashStateAsync(connection, transaction: null, profileId, cancellationToken)
                    .ConfigureAwait(false);
                var plan = ProfileTrashPlan.FromJson(activeEntry.PlanJson);
                return OperationResult<ProfileTrashOutcome>.Success(
                    new ProfileTrashOutcome(
                        activeEntry.TrashEntryId,
                        profileId,
                        profile?.RowVersion ?? activeEntry.RowVersion,
                        0,
                        0),
                    plan?.OperationId);
            }
        }

        var prepareResult = await _trashCoordinator.PrepareProfileTrashAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (!prepareResult.IsSuccess || prepareResult.Value is null)
        {
            return new OperationResult<ProfileTrashOutcome>(
                prepareResult.Status,
                default,
                prepareResult.ErrorCode,
                prepareResult.UserMessage,
                prepareResult.OperationId);
        }

        return await _trashCoordinator.CommitProfileTrashAsync(prepareResult.Value, dispositions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<MediaRestoreOutcome>> RestoreMediaAsync(
        Guid assetOrTrashEntryId,
        Guid? targetOwnerProfileId = null,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(RestoreMediaAsync));
        if (assetOrTrashEntryId == Guid.Empty)
        {
            return OperationResult<MediaRestoreOutcome>.Validation(
                OperationErrorCode.MediaNotFound,
                "A stable identifier is required to restore media.");
        }

        var trashEntryId = await ResolveTrashEntryIdAsync(
            TrashEntityType.Media, assetOrTrashEntryId, cancellationToken).ConfigureAwait(false);

        return await _restoreExecutor.RestoreMediaAsync(trashEntryId, targetOwnerProfileId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<ProfileRestoreOutcome>> RestoreProfileAsync(
        Guid profileOrTrashEntryId,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(RestoreProfileAsync));
        if (profileOrTrashEntryId == Guid.Empty)
        {
            return OperationResult<ProfileRestoreOutcome>.Validation(
                OperationErrorCode.ProfileNotFound,
                "A stable identifier is required to restore a Profile.");
        }

        var trashEntryId = await ResolveTrashEntryIdAsync(
            TrashEntityType.Profile, profileOrTrashEntryId, cancellationToken).ConfigureAwait(false);

        return await _restoreExecutor.RestoreProfileAsync(trashEntryId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> ResolveTrashEntryIdAsync(
        string entityType,
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var directEntry = await TrashCoordinator.ReadTrashEntryAsync(connection, candidateId, cancellationToken).ConfigureAwait(false);
        if (directEntry is not null && directEntry.EntityType == entityType)
        {
            return directEntry.TrashEntryId;
        }

        var activeEntry = await TrashCoordinator.ReadActiveTrashEntryAsync(connection, entityType, candidateId, cancellationToken).ConfigureAwait(false);
        if (activeEntry is not null)
        {
            return activeEntry.TrashEntryId;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT trash_entry_id
            FROM trash_entries
            WHERE entity_type = $entityType
              AND entity_id = $entityId
            ORDER BY created_at_ms DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", DbGuid.Format(candidateId));

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is string idStr)
        {
            return DbGuid.Parse(idStr);
        }

        return candidateId;
    }
}
