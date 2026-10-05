using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Image;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Media.Video;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.MediaTools;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Jobs.Handlers;

public interface IExtractMetadataJobOperation : IAuthorizedJobOperation;

public sealed class ExtractMetadataJobHandler : AuthorizedJobHandler
{
    public ExtractMetadataJobHandler(IExtractMetadataJobOperation operation)
        : base(
            "ExtractMetadata",
            [JobLane.Cpu, JobLane.Media],
            "Media",
            operation,
            runOffCallingThread: true)
    {
    }
}

public sealed record AuthorizedMediaToolPlan(
    bool AlreadyCompleted,
    ProcessRunRequest? Request,
    JobFailureClassification NonZeroExitClassification =
        JobFailureClassification.DeterministicInvalidInput);

public interface IAuthorizedMediaToolPlanSource
{
    Task<AuthorizedMediaToolPlan> ResolveAsync(
        JobExecutionContext context,
        CancellationToken cancellationToken);

    Task<JobExecutionResult> PublishAsync(
        JobExecutionContext context,
        AuthorizedMediaToolPlan plan,
        ProcessRunResult processResult,
        CancellationToken cancellationToken);
}

public sealed class ExternalMediaToolJobOperation : IExtractMetadataJobOperation
{
    private readonly IProcessLauncher _launcher;
    private readonly IAuthorizedMediaToolPlanSource _plans;

    public ExternalMediaToolJobOperation(
        IProcessLauncher launcher,
        IAuthorizedMediaToolPlanSource plans)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
    }

    public async Task<JobExecutionResult> ExecuteAsync(
        JobExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var plan = await _plans.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
            if (plan.AlreadyCompleted)
            {
                return JobExecutionResult.Succeeded;
            }

            if (plan.Request is null)
            {
                return JobExecutionResult.Failed(
                    JobFailureClassification.DeterministicInvalidInput,
                    "MEDIA_TOOL_PLAN_INVALID",
                    "Persisted authority did not resolve an external-tool request.");
            }

            if (!Enum.IsDefined(plan.NonZeroExitClassification)
                || plan.NonZeroExitClassification == JobFailureClassification.Cancelled)
            {
                return JobExecutionResult.Failed(
                    JobFailureClassification.DeterministicInvalidInput,
                    "MEDIA_TOOL_PLAN_INVALID",
                    "The external-tool plan supplied an invalid exit classification.");
            }

            await context.Progress.ReportProgressAsync(0, 1, "External media tool", cancellationToken)
                .ConfigureAwait(false);
            var processResult = await _launcher.RunAsync(plan.Request, cancellationToken)
                .ConfigureAwait(false);
            if (processResult.TimedOut)
            {
                return JobExecutionResult.Failed(
                    JobFailureClassification.ToolLaunchTransient,
                    "MEDIA_TOOL_TIMEOUT",
                    "The external media tool exceeded its bounded timeout.");
            }

            if (processResult.ExitCode != 0)
            {
                return JobExecutionResult.Failed(
                    plan.NonZeroExitClassification,
                    "MEDIA_TOOL_EXIT_FAILED",
                    DescribeFailedProcess(plan, processResult));
            }

            var publishResult = await _plans.PublishAsync(context, plan, processResult, cancellationToken)
                .ConfigureAwait(false);
            if (publishResult.IsSucceeded)
            {
                await context.Progress.ReportProgressAsync(1, 1, "Published", cancellationToken)
                    .ConfigureAwait(false);
            }

            return publishResult;
        }
        catch (MediaToolPlanRefusedException refusal)
        {
            return JobExecutionResult.Failed(
                refusal.Classification,
                refusal.ErrorCode,
                refusal.SafeDetail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return JobExecutionResult.Cancelled("The external media tool was stopped at a safe boundary.");
        }
        catch (FileNotFoundException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.MissingRequiredToolOrModel,
                "MEDIA_TOOL_MISSING",
                "The approved external media tool is not present.");
        }
        catch (UnauthorizedAccessException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.AccessTemporarilyDenied,
                "MEDIA_TOOL_ACCESS_DENIED",
                "The media source cannot currently be read.");
        }
        catch (System.Security.SecurityException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.AccessTemporarilyDenied,
                "MEDIA_TOOL_SECURITY_DENIED",
                "The media source cannot be read due to security permissions.");
        }
        catch (IOException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.TransientIo,
                "MEDIA_TOOL_IO_FAILED",
                "An I/O error occurred while reading the media source.");
        }
        catch (JsonException exception)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "MEDIA_TOOL_OUTPUT_CORRUPT",
                $"The media tool output was not valid JSON: {exception.Message}");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.MissingRequiredToolOrModel,
                "MEDIA_TOOL_MISSING",
                "The approved external media tool is not present.");
        }
        catch (Win32Exception)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.ToolLaunchTransient,
                "MEDIA_TOOL_LAUNCH_FAILED",
                "The approved external media tool could not be launched.");
        }
    }

    private static string DescribeFailedProcess(
        AuthorizedMediaToolPlan plan,
        ProcessRunResult result)
    {
        const int maximumDiagnosticLength = 320;
        var fallback = $"The external media tool exited with code {result.ExitCode}.";
        if (string.IsNullOrWhiteSpace(result.StandardError))
        {
            return fallback;
        }

        var lines = result.StandardError
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return fallback;
        }

        var diagnostic = string.Join(" | ", lines.TakeLast(Math.Min(2, lines.Length)));
        if (plan.Request is { } request)
        {
            diagnostic = diagnostic.Replace(
                request.ExecutablePath,
                "<tool>",
                StringComparison.OrdinalIgnoreCase);
            foreach (var argument in request.Arguments)
            {
                if (!string.IsNullOrWhiteSpace(argument) && Path.IsPathFullyQualified(argument))
                {
                    diagnostic = diagnostic.Replace(
                        argument,
                        "<path>",
                        StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        if (diagnostic.Length > maximumDiagnosticLength)
        {
            diagnostic = diagnostic[..(maximumDiagnosticLength - 1)] + "…";
        }

        return $"{fallback} {diagnostic}";
    }
}

public sealed class ProductionMediaToolPlanSource : IAuthorizedMediaToolPlanSource
{
    private const int _checkpointSchemaVersion = 1;

    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly MediaReads _reads;
    private readonly MediaWrites _writes;
    private readonly VaultPaths _paths;
    private readonly ExternalToolResolver _tools;
    private readonly ModelPreviewAdapterRegistry _modelAdapters;
    private readonly ImageMetadataAdapter _imageMetadata = new();

    public ProductionMediaToolPlanSource(
        MediaReads reads,
        MediaWrites writes,
        VaultPaths paths,
        ExternalToolResolver tools,
        ModelPreviewAdapterRegistry modelAdapters)
    {
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _modelAdapters = modelAdapters ?? throw new ArgumentNullException(nameof(modelAdapters));
    }

    public async Task<AuthorizedMediaToolPlan> ResolveAsync(
        JobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var source = await _reads.GetMediaSourceAsync(context.OwnerId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null || source.IsRetired)
        {
            throw new MediaToolPlanRefusedException(
                JobFailureClassification.DeterministicInvalidInput,
                "MEDIA_TOOL_OWNER_UNUSABLE",
                "The persisted job owner is not an Media a derivation may be published for.");
        }

        var readablePath = source.ResolveManagedPath(_paths);
        if (readablePath is null)
        {
            throw new MediaToolPlanRefusedException(
                JobFailureClassification.ContentMismatch,
                "MEDIA_TOOL_SOURCE_MISSING",
                "The canonical managed media file is not available.");
        }

        return context.Kind switch
        {
            "ExtractMetadata" => await ResolveExtractMetadataAsync(
                context,
                source,
                readablePath,
                cancellationToken).ConfigureAwait(false),
            _ => throw new MediaToolPlanRefusedException(
                JobFailureClassification.DeterministicInvalidInput,
                "MEDIA_TOOL_KIND_UNSUPPORTED",
                $"'{context.Kind}' is not a kind this plan source serves."),
        };
    }

    public async Task<JobExecutionResult> PublishAsync(
        JobExecutionContext context,
        AuthorizedMediaToolPlan plan,
        ProcessRunResult processResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(processResult);

        if (!string.Equals(context.Kind, "ExtractMetadata", StringComparison.Ordinal))
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "MEDIA_TOOL_PUBLISH_UNSUPPORTED",
                $"'{context.Kind}' produced external-tool output that no publisher owns.");
        }

        VideoMetadata metadata;
        try
        {
            metadata = VideoMetadataAdapter.ParseJson(processResult.StandardOutput);
        }
        catch (JsonException exception)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "MEDIA_TOOL_OUTPUT_CORRUPT",
                $"The video probe output was not valid JSON: {exception.Message}");
        }

        if (metadata.Width is null && metadata.Duration is null && metadata.Container is null)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "MEDIA_TOOL_OUTPUT_UNUSABLE",
                "The video probe produced no usable typed metadata.");
        }

        await _writes.SaveMetadataAsync(
                context.OwnerId,
                metadata.Width,
                metadata.Height,
                metadata.Duration is { } duration
                    ? (int)Math.Min(int.MaxValue, duration.TotalMilliseconds)
                    : null,
                metadata.CapturedAt,
                VideoMetadataAdapter.Serialize(metadata),
                cancellationToken: cancellationToken, requirePublicActive: IsExplicitRefresh(context))
            .ConfigureAwait(false);

        await WriteCheckpointAsync(context, "video-ffprobe", cancellationToken).ConfigureAwait(false);
        return JobExecutionResult.Succeeded;
    }

    private static bool IsExplicitRefresh(JobExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.CheckpointJson)) return false;
        using var checkpoint = JsonDocument.Parse(context.CheckpointJson);
        return checkpoint.RootElement.TryGetProperty("refreshMetadata", out var value)
            && value.ValueKind == JsonValueKind.True;
    }
    private async Task<AuthorizedMediaToolPlan> ResolveExtractMetadataAsync(
        JobExecutionContext context,
        MediaSource source,
        string readablePath,
        CancellationToken cancellationToken)
    {
        switch (source.MediaType)
        {
            case MediaType.Image:
            {
                ImageMetadata metadata;
                try
                {
                    metadata = _imageMetadata.ExtractFromFile(readablePath,
                        Path.GetExtension(source.SourcePath ?? readablePath));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new MediaToolPlanRefusedException(
                        JobFailureClassification.DeterministicInvalidInput,
                        "IMAGE_METADATA_UNUSABLE",
                        $"Image metadata extraction failed: {exception.Message}");
                }

                await _writes.SaveMetadataAsync(
                        context.OwnerId,
                        metadata.Width,
                        metadata.Height,
                        durationMs: null,
                        metadata.CapturedAt,
                        ImageMetadataAdapter.Serialize(metadata),
                        cancellationToken: cancellationToken, requirePublicActive: IsExplicitRefresh(context))
                    .ConfigureAwait(false);
                await WriteCheckpointAsync(
                        context,
                        "image-inprocess",
                        cancellationToken)
                    .ConfigureAwait(false);
                return new AuthorizedMediaToolPlan(AlreadyCompleted: true, Request: null);
            }

            case MediaType.Model:
            {
                ModelMetadata probe;
                try
                {
                    probe = await _modelAdapters
                        .ProbeAsync(
                             new ModelProbeInput(readablePath,
                                 Extension: Path.GetExtension(source.SourcePath ?? readablePath),
                                 FileSizeBytes: source.ByteLength ?? 0),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new MediaToolPlanRefusedException(
                        JobFailureClassification.DeterministicInvalidInput,
                        "MODEL_PROBE_FAILED",
                        $"Model metadata probe failed: {exception.Message}");
                }

                await _writes.SaveMetadataAsync(context.OwnerId, probe, cancellationToken)
                    .ConfigureAwait(false);
                await WriteCheckpointAsync(
                        context,
                        $"model-{probe.AdapterId}",
                        cancellationToken)
                    .ConfigureAwait(false);
                return new AuthorizedMediaToolPlan(AlreadyCompleted: true, Request: null);
            }

            case MediaType.Video:
            {
                var resolution = _tools.Resolve(ExternalToolResolver.FfprobeToolId);
                if (!resolution.IsUsable)
                {
                    throw new MediaToolPlanRefusedException(
                        JobFailureClassification.MissingRequiredToolOrModel,
                        "MEDIA_TOOL_UNAVAILABLE",
                        resolution.Reason);
                }

                return new AuthorizedMediaToolPlan(
                    AlreadyCompleted: false,
                    Request: new ProcessRunRequest(
                        ExecutablePath: resolution.ResolvedPath!,
                        Arguments:
                        [
                            "-v", "quiet",
                            "-print_format", "json",
                            "-show_format",
                            "-show_streams",
                            readablePath,
                        ],
                        Timeout: ProbeTimeout),
                    NonZeroExitClassification: JobFailureClassification.DeterministicInvalidInput);
            }

            default:
                throw new MediaToolPlanRefusedException(
                    JobFailureClassification.DeterministicInvalidInput,
                    "MEDIA_TOOL_MEDIA_TYPE_UNSUPPORTED",
                    $"'{source.MediaType}' has no metadata derivation.");
        }
    }

    private static Task WriteCheckpointAsync(
        JobExecutionContext context,
        string derivation,
        CancellationToken cancellationToken)
    {
        var checkpoint = JsonSerializer.Serialize(new
        {
            schemaVersion = _checkpointSchemaVersion,
            assetId = context.OwnerId,
            kind = context.Kind,
            derivation,
            published = true,
        });

        return context.Checkpoints.UpdateCheckpointAsync(checkpoint, cancellationToken);
    }
}

public sealed class MediaToolPlanRefusedException : InvalidOperationException
{
    public MediaToolPlanRefusedException(
        JobFailureClassification classification,
        string errorCode,
        string safeDetail)
        : base(safeDetail)
    {
        Classification = classification;
        ErrorCode = errorCode;
        SafeDetail = safeDetail;
    }

    public JobFailureClassification Classification { get; }
    public string ErrorCode { get; }
    public string SafeDetail { get; }
}
