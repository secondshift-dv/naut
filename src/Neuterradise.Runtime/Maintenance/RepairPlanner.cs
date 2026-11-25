using Neuterradise.App.Media.Model;
using System.IO;
using System.Security.Cryptography;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.TimeAndIds;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Import;

namespace Neuterradise.App.Maintenance;

public sealed class RepairPlanner
{
    public static bool SupportsFinding(string code) => code is
        HealthFindingCode.ProfileManifestMissing or HealthFindingCode.ProfileManifestMalformed
        or HealthFindingCode.ProfileManifestStale or HealthFindingCode.MediaAssetMissing
        or HealthFindingCode.MediaAssetFingerprintMismatch or HealthFindingCode.MediaAssetNeedsRepair
        or HealthFindingCode.ArtifactContractObsolete or HealthFindingCode.RetiredProfileDirectory
        or HealthFindingCode.CanonicalOriginalMissing or HealthFindingCode.CanonicalOriginalMismatch
        or HealthFindingCode.CanonicalOriginalReverify or HealthFindingCode.CanonicalOriginalRepairPending or HealthFindingCode.CanonicalPlacementStale;

    private readonly CatalogDb _catalog;
    private readonly ManagedPathPlanner _pathPlanner;
    private readonly TimeProvider _timeProvider;
    private readonly OperationExecution _execution;

    /// <summary>Canonical operation kind of preparing a repair plan.</summary>
    public const string PrepareRepairKind = "MAINTENANCE_PREPARE_REPAIR";

    public RepairPlanner(
        CatalogDb catalog,
        ManagedPathPlanner? pathPlanner = null,
        TimeProvider? timeProvider = null,
        StructuredDiagnostics? diagnostics = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _pathPlanner = pathPlanner ?? new ManagedPathPlanner(_catalog.Paths.Root);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _execution = new OperationExecution(diagnostics, _timeProvider.AsClock());
    }

    /// <summary>
    /// Prepares a repair plan for one finding. Cancellation and infrastructure faults are returned
    /// as canonical results so the Library Health surface never has to interpret an exception.
    /// </summary>
    public Task<OperationResult<RepairPlan>> PrepareAsync(
        HealthFinding finding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return _execution.RunAsync<RepairPlan>(
            OperationContext.Start(PrepareRepairKind, _timeProvider, cancellationToken)
                with { ProfileId = finding.ProfileId, MediaId = finding.MediaId },
            context => PrepareCoreAsync(finding, context.CancellationToken));
    }

    private async Task<OperationResult<RepairPlan>> PrepareCoreAsync(
        HealthFinding finding,
        CancellationToken cancellationToken)
    {

        using var admission = _catalog.MutationAdmission.Enter(PrepareRepairKind);
        if (finding.Code == HealthFindingCode.CanonicalOriginalRepairPending
            && Guid.TryParse(finding.OperationId, out var operation))
        {
            var pending = await _catalog.MaintenanceWrites.ReadRepairOperationAsync(operation, cancellationToken)
                .ConfigureAwait(false);
            if (pending is not null && pending.Plan.MediaId == finding.MediaId
                && pending.State is "PREPARED" or "EXECUTING" or "FAILED_RETRYABLE"
                && pending.Plan.Kind is RepairKind.ReconcileOriginalIntegrity or RepairKind.RestoreCanonicalOriginal or RepairKind.ReconcileOriginalPlacement)
                return OperationResult<RepairPlan>.Success(pending.Plan, operation);
            return Unsupported(finding.Code);
        }
        OperationResult<RepairPlan> prepared;
        if (finding.Code is HealthFindingCode.ProfileManifestMissing
            or HealthFindingCode.ProfileManifestMalformed
            or HealthFindingCode.ProfileManifestStale)
        {
            prepared = await PrepareManifestRepairAsync(finding, cancellationToken).ConfigureAwait(false);
        }
        else if (finding.Code is HealthFindingCode.MediaAssetMissing
                 or HealthFindingCode.MediaAssetFingerprintMismatch
                 or HealthFindingCode.MediaAssetNeedsRepair
                 or HealthFindingCode.ArtifactContractObsolete)
        {
            prepared = await PrepareMediaAssetRepairAsync(finding, cancellationToken).ConfigureAwait(false);
        }
        else if (finding.Code is HealthFindingCode.CanonicalOriginalMissing
                 or HealthFindingCode.CanonicalOriginalMismatch or HealthFindingCode.CanonicalOriginalReverify)
        {
            prepared = await PrepareOriginalIntegrityAsync(finding, cancellationToken).ConfigureAwait(false);
        }
        else if (finding.Code == HealthFindingCode.CanonicalPlacementStale)
        {
            prepared = await PreparePlacementRepairAsync(finding, cancellationToken).ConfigureAwait(false);
        }
        else if (finding.Code == HealthFindingCode.RetiredProfileDirectory)
        {
            prepared = await PrepareDirectoryRepairAsync(finding, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return Unsupported(finding.Code);
        }

        if (!prepared.IsSuccess || prepared.Value is null)
        {
            return prepared;
        }

        if (!finding.RepairAvailable)
        {
            return Unsupported(finding.Code);
        }

        try
        {
            var persisted = await _catalog.MaintenanceWrites
                .BeginOrReadRepairOperationAsync(prepared.Value, cancellationToken)
                .ConfigureAwait(false);
            return OperationResult<RepairPlan>.Success(persisted, persisted.OperationId);
        }
        catch (CatalogInvariantException)
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "A different repair operation already owns this subject.");
        }
    }

    private async Task<OperationResult<RepairPlan>> PreparePlacementRepairAsync(HealthFinding finding, CancellationToken ct)
    {
        if (finding.MediaId is not Guid id) return Unsupported(finding.Code);
        var media = (await _catalog.HealthReads.GetHealthMediaItemsAsync(id, cancellationToken: ct).ConfigureAwait(false)).SingleOrDefault();
        if (media is not { HasStablePublishedOwner: true, OwnerProfileId: Guid owner,
            CurrentManagedRelativePath: { Length: > 0 } directory, CurrentManagedFileName: { Length: > 0 } name,
            Sha256: { Length: 64 } sha }) return Unsupported(finding.Code);
        var components = media.MediaType == Neuterradise.App.Media.MediaType.Model
            ? await _catalog.MediaWrites.GetMediaComponentsAsync(id, ct).ConfigureAwait(false) : [];
        using var permit = await Neuterradise.App.SystemServices.Resources.ResourceGovernor.Shared.AcquireAsync(
            Neuterradise.App.SystemServices.Resources.ResourceClass.DiskHeavy, ct).ConfigureAwait(false);
        var target = await new CanonicalOriginalRecovery(_catalog).FindPlacementAsync(media, components, true, ct).ConfigureAwait(false);
        if (target is null || target != finding.ManagedRelativePath)
            return OperationResult<RepairPlan>.Conflict(OperationErrorCode.RepairPlanStale,
                "There is no unique verified canonical placement.");
        var plan = new RepairPlan(RepairPlan.CurrentSchemaVersion, Guid.NewGuid(), finding.Code,
            RepairKind.ReconcileOriginalPlacement, null, id, null, media.RowVersion,
            $"{owner:D}|{media.OriginalHealthStatus ?? "UNRECORDED"}", $"{directory}/{name}",
            target, sha, media.ByteLength, RepairPhysicalFact.OriginalMissing,
            "Adopt the verified existing canonical placement; preserve every file, fingerprint and Media relation.",
            true, Guid.NewGuid(), _timeProvider.GetUtcNow());
        return OperationResult<RepairPlan>.Success(plan, plan.OperationId);
    }

    public Task<OperationResult<RepairPlan>> PrepareOriginalRecoveryAsync(
        HealthFinding finding, string sourcePath, CancellationToken ct = default)
        => _execution.RunAsync<RepairPlan>(OperationContext.Start(PrepareRepairKind, _timeProvider, ct)
            with { MediaId = finding.MediaId }, async context =>
        {
            using var admission = _catalog.MutationAdmission.Enter(PrepareRepairKind);
            var token = context.CancellationToken;
            if (!finding.RecoveryAvailable || finding.MediaId is not Guid mediaId)
                return Unsupported(finding.Code);
            var media = (await _catalog.HealthReads.GetHealthMediaItemsAsync(mediaId, cancellationToken: token)
                .ConfigureAwait(false)).SingleOrDefault();
            if (media is not { HasStablePublishedOwner: true, OwnerProfileId: Guid owner,
                CurrentManagedRelativePath: { Length: > 0 } directory,
                CurrentManagedFileName: { Length: > 0 } name, Sha256: { Length: 64 } sha,
                OriginalHealthStatus: not null })
                return Unsupported(finding.Code);
            var components = media.MediaType == Neuterradise.App.Media.MediaType.Model
                ? await _catalog.MediaWrites.GetMediaComponentsAsync(mediaId, token).ConfigureAwait(false) : [];
            var files = CanonicalOriginalInspection.Files(media, components);
            using var disk = await Neuterradise.App.SystemServices.Resources.ResourceGovernor.Shared.AcquireAsync(
                Neuterradise.App.SystemServices.Resources.ResourceClass.DiskHeavy, token).ConfigureAwait(false);
            if (files is null || !await new CanonicalOriginalRecovery(_catalog)
                    .ValidateSelectionAsync(media, files, sourcePath, token).ConfigureAwait(false))
                return OperationResult<RepairPlan>.Conflict(OperationErrorCode.RepairPlanStale,
                    "The selected recovery source is not a verified, unambiguous match.");
            var plan = new RepairPlan(RepairPlan.CurrentSchemaVersion, Guid.NewGuid(), finding.Code,
                RepairKind.RestoreCanonicalOriginal, null, mediaId, null, media.RowVersion,
                $"{owner:D}|{media.OriginalHealthStatus}", Path.GetFullPath(sourcePath),
                $"{directory}/{name}", sha, media.ByteLength, RepairPhysicalFact.OriginalMismatch,
                DescribeRecoveryAction(media, sourcePath),
                true, Guid.NewGuid(), _timeProvider.GetUtcNow());
            try
            {
                var persisted = await _catalog.MaintenanceWrites.BeginOrReadRepairOperationAsync(plan, token).ConfigureAwait(false);
                return OperationResult<RepairPlan>.Success(persisted, persisted.OperationId);
            }
            catch (CatalogInvariantException)
            {
                return OperationResult<RepairPlan>.Conflict(OperationErrorCode.RepairPlanStale,
                    "An existing repair owns this Media. Resume or resolve that operation first.");
            }
        });

    private string DescribeRecoveryAction(HealthMediaItem media, string source)
    {
        var selected = Path.GetFullPath(source);
        var directory = _catalog.Paths.ResolveVaultRelativePath(media.CurrentManagedRelativePath!);
        var action = RootPathRules.IsStrictDescendant(_catalog.Paths.ProfilesPath, selected)
            ? (RootPathRules.AreSameRoot(Path.GetDirectoryName(selected)!, directory)
                ? "Recover a physically renamed original." : "Recover a physically relocated original.")
            : "Recover from the selected replacement.";
        return action + " Copy verified bytes to the recorded placement and preserve the selected source and damaged evidence.";
    }

    private async Task<OperationResult<RepairPlan>> PrepareOriginalIntegrityAsync(
        HealthFinding finding, CancellationToken ct)
    {
        if (finding.MediaId is not Guid mediaId) return Unsupported(finding.Code);
        var media = (await _catalog.HealthReads.GetHealthMediaItemsAsync(mediaId, cancellationToken: ct)
            .ConfigureAwait(false)).SingleOrDefault();
        if (media is not { HasStablePublishedOwner: true, OwnerProfileId: Guid owner,
            CurrentManagedRelativePath: { Length: > 0 } directory,
            CurrentManagedFileName: { Length: > 0 } name, Sha256: { Length: 64 } sha })
            return OperationResult<RepairPlan>.Conflict(OperationErrorCode.RepairPlanStale,
                "The original no longer has stable published ownership and fingerprint authority.");
        var components = media.MediaType == Neuterradise.App.Media.MediaType.Model
            ? await _catalog.MediaWrites.GetMediaComponentsAsync(mediaId, ct).ConfigureAwait(false) : [];
        using var disk = await Neuterradise.App.SystemServices.Resources.ResourceGovernor.Shared.AcquireAsync(
            Neuterradise.App.SystemServices.Resources.ResourceClass.DiskHeavy, ct).ConfigureAwait(false);
        var inspected = await CanonicalOriginalInspection.InspectAsync(_catalog, media, components, true, ct).ConfigureAwait(false);
        var expected = finding.Code switch
        {
            HealthFindingCode.CanonicalOriginalMissing => OriginalInspectionStatus.Missing,
            HealthFindingCode.CanonicalOriginalMismatch => OriginalInspectionStatus.Mismatch,
            _ => OriginalInspectionStatus.Match,
        };
        if (inspected != expected || (expected == OriginalInspectionStatus.Match && media.OriginalHealthStatus is null))
            return OperationResult<RepairPlan>.Conflict(OperationErrorCode.RepairPlanStale,
                "The original no longer matches the selected integrity finding.");
        var relative = $"{directory}/{name}";
        var fact = expected switch
        {
            OriginalInspectionStatus.Missing => RepairPhysicalFact.OriginalMissing,
            OriginalInspectionStatus.Mismatch => RepairPhysicalFact.OriginalMismatch,
            _ => RepairPhysicalFact.SourcePresent,
        };
        var plan = new RepairPlan(RepairPlan.CurrentSchemaVersion, Guid.NewGuid(), finding.Code,
            RepairKind.ReconcileOriginalIntegrity, null, mediaId, null, media.RowVersion,
            $"{owner:D}|{media.OriginalHealthStatus ?? "UNRECORDED"}",
            relative, relative, sha, media.ByteLength, fact,
            expected == OriginalInspectionStatus.Match
                ? "Restore duplicate reuse eligibility after verifying the original and all required components."
                : "Record the original integrity problem and exclude it from duplicate reuse. Preserve every file and Media relation.",
            true, Guid.NewGuid(), _timeProvider.GetUtcNow());
        return OperationResult<RepairPlan>.Success(plan, plan.OperationId);
    }

    private async Task<OperationResult<RepairPlan>> PrepareDirectoryRepairAsync(
        HealthFinding finding, CancellationToken cancellationToken)
    {
        var relative = finding.ManagedRelativePath;
        if (relative is null || finding.ProfileId is not Guid profileId) return Unsupported(finding.Code);
        var path = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.Profiles, relative);
        if (!string.Equals(Path.GetDirectoryName(path), _catalog.Paths.ProfilesPath, StringComparison.OrdinalIgnoreCase))
            return Unsupported(finding.Code);
        var authority = await RetiredProfileDirectory.ReadAuthorityAsync(_catalog, relative, cancellationToken).ConfigureAwait(false);
        if (authority is null || authority.ProfileId != profileId || !RetiredProfileDirectory.IsEmptyTree(_catalog.Paths.Root, path))
            return OperationResult<RepairPlan>.Conflict(OperationErrorCode.RepairPlanStale,
                "The folder is no longer empty or its Profile Purge authority changed. Nothing was removed.");
        var plan = new RepairPlan(RepairPlan.CurrentSchemaVersion, Guid.NewGuid(), finding.Code,
            RepairKind.RemoveRetiredProfileDirectory, profileId, null, null, authority.RowVersion,
            authority.State, relative, relative, null, 0, RepairPhysicalFact.EmptyDirectory,
            "Remove only empty directories left by the permanently deleted Profile; preserve every file.",
            true, Guid.NewGuid(), _timeProvider.GetUtcNow());
        return OperationResult<RepairPlan>.Success(plan, plan.OperationId);
    }

    private async Task<OperationResult<RepairPlan>> PrepareManifestRepairAsync(
        HealthFinding finding,
        CancellationToken cancellationToken)
    {
        if (finding.ProfileId is not Guid profileId || profileId == Guid.Empty)
        {
            return OperationResult<RepairPlan>.Validation(
                OperationErrorCode.RepairNotSupported,
                "Manifest repair requires a stable ProfileId.");
        }

        var profile = (await _catalog.HealthReads.GetHealthProfileItemsAsync(profileId, cancellationToken)
                .ConfigureAwait(false))
            .SingleOrDefault();
        if (profile is null || profile.IsTrashed || string.IsNullOrWhiteSpace(profile.StorageToken)
            || profile.PathState != ManagedPathState.None)
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "The Profile is not in a stable active state for manifest regeneration.");
        }

        var canonicalFolder = _pathPlanner.PlanProfile(
            profile.ProfileId,
            profile.DisplayName,
            new ProfileStorageToken(profile.StorageToken)).ProfileFolderRelativePath;
        if (string.IsNullOrWhiteSpace(profile.CurrentManagedRelativePath))
        {
            return OperationResult<RepairPlan>.Validation(
                OperationErrorCode.RepairNotSupported,
                "This Profile has no persisted managed folder. Its placement must be reconciled before a manifest can be repaired.");
        }
        var currentFolder = NormalizeRelative(profile.CurrentManagedRelativePath);
        if (!string.Equals(currentFolder, canonicalFolder, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "Profile placement must be reconciled before its manifest is regenerated.");
        }

        string absoluteFolder;
        try
        {
            absoluteFolder = _catalog.Paths.ResolveVaultRelativePath(currentFolder);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return OperationResult<RepairPlan>.Validation(
                OperationErrorCode.RepairNotSupported,
                "The Profile folder is outside the managed Profiles area.");
        }

        if (!Directory.Exists(absoluteFolder))
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "The Profile folder no longer exists at the authoritative path.");
        }

        var inspection = ProfileManifestWriter.InspectManifest(absoluteFolder);
        var expectedFact = finding.Code switch
        {
            HealthFindingCode.ProfileManifestMissing => RepairPhysicalFact.ManifestMissing,
            HealthFindingCode.ProfileManifestMalformed => RepairPhysicalFact.ManifestMalformed,
            _ => RepairPhysicalFact.ManifestStale,
        };
        if (!MatchesManifestFact(
                inspection,
                expectedFact,
                profileId,
                profile.Kind,
                profile.DisplayName,
                Path.GetFileName(canonicalFolder)))
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "The manifest no longer matches the finding that was selected.");
        }

        var manifestRelative = $"{currentFolder.TrimEnd('/')}/{ProfileManifestWriter.ManifestFileName}";
        var manifestPath = Path.Combine(absoluteFolder, ProfileManifestWriter.ManifestFileName);
        var (length, sha256) = File.Exists(manifestPath)
            ? await FingerprintAsync(manifestPath, cancellationToken).ConfigureAwait(false)
            : ((long?)null, null);
        var plan = new RepairPlan(
            RepairPlan.CurrentSchemaVersion,
            Guid.NewGuid(),
            finding.Code,
            RepairKind.RegenerateProfileManifest,
            profileId,
            null,
            null,
            profile.RowVersion,
            $"ACTIVE|NONE|{profile.StorageToken}",
            manifestRelative,
            manifestRelative,
            sha256,
            length,
            expectedFact,
            "Regenerate manifest.json from current catalog authority.",
            IsMaterialOrDestructive: true,
            Guid.NewGuid(),
            _timeProvider.GetUtcNow());
        return OperationResult<RepairPlan>.Success(plan, plan.OperationId);
    }

    private async Task<OperationResult<RepairPlan>> PrepareMediaAssetRepairAsync(
        HealthFinding finding, CancellationToken cancellationToken)
    {
        if (finding.MediaId is not Guid assetId) return Unsupported(finding.Code);
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT aa.role, aa.relative_path, aa.byte_length, aa.sha256,
                   aa.contract_version, aa.row_version, aa.state,
                   EXISTS (SELECT 1 FROM jobs j WHERE j.owner_type = 'Media'
                       AND j.owner_id = aa.media_id AND j.kind = CASE
                           WHEN aa.role = 'BANNER' THEN 'GenerateBanner'
                           WHEN a.media_type = 'VIDEO' THEN 'GenerateVideoMediaAssets'
                           WHEN a.media_type = 'MODEL' THEN 'GenerateModelMediaAssets'
                           ELSE 'GenerateThumbnail' END
                       AND j.state IN ('PENDING','RUNNABLE','RUNNING','PAUSED','FAILED_RETRYABLE','WAITING_DEPENDENCY','WAITING_USER_INPUT')),
                   a.sha256, a.media_type, coalesce(a.current_managed_file_name,a.original_file_name), a.dependency_status, a.media_storage_token
            FROM media_assets aa JOIN media a ON a.media_id = aa.media_id
            WHERE aa.media_id = $id AND a.state = 'ACTIVE' AND a.trashed_at_ms IS NULL
            ORDER BY aa.role;
            """;
        command.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var role = reader.GetString(0);
            var relative = reader.GetString(1);
            var expectedLength = reader.GetInt64(2);
            var expectedSha = reader.GetString(3);
            var version = reader.GetInt32(4);
            var rowVersion = reader.GetInt64(5);
            string path;
            try { path = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, relative); }
            catch (Exception exception) when (exception is ArgumentException or IOException)
            {
                continue;
            }
            var structuralInvalid = false;
            var derivativeNeedsRefresh = false;
            if (role == "MODEL_RENDER")
            {
                string expectedModelPath;
                try { expectedModelPath = _pathPlanner.PlanMediaAsset(new MediaStorageToken(reader.GetString(12)),MediaAssetRole.ModelRender); }
                catch (ArgumentException) { continue; }
                if (reader.IsDBNull(8) || reader.GetString(9) != "MODEL" || reader.IsDBNull(10)
                    || !ModelRenderEligibility.IsEligible(reader.GetString(10),reader.GetString(11))
                    || relative != expectedModelPath)
                    continue;
                if (File.Exists(path))
                {
                    try
                    {
                        var valid = await new MediaAssetFileValidator().ValidateAsync(path,MediaAssetRole.ModelRender,cancellationToken,reader.GetString(8)).ConfigureAwait(false);
                        structuralInvalid = valid.ByteLength != expectedLength || valid.Sha256 != expectedSha;
                        derivativeNeedsRefresh = !structuralInvalid && valid.NeedsRefresh;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
                    { structuralInvalid = true; }
                }
            }
            var invalid = finding.Code switch
            {
                HealthFindingCode.ArtifactContractObsolete => derivativeNeedsRefresh || !MediaAssetContracts.IsSupported(DbEnum.ParseMediaAssetRole(role), version),
                HealthFindingCode.MediaAssetMissing => !File.Exists(path),
                HealthFindingCode.MediaAssetNeedsRepair => reader.GetString(6) == "NEEDS_REPAIR",
                _ => role == "MODEL_RENDER" ? structuralInvalid : File.Exists(path) &&
                     (new FileInfo(path).Length != expectedLength ||
                      !string.Equals((await FingerprintAsync(path, cancellationToken).ConfigureAwait(false)).Sha256,
                          expectedSha, StringComparison.Ordinal)),
            };
            if (!invalid) continue;
            if (reader.GetInt64(7) != 0)
                return OperationResult<RepairPlan>.Conflict(OperationErrorCode.RepairPlanStale,
                    "A durable MediaAsset repair job is already active for this Media role.");
            var plan = new RepairPlan(RepairPlan.CurrentSchemaVersion, Guid.NewGuid(), finding.Code,
                RepairKind.RegenerateMediaAsset, null, assetId, null, rowVersion,
                role, relative, relative, expectedSha, expectedLength,
                RepairPhysicalFact.ArtifactInvalid,
                $"Queue a background {role} MediaAsset rebuild from managed Media bytes.",
                true, Guid.NewGuid(), _timeProvider.GetUtcNow());
            return OperationResult<RepairPlan>.Success(plan, plan.OperationId);
        }
        return OperationResult<RepairPlan>.Conflict(OperationErrorCode.RepairPlanStale,
            "The MediaAsset no longer matches the selected Health finding.");
    }

    private static bool MatchesManifestFact(
        ManifestInspectionResult inspection,
        RepairPhysicalFact expected,
        Guid profileId,
        ProfileKind profileKind,
        string displayName,
        string folderName) => expected switch
        {
            RepairPhysicalFact.ManifestMissing => inspection.Status == ManifestStatus.Missing,
            RepairPhysicalFact.ManifestMalformed => inspection.Status == ManifestStatus.Malformed,
            RepairPhysicalFact.ManifestStale =>
                inspection.Status == ManifestStatus.Valid
                && inspection.Manifest is { } manifest
                && manifest.ProfileId == profileId
                && (!string.Equals(manifest.DisplayName, displayName, StringComparison.Ordinal)
                    || manifest.Kind != profileKind
                    || !string.Equals(manifest.FolderName, folderName, StringComparison.Ordinal)),
            _ => false,
        };

    private static async Task<(long? Length, string? Sha256)> FingerprintAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return (stream.Length, Convert.ToHexStringLower(hash));
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/').Trim('/');

    private static OperationResult<RepairPlan> Unsupported(string code) =>
        OperationResult<RepairPlan>.Validation(
            OperationErrorCode.RepairNotSupported,
            $"{code} has no conservative automatic repair.");
}
