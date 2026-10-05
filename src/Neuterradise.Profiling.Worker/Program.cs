namespace Neuterradise.Profiling.Worker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? pipeName = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--pipe", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                pipeName = args[++i];
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            Console.Error.WriteLine("Usage: Neuterradise.Profiling.Worker --pipe <pipeName>");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        return await ProfilingWorkerHost.RunAsync(pipeName, cts.Token).ConfigureAwait(false);
    }
}
