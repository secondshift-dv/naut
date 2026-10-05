using System.Windows.Input;
using Neuterradise.App.Localization;
using Neuterradise.App.SystemServices;

namespace Neuterradise.App.Shell;

/// <summary>
/// What the user may do from the nonwritable startup surface. Only actions that are genuinely
/// available for the reported failure are offered; nothing is shown as a dead command.
/// </summary>
public enum StartupRecoveryDecision
{
    /// <summary>Try the whole startup sequence again with the same configuration.</summary>
    Retry,

    /// <summary>Leave the application without opening a writable catalog.</summary>
    Exit,
}

/// <summary>
/// Single owner of the presentation for every startup failure that keeps the application nonwritable:
/// unresolved roots, unreadable configuration, an unopenable Vault, failed schema initialization/recovery and the
/// critical integrity gate. The normal shell is never composed while this surface is showing.
/// </summary>
public sealed class StartupRecoveryViewModel
{
    /// <summary>Used when a caller has no more specific classification.</summary>
    public const string GenericErrorCode = "STARTUP_FAILURE";

    private StartupRecoveryViewModel(
        string errorCode,
        string title,
        string message,
        string nextStep,
        string diagnosticsPath,
        bool canRetry,
        ICommand? retryCommand,
        ICommand? openDiagnosticsCommand,
        ICommand? exitCommand)
    {
        ErrorCode = errorCode;
        Title = title;
        Message = message;
        NextStep = nextStep;
        DiagnosticsPath = diagnosticsPath;
        CanRetry = canRetry;
        RetryCommand = retryCommand;
        OpenDiagnosticsCommand = openDiagnosticsCommand;
        ExitCommand = exitCommand;
    }

    public string ErrorCode { get; }

    public string Title { get; }

    /// <summary>The safe explanation of what stopped startup. Never a stack trace or raw SQLite text.</summary>
    public string Message { get; }

    /// <summary>The concrete action the user can take to make progress.</summary>
    public string NextStep { get; }

    public string DiagnosticsPath { get; }

    public bool CanRetry { get; }

    public ICommand? RetryCommand { get; }

    public ICommand? OpenDiagnosticsCommand { get; }

    public ICommand? ExitCommand { get; }

    /// <summary>
    /// Builds the surface state for one failure. Retry is offered only when retrying the same
    /// configuration could plausibly succeed, so the user is never given a button that cannot help.
    /// </summary>
    public static StartupRecoveryViewModel Create(
        string? errorCode,
        string diagnosticsPath,
        Action retry,
        Action openDiagnostics,
        Action exit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticsPath);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(openDiagnostics);
        ArgumentNullException.ThrowIfNull(exit);

        var code = string.IsNullOrWhiteSpace(errorCode) ? GenericErrorCode : errorCode;
        var (title, message, nextStep, canRetry) = Describe(code);

        return new StartupRecoveryViewModel(
            code,
            title,
            message,
            nextStep,
            diagnosticsPath,
            canRetry,
            canRetry ? new RelayCommand(_ => retry()) : null,
            new RelayCommand(_ => openDiagnostics()),
            new RelayCommand(_ => exit()));
    }

    private static (string Title, string Message, string NextStep, bool CanRetry) Describe(string errorCode)
    {
        var keySuffix = errorCode switch
        {
            "VAULT_NOT_CONFIGURED" => "VAULT_NOT_CONFIGURED",
            "VAULT_ROOT_INVALID" => "VAULT_ROOT_INVALID",
            "VAULT_LOCK_UNAVAILABLE" => "VAULT_LOCK_UNAVAILABLE",
            "VAULT_ROOT_ACCESS_DENIED" => "VAULT_ROOT_ACCESS_DENIED",
            "VAULT_ROOT_IO_FAILURE" => "VAULT_ROOT_IO_FAILURE",
            "CONFIGURATION_UNREADABLE" => "CONFIGURATION_UNREADABLE",
            "CONFIGURATION_ACCESS_DENIED" => "CONFIGURATION_ACCESS_DENIED",
            "DATABASE_INITIALIZATION_FAILED" => "DATABASE_INITIALIZATION_FAILED",
            "RECOVERY_FAILED" => "RECOVERY_FAILED",
            "CRITICAL_INTEGRITY_VIOLATION" => "CRITICAL_INTEGRITY_VIOLATION",
            "STARTUP_CANCELLED" => "STARTUP_CANCELLED",
            _ => "Generic",
        };

        var canRetry = errorCode switch
        {
            "CRITICAL_INTEGRITY_VIOLATION" => false,
            _ => true,
        };

        var titleFallback = errorCode switch
        {
            "VAULT_NOT_CONFIGURED" => $"Choose where {ProductIdentity.DisplayName} should keep your Vault",
            "VAULT_ROOT_INVALID" => "That vault folder cannot be used",
            "VAULT_LOCK_UNAVAILABLE" => "This vault is already open",
            "VAULT_ROOT_ACCESS_DENIED" => "Windows denied access to the vault",
            "VAULT_ROOT_IO_FAILURE" => "The vault folder could not be read",
            "CONFIGURATION_UNREADABLE" => "The application configuration could not be read",
            "CONFIGURATION_ACCESS_DENIED" => "Windows denied access to the application data folder",
            "DATABASE_INITIALIZATION_FAILED" => "The Vault database could not be initialized",
            "RECOVERY_FAILED" => "Interrupted work could not be reconciled",
            "CRITICAL_INTEGRITY_VIOLATION" => "This vault needs attention before it can be opened",
            "STARTUP_CANCELLED" => "Startup was cancelled",
            _ => $"{ProductIdentity.DisplayName} could not open this vault safely",
        };

        var messageFallback = errorCode switch
        {
            "VAULT_NOT_CONFIGURED" => "No vault folder has been selected yet. Nothing is created automatically, so no folder has been written to.",
            "VAULT_ROOT_INVALID" => "The configured vault folder is not a usable location. It must be a fully qualified local folder that is neither the application folder nor the application data folder, and neither may contain the other.",
            "VAULT_LOCK_UNAVAILABLE" => $"Another {ProductIdentity.DisplayName} process currently owns this vault. Two writers may never share one vault.",
            "VAULT_ROOT_ACCESS_DENIED" => "The vault folder exists but this account may not read or write it. No data was changed.",
            "VAULT_ROOT_IO_FAILURE" => "Windows reported an I/O failure while opening the vault folder. No data was changed.",
            "CONFIGURATION_UNREADABLE" => "The configuration file exists but is not valid. The original file was preserved next to it for diagnosis and no settings were overwritten.",
            "CONFIGURATION_ACCESS_DENIED" => "Application configuration and diagnostics live in the application data folder, which this account may not read or write.",
            "DATABASE_INITIALIZATION_FAILED" => "The catalog database could not be created or did not match Catalog Schema v1. No database upgrade was attempted.",
            "RECOVERY_FAILED" => "Persisted recovery work could not reach a safe checkpoint. No repair was guessed and nothing was deleted.",
            "CRITICAL_INTEGRITY_VIOLATION" => "The Vault contains an authority conflict that must be resolved before writable access is safe. Opening it anyway could corrupt the Vault.",
            "STARTUP_CANCELLED" => "Startup stopped before writable access was established. Nothing was changed.",
            _ => "Startup stopped before writable access because the vault could not be opened safely. No partial state was accepted.",
        };

        var nextStepFallback = errorCode switch
        {
            "VAULT_NOT_CONFIGURED" => "Select a vault folder, then start the application again.",
            "VAULT_ROOT_INVALID" => "Choose a different vault folder, then retry.",
            "VAULT_LOCK_UNAVAILABLE" => "Close the other window, then retry.",
            "VAULT_ROOT_ACCESS_DENIED" => "Correct the folder permissions, then retry.",
            "VAULT_ROOT_IO_FAILURE" => "Check that the drive is connected and healthy, then retry.",
            "CONFIGURATION_UNREADABLE" => "Open diagnostics to inspect the preserved file, then retry.",
            "CONFIGURATION_ACCESS_DENIED" => "Correct the permissions on the application data folder, then retry.",
            "DATABASE_INITIALIZATION_FAILED" => "Open diagnostics for the reported catalog schema problem, then retry.",
            "RECOVERY_FAILED" => "Open diagnostics for the failing checkpoint. Retry once the reported condition is resolved.",
            "CRITICAL_INTEGRITY_VIOLATION" => "Open diagnostics for the reported conflict. Writable access stays closed until it is resolved.",
            "STARTUP_CANCELLED" => "Retry when you are ready.",
            _ => "Open diagnostics for the recorded failure, then retry.",
        };

        var title = SurfaceText.Get($"Startup.Error.{keySuffix}.Title", titleFallback);
        var message = SurfaceText.Get($"Startup.Error.{keySuffix}.Message", messageFallback);
        var nextStep = SurfaceText.Get($"Startup.Error.{keySuffix}.NextStep", nextStepFallback);

        return (title, message, nextStep, canRetry);
    }
}
