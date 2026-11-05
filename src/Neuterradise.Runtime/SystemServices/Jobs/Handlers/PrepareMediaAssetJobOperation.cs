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
    // Orientation-aware ceilings preserve portrait detail without upscaling the original.
    private const string HoverScale = "scale=w='if(gte(iw,ih),min(854,iw),min(480,iw))':" +
        "h='if(gte(iw,ih),min(480,ih),min(854,ih))':force_original_aspect_ratio=decrease:" +
        "force_divisible_by=2,format=yuv420p";
    private static readonly string BannerScale = HoverScale.Replace("854", "1600", StringComparison.Ordinal).Replace("480", "900", StringComparison.Ordinal);
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

    public async Task<JobExecutionResult> ExecuteAsync(JobExecutionContext context, CancellationToken ct)
    {
        var result = context.Kind is "GenerateVideoMediaAssets" or "GenerateBanner"
            ? await ExecuteVideoAssetsAsync(context, ct).ConfigureAwait(false)
            : context.Kind == "GenerateModelMediaAssets"
            ? await ExecuteModelAssetsAsync(context, ct).ConfigureAwait(false)
            : await ExecuteRoleAsync(context, MediaAssetRole.Thumbnail, ct).ConfigureAwait(false);
        if (context.Kind == "GenerateBanner" && !result.IsSucceeded && !ct.IsCancellationRequested)
        {
            await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT EXISTS(SELECT 1 FROM profile_banner_requests WHERE job_id=$job AND origin='INITIAL') AND NOT EXISTS(SELECT 1 FROM profile_banner_requests WHERE job_id=$job AND origin='ADDED');";
            read.Parameters.AddWithValue("$job", DbGuid.Format(context.JobId));
            var initialOnly = Convert.ToInt32(await read.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1;
            if (initialOnly)
                return await ExecuteVideoAssetsAsync(context, ct, suppressBanner: true).ConfigureAwait(false);
        }
        return result;
    }

    private async Task<JobExecutionResult> ExecuteModelAssetsAsync(JobExecutionContext context, CancellationToken ct)
    {
        var source = await _media.GetMediaSourceAsync(context.OwnerId, ct).ConfigureAwait(false);
        if (source is null || !source.IsActive || source.MediaType != MediaType.Model)
            return Invalid("MODEL_SOURCE_NOT_COMMITTED", "Model assets require committed canonical Model media.");
        var path = source.ResolveManagedPath(_paths);
        if (path is null || source.Sha256 is null)
            return Invalid("MODEL_SOURCE_MISSING", "Canonical Model bytes or provenance are unavailable.");
        if (!await ModelRenderEligibility.IsEligibleAsync(_catalog, source.MediaId, ct).ConfigureAwait(false))
            return await ExecuteRoleAsync(context, MediaAssetRole.Thumbnail, ct).ConfigureAwait(false);
        if (!await NeedsGenerationAsync(source.MediaId, MediaAssetRole.Thumbnail, ct).ConfigureAwait(false)
            && !await NeedsGenerationAsync(source.MediaId, MediaAssetRole.ModelRender, ct).ConfigureAwait(false))
            return JobExecutionResult.Succeeded;
        var tempDirectory = _paths.ResolveContainedPath(VaultPathArea.TempMediaAssets, $"{source.MediaId:N}/{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var temp = Path.Combine(tempDirectory, "scene.nfig");
        try
        {
            string? inputHash = null;
            if (!string.Equals(Path.GetExtension(path), ".glb", StringComparison.OrdinalIgnoreCase))
            {
                path = await ModelFigureConversion.ConvertAsync(source, _media, _paths, _tools, _launcher, tempDirectory, ct).ConfigureAwait(false);
                await using var converted = File.OpenRead(path);
                inputHash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(converted, ct).ConfigureAwait(false));
            }
            var thumbnail = await ExecuteRoleAsync(context, MediaAssetRole.Thumbnail, ct, path).ConfigureAwait(false);
            if (!thumbnail.IsSucceeded) return thumbnail;
            if (!await NeedsGenerationAsync(source.MediaId, MediaAssetRole.ModelRender, ct).ConfigureAwait(false))
                return JobExecutionResult.Succeeded;
            await GlbThumbnailPreviewAdapter.BuildModelRenderAsync(path, source.Sha256, temp, ct, inputHash).ConfigureAwait(false);
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
            try { if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<JobExecutionResult> ExecuteVideoAssetsAsync(
        JobExecutionContext context,
        CancellationToken ct, bool suppressBanner = false)
    {
        var source = await _media.GetMediaSourceAsync(context.OwnerId, ct).ConfigureAwait(false);
        if (source is null || !source.IsActive)
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
        var banners = new Neuterradise.App.Profiles.ProfileBannerOperations(_catalog);
        var bannerRequested = context.Kind == "GenerateBanner" && !suppressBanner;
        var small = await banners.IsSmallSourceAsync(source.MediaId, ct).ConfigureAwait(false);
        if (bannerRequested && small && !needHover)
        {
            var existingHover = await _mediaAssets.GetOwnedMediaAssetAsync(source.MediaId, MediaAssetRole.Hover, ct).ConfigureAwait(false);
            if (existingHover?.ContractVersion != 2 && !await _writes.TryAcceptHoverContractAsync(source.MediaId, ct).ConfigureAwait(false))
            {
                await _writes.MarkMediaNeedsRepairAsync(source.MediaId, MediaAssetRole.Hover, ct).ConfigureAwait(false);
                needHover = true;
            }
        }
        var needBanner = bannerRequested && !small
            && await NeedsGenerationAsync(source.MediaId, MediaAssetRole.Banner, ct).ConfigureAwait(false);
        if (!needThumbnail && !needHover && !needBanner)
        {
            if (bannerRequested && !await banners.CompleteRequestsAsync(source.MediaId, ct).ConfigureAwait(false))
                return Invalid("BANNER_MEMBERSHIP_NOT_COMMITTED", "The Banner request no longer has an eligible Profile relation.");
            return JobExecutionResult.Succeeded;
        }

        var representativeTimestamp = await ReadRepresentativeTimestampAsync(
            source.MediaId, source.DurationMilliseconds, ct).ConfigureAwait(false);
        var hoverStart = Math.Max(0, Math.Min(
            representativeTimestamp - 500,
            Math.Max(0, (source.DurationMilliseconds ?? 0) - 5800)));
        if (bannerRequested && (await _mediaAssets.GetOwnedMediaAssetAsync(source.MediaId, MediaAssetRole.Hover, ct).ConfigureAwait(false)) is not null)
        {
            var hover = await _mediaAssets.GetOwnedMediaAssetAsync(source.MediaId, MediaAssetRole.Hover, ct).ConfigureAwait(false);
            if (hover?.SourceTimestampMs is not { } timestamp)
                return Invalid("BANNER_TIMESTAMP_UNAVAILABLE", "The prepared Hover has no authoritative source timestamp.");
            hoverStart = timestamp;
        }
        var thumbnailOffset = Math.Max(0, representativeTimestamp - hoverStart);

        var tempDirectory = _paths.ResolveContainedPath(
            VaultPathArea.TempMediaAssets, $"{source.MediaId:N}/{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var thumbnailTemp = Path.Combine(tempDirectory, "thumbnail.webp");
        var frameTemp = Path.Combine(tempDirectory, "thumbnail.jpg");
        var hoverTemp = Path.Combine(tempDirectory, "hover.mp4");
        var bannerTemp = Path.Combine(tempDirectory, "banner.mp4");

        try
        {
            var ffmpeg = _tools.TryResolveExecutablePath(ExternalToolResolver.FfmpegToolId);
            if (ffmpeg is null)
                return JobExecutionResult.Failed(
                    JobFailureClassification.MissingRequiredToolOrModel,
                    "MEDIA_ASSET_TOOL_UNAVAILABLE",
                    "The approved video encoder is unavailable.");

            var outputs = new List<(string Label, string Filter)>();
            if (needThumbnail) outputs.Add(("thumb", $"select='gte(t,{TimestampSeconds(thumbnailOffset)})',scale='min(1024,iw)':'min(1024,ih)':force_original_aspect_ratio=decrease"));
            if (needHover) outputs.Add(("hover", HoverScale + ",fps=24"));
            if (needBanner) outputs.Add(("banner", BannerScale + ",fps=24"));
            var filter = "[0:v]setpts=PTS-STARTPTS";
            if (outputs.Count > 1) filter += ",split=" + outputs.Count + string.Concat(outputs.Select(output => "[" + output.Label + "src]"));
            else filter += "[" + outputs[0].Label + "src]";
            filter += ";" + string.Join(";", outputs.Select(output => "[" + output.Label + "src]" + output.Filter + "[" + output.Label + "]"));
            var args = new List<string> { "-y", "-ss", TimestampSeconds(hoverStart), "-i", path,
                "-filter_complex", filter, "-progress", "pipe:1", "-nostats" };
            foreach (var output in outputs)
            {
                args.AddRange(["-map", "[" + output.Label + "]", "-an"]);
                if (output.Label == "thumb") args.AddRange(["-frames:v", "1", "-c:v", "mjpeg", "-threads", "2", frameTemp]);
                else args.AddRange(["-t", "5.8", "-r", "24", "-c:v", "libopenh264", "-threads", "2",
                    "-b:v", output.Label == "banner" ? "6000k" : "1000k", "-rc_mode", "bitrate",
                    "-movflags", "+faststart", output.Label == "banner" ? bannerTemp : hoverTemp]);
            }
            var duration = Math.Max(1, Math.Min(5800, (source.DurationMilliseconds ?? 5800) - hoverStart));
            await context.Progress.ReportProgressAsync(0, duration + 1, "Preparing", ct).ConfigureAwait(false);
            async Task OnProgress(string line)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(12), out var microseconds))
                    await context.Progress.ReportProgressAsync(Math.Clamp(microseconds / 1000, 0, duration), duration + 1, "Encoding", ct).ConfigureAwait(false);
            }

            var result = await _launcher.RunAsync(
                new ProcessRunRequest(ffmpeg, args, Timeout: TimeSpan.FromMinutes(3), OutputLineReceived: OnProgress), ct)
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
                    2,
                    hoverTemp,
                    hoverStart,
                    ct: ct).ConfigureAwait(false);
            }

            if (needBanner)
                await _writes.PublishMediaAssetAsync(source.MediaId, MediaAssetRole.Banner, 2,
                    bannerTemp, hoverStart, ct: ct).ConfigureAwait(false);
            if (bannerRequested)
            {
                await context.Progress.ReportProgressAsync(duration, duration + 1, "Finalizing", ct).ConfigureAwait(false);
                if (!await banners.CompleteRequestsAsync(source.MediaId, ct).ConfigureAwait(false))
                    return Invalid("BANNER_MEMBERSHIP_NOT_COMMITTED", "The Banner request no longer has an eligible Profile relation.");
                await context.Progress.ReportProgressAsync(duration + 1, duration + 1, "Ready", ct).ConfigureAwait(false);
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
                ct, sourceHash, existing.ContractVersion).ConfigureAwait(false);
            if (current.ByteLength == existing.ByteLength
                && current.Sha256 == existing.Sha256
                && MediaAssetContracts.IsSupported(role, existing.ContractVersion))
                return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
        }

        await _writes.MarkMediaNeedsRepairAsync(mediaId, role, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<JobExecutionResult> ExecuteRoleAsync(JobExecutionContext context,
        MediaAssetRole role, CancellationToken ct, string? preparedModelPath = null)
    {
        var source = await _media.GetMediaSourceAsync(context.OwnerId, ct).ConfigureAwait(false);
        if (source is null || !source.IsActive)
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
                path = preparedModelPath ?? path;
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

    private void EncodeWebp(string input, string output)
    {
        using var bitmap = TargetSizeDecoder.Decode(input, 1024, 1024, _tools)
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
