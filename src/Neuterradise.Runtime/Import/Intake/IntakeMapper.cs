using System.IO;

namespace Neuterradise.App.Import.Intake;

public sealed record MappedUnitPlan(
    Guid ImportUnitId,
    string SourceKind,
    string SourceDisplayName,
    string? SourcePathOrReference,
    IReadOnlyList<SourceItemDescriptor> Items);

public sealed record IntakeSessionPlan(
    Guid ImportSessionId,
    IntakeOrigin Origin,
    Guid? SuggestedDestinationProfileId,
    IReadOnlyList<MappedUnitPlan> Units);

public static class IntakeMapper
{
    public static IntakeSessionPlan MapIntake(
        ImportIntakeRequest request,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var actualSessionId = sessionId ?? Guid.NewGuid();

        // Canonicalize the casing of existing path segments from the filesystem, then compare
        // ordinally. On ordinary case-insensitive Windows this collapses spelling aliases such as
        // C:\Media\A.jpg vs C:\MEDIA\a.JPG back to one existing entry. In a case-sensitive
        // directory, Foo and foo enumerate as distinct actual entries and remain distinct. This
        // keeps path identity truthful without globally forcing either case model.
        var uniquePaths = new HashSet<string>(StringComparer.Ordinal);
        var candidateDirectories = new List<string>();
        var candidateFiles = new List<string>();

        foreach (var entry in request.SourceEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(entry);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            var isDirectory = Directory.Exists(fullPath);
            var isFile = !isDirectory && File.Exists(fullPath);
            if (!isDirectory && !isFile)
            {
                continue;
            }

            fullPath = CanonicalizeExistingPathCasing(fullPath);
            if (!uniquePaths.Add(fullPath))
            {
                continue;
            }

            if (isDirectory)
            {
                candidateDirectories.Add(fullPath);
            }
            else
            {
                candidateFiles.Add(fullPath);
            }
        }

        // Deduplicate overlapping directories:
        // When both parent and subfolder are selected, the parent top-level folder already
        // enumerates the subfolder. Each independent top-level folder remains a separate Import Unit.
        candidateDirectories.Sort((a, b) => a.Length.CompareTo(b.Length));
        var topLevelDirectories = new List<string>();
        foreach (var dir in candidateDirectories)
        {
            var normalizedDir = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var isContainedInParent = topLevelDirectories.Any(parent =>
            {
                var normalizedParent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return normalizedDir.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || normalizedDir.StartsWith(normalizedParent + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
            });

            if (!isContainedInParent)
            {
                topLevelDirectories.Add(dir);
            }
        }

        // Sort top-level directories deterministically. This ordering does not define identity.
        topLevelDirectories.Sort((a, b) =>
        {
            var cmp = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a, b, StringComparison.Ordinal);
        });

        var directoryUnits = new List<MappedUnitPlan>();
        foreach (var fullPath in topLevelDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dirName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(dirName))
            {
                dirName = fullPath;
            }

            var items = SourceEnumerator.EnumerateDirectory(fullPath, cancellationToken);
            var unitId = ImportIntakeCoordinator.DeriveDeterministicId(actualSessionId, "unit:dir:" + fullPath);

            // Folder name (even if "Unknown") is strictly treated as source text / display name,
            // never as an implicit System Unknown destination.
            directoryUnits.Add(new MappedUnitPlan(
                ImportUnitId: unitId,
                SourceKind: "DIRECTORY",
                SourceDisplayName: dirName,
                SourcePathOrReference: fullPath,
                Items: items));
        }

        // Filter out loose files that are already inside any of the selected top-level directories.
        // Inputs have already been canonicalized from actual filesystem entries, so ordinal
        // containment collapses ordinary case aliases without erasing case-distinct directory entries.
        var nonOverlappingFiles = new List<string>();
        foreach (var file in candidateFiles)
        {
            var isContainedInDir = topLevelDirectories.Any(dir =>
            {
                var normalizedDir = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return file.StartsWith(normalizedDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || file.StartsWith(normalizedDir + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
            });

            if (!isContainedInDir)
            {
                nonOverlappingFiles.Add(file);
            }
        }

        var looseFiles = new List<SourceItemDescriptor>();
        foreach (var filePath in nonOverlappingFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            looseFiles.Add(SourceEnumerator.DescribeFile(filePath));
        }

        var units = new List<MappedUnitPlan>();

        if (looseFiles.Count > 0)
        {
            looseFiles.Sort((a, b) =>
            {
                var cmp = string.Compare(a.FullSourcePath, b.FullSourcePath, StringComparison.OrdinalIgnoreCase);
                return cmp != 0 ? cmp : string.Compare(a.FullSourcePath, b.FullSourcePath, StringComparison.Ordinal);
            });

            var displayName = looseFiles.Count == 1
                ? Path.GetFileName(looseFiles[0].FullSourcePath)
                : "Loose files";

            var pathOrRef = looseFiles.Count == 1
                ? looseFiles[0].FullSourcePath
                : null;

            var unitId = ImportIntakeCoordinator.DeriveDeterministicId(actualSessionId, "unit:loose_files");

            units.Add(new MappedUnitPlan(
                ImportUnitId: unitId,
                SourceKind: "FILES",
                SourceDisplayName: displayName,
                SourcePathOrReference: pathOrRef,
                Items: looseFiles));
        }

        units.AddRange(directoryUnits);

        // One user action is one import. Picking two folders plus a few loose files used to create
        // several imports that each needed their own decisions and each appeared as a separate row;
        // the user sees a single batch, so the batch is a single unit with every discovered item.
        if (units.Count > 1)
        {
            units = [CombineIntoBatch(actualSessionId, looseFiles, directoryUnits)];
        }

        return new IntakeSessionPlan(
            actualSessionId,
            request.Origin,
            request.SuggestedDestinationProfileId,
            units);
    }

    private static string CanonicalizeExistingPathCasing(string fullPath)
    {
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return fullPath;
            }

            var relative = Path.GetRelativePath(root, fullPath);
            if (relative == ".")
            {
                return Path.GetFullPath(root);
            }

            var current = Path.GetFullPath(root);
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                // Search semantics follow the containing filesystem/directory. The returned entry
                // carries the actual persisted casing. Prefer an exact ordinal name if more than one
                // result is possible, which is the distinguishing case for case-sensitive folders.
                var matches = Directory
                    .EnumerateFileSystemEntries(current, segment, SearchOption.TopDirectoryOnly)
                    .ToList();
                var chosen = matches.FirstOrDefault(path =>
                                 string.Equals(Path.GetFileName(path), segment, StringComparison.Ordinal))
                             ?? matches.FirstOrDefault();
                if (chosen is null)
                {
                    return fullPath;
                }

                current = chosen;
            }

            return current;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Admission still has explicit Exists/IsReadable checks. Failure to recover persisted
            // casing must not invent identity; preserve the normalized caller path as the safe fallback.
            return fullPath;
        }
    }

    private static MappedUnitPlan CombineIntoBatch(
        Guid sessionId,
        IReadOnlyList<SourceItemDescriptor> looseFiles,
        IReadOnlyList<MappedUnitPlan> directoryUnits)
    {
        var items = new List<SourceItemDescriptor>(looseFiles);
        foreach (var directory in directoryUnits)
        {
            items.AddRange(directory.Items);
        }

        string displayName;
        if (directoryUnits.Count == 0)
        {
            displayName = "Loose files";
        }
        else if (directoryUnits.Count == 1)
        {
            displayName = looseFiles.Count == 1
                ? $"{directoryUnits[0].SourceDisplayName} and 1 file"
                : $"{directoryUnits[0].SourceDisplayName} and {looseFiles.Count} files";
        }
        else
        {
            displayName = $"{directoryUnits[0].SourceDisplayName} and {directoryUnits.Count - 1} more";
        }

        return new MappedUnitPlan(
            ImportUnitId: ImportIntakeCoordinator.DeriveDeterministicId(sessionId, "unit:batch"),
            SourceKind: "FILES",
            SourceDisplayName: displayName,
            SourcePathOrReference: null,
            Items: items);
    }
}
