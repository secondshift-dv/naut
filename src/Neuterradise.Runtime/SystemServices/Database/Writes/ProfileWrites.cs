using Microsoft.Data.Sqlite;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Profiles;

namespace Neuterradise.App.SystemServices.Database.Writes;

public sealed class ProfileWrites
{
    private const int _maximumTokenCandidates = 30;

    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public ProfileWrites(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<NormalProfileCreationResult> CreateNormalProfileAsync(
        Guid profileId,
        Guid identityId,
        string displayName,
        string visibility = "DRAFT",
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));
        EnsureNonEmpty(identityId, nameof(identityId));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var normalizedDisplayName = displayName.Trim();

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var existing = await ReadProfileAsync(transaction, profileId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Kind != ProfileKind.Normal
                || !string.Equals(existing.DisplayName, normalizedDisplayName, StringComparison.Ordinal)
                || existing.UnknownSequence is not null
                || !await HasExactlyIdentityAsync(transaction, profileId, identityId, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new CatalogInvariantException(
                    $"Profile {profileId:D} already exists with different NORMAL Profile authority.");
            }

            if (!string.Equals(existing.Visibility, visibility, StringComparison.Ordinal))
            {
                await using var update = transaction.CreateCommand(
                    """
                    UPDATE profiles SET visibility = $visibility,
                        updated_at_ms = $now, row_version = row_version + 1
                    WHERE profile_id = $profileId AND visibility <> $visibility;
                    """);
                update.Parameters.AddWithValue("$visibility", visibility);
                update.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
                update.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new NormalProfileCreationResult(profileId, identityId);
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using (var insertProfile = transaction.CreateCommand(
            """
            INSERT INTO profiles(
                profile_id, kind, display_name, unknown_sequence, visibility,
                created_at_ms, updated_at_ms)
            VALUES ($profileId, $kind, $displayName, NULL, $visibility, $now, $now);
            """))
        {
            insertProfile.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            insertProfile.Parameters.AddWithValue("$kind", DbEnum.Format(ProfileKind.Normal));
            insertProfile.Parameters.AddWithValue("$displayName", normalizedDisplayName);
            insertProfile.Parameters.AddWithValue("$visibility", visibility);
            insertProfile.Parameters.AddWithValue("$now", now);
            await insertProfile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insertIdentity = transaction.CreateCommand(
            """
            INSERT INTO identities(identity_id, profile_id, is_active, created_at_ms)
            VALUES ($identityId, $profileId, 1, $now);
            """))
        {
            insertIdentity.Parameters.AddWithValue("$identityId", DbGuid.Format(identityId));
            insertIdentity.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            insertIdentity.Parameters.AddWithValue("$now", now);
            await insertIdentity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!await HasExactlyIdentityAsync(transaction, profileId, identityId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new CatalogInvariantException(
                "A NORMAL Profile must commit with exactly one active Identity.");
        }

        await using (var insertAppearance = transaction.CreateCommand(
            "INSERT INTO profile_appearance(profile_id, schema_version, layout_preset_id, overrides_json, updated_at_ms, row_version) " +
            "VALUES ($profileId, $schemaVersion, $layout, $overrides, $now, 1);"))
        {
            insertAppearance.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            insertAppearance.Parameters.AddWithValue("$schemaVersion", ProfileAppearanceOverrides.SchemaVersion);
            insertAppearance.Parameters.AddWithValue("$layout", ProfileLayoutResolver.FallbackPresetId);
            insertAppearance.Parameters.AddWithValue("$overrides", ProfileAppearanceOverrides.Default.ToJson());
            insertAppearance.Parameters.AddWithValue("$now", now);
            await insertAppearance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new NormalProfileCreationResult(profileId, identityId);
    }

    public async Task<UnknownProfileCreationResult> CreateUnknownProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var existing = await ReadProfileAsync(transaction, profileId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Kind != ProfileKind.Unknown
                || existing.DisplayName is not null
                || existing.UnknownSequence is null
                || await CountActiveIdentitiesAsync(transaction, profileId, cancellationToken)
                    .ConfigureAwait(false) != 0)
            {
                throw new CatalogInvariantException(
                    $"Profile {profileId:D} already exists with different UNKNOWN Profile authority.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new UnknownProfileCreationResult(profileId, existing.UnknownSequence.Value);
        }

        await using (var ensureSequence = transaction.CreateCommand(
            """
            INSERT OR IGNORE INTO sequences(name, next_value)
            SELECT
                'unknown_profile',
                MAX(1, COALESCE(MAX(unknown_sequence), 0) + 1)
            FROM profiles
            WHERE kind = 'UNKNOWN';
            """))
        {
            await ensureSequence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long sequence;
        await using (var readSequence = transaction.CreateCommand(
            "SELECT next_value FROM sequences WHERE name = 'unknown_profile';"))
        {
            var persistedSequence = await readSequence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (persistedSequence is null or DBNull)
            {
                throw new CatalogInvariantException("The unknown_profile sequence authority is missing.");
            }

            sequence = Convert.ToInt64(persistedSequence, System.Globalization.CultureInfo.InvariantCulture);
            if (sequence <= 0)
            {
                throw new CatalogInvariantException("The unknown_profile sequence authority is invalid.");
            }
        }

        await using (var advanceSequence = transaction.CreateCommand(
            """
            UPDATE sequences
            SET next_value = next_value + 1
            WHERE name = 'unknown_profile' AND next_value = $expected;
            """))
        {
            advanceSequence.Parameters.AddWithValue("$expected", sequence);
            if (await advanceSequence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new CatalogInvariantException("UnknownSequence allocation lost serialization.");
            }
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using (var insertProfile = transaction.CreateCommand(
            """
            INSERT INTO profiles(
                profile_id, kind, display_name, unknown_sequence,
                created_at_ms, updated_at_ms)
            VALUES ($profileId, $kind, NULL, $unknownSequence, $now, $now);
            """))
        {
            insertProfile.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            insertProfile.Parameters.AddWithValue("$kind", DbEnum.Format(ProfileKind.Unknown));
            insertProfile.Parameters.AddWithValue("$unknownSequence", sequence);
            insertProfile.Parameters.AddWithValue("$now", now);
            await insertProfile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await CountActiveIdentitiesAsync(transaction, profileId, cancellationToken)
                .ConfigureAwait(false) != 0)
        {
            throw new CatalogInvariantException(
                "An UNKNOWN Profile must commit without an active Identity.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new UnknownProfileCreationResult(profileId, sequence);
    }

    public async Task<string> AssignStorageTokenAsync(
        Guid profileId,
        Func<int, string> candidateProvider,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));
        ArgumentNullException.ThrowIfNull(candidateProvider);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var tokenState = await ReadStorageTokenStateAsync(transaction, profileId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CatalogInvariantException($"Profile {profileId:D} does not exist.");
        var existing = tokenState.Token;
        var currentRowVersion = tokenState.RowVersion;
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        for (var attempt = 0; attempt < _maximumTokenCandidates; attempt++)
        {
            var candidate = candidateProvider(attempt);
            ValidateTokenCandidate(candidate);

            if (await TokenExistsAsync(transaction, candidate, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await using var update = transaction.CreateCommand(
                """
                UPDATE profiles
                SET profile_storage_token = $token,
                    row_version = row_version + 1
                WHERE profile_id = $profileId
                  AND profile_storage_token IS NULL
                  AND row_version = $expectedRowVersion;
                """);
            update.Parameters.AddWithValue("$token", candidate);
            update.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            update.Parameters.AddWithValue("$expectedRowVersion", currentRowVersion);

            try
            {
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new CatalogInvariantException(
                        $"Profile {profileId:D} does not exist or already changed during token assignment.");
                }
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                continue;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return candidate;
        }

        throw new CatalogInvariantException(
            $"No unique Profile storage token candidate remained for {profileId:D}.");
    }

    public async Task<long> UpdateProfileMetadataAsync(
        Guid profileId, string displayName, string? categoryId, int? rating, bool isFavorite, string? overview, string? notes, long expectedRowVersion, CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        var rowVersion = await UpdateProfileMetadataAsync(
            transaction,
            profileId,
            displayName,
            categoryId,
            rating,
            isFavorite,
            overview,
            notes,
            expectedRowVersion,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rowVersion;
    }

    internal async Task<long> UpdateProfileMetadataAsync(
        CatalogTransaction transaction,
        Guid profileId,
        string displayName,
        string? categoryId,
        int? rating,
        bool isFavorite,
        string? overview,
        string? notes,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        EnsureNonEmpty(profileId, nameof(profileId));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using var command = transaction.CreateCommand("""
            UPDATE profiles SET display_name = $displayName, category_id = $categoryId, rating = $rating, is_favorite = $isFavorite, overview = $overview, notes = $notes, updated_at_ms = $now, row_version = row_version + 1 WHERE profile_id = $profileId AND row_version = $expectedRowVersion;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$displayName", displayName.Trim());
        command.Parameters.AddWithValue("$categoryId", (object?)categoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$rating", (object?)rating ?? DBNull.Value);
        command.Parameters.AddWithValue("$isFavorite", isFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$overview", (object?)overview ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            await CheckProfileExistenceAndThrowConflictAsync(
                transaction,
                profileId,
                expectedRowVersion,
                cancellationToken).ConfigureAwait(false);
        }

        return expectedRowVersion + 1;
    }

    public async Task<long> SetCategoryAndTagsAsync(
        Guid profileId,
        string? categoryId,
        IReadOnlyList<string> tagIds,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));
        ArgumentNullException.ThrowIfNull(tagIds);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await using var command = transaction.CreateCommand(
            """
            UPDATE profiles
            SET category_id = $categoryId,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE profile_id = $profileId AND row_version = $expectedRowVersion;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$categoryId", (object?)categoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            await CheckProfileExistenceAndThrowConflictAsync(transaction, profileId, expectedRowVersion, cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteTags = transaction.CreateCommand(
            "DELETE FROM profile_tags WHERE profile_id = $profileId;"))
        {
            deleteTags.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            await deleteTags.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var tagId in tagIds.Distinct())
        {
            if (string.IsNullOrWhiteSpace(tagId)) continue;
            await using var insertTag = transaction.CreateCommand(
                """
                INSERT INTO profile_tags(profile_id, tag_id, created_at_ms)
                VALUES ($profileId, $tagId, $now);
                """);
            insertTag.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            insertTag.Parameters.AddWithValue("$tagId", tagId.Trim());
            insertTag.Parameters.AddWithValue("$now", now);
            await insertTag.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expectedRowVersion + 1;
    }

    public async Task<long> UpdateAppearanceAsync(
        Guid profileId,
        string? layoutPresetId,
        string overridesJson,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));
        ArgumentException.ThrowIfNullOrWhiteSpace(overridesJson);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var now = DbTime.Format(_timeProvider.GetUtcNow());

        await using (var checkProfile = transaction.CreateCommand("SELECT 1 FROM profiles WHERE profile_id = $profileId;"))
        {
            checkProfile.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            var exists = await checkProfile.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (exists is null or DBNull)
            {
                throw new CatalogInvariantException($"Profile {profileId:D} does not exist.");
            }
        }

        await using (var upsert = transaction.CreateCommand(
            """
            INSERT INTO profile_appearance(profile_id, schema_version, layout_preset_id, overrides_json, updated_at_ms, row_version)
            VALUES ($profileId, 1, $presetId, $overrides, $now, 1)
            ON CONFLICT(profile_id) DO UPDATE SET
                layout_preset_id = excluded.layout_preset_id,
                overrides_json = excluded.overrides_json,
                updated_at_ms = excluded.updated_at_ms,
                row_version = profile_appearance.row_version + 1
            WHERE profile_appearance.row_version = $expectedRowVersion;
            """))
        {
            upsert.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            upsert.Parameters.AddWithValue("$presetId", (object?)layoutPresetId ?? ProfileLayoutResolver.FallbackPresetId);
            upsert.Parameters.AddWithValue("$overrides", overridesJson.Trim());
            upsert.Parameters.AddWithValue("$now", now);
            upsert.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);

            var affected = await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                throw new CatalogConcurrencyConflictException(
                    $"ProfileAppearance for {profileId:D} had a concurrency conflict. Expected row_version {expectedRowVersion}.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expectedRowVersion + 1;
    }

    public async Task<PersistedProfileRenamePlan?> ReadProfileRenamePlanAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT kind, profile_storage_token,
                   current_managed_relative_path, target_managed_relative_path,
                   path_state, reconciliation_operation_id, trashed_at_ms
            FROM profiles
            WHERE profile_id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PersistedProfileRenamePlan(
            profileId,
            DbEnum.ParseProfileKind(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DbEnum.ParseManagedPathState(reader.GetString(4)),
            reader.IsDBNull(5) ? null : DbGuid.Parse(reader.GetString(5)),
            IsTrashed: !reader.IsDBNull(6));
    }

    public async Task<bool> IsProfileFolderClaimedByAnotherProfileAsync(
        Guid profileId,
        string folderRelativePath,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));
        ArgumentException.ThrowIfNullOrWhiteSpace(folderRelativePath);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM profiles
                WHERE profile_id <> $profileId
                  AND (current_managed_relative_path = $folder
                       OR target_managed_relative_path = $folder));
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$folder", folderRelativePath);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public async Task AdoptProfileCurrentFolderAsync(
        Guid profileId,
        Guid operationId,
        string currentFolderRelativePath,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));
        EnsureNonEmpty(operationId, nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFolderRelativePath);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using (var update = transaction.CreateCommand(
            """
            UPDATE profiles
            SET current_managed_relative_path = $currentFolder
            WHERE profile_id = $profileId
              AND reconciliation_operation_id = $operationId
              AND path_state = 'PENDING'
              AND current_managed_relative_path IS NULL;
            """))
        {
            update.Parameters.AddWithValue("$currentFolder", currentFolderRelativePath);
            update.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            update.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CheckpointProfileFolderPlacementAsync(
        Guid profileId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));
        EnsureNonEmpty(operationId, nameof(operationId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var folders = await ReadReconciliationFoldersAsync(
            transaction, profileId, operationId, cancellationToken).ConfigureAwait(false);

        if (string.Equals(folders.CurrentFolder, folders.TargetFolder, StringComparison.Ordinal))
        {

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using (var rebase = transaction.CreateCommand(
            """
            UPDATE media
            SET current_managed_relative_path =
                    $targetFolder || substr(current_managed_relative_path, length($currentFolder) + 1),
                row_version = row_version + 1
            WHERE current_managed_relative_path IS NOT NULL
              AND substr(current_managed_relative_path, 1, length($currentFolder) + 1)
                  = $currentFolderPrefix;
            """))
        {
            rebase.Parameters.AddWithValue("$targetFolder", folders.TargetFolder);
            rebase.Parameters.AddWithValue("$currentFolder", folders.CurrentFolder);
            rebase.Parameters.AddWithValue("$currentFolderPrefix", folders.CurrentFolder + "/");
            await rebase.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var advance = transaction.CreateCommand(
            """
            UPDATE profiles
            SET current_managed_relative_path = target_managed_relative_path
            WHERE profile_id = $profileId
              AND reconciliation_operation_id = $operationId
              AND path_state = 'PENDING';
            """))
        {
            advance.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            advance.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
            if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new CatalogInvariantException(
                    $"Profile {profileId:D} lost its persisted rename authority before the folder checkpoint.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PersistedRenameMediaPlan>> ReadPendingRenameMediaPlansAsync(
        Guid operationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(operationId, nameof(operationId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT media_id, sha256, byte_length,
                   current_managed_relative_path, current_managed_file_name,
                   target_managed_relative_path, target_managed_file_name
            FROM media
            WHERE reconciliation_operation_id = $operationId
              AND path_state = 'PENDING'
            ORDER BY media_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
        command.Parameters.AddWithValue("$limit", limit);

        var plans = new List<PersistedRenameMediaPlan>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            plans.Add(new PersistedRenameMediaPlan(
                DbGuid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return plans;
    }

    public async Task<bool> CompleteProfileRenameAsync(
        Guid profileId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));
        EnsureNonEmpty(operationId, nameof(operationId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        await using (var outstanding = transaction.CreateCommand(
            """
            SELECT COUNT(*)
            FROM media
            WHERE reconciliation_operation_id = $operationId
              AND path_state IN ('PENDING','NEEDS_ATTENTION');
            """))
        {
            outstanding.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
            if (Convert.ToInt32(
                    await outstanding.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        int converged;
        await using (var advance = transaction.CreateCommand(
            """
            UPDATE profiles
            SET path_state = 'NONE',
                reconciliation_operation_id = NULL
            WHERE profile_id = $profileId
              AND reconciliation_operation_id = $operationId
              AND current_managed_relative_path IS NOT NULL
              AND current_managed_relative_path = target_managed_relative_path;
            """))
        {
            advance.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            advance.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
            converged = await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (converged != 1)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await CompleteStorageOperationAsync(
                transaction,
                operationId,
                DbTime.Format(_timeProvider.GetUtcNow()),
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task MarkProfileRenameNeedsAttentionAsync(
        Guid profileId,
        Guid operationId,
        string safeErrorDetail,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));
        EnsureNonEmpty(operationId, nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(safeErrorDetail);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        var now = DbTime.Format(_timeProvider.GetUtcNow());

        await using (var update = transaction.CreateCommand(
            """
            UPDATE profiles
            SET path_state = 'NEEDS_ATTENTION'
            WHERE profile_id = $profileId
              AND reconciliation_operation_id = $operationId;
            """))
        {
            update.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            update.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await MarkStorageOperationNeedsAttentionAsync(
            transaction, operationId, safeErrorDetail, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ReconciliationFolders> ReadReconciliationFoldersAsync(
        CatalogTransaction transaction,
        Guid profileId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var read = transaction.CreateCommand(
            """
            SELECT current_managed_relative_path, target_managed_relative_path,
                   path_state, reconciliation_operation_id
            FROM profiles
            WHERE profile_id = $profileId;
            """);
        read.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new CatalogInvariantException($"Profile {profileId:D} no longer exists.");
        }

        var currentFolder = reader.IsDBNull(0) ? null : reader.GetString(0);
        var targetFolder = reader.IsDBNull(1) ? null : reader.GetString(1);
        var pathState = DbEnum.ParseManagedPathState(reader.GetString(2));
        var persistedOperationId = reader.IsDBNull(3) ? (Guid?)null : DbGuid.Parse(reader.GetString(3));

        if (pathState != ManagedPathState.Pending || persistedOperationId != operationId)
        {
            throw new CatalogInvariantException(
                $"Profile {profileId:D} is not reconciling under operation {operationId:D}.");
        }

        if (currentFolder is null || targetFolder is null)
        {
            throw new CatalogInvariantException(
                $"Profile {profileId:D} has an incomplete persisted rename plan.");
        }

        return new ReconciliationFolders(currentFolder, targetFolder);
    }

    private static async Task CompleteStorageOperationAsync(
        CatalogTransaction transaction,
        Guid operationId,
        long nowMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var complete = transaction.CreateCommand(
            """
            UPDATE storage_operations
            SET state = 'COMPLETED',
                completed_at_ms = $now,
                updated_at_ms = $now,
                error_code = NULL,
                error_detail_safe = NULL,
                row_version = row_version + 1
            WHERE operation_id = $operationId;
            """);
        complete.Parameters.AddWithValue("$now", nowMilliseconds);
        complete.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
        await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkStorageOperationNeedsAttentionAsync(
        CatalogTransaction transaction,
        Guid operationId,
        string safeErrorDetail,
        long nowMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var update = transaction.CreateCommand(
            """
            UPDATE storage_operations
            SET state = 'NEEDS_ATTENTION',
                error_code = 'RECONCILIATION_AMBIGUOUS',
                error_detail_safe = $detail,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE operation_id = $operationId;
            """);
        update.Parameters.AddWithValue("$detail", safeErrorDetail);
        update.Parameters.AddWithValue("$now", nowMilliseconds);
        update.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record ReconciliationFolders(string CurrentFolder, string TargetFolder);

    private static async Task CheckProfileExistenceAndThrowConflictAsync(
        CatalogTransaction transaction,
        Guid profileId,
        long expectedRowVersion,
        CancellationToken cancellationToken)
    {
        await using var check = transaction.CreateCommand("SELECT row_version FROM profiles WHERE profile_id = $profileId;");
        check.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        var currentVersion = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (currentVersion is null or DBNull)
        {
            throw new CatalogInvariantException($"Profile {profileId:D} does not exist.");
        }

        throw new CatalogConcurrencyConflictException(
            $"Profile {profileId:D} concurrency conflict: expected row_version {expectedRowVersion}, but found {currentVersion}.");
    }

    private static async Task<ProfileRow?> ReadProfileAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT kind, display_name, unknown_sequence, visibility
            FROM profiles
            WHERE profile_id = $profileId;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProfileRow(
            DbEnum.ParseProfileKind(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.GetString(3));
    }

    private static async Task<bool> HasExactlyIdentityAsync(
        CatalogTransaction transaction,
        Guid profileId,
        Guid identityId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN identity_id = $identityId THEN 1 ELSE 0 END), 0)
            FROM identities
            WHERE profile_id = $profileId AND is_active = 1;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$identityId", DbGuid.Format(identityId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return reader.GetInt64(0) == 1 && reader.GetInt64(1) == 1;
    }

    private static async Task<long> CountActiveIdentitiesAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            "SELECT COUNT(*) FROM identities WHERE profile_id = $profileId AND is_active = 1;");
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }


    private static async Task<ProfileTokenState?> ReadStorageTokenStateAsync(
        CatalogTransaction transaction, Guid profileId, CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            "SELECT profile_storage_token, row_version FROM profiles WHERE profile_id = $profileId;");
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ProfileTokenState(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    private static async Task<string?> ReadStorageTokenAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            "SELECT profile_storage_token FROM profiles WHERE profile_id = $profileId;");
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (value is null)
        {
            throw new CatalogInvariantException($"Profile {profileId:D} does not exist.");
        }

        return value is DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> TokenExistsAsync(
        CatalogTransaction transaction,
        string candidate,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            "SELECT EXISTS(SELECT 1 FROM profiles WHERE profile_storage_token = $token);");
        command.Parameters.AddWithValue("$token", candidate);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static void ValidateTokenCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length < 3)
        {
            throw new ArgumentException("A persisted storage token candidate must contain at least three characters.");
        }
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }

    private sealed record ProfileRow(ProfileKind Kind, string? DisplayName, long? UnknownSequence, string Visibility = "PUBLISHED");
}

public sealed record NormalProfileCreationResult(Guid ProfileId, Guid IdentityId);

public sealed record PersistedProfileRenamePlan(
    Guid ProfileId,
    ProfileKind Kind,
    string? StorageToken,
    string? CurrentManagedRelativePath,
    string? TargetManagedRelativePath,
    ManagedPathState PathState,
    Guid? ReconciliationOperationId,
    bool IsTrashed);

public sealed record PersistedRenameMediaPlan(
    Guid MediaId,
    string? ExpectedSha256,
    long? ExpectedByteLength,
    string? CurrentManagedRelativePath,
    string? CurrentManagedFileName,
    string? TargetManagedRelativePath,
    string? TargetManagedFileName);

public sealed record UnknownProfileCreationResult(Guid ProfileId, long UnknownSequence);
