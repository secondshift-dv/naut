using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.MediaTools;
using Neuterradise.App.SystemServices.Storage;
using SkiaSharp;

namespace Neuterradise.App.SystemServices.Jobs.Handlers;

public interface IGenerateThumbnailJobOperation : IAuthorizedJobOperation;

public sealed class GenerateThumbnailJobHandler : AuthorizedJobHandler
{
    public GenerateThumbnailJobHandler(IGenerateThumbnailJobOperation operation)
        : base("GenerateThumbnail", [JobLane.Cpu, JobLane.Media], "Media", operation,
            runOffCallingThread: true)
    {
    }
}

public sealed class PrepareMediaAssetJobOperation : IGenerateThumbnailJobOperation,
    IGenerateVideoMediaAssetsJobOperation, IGenerateModelMediaAssetsJobOperation
{
    private const int ContractVersion = 1;
    private readonly MediaReads _media;
    private readonly CatalogDb _catalog;
    private readonly MediaAssetReads _mediaAssets;
    private readonly MediaAssetWrites _writes;
    private readonly MediaAssetFileValidator _validator;
    private readonly VaultPaths _paths;
    private readonly ExternalToolResolver _tools;
    private readonly IProcessLauncher _launcher;
    private readonly ModelPreviewAdapterRegistry _models;

    public PrepareMediaAssetJobOperation(MediaReads media, CatalogDb catalog, VaultPaths paths,
        ExternalToolResolver tools, IProcessLauncher launcher, ModelPreviewAdapterRegistry models)
    {
        _catalog = catalog;
        _media = media;
        _mediaAssets = new MediaAssetReads(catalog);
        _validator = new MediaAssetFileValidator(tools: tools, launcher: launcher);
        _writes = new MediaAssetWrites(catalog, _validator);
        _paths = paths;
        _tools = tools;
        _launcher = launcher;
        _models = models;
    }

    public Task<JobExecutionResult> ExecuteAsync(JobExecutionContext context, CancellationToken ct) =>
        context.Kind == "GenerateVideoMediaAssets"
            ? ExecuteVideoAssetsAsync(context, ct)
            : context.Kind == "GenerateModelMediaAssets"
            ? ExecuteModelAssetsAsync(context, ct)
            : ExecuteRoleAsync(context, MediaAssetRole.Thumbnail, ct);

    private async Task<JobExecutionResult> ExecuteModelAssetsAsync(JobExecutionContext context, CancellationToken ct)
    {
        var source = await _media.GetMediaSourceAsync(context.OwnerId, ct).ConfigureAwait(false);
        if (source is null || !source.IsActive || source.MediaType != MediaType.Model)
            return Invalid("MODEL_SOURCE_NOT_COMMITTED", "Model assets require committed canonical Model media.");
        var path = source.ResolveManagedPath(_paths);
        if (path is null || source.Sha256 is null)
            return Invalid("MODEL_SOURCE_MISSING", "Canonical Model bytes or provenance are unavailable.");
        var thumbnail = await ExecuteRoleAsync(context, MediaAssetRole.Thumbnail, ct).ConfigureAwait(false);
        if (!thumbnail.IsSucceeded || !await ModelRenderEligibility.IsEligibleAsync(_catalog, source.MediaId, ct).ConfigureAwait(false))
            return thumbnail;
        if (!await NeedsGenerationAsync(source.MediaId, MediaAssetRole.ModelRender, ct).ConfigureAwait(false))
            return JobExecutionResult.Succeeded;
        var temp = _paths.ResolveContainedPath(VaultPathArea.TempMediaAssets, $"{source.MediaId:N}/{Guid.NewGuid():N}.nfig");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            await GlbThumbnailPreviewAdapter.BuildModelRenderAsync(path, source.Sha256, temp, ct).ConfigureAwait(false);
            await _writes.PublishMediaAssetAsync(source.MediaId, MediaAssetRole.ModelRender, ContractVersion, temp, ct: ct).ConfigureAwait(false);
            return JobExecutionResult.Succeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return JobExecutionResult.Cancelled("Model preparation was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or NotSupportedException)
        {
            return Invalid("MODEL_RENDER_PREPARATION_FAILED", ex.Message);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<JobExecutionResult> ExecuteVideoAssetsAsync(
        JobExecutionContext context,
        CancellationToken ct)
    {
        var source = await _media.GetMediaSourceAsync(context.OwnerId, ct).ConfigureAwait(false);
        if (source is null || source.IsRetired)
            return JobExecutionResult.Cancelled("The Media is no longer available for preparation.");
        if (source.MediaType != MediaType.Video)
            return Invalid("MEDIA_ASSET_MEDIA_TYPE_INVALID", "Video MediaAsset preparation requires video Media.");

        var path = source.ResolveManagedPath(_paths);
        if (path is null)
            return Invalid("MEDIA_ASSET_SOURCE_MISSING", "The managed Media bytes are unavailable.");

        var needThumbnail = await NeedsGenerationAsync(source.MediaId, MediaAssetRole.Thumbnail, ct)
            .ConfigureAwait(false);
        var needHover = await NeedsGenerationAsync(source.MediaId, MediaAssetRole.Hover, ct)
            .ConfigureAwait(false);
        if (!needThumbnail && !needHover)
            return JobExecutionResult.Succeeded;

        var representativeTimestamp = await ReadRepresentativeTimestampAsync(
            source.MediaId, source.DurationMilliseconds, ct).ConfigureAwait(false);
        var hoverStart = Math.Max(0, Math.Min(
            representativeTimestamp - 500,
            Math.Max(0, (source.DurationMilliseconds ?? 0) - 5800)));
        var thumbnailOffset = Math.Max(0, representativeTimestamp - hoverStart);

        var tempDirectory = _paths.ResolveContainedPath(
            VaultPathArea.TempMediaAssets, $"{source.MediaId:N}/{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var thumbnailTemp = Path.Combine(tempDirectory, "thumbnail.webp");
        var frameTemp = Path.Combine(tempDirectory, "thumbnail.jpg");
        var hoverTemp = Path.Combine(tempDirectory, "hover.mp4");

        try
        {
            var ffmpeg = _tools.TryResolveExecutablePath(ExternalToolResolver.FfmpegToolId);
            if (ffmpeg is null)
                return JobExecutionResult.Failed(
                    JobFailureClassification.MissingRequiredToolOrModel,
                    "MEDIA_ASSET_TOOL_UNAVAILABLE",
                    "The approved video encoder is unavailable.");

            IReadOnlyList<string> args;
            if (needThumbnail && needHover)
            {
                var thumbnailOffsetSeconds = TimestampSeconds(thumbnailOffset);
                var filter =
                    $"[0:v]setpts=PTS-STARTPTS,split=2[thumbsrc][hoversrc];" +
                    $"[thumbsrc]select='gte(t,{thumbnailOffsetSeconds})'," +
                    "scale='min(1024,iw)':'min(1024,ih)':force_original_aspect_ratio=decrease[thumb];" +
                    "[hoversrc]scale='min(1280,iw)':'min(720,ih)':force_original_aspect_ratio=decrease:" +
                    "force_divisible_by=2,format=yuv420p[hover]";
                args =
                [
                    "-y", "-ss", TimestampSeconds(hoverStart), "-i", path,
                    "-filter_complex", filter,
                    "-map", "[thumb]", "-frames:v", "1", "-an",
                    "-c:v", "mjpeg", "-threads", "2", frameTemp,
                    "-map", "[hover]", "-t", "5.8", "-an",
                    "-c:v", "libopenh264", "-threads", "2",
                    "-movflags", "+faststart", hoverTemp
                ];
            }
            else if (needThumbnail)
            {
                args =
                [
                    "-y", "-ss", TimestampSeconds(representativeTimestamp), "-i", path,
                    "-frames:v", "1",
                    "-vf", "scale='min(1024,iw)':'min(1024,ih)':force_original_aspect_ratio=decrease",
                    "-an", "-c:v", "mjpeg", "-threads", "2", frameTemp
                ];
            }
            else
            {
                args =
                [
                    "-y", "-ss", TimestampSeconds(hoverStart), "-i", path, "-t", "5.8",
                    "-map", "0:v:0", "-an",
                    "-vf", "scale='min(1280,iw)':'min(720,ih)':force_original_aspect_ratio=decrease:" +
                           "force_divisible_by=2,format=yuv420p",
                    "-c:v", "libopenh264", "-threads", "2",
                    "-movflags", "+faststart", hoverTemp
                ];
            }

            var result = await _launcher.RunAsync(
                new ProcessRunRequest(ffmpeg, args, Timeout: TimeSpan.FromMinutes(3)), ct)
                .ConfigureAwait(false);
            if (result.TimedOut || result.ExitCode != 0)
                return Invalid("MEDIA_ASSET_RENDER_FAILED",
                    "The managed video could not produce the required MediaAssets.");

            if (needThumbnail)
            {
                EncodeWebp(frameTemp, thumbnailTemp);
                await _writes.PublishMediaAssetAsync(
                    source.MediaId,
                    MediaAssetRole.Thumbnail,
                    ContractVersion,
                    thumbnailTemp,
                    representativeTimestamp,
                    ct: ct).ConfigureAwait(false);
            }

            if (needHover)
            {
                await _writes.PublishMediaAssetAsync(
                    source.MediaId,
                    MediaAssetRole.Hover,
                    ContractVersion,
                    hoverTemp,
                    hoverStart,
                    ct: ct).ConfigureAwait(false);
            }

            return JobExecutionResult.Succeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return JobExecutionResult.Cancelled("Video MediaAsset preparation was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return Invalid("MEDIA_ASSET_PREPARATION_FAILED", ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<bool> NeedsGenerationAsync(
        Guid mediaId,
        MediaAssetRole role,
        CancellationToken ct)
    {
        var existing = await _mediaAssets.GetOwnedMediaAssetAsync(mediaId, role, ct)
            .ConfigureAwait(false);
        if (existing?.State != MediaAssetState.Ready)
            return true;

        try
        {
            var sourceHash = role == MediaAssetRole.ModelRender
                ? (await _media.GetMediaSourceAsync(mediaId, ct).ConfigureAwait(false))?.Sha256 : null;
            if (role == MediaAssetRole.ModelRender && sourceHash is null)
                throw new InvalidDataException("ModelRender source provenance is missing.");
            var current = await _validator.ValidateAsync(
                _paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, existing.RelativePath),
                role,
                ct, sourceHash).ConfigureAwait(false);
            if (current.ByteLength == existing.ByteLength
                && current.Sha256 == existing.Sha256
                && existing.ContractVersion == ContractVersion)
                return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
        }

        await _writes.MarkMediaNeedsRepairAsync(mediaId, role, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<JobExecutionResult> ExecuteRoleAsync(JobExecutionContext context,
        MediaAssetRole role, CancellationToken ct)
    {
        var source = await _media.GetMediaSourceAsync(context.OwnerId, ct).ConfigureAwait(false);
        if (source is null || source.IsRetired)
            return JobExecutionResult.Cancelled("The Media is no longer available for preparation.");

        var expectedType = context.Kind switch
        {
            "GenerateModelMediaAssets" => MediaType.Model,
            "GenerateThumbnail" => MediaType.Image,
            _ => throw new InvalidOperationException("Unsupported MediaAsset job kind."),
        };
        if (source.MediaType != expectedType || role != MediaAssetRole.Thumbnail)
            return Invalid("MEDIA_ASSET_MEDIA_TYPE_INVALID", "The MediaAsset job does not match its Media type.");

        var path = source.ResolveManagedPath(_paths);
        if (path is null)
            return Invalid("MEDIA_ASSET_SOURCE_MISSING", "The managed Media bytes are unavailable.");
        if (!await NeedsGenerationAsync(source.MediaId, role, ct).ConfigureAwait(false))
            return JobExecutionResult.Succeeded;

        var temp = _paths.ResolveContainedPath(
            VaultPathArea.TempMediaAssets,
            $"{source.MediaId:N}/{Guid.NewGuid():N}.webp");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        string? modelPreviewPath = null;
        try
        {
            if (source.MediaType == MediaType.Model)
            {
                var extension = Path.GetExtension(path);
                var adapter = _models.ResolveAdapter(new ModelProbeInput(path, extension));
                if (!adapter.Capabilities.HasFlag(ModelAdapterCapabilities.StaticThumbnail))
                    return JobExecutionResult.Succeeded;
                var descriptor = await _models.GetOrGeneratePreviewAsync(
                    new ModelPreviewRequest(source.MediaId, path, OutputDirectory: Path.GetDirectoryName(temp), Extension: extension), ct)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(descriptor.StaticThumbnailPath))
                    return Invalid("MODEL_THUMBNAIL_MISSING",
                        "The selected model adapter declared a thumbnail but returned no image.");
                modelPreviewPath = descriptor.StaticThumbnailPath;
                EncodeWebp(modelPreviewPath, temp);
            }
            else
            {
                EncodeWebp(path, temp);
            }

            await _writes.PublishMediaAssetAsync(
                source.MediaId,
                role,
                ContractVersion,
                temp,
                sourceTimestampMs: null,
                ct: ct).ConfigureAwait(false);
            return JobExecutionResult.Succeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return JobExecutionResult.Cancelled("MediaAsset preparation was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or NotSupportedException)
        {
            return Invalid("MEDIA_ASSET_PREPARATION_FAILED", ex.Message);
        }
        finally
        {
            try
            {
                if (modelPreviewPath is not null
                    && string.Equals(Path.GetDirectoryName(modelPreviewPath), Path.GetDirectoryName(temp), StringComparison.OrdinalIgnoreCase)
                    && File.Exists(modelPreviewPath))
                    File.Delete(modelPreviewPath);
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<long> ReadRepresentativeTimestampAsync(Guid assetId, long? durationMs,
        CancellationToken ct)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sampled_timestamp_ms FROM face_detections
            WHERE media_id = $asset AND sampled_timestamp_ms IS NOT NULL
            ORDER BY COALESCE(confidence, 0) DESC, sampled_timestamp_ms, face_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$asset", DbGuid.Format(assetId));
        var evidence = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var fallback = durationMs is > 0 ? durationMs.Value / 3 : 0;
        return Math.Max(0, Math.Min(evidence is long timestamp ? timestamp : fallback,
            Math.Max(0, (durationMs ?? long.MaxValue) - 100)));
    }

    private static string TimestampSeconds(long milliseconds) =>
        FormattableString.Invariant($"{milliseconds / 1000d:0.###}");

    private static void EncodeWebp(string input, string output)
    {
        using var bitmap = TargetSizeDecoder.Decode(input, 1024, 1024)
            ?? throw new InvalidDataException("The model or image adapter did not return a decodable still.");
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 85)
            ?? throw new InvalidDataException("The thumbnail encoder produced no bytes.");
        using var file = File.Create(output);
        encoded.SaveTo(file);
    }

    private static JobExecutionResult Invalid(string code, string detail) =>
        JobExecutionResult.Failed(JobFailureClassification.DeterministicInvalidInput, code, detail);
}
