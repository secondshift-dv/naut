using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace Neuterradise.Release.Contracts;

/// <summary>Cross-process update authority. The lock and its physical directory ancestry
/// remain pinned for the entire operation, without following a substituted reparse entry.</summary>
public sealed class UpdateOperationLease : IDisposable, IAsyncDisposable
{
    private readonly IDisposable _pins;
    private readonly SafeFileHandle _handle;

    private UpdateOperationLease(IDisposable pins, SafeFileHandle handle)
    {
        _pins = pins;
        _handle = handle;
    }

    public static async Task<UpdateOperationLease> AcquireAsync(string appStateRoot, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(appStateRoot, "updates");
        var pins = UpdatePathGuard.PinDirectory(directory, create: true);
        try
        {
            var path = Path.Combine(directory, "operation.lock");
            var start = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return new UpdateOperationLease(pins, UpdatePathGuard.OpenRegularFile(path, 0xC0000000, 0, 4));
                }
                catch (IOException exception) when (exception.InnerException is Win32Exception { NativeErrorCode: 32 or 33 }
                    && start.Elapsed < TimeSpan.FromSeconds(30))
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch { pins.Dispose(); throw; }
    }

    public void Dispose()
    {
        _handle.Dispose();
        _pins.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
