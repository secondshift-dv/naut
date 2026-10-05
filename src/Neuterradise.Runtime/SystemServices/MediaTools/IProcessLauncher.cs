using System.Diagnostics;
using System.IO;
using System.Text;

namespace Neuterradise.App.SystemServices.MediaTools;

public interface IProcessLauncher
{

    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default);
}

public sealed record ProcessRunRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null,
    int MaxCapturedCharacters = BoundedProcessLauncher.DefaultMaxCapturedCharacters);

public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

public sealed class BoundedProcessLauncher : IProcessLauncher
{
    public const int DefaultMaxCapturedCharacters = 1024 * 1024;
    public const int MaximumMaxCapturedCharacters = 8 * 1024 * 1024;
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _defaultTimeout;

    public BoundedProcessLauncher(TimeSpan? defaultTimeout = null)
    {
        _defaultTimeout = defaultTimeout ?? DefaultTimeout;
        if (_defaultTimeout <= TimeSpan.Zero || _defaultTimeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultTimeout),
                "The default process timeout must be positive and no longer than one day.");
        }
    }

    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(request.Timeout ?? _defaultTimeout);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (request.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The approved external media tool did not start.");
        TrySetBelowNormalPriority(process);

        var outputTask = ReadBoundedAsync(
            process.StandardOutput,
            request.MaxCapturedCharacters);
        var errorTask = ReadBoundedAsync(
            process.StandardError,
            request.MaxCapturedCharacters);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested)
        {
            timedOut = timeout.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested;
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            if (!timedOut)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        var standardOutput = await outputTask.ConfigureAwait(false);
        var standardError = await errorTask.ConfigureAwait(false);
        return new ProcessRunResult(
            process.ExitCode,
            standardOutput,
            standardError,
            timedOut);
    }

    private static void Validate(ProcessRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutablePath);
        ArgumentNullException.ThrowIfNull(request.Arguments);

        if (!Path.IsPathFullyQualified(request.ExecutablePath))
        {
            throw new ArgumentException(
                "An approved external tool path must be fully qualified.",
                nameof(request));
        }

        if (request.WorkingDirectory is not null
            && !Path.IsPathFullyQualified(request.WorkingDirectory))
        {
            throw new ArgumentException("The working directory must be fully qualified.", nameof(request));
        }

        if (request.Timeout is { } timeout
            && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The process timeout must be positive and no longer than one day.");
        }

        if (request.MaxCapturedCharacters < 1
            || request.MaxCapturedCharacters > MaximumMaxCapturedCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Captured output must be between 1 and {MaximumMaxCapturedCharacters} characters per stream.");
        }

        if (request.Arguments.Any(static argument => argument is null))
        {
            throw new ArgumentException("Process arguments cannot contain null values.", nameof(request));
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maxCapturedCharacters)
    {
        var buffer = new char[4096];
        var captured = new StringBuilder(Math.Min(maxCapturedCharacters, buffer.Length));
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            var remaining = maxCapturedCharacters - captured.Length;
            if (remaining > 0)
            {
                captured.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        return captured.ToString();
    }

    private static void TrySetBelowNormalPriority(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (InvalidOperationException) { }
        catch (NotSupportedException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }
}
