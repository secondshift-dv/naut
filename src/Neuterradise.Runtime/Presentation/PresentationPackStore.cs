using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Neuterradise.App.Media.Video;
using Neuterradise.App.SystemServices.MediaTools;

namespace Neuterradise.App.Presentation;

public enum PackOrigin
{
    BuiltIn,
    User,
}

/// <summary>A pack as the registry knows it: manifest, where it lives, and whether it may be used.</summary>
public sealed record InstalledPack(
    PresentationPackManifest Manifest,
    PackOrigin Origin,
    string? RootPath,
    string ContentHash,
    IReadOnlyList<PackDiagnostic> Diagnostics)
{
    public bool IsUsable => Diagnostics.All(d => !d.IsError);

    public string PackId => Manifest.PackId;
}

public sealed record PackInstallResult(bool Succeeded, InstalledPack? Pack, IReadOnlyList<PackDiagnostic> Diagnostics, string? Message = null);

/// <summary>
/// Presentation resolves assets only as (packId, assetId). It never receives or concatenates an
/// arbitrary Vault path; the store owns the mapping and re-validates containment on every lookup.
/// </summary>
public interface IPresentationAssetStore
{
    PackAssetEntry? GetAsset(string packId, string assetId);

    /// <summary>Absolute path of a validated asset, or null when the pack/asset is unknown or unsafe.</summary>
    string? ResolveAssetPath(string packId, string assetId);
}

/// <summary>
/// Built-in packs live with the application payload (manifest embedded,
/// assets under InstallRoot); user packs are durable Vault user data under <c>_system/presentation-packs</c>;
/// compiled plans and derivatives live only in memory / app cache. Nothing here ever deletes media,
/// Profiles or other Vault content.
/// </summary>
public sealed class PresentationPackStore : IPresentationAssetStore
{
    public const string PackArchiveExtension = ".ntpack";
    private const string StagingFolderName = ".staging";
    private const int MaxJournalBytes = 512 * 1024;
    private const long MaxArchiveUncompressedBytes = PackValidator.MaxPackBytes + (16L * 1024 * 1024);

    private readonly string _userPacksRoot;
    private readonly string? _builtInAssetsRoot;
    private readonly Func<string, string, bool>? _tryMoveDirectory;
    private readonly ExternalToolResolver? _videoTools;
    private readonly Lock _sync = new();
    private Dictionary<string, InstalledPack> _packs = new(StringComparer.Ordinal);

    public PresentationPackStore(string userPacksRoot, string? builtInAssetsRoot, ExternalToolResolver? videoTools = null)
        : this(userPacksRoot, builtInAssetsRoot, null, videoTools)
    {
    }

    internal PresentationPackStore(
        string userPacksRoot,
        string? builtInAssetsRoot,
        Func<string, string, bool>? tryMoveDirectory,
        ExternalToolResolver? videoTools = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userPacksRoot);
        _userPacksRoot = Path.GetFullPath(userPacksRoot);
        _builtInAssetsRoot = builtInAssetsRoot is null ? null : Path.GetFullPath(builtInAssetsRoot);
        _tryMoveDirectory = tryMoveDirectory;
        _videoTools = videoTools;
    }

    public string UserPacksRoot => _userPacksRoot;

    public IReadOnlyList<InstalledPack> Packs
    {
        get
        {
            lock (_sync)
            {
                return [.. _packs.Values.OrderBy(p => p.Origin).ThenBy(p => p.Manifest.Name, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    public bool TryGetPack(string packId, out InstalledPack? pack)
    {
        lock (_sync)
        {
            return _packs.TryGetValue(packId, out pack);
        }
    }

    /// <summary>Loads the built-in pack and every user pack. A bad user pack is kept as unusable, never fatal.</summary>
    public IReadOnlyList<InstalledPack> LoadAll()
    {
        EnsureManagedBoundary();
        var loaded = new Dictionary<string, InstalledPack>(StringComparer.Ordinal);
        var builtIn = LoadBuiltIn();
        loaded[builtIn.PackId] = builtIn;

        if (Directory.Exists(_userPacksRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(_userPacksRoot))
            {
                if (Path.GetFileName(directory).StartsWith('.'))
                {
                    continue;
                }

                var pack = LoadUserPack(directory);
                if (pack is null)
                {
                    continue;
                }

                if (loaded.ContainsKey(pack.PackId))
                {
                    Trace.TraceWarning("Presentation pack {0} is duplicated at {1}; the first copy is used.", pack.PackId, directory);
                    continue;
                }

                loaded[pack.PackId] = pack;
            }
        }

        lock (_sync)
        {
            _packs = loaded;
        }

        return Packs;
    }

    public static string ReadBuiltInManifestJson()
    {
        var catalog = JsonNode.Parse(BuiltInPresentationCatalog.ReadResource("Neuterradise.App.Presentation.BuiltIn.catalog.json"))!.AsObject();
        var definitions = new JsonArray();
        foreach (var file in catalog["definitionFiles"]!.AsArray())
        {
            var name = Path.GetFileName(file!.GetValue<string>());
            definitions.Add(JsonNode.Parse(BuiltInPresentationCatalog.ReadResource($"Neuterradise.App.Presentation.BuiltIn.{name}")));
        }

        catalog.Remove("definitionFiles");
        catalog.Remove("defaults");
        catalog.Remove("primary");
        catalog.Remove("replacements");
        catalog["definitions"] = definitions;
        return catalog.ToJsonString();
    }

    private InstalledPack LoadBuiltIn()
    {
        var json = ReadBuiltInManifestJson();
        var read = PackManifestReader.Read(json, "built-in");
        if (read.Manifest is null)
        {
            throw new InvalidOperationException("The built-in Presentation Pack manifest is invalid: " + string.Join("; ", read.Diagnostics.Select(d => d.Detail)));
        }

        foreach (var slot in PresentationSlots.All.Where(s => !s.IsStateOnly))
        {
            if (!read.Manifest.Definitions.Any(d => d.Id == slot.Default?.DefinitionId && d.Kind == slot.Kind))
            {
                throw new InvalidOperationException($"Built-in default for '{slot.Id}' is missing or has the wrong kind.");
            }
        }

        var diagnostics = read.Diagnostics.Concat(PackValidator.Validate(read.Manifest, _builtInAssetsRoot is not null && Directory.Exists(_builtInAssetsRoot) ? _builtInAssetsRoot : null, isBuiltIn: true)).ToList();
        foreach (var diagnostic in diagnostics.Where(d => d.IsError))
        {
            Trace.TraceError("Built-in presentation pack diagnostic {0} on {1}: {2}", diagnostic.Code, diagnostic.Subject, diagnostic.Detail);
        }

        // Built-in assets are optional payload; a missing asset only disables the definitions using it.
        var fatal = diagnostics.Where(d => d.IsError && d.Code is not (PackDiagnosticCodes.AssetMissing or PackDiagnosticCodes.AssetUnreadable)).ToList();
        return new InstalledPack(read.Manifest, PackOrigin.BuiltIn, _builtInAssetsRoot, Hash(json), fatal);
    }

    private static InstalledPack? LoadUserPack(string directory)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
            var manifestPath = Path.Combine(directory, PackManifestReader.ManifestFileName);
            if (!File.Exists(manifestPath)) return null;
            if (new FileInfo(manifestPath).Length > PackManifestReader.MaxManifestBytes) return null;
            var json = File.ReadAllText(manifestPath, Encoding.UTF8);
            var read = PackManifestReader.Read(json, manifestPath);
            if (read.Manifest is null)
            {
                return null;
            }

            var diagnostics = read.Diagnostics.Concat(PackValidator.Validate(read.Manifest, directory, isBuiltIn: false)).ToList();
            if (!string.Equals(Path.GetFileName(directory), read.Manifest.PackId, StringComparison.Ordinal))
            {
                diagnostics.Add(new(PackDiagnosticCodes.IdInvalid, read.Manifest.PackId, "The pack folder name must equal its pack id."));
            }

            return new InstalledPack(read.Manifest, PackOrigin.User, directory, HashDirectory(directory), diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            Trace.TraceWarning("Presentation pack at {0} could not be read: {1}", directory, exception.GetType().Name);
            return null;
        }
    }

    // ----------------------------------------------------------------- install / export / remove

    /// <summary>
    /// Installs a pack from a folder or a <c>.ntpack</c>/<c>.zip</c> archive: stage, validate manifest
    /// and assets, hash, then move into the Vault. Registration follows publication.
    /// </summary>
    public Task<PackInstallResult> InstallAsync(string sourcePath, bool replaceExisting, CancellationToken cancellationToken = default) =>
        InstallWithPublicationAsync(sourcePath, replaceExisting, static (_, _) => Task.CompletedTask, cancellationToken);

    internal async Task<PackInstallResult> InstallWithPublicationAsync(
        string sourcePath, bool replaceExisting,
        Func<InstalledPack, CancellationToken, Task> publish,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        Directory.CreateDirectory(_userPacksRoot);
        EnsureManagedBoundary();
        var staging = Path.Combine(_userPacksRoot, StagingFolderName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            if (Directory.Exists(sourcePath))
            {
                await Task.Run(() => CopyDirectorySafe(sourcePath, staging), cancellationToken).ConfigureAwait(false);
            }
            else if (File.Exists(sourcePath))
            {
                var extractError = await Task.Run(() => ExtractArchiveSafe(sourcePath, staging), cancellationToken).ConfigureAwait(false);
                if (extractError is not null)
                {
                    return new(false, null, [new(PackDiagnosticCodes.ForbiddenContent, Path.GetFileName(sourcePath), extractError)], extractError);
                }
            }
            else
            {
                return new(false, null, [], "The selected pack could not be found.");
            }

            // Accept archives that wrap the pack in one top-level folder.
            var root = File.Exists(Path.Combine(staging, PackManifestReader.ManifestFileName))
                ? staging
                : Directory.GetDirectories(staging) is [var single] && File.Exists(Path.Combine(single, PackManifestReader.ManifestFileName)) ? single : null;
            if (root is null)
            {
                return new(false, null, [new(PackDiagnosticCodes.FieldMissing, "pack.json", "The pack has no pack.json manifest.")], "This is not a Presentation Pack.");
            }

            var manifestPath = Path.Combine(root, PackManifestReader.ManifestFileName);
            if (new FileInfo(manifestPath).Length > PackManifestReader.MaxManifestBytes)
                return new(false, null, [new(PackDiagnosticCodes.PackTooLarge, "pack.json", "The manifest exceeds 4 MiB.")], "The pack manifest is too large.");
            var json = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var read = PackManifestReader.Read(json, "pack.json");
            if (read.Manifest is null)
            {
                return new(false, null, read.Diagnostics, "The pack manifest is not valid.");
            }

            var diagnostics = read.Diagnostics.Concat(PackValidator.Validate(read.Manifest, root, isBuiltIn: false)).ToList();
            if (!diagnostics.Any(d => d.IsError))
                diagnostics.AddRange(await ValidateVideosAsync(read.Manifest, root, cancellationToken).ConfigureAwait(false));
            var compileDiagnostics = PresentationCompiler.ValidateDefinitions(read.Manifest);
            diagnostics.AddRange(compileDiagnostics);
            if (diagnostics.Any(d => d.IsError))
            {
                return new(false, null, diagnostics, "The pack did not pass validation.");
            }

            var packId = read.Manifest.PackId;
            lock (_sync)
            {
                if (_packs.TryGetValue(packId, out var existing) && (existing.Origin == PackOrigin.BuiltIn || !replaceExisting))
                {
                    var conflict = existing.Origin == PackOrigin.BuiltIn
                        ? new PackDiagnostic(PackDiagnosticCodes.IdReserved, packId, "A built-in pack already uses that id.")
                        : new PackDiagnostic(PackDiagnosticCodes.AlreadyInstalled, packId, "A pack with that id is already installed.");
                    return new(false, null, [.. diagnostics, conflict], conflict.Detail);
                }
            }

            var destination = Path.Combine(_userPacksRoot, packId);
            var hash = HashDirectory(root);
            var transaction = new PackTransaction(packId, Guid.NewGuid().ToString("N"), "install", hash, Directory.Exists(destination), Directory.Exists(destination) ? HashDirectory(destination) : null);
            WriteTransaction(transaction);
            var backup = TransactionPayloadPath(transaction);
            var installed = new InstalledPack(read.Manifest, PackOrigin.User, destination, hash, diagnostics);
            try
            {
                if (transaction.HadPrevious) Directory.Move(destination, backup);
                Directory.Move(root, destination);
                await publish(installed, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                RollbackInstall(transaction);
                throw;
            }
            TryFinishTransaction(transaction);

            lock (_sync)
            {
                _packs[packId] = installed;
            }

            return new(true, installed, diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(false, null, [], "The pack could not be installed: " + exception.Message);
        }
        finally
        {
            TryDeleteStaging(staging);
        }
    }

    /// <summary>Writes a pack as a portable <c>.ntpack</c> archive. Built-in packs export their manifest only.</summary>
    public async Task ExportAsync(string packId, string destinationFile, CancellationToken cancellationToken = default)
    {
        InstalledPack? pack;
        lock (_sync)
        {
            _packs.TryGetValue(packId, out pack);
        }

        if (pack is null)
        {
            throw new InvalidOperationException("That pack is not installed.");
        }

        var temporary = destinationFile + ".partial";
        await Task.Run(() =>
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                if (pack.Origin == PackOrigin.BuiltIn)
                {
                    var entry = archive.CreateEntry(PackManifestReader.ManifestFileName, CompressionLevel.Optimal);
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    writer.Write(ReadBuiltInManifestJson());
                }
                else
                {
                    foreach (var file in PackValidator.EnumerateFilesWithoutLinks(pack.RootPath!))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(pack.RootPath!, file).Replace('\\', '/');
                        archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                    }
                }
            }

            File.Move(temporary, destinationFile, overwrite: true);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal Task<PackRemovalStage?> StageRemovalAsync(
        string packId,
        CancellationToken cancellationToken = default)
    {
        InstalledPack? pack;
        lock (_sync)
        {
            _packs.TryGetValue(packId, out pack);
        }

        if (pack is null || pack.Origin != PackOrigin.User || pack.RootPath is null)
        {
            return Task.FromResult<PackRemovalStage?>(null);
        }

        return Task.Run(() =>
        {
            var root = Path.GetFullPath(pack.RootPath);
            if (!root.StartsWith(_userPacksRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            EnsureManagedBoundary();
            RequireHash(root, pack.ContentHash);
            var transaction = new PackTransaction(packId, Guid.NewGuid().ToString("N"), "remove", pack.ContentHash, true, pack.ContentHash);
            WriteTransaction(transaction);
            var removed = TransactionPayloadPath(transaction);
            Directory.CreateDirectory(Path.GetDirectoryName(removed)!);
            try
            {
                var moved = _tryMoveDirectory?.Invoke(root, removed) ?? MoveDirectory(root, removed);
                if (!moved) { DeleteTransaction(transaction); return null; }
                return new PackRemovalStage(packId, root, removed, transaction.Token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                DeleteTransaction(transaction);
                Trace.TraceWarning("Presentation pack could not be staged for removal: {0}", exception.GetType().Name);
                return null;
            }
        }, cancellationToken);
    }

    internal bool RestoreRemoval(PackRemovalStage stage)
    {
        try
        {
            EnsureManagedBoundary();
            var transaction = ReadTransaction(stage.Token);
            RestoreRemoved(transaction);
            DeleteJournal(stage.Token);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            Trace.TraceError("Presentation pack removal rollback failed for {0}: {1}", stage.PackId, exception.Message);
            return false;
        }
    }

    internal void CompleteRemoval(PackRemovalStage stage)
    {
        lock (_sync)
        {
            _packs.Remove(stage.PackId);
        }

        // The atomic rename made the pack unavailable. A locked staging folder is harmless and can
        // be retried by later maintenance; it must not turn a coherent logical removal into data loss.
        try { TryFinishTransaction(ReadTransaction(stage.Token)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            Trace.TraceWarning("A committed pack removal retains its recovery journal: {0}", exception.GetType().Name);
        }
    }

    internal Task RecoverPendingAsync(IReadOnlyDictionary<string, string> recordedHashes, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            EnsureManagedBoundary();
            var staging = Path.Combine(_userPacksRoot, StagingFolderName);
            if (!Directory.Exists(staging)) return;
            foreach (var journal in Directory.EnumerateFiles(staging, "*.transaction.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(journal) & FileAttributes.ReparsePoint) != 0 || new FileInfo(journal).Length > MaxJournalBytes)
                    throw new InvalidDataException("An invalid pack recovery journal requires attention.");
                var transaction = ReadTransaction(Path.GetFileName(journal)[..^".transaction.json".Length]);
                if (transaction is null || !PackValidator.IsValidId(transaction.PackId) || transaction.PackId.StartsWith("builtin.", StringComparison.Ordinal)
                    || !Guid.TryParseExact(transaction.Token, "N", out _) || transaction.Mode is not ("install" or "remove")
                    || !string.Equals(journal, TransactionJournalPath(transaction.Token), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("An invalid pack recovery journal requires attention.");
                var destination = Path.Combine(_userPacksRoot, transaction.PackId);
                if (transaction.Mode == "install")
                {
                    if (recordedHashes.TryGetValue(transaction.PackId, out var hash) && hash == transaction.NewHash)
                    {
                        if (!Directory.Exists(destination) || HashDirectory(destination) != hash)
                            throw new InvalidDataException("The published pack generation is missing or changed.");
                        TryFinishTransaction(transaction);
                    }
                    else RollbackInstall(transaction);
                }
                else if (recordedHashes.ContainsKey(transaction.PackId))
                {
                    RestoreRemoved(transaction);
                    DeleteTransaction(transaction);
                }
                else TryFinishTransaction(transaction);
            }
        }, cancellationToken);

    private string TransactionJournalPath(string token) => Path.Combine(_userPacksRoot, StagingFolderName, token + ".transaction.json");
    private string TransactionPayloadPath(PackTransaction transaction) => Path.Combine(_userPacksRoot, StagingFolderName, transaction.Token + (transaction.Mode == "install" ? ".previous" : ".removed"));

    private void WriteTransaction(PackTransaction transaction)
    {
        EnsureManagedBoundary();
        var path = TransactionJournalPath(transaction.Token);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        EnsureManagedBoundary();
        foreach (var journal in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.transaction.json"))
        {
            var pending = ReadTransaction(Path.GetFileName(journal)[..^".transaction.json".Length]);
            if (pending.PackId == transaction.PackId)
                throw new InvalidDataException("This pack has an unfinished operation. Restart to recover it before changing it again.");
        }
        WriteJournalAtomically(transaction, replace: false);
    }

    private void WriteJournalAtomically(PackTransaction transaction, bool replace)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(transaction);
        if (bytes.Length > MaxJournalBytes) throw new InvalidDataException("The pack cleanup receipt exceeds its size limit.");
        var path = TransactionJournalPath(transaction.Token);
        var temporary = path + ".partial." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: replace);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void DeleteTransaction(PackTransaction transaction) => DeleteJournal(transaction.Token);

    private void DeleteJournal(string token)
    {
        try { File.Delete(TransactionJournalPath(token)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("A settled presentation-pack journal will be retried at startup: {0}", exception.GetType().Name);
        }
    }

    private PackTransaction ReadTransaction(string token)
    {
        EnsureManagedBoundary();
        if (!Guid.TryParseExact(token, "N", out _)) throw new InvalidDataException("The pack journal id is invalid.");
        var path = TransactionJournalPath(token);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > MaxJournalBytes)
            throw new InvalidDataException("The pack journal is invalid.");
        var transaction = JsonSerializer.Deserialize<PackTransaction>(File.ReadAllText(path));
        if (transaction is null || transaction.Token != token || !PackValidator.IsValidId(transaction.PackId)
            || transaction.PackId.StartsWith("builtin.", StringComparison.Ordinal) || transaction.Mode is not ("install" or "remove")
            || !IsHash(transaction.NewHash) || (transaction.HadPrevious && !IsHash(transaction.PreviousHash)))
            throw new InvalidDataException("The pack journal is invalid.");
        if (transaction.CleanupFiles is not null)
        {
            if (transaction.CleanupDirectories is null || transaction.CleanupFiles.Length > PackValidator.MaxFiles
                || transaction.CleanupDirectories.Length > PackValidator.MaxFiles * 2
                || transaction.CleanupFiles.Any(file => file is null || !PackValidator.IsSafeRelativePath(file.Path) || !IsHash(file.Sha256))
                || transaction.CleanupDirectories.Any(directory => !PackValidator.IsSafeRelativePath(directory))
                || transaction.CleanupFiles.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != transaction.CleanupFiles.Length
                || transaction.CleanupDirectories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != transaction.CleanupDirectories.Length)
                throw new InvalidDataException("The pack cleanup inventory is invalid.");
        }
        else if (transaction.CleanupDirectories is not null) throw new InvalidDataException("The pack cleanup inventory is incomplete.");
        return transaction;
    }

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private void EnsureManagedBoundary()
    {
        foreach (var path in new[] { _userPacksRoot, Path.Combine(_userPacksRoot, StagingFolderName) })
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Pack storage cannot be a link or reparse point.");
    }

    private static void RequireHash(string path, string expected)
    {
        if (!Directory.Exists(path) || HashDirectory(path) != expected)
            throw new InvalidDataException("The pack recovery generation is missing or changed; it has been preserved for review.");
    }

    private void RestoreRemoved(PackTransaction transaction)
    {
        var destination = Path.Combine(_userPacksRoot, transaction.PackId);
        var removed = TransactionPayloadPath(transaction);
        if (Directory.Exists(removed))
        {
            RequireHash(removed, transaction.NewHash);
            if (Directory.Exists(destination))
                throw new InvalidDataException("Pack removal recovery found conflicting directories; both were preserved.");
            Directory.Move(removed, destination);
        }
        else RequireHash(destination, transaction.NewHash);
    }

    private void TryFinishTransaction(PackTransaction transaction)
    {
        try { FinishTransaction(transaction); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Trace.TraceWarning("A committed pack transaction retains its recovery journal: {0}", exception.GetType().Name);
        }
    }

    private void FinishTransaction(PackTransaction transaction)
    {
        EnsureManagedBoundary();
        var payload = TransactionPayloadPath(transaction);
        if (File.Exists(payload) && !Directory.Exists(payload))
            throw new InvalidDataException("The cleanup directory was replaced by a file; it was preserved.");
        if (Directory.Exists(payload))
        {
            if (transaction.CleanupFiles is null)
            {
                RequireHash(payload, transaction.PreviousHash ?? transaction.NewHash);
                var (files, directories) = ReadCleanupTree(payload);
                transaction = transaction with
                {
                    CleanupFiles = files.Select(file => new CleanupFile(Path.GetRelativePath(payload, file).Replace('\\', '/'), HashFile(file))).ToArray(),
                    CleanupDirectories = directories.Select(directory => Path.GetRelativePath(payload, directory).Replace('\\', '/')).ToArray(),
                };
                if (transaction.CleanupFiles.Any(file => !PackValidator.IsSafeRelativePath(file.Path))
                    || transaction.CleanupDirectories.Any(directory => !PackValidator.IsSafeRelativePath(directory)))
                    throw new InvalidDataException("Cleanup cannot record unsafe relative paths.");
                RequireHash(payload, transaction.PreviousHash ?? transaction.NewHash);
                // Persist the complete inventory before deleting the first member. Missing members on
                // retry are expected; additions, changed bytes and links are preserved for review.
                WriteJournalAtomically(transaction, replace: true);
            }
            var (remainingFiles, remainingDirectories) = ReadCleanupTree(payload);
            var expectedFiles = transaction.CleanupFiles!.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            var expectedDirectories = transaction.CleanupDirectories!.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in remainingFiles)
            {
                var relative = Path.GetRelativePath(payload, file).Replace('\\', '/');
                if (!expectedFiles.TryGetValue(relative, out var expected) || HashFile(file) != expected.Sha256)
                    throw new InvalidDataException("The pack cleanup files changed; remaining bytes were preserved.");
            }
            if (remainingDirectories.Any(directory => !expectedDirectories.Contains(Path.GetRelativePath(payload, directory).Replace('\\', '/'))))
                throw new InvalidDataException("The pack cleanup directories changed; remaining bytes were preserved.");
            foreach (var file in remainingFiles.Order(StringComparer.Ordinal))
            {
                var expected = expectedFiles[Path.GetRelativePath(payload, file).Replace('\\', '/')];
                if (HashFile(file) != expected.Sha256) throw new InvalidDataException("A cleanup file changed and was preserved.");
                File.Delete(file);
            }
            foreach (var directory in remainingDirectories.OrderByDescending(directory => directory.Length))
            {
                RejectCleanupLink(directory);
                Directory.Delete(directory, recursive: false);
            }
            RejectCleanupLink(payload);
            Directory.Delete(payload, recursive: false);
        }
        DeleteTransaction(transaction);
    }

    private static string HashFile(string path)
    {
        RejectCleanupLink(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void RejectCleanupLink(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Pack cleanup cannot follow links or reparse points.");
    }

    private static (List<string> Files, List<string> Directories) ReadCleanupTree(string root)
    {
        var files = new List<string>();
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            RejectCleanupLink(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectCleanupLink(entry);
                if (File.GetAttributes(entry).HasFlag(FileAttributes.Directory))
                {
                    directories.Add(entry);
                    pending.Push(entry);
                }
                else files.Add(entry);
                if (files.Count > PackValidator.MaxFiles || files.Count + directories.Count > PackValidator.MaxFiles * 2)
                    throw new InvalidDataException("The cleanup generation contains too many entries.");
            }
        }
        return (files, directories);
    }

    private void RollbackInstall(PackTransaction transaction)
    {
        EnsureManagedBoundary();
        var destination = Path.Combine(_userPacksRoot, transaction.PackId);
        var backup = TransactionPayloadPath(transaction);
        if (Directory.Exists(backup))
        {
            RequireHash(backup, transaction.PreviousHash!);
            if (Directory.Exists(destination))
            {
                RequireHash(destination, transaction.NewHash);
                Directory.Delete(destination, recursive: true);
            }
            Directory.Move(backup, destination);
        }
        else if (transaction.HadPrevious) RequireHash(destination, transaction.PreviousHash!);
        else if (Directory.Exists(destination))
        {
            RequireHash(destination, transaction.NewHash);
            Directory.Delete(destination, recursive: true);
        }
        DeleteTransaction(transaction);
    }

    private sealed record PackTransaction(string PackId, string Token, string Mode, string NewHash, bool HadPrevious,
        string? PreviousHash, CleanupFile[]? CleanupFiles = null, string[]? CleanupDirectories = null);
    private sealed record CleanupFile(string Path, string Sha256);

    private static bool MoveDirectory(string source, string destination)
    {
        Directory.Move(source, destination);
        return true;
    }

    // ----------------------------------------------------------------- asset store

    public PackAssetEntry? GetAsset(string packId, string assetId)
    {
        lock (_sync)
        {
            return _packs.TryGetValue(packId, out var pack)
                ? pack.Manifest.Assets.FirstOrDefault(asset => asset.Id == assetId)
                : null;
        }
    }

    public string? ResolveAssetPath(string packId, string assetId)
    {
        InstalledPack? pack;
        lock (_sync)
        {
            _packs.TryGetValue(packId, out pack);
        }

        if (pack?.RootPath is null)
        {
            return null;
        }

        var asset = pack.Manifest.Assets.FirstOrDefault(a => a.Id == assetId);
        if (asset is null || !PackValidator.IsSafeRelativePath(asset.Path))
        {
            return null;
        }

        var root = Path.GetFullPath(pack.RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, asset.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
        try
        {
            EnsureManagedBoundary();
            for (var current = full; current is not null && current.Length >= root.TrimEnd(Path.DirectorySeparatorChar).Length; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
            return full;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    // ----------------------------------------------------------------- helpers

    private async Task<IReadOnlyList<PackDiagnostic>> ValidateVideosAsync(
        PresentationPackManifest manifest, string root, CancellationToken cancellationToken)
    {
        var diagnostics = new List<PackDiagnostic>();
        var videos = manifest.Assets.Where(a => a.Kind == PackAssetKind.Video).ToList();
        if (videos.Count == 0) return diagnostics;
        var resolver = _videoTools ?? ExternalToolResolver.ForProduction();
        var probe = resolver.TryResolveExecutablePath(ExternalToolResolver.FfprobeToolId);
        var decode = resolver.TryResolveExecutablePath(ExternalToolResolver.FfmpegToolId);
        if (probe is null || decode is null)
            return [new(PackDiagnosticCodes.AssetUnreadable, manifest.PackId, "The approved video tools are unavailable; video packs cannot be validated.")];
        var launcher = new BoundedProcessLauncher(TimeSpan.FromSeconds(10));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        foreach (var asset in videos)
        {
            var path = Path.GetFullPath(Path.Combine(root, asset.Path));
            try
            {
                var result = await launcher.RunAsync(new ProcessRunRequest(probe,
                    ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "mov,matroska,webm", "-print_format", "json", "-show_format", "-show_streams", path],
                    Timeout: TimeSpan.FromSeconds(10), MaxCapturedCharacters: 128 * 1024), budget.Token).ConfigureAwait(false);
                var metadata = VideoMetadataAdapter.ParseJson(result.StandardOutput);
                if (result.TimedOut || result.ExitCode != 0 || metadata.Width is not (> 0 and <= 16384)
                    || metadata.Height is not (> 0 and <= 16384) || string.IsNullOrWhiteSpace(metadata.VideoCodec))
                {
                    diagnostics.Add(new(PackDiagnosticCodes.AssetUnreadable, asset.Id, "The video has no readable, supported video stream."));
                    continue;
                }
                result = await launcher.RunAsync(new ProcessRunRequest(decode,
                    ["-nostdin", "-v", "error", "-max_alloc", "67108864", "-threads", "1", "-protocol_whitelist", "file", "-format_whitelist", "mov,matroska,webm", "-i", path,
                     "-map", "0:v:0", "-an", "-sn", "-dn", "-threads", "1", "-frames:v", "1", "-progress", "pipe:1", "-nostats", "-f", "null", "-"],
                    Timeout: TimeSpan.FromSeconds(10), MaxCapturedCharacters: 16 * 1024), budget.Token).ConfigureAwait(false);
                if (result.TimedOut || result.ExitCode != 0 || !result.StandardOutput.Split('\n').Any(line => line.Trim() == "frame=1"))
                    diagnostics.Add(new(PackDiagnosticCodes.AssetUnreadable, asset.Id, "The video's first frame cannot be decoded."));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                diagnostics.Add(new(PackDiagnosticCodes.AssetUnreadable, asset.Id, "Video validation exceeded the pack time budget."));
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                diagnostics.Add(new(PackDiagnosticCodes.AssetUnreadable, asset.Id, "Video validation could not complete: " + exception.Message));
            }
        }
        return diagnostics;
    }

    private static void CopyDirectorySafe(string source, string destination)
    {
        var sourceRoot = Path.GetFullPath(source);
        var count = 0;
        long bytes = 0;
        foreach (var file in PackValidator.EnumerateFilesWithoutLinks(sourceRoot))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Packs cannot contain links.");
            }

            if (++count > PackValidator.MaxFiles || (bytes += info.Length) > MaxArchiveUncompressedBytes)
            {
                throw new InvalidDataException("The pack is too large.");
            }

            var relative = Path.GetRelativePath(sourceRoot, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static string? ExtractArchiveSafe(string archivePath, string destination)
    {
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > PackValidator.MaxFiles)
        {
            return "The archive contains too many files.";
        }

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            total += entry.Length;
            if (total > MaxArchiveUncompressedBytes)
            {
                return "The archive expands beyond the allowed pack size.";
            }

            if (!PackValidator.IsSafeRelativePath(entry.FullName.TrimEnd('/')))
                return "The archive contains an invalid relative path.";
            var target = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                return "The archive contains a path that escapes the pack.";
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (PackValidator.ForbiddenExtensions.Contains(Path.GetExtension(entry.FullName)))
            {
                return $"'{entry.FullName}' is executable or script content and is not allowed in a pack.";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }

        return null;
    }

    private static void TryDeleteStaging(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Presentation pack staging folder could not be removed: {0}", exception.GetType().Name);
        }
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string HashDirectory(string directory)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in PackValidator.EnumerateFilesWithoutLinks(directory).Order(StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, file).Replace('\\', '/')));
            using var stream = File.OpenRead(file);
            sha.AppendData(SHA256.HashData(stream));
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}

internal sealed record PackRemovalStage(string PackId, string OriginalPath, string StagedPath, string Token);
