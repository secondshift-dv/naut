using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neuterradise.App.SystemServices.Storage;

internal sealed record VaultMoveFileEntry(
    string RelativePath,
    long Length,
    string Sha256,
    long LastWriteUtcTicks);

internal sealed record VaultMoveInventory(
    int SchemaVersion,
    Guid OperationId,
    string SourceRoot,
    string TargetRoot,
    IReadOnlyList<string> Directories,
    IReadOnlyList<VaultMoveFileEntry> Files,
    long TotalBytes,
    string TreeSha256);

internal sealed record VaultMoveCleanupResult(
    bool Succeeded,
    string? ErrorCode,
    string? SafeErrorDetail)
{
    public static VaultMoveCleanupResult Success() => new(true, null, null);
    public static VaultMoveCleanupResult Failed(string code, string detail) => new(false, code, detail);
}

internal sealed class VaultMoveEngine
{
    private const int InventorySchemaVersion = 1;
    private const int CopyBufferSize = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    private readonly AppStatePaths _appState;

    public VaultMoveEngine(AppStatePaths appState) =>
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));

    public long EstimateSourceBytes(string sourceRoot)
    {
        var normalizedRoot = RootPathRules.NormalizeRoot(sourceRoot, nameof(sourceRoot));
        var lockPath = new VaultPaths(normalizedRoot).LockPath;
        long totalBytes = 0;
        var pending = new Stack<string>();
        pending.Push(normalizedRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Vault Move refuses reparse-point content: {entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else if (!string.Equals(entry, lockPath, StringComparison.OrdinalIgnoreCase))
                {
                    totalBytes = checked(totalBytes + new FileInfo(entry).Length);
                }
            }
        }

        return totalBytes;
    }

    public long EstimateExistingStagingBytes(VaultTransitionRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.StagingRoot) || !Directory.Exists(record.StagingRoot))
        {
            return 0;
        }

        long totalBytes = 0;
        var pending = new Stack<string>();
        pending.Push(record.StagingRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Vault Move refuses reparse-point staging content: {entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    totalBytes = checked(totalBytes + new FileInfo(entry).Length);
                }
            }
        }

        return totalBytes;
    }

    public string GetCleanupRoot(VaultTransitionRecord record)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(record.SourceRoot))
            ?? throw new InvalidOperationException("Vault source root has no parent.");
        return Path.Combine(parent, $".naut-move-{record.OperationId:N}.source-cleanup");
    }

    public async Task<VaultMoveInventory> GetOrCreateSourceInventoryAsync(
        VaultTransitionRecord record,
        IProgress<VaultMoveProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existing = await TryLoadInventoryAsync(record.OperationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            ValidateInventoryBinding(existing, record);
            var current = await BuildInventoryAsync(
                record.SourceRoot,
                record.OperationId,
                record.TargetRoot,
                progress,
                "Verifying source",
                cancellationToken).ConfigureAwait(false);
            if (!Equivalent(existing, current))
            {
                throw new IOException(
                    "The source Vault changed after Move started. The old Vault remains canonical and automatic Move was stopped.");
            }

            return existing;
        }

        var inventory = await BuildInventoryAsync(
            record.SourceRoot,
            record.OperationId,
            record.TargetRoot,
            progress,
            "Inventory",
            cancellationToken).ConfigureAwait(false);
        await WriteInventoryAsync(inventory, cancellationToken).ConfigureAwait(false);
        return inventory;
    }

    public async Task<VaultMoveInventory?> TryLoadInventoryAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var path = _appState.GetVaultTransitionInventoryFilePath(operationId);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var inventory = JsonSerializer.Deserialize<VaultMoveInventory>(bytes, JsonOptions)
            ?? throw new JsonException("Vault Move inventory is empty.");
        if (inventory.SchemaVersion != InventorySchemaVersion || inventory.OperationId != operationId)
        {
            throw new JsonException("Vault Move inventory has an unsupported schema or operation id.");
        }

        ValidateInventory(inventory);
        return inventory;
    }

    public async Task CopyAndVerifyStagingAsync(
        VaultTransitionRecord record,
        VaultMoveInventory inventory,
        IProgress<VaultMoveProgress>? progress,
        Func<VaultMoveProgress, CancellationToken, Task>? persistProgress,
        CancellationToken cancellationToken)
    {
        if (record.MoveMode != VaultMoveMode.CrossVolume || string.IsNullOrWhiteSpace(record.StagingRoot))
        {
            throw new InvalidOperationException("Cross-volume staging was requested without a cross-volume Move record.");
        }

        ValidateInventoryBinding(inventory, record);
        var stagingRoot = record.StagingRoot;
        Directory.CreateDirectory(stagingRoot);

        foreach (var relativeDirectory in inventory.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(ToPlatformPath(stagingRoot, relativeDirectory));
        }

        long completedBytes = 0;
        var completedFiles = 0;
        long lastPersistedBytes = 0;
        var lastPersistedFiles = 0;
        foreach (var file in inventory.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = ToPlatformPath(record.SourceRoot, file.RelativePath);
            var destinationPath = ToPlatformPath(stagingRoot, file.RelativePath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            var tempPath = destinationPath + $".nautpartial-{record.OperationId:N}";
            TryDeleteFile(tempPath);

            var alreadyVerified = false;
            if (File.Exists(destinationPath))
            {
                var info = new FileInfo(destinationPath);
                if (info.Length == file.Length)
                {
                    var existingHash = await HashFileAsync(destinationPath, cancellationToken).ConfigureAwait(false);
                    alreadyVerified = string.Equals(existingHash, file.Sha256, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (!alreadyVerified)
            {
                await CopyFileAsync(sourcePath, tempPath, cancellationToken).ConfigureAwait(false);
                var copiedHash = await HashFileAsync(tempPath, cancellationToken).ConfigureAwait(false);
                var copiedLength = new FileInfo(tempPath).Length;
                if (copiedLength != file.Length
                    || !string.Equals(copiedHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"Copied Vault file failed verification: {file.RelativePath}");
                }

                File.SetLastWriteTimeUtc(tempPath, new DateTime(file.LastWriteUtcTicks, DateTimeKind.Utc));
                File.Move(tempPath, destinationPath, overwrite: true);
            }

            completedFiles++;
            completedBytes += file.Length;
            var moveProgress = new VaultMoveProgress(
                "Copying",
                completedFiles,
                inventory.Files.Count,
                completedBytes,
                inventory.TotalBytes,
                file.RelativePath);
            progress?.Report(moveProgress);
            if (persistProgress is not null && cancellationToken.IsCancellationRequested)
            {
                await persistProgress(moveProgress, CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            const int checkpointFileInterval = 32;
            const long checkpointByteInterval = 64L * 1024L * 1024L;
            if (persistProgress is not null
                && (completedFiles == inventory.Files.Count
                    || completedFiles - lastPersistedFiles >= checkpointFileInterval
                    || completedBytes - lastPersistedBytes >= checkpointByteInterval))
            {
                await persistProgress(moveProgress, cancellationToken).ConfigureAwait(false);
                lastPersistedFiles = completedFiles;
                lastPersistedBytes = completedBytes;
            }
        }

        var stagedInventory = await BuildInventoryAsync(
            stagingRoot,
            record.OperationId,
            record.TargetRoot,
            progress,
            "Verifying destination",
            cancellationToken).ConfigureAwait(false);
        if (!Equivalent(inventory, stagedInventory))
        {
            throw new IOException(
                "The copied Vault failed deterministic inventory verification. The source Vault remains canonical.");
        }
    }
    public async Task<bool> VerifySourceAsync(
        VaultTransitionRecord record,
        CancellationToken cancellationToken)
    {
        var inventory = await TryLoadInventoryAsync(record.OperationId, cancellationToken).ConfigureAwait(false);
        if (inventory is null || !Directory.Exists(record.SourceRoot))
        {
            return false;
        }

        ValidateInventoryBinding(inventory, record);
        var sourceInventory = await BuildInventoryAsync(
            record.SourceRoot,
            record.OperationId,
            record.TargetRoot,
            progress: null,
            stage: "Verifying source",
            cancellationToken).ConfigureAwait(false);
        return Equivalent(inventory, sourceInventory);
    }

    public async Task<bool> VerifyTargetAsync(
        VaultTransitionRecord record,
        CancellationToken cancellationToken)
    {
        var inventory = await TryLoadInventoryAsync(record.OperationId, cancellationToken).ConfigureAwait(false);
        if (inventory is null || !Directory.Exists(record.TargetRoot))
        {
            return false;
        }

        ValidateInventoryBinding(inventory, record);
        var targetInventory = await BuildInventoryAsync(
            record.TargetRoot,
            record.OperationId,
            record.TargetRoot,
            progress: null,
            stage: "Verifying destination",
            cancellationToken).ConfigureAwait(false);
        return Equivalent(inventory, targetInventory);
    }

    public async Task<VaultMoveCleanupResult> CleanupSourceAfterSuccessfulBootstrapAsync(
        VaultTransitionRecord record,
        CancellationToken cancellationToken)
    {
        var inventory = await TryLoadInventoryAsync(record.OperationId, cancellationToken).ConfigureAwait(false);
        if (inventory is null)
        {
            return VaultMoveCleanupResult.Failed(
                "VAULT_MOVE_INVENTORY_MISSING",
                "The verified Move inventory is missing, so the old Vault was preserved.");
        }

        ValidateInventoryBinding(inventory, record);
        var cleanupRoot = GetCleanupRoot(record);
        try
        {
            if (Directory.Exists(record.SourceRoot))
            {
                if (Directory.Exists(cleanupRoot) || File.Exists(cleanupRoot))
                {
                    return VaultMoveCleanupResult.Failed(
                        "VAULT_MOVE_CLEANUP_AMBIGUOUS",
                        "Both the old Vault and a pending cleanup folder exist. Automatic cleanup stopped.");
                }

                var currentSource = await BuildInventoryAsync(
                    record.SourceRoot,
                    record.OperationId,
                    record.TargetRoot,
                    progress: null,
                    stage: "Verifying old source",
                    cancellationToken).ConfigureAwait(false);
                if (!Equivalent(inventory, currentSource))
                {
                    return VaultMoveCleanupResult.Failed(
                        "VAULT_MOVE_SOURCE_CHANGED",
                        "The old Vault changed after it was copied. Automatic cleanup stopped and all old bytes were preserved.");
                }

                Directory.Move(record.SourceRoot, cleanupRoot);
            }

            if (!Directory.Exists(cleanupRoot))
            {
                return VaultMoveCleanupResult.Success();
            }

            if (!await CleanupTreeIsSafeSubsetAsync(
                cleanupRoot,
                inventory,
                cancellationToken).ConfigureAwait(false))
            {
                return VaultMoveCleanupResult.Failed(
                    "VAULT_MOVE_SOURCE_CHANGED",
                    "The old Vault cleanup area contains changed or unexpected bytes. Automatic deletion stopped.");
            }

            Directory.Delete(cleanupRoot, recursive: true);
            return VaultMoveCleanupResult.Success();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return VaultMoveCleanupResult.Failed(
                "VAULT_MOVE_SOURCE_CLEANUP_FAILED",
                exception.Message);
        }
    }

    private async Task<bool> CleanupTreeIsSafeSubsetAsync(
        string cleanupRoot,
        VaultMoveInventory inventory,
        CancellationToken cancellationToken)
    {
        var expectedFiles = inventory.Files.ToDictionary(
            entry => entry.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        var expectedDirectories = inventory.Directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lockRelative = NormalizeRelative(Path.GetRelativePath(
            cleanupRoot,
            new VaultPaths(cleanupRoot).LockPath));
        var pending = new Stack<string>();
        pending.Push(cleanupRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                var relative = NormalizeRelative(Path.GetRelativePath(cleanupRoot, entry));
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!expectedDirectories.Contains(relative))
                    {
                        return false;
                    }

                    pending.Push(entry);
                    continue;
                }

                if (string.Equals(relative, lockRelative, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!expectedFiles.TryGetValue(relative, out var expected))
                {
                    return false;
                }

                var info = new FileInfo(entry);
                if (info.Length != expected.Length)
                {
                    return false;
                }

                var hash = await HashFileAsync(entry, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }
    public void DeleteOperationState(Guid operationId)
    {
        var operationPath = _appState.GetVaultTransitionOperationPath(operationId);
        if (Directory.Exists(operationPath))
        {
            Directory.Delete(operationPath, recursive: true);
        }
    }

    private async Task<VaultMoveInventory> BuildInventoryAsync(
        string root,
        Guid operationId,
        string targetRoot,
        IProgress<VaultMoveProgress>? progress,
        string stage,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Vault tree does not exist: {root}");
        }

        var normalizedRoot = RootPathRules.NormalizeRoot(root, nameof(root));
        var lockRelative = NormalizeRelative(Path.GetRelativePath(
            normalizedRoot,
            new VaultPaths(normalizedRoot).LockPath));

        var directories = new List<string>();
        var filePaths = new List<string>();
        var pending = new Stack<string>();
        pending.Push(normalizedRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Vault Move refuses reparse-point content: {entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(NormalizeRelative(Path.GetRelativePath(normalizedRoot, entry)));
                    pending.Push(entry);
                }
                else
                {
                    var relative = NormalizeRelative(Path.GetRelativePath(normalizedRoot, entry));
                    if (!string.Equals(relative, lockRelative, StringComparison.OrdinalIgnoreCase))
                    {
                        filePaths.Add(relative);
                    }
                }
            }
        }

        directories.Sort(StringComparer.OrdinalIgnoreCase);
        filePaths.Sort(StringComparer.OrdinalIgnoreCase);

        var files = new List<VaultMoveFileEntry>(filePaths.Count);
        long totalBytes = 0;
        for (var index = 0; index < filePaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = filePaths[index];
            var absolute = ToPlatformPath(normalizedRoot, relative);
            var info = new FileInfo(absolute);
            var hash = await HashFileAsync(absolute, cancellationToken).ConfigureAwait(false);
            files.Add(new VaultMoveFileEntry(relative, info.Length, hash, info.LastWriteTimeUtc.Ticks));
            totalBytes = checked(totalBytes + info.Length);
            progress?.Report(new VaultMoveProgress(
                stage,
                index + 1,
                filePaths.Count,
                totalBytes,
                0,
                relative));
        }

        var treeSha256 = ComputeTreeHash(directories, files);
        return new VaultMoveInventory(
            InventorySchemaVersion,
            operationId,
            normalizedRoot,
            RootPathRules.NormalizeRoot(targetRoot, nameof(targetRoot)),
            directories,
            files,
            totalBytes,
            treeSha256);
    }
    private async Task WriteInventoryAsync(
        VaultMoveInventory inventory,
        CancellationToken cancellationToken)
    {
        var path = _appState.GetVaultTransitionInventoryFilePath(inventory.OperationId);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Vault Move inventory path has no directory.");
        Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(inventory, JsonOptions);
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _ = JsonSerializer.Deserialize<VaultMoveInventory>(
                await File.ReadAllBytesAsync(tempPath, cancellationToken).ConfigureAwait(false),
                JsonOptions)
                ?? throw new JsonException("Vault Move inventory verification failed.");
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, CopyBufferSize, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeTreeHash(
        IReadOnlyList<string> directories,
        IReadOnlyList<VaultMoveFileEntry> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var directory in directories)
        {
            AppendHashLine(hash, $"D\0{directory}\n");
        }

        foreach (var file in files)
        {
            AppendHashLine(hash, $"F\0{file.RelativePath}\0{file.Length}\0{file.Sha256}\n");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendHashLine(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static void ValidateInventory(VaultMoveInventory inventory)
    {
        if (string.IsNullOrWhiteSpace(inventory.SourceRoot)
            || string.IsNullOrWhiteSpace(inventory.TargetRoot)
            || string.IsNullOrWhiteSpace(inventory.TreeSha256)
            || inventory.Directories is null
            || inventory.Files is null
            || inventory.TotalBytes < 0)
        {
            throw new JsonException("Vault Move inventory contains invalid root or total metadata.");
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in inventory.Directories)
        {
            ValidateRelativeInventoryPath(directory);
            if (!seenPaths.Add(directory))
            {
                throw new JsonException($"Vault Move inventory contains duplicate path '{directory}'.");
            }
        }

        long totalBytes = 0;
        foreach (var file in inventory.Files)
        {
            if (file is null)
            {
                throw new JsonException("Vault Move inventory contains a null file entry.");
            }

            ValidateRelativeInventoryPath(file.RelativePath);
            if (!seenPaths.Add(file.RelativePath))
            {
                throw new JsonException($"Vault Move inventory contains duplicate path '{file.RelativePath}'.");
            }

            if (file.Length < 0
                || file.LastWriteUtcTicks < DateTime.MinValue.Ticks
                || file.LastWriteUtcTicks > DateTime.MaxValue.Ticks
                || string.IsNullOrWhiteSpace(file.Sha256)
                || file.Sha256.Length != 64
                || file.Sha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new JsonException($"Vault Move inventory contains invalid file metadata for '{file.RelativePath}'.");
            }

            try
            {
                totalBytes = checked(totalBytes + file.Length);
            }
            catch (OverflowException exception)
            {
                throw new JsonException("Vault Move inventory byte total overflowed.", exception);
            }
        }

        if (totalBytes != inventory.TotalBytes)
        {
            throw new JsonException("Vault Move inventory byte total does not match its file entries.");
        }

        var computedTreeHash = ComputeTreeHash(inventory.Directories, inventory.Files);
        if (!string.Equals(computedTreeHash, inventory.TreeSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException("Vault Move inventory tree hash is invalid.");
        }
    }

    private static void ValidateInventoryBinding(
        VaultMoveInventory inventory,
        VaultTransitionRecord record)
    {
        try
        {
            if (!RootPathRules.AreSameRoot(inventory.SourceRoot, record.SourceRoot)
                || !RootPathRules.AreSameRoot(inventory.TargetRoot, record.TargetRoot))
            {
                throw new JsonException("Vault Move inventory does not belong to the active source/target roots.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("Vault Move inventory contains an invalid root binding.", exception);
        }
    }

    private static void ValidateRelativeInventoryPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.Contains('\\')
            || relativePath.StartsWith("/", StringComparison.Ordinal)
            || relativePath.EndsWith("/", StringComparison.Ordinal))
        {
            throw new JsonException($"Vault Move inventory contains invalid relative path '{relativePath}'.");
        }

        var segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new JsonException($"Vault Move inventory contains traversal or empty path segments in '{relativePath}'.");
        }
    }

    private static bool Equivalent(VaultMoveInventory expected, VaultMoveInventory actual) =>
        expected.Files.Count == actual.Files.Count
        && expected.Directories.Count == actual.Directories.Count
        && expected.TotalBytes == actual.TotalBytes
        && string.Equals(expected.TreeSha256, actual.TreeSha256, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelative(string relativePath) =>
        relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string ToPlatformPath(string root, string relativePath) =>
        RootPathRules.ResolveContainedPath(root, root, relativePath, nameof(relativePath));

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

}
