using System.IO;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.Media.Model;

namespace Neuterradise.App.Maintenance;

/// <summary>Original recovery is a LIBRARY_REPAIR operation; evidence and staged bytes are owned by its durable OperationId.</summary>
internal sealed class CanonicalOriginalRecovery(CatalogDb catalog)
{
    private readonly ManagedFileVerifier _verifier = new();

    private string Target(HealthMediaItem media, CanonicalOriginalInspection.OriginalFile file)
        => catalog.Paths.ResolveVaultRelativePath(VaultPathArea.Profiles,
            $"{media.CurrentManagedRelativePath}/{file.RelativePath}");

    private string Work(Guid operation, string area, int index)
        => catalog.Paths.ResolveVaultRelativePath(VaultPathArea.Root,
            $"_system/{area}/{operation:D}/{index:D6}");

    private string Source(HealthMediaItem media, string selection, bool package, CanonicalOriginalInspection.OriginalFile file)
    {
        var path = package
            ? RootPathRules.ResolveContainedPath(selection, selection, file.RelativePath, nameof(selection))
            : Path.GetFullPath(selection);
        RootPathRules.RejectExistingReparsePoints(Path.GetPathRoot(path)!, path);
        if (RootPathRules.IsWithinOrEqual(catalog.Paths.Root, path))
        {
            if (media.OwnerManagedRelativePath is not { Length: > 0 } ownerPath
                || !RootPathRules.IsStrictDescendant(catalog.Paths.ResolveVaultRelativePath(ownerPath), path))
                throw new IOException("An in-Vault recovery source must be in the Media's recorded owner folder.");
        }
        return path;
    }

    // Adopt only a unique existing path derived from the owner's folder and stable identity.
    internal async Task<string?> FindPlacementAsync(HealthMediaItem media,
        IReadOnlyList<MediaComponentRecord> components, bool deep, CancellationToken ct,
        CatalogTransaction? transaction = null)
    {
        if (media is not { HasStablePublishedOwner: true, OwnerProfileId: Guid owner,
            OwnerManagedRelativePath: { Length: > 0 } ownerFolder, OwnerStorageToken: { Length: > 0 } ownerToken,
            StorageToken: { Length: > 0 } token, CurrentManagedRelativePath: { Length: > 0 } oldDirectory,
            CurrentManagedFileName: { Length: > 0 } oldName }) return null;
        var files = CanonicalOriginalInspection.Files(media, components);
        if (files is null) return null;
        var isPackage = files.Count > 1;
        var planner = new ManagedPathPlanner(catalog.Paths.Root);
        var label = media.OwnerDisplayName ?? "Profile";
        var planned = planner.PlanMedia(owner, label, new ProfileStorageToken(ownerToken), media.MediaId,
            new MediaStorageToken(token), media.MediaType, media.Extension);
        var plannedFile = planned.ManagedFileRelativePath!;
        var parent = ownerFolder + plannedFile[planned.ProfileFolderRelativePath.Length..plannedFile.LastIndexOf('/')];
        // Do not silently abandon an existing or partially occupied previous placement.
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var old = Target(media, file);
            if (EntryExists(old)) return null;
        }
        string? unique = null;
        foreach (var name in planner.EnumerateMediaNames(parent, label, media.MediaId, media.MediaType,
            isPackage ? "" : Path.GetExtension(planned.ManagedFileName)!,
            isPackage ? files.Select(f => f.RelativePath).ToArray() : null))
        {
            ct.ThrowIfCancellationRequested();
            var directory = isPackage ? $"{parent}/{name}" : parent;
            var fileName = isPackage ? oldName : name;
            if ($"{directory}/{fileName}" == $"{oldDirectory}/{oldName}") continue;
            var candidate = isPackage ? media with { CurrentManagedRelativePath = directory }
                : media with { CurrentManagedRelativePath = directory, CurrentManagedFileName = fileName };
            var candidateFiles = CanonicalOriginalInspection.Files(candidate, components)!;
            var exists = false;
            var matches = true;
            foreach (var file in candidateFiles)
            {
                var absolute = Target(candidate, file);
                if (!EntryExists(absolute)) { matches = false; continue; }
                exists = true;
                if (!await IsUnclaimedAsync(media.MediaId, absolute, ct, transaction).ConfigureAwait(false)
                    || await CanonicalOriginalInspection.InspectFileAsync(catalog, $"{directory}/{file.RelativePath}",
                        file.Length, file.Sha256, deep, ct).ConfigureAwait(false) != OriginalInspectionStatus.Match)
                    return null;
            }
            if (!exists) continue;
            if (!matches || unique is not null) return null;
            unique = $"{directory}/{fileName}";
        }
        return unique;
    }

    internal async Task<bool> ValidateSelectionAsync(HealthMediaItem media,
        IReadOnlyList<CanonicalOriginalInspection.OriginalFile> files, string selection, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(selection)) return false;
        var package = files.Count > 1;
        var needsRepair = false;
        foreach (var file in files)
        {
            var target = Target(media, file);
            RequireOwnedDestination(media, target);
            var current = await _verifier.VerifyAsync(target, file.Length, file.Sha256, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (current.IsMatch) continue;
            if (!Repairable(current)) return false;
            needsRepair = true;
            var source = Source(media, selection, package, file);
            if (RootPathRules.AreSameRoot(source, target)
                || !await IsUnclaimedAsync(media.MediaId, source, ct).ConfigureAwait(false))
                return false;
            if (!(await _verifier.VerifyAsync(source, file.Length, file.Sha256, ct).ConfigureAwait(false)).IsMatch)
                return false;
        }
        return needsRepair;
    }

    internal async Task StageAsync(RepairPlan plan, HealthMediaItem media,
        IReadOnlyList<CanonicalOriginalInspection.OriginalFile> files, CancellationToken ct)
    {
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            var target = Target(media, file);
            RequireOwnedDestination(media, target);
            var current = await _verifier.VerifyAsync(target, file.Length, file.Sha256, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (current.IsMatch) continue;
            if (!Repairable(current)) throw new IOException("Canonical original is not safely accessible.");
            var staged = Work(plan.OperationId, "staging/repairs", i);
            var existing = await _verifier.VerifyAsync(staged, file.Length, file.Sha256, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (existing.IsMatch) continue;
            if (!Repairable(existing)) throw new IOException("Repair staging is not safely accessible.");
            var source = Source(media, plan.SourcePathOrRelative, files.Count > 1, file);
            if (RootPathRules.AreSameRoot(source, target)
                || !await IsUnclaimedAsync(media.MediaId, source, ct).ConfigureAwait(false))
                throw new IOException("Recovery source conflicts with another managed original.");
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            RootPathRules.RejectExistingReparsePoints(catalog.Paths.Root, staged);
            // Only this journal-owned incomplete stage may be replaced. Never overwrite a canonical original here.
            if (existing.Status != ManagedFileVerificationStatus.Missing) File.Delete(staged);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (!SourceIdentityHelper.MatchesOpenedPath(input.SafeFileHandle, source) || input.Length != file.Length)
                    throw new IOException("Recovery source identity changed.");
                await using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            if (!(await _verifier.VerifyAsync(staged, file.Length, file.Sha256, ct).ConfigureAwait(false)).IsMatch)
                throw new IOException("Recovery source failed canonical fingerprint verification.");
        }
    }

    // Called only after a catalog compare-and-set under the existing writer/transaction authority.
    // A process interruption leaves the original excluded and the EXECUTING receipt available for replay.
    internal async Task PublishAsync(CatalogTransaction transaction, RepairPlan plan, HealthMediaItem media,
        IReadOnlyList<CanonicalOriginalInspection.OriginalFile> files, CancellationToken ct)
    {
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            var target = Target(media, file);
            RequireOwnedDestination(media, target);
            if (!await IsUnclaimedAsync(media.MediaId, target, ct, transaction).ConfigureAwait(false))
                throw new IOException("Another Media claims this managed destination.");
            var current = await _verifier.VerifyAsync(target, file.Length, file.Sha256, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (current.IsMatch) continue;
            if (!Repairable(current)) throw new IOException("Canonical original is not safely accessible.");
            var staged = Work(plan.OperationId, "staging/repairs", i);
            if (!(await _verifier.VerifyAsync(staged, file.Length, file.Sha256, ct).ConfigureAwait(false)).IsMatch)
                throw new IOException("Repair staging has no verified original.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            RootPathRules.RejectExistingReparsePoints(catalog.Paths.Root, target);
            RootPathRules.RejectExistingReparsePoints(catalog.Paths.Root, staged);
            ct.ThrowIfCancellationRequested();
            if (current.Status == ManagedFileVerificationStatus.Missing)
                File.Move(staged, target, overwrite: false);
            else
            {
                var evidence = Work(plan.OperationId, "repair-evidence", i);
                Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
                RootPathRules.RejectExistingReparsePoints(catalog.Paths.Root, evidence);
                if (File.Exists(evidence) || Directory.Exists(evidence))
                    throw new IOException("An earlier repair already preserved evidence; no bytes were overwritten.");
                File.Replace(staged, target, evidence, ignoreMetadataErrors: false);
            }
            if (!(await _verifier.VerifyAsync(target, file.Length, file.Sha256, CancellationToken.None).ConfigureAwait(false)).IsMatch)
                throw new IOException("Recovered original failed final verification.");
        }
    }

    internal void CleanupStaging(RepairPlan plan, int count)
    {
        // Only exact files owned by the completed operation, never recursive deletion.
        for (var i = 0; i < count; i++)
        {
            var staged = Work(plan.OperationId, "staging/repairs", i);
            if (File.Exists(staged)) File.Delete(staged);
        }
        var directory = Path.GetDirectoryName(Work(plan.OperationId, "staging/repairs", 0))!;
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private void RequireOwnedDestination(HealthMediaItem media, string absolute)
    {
        if (media.OwnerManagedRelativePath is not { Length: > 0 } owner
            || !RootPathRules.IsStrictDescendant(catalog.Paths.ResolveVaultRelativePath(owner), absolute))
            throw new IOException("The original placement is not contained by its recorded owner folder.");
    }

    private static bool Repairable(ManagedFileVerificationResult result)
        => result.Status is ManagedFileVerificationStatus.Missing or ManagedFileVerificationStatus.LengthMismatch
            or ManagedFileVerificationStatus.HashMismatch;

    internal async Task<bool> IsUnclaimedAsync(Guid mediaId, string absolute, CancellationToken ct,
        CatalogTransaction? transaction = null)
    {
        if (!RootPathRules.IsStrictDescendant(catalog.Paths.ProfilesPath, absolute)) return true;
        var relative = Path.GetRelativePath(catalog.Paths.Root, absolute).Replace('\\', '/');
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM media m WHERE m.media_id <> $id AND (
                    (m.current_managed_relative_path || '/' || m.current_managed_file_name) = $path COLLATE NOCASE
                    OR (m.target_managed_relative_path || '/' || m.target_managed_file_name) = $path COLLATE NOCASE
                    OR EXISTS (SELECT 1 FROM media_components c WHERE c.media_id=m.media_id AND
                        ((m.current_managed_relative_path || '/' || c.component_relative_path) = $path COLLATE NOCASE
                        OR (m.target_managed_relative_path || '/' || c.component_relative_path) = $path COLLATE NOCASE))));
            """;
        await using var connection = transaction is null ? await catalog.OpenConnectionAsync(ct).ConfigureAwait(false) : null;
        await using var command = transaction is not null ? transaction.CreateCommand(sql) : connection!.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", DbGuid.Format(mediaId));
        command.Parameters.AddWithValue("$path", relative);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0;
    }
}
