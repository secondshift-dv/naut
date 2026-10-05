using System.IO;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Import;

/// <summary>
/// Re-applies the persisted import source timestamp to canonical managed files after canonical commit
/// materialization. Recovery uses this before source cleanup so a restart cannot silently lose the
/// original-date invariant when placement resumes through a recovery coordinator.
/// </summary>
public sealed class ImportSourceTimestampReconciler
{
    private readonly CatalogDb _catalog;
    private readonly MediaWrites _assetWrites;
    private readonly VaultPaths _paths;

    public ImportSourceTimestampReconciler(CatalogDb catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _assetWrites = new MediaWrites(catalog);
        _paths = catalog.Paths;
    }

    public async Task ReconcileAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return;
        }

        var items = await _catalog.ImportReads
            .GetUnitItemsUnboundedAsync(unitId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.Disposition != ItemDisposition.Included
                || item.DuplicateDecision == DuplicateDecision.Reuse
                || item.CandidateMediaId is not { } assetId
                || item.SourceLastWriteUtc is not { } sourceTimestamp)
            {
                continue;
            }

            var location = await ReadManagedLocationAsync(assetId, cancellationToken)
                .ConfigureAwait(false);
            if (location is null)
            {
                continue;
            }

            var components = await _assetWrites.GetMediaComponentsAsync(assetId, cancellationToken)
                .ConfigureAwait(false);
            var isMultiFile = components.Count > 1
                || (components.Count == 1 && components[0].ComponentRole == ComponentRole.Dependency);

            if (!isMultiFile)
            {
                var targetPath = TryResolveManagedPath(
                    location.ManagedRelativePath,
                    location.ManagedFileName);
                if (targetPath is not null)
                {
                    TryApplyTimestamp(targetPath, sourceTimestamp.UtcDateTime);
                }

                continue;
            }

            var primarySourceDirectory = Path.GetDirectoryName(item.SourcePath) ?? string.Empty;
            foreach (var component in components)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var targetPath = TryResolveManagedPath(
                    location.ManagedRelativePath,
                    component.ComponentRelativePath);
                if (targetPath is null)
                {
                    continue;
                }

                var componentSourcePath = !string.IsNullOrWhiteSpace(component.OriginalSourcePath)
                    ? component.OriginalSourcePath
                    : Path.Combine(
                        primarySourceDirectory,
                        component.ComponentRelativePath.Replace('/', Path.DirectorySeparatorChar));
                var componentTimestamp = TryReadTimestamp(componentSourcePath);
                if (componentTimestamp is { } timestamp)
                {
                    TryApplyTimestamp(targetPath, timestamp);
                }
            }
        }
    }

    private async Task<ManagedLocation?> ReadManagedLocationAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT current_managed_relative_path, current_managed_file_name
            FROM media
            WHERE media_id = $assetId;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.IsDBNull(0)
            || reader.IsDBNull(1))
        {
            return null;
        }

        return new ManagedLocation(reader.GetString(0), reader.GetString(1));
    }

    private string? TryResolveManagedPath(string relativePath, string fileName)
    {
        try
        {
            return _paths.ResolveVaultRelativePath(CombineRelative(relativePath, fileName));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static DateTime? TryReadTimestamp(string sourcePath)
    {
        try
        {
            return File.Exists(sourcePath) ? File.GetLastWriteTimeUtc(sourcePath) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryApplyTimestamp(string targetPath, DateTime sourceTimestampUtc)
    {
        try
        {
            if (File.Exists(targetPath))
            {
                File.SetLastWriteTimeUtc(targetPath, sourceTimestampUtc);
            }
        }
        catch (IOException)
        {
            // canonical commit bytes are already authoritative; timestamp preservation remains advisory.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale: never invalidate otherwise durable canonical media.
        }
    }

    private static string CombineRelative(string first, string second) =>
        $"{first.TrimEnd('/', '\\')}/{second.TrimStart('/', '\\')}";

    private sealed record ManagedLocation(string ManagedRelativePath, string ManagedFileName);
}
