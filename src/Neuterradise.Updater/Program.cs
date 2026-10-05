using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Neuterradise.Updater;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? handoffPath = null;
        int? parentPid = null;
        string? handoffSha256 = null;
        string? manifestSha256 = null;
        bool launchApp = true;

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--handoff", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                handoffPath = args[++i];
            }
            else if (string.Equals(args[i], "--handoff-sha256", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                handoffSha256 = args[++i];
            }
            else if (string.Equals(args[i], "--manifest-sha256", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                manifestSha256 = args[++i];
            }
            else if (string.Equals(args[i], "--parent-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var pid))
                {
                    parentPid = pid;
                }
            }
            else if (string.Equals(args[i], "--no-launch", StringComparison.OrdinalIgnoreCase))
            {
                launchApp = false;
            }
        }

        if (string.IsNullOrWhiteSpace(handoffPath)
            || !File.Exists(handoffPath)
            || string.IsNullOrWhiteSpace(handoffSha256)
            || string.IsNullOrWhiteSpace(manifestSha256))
        {
            Console.Error.WriteLine("Error: Valid --handoff, --handoff-sha256, and --manifest-sha256 authorities are required.");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            var result = await ReplacementEngine.ExecuteAsync(handoffPath, handoffSha256, manifestSha256, parentPid, launchApp, cts.Token);
            if (!result.Success)
            {
                Console.Error.WriteLine($"Updater failed: {result.Error}");
                return 2;
            }

            Console.WriteLine("Update replacement completed successfully.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Update replacement canceled.");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unhandled updater failure: {ex.Message}");
            return 4;
        }
    }
}
