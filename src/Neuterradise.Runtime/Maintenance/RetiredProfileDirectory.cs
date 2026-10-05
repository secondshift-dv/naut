using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Trash;

namespace Neuterradise.App.Maintenance;

internal static class RetiredProfileDirectory
{
    internal sealed record Authority(Guid ProfileId, long RowVersion, string State);

    internal static async Task<Authority?> ReadAuthorityAsync(
        CatalogDb catalog,
        string relative,
        CancellationToken cancellationToken)
    {
        relative = relative.Replace('\\', '/').TrimEnd('/');
        await using var connection = await catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // While a Profile is still in Trash, its durable IN_TRASH receipt plus recovery manifest
        // authorizes removal of source scaffolding only when that scaffolding is independently
        // proven empty by the caller.
        await using (var trashed = connection.CreateCommand())
        {
            trashed.CommandText = """
                SELECT p.profile_id, p.row_version, te.recovery_relative_path
                FROM profiles p
                JOIN trash_entries te
                  ON te.entity_type='PROFILE'
                 AND te.entity_id=p.profile_id
                 AND te.state='IN_TRASH'
                WHERE p.trashed_at_ms IS NOT NULL
                  AND rtrim(p.current_managed_relative_path, '/') = $relative
                ORDER BY te.updated_at_ms DESC
                LIMIT 1;
                """;
            trashed.Parameters.AddWithValue("$relative", relative);
            await using var reader = await trashed.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.IsDBNull(2))
            {
                try
                {
                    var recovery = catalog.Paths.ResolveVaultRelativePath(reader.GetString(2));
                    if (File.Exists(Path.Combine(recovery, ProfileManifestWriter.ManifestFileName)))
                    {
                        return new Authority(
                            DbGuid.Parse(reader.GetString(0)),
                            reader.GetInt64(1),
                            "TRASHED");
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return null;
                }
            }
        }

        // After Purge, only storage work owned by that same Profile can block cleanup. Unrelated
        // imports elsewhere in the Vault must not make a proven empty retired folder ambiguous.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT te.entity_id, te.row_version, te.plan_json
            FROM trash_entries te
            WHERE te.entity_type='PROFILE'
              AND te.state='PURGED'
              AND NOT EXISTS(SELECT 1 FROM profiles p WHERE p.profile_id=te.entity_id)
              AND NOT EXISTS(
                  SELECT 1
                  FROM storage_operations s
                  WHERE s.kind<>'LIBRARY_REPAIR'
                    AND s.state NOT IN ('COMPLETED','TERMINAL','CANCELLED','STALE')
                    AND upper(s.entity_type)='PROFILE'
                    AND s.entity_id=te.entity_id);
            """;
        await using var purgeReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await purgeReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var plan = ProfileTrashPlan.FromJson(purgeReader.GetString(2));
            if (plan?.CurrentManagedRelativePath is not string recorded
                || !string.Equals(
                    recorded.Replace('\\', '/').TrimEnd('/'),
                    relative,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var profiles = await catalog.HealthReads
                .GetHealthProfileItemsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (profiles.Any(profile =>
                    string.Equals(
                        profile.CurrentManagedRelativePath?.Replace('\\', '/').TrimEnd('/'),
                        relative,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        profile.TargetManagedRelativePath?.Replace('\\', '/').TrimEnd('/'),
                        relative,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return new Authority(
                DbGuid.Parse(purgeReader.GetString(0)),
                purgeReader.GetInt64(1),
                "PURGED");
        }

        return null;
    }

    internal static bool IsEmptyTree(string vaultRoot, string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        RootPathRules.RejectExistingReparsePoints(vaultRoot, path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            RootPathRules.RejectExistingReparsePoints(vaultRoot, entry);
            if (!Directory.Exists(entry) || !IsEmptyTree(vaultRoot, entry))
            {
                return false;
            }
        }

        return true;
    }

    internal static void RemoveEmptyTree(string vaultRoot, string path)
    {
        RootPathRules.RejectExistingReparsePoints(vaultRoot, path);
        foreach (var directory in Directory.EnumerateDirectories(path))
        {
            RemoveEmptyTree(vaultRoot, directory);
        }

        // Non-recursive deletion fails closed if any file appears after inspection.
        Directory.Delete(path, recursive: false);
    }
}
