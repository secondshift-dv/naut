using System.IO;
using Microsoft.Data.Sqlite;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;

namespace Neuterradise.App.SystemServices.Storage;

internal sealed class ManagedMediaNamingMigration(CatalogDb catalog)
{
    private const string CompletionKey = "storage.managed_media_names.v1";
    private const string OperationKind = "MEDIA_NAMING_MIGRATION";
    private readonly ManagedPathPlanner _planner = new(catalog.Paths.Root);
    private readonly ManagedFileVerifier _verifier = new();

    // Startup owns the Vault lock and has not activated the scheduler or exposed a writable session.
    // Resume even Candidate placements before Import recovery can read or roll back those paths.
    public async Task ResumeAsync(IProgress<(int Processed, int Total)>? progress, CancellationToken ct)
    {
        await using var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.media_id
            FROM storage_operations o JOIN media m ON m.reconciliation_operation_id = o.operation_id
            WHERE o.kind = $kind AND m.path_state = 'PENDING';
            """;
        command.Parameters.AddWithValue("$kind", OperationKind);
        var pending = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                pending.Add(DbGuid.Parse(reader.GetString(0)));
        }

        for (var index = 0; index < pending.Count; index++)
        {
            progress?.Report((index, pending.Count));
            var id = pending[index];
            var placement = await catalog.MediaWrites.ReadOwnerRelocationPlanAsync(id, ct).ConfigureAwait(false)
                ?? throw new CatalogInvariantException("A pending media naming migration lost its catalog record.");
            await FinishAsync(placement, await ReadPackageAsync(id, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
    }

    public async Task<bool> IsCompleteAsync(CancellationToken ct)
    {
        await using var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", CompletionKey);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is "true";
    }

    public async Task MigrateVaultAsync(bool created, IProgress<(int Processed, int Total)>? progress, CancellationToken ct)
    {
        if (!created)
        {
            var active = new List<(Guid Id, MediaType Type, string Label)>();
            await using (var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT m.media_id, m.media_type, p.kind, p.display_name, p.unknown_sequence
                    FROM media m
                    LEFT JOIN profile_media pm ON pm.media_id = m.media_id AND pm.relation_type = 'OWNER'
                    LEFT JOIN profiles p ON p.profile_id = pm.profile_id
                    WHERE m.state = 'ACTIVE' AND m.trashed_at_ms IS NULL
                    ORDER BY m.media_id;
                    """;
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var id = DbGuid.Parse(reader.GetString(0));
                    if (reader.IsDBNull(2))
                        throw new ManagedMediaNamingException(id, "The media item has no recorded owner.");
                    var label = DbEnum.ParseProfileKind(reader.GetString(2)) == ProfileKind.Unknown
                        ? UnknownProfileRules.FormatDerivedLabel(reader.GetInt64(4))
                        : reader.GetString(3);
                    active.Add((id, DbEnum.ParseMediaType(reader.GetString(1)), label));
                }
            }

            progress?.Report((0, active.Count));
            var nextUpdate = Environment.TickCount64;
            for (var index = 0; index < active.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var (id, type, label) = active[index];
                await MigratePlacementAsync(id, type, label, ct).ConfigureAwait(false);
                if (progress is not null && (Environment.TickCount64 >= nextUpdate || index + 1 == active.Count))
                {
                    progress.Report((index + 1, active.Count));
                    nextUpdate = Environment.TickCount64 + 100;
                }
            }
        }

        await using var lease = await catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false);
        await using var writer = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var complete = writer.CreateCommand();
        complete.CommandText = """
            INSERT INTO settings(key, value_json, updated_at_ms) VALUES ($key, 'true', $now)
            ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json, updated_at_ms = excluded.updated_at_ms;
            """;
        complete.Parameters.AddWithValue("$key", CompletionKey);
        complete.Parameters.AddWithValue("$now", DbTime.Format(DateTimeOffset.UtcNow));
        await complete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // A pre-upgrade Import checkpoint can still supply a legacy location after startup, for example
    // when resuming a paused Import. Normalize verified placement before it becomes library authority.
    public async Task MigratePlacementAsync(Guid id, MediaType type, string label, CancellationToken ct)
    {
        var placement = await catalog.MediaWrites.ReadOwnerRelocationPlanAsync(id, ct).ConfigureAwait(false)
            ?? throw new CatalogInvariantException("Media disappeared while checking its managed name.");
        if (placement.CurrentManagedRelativePath is not { } directory || placement.CurrentManagedFileName is not { } fileName)
            return;

        var package = type == MediaType.Model ? await ReadPackageAsync(id, ct).ConfigureAwait(false) : [];
        var isPackage = package.Count > 0;
        var entry = isPackage ? directory[(directory.LastIndexOf('/') + 1)..] : fileName;
        if (!ManagedPathPlanner.HasLegacyMediaName(entry, id, type, isPackage))
            return;

        if (placement.PathState != ManagedPathState.None)
        {
            if (!await IsMigrationOperationAsync(placement.ReconciliationOperationId, ct).ConfigureAwait(false))
                throw new ManagedMediaNamingException(id, "Previous media placement must finish first.");
            await FinishAsync(placement, package, ct).ConfigureAwait(false);
            return;
        }

        var parent = isPackage ? directory[..directory.LastIndexOf('/')] : directory;
        var names = _planner.EnumerateMediaNames(parent, label, id, type,
            isPackage ? "" : Path.GetExtension(fileName),
            isPackage ? package.Select(component => component.ComponentRelativePath).ToArray() : null);
        foreach (var name in names)
        {
            var targetDirectory = isPackage ? $"{parent}/{name}" : directory;
            var targetName = isPackage ? fileName : name;
            var target = catalog.Paths.ResolveVaultRelativePath($"{parent}/{name}");
            if (EntryExists(target) || await IsTargetClaimedAsync(id, targetDirectory, isPackage ? null : targetName, ct).ConfigureAwait(false))
                continue;

            var operationId = await PersistPlanAsync(placement, targetDirectory, targetName, ct).ConfigureAwait(false);
            await FinishAsync(placement with
            {
                TargetManagedRelativePath = targetDirectory,
                TargetManagedFileName = targetName,
                ReconciliationOperationId = operationId
            }, package, ct).ConfigureAwait(false);
            return;
        }

        throw new ManagedMediaNamingException(id, "All managed name candidates are occupied.");
    }

    private async Task<IReadOnlyList<MediaComponentRecord>> ReadPackageAsync(Guid id, CancellationToken ct)
    {
        var components = await catalog.MediaWrites.GetMediaComponentsAsync(id, ct).ConfigureAwait(false);
        return components.Any(component => component.ComponentRole == ComponentRole.Dependency) ? components : [];
    }

    private async Task FinishAsync(PersistedOwnerRelocationPlan plan, IReadOnlyList<MediaComponentRecord> package, CancellationToken ct)
    {
        var sourceDirectory = plan.CurrentManagedRelativePath!;
        var targetDirectory = plan.TargetManagedRelativePath!;
        var source = catalog.Paths.ResolveVaultRelativePath(package.Count > 0 ? sourceDirectory :
            $"{sourceDirectory}/{plan.CurrentManagedFileName}");
        var target = catalog.Paths.ResolveVaultRelativePath(package.Count > 0 ? targetDirectory :
            $"{targetDirectory}/{plan.TargetManagedFileName}");
        if (await IsTargetClaimedAsync(plan.MediaId, targetDirectory,
                package.Count > 0 ? null : plan.TargetManagedFileName, ct).ConfigureAwait(false))
            throw new ManagedMediaNamingException(plan.MediaId, "Another media record claims the target.");

        var sourceExists = EntryExists(source);
        var targetExists = EntryExists(target);
        if (sourceExists && targetExists)
            throw new ManagedMediaNamingException(plan.MediaId, "The persisted target is occupied.");
        if (!sourceExists && !targetExists)
            throw new ManagedMediaNamingException(plan.MediaId, "The managed original is missing.");

        // Directory.Move preserves the entire package, including the exact spelling and layout of
        // its internal entries. A restarted operation verifies the target before adopting it.
        var location = sourceExists ? source : target;
        if (package.Count > 0)
        {
            var relative = sourceExists ? sourceDirectory : targetDirectory;
            foreach (var component in package)
                await VerifyAsync(plan.MediaId, catalog.Paths.ResolveVaultRelativePath($"{relative}/{component.ComponentRelativePath}"),
                    component.ByteLength, component.Sha256, ct).ConfigureAwait(false);
        }
        else
        {
            await VerifyAsync(plan.MediaId, location, plan.ExpectedByteLength!.Value, plan.ExpectedSha256!, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        if (sourceExists)
        {
            var move = package.Count > 0
                ? PathReconciler.MoveDirectory(source, target)
                : PathReconciler.MoveFile(source, target);
            if (!move.IsSuccess)
                throw new ManagedMediaNamingException(plan.MediaId, $"The managed original could not be renamed ({move.Status}).");
        }

        // Once bytes move, prefer completing the durable catalog commit even during shutdown.
        if (!await catalog.MediaWrites.CheckpointReconciledPlacementAsync(
                plan.MediaId, plan.ReconciliationOperationId!.Value, completeOperation: true, CancellationToken.None).ConfigureAwait(false))
            throw new ManagedMediaNamingException(plan.MediaId, "The catalog rejected the placement checkpoint.");
    }

    private async Task VerifyAsync(Guid id, string path, long length, string sha256, CancellationToken ct)
    {
        var result = await _verifier.VerifyAsync(path, length, sha256, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!result.IsMatch)
            throw new ManagedMediaNamingException(id, $"Managed content verification failed ({result.Status}).");
    }

    private async Task<Guid> PersistPlanAsync(PersistedOwnerRelocationPlan plan, string directory, string fileName, CancellationToken ct)
    {
        var operationId = Guid.NewGuid();
        await using var lease = await catalog.WriteCoordinator.EnterAsync(ct).ConfigureAwait(false);
        await using var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, catalog.WriteCoordinator);
        await using var update = transaction.CreateCommand("""
            UPDATE media
            SET target_managed_relative_path = $directory, target_managed_file_name = $fileName,
                path_state = 'PENDING', reconciliation_operation_id = $operation, row_version = row_version + 1
            WHERE media_id = $id AND state IN ('ACTIVE','CANDIDATE') AND path_state = 'NONE'
              AND current_managed_relative_path = $sourceDirectory AND current_managed_file_name = $sourceFile;
            """);
        update.Parameters.AddWithValue("$id", DbGuid.Format(plan.MediaId));
        update.Parameters.AddWithValue("$operation", DbGuid.Format(operationId));
        update.Parameters.AddWithValue("$directory", directory);
        update.Parameters.AddWithValue("$fileName", fileName);
        update.Parameters.AddWithValue("$sourceDirectory", plan.CurrentManagedRelativePath!);
        update.Parameters.AddWithValue("$sourceFile", plan.CurrentManagedFileName!);
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new ManagedMediaNamingException(plan.MediaId, "The managed location changed before migration.");

        await using var insert = transaction.CreateCommand("""
            INSERT INTO storage_operations(operation_id, kind, entity_type, entity_id, state, checkpoint_json, created_at_ms, updated_at_ms)
            VALUES ($operation, $kind, 'Media', $id, 'PENDING', '{}', $now, $now);
            """);
        insert.Parameters.AddWithValue("$operation", DbGuid.Format(operationId));
        insert.Parameters.AddWithValue("$kind", OperationKind);
        insert.Parameters.AddWithValue("$id", DbGuid.Format(plan.MediaId));
        insert.Parameters.AddWithValue("$now", DbTime.Format(DateTimeOffset.UtcNow));
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return operationId;
    }

    private async Task<bool> IsMigrationOperationAsync(Guid? operationId, CancellationToken ct)
    {
        if (operationId is not Guid id)
            return false;
        await using var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind FROM storage_operations WHERE operation_id = $operation;";
        command.Parameters.AddWithValue("$operation", DbGuid.Format(id));
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is OperationKind;
    }

    private async Task<bool> IsTargetClaimedAsync(Guid id, string directory, string? fileName, CancellationToken ct)
    {
        await using var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM media WHERE media_id <> $id AND (
                    (current_managed_relative_path = $directory COLLATE NOCASE
                     AND ($file IS NULL OR current_managed_file_name = $file COLLATE NOCASE))
                    OR (target_managed_relative_path = $directory COLLATE NOCASE
                     AND ($file IS NULL OR target_managed_file_name = $file COLLATE NOCASE))
                ));
            """;
        command.Parameters.AddWithValue("$id", DbGuid.Format(id));
        command.Parameters.AddWithValue("$directory", directory);
        command.Parameters.AddWithValue("$file", (object?)fileName ?? DBNull.Value);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is 1L;
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}

internal sealed class ManagedMediaNamingException(Guid mediaId, string detail)
    : IOException($"Media {mediaId:D}. {detail}");
