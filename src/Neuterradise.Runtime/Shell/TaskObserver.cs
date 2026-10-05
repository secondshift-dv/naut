using System.Diagnostics;

namespace Neuterradise.App.Shell;

/// <summary>
/// Observes a task started from a user action without awaiting it at the call site. A fault is
/// logged and optionally reported to the surface, so a recoverable feature failure can never reach
/// <see cref="TaskScheduler.UnobservedTaskException"/>, which the crash handler treats as fatal.
/// </summary>
public static class TaskObserver
{
    public static void Observe(Task task, string context, Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        ObserveCore(task, context, onFailure);
    }

    private static async void ObserveCore(Task task, string context, Action<Exception>? onFailure)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("{0} failed: {1}", context, exception);

            if (onFailure is not null)
            {
                try
                {
                    onFailure(exception);
                }
                catch (Exception reportFailure)
                {
                    Trace.TraceError("{0} failure report failed: {1}", context, reportFailure);
                }
            }
        }
    }
}
