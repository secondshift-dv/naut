using System.IO;

namespace Neuterradise.App.Import.Intake;

public static class SourceEnumerator
{
    public static SourceItemDescriptor DescribeFile(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
            {
                return new SourceItemDescriptor(
                    fullPath,
                    Exists: false,
                    IsReadable: false,
                    ByteLength: 0,
                    LastWriteUtc: null,
                    Classification: null);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new SourceItemDescriptor(
                fullPath,
                Exists: false,
                IsReadable: false,
                ByteLength: 0,
                LastWriteUtc: null,
                Classification: null);
        }

        var isReadable = false;
        long length = 0;
        DateTimeOffset? lastWriteUtc = null;

        try
        {
            length = fileInfo.Length;
            lastWriteUtc = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);

            using (var stream = fileInfo.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                isReadable = stream.CanRead;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // The file is present but locked, denied or otherwise unreadable. Report that truthfully
            // rather than inventing a byte length or a write timestamp.
            isReadable = false;
            lastWriteUtc = null;
        }

        var classification = SupportedMediaClassifier.Classify(fileInfo.Name);

        return new SourceItemDescriptor(
            fullPath,
            Exists: true,
            IsReadable: isReadable,
            ByteLength: length,
            LastWriteUtc: lastWriteUtc,
            Classification: classification);
    }

    public static IReadOnlyList<SourceItemDescriptor> EnumerateDirectory(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        cancellationToken.ThrowIfCancellationRequested();

        string fullDir;
        try
        {
            fullDir = Path.GetFullPath(directoryPath);
            if (!Directory.Exists(fullDir))
            {
                return Array.Empty<SourceItemDescriptor>();
            }

            var dirInfo = new DirectoryInfo(fullDir);
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // Unsafe reparse point / symlink / junction at directory root is not followed
                return Array.Empty<SourceItemDescriptor>();
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return Array.Empty<SourceItemDescriptor>();
        }

        var descriptors = new List<SourceItemDescriptor>();

        // DirectoryInfo enumeration returns the actual filesystem entry names. Reparse points are not
        // followed, so the visited set is cycle protection rather than an identity authority. Keep it
        // ordinal: a case-sensitive directory may legally expose both Foo and foo as distinct subtrees.
        var visitedDirectories = new HashSet<string>(StringComparer.Ordinal);

        EnumerateDirectoryRecursive(fullDir, descriptors, visitedDirectories, cancellationToken);

        descriptors.Sort((a, b) =>
        {
            var cmp = string.Compare(a.FullSourcePath, b.FullSourcePath, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a.FullSourcePath, b.FullSourcePath, StringComparison.Ordinal);
        });

        return descriptors;
    }

    private static void EnumerateDirectoryRecursive(
        string directoryPath,
        List<SourceItemDescriptor> results,
        HashSet<string> visitedDirectories,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string fullDir;
        DirectoryInfo dirInfo;
        try
        {
            fullDir = Path.GetFullPath(directoryPath);
            if (!visitedDirectories.Add(fullDir))
            {
                return;
            }

            dirInfo = new DirectoryInfo(fullDir);
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            // Presentation Packs are customization data, never library media: a frame PNG, backdrop
            // video or font inside a pack must not become an imported Media item by accident.
            if (IsPresentationPackDirectory(dirInfo))
            {
                return;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return;
        }

        try
        {
            foreach (var file in dirInfo.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // Skip reparse points to prevent unsafe traversal
                        continue;
                    }

                    results.Add(DescribeFile(file.FullName));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    results.Add(new SourceItemDescriptor(
                        file.FullName,
                        Exists: true,
                        IsReadable: false,
                        ByteLength: 0,
                        LastWriteUtc: null,
                        Classification: SupportedMediaClassifier.Classify(file.Name)));
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or DirectoryNotFoundException)
        {
            // Directory unreadable or denied; proceed safely
        }

        try
        {
            foreach (var subDir in dirInfo.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (subDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    EnumerateDirectoryRecursive(subDir.FullName, results, visitedDirectories, cancellationToken);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or DirectoryNotFoundException)
                {
                    continue;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or DirectoryNotFoundException)
        {
            // Subdirectories inaccessible; return safely
        }
    }

    /// <summary>
    /// True for a Presentation Pack root (a <c>pack.json</c> manifest declaring a <c>packId</c>) and for
    /// anything inside a Vault <c>_system/presentation-packs</c> tree.
    /// </summary>
    internal static bool IsPresentationPackDirectory(DirectoryInfo directory)
    {
        var parent = directory.Parent;
        if (string.Equals(directory.Name, "presentation-packs", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parent?.Name, "_system", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        for (var current = directory; current is not null; current = current.Parent)
        {
            if (string.Equals(current.Name, "presentation-packs", StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.Parent?.Name, "_system", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var manifest = Path.Combine(directory.FullName, "pack.json");
        if (!File.Exists(manifest))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(manifest);
            if (info.Length > 2 * 1024 * 1024)
            {
                return false;
            }

            using var stream = info.OpenRead();
            using var document = System.Text.Json.JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("packId", out _)
                && document.RootElement.TryGetProperty("definitions", out _);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
