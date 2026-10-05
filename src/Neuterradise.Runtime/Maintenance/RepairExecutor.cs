using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Import;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.Maintenance;

public sealed class RepairExecutor
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _vaultExecutionGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly CatalogDb _catalog;
    private readonly ProfileManifestWriter _manifestWriter;
    private readonly OperationExecution _execution;

    /// <summary>Canonical operation kind of an executed repair plan.</summary>
    public const string ExecuteRepairKind = "MAINTENANCE_EXECUTE_REPAIR";

    public RepairExecutor(
        CatalogDb catalog,
        ProfileManifestWriter? manifestWriter = null,
        StructuredDiagnostics? diagnostics = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _manifestWriter = manifestWriter ?? new ProfileManifestWriter(catalog.Paths);
        _execution = new OperationExecution(diagnostics);
    }

    /// <summary>
    /// Executes one prepared repair plan. The plan already carries the logical OperationId, so a
    /// retry resumes the same operation instead of starting a second one, and cancellation or an
    /// infrastructure fault returns a canonical result instead of an exception to the surface.
    /// </summary>
    public Task<OperationResult<RepairPlan>> ExecuteAsync(
        RepairPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var context = OperationContext.Resume(
            ExecuteRepairKind,
            plan.OperationId,
            plan.PreparedAtUtc,
            cancellationToken) with
        {
            ProfileId = plan.ProfileId,
            MediaId = plan.MediaId,
            ImportItemId = plan.ImportItemId,
        };

        return _execution.RunAsync<RepairPlan>(context, ExecuteGatedAsync);

        async Task<OperationResult<RepairPlan>> ExecuteGatedAsync(OperationContext operation)
        {
            var vaultKey = Path.GetFullPath(_catalog.Paths.Root);
            var executionGate = _vaultExecutionGates.GetOrAdd(vaultKey, static _ => new SemaphoreSlim(1, 1));
            await executionGate.WaitAsync(operation.CancellationToken).ConfigureAwait(false);
            try
            {
                return await ExecuteCoreAsync(plan, operation.CancellationToken).ConfigureAwait(false);
            }
            finally
            {
                executionGate.Release();
            }
        }
    }

    private async Task<OperationResult<RepairPlan>> ExecuteCoreAsync(
        RepairPlan plan,
        CancellationToken cancellationToken)
    {
        var persisted = await _catalog.MaintenanceWrites
            .ReadRepairOperationAsync(plan.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (persisted is null)
        {
            return OperationResult<RepairPlan>.NotFound(
                OperationErrorCode.RepairPlanNotFound,
                "The persisted Vault Repair operation was not found.");
        }

        if (persisted.Plan != plan)
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "The supplied repair plan does not match its persisted operation.");
        }

        if (string.Equals(persisted.State, "COMPLETED", StringComparison.Ordinal))
        {
            return OperationResult<RepairPlan>.Success(plan, plan.OperationId);
        }

        if (string.Equals(persisted.State, "STALE", StringComparison.Ordinal))
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "The repair plan was already invalidated by newer authority or physical facts.");
        }

        var checkpointStarted = persisted.State is "EXECUTING" or "FAILED_RETRYABLE";
        var readiness = await RevalidateAsync(plan, checkpointStarted, cancellationToken).ConfigureAwait(false);
        if (readiness == RepairReadiness.Stale)
        {
            await _catalog.MaintenanceWrites.SetRepairOperationStateAsync(
                plan.OperationId,
                "STALE",
                OperationErrorCode.RepairPlanStale,
                "Authority or physical facts changed after repair preparation.",
                cancellationToken).ConfigureAwait(false);
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "Authority or physical facts changed after repair preparation.");
        }

        if (!string.Equals(persisted.State, "EXECUTING", StringComparison.Ordinal))
        {
            await _catalog.MaintenanceWrites.SetRepairOperationStateAsync(
                plan.OperationId,
                "EXECUTING",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        StorageOperationResult actionResult;
        if (readiness == RepairReadiness.AlreadyApplied)
        {
            actionResult = new StorageOperationResult(StorageOperationStatus.AlreadyCompleted);
        }
        else
        {
            actionResult = plan.Kind switch
            {
                RepairKind.RegenerateProfileManifest when plan.ProfileId is Guid profileId =>
                    await _manifestWriter.RegenerateManifestAsync(
                        _catalog,
                        profileId,
                        plan.OperationId,
                        cancellationToken).ConfigureAwait(false),
                RepairKind.RegenerateMediaAsset when plan.MediaId is Guid assetId =>
                    await QueueMediaAssetRepairAsync(plan, assetId, cancellationToken).ConfigureAwait(false),
                RepairKind.RemoveRetiredProfileDirectory =>
                    await RemoveRetiredDirectoryAsync(plan, cancellationToken).ConfigureAwait(false),
                _ => new StorageOperationResult(
                    StorageOperationStatus.NeedsAttention,
                    SafeErrorDetail: "The persisted repair kind has no executor."),
            };
        }

        if (!actionResult.IsSuccess)
        {
            await _catalog.MaintenanceWrites.SetRepairOperationStateAsync(
                plan.OperationId,
                "FAILED_RETRYABLE",
                OperationErrorCode.RepairExecutionFailed,
                SafeFailureDetail(actionResult),
                CancellationToken.None).ConfigureAwait(false);
            return OperationResult<RepairPlan>.NeedsAttention(
                OperationErrorCode.RepairExecutionFailed,
                "The repair did not complete and remains safe to retry.",
                plan.OperationId);
        }

        await AppendCompletionActivityAsync(plan).ConfigureAwait(false);
        await _catalog.MaintenanceWrites.SetRepairOperationStateAsync(
            plan.OperationId,
            "COMPLETED",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return OperationResult<RepairPlan>.Success(plan, plan.OperationId);
    }

    private async Task<RepairReadiness> RevalidateAsync(
        RepairPlan plan,
        bool checkpointStarted,
        CancellationToken cancellationToken) => plan.Kind switch
        {
            RepairKind.RegenerateProfileManifest =>
                await RevalidateManifestAsync(plan, checkpointStarted, cancellationToken).ConfigureAwait(false),
            RepairKind.RegenerateMediaAsset =>
                await RevalidateMediaAssetAsync(plan, cancellationToken).ConfigureAwait(false),
            RepairKind.RemoveRetiredProfileDirectory =>
                await RevalidateDirectoryAsync(plan, checkpointStarted, cancellationToken).ConfigureAwait(false),
            _ => RepairReadiness.Stale,
        };

    private async Task<RepairReadiness> RevalidateDirectoryAsync(RepairPlan plan, bool checkpointStarted,
        CancellationToken cancellationToken)
    {
        var relative = plan.TargetPathOrRelative;
        var path = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.Profiles, relative);
        if (plan.SourcePathOrRelative != relative || plan.CurrentPhysicalFact != RepairPhysicalFact.EmptyDirectory
            || !string.Equals(Path.GetDirectoryName(path), _catalog.Paths.ProfilesPath, StringComparison.OrdinalIgnoreCase))
            return RepairReadiness.Stale;
        var authority = await RetiredProfileDirectory.ReadAuthorityAsync(_catalog, relative, cancellationToken).ConfigureAwait(false);
        if (authority?.ProfileId != plan.ProfileId
            || authority?.RowVersion != plan.ExpectedRowVersion
            || !string.Equals(authority?.State, plan.ExpectedAuthorityState, StringComparison.Ordinal))
            return RepairReadiness.Stale;
        if (!Directory.Exists(path)) return checkpointStarted ? RepairReadiness.AlreadyApplied : RepairReadiness.Stale;
        return RetiredProfileDirectory.IsEmptyTree(_catalog.Paths.Root, path) ? RepairReadiness.Ready : RepairReadiness.Stale;
    }

    private async Task<StorageOperationResult> RemoveRetiredDirectoryAsync(RepairPlan plan,
        CancellationToken cancellationToken)
    {
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        if (await RevalidateDirectoryAsync(plan, true, cancellationToken).ConfigureAwait(false) != RepairReadiness.Ready)
            return new StorageOperationResult(StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The folder or its authority changed. Every file was preserved.");
        cancellationToken.ThrowIfCancellationRequested();
        var path = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.Profiles, plan.TargetPathOrRelative);
        RetiredProfileDirectory.RemoveEmptyTree(_catalog.Paths.Root, path);
        return new StorageOperationResult(StorageOperationStatus.Success);
    }

    private async Task<RepairReadiness> RevalidateMediaAssetAsync(
        RepairPlan plan, CancellationToken cancellationToken)
    {
        if (plan.MediaId is not Guid assetId) return RepairReadiness.Stale;
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT aa.row_version, aa.contract_version, aa.byte_length, aa.sha256, a.state,
                   a.trashed_at_ms, aa.state, a.sha256, a.media_type,
                   coalesce(a.current_managed_file_name,a.original_file_name), a.dependency_status, a.media_storage_token
            FROM media_assets aa JOIN media a ON a.media_id = aa.media_id
            WHERE aa.media_id = $asset AND aa.role = $role AND aa.relative_path = $path;
            """;
        command.Parameters.AddWithValue("$asset", DbGuid.Format(assetId));
        command.Parameters.AddWithValue("$role", plan.ExpectedAuthorityState);
        command.Parameters.AddWithValue("$path", plan.SourcePathOrRelative);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetInt64(0) != plan.ExpectedRowVersion
            || reader.GetString(4) != "ACTIVE" || !reader.IsDBNull(5)
            || reader.GetInt64(2) != plan.ExpectedPhysicalByteLength
            || reader.GetString(3) != plan.ExpectedPhysicalSha256)
            return RepairReadiness.Stale;
        var version = reader.GetInt32(1);
        string path;
        try { path = _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, plan.SourcePathOrRelative); }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        { return RepairReadiness.Stale; }
        if (plan.ExpectedAuthorityState == "MODEL_RENDER")
        {
            string expectedModelPath;
            try { expectedModelPath = new ManagedPathPlanner().PlanMediaAsset(new MediaStorageToken(reader.GetString(11)),MediaAssetRole.ModelRender); }
            catch (ArgumentException) { return RepairReadiness.Stale; }
            if (reader.IsDBNull(7) || reader.GetString(8) != "MODEL" || reader.IsDBNull(9)
                || !ModelRenderEligibility.IsEligible(reader.GetString(9),reader.GetString(10))
                || plan.SourcePathOrRelative != expectedModelPath)
                return RepairReadiness.Stale;
            if (version != 1 || reader.GetString(6) == "NEEDS_REPAIR" || !File.Exists(path)) return RepairReadiness.Ready;
            try
            {
                var valid = await new MediaAssetFileValidator().ValidateAsync(path,MediaAssetRole.ModelRender,cancellationToken,reader.GetString(7)).ConfigureAwait(false);
                return valid.ByteLength == plan.ExpectedPhysicalByteLength && valid.Sha256 == plan.ExpectedPhysicalSha256
                    ? RepairReadiness.Stale : RepairReadiness.Ready;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
            { return RepairReadiness.Ready; }
        }
        if (version != 1 || reader.GetString(6) == "NEEDS_REPAIR"
            || !File.Exists(path) || new FileInfo(path).Length != plan.ExpectedPhysicalByteLength)
            return RepairReadiness.Ready;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return hash == plan.ExpectedPhysicalSha256 ? RepairReadiness.Stale : RepairReadiness.Ready;
    }

    private async Task<StorageOperationResult> QueueMediaAssetRepairAsync(
        RepairPlan plan, Guid assetId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT media_type FROM media WHERE media_id = $id AND state = 'ACTIVE';";
        command.Parameters.AddWithValue("$id", DbGuid.Format(assetId));
        var mediaType = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (mediaType is null) return new StorageOperationResult(StorageOperationStatus.NeedsAttention);
        var kind = mediaType == "VIDEO" ? "GenerateVideoMediaAssets"
            : mediaType == "MODEL" ? "GenerateModelMediaAssets" : "GenerateThumbnail";
        var lane = kind == "GenerateThumbnail" && mediaType == "IMAGE" ? JobLane.Cpu : JobLane.Media;
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        await using var insert = transaction.CreateCommand("""
            INSERT INTO jobs(job_id,kind,lane,state,priority,owner_type,owner_id,attempt,max_attempts,checkpoint_json,created_at_ms)
            SELECT $id,$kind,$lane,'PENDING',$priority,'Media',$media,0,$attempts,'{}',$now
            WHERE NOT EXISTS (SELECT 1 FROM jobs WHERE owner_type = 'Media' AND owner_id = $media
                AND kind = $kind AND state IN ('PENDING','RUNNABLE','RUNNING','PAUSED','FAILED_RETRYABLE','WAITING_DEPENDENCY','WAITING_USER_INPUT'));
            """);
        insert.Parameters.AddWithValue("$id",DbGuid.Format(plan.RepairPlanId));
        insert.Parameters.AddWithValue("$kind",kind); insert.Parameters.AddWithValue("$lane",DbEnum.Format(lane));
        insert.Parameters.AddWithValue("$priority",JobPriorityPolicy.PriorityBackground);
        insert.Parameters.AddWithValue("$media",DbGuid.Format(assetId)); insert.Parameters.AddWithValue("$attempts",JobRetryPolicy.DefaultMaxAttempts);
        insert.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        JobSignals.Raise();
        return new StorageOperationResult(StorageOperationStatus.Success);
    }

    private async Task<RepairReadiness> RevalidateManifestAsync(
        RepairPlan plan,
        bool checkpointStarted,
        CancellationToken cancellationToken)
    {
        if (plan.ProfileId is not Guid profileId)
        {
            return RepairReadiness.Stale;
        }

        var healthProfile = (await _catalog.HealthReads.GetHealthProfileItemsAsync(profileId, cancellationToken)
                .ConfigureAwait(false))
            .SingleOrDefault();
        if (healthProfile is null
            || healthProfile.RowVersion != plan.ExpectedRowVersion
            || healthProfile.IsTrashed
            || healthProfile.PathState != ManagedPathState.None
            || string.IsNullOrWhiteSpace(healthProfile.CurrentManagedRelativePath)
            || !string.Equals(
                CombineRelative(healthProfile.CurrentManagedRelativePath, ProfileManifestWriter.ManifestFileName),
                plan.TargetPathOrRelative,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                $"{DbEnum.Format(MediaState.Active)}|{DbEnum.Format(ManagedPathState.None)}|{healthProfile.StorageToken}",
                plan.ExpectedAuthorityState,
                StringComparison.Ordinal))
        {
            return RepairReadiness.Stale;
        }

        string manifestPath;
        try
        {
            manifestPath = _catalog.Paths.ResolveVaultRelativePath(plan.TargetPathOrRelative);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return RepairReadiness.Stale;
        }

        var folder = Path.GetDirectoryName(manifestPath);
        if (folder is null || !Directory.Exists(folder))
        {
            return RepairReadiness.Stale;
        }

        if (checkpointStarted && await ManifestMatchesAuthorityAsync(profileId, folder, cancellationToken).ConfigureAwait(false))
        {
            return RepairReadiness.AlreadyApplied;
        }

        return await PhysicalFactMatchesAsync(plan, manifestPath, cancellationToken).ConfigureAwait(false)
            ? RepairReadiness.Ready
            : RepairReadiness.Stale;
    }

    private async Task<bool> ManifestMatchesAuthorityAsync(
        Guid profileId,
        string folder,
        CancellationToken cancellationToken)
    {
        var detail = await _catalog.ProfileReads.GetDetailAsync(profileId, cancellationToken).ConfigureAwait(false);
        var inspection = ProfileManifestWriter.InspectManifest(folder);
        if (detail is null || inspection.Status != ManifestStatus.Valid || inspection.Manifest is null)
        {
            return false;
        }

        var expected = ProfileManifestWriter.CreateManifest(detail, Path.GetFileName(folder));
        return inspection.Manifest == expected;
    }

    private static async Task<bool> PhysicalFactMatchesAsync(
        RepairPlan plan,
        string path,
        CancellationToken cancellationToken)
    {
        if (plan.CurrentPhysicalFact == RepairPhysicalFact.ManifestMissing)
        {
            return !File.Exists(path);
        }

        if (!File.Exists(path)
            || plan.ExpectedPhysicalSha256 is null
            || plan.ExpectedPhysicalByteLength is null)
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        if (stream.Length != plan.ExpectedPhysicalByteLength.Value)
        {
            return false;
        }

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexStringLower(hash),
            plan.ExpectedPhysicalSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task AppendCompletionActivityAsync(RepairPlan plan)
    {
        var payload = JsonSerializer.Serialize(new
        {
            repairPlanId = plan.RepairPlanId,
            repairKind = plan.Kind.ToString(),
            findingCode = plan.FindingCode,
        });
        await _catalog.ActivityWrites.AppendAsync(new ActivityEntryPersistence(
            plan.RepairPlanId,
            ActivityEventType.LibraryRepairCompleted,
            plan.ProfileId,
            plan.MediaId,
            null,
            plan.OperationId,
            payload,
            plan.PreparedAtUtc), CancellationToken.None).ConfigureAwait(false);
    }

    private static string SafeFailureDetail(StorageOperationResult result) =>
        string.IsNullOrWhiteSpace(result.SafeErrorDetail)
            ? $"Storage operation reported {result.Status}."
            : result.SafeErrorDetail;

    private static string CombineRelative(string directory, string fileName) =>
        $"{directory.Replace('\\', '/').TrimEnd('/')}/{fileName}";

    private enum RepairReadiness
    {
        Ready,
        AlreadyApplied,
        Stale
    }
}
