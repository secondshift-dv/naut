namespace Neuterradise.Profiling.Worker;

public static class ProfilingWorkerHost
{
    public static async Task<int> RunAsync(string pipeName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        await using var client = new Transport.ProfilingClientTransport(pipeName);
        try
        {
            await client.ConnectAsync(15000, cancellationToken).ConfigureAwait(false);
            await Transport.ProfilingHandshake.PerformClientHandshakeAsync(client, cancellationToken).ConfigureAwait(false);

            using var router = new Dispatching.ProfilingRequestDispatcher();
            return await router.RunAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Profiling Worker execution failed: {ex.Message}");
            return 1;
        }
    }
}
