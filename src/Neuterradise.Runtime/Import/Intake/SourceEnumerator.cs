using System.IO;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Import.Intake;

public static class SourceEnumerator
{
    public static SourceItemDescriptor DescribeFile(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        var classification = SupportedMediaClassifier.Classify(fullPath);
        try
        {
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return Failed(fullPath, SourceIntakeIssueKind.Unreadable, classification);
            if (classification is null)
                return Failed(fullPath, SourceIntakeIssueKind.Unsupported, null, exists: true);

            using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var identity = SourceIdentityHelper.ReadIdentity(stream.SafeFileHandle, fullPath);
            if (identity is null)
                return Failed(fullPath, SourceIntakeIssueKind.Unreadable, classification, exists: true);
            return new SourceItemDescriptor(fullPath, true, true, identity.ByteLength,
                new DateTimeOffset(DateTime.FromFileTimeUtc(identity.LastWriteTimeUtcTicks)), classification,
                SourceIdentityJson: SourceIdentityHelper.SerializeIdentity(identity));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return Failed(fullPath, ClassifyFailure(exception), classification);
        }
    }

    private static SourceIntakeIssueKind ClassifyFailure(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => SourceIntakeIssueKind.Missing,
        UnauthorizedAccessException or System.Security.SecurityException => SourceIntakeIssueKind.AccessDenied,
        IOException io when (io.HResult & 0xffff) is 32 or 33 => SourceIntakeIssueKind.Locked,
        _ => SourceIntakeIssueKind.Unreadable,
    };

    private static SourceItemDescriptor Failed(string path, SourceIntakeIssueKind issue,
        Neuterradise.App.Media.MediaType? classification = null, bool exists = false) =>
        new(path, exists, false, 0, null, classification, issue);

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
                return [Failed(directoryPath, SourceIntakeIssueKind.DirectoryUnavailable)];
            }

            var dirInfo = new DirectoryInfo(fullDir);
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || IsWithinPresentationPackDirectory(dirInfo))
            {
                // Unsafe reparse point / symlink / junction or pack root is not followed
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

        var originalProfiles = Path.Combine(fullDir, "profiles");
        if (Directory.Exists(originalProfiles) && File.Exists(Path.Combine(fullDir, "_system", "catalog.db")))
        {
            EnumerateDirectoryRecursive(originalProfiles, results, visitedDirectories, cancellationToken);
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
            results.Add(Failed(directoryPath, SourceIntakeIssueKind.DirectoryUnavailable));
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
            if (!results.Any(item => item.FullSourcePath == directoryPath && item.Issue == SourceIntakeIssueKind.DirectoryUnavailable))
                results.Add(Failed(directoryPath, SourceIntakeIssueKind.DirectoryUnavailable));
        }
    }

    /// <summary>
    /// True for a Presentation Pack root, including the Vault presentation-pack collection.
    /// </summary>
    internal static bool IsWithinPresentationPackDirectory(DirectoryInfo? directory)
    {
        for (var current = directory; current is not null; current = current.Parent)
            if (IsPresentationPackDirectory(current)) return true;
        return false;
    }

    private static bool IsPresentationPackDirectory(DirectoryInfo directory)
    {
        var parent = directory.Parent;
        if (string.Equals(directory.Name, "presentation-packs", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parent?.Name, "_system", StringComparison.OrdinalIgnoreCase))
        {
            return true;
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
