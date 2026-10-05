using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Maintenance;

public sealed class LibraryHealthEvaluator
{
    private readonly CatalogDb _catalog;
    private readonly VaultPaths _paths;
    private readonly ManagedPathPlanner _pathPlanner;
    private readonly TimeProvider _timeProvider;

    public LibraryHealthEvaluator(
        CatalogDb catalog,
        ManagedPathPlanner? pathPlanner = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _paths = catalog.Paths;
        _pathPlanner = pathPlanner ?? new ManagedPathPlanner(_paths.Root);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<HealthFinding>> ScanAsync(
        HealthScanOptions? options = null,
        IProgress<HealthScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunScanAsync(options, progress, cancellationToken).ConfigureAwait(false);
        return result.Findings;
    }

    public async Task<HealthScanResult> RunScanAsync(
        HealthScanOptions? options = null,
        IProgress<HealthScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new HealthScanOptions();
        var startTime = _timeProvider.GetTimestamp();
        var findings = new List<HealthFinding>();
        var itemsScanned = 0;

        if (opts.Mode == HealthScanMode.StartupCritical)
        {
            progress?.Report(new HealthScanProgress("Startup critical authority checks", 0, null, 0));
            var dbFindings = await _catalog.HealthReads.GetDatabaseIntegrityFindingsAsync(cancellationToken)
                .ConfigureAwait(false);

            itemsScanned += dbFindings.Count;
            findings.AddRange(dbFindings);
            findings.AddRange(await ScanArtifactAuthorityAsync(opts, cancellationToken).ConfigureAwait(false));
            progress?.Report(new HealthScanProgress("Startup checks completed", itemsScanned, itemsScanned, findings.Count));

            var elapsedStartup = _timeProvider.GetElapsedTime(startTime);
            return new HealthScanResult(
                findings,
                opts.Mode,
                itemsScanned,
                elapsedStartup,
                _timeProvider.GetUtcNow());
        }

        progress?.Report(new HealthScanProgress("Evaluating database integrity invariants", itemsScanned, null, findings.Count));
        var rawDbFindings = await _catalog.HealthReads.GetDatabaseIntegrityFindingsAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var finding in rawDbFindings)
        {
            if (opts.FocusedProfileId.HasValue && finding.ProfileId != opts.FocusedProfileId)
            {
                continue;
            }

            if (opts.FocusedMediaId.HasValue && finding.MediaId != opts.FocusedMediaId)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(opts.FocusedOperationId) && finding.OperationId != opts.FocusedOperationId)
            {
                continue;
            }

            findings.Add(finding);
        }
        itemsScanned += rawDbFindings.Count;

        findings.AddRange(await ScanArtifactAuthorityAsync(opts, cancellationToken).ConfigureAwait(false));

        progress?.Report(new HealthScanProgress("Scanning profiles and manifests", itemsScanned, null, findings.Count));
        var profiles = await _catalog.HealthReads.GetHealthProfileItemsAsync(opts.FocusedProfileId, cancellationToken)
            .ConfigureAwait(false);

        var knownProfileIds = new HashSet<Guid>();
        var knownProfileFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownProfileTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenProfileTokens = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            itemsScanned++;

            knownProfileIds.Add(profile.ProfileId);
            if (!string.IsNullOrWhiteSpace(profile.StorageToken))
            {
                knownProfileTokens.Add(profile.StorageToken);
                if (seenProfileTokens.TryGetValue(profile.StorageToken, out var existingProfileId) && existingProfileId != profile.ProfileId)
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.StorageTokenConflict,
                        HealthSeverity.Critical,
                        profile.ProfileId,
                        null,
                        null,
                        null,
                        $"Storage token '{profile.StorageToken}' is claimed by multiple profiles ({existingProfileId} and {profile.ProfileId}).",
                        RepairAvailable: false));
                }
                else
                {
                    seenProfileTokens[profile.StorageToken] = profile.ProfileId;
                }
            }

            if (profile.IsTrashed)
            {

                var trashProfileDir = Path.Combine(_paths.TrashProfilesPath, profile.ProfileId.ToString("D").ToLowerInvariant());
                var recoveryManifest = Path.Combine(trashProfileDir, ProfileManifestWriter.ManifestFileName);

                if (!Directory.Exists(trashProfileDir) || !File.Exists(recoveryManifest))
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.TrashStateMismatch,
                        HealthSeverity.Error,
                        profile.ProfileId,
                        null,
                        null,
                        null,
                        $"Profile {profile.ProfileId} is marked TRASHED in catalog but recovery manifest is missing from trash root.",
                        RepairAvailable: false));
                }

                if (!string.IsNullOrWhiteSpace(profile.StorageToken))
                {
                    var planned = _pathPlanner.PlanProfile(
                        profile.ProfileId,
                        profile.DisplayName,
                        new ProfileStorageToken(profile.StorageToken));
                    var folderName = Path.GetFileName(planned.ProfileFolderRelativePath);
                    var activeDir = Path.Combine(_paths.ProfilesPath, folderName);
                    if (Directory.Exists(activeDir))
                    {
                        // Do not report the same physical folder again as an unowned folder later in
                        // the filesystem pass. If recovery is durable and the source tree is empty,
                        // expose the existing conservative empty-directory repair instead.
                        knownProfileFolderNames.Add(folderName);
                        var relative = Path.GetRelativePath(_paths.Root, activeDir).Replace('\\', '/');
                        RetiredProfileDirectory.Authority? authority = null;
                        var empty = false;
                        try
                        {
                            empty = RetiredProfileDirectory.IsEmptyTree(_paths.Root, activeDir);
                            if (empty)
                            {
                                authority = await RetiredProfileDirectory.ReadAuthorityAsync(
                                        _catalog,
                                        relative,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                        catch (Exception exception) when (
                            exception is IOException or UnauthorizedAccessException or ArgumentException)
                        {
                            // Linked/unreadable/non-empty source directories remain non-repairable.
                        }

                        findings.Add(new HealthFinding(
                            authority is not null
                                ? HealthFindingCode.RetiredProfileDirectory
                                : HealthFindingCode.TrashStateMismatch,
                            authority is not null ? HealthSeverity.Warning : HealthSeverity.Error,
                            profile.ProfileId,
                            null,
                            null,
                            null,
                            authority is not null
                                ? $"Empty Profile folder remains after the Profile moved to Trash: '{folderName}'. Repair removes only empty directories."
                                : $"Profile {profile.ProfileId} is marked TRASHED in catalog but active folder '{folderName}' still exists in profiles directory."
                                  + (empty ? " Recovery authority is incomplete, so automatic removal is not safe." : " Contents are preserved."),
                            RepairAvailable: authority is not null,
                            ManagedRelativePath: relative));
                    }
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(profile.StorageToken))
            {

                continue;
            }

            var plan = _pathPlanner.PlanProfile(profile.ProfileId, profile.DisplayName, new ProfileStorageToken(profile.StorageToken));
            var expectedFolderName = Path.GetFileName(plan.ProfileFolderRelativePath);
            knownProfileFolderNames.Add(expectedFolderName);
            var expectedFolderPath = Path.Combine(_paths.ProfilesPath, expectedFolderName);

            // Trash fallback Unknowns can own independently reconciled media without a persisted
            // Profile folder. Their media placement is checked below; no Profile manifest is owed.
            if (profile.Kind == ProfileKind.Unknown && profile.PathState == ManagedPathState.None
                && profile.CurrentManagedRelativePath is null && profile.TargetManagedRelativePath is null)
            {
                continue;
            }

            string actualFolderPath;
            if (!Directory.Exists(expectedFolderPath))
            {

                if (TryFindFolderByToken(_paths.ProfilesPath, profile.StorageToken, out var foundDir))
                {
                    actualFolderPath = foundDir;
                    knownProfileFolderNames.Add(Path.GetFileName(foundDir));
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ProfileFolderNameMismatch,
                        HealthSeverity.Warning,
                        profile.ProfileId,
                        null,
                        null,
                        null,
                        $"Profile folder name '{Path.GetFileName(foundDir)}' does not match canonical name '{expectedFolderName}'.",
                        RepairAvailable: true));
                }
                else
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ProfileFolderMissing,
                        HealthSeverity.Error,
                        profile.ProfileId,
                        null,
                        null,
                        null,
                        $"Canonical profile folder '{expectedFolderName}' does not exist on disk.",
                        RepairAvailable: true));
                    continue;
                }
            }
            else
            {
                actualFolderPath = expectedFolderPath;
            }

            var manifestRepairAvailable = profile.PathState == ManagedPathState.None
                && !string.IsNullOrWhiteSpace(profile.CurrentManagedRelativePath)
                && string.Equals(profile.CurrentManagedRelativePath.Replace('\\', '/').Trim('/'),
                    plan.ProfileFolderRelativePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(actualFolderPath, expectedFolderPath, StringComparison.OrdinalIgnoreCase);
            var placementNote = manifestRepairAvailable ? string.Empty
                : " Managed folder placement must be reconciled before manifest repair is available.";
            var inspection = ProfileManifestWriter.InspectManifest(actualFolderPath);
            if (inspection.Status == ManifestStatus.Missing)
            {
                findings.Add(new HealthFinding(
                    HealthFindingCode.ProfileManifestMissing,
                    HealthSeverity.Warning,
                    profile.ProfileId,
                    null,
                    null,
                    null,
                    $"Profile manifest '{ProfileManifestWriter.ManifestFileName}' is missing from folder '{Path.GetFileName(actualFolderPath)}'.{placementNote}",
                    RepairAvailable: manifestRepairAvailable));
            }
            else if (inspection.Status == ManifestStatus.Malformed)
            {
                findings.Add(new HealthFinding(
                    HealthFindingCode.ProfileManifestMalformed,
                    HealthSeverity.Warning,
                    profile.ProfileId,
                    null,
                    null,
                    null,
                    $"Profile manifest in '{Path.GetFileName(actualFolderPath)}' is malformed: {inspection.ErrorDetail}.{placementNote}",
                    RepairAvailable: manifestRepairAvailable));
            }
            else if (inspection.Status == ManifestStatus.Valid && inspection.Manifest is not null)
            {
                var manifest = inspection.Manifest;
                if (manifest.ProfileId != profile.ProfileId)
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ProfileManifestIdMismatch,
                        HealthSeverity.Error,
                        profile.ProfileId,
                        null,
                        null,
                        null,
                        $"Profile manifest in '{Path.GetFileName(actualFolderPath)}' carries ProfileId {manifest.ProfileId} instead of authoritative {profile.ProfileId}.",
                        RepairAvailable: false));
                }
                else if (!string.Equals(manifest.DisplayName, profile.DisplayName, StringComparison.Ordinal)
                         || manifest.Kind != profile.Kind
                         || !string.Equals(manifest.FolderName, expectedFolderName, StringComparison.Ordinal))
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ProfileManifestStale,
                        HealthSeverity.Info,
                        profile.ProfileId,
                        null,
                        null,
                        null,
                        $"Profile manifest in '{Path.GetFileName(actualFolderPath)}' is stale compared to database authority.{placementNote}",
                        RepairAvailable: manifestRepairAvailable));
                }
            }
        }

        AddDuplicateFolderTokenFindings(findings);

        progress?.Report(new HealthScanProgress("Scanning managed media and integrity", itemsScanned, null, findings.Count));
        var media = await _catalog.HealthReads.GetHealthMediaItemsAsync(opts.FocusedMediaId, opts.FocusedProfileId, cancellationToken)
            .ConfigureAwait(false);

        var knownMediaIds = new HashSet<Guid>();
        var knownMediaFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenMediaTokens = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in media)
        {
            cancellationToken.ThrowIfCancellationRequested();
            itemsScanned++;

            knownMediaIds.Add(asset.MediaId);

            if (!string.IsNullOrWhiteSpace(asset.StorageToken))
            {
                if (seenMediaTokens.TryGetValue(asset.StorageToken, out var existingMediaId) && existingMediaId != asset.MediaId)
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.StorageTokenConflict,
                        HealthSeverity.Critical,
                        asset.OwnerProfileId,
                        asset.MediaId,
                        null,
                        null,
                        $"Storage token '{asset.StorageToken}' is claimed by multiple media ({existingMediaId} and {asset.MediaId}).",
                        RepairAvailable: false));
                }
                else
                {
                    seenMediaTokens[asset.StorageToken] = asset.MediaId;
                }
            }

            if (asset.IsTrashed || asset.State == MediaState.Trashed)
            {
                var recoveryDir = Path.Combine(_paths.TrashMediasPath, asset.MediaId.ToString("D").ToLowerInvariant());
                if (!Directory.Exists(recoveryDir) || !Directory.EnumerateFiles(recoveryDir).Any())
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.TrashStateMismatch,
                        HealthSeverity.Error,
                        asset.OwnerProfileId,
                        asset.MediaId,
                        null,
                        null,
                        $"Media {asset.MediaId} is marked TRASHED in catalog but recovery bytes are missing from trash root.",
                        RepairAvailable: true));
                }

                if (!string.IsNullOrWhiteSpace(asset.CurrentManagedRelativePath)
                    && !string.IsNullOrWhiteSpace(asset.CurrentManagedFileName))
                {
                    try
                    {
                        var managedFile = _paths.ResolveVaultRelativePath(
                            $"{asset.CurrentManagedRelativePath}/{asset.CurrentManagedFileName}");
                        if (File.Exists(managedFile))
                        {
                            findings.Add(new HealthFinding(
                                HealthFindingCode.TrashStateMismatch,
                                HealthSeverity.Error,
                                asset.OwnerProfileId,
                                asset.MediaId,
                                null,
                                null,
                                $"Media {asset.MediaId} is marked TRASHED in catalog but file still exists at managed path '{asset.CurrentManagedRelativePath}/{asset.CurrentManagedFileName}'.",
                                RepairAvailable: true));
                        }
                    }
                    catch (ArgumentException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                continue;
            }

            if (asset.State != MediaState.Active)
            {
                continue;
            }

            if (!asset.OwnerProfileId.HasValue || string.IsNullOrWhiteSpace(asset.OwnerStorageToken))
            {

                continue;
            }

            if (string.IsNullOrWhiteSpace(asset.StorageToken))
            {

                continue;
            }

            IReadOnlyList<MediaComponentRecord> packageComponents = asset.MediaType == MediaType.Model
                ? await _catalog.MediaWrites.GetMediaComponentsAsync(asset.MediaId, cancellationToken)
                    .ConfigureAwait(false)
                : Array.Empty<MediaComponentRecord>();
            var isPackage = packageComponents.Count > 1
                || packageComponents.Any(component => component.ComponentRole == ComponentRole.Dependency);

            if (isPackage)
            {
                if (string.IsNullOrWhiteSpace(asset.CurrentManagedRelativePath))
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ManagedPathMismatch,
                        HealthSeverity.Error,
                        asset.OwnerProfileId,
                        asset.MediaId,
                        null,
                        null,
                        $"Package asset {asset.MediaId} has no authoritative managed package directory.",
                        RepairAvailable: false));
                    continue;
                }

                foreach (var component in packageComponents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string componentPath;
                    try
                    {
                        componentPath = _paths.ResolveVaultRelativePath(
                            $"{asset.CurrentManagedRelativePath}/{component.ComponentRelativePath}");
                    }
                    catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                    {
                        findings.Add(new HealthFinding(
                            HealthFindingCode.ManagedPathMismatch,
                            HealthSeverity.Critical,
                            asset.OwnerProfileId,
                            asset.MediaId,
                            null,
                            null,
                            $"Package component '{component.ComponentRelativePath}' resolves outside canonical Vault authority.",
                            RepairAvailable: false));
                        continue;
                    }

                    knownMediaFileNames.Add(Path.GetFileName(componentPath));
                    if (!File.Exists(componentPath))
                    {
                        findings.Add(new HealthFinding(
                            HealthFindingCode.ManagedPathMismatch,
                            HealthSeverity.Error,
                            asset.OwnerProfileId,
                            asset.MediaId,
                            null,
                            null,
                            $"Required package component '{component.ComponentRelativePath}' is missing.",
                            RepairAvailable: true));
                        continue;
                    }

                    var info = new FileInfo(componentPath);
                    if (info.Length != component.ByteLength)
                    {
                        findings.Add(new HealthFinding(
                            HealthFindingCode.ContentMismatch,
                            HealthSeverity.Critical,
                            asset.OwnerProfileId,
                            asset.MediaId,
                            null,
                            null,
                            $"Package component '{component.ComponentRelativePath}' length does not match durable component authority.",
                            RepairAvailable: false));
                        continue;
                    }

                    if (opts.Mode == HealthScanMode.Deep)
                    {
                        await using var componentStream = File.OpenRead(componentPath);
                        var componentHash = Convert.ToHexStringLower(
                            await SHA256.HashDataAsync(componentStream, cancellationToken).ConfigureAwait(false));
                        if (!string.Equals(componentHash, component.Sha256, StringComparison.Ordinal))
                        {
                            findings.Add(new HealthFinding(
                                HealthFindingCode.ContentMismatch,
                                HealthSeverity.Critical,
                                asset.OwnerProfileId,
                                asset.MediaId,
                                null,
                                null,
                                $"Package component '{component.ComponentRelativePath}' SHA-256 does not match durable component authority.",
                                RepairAvailable: false));
                        }
                    }
                }

                continue;
            }

            var assetPlan = _pathPlanner.PlanMedia(
                asset.OwnerProfileId.Value,
                asset.OwnerDisplayName ?? "Profile",
                new ProfileStorageToken(asset.OwnerStorageToken),
                asset.MediaId,
                new MediaStorageToken(asset.StorageToken),
                asset.MediaType,
                asset.Extension);

            var expectedFileName = assetPlan.ManagedFileName!;
            knownMediaFileNames.Add(expectedFileName);
            var expectedRelative = NormalizeRelative(assetPlan.ManagedFileRelativePath!);
            string expectedFullPath;
            try
            {
                expectedFullPath = _paths.ResolveVaultRelativePath(
                    assetPlan.ManagedFileRelativePath!);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                findings.Add(new HealthFinding(
                    HealthFindingCode.ManagedPathMismatch,
                    HealthSeverity.Error,
                    asset.OwnerProfileId,
                    asset.MediaId,
                    null,
                    null,
                    $"Canonical managed path '{expectedRelative}' resolves outside Vault path authority.",
                    RepairAvailable: false));
                continue;
            }

            string actualFilePath;
            if (!File.Exists(expectedFullPath))
            {

                var profileFolder = Path.Combine(_paths.ProfilesPath, Path.GetFileName(assetPlan.ProfileFolderRelativePath));
                var expectedDirectory = Path.GetDirectoryName(expectedFullPath) ?? profileFolder;
                if (TryFindMediaFileByToken(profileFolder, expectedDirectory, asset.StorageToken, out var foundPath))
                {
                    actualFilePath = foundPath;
                    knownMediaFileNames.Add(Path.GetFileName(foundPath));
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ManagedFileNameMismatch,
                        HealthSeverity.Warning,
                        asset.OwnerProfileId,
                        asset.MediaId,
                        null,
                        null,
                        $"Managed file name '{Path.GetFileName(foundPath)}' does not match canonical name '{expectedFileName}'.",
                        RepairAvailable: true));
                }
                else
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ManagedPathMismatch,
                        HealthSeverity.Error,
                        asset.OwnerProfileId,
                        asset.MediaId,
                        null,
                        null,
                        $"Managed file is missing at expected path '{expectedRelative}'.",
                        RepairAvailable: true));
                    continue;
                }
            }
            else
            {
                actualFilePath = expectedFullPath;
            }

            var actualName = Path.GetFileName(actualFilePath);
            var extractedToken = ExtractMediaStorageTokenFromFileName(actualName);
            if (extractedToken is not null && !string.Equals(extractedToken, asset.StorageToken, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new HealthFinding(
                    HealthFindingCode.StorageTokenPathMismatch,
                    HealthSeverity.Error,
                    asset.OwnerProfileId,
                    asset.MediaId,
                    null,
                    null,
                    $"Managed file '{actualName}' carries storage token '{extractedToken}' which does not match assigned token '{asset.StorageToken}'.",
                    RepairAvailable: true));
            }

            if (opts.Mode == HealthScanMode.Deep && !string.IsNullOrWhiteSpace(asset.Sha256))
            {
                await using var stream = File.OpenRead(actualFilePath);
                var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                var actualSha256 = Convert.ToHexStringLower(hashBytes);

                if (!string.Equals(actualSha256, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ContentMismatch,
                        HealthSeverity.Critical,
                        asset.OwnerProfileId,
                        asset.MediaId,
                        null,
                        null,
                        $"Content fingerprint mismatch for asset {asset.MediaId}. Catalog SHA-256: {asset.Sha256}, actual file SHA-256: {actualSha256}.",
                        RepairAvailable: false));
                }
            }
        }

        if (opts.Mode != HealthScanMode.Focused && Directory.Exists(_paths.ProfilesPath))
        {
            progress?.Report(new HealthScanProgress("Auditing filesystem for unexpected MediaAssets", itemsScanned, null, findings.Count));
            foreach (var profileDir in Directory.EnumerateDirectories(_paths.ProfilesPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(profileDir);
                var folderToken = ExtractProfileStorageTokenFromFolder(dirName);

                bool isKnown = knownProfileFolderNames.Contains(dirName)
                    || (folderToken is not null && knownProfileTokens.Contains(folderToken));

                if (!isKnown)
                {
                    var relative = Path.GetRelativePath(_paths.Root, profileDir).Replace('\\', '/');
                    RetiredProfileDirectory.Authority? retired = null;
                    var empty = false;
                    try
                    {
                        empty = RetiredProfileDirectory.IsEmptyTree(_paths.Root, profileDir);
                        if (empty) retired = await RetiredProfileDirectory.ReadAuthorityAsync(
                            _catalog, relative, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        // Unreadable or linked directories remain findings without destructive repair.
                    }
                    findings.Add(new HealthFinding(
                        retired is null ? HealthFindingCode.UnexpectedManagedFile : HealthFindingCode.RetiredProfileDirectory,
                        HealthSeverity.Warning,
                        retired?.ProfileId,
                        null,
                        null,
                        null,
                        retired is not null
                            ? $"Empty folder remains after this Profile was permanently deleted: '{dirName}'. Repair removes only empty directories."
                            : empty ? $"Unowned empty Profile folder: '{dirName}'. Current lifecycle authority cannot prove automatic removal safe."
                            : $"Unowned Profile folder: '{dirName}'. Contents are preserved; review its files before moving or deleting it.",
                        RepairAvailable: retired is not null,
                        ManagedRelativePath: relative));
                    continue;
                }

                var mediaRoot = Path.Combine(profileDir, "media");
                if (Directory.Exists(mediaRoot))
                {
                    foreach (var mediaTypeDir in Directory.EnumerateDirectories(mediaRoot))
                    {
                        foreach (var file in Directory.EnumerateFiles(mediaTypeDir))
                        {
                            var fileName = Path.GetFileName(file);
                            if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (!knownMediaFileNames.Contains(fileName))
                            {
                                var fileToken = ExtractMediaStorageTokenFromFileName(fileName);
                                if (fileToken is null || !seenMediaTokens.ContainsKey(fileToken))
                                {
                                    findings.Add(new HealthFinding(
                                        HealthFindingCode.UnexpectedManagedFile,
                                        HealthSeverity.Warning,
                                        null,
                                        null,
                                        null,
                                        null,
                                        $"Unexpected file in profile media: '{Path.GetRelativePath(_paths.ProfilesPath, file)}'.",
                                        RepairAvailable: false));
                                }
                            }
                        }
                    }
                }
            }
        }

        if (opts.Mode != HealthScanMode.Focused && Directory.Exists(_paths.TrashMediasPath))
        {
            foreach (var trashMediaDir in Directory.EnumerateDirectories(_paths.TrashMediasPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(trashMediaDir);
                if (Guid.TryParse(dirName, out var trashMediaId))
                {
                    var match = media.FirstOrDefault(a => a.MediaId == trashMediaId);
                    if (match is not null && match.State == MediaState.Active && !match.IsTrashed)
                    {
                        findings.Add(new HealthFinding(
                            HealthFindingCode.TrashStateMismatch,
                            HealthSeverity.Error,
                            match.OwnerProfileId,
                            trashMediaId,
                            null,
                            null,
                            $"Media {trashMediaId} is ACTIVE in catalog but recovery directory exists in trash media root.",
                            RepairAvailable: true));
                    }
                }
            }
        }

        if (opts.Mode != HealthScanMode.Focused && Directory.Exists(_paths.StagingImportsPath))
        {
            progress?.Report(new HealthScanProgress("Auditing staging directory", itemsScanned, null, findings.Count));
            foreach (var stagingDir in Directory.EnumerateDirectories(_paths.StagingImportsPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(stagingDir);
                if (!await IsOwnedStagingDirectoryAsync(dirName, cancellationToken).ConfigureAwait(false))
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.StagingOrphanAmbiguous,
                        HealthSeverity.Warning,
                        null,
                        null,
                        null,
                        null,
                        $"Staging directory '{dirName}' has no durable import unit.",
                        RepairAvailable: false));
                }
            }
        }

        if (opts.Mode != HealthScanMode.Focused && Directory.Exists(_paths.CachePath))
        {
            progress?.Report(new HealthScanProgress("Auditing cache directories", itemsScanned, null, findings.Count));
            foreach (var file in Directory.EnumerateFiles(_paths.CachePath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                if (info.Length == 0)
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.CacheCorrupt,
                        HealthSeverity.Warning,
                        null,
                        null,
                        null,
                        null,
                        $"Cache file '{Path.GetRelativePath(_paths.CachePath, file)}' is 0 bytes (corrupted).",
                        RepairAvailable: true));
                    continue;
                }

                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);
                if (Guid.TryParse(fileNameWithoutExt, out var parsedMediaId) && !knownMediaIds.Contains(parsedMediaId))
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.CacheOrphan,
                        HealthSeverity.Info,
                        null,
                        parsedMediaId,
                        null,
                        null,
                        $"Cache artifact '{Path.GetFileName(file)}' does not correspond to any known asset in the catalog.",
                        RepairAvailable: true));
                }
            }
        }

        for (var index = 0; index < findings.Count; index++)
        {
            if (findings[index].RepairAvailable && !RepairPlanner.SupportsFinding(findings[index].Code))
                findings[index] = findings[index] with { RepairAvailable = false };
        }

        var elapsed = _timeProvider.GetElapsedTime(startTime);
        progress?.Report(new HealthScanProgress("Scan completed", itemsScanned, itemsScanned, findings.Count));

        return new HealthScanResult(
            findings,
            opts.Mode,
            itemsScanned,
            elapsed,
            _timeProvider.GetUtcNow());
    }

    private async Task<IReadOnlyList<HealthFinding>> ScanArtifactAuthorityAsync(
        HealthScanOptions options, CancellationToken cancellationToken)
    {
        var findings = new List<HealthFinding>();
        var knownMediaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT aa.media_id, aa.relative_path, aa.byte_length, aa.sha256,
                       aa.contract_version, a.state, a.trashed_at_ms, aa.state,
                       aa.role, a.sha256, a.media_type, coalesce(a.current_managed_file_name,a.original_file_name),
                       a.dependency_status, a.media_storage_token
                FROM media_assets aa JOIN media a ON a.media_id = aa.media_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var assetId = DbGuid.Parse(reader.GetString(0));
                var relativePath = reader.GetString(1);
                knownMediaPaths.Add(relativePath.Replace('\\', '/'));
                if (options.FocusedMediaId is { } focus && focus != assetId) continue;
                if (options.FocusedProfileId.HasValue) continue;
                if (reader.GetString(5) == "TRASHED" || !reader.IsDBNull(6)) continue;
                if (reader.GetInt32(4) != 1)
                    findings.Add(new HealthFinding(HealthFindingCode.ArtifactContractObsolete,
                        HealthSeverity.Error, null, assetId, null, null,
                        $"MediaAsset '{relativePath}' uses an obsolete contract.", true));
                if (reader.GetString(7) == "NEEDS_REPAIR")
                    findings.Add(new HealthFinding(HealthFindingCode.MediaAssetNeedsRepair,
                        HealthSeverity.Warning, null, assetId, null, null,
                        $"MediaAsset '{relativePath}' is awaiting durable repair.", true));
                if (reader.GetString(8) == "MODEL_RENDER")
                {
                    var eligible = !reader.IsDBNull(9) && reader.GetString(10) == "MODEL" && !reader.IsDBNull(11)
                        && ModelRenderEligibility.IsEligible(reader.GetString(11),reader.GetString(12));
                    string expectedPath = "";
                    try { expectedPath = _pathPlanner.PlanMediaAsset(new MediaStorageToken(reader.GetString(13)),MediaAssetRole.ModelRender); }
                    catch (ArgumentException) { eligible = false; }
                    if (!eligible || relativePath != expectedPath)
                    {
                        findings.Add(new HealthFinding(HealthFindingCode.MediaAssetFingerprintMismatch,HealthSeverity.Error,
                            null,assetId,null,null,$"ModelRender '{relativePath}' source fingerprint, type, format, dependency or canonical path is invalid.",false));
                        continue;
                    }
                    try
                    {
                        var physical = _paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets,relativePath);
                        if (File.Exists(physical))
                        {
                            var valid = await new MediaAssetFileValidator().ValidateAsync(physical,MediaAssetRole.ModelRender,
                                cancellationToken,reader.GetString(9)).ConfigureAwait(false);
                            if (valid.ByteLength != reader.GetInt64(2) || valid.Sha256 != reader.GetString(3))
                                throw new InvalidDataException("ModelRender catalog fingerprint differs from validated bytes.");
                            continue;
                        }
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException)
                    {
                        findings.Add(new HealthFinding(HealthFindingCode.MediaAssetFingerprintMismatch,HealthSeverity.Error,
                            null,assetId,null,null,$"ModelRender '{relativePath}' structure or source provenance is invalid: {ex.Message}",true));
                        continue;
                    }
                }
                await CheckArtifactFileAsync(findings, assetId, null, relativePath,
                    reader.GetInt64(2), reader.GetString(3), VaultPathArea.MediaAssets,
                    HealthFindingCode.MediaAssetMissing,
                    HealthFindingCode.MediaAssetFingerprintMismatch,
                    options.Mode == HealthScanMode.Deep, cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT pa.profile_id,
                       CASE WHEN pa.cover_media_asset_id IS NOT NULL AND
                           (cover.media_asset_id IS NULL OR cover.role <> 'THUMBNAIL'
                            OR cover.state <> 'READY' OR cover_media.state <> 'ACTIVE'
                            OR NOT EXISTS (SELECT 1 FROM profile_media relation
                                WHERE relation.profile_id = pa.profile_id
                                  AND relation.media_id = cover.media_id
                                  AND relation.publication_import_unit_id IS NULL))
                           THEN 1 ELSE 0 END,
                       CASE WHEN pa.banner_media_asset_id IS NOT NULL AND
                           (banner.media_asset_id IS NULL OR banner.role NOT IN ('THUMBNAIL','HOVER')
                            OR banner.state <> 'READY' OR banner_media.state <> 'ACTIVE'
                            OR NOT EXISTS (SELECT 1 FROM profile_media relation
                                WHERE relation.profile_id = pa.profile_id
                                  AND relation.media_id = banner.media_id
                                  AND relation.publication_import_unit_id IS NULL))
                           THEN 1 ELSE 0 END
                FROM profile_appearance pa
                JOIN profiles profile ON profile.profile_id = pa.profile_id
                LEFT JOIN media_assets cover ON cover.media_asset_id = pa.cover_media_asset_id
                LEFT JOIN media cover_media ON cover_media.media_id = cover.media_id
                LEFT JOIN media_assets banner ON banner.media_asset_id = pa.banner_media_asset_id
                LEFT JOIN media banner_media ON banner_media.media_id = banner.media_id
                WHERE profile.trashed_at_ms IS NULL;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var profileId = DbGuid.Parse(reader.GetString(0));
                if (options.FocusedProfileId is { } focus && focus != profileId) continue;
                if (options.FocusedMediaId.HasValue) continue;
                if (reader.GetInt32(1) != 0 || reader.GetInt32(2) != 0)
                    findings.Add(new HealthFinding(HealthFindingCode.ProfileMediaSelectionInvalid,
                        HealthSeverity.Error, profileId, null, null, null,
                        "A selected Cover or Banner MediaAsset is unavailable or unrelated to this Profile.", false));
            }
        }

        if (!options.FocusedMediaId.HasValue && !options.FocusedProfileId.HasValue
            && options.Mode != HealthScanMode.StartupCritical && Directory.Exists(_paths.MediaAssetsPath))
        {
            foreach (var file in Directory.EnumerateFiles(_paths.MediaAssetsPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(_paths.Root, file).Replace('\\', '/');
                if (!knownMediaPaths.Contains(relative))
                    findings.Add(new HealthFinding(HealthFindingCode.ArtifactOrphaned,
                        HealthSeverity.Warning, null, null, null, null,
                        $"Unreferenced MediaAsset '{relative}' exists in the Vault.", false));
            }
        }
        return findings;
    }

    private async Task CheckArtifactFileAsync(List<HealthFinding> findings, Guid? assetId,
        Guid? profileId, string relativePath, long expectedLength, string expectedSha,
        VaultPathArea area, string missingCode, string mismatchCode, bool deep,
        CancellationToken cancellationToken)
    {
        string absolute;
        try { absolute = _paths.ResolveVaultRelativePath(area, relativePath); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            findings.Add(new HealthFinding(mismatchCode, HealthSeverity.Error, profileId,
                assetId, null, null, $"Artifact path '{relativePath}' is invalid.", false));
            return;
        }
        if (!File.Exists(absolute))
        {
            findings.Add(new HealthFinding(missingCode, HealthSeverity.Error, profileId,
                assetId, null, null, $"MediaAsset file '{relativePath}' is missing.", assetId.HasValue));
            return;
        }
        if (new FileInfo(absolute).Length != expectedLength)
        {
            findings.Add(new HealthFinding(mismatchCode, HealthSeverity.Error, profileId,
                assetId, null, null, $"MediaAsset file '{relativePath}' has the wrong length.", assetId.HasValue));
            return;
        }
        if (deep)
        {
            await using var stream = File.OpenRead(absolute);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(hash, expectedSha, StringComparison.Ordinal))
                findings.Add(new HealthFinding(mismatchCode, HealthSeverity.Critical, profileId,
                    assetId, null, null, $"MediaAsset file '{relativePath}' has the wrong SHA-256.", assetId.HasValue));
        }
    }

    private static string NormalizeRelative(string relativePath) =>
        relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);

    private static string? ExtractProfileStorageTokenFromFolder(string folderName)
    {
        var open = folderName.LastIndexOf('[');
        var close = folderName.LastIndexOf(']');
        if (open >= 0 && close > open + 1)
        {
            return folderName.Substring(open + 1, close - open - 1).Trim();
        }
        return null;
    }

    private static string? ExtractProfileStorageTokenFromFolderName(string folderName)
    {
        var open = folderName.LastIndexOf('[');
        var close = folderName.LastIndexOf(']');
        if (open < 0 || close <= open + 1)
        {
            return null;
        }

        return folderName[(open + 1)..close].Trim();
    }

    private static string? ExtractMediaStorageTokenFromFileName(string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var dashIndex = nameWithoutExt.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dashIndex >= 0 && dashIndex + 3 < nameWithoutExt.Length)
        {
            return nameWithoutExt.Substring(dashIndex + 3).Trim();
        }
        return null;
    }

    private void AddDuplicateFolderTokenFindings(List<HealthFinding> findings)
    {
        if (!Directory.Exists(_paths.ProfilesPath))
        {
            return;
        }

        var byToken = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(_paths.ProfilesPath))
        {
            var name = Path.GetFileName(directory);
            if (ExtractProfileStorageTokenFromFolderName(name) is not { } token)
            {
                continue;
            }

            if (!byToken.TryGetValue(token, out var claimants))
            {
                claimants = [];
                byToken[token] = claimants;
            }

            claimants.Add(name);
        }

        foreach (var (token, claimants) in byToken.Where(entry => entry.Value.Count > 1)
                     .OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            findings.Add(new HealthFinding(
                HealthFindingCode.StorageTokenConflict,
                HealthSeverity.Critical,
                null,
                null,
                null,
                null,
                $"Storage token '{token}' is claimed by {claimants.Count} managed folders: "
                    + string.Join(", ", claimants.OrderBy(name => name, StringComparer.Ordinal)) + ".",
                RepairAvailable: false));
        }
    }

    private static bool TryFindFolderByToken(string profilesPath, string token, out string foundPath)
    {
        foundPath = string.Empty;
        if (!Directory.Exists(profilesPath))
        {
            return false;
        }

        var marker = $"[{token}]";
        foreach (var dir in Directory.EnumerateDirectories(profilesPath))
        {
            if (dir.EndsWith(marker, StringComparison.OrdinalIgnoreCase)
                || dir.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                foundPath = dir;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindMediaFileByToken(
        string profileFolder,
        string expectedDirectory,
        string token,
        out string foundPath)
    {
        foundPath = string.Empty;

        if (TryMatchTokenInDirectory(expectedDirectory, token, SearchOption.TopDirectoryOnly, out foundPath))
        {
            return true;
        }

        return TryMatchTokenInDirectory(profileFolder, token, SearchOption.AllDirectories, out foundPath);
    }

    private static bool TryMatchTokenInDirectory(
        string directory,
        string token,
        SearchOption searchOption,
        out string foundPath)
    {
        foundPath = string.Empty;
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", searchOption))
        {
            if (Path.GetFileName(file).Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                foundPath = file;
                return true;
            }
        }

        return false;
    }

    private async Task<bool> IsOwnedStagingDirectoryAsync(string dirName, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // A unit retains staging ownership through pause, failure and restart until settlement.
        command.CommandText = """
            SELECT 1 FROM import_units
            WHERE import_unit_id = $dirName;
            """;
        command.Parameters.AddWithValue("$dirName", dirName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }
}