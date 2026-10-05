using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Lifecycle;

public sealed class CrashDiagnostics : IDisposable
{
    private readonly Func<CrashDiagnosticContext> _context;
    private readonly Action? _requestControlledShutdown;
    private readonly object _writeGate = new();
    private bool _installed;
    private bool _disposed;

    public CrashDiagnostics(string logPath, Func<CrashDiagnosticContext> context, Action? requestControlledShutdown = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        LogPath = Path.GetFullPath(logPath);
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _requestControlledShutdown = requestControlledShutdown;
    }

    public string LogPath { get; }

    public static CrashDiagnostics CreateProduction(AppStatePaths appState, Func<CrashDiagnosticContext> context, Action requestControlledShutdown)
    {
        ArgumentNullException.ThrowIfNull(appState);
        appState.EnsureStructuralDirectories();
        return new CrashDiagnostics(Path.Combine(appState.DiagnosticsPath, "crash-diagnostics.jsonl"), context, requestControlledShutdown);
    }

    public void OpenLocation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var target = File.Exists(LogPath)
                ? $"/select,\"{LogPath}\""
                : Path.GetDirectoryName(LogPath);
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = target,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            Trace.TraceError("Could not open crash diagnostics location: {0}", exception.GetType().Name);
        }
    }

    /// <summary>
    /// Installs process-wide handlers. The UI host forwards its own unhandled UI-thread exceptions to
    /// <see cref="HandleUiThreadException"/>, so this stays independent of any UI framework.
    /// </summary>
    public void Install()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_installed) return;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _installed = true;
    }

    /// <summary>Records an unhandled UI-thread exception; returns true when a controlled shutdown took over.</summary>
    public bool HandleUiThreadException(Exception exception) =>
        CaptureAndRequestShutdown(exception, CrashOrigin.DispatcherUnhandledException) == CrashDisposition.ControlledShutdown;

    public CrashDisposition CaptureAndRequestShutdown(Exception exception, CrashOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write(exception, origin);
        if (_requestControlledShutdown is null) return CrashDisposition.AbruptExit;
        try { _requestControlledShutdown(); return CrashDisposition.ControlledShutdown; }
        catch (Exception shutdownException) { Write(shutdownException, CrashOrigin.ControlledShutdownFailure); return CrashDisposition.AbruptExit; }
    }

    public void Capture(Exception exception, CrashOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write(exception, origin);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_installed)
        {
            AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        }
        _installed = false;
    }

    private void Write(Exception exception, CrashOrigin origin)
    {
        CrashDiagnosticContext context;
        try { context = _context(); }
        catch (Exception contextException)
        {
            context = new CrashDiagnosticContext(null, null, null, StartupState.ProcessStart, SafeContextFailure: contextException.GetType().Name);
        }
        var record = new CrashDiagnosticRecord(DateTimeOffset.UtcNow, origin, context,
            exception.GetType().FullName ?? exception.GetType().Name,
            StructuredDiagnostics.SanitizeDetail(exception.Message) ?? exception.GetType().Name,
            StructuredDiagnostics.SanitizeDetail(exception.StackTrace));
        try
        {
            lock (_writeGate)
            {
                var directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(LogPath, JsonSerializer.Serialize(record) + Environment.NewLine);
            }
        }
        catch (Exception writeException)
        {
            Trace.TraceError("{0} crash diagnostic write failed ({1}); original {2}", ProductIdentity.DisplayName, writeException.GetType().Name, exception.GetType().Name);
        }
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => Write(e.ExceptionObject as Exception ?? new InvalidOperationException("Unhandled non-Exception object."), CrashOrigin.AppDomainUnhandledException);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var disposition = CaptureAndRequestShutdown(e.Exception, CrashOrigin.UnobservedTaskException);
        if (disposition == CrashDisposition.ControlledShutdown) e.SetObserved();
    }

}

public enum CrashOrigin { AppDomainUnhandledException, UnobservedTaskException, DispatcherUnhandledException, StartupFailure, ControlledShutdownFailure }
public enum CrashDisposition { ControlledShutdown, AbruptExit }
public sealed record CrashDiagnosticContext(string? AppVersion, string? SchemaVersion, string? VaultRoot, StartupState StartupState, Guid? JobId = null, Guid? OperationId = null, string? WorkerVersion = null, string? ProtocolVersion = null, string? ModelVersion = null, string? SafeContextFailure = null);
public sealed record CrashDiagnosticRecord(DateTimeOffset TimestampUtc, CrashOrigin Origin, CrashDiagnosticContext Context, string ExceptionType, string ExceptionMessage, string? ExceptionStack);
