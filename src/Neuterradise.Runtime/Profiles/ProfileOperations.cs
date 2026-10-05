using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.Profiles;

public sealed class ProfileOperations
{
    private readonly CatalogDb _catalog;
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly ProfileWrites _profileWrites;
    private readonly ManagedPathPlanner _pathPlanner;
    private readonly ExplorerLocationProvider _explorerLocations;
    private readonly TimeProvider _timeProvider;

    public ProfileOperations(
        CatalogDb catalog,
        TimeProvider? timeProvider = null,
        ManagedPathPlanner? pathPlanner = null,
        ExplorerLocationProvider? explorerLocations = null,
        ProfileRenameReconciliationEnqueue? renameReconciliationEnqueue = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _profileWrites = new ProfileWrites(catalog, _timeProvider);
        _pathPlanner = pathPlanner ?? new ManagedPathPlanner(_catalog.Paths.Root);
        _explorerLocations = explorerLocations ?? new ExplorerLocationProvider(catalog.Paths);
    }

    public async Task<NormalProfileCreationResult> CreateNormalProfileAsync(
        CreateNormalProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(CreateNormalProfileAsync));
        ArgumentNullException.ThrowIfNull(command);
        if (command.ProfileId == Guid.Empty)
        {
            throw new ArgumentException("Profile identifier cannot be empty.", nameof(command));
        }

        var normalizedName = ProfileRules.NormalizeDisplayName(command.DisplayName);
        var identityId = command.IdentityId ?? Guid.NewGuid();
        if (identityId == Guid.Empty)
        {
            throw new ArgumentException("Identity identifier cannot be empty.", nameof(command));
        }

        ProfileRules.ValidateIdentity(identityId, command.ProfileId, ProfileKind.Normal);

        var existing = await _catalog.ProfileReads.GetHeaderAsync(command.ProfileId, cancellationToken).ConfigureAwait(false);

        var result = await _profileWrites.CreateNormalProfileAsync(
            command.ProfileId,
            identityId,
            normalizedName,
            command.Visibility,
            cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            await _catalog.ActivityWrites.AppendAsync(new ActivityEntryPersistence(
                ActivityId: Guid.NewGuid(),
                EventType: ActivityEventType.ProfileCreated,
                ProfileId: command.ProfileId,
                MediaId: null,
                ImportUnitId: null,
                OperationId: null,
                PayloadJson: JsonSerializer.Serialize(new { displayName = normalizedName, identityId = identityId.ToString("D") }),
                OccurredAtUtc: _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(command.Visibility, "PUBLISHED", StringComparison.Ordinal))
        {
            _writeCoordinator.PublishAfterCommit(new CatalogInvalidation(
                Guid.Empty,
                [command.ProfileId],
                CatalogInvalidationDomain.Profile,
                1));
        }

        return result;
    }

    public async Task<UnknownProfileCreationResult> CreateUnknownProfileAsync(
        CreateUnknownProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(CreateUnknownProfileAsync));
        ArgumentNullException.ThrowIfNull(command);
        if (command.ProfileId == Guid.Empty)
        {
            throw new ArgumentException("Profile identifier cannot be empty.", nameof(command));
        }

        var result = await _profileWrites.CreateUnknownProfileAsync(
            command.ProfileId,
            cancellationToken).ConfigureAwait(false);

        _writeCoordinator.PublishAfterCommit(new CatalogInvalidation(
            Guid.Empty,
            [command.ProfileId],
            CatalogInvalidationDomain.Profile,
            1));

        return result;
    }

    public async Task<OperationResult<ProfileRenameOutcome>> RenameProfileAsync(
        RenameProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(RenameProfileAsync));
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProfileId == Guid.Empty)
        {
            throw new ArgumentException("Profile identifier cannot be empty.", nameof(request));
        }

        if (!ProfileRules.TryValidateDisplayName(request.DisplayName, out var normalizedDisplayName, out var nameFailure))
        {
            return OperationResult<ProfileRenameOutcome>.Validation(
                OperationErrorCode.ProfileNameInvalid,
                nameFailure!);
        }

        ProfileRenameReconciliationRequest? reconciliation = null;
        OperationResult<ProfileRenameOutcome> result;

        await using (var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false))
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = CatalogTransaction.Begin(connection, _writeCoordinator))
        {
            var profile = await ReadRenameStateAsync(transaction, request.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (profile is null)
            {
                return OperationResult<ProfileRenameOutcome>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "That Profile no longer exists.");
            }

            if (profile.IsTrashed)
            {
                return OperationResult<ProfileRenameOutcome>.Conflict(
                    OperationErrorCode.ProfileConflict,
                    "That Profile was moved to Trash and cannot be renamed.");
            }

            if (profile.Kind != ProfileKind.Normal)
            {
                return OperationResult<ProfileRenameOutcome>.Validation(
                    OperationErrorCode.ProfileNotNormal,
                    "An Unknown Profile has no editable name; its label follows its Unknown number.");
            }

            if (string.Equals(profile.DisplayName, normalizedDisplayName, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return profile.PathState == ManagedPathState.None
                    ? OperationResult<ProfileRenameOutcome>.Success(
                        new ProfileRenameOutcome(
                            profile.ProfileId,
                            normalizedDisplayName,
                            profile.RowVersion,
                            profile.CurrentManagedRelativePath,
                            ManagedPathState.None,
                            null,
                            0))
                    : OperationResult<ProfileRenameOutcome>.Accepted(
                        new ProfileRenameOutcome(
                            profile.ProfileId,
                            normalizedDisplayName,
                            profile.RowVersion,
                            profile.TargetManagedRelativePath,
                            profile.PathState,
                            profile.ReconciliationOperationId,
                            0),
                        profile.ReconciliationOperationId,
                        "Folder and file names are still being brought in line with this name.");
            }

            if (profile.RowVersion != request.ExpectedRowVersion)
            {
                return OperationResult<ProfileRenameOutcome>.Conflict(
                    OperationErrorCode.ProfileConflict,
                    "That Profile changed somewhere else. Reload it and try the rename again.");
            }

            if (profile.PathState != ManagedPathState.None)
            {
                return OperationResult<ProfileRenameOutcome>.Conflict(
                    OperationErrorCode.ProfilePathReconciliationBlocked,
                    "This Profile's folder is still being renamed. Wait for that to finish, then rename again.");
            }

            var now = DbTime.Format(_timeProvider.GetUtcNow());
            var newRowVersion = profile.RowVersion + 1;

            if (profile.StorageToken is null)
            {
                await UpdateProfileNameAsync(
                    transaction, request.ProfileId, normalizedDisplayName, now, newRowVersion,
                    profile.RowVersion, cancellationToken).ConfigureAwait(false);
                await AppendActivityAsync(
                    transaction,
                    ActivityEventType.ProfileRenamed,
                    request.ProfileId,
                    operationId: null,
                    BuildRenamePayload(normalizedDisplayName, targetFolder: null, plannedMedias: 0),
                    DbTime.Format(_timeProvider.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
                transaction.QueueInvalidation(new CatalogInvalidation(
                    Guid.Empty,
                    [request.ProfileId],
                    CatalogInvalidationDomain.Profile,
                    newRowVersion));
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return OperationResult<ProfileRenameOutcome>.Success(
                    new ProfileRenameOutcome(
                        request.ProfileId, normalizedDisplayName, newRowVersion,
                        null, ManagedPathState.None, null, 0));
            }

            var ownedMedias = await ReadOwnedManagedMediasAsync(
                transaction, request.ProfileId, cancellationToken).ConfigureAwait(false);

            if (ownedMedias.Any(asset => asset.PathState != ManagedPathState.None))
            {
                return OperationResult<ProfileRenameOutcome>.Conflict(
                    OperationErrorCode.ProfilePathReconciliationBlocked,
                    "Media in this Profile is still being moved. Wait for that to finish, then rename again.");
            }

            var collisions = await ReadRenameCollisionSnapshotAsync(
                transaction,
                request.ProfileId,
                cancellationToken).ConfigureAwait(false);

            string targetFolder;
            List<OwnedMediaRenamePlan> assetPlans;
            try
            {
                (targetFolder, assetPlans) = PlanRenameTargets(
                    request.ProfileId,
                    normalizedDisplayName,
                    profile.StorageToken,
                    profile.CurrentManagedRelativePath,
                    ownedMedias,
                    collisions);
            }
            catch (Exception exception) when (exception is ArgumentException or ManagedPathPlanningException)
            {

                return OperationResult<ProfileRenameOutcome>.NeedsAttention(
                    OperationErrorCode.ProfilePathReconciliationBlocked,
                    "This Profile's stored folder or file naming needs repair before it can be renamed.");
            }

            var folderAlreadyCurrent = string.Equals(
                profile.CurrentManagedRelativePath, targetFolder, StringComparison.Ordinal);

            await UpdateProfileNameAsync(
                transaction, request.ProfileId, normalizedDisplayName, now, newRowVersion,
                profile.RowVersion, cancellationToken).ConfigureAwait(false);

            if (folderAlreadyCurrent && assetPlans.Count == 0)
            {

                await using (var alignTarget = transaction.CreateCommand(
                    """
                    UPDATE profiles
                    SET target_managed_relative_path = $targetPath
                    WHERE profile_id = $profileId;
                    """))
                {
                    alignTarget.Parameters.AddWithValue("$targetPath", targetFolder);
                    alignTarget.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
                    await alignTarget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await AppendActivityAsync(
                    transaction,
                    ActivityEventType.ProfileRenamed,
                    request.ProfileId,
                    operationId: null,
                    BuildRenamePayload(normalizedDisplayName, targetFolder, plannedMedias: 0),
                    DbTime.Format(_timeProvider.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
                transaction.QueueInvalidation(new CatalogInvalidation(
                    Guid.Empty,
                    [request.ProfileId],
                    CatalogInvalidationDomain.Profile,
                    newRowVersion));
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return OperationResult<ProfileRenameOutcome>.Success(
                    new ProfileRenameOutcome(
                        request.ProfileId, normalizedDisplayName, newRowVersion,
                        targetFolder, ManagedPathState.None, null, 0));
            }

            var operationId = Guid.NewGuid();
            await PersistRenamePlanAsync(
                transaction, request.ProfileId, operationId, targetFolder, assetPlans, now, cancellationToken)
                .ConfigureAwait(false);
            await ReconciliationJobAuthority.EnsureProfileRenameJobAsync(
                transaction,
                operationId,
                request.ProfileId,
                now,
                cancellationToken).ConfigureAwait(false);
            await AppendActivityAsync(
                transaction,
                ActivityEventType.ProfileRenamed,
                request.ProfileId,
                operationId,
                BuildRenamePayload(normalizedDisplayName, targetFolder, assetPlans.Count),
                DbTime.Format(_timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);

            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [request.ProfileId],
                CatalogInvalidationDomain.Profile,
                newRowVersion));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            reconciliation = new ProfileRenameReconciliationRequest(
                request.ProfileId, operationId, targetFolder, assetPlans.Count);
            result = OperationResult<ProfileRenameOutcome>.Accepted(
                new ProfileRenameOutcome(
                    request.ProfileId, normalizedDisplayName, newRowVersion,
                    targetFolder, ManagedPathState.Pending, operationId, assetPlans.Count),
                operationId,
                "The new name is saved. Folder and file names are being brought in line in the background.");
        }

        if (reconciliation is not null)
        {
            JobSignals.Raise();
        }

        return result;
    }

    public async Task<OperationResult<ProfileMetadataOutcome>> UpdateProfileMetadataAsync(
        UpdateProfileMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(UpdateProfileMetadataAsync));
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProfileId == Guid.Empty)
        {
            throw new ArgumentException("Profile identifier cannot be empty.", nameof(request));
        }

        if (!ProfileRules.IsRatingValid(request.Rating))
        {
            return OperationResult<ProfileMetadataOutcome>.Validation(
                OperationErrorCode.ProfileMetadataInvalid,
                $"A rating must be between {ProfileRules.MinimumRating} and {ProfileRules.MaximumRating}, or cleared.");
        }

        if (!ProfileRules.IsOverviewValid(request.Overview))
        {
            return OperationResult<ProfileMetadataOutcome>.Validation(
                OperationErrorCode.ProfileMetadataInvalid,
                $"An overview is limited to {ProfileRules.MaximumOverviewLength:N0} characters.");
        }

        var tagIds = request.TagIds is null
            ? []
            : request.TagIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var profile = await ReadRenameStateAsync(transaction, request.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return OperationResult<ProfileMetadataOutcome>.NotFound(
                OperationErrorCode.ProfileNotFound,
                "That Profile no longer exists.");
        }

        if (profile.IsTrashed)
        {
            return OperationResult<ProfileMetadataOutcome>.Conflict(
                OperationErrorCode.ProfileConflict,
                "That Profile was moved to Trash and cannot be edited.");
        }

        if (profile.Kind != ProfileKind.Normal)
        {
            return OperationResult<ProfileMetadataOutcome>.Validation(
                OperationErrorCode.ProfileNotNormal,
                "An Unknown Profile has no editable metadata until it is resolved.");
        }

        if (profile.RowVersion != request.ExpectedRowVersion)
        {
            return OperationResult<ProfileMetadataOutcome>.Conflict(
                OperationErrorCode.ProfileConflict,
                "That Profile changed somewhere else. Reload it and apply your edits again.");
        }

        if (!ProfileRules.IsPrivateNotesValid(request.Notes))
        {
            // Existing longer notes remain readable and do not block unrelated metadata edits.
            await using var notesRead = transaction.CreateCommand("SELECT notes FROM profiles WHERE profile_id = $profileId;");
            notesRead.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
            var existingNotes = await notesRead.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (!string.Equals(existingNotes, request.Notes, StringComparison.Ordinal))
            {
                return OperationResult<ProfileMetadataOutcome>.Validation(
                    OperationErrorCode.ProfileMetadataInvalid,
                    $"Private notes are limited to {ProfileRules.MaximumPrivateNotesLength:N0} characters.");
            }
        }

        var categoryId = string.IsNullOrWhiteSpace(request.CategoryId) ? null : request.CategoryId.Trim();
        if (categoryId is not null
            && !await RowExistsAsync(transaction, "categories", "category_id", categoryId, cancellationToken).ConfigureAwait(false))
        {
            return OperationResult<ProfileMetadataOutcome>.Validation(
                OperationErrorCode.ProfileCategoryNotFound,
                "That category no longer exists.");
        }

        foreach (var tagId in tagIds)
        {
            if (!await RowExistsAsync(transaction, "tags", "tag_id", tagId, cancellationToken).ConfigureAwait(false))
            {
                return OperationResult<ProfileMetadataOutcome>.Validation(
                    OperationErrorCode.ProfileTagNotFound,
                    "One of those tags no longer exists.");
            }
        }

        var displayName = profile.DisplayName
            ?? throw new CatalogInvariantException("A normal Profile must have a display name.");

        // Read previous category assignment.
        string? previousCategoryId;
        await using (var readCat = transaction.CreateCommand(
            "SELECT category_id FROM profiles WHERE profile_id = $profileId;"))
        {
            readCat.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
            var catResult = await readCat.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            previousCategoryId = catResult is null or DBNull ? null : Convert.ToString(catResult, CultureInfo.InvariantCulture);
        }

        // Read previous tag assignments.
        var previousTagIds = new HashSet<string>(StringComparer.Ordinal);
        await using (var readTags = transaction.CreateCommand(
            "SELECT tag_id FROM profile_tags WHERE profile_id = $profileId;"))
        {
            readTags.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
            await using var tagReader = await readTags.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await tagReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                previousTagIds.Add(tagReader.GetString(0));
            }
        }

        var newRowVersion = await _profileWrites.UpdateProfileMetadataAsync(
            transaction,
            request.ProfileId,
            displayName,
            categoryId,
            request.Rating,
            request.IsFavorite,
            request.Overview,
            request.Notes,
            profile.RowVersion,
            cancellationToken).ConfigureAwait(false);
        var now = DbTime.Format(_timeProvider.GetUtcNow());

        await using (var deleteTags = transaction.CreateCommand(
            "DELETE FROM profile_tags WHERE profile_id = $profileId;"))
        {
            deleteTags.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
            await deleteTags.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var tagId in tagIds)
        {
            await using var insertTag = transaction.CreateCommand(
                """
                INSERT INTO profile_tags(profile_id, tag_id, created_at_ms)
                VALUES ($profileId, $tagId, $now);
                """);
            insertTag.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
            insertTag.Parameters.AddWithValue("$tagId", tagId);
            insertTag.Parameters.AddWithValue("$now", now);
            await insertTag.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AppendActivityAsync(
            transaction,
            ActivityEventType.ProfileMetadataChanged,
            request.ProfileId,
            operationId: null,
            BuildMetadataPayload(categoryId, tagIds.Length, request.Rating, request.IsFavorite),
            DbTime.Format(_timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.ProfileId],
            CatalogInvalidationDomain.Profile,
            newRowVersion));

        // Determine whether taxonomy usage actually changed.
        var categoryChanged = !string.Equals(previousCategoryId, categoryId, StringComparison.Ordinal);
        var newTagIdSet = new HashSet<string>(tagIds, StringComparer.Ordinal);
        var tagsChanged = !previousTagIds.SetEquals(newTagIdSet);
        if (categoryChanged || tagsChanged)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [request.ProfileId],
                CatalogInvalidationDomain.TaxonomyUsage,
                newRowVersion));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return OperationResult<ProfileMetadataOutcome>.Success(
            new ProfileMetadataOutcome(request.ProfileId, newRowVersion));
    }

    public async Task<OperationResult> OpenProfileFolderAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile identifier cannot be empty.", nameof(profileId));
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var profile = await ReadRenameStateAsync(transaction, profileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return OperationResult.NotFound(
                OperationErrorCode.ProfileNotFound,
                "That Profile no longer exists.");
        }

        if (profile.PathState == ManagedPathState.NeedsAttention)
        {
            return OperationResult.NeedsAttention(
                OperationErrorCode.CurrentPathAmbiguous,
                "This Profile's folder location needs attention before it can be opened.",
                profile.ReconciliationOperationId);
        }

        if (string.IsNullOrWhiteSpace(profile.CurrentManagedRelativePath))
        {
            return OperationResult.NeedsAttention(
                OperationErrorCode.CurrentPathMissing,
                "This Profile has no managed folder on disk yet.");
        }

        var opened = await _explorerLocations
            .OpenProfileFolderAsync(profile.CurrentManagedRelativePath, cancellationToken)
            .ConfigureAwait(false);

        return opened.Status switch
        {
            StorageOperationStatus.Success or StorageOperationStatus.AlreadyCompleted =>
                OperationResult.Success(),
            StorageOperationStatus.SourceMissing => OperationResult.NeedsAttention(
                OperationErrorCode.CurrentPathMissing,
                "This Profile's folder is recorded but is not where it should be."),
            StorageOperationStatus.PathOutsideVault or StorageOperationStatus.NeedsAttention =>
                OperationResult.NeedsAttention(
                    OperationErrorCode.CurrentPathAmbiguous,
                    "This Profile's recorded folder could not be trusted, so nothing was opened."),
            StorageOperationStatus.Cancelled => OperationResult.Failed(
                OperationErrorCode.CurrentPathAmbiguous,
                "Opening the Profile folder was cancelled."),
            _ => OperationResult.Failed(
                OperationErrorCode.CurrentPathAmbiguous,
                "Windows Explorer could not open this Profile's folder."),
        };
    }

    public async Task AddAppearsRelationAsync(
        AddProfileMediaRelationRequest request,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(AddAppearsRelationAsync));
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.ProfileId, nameof(request.ProfileId));
        EnsureNonEmpty(request.MediaId, nameof(request.MediaId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        await ValidateActiveProfileExistsAsync(transaction, request.ProfileId, cancellationToken).ConfigureAwait(false);
        await ValidateActiveMediaExistsAsync(transaction, request.MediaId, cancellationToken).ConfigureAwait(false);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using var insert = transaction.CreateCommand(
            """
            INSERT OR IGNORE INTO profile_media(profile_id, media_id, relation_type, provenance_key, created_at_ms)
            VALUES ($profileId, $assetId, 'APPEARS', $provenanceKey, $now);
            """);
        insert.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
        insert.Parameters.AddWithValue("$assetId", DbGuid.Format(request.MediaId));
        insert.Parameters.AddWithValue("$provenanceKey", request.ProvenanceKey is not null ? request.ProvenanceKey : DBNull.Value);
        insert.Parameters.AddWithValue("$now", now);

        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.ProfileId],
            CatalogInvalidationDomain.Profile,
            0));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.MediaId],
            CatalogInvalidationDomain.Media,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAppearsRelationAsync(
        RemoveProfileMediaRelationRequest request,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(RemoveAppearsRelationAsync));
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.ProfileId, nameof(request.ProfileId));
        EnsureNonEmpty(request.MediaId, nameof(request.MediaId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        await using var delete = transaction.CreateCommand(
            """
            DELETE FROM profile_media
            WHERE profile_id = $profileId AND media_id = $assetId AND relation_type = 'APPEARS';
            """);
        delete.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
        delete.Parameters.AddWithValue("$assetId", DbGuid.Format(request.MediaId));
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await ClearAppearanceIfNoRemainingRelationAsync(transaction, request.ProfileId, request.MediaId, cancellationToken).ConfigureAwait(false);

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.ProfileId],
            CatalogInvalidationDomain.Profile,
            0));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.MediaId],
            CatalogInvalidationDomain.Media,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddManualRelationAsync(
        AddProfileMediaRelationRequest request,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(AddManualRelationAsync));
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.ProfileId, nameof(request.ProfileId));
        EnsureNonEmpty(request.MediaId, nameof(request.MediaId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        await ValidateActiveProfileExistsAsync(transaction, request.ProfileId, cancellationToken).ConfigureAwait(false);
        await ValidateActiveMediaExistsAsync(transaction, request.MediaId, cancellationToken).ConfigureAwait(false);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using var insert = transaction.CreateCommand(
            """
            INSERT OR IGNORE INTO profile_media(profile_id, media_id, relation_type, provenance_key, created_at_ms)
            VALUES ($profileId, $assetId, 'MANUAL', $provenanceKey, $now);
            """);
        insert.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
        insert.Parameters.AddWithValue("$assetId", DbGuid.Format(request.MediaId));
        insert.Parameters.AddWithValue("$provenanceKey", request.ProvenanceKey is not null ? request.ProvenanceKey : DBNull.Value);
        insert.Parameters.AddWithValue("$now", now);

        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.ProfileId],
            CatalogInvalidationDomain.Profile,
            0));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.MediaId],
            CatalogInvalidationDomain.Media,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveManualRelationAsync(
        RemoveProfileMediaRelationRequest request,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(RemoveManualRelationAsync));
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.ProfileId, nameof(request.ProfileId));
        EnsureNonEmpty(request.MediaId, nameof(request.MediaId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        await using var delete = transaction.CreateCommand(
            """
            DELETE FROM profile_media
            WHERE profile_id = $profileId AND media_id = $assetId AND relation_type = 'MANUAL';
            """);
        delete.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
        delete.Parameters.AddWithValue("$assetId", DbGuid.Format(request.MediaId));
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await ClearAppearanceIfNoRemainingRelationAsync(transaction, request.ProfileId, request.MediaId, cancellationToken).ConfigureAwait(false);

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.ProfileId],
            CatalogInvalidationDomain.Profile,
            0));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.MediaId],
            CatalogInvalidationDomain.Media,
            0));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> TransferMediaOwnershipAsync(
        TransferMediaOwnershipRequest request,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(TransferMediaOwnershipAsync));
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.MediaId, nameof(request.MediaId));
        EnsureNonEmpty(request.NewOwnerProfileId, nameof(request.NewOwnerProfileId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        await using var assetRead = transaction.CreateCommand(
            """
            SELECT state, trashed_at_ms, row_version
            FROM media
            WHERE media_id = $assetId;
            """);
        assetRead.Parameters.AddWithValue("$assetId", DbGuid.Format(request.MediaId));

        string assetTextState;
        bool isMediaTrashed;
        long currentMediaRowVersion;
        await using (var assetReader = await assetRead.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await assetReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new CatalogInvariantException($"Media {request.MediaId:D} does not exist.");
            }

            assetTextState = assetReader.GetString(0);
            isMediaTrashed = !assetReader.IsDBNull(1);
            currentMediaRowVersion = assetReader.GetInt64(2);
        }

        var state = DbEnum.ParseMediaState(assetTextState);
        if (state != MediaState.Active || isMediaTrashed)
        {
            throw new InvalidOperationException(
                $"Only an ACTIVE, non-trashed Media can transfer ownership; Media {request.MediaId:D} is {state} (trashed={isMediaTrashed}).");
        }

        if (currentMediaRowVersion != request.ExpectedMediaRowVersion)
        {
            throw new CatalogInvariantException(
                $"Media {request.MediaId:D} row version mismatch (expected {request.ExpectedMediaRowVersion}, found {currentMediaRowVersion}).");
        }

        await ValidateActiveProfileExistsAsync(transaction, request.NewOwnerProfileId, cancellationToken).ConfigureAwait(false);

        await using var ownerRead = transaction.CreateCommand(
            """
            SELECT profile_id
            FROM profile_media
            WHERE media_id = $assetId AND relation_type = 'OWNER';
            """);
        ownerRead.Parameters.AddWithValue("$assetId", DbGuid.Format(request.MediaId));

        var currentOwnerResult = await ownerRead.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (currentOwnerResult is null)
        {
            throw new CatalogInvariantException($"ACTIVE Media {request.MediaId:D} must have an existing OWNER.");
        }

        var oldOwnerProfileId = DbGuid.Parse(Convert.ToString(currentOwnerResult, System.Globalization.CultureInfo.InvariantCulture)!);

        if (oldOwnerProfileId == request.NewOwnerProfileId)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return currentMediaRowVersion;
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        long newMediaRowVersion = currentMediaRowVersion + 1;

        await using (var updateOwner = transaction.CreateCommand(
            """
            UPDATE profile_media
            SET profile_id = $newOwnerId,
                created_at_ms = $now
            WHERE media_id = $assetId AND relation_type = 'OWNER';
            """))
        {
            updateOwner.Parameters.AddWithValue("$newOwnerId", DbGuid.Format(request.NewOwnerProfileId));
            updateOwner.Parameters.AddWithValue("$now", now);
            updateOwner.Parameters.AddWithValue("$assetId", DbGuid.Format(request.MediaId));

            if (await updateOwner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new CatalogInvariantException($"Ownership transfer failed for Media {request.MediaId:D}.");
            }
        }

        await ClearAppearanceIfNoRemainingRelationAsync(transaction, oldOwnerProfileId, request.MediaId, cancellationToken).ConfigureAwait(false);

        await using (var updateMedia = transaction.CreateCommand(
            """
            UPDATE media
            SET row_version = $newRowVersion
            WHERE media_id = $assetId AND row_version = $expectedRowVersion;
            """))
        {
            updateMedia.Parameters.AddWithValue("$newRowVersion", newMediaRowVersion);
            updateMedia.Parameters.AddWithValue("$assetId", DbGuid.Format(request.MediaId));
            updateMedia.Parameters.AddWithValue("$expectedRowVersion", request.ExpectedMediaRowVersion);

            if (await updateMedia.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new CatalogInvariantException($"Media {request.MediaId:D} concurrency check failed during ownership transfer.");
            }
        }

        await using var countOwners = transaction.CreateCommand(
            """
            SELECT COUNT(*) FROM profile_media
            WHERE media_id = $assetId AND relation_type = 'OWNER';
            """);
        countOwners.Parameters.AddWithValue("$assetId", DbGuid.Format(request.MediaId));

        if (Convert.ToInt64(await countOwners.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 1)
        {
            throw new CatalogInvariantException($"ACTIVE Media {request.MediaId:D} postcondition failed: must have exactly one OWNER.");
        }

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.NewOwnerProfileId, oldOwnerProfileId],
            CatalogInvalidationDomain.Profile,
            0));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.MediaId],
            CatalogInvalidationDomain.Media,
            newMediaRowVersion));

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return newMediaRowVersion;
    }

    private static async Task ValidateActiveProfileExistsAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT trashed_at_ms
            FROM profiles
            WHERE profile_id = $profileId;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new CatalogInvariantException($"Profile {profileId:D} does not exist.");
        }

        if (!reader.IsDBNull(0))
        {
            throw new InvalidOperationException($"Profile {profileId:D} is trashed.");
        }
    }

    private static async Task ValidateActiveMediaExistsAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT state, trashed_at_ms
            FROM media
            WHERE media_id = $assetId;
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new CatalogInvariantException($"Media {assetId:D} does not exist.");
        }

        var state = DbEnum.ParseMediaState(reader.GetString(0));
        var isTrashed = !reader.IsDBNull(1);

        if (state != MediaState.Active || isTrashed)
        {
            throw new InvalidOperationException(
                $"Media {assetId:D} is not an active Media (state={state}, trashed={isTrashed}).");
        }
    }

    private static async Task ClearAppearanceIfNoRemainingRelationAsync(
        CatalogTransaction transaction,
        Guid profileId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var checkRemaining = transaction.CreateCommand(
            """
            SELECT EXISTS(
                SELECT 1 FROM profile_media
                WHERE profile_id = $profileId AND media_id = $assetId
            );
            """);
        checkRemaining.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        checkRemaining.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var hasRemainingRelation = Convert.ToInt32(
            await checkRemaining.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;

        if (!hasRemainingRelation)
        {
            await using (var clearCoverSource = transaction.CreateCommand(
                """
                UPDATE profiles
                SET cover_media_id = NULL,
                    row_version = row_version + 1
                WHERE profile_id = $profileId AND cover_media_id = $assetId;
                """))
            {
                clearCoverSource.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
                clearCoverSource.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                await clearCoverSource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var clearAppearance = transaction.CreateCommand(
                """
                UPDATE profile_appearance
                SET cover_media_asset_id = CASE
                        WHEN cover_media_asset_id IN (SELECT media_asset_id FROM media_assets WHERE media_id = $assetId)
                        THEN NULL ELSE cover_media_asset_id END,
                    banner_media_asset_id = CASE
                        WHEN banner_media_asset_id IN (SELECT media_asset_id FROM media_assets WHERE media_id = $assetId)
                        THEN NULL ELSE banner_media_asset_id END,
                    row_version = row_version + 1
                WHERE profile_id = $profileId
                  AND (
                    cover_media_asset_id IN (SELECT media_asset_id FROM media_assets WHERE media_id = $assetId)
                    OR banner_media_asset_id IN (SELECT media_asset_id FROM media_assets WHERE media_id = $assetId)
                  );
                """);
            clearAppearance.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
            clearAppearance.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            await clearAppearance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void EnsureNonEmpty(Guid value, string paramName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", paramName);
        }
    }

    private (string TargetFolder, List<OwnedMediaRenamePlan> MediaPlans) PlanRenameTargets(
        Guid profileId,
        string displayName,
        string profileStorageToken,
        string? currentProfileFolder,
        IReadOnlyList<OwnedManagedMedia> ownedMedias,
        RenameCollisionSnapshot collisions)
    {
        var profileToken = new ProfileStorageToken(profileStorageToken);
        var targetProfilePlan = _pathPlanner.AllocateProfilePlan(
            profileId,
            displayName,
            profileToken,
            candidate =>
            {
                var relative = candidate.ProfileFolderRelativePath.Replace('\\', '/');
                if (string.Equals(relative, currentProfileFolder?.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (collisions.ProfileFolders.Contains(relative))
                {
                    return true;
                }

                try
                {
                    return Directory.Exists(_catalog.Paths.ResolveVaultRelativePath(relative));
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    return true;
                }
            });
        var targetFolder = targetProfilePlan.ProfileFolderRelativePath;

        var reservedMediaPaths = new HashSet<string>(collisions.MediaPaths, StringComparer.OrdinalIgnoreCase);
        var assetPlans = new List<OwnedMediaRenamePlan>(ownedMedias.Count);
        foreach (var asset in ownedMedias)
        {
            string targetFilePath;
            string targetFileName;
            if (asset.MediaType == MediaType.Model && asset.DependencyStatus != MediaDependencyStatus.SelfContained)
            {
                var pkgPlan = _pathPlanner.AllocateModelPackagePlan(
                    profileId,
                    displayName,
                    profileToken,
                    asset.MediaId,
                    new MediaStorageToken(asset.StorageToken),
                    asset.CurrentManagedFileName,
                    candidate =>
                    {
                        var primary = candidate.PrimaryManagedRelativePath.Replace('\\', '/');
                        if (string.Equals(primary, asset.CurrentManagedFilePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        if (reservedMediaPaths.Contains(primary))
                        {
                            return true;
                        }

                        try
                        {
                            return Directory.Exists(_catalog.Paths.ResolveVaultRelativePath(
                                candidate.PackageDirectoryRelativePath));
                        }
                        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                        {
                            return true;
                        }
                    });
                targetFilePath = pkgPlan.PrimaryManagedRelativePath;
                targetFileName = pkgPlan.PrimaryFileName;
            }
            else
            {
                var plan = _pathPlanner.AllocateMediaPlan(
                    profileId,
                    displayName,
                    profileToken,
                    asset.MediaId,
                    new MediaStorageToken(asset.StorageToken),
                    asset.MediaType,
                    Path.GetExtension(asset.CurrentManagedFileName),
                    candidate =>
                    {
                        var relative = candidate.ManagedFileRelativePath!.Replace('\\', '/');
                        if (string.Equals(relative, asset.CurrentManagedFilePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        if (reservedMediaPaths.Contains(relative))
                        {
                            return true;
                        }

                        try
                        {
                            return File.Exists(_catalog.Paths.ResolveVaultRelativePath(relative));
                        }
                        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                        {
                            return true;
                        }
                    });
                targetFilePath = plan.ManagedFileRelativePath!;
                targetFileName = plan.ManagedFileName!;
            }

            reservedMediaPaths.Add(targetFilePath.Replace('\\', '/'));

            if (string.Equals(targetFilePath, asset.CurrentManagedFilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            assetPlans.Add(new OwnedMediaRenamePlan(
                asset.MediaId,
                targetFilePath[..targetFilePath.LastIndexOf('/')],
                targetFileName,
                asset.RowVersion));
        }

        return (targetFolder, assetPlans);
    }

    private static async Task<RenameCollisionSnapshot> ReadRenameCollisionSnapshotAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var profileFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var profiles = transaction.CreateCommand(
            """
            SELECT current_managed_relative_path
            FROM profiles
            WHERE profile_id <> $profileId
              AND current_managed_relative_path IS NOT NULL
            UNION
            SELECT target_managed_relative_path
            FROM profiles
            WHERE profile_id <> $profileId
              AND target_managed_relative_path IS NOT NULL;
            """))
        {
            profiles.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            await using var reader = await profiles.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                profileFolders.Add(reader.GetString(0).Replace('\\', '/'));
            }
        }

        var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var media = transaction.CreateCommand(
            """
            SELECT a.current_managed_relative_path || '/' || a.current_managed_file_name
            FROM media a
            WHERE a.current_managed_relative_path IS NOT NULL
              AND a.current_managed_file_name IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM profile_media owner
                  WHERE owner.media_id = a.media_id
                    AND owner.profile_id = $profileId
                    AND owner.relation_type = 'OWNER'
              )
            UNION
            SELECT a.target_managed_relative_path || '/' || a.target_managed_file_name
            FROM media a
            WHERE a.target_managed_relative_path IS NOT NULL
              AND a.target_managed_file_name IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM profile_media owner
                  WHERE owner.media_id = a.media_id
                    AND owner.profile_id = $profileId
                    AND owner.relation_type = 'OWNER'
              );
            """))
        {
            media.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            await using var reader = await media.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                assetPaths.Add(reader.GetString(0).Replace('\\', '/'));
            }
        }

        return new RenameCollisionSnapshot(profileFolders, assetPaths);
    }

    private static async Task<ProfileRenameState?> ReadRenameStateAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT kind, display_name, trashed_at_ms, row_version, profile_storage_token,
                   current_managed_relative_path, target_managed_relative_path,
                   path_state, reconciliation_operation_id
            FROM profiles
            WHERE profile_id = $profileId;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProfileRenameState(
            profileId,
            DbEnum.ParseProfileKind(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            !reader.IsDBNull(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            DbEnum.ParseManagedPathState(reader.GetString(7)),
            reader.IsDBNull(8) ? null : DbGuid.Parse(reader.GetString(8)));
    }

    private static async Task UpdateProfileNameAsync(
        CatalogTransaction transaction,
        Guid profileId,
        string displayName,
        long nowMilliseconds,
        long newRowVersion,
        long expectedRowVersion,
        CancellationToken cancellationToken)
    {
        await using var update = transaction.CreateCommand(
            """
            UPDATE profiles
            SET display_name = $displayName,
                updated_at_ms = $now,
                row_version = $newRowVersion
            WHERE profile_id = $profileId AND row_version = $expectedRowVersion;
            """);
        update.Parameters.AddWithValue("$displayName", displayName);
        update.Parameters.AddWithValue("$now", nowMilliseconds);
        update.Parameters.AddWithValue("$newRowVersion", newRowVersion);
        update.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        update.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);

        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CatalogInvariantException(
                $"Profile {profileId:D} update lost concurrency validation.");
        }
    }

    private static async Task<IReadOnlyList<OwnedManagedMedia>> ReadOwnedManagedMediasAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT a.media_id, a.media_storage_token, a.media_type,
                   a.current_managed_relative_path, a.current_managed_file_name, a.row_version,
                   a.path_state, a.dependency_status
            FROM profile_media pa
            JOIN media a ON a.media_id = pa.media_id
            WHERE pa.profile_id = $profileId
              AND pa.relation_type = 'OWNER'
              AND a.state = 'ACTIVE'
              AND a.trashed_at_ms IS NULL
              AND a.media_storage_token IS NOT NULL
              AND a.current_managed_file_name IS NOT NULL
              AND a.current_managed_relative_path IS NOT NULL
            ORDER BY a.media_id;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        var media = new List<OwnedManagedMedia>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            media.Add(new OwnedManagedMedia(
                DbGuid.Parse(reader.GetString(0)),
                reader.GetString(1),
                DbEnum.ParseMediaType(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                DbEnum.ParseManagedPathState(reader.GetString(6)),
                reader.IsDBNull(7) ? MediaDependencyStatus.SelfContained : DbEnum.ParseMediaDependencyStatus(reader.GetString(7))));
        }

        return media;
    }

    private static async Task PersistRenamePlanAsync(
        CatalogTransaction transaction,
        Guid profileId,
        Guid operationId,
        string targetProfileFolder,
        IReadOnlyList<OwnedMediaRenamePlan> assetPlans,
        long nowMilliseconds,
        CancellationToken cancellationToken)
    {
        await using (var updateProfile = transaction.CreateCommand(
            """
            UPDATE profiles
            SET target_managed_relative_path = $targetPath,
                path_state = 'PENDING',
                reconciliation_operation_id = $operationId
            WHERE profile_id = $profileId;
            """))
        {
            updateProfile.Parameters.AddWithValue("$targetPath", targetProfileFolder);
            updateProfile.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
            updateProfile.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            await updateProfile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var plan in assetPlans)
        {
            await using var updateMedia = transaction.CreateCommand(
                """
                UPDATE media
                SET target_managed_relative_path = $targetPath,
                    target_managed_file_name = $targetFileName,
                    path_state = 'PENDING',
                    reconciliation_operation_id = $operationId,
                    row_version = row_version + 1
                WHERE media_id = $assetId AND row_version = $expectedRowVersion;
                """);
            updateMedia.Parameters.AddWithValue("$targetPath", plan.TargetManagedRelativePath);
            updateMedia.Parameters.AddWithValue("$targetFileName", plan.TargetManagedFileName);
            updateMedia.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
            updateMedia.Parameters.AddWithValue("$assetId", DbGuid.Format(plan.MediaId));
            updateMedia.Parameters.AddWithValue("$expectedRowVersion", plan.ExpectedRowVersion);

            if (await updateMedia.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new CatalogInvariantException(
                    $"Media {plan.MediaId:D} changed while the Profile rename plan was being written.");
            }
        }

        await using var insertOperation = transaction.CreateCommand(
            """
            INSERT INTO storage_operations(
                operation_id, kind, entity_type, entity_id, state, checkpoint_json,
                created_at_ms, updated_at_ms)
            VALUES (
                $operationId, 'PROFILE_RENAME', 'PROFILE', $entityId, 'PENDING', $checkpoint,
                $now, $now);
            """);
        insertOperation.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
        insertOperation.Parameters.AddWithValue("$entityId", DbGuid.Format(profileId));
        insertOperation.Parameters.AddWithValue("$checkpoint", BuildRenameCheckpoint(targetProfileFolder, assetPlans.Count));
        insertOperation.Parameters.AddWithValue("$now", nowMilliseconds);
        await insertOperation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AppendActivityAsync(
        CatalogTransaction transaction,
        string eventType,
        Guid profileId,
        Guid? operationId,
        string payloadJson,
        long occurredAtMs,
        CancellationToken cancellationToken)
    {
        await using var insert = transaction.CreateCommand(
            """
            INSERT INTO activity_log(
                activity_id, event_type, profile_id, operation_id, payload_json, occurred_at_ms)
            VALUES ($activityId, $eventType, $profileId, $operationId, $payloadJson, $occurredAtMs);
            """);
        insert.Parameters.AddWithValue("$activityId", DbGuid.Format(Guid.NewGuid()));
        insert.Parameters.AddWithValue("$eventType", eventType);
        insert.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        insert.Parameters.AddWithValue(
            "$operationId",
            operationId is null ? DBNull.Value : DbGuid.Format(operationId.Value));
        insert.Parameters.AddWithValue("$payloadJson", payloadJson);
        insert.Parameters.AddWithValue("$occurredAtMs", occurredAtMs);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> RowExistsAsync(
        CatalogTransaction transaction,
        string table,
        string keyColumn,
        string keyValue,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            $"SELECT EXISTS(SELECT 1 FROM {table} WHERE {keyColumn} = $key);");
        command.Parameters.AddWithValue("$key", keyValue);
        var found = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(found, CultureInfo.InvariantCulture) == 1;
    }

    private static string BuildRenameCheckpoint(string targetProfileFolder, int plannedMedias)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("targetProfileFolder", targetProfileFolder);
            writer.WriteNumber("plannedMediaCount", plannedMedias);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string BuildRenamePayload(string displayName, string? targetFolder, int plannedMedias)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("displayName", displayName);
            if (targetFolder is not null)
            {
                writer.WriteString("targetProfileFolder", targetFolder);
            }

            writer.WriteNumber("plannedMediaCount", plannedMedias);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string BuildMetadataPayload(string? categoryId, int tagCount, int? rating, bool isFavorite)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (categoryId is null)
            {
                writer.WriteNull("categoryId");
            }
            else
            {
                writer.WriteString("categoryId", categoryId);
            }

            writer.WriteNumber("tagCount", tagCount);
            if (rating is null)
            {
                writer.WriteNull("rating");
            }
            else
            {
                writer.WriteNumber("rating", rating.Value);
            }

            writer.WriteBoolean("isFavorite", isFavorite);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed record ProfileRenameState(
        Guid ProfileId,
        ProfileKind Kind,
        string? DisplayName,
        bool IsTrashed,
        long RowVersion,
        string? StorageToken,
        string? CurrentManagedRelativePath,
        string? TargetManagedRelativePath,
        ManagedPathState PathState,
        Guid? ReconciliationOperationId);

    private sealed record OwnedManagedMedia(
        Guid MediaId,
        string StorageToken,
        MediaType MediaType,
        string CurrentManagedRelativePath,
        string CurrentManagedFileName,
        long RowVersion,
        ManagedPathState PathState,
        MediaDependencyStatus DependencyStatus = MediaDependencyStatus.SelfContained)
    {
        public string CurrentManagedFilePath =>
            $"{CurrentManagedRelativePath}/{CurrentManagedFileName}";
    }

    private sealed record RenameCollisionSnapshot(
        HashSet<string> ProfileFolders,
        HashSet<string> MediaPaths);

    private sealed record OwnedMediaRenamePlan(
        Guid MediaId,
        string TargetManagedRelativePath,
        string TargetManagedFileName,
        long ExpectedRowVersion);
}

public sealed record ProfileRenameReconciliationRequest(
    Guid ProfileId,
    Guid OperationId,
    string TargetProfileFolderRelativePath,
    int PlannedMediaCount);

public delegate Task ProfileRenameReconciliationEnqueue(
    ProfileRenameReconciliationRequest request,
    CancellationToken cancellationToken);

public sealed record CreateNormalProfileCommand(
    Guid ProfileId,
    string DisplayName,
    Guid? IdentityId = null,
    string Visibility = "DRAFT");

public sealed record CreateUnknownProfileCommand(
    Guid ProfileId);

public sealed record RenameProfileRequest(
    Guid ProfileId,
    long ExpectedRowVersion,
    string DisplayName);

public sealed record ProfileRenameOutcome(
    Guid ProfileId,
    string DisplayName,
    long RowVersion,
    string? ProfileFolderRelativePath,
    ManagedPathState PathState,
    Guid? ReconciliationOperationId,
    int PlannedMediaCount);

public sealed record UpdateProfileMetadataRequest(
    Guid ProfileId,
    long ExpectedRowVersion,
    string? CategoryId = null,
    IReadOnlyList<string>? TagIds = null,
    int? Rating = null,
    bool IsFavorite = false,
    string? Overview = null,
    string? Notes = null);
public sealed record ProfileMetadataOutcome(Guid ProfileId, long RowVersion);

public sealed record AddProfileMediaRelationRequest(
    Guid ProfileId,
    Guid MediaId,
    string? ProvenanceKey = null);

public sealed record RemoveProfileMediaRelationRequest(
    Guid ProfileId,
    Guid MediaId);

public sealed record TransferMediaOwnershipRequest(
    Guid MediaId,
    Guid NewOwnerProfileId,
    long ExpectedMediaRowVersion);
