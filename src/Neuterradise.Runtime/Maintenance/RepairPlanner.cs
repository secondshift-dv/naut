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
        or HealthFindingCode.ArtifactContractObsolete or HealthFindingCode.RetiredProfileDirectory;

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
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
                    { structuralInvalid = true; }
                }
            }
            var invalid = finding.Code switch
            {
                HealthFindingCode.ArtifactContractObsolete => version != 1,
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
