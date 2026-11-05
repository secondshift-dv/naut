using System.Diagnostics;
using System.Globalization;

namespace Neuterradise.App.SystemServices.MediaTools;

/// <summary>One bounded pipe decoder for runtime previews and isolated profiling frame inputs.</summary>
public static class BoundedMediaFrameDecoder
{
    public static async Task<byte[]?> DecodeAsync(string verifiedExecutable, string path,
        int maximumWidth, int maximumHeight, long? timestampMilliseconds = null, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(verifiedExecutable) || !File.Exists(verifiedExecutable))
            throw new ArgumentException("A verified absolute executable is required.", nameof(verifiedExecutable));
        maximumWidth = Math.Clamp(maximumWidth, 1, 4096);
        maximumHeight = Math.Clamp(maximumHeight, 1, 4096);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(verifiedExecutable)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true },
        };
        var arguments = new List<string> { "-nostdin", "-v", "error", "-max_alloc", "67108864", "-threads", "1",
            "-filter_threads", "1", "-protocol_whitelist", "file,pipe" };
        if (timestampMilliseconds is { } timestamp)
            arguments.AddRange(["-ss", (Math.Max(0, timestamp) / 1000d).ToString("0.###", CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-i", path, "-frames:v", "1", "-an", "-vf",
            $"scale=w='min({maximumWidth},iw)':h='min({maximumHeight},ih)':force_original_aspect_ratio=decrease",
            "-c:v", "png", "-f", "image2pipe", "pipe:1"]);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var output = new MemoryStream();
        var errorDrain = DrainAsync(process.StandardError, deadline.Token);
        try
        {
            var buffer = new byte[65536];
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > 64L * 1024 * 1024) throw new InvalidDataException("Frame exceeds its memory budget.");
                output.Write(buffer, 0, read);
            }
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await errorDrain.ConfigureAwait(false);
            return process.ExitCode == 0 && output.Length > 0 ? output.ToArray() : null;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await errorDrain.ConfigureAwait(false); } catch (OperationCanceledException) { }
            throw;
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer, ct).ConfigureAwait(false) != 0) { }
    }
}
