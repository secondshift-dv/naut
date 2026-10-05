using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Shell;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private readonly Action<object?> _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    private readonly Func<object?, bool>? _canExecute = canExecute;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _execute(parameter);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;

    private bool _isRunning;

    public AsyncRelayCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? onError = null)
        : this(
            _ => (execute ?? throw new ArgumentNullException(nameof(execute)))(),
            canExecute is null ? null : _ => canExecute(),
            onError)
    {
    }

    public AsyncRelayCommand(
        Func<object?, Task> execute,
        Func<object?, bool>? canExecute = null,
        Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;

    public event EventHandler<AsyncCommandFailedEventArgs>? ExecutionFailed;

    public bool IsRunning => _isRunning;

    public Exception? LastException { get; private set; }

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    void ICommand.Execute(object? parameter) => ExecuteFromCommand(parameter);

    private async void ExecuteFromCommand(object? parameter)
    {
        try
        {
            await ExecuteAsync(parameter);
        }
        catch (Exception exception)
        {
            Trace.TraceError("Async command failed: {0}", exception);
        }
    }

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        LastException = null;
        RaiseCanExecuteChanged();

        try
        {
            await _execute(parameter);
        }
        catch (Exception exception)
        {
            LastException = exception;
            ExecutionFailed?.Invoke(this, new AsyncCommandFailedEventArgs(exception));

            if (_onError is not null)
            {
                _onError(exception);
                return;
            }

            throw;
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncCommandFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception ?? throw new ArgumentNullException(nameof(exception));
}

public enum ScreenStatus
{
    Loading,
    Ready,
    Empty,
    RecoverableError,
    CriticalError,
}

public abstract class ScreenStateViewModel : ObservableObject
{
    private readonly CancellationTokenSource _routeLifetime = new();
    private ScreenStatus _status = ScreenStatus.Loading;
    private bool _isBackgroundUpdating;
    private string? _errorMessage;
    private bool _isRouteActive = true;

    public ScreenStatus Status => _status;
    public bool IsBackgroundUpdating => _isBackgroundUpdating;
    public string? ErrorMessage => _errorMessage;
    public bool IsLoading => _status == ScreenStatus.Loading;
    public bool IsReady => _status == ScreenStatus.Ready;
    public bool IsEmpty => _status == ScreenStatus.Empty;
    public bool HasError => _status is ScreenStatus.RecoverableError or ScreenStatus.CriticalError;
    public bool IsRouteActive => _isRouteActive;
    protected CancellationToken RouteCancellationToken => _routeLifetime.Token;

    protected void StartRouteTask(Func<CancellationToken, Task> operation, string failurePrefix)
    {
        ArgumentNullException.ThrowIfNull(operation);
        TaskObserver.Observe(
            ObserveRouteTaskAsync(operation, failurePrefix),
            $"{GetType().Name}.RouteTask");
    }

    private async Task ObserveRouteTaskAsync(
        Func<CancellationToken, Task> operation,
        string failurePrefix)
    {
        try
        {
            await operation(RouteCancellationToken);
        }
        catch (OperationCanceledException) when (!_isRouteActive || RouteCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("Route-owned presentation task failed: {0}", exception);
            if (_isRouteActive)
            {
                var prefix = string.IsNullOrWhiteSpace(failurePrefix) ? "Operation failed." : failurePrefix.Trim();
                var safeMessage = OperationExecution.Classify(exception).Error.UserMessage;
                ShowRecoverableError($"{prefix} {safeMessage}".Trim());
            }
        }
    }

    internal void RetireRoute()
    {
        if (!_isRouteActive)
        {
            return;
        }

        _isRouteActive = false;
        _routeLifetime.Cancel();
    }

    protected void ShowLoading() => SetStatus(ScreenStatus.Loading, error: null);
    protected void ShowReady() => SetStatus(ScreenStatus.Ready, error: null);
    protected void ShowEmpty() => SetStatus(ScreenStatus.Empty, error: null);
    protected void ShowRecoverableError(string message) => SetStatus(ScreenStatus.RecoverableError, message);
    protected void ShowCriticalError(string message) => SetStatus(ScreenStatus.CriticalError, message);
    protected void BeginBackgroundUpdate() => SetBackgroundUpdating(true);
    protected void EndBackgroundUpdate() => SetBackgroundUpdating(false);

    private void SetStatus(ScreenStatus status, string? error)
    {
        if (!_isRouteActive)
        {
            return;
        }

        _status = status;
        _errorMessage = error;
        RaisePropertyChanged(nameof(Status));
        RaisePropertyChanged(nameof(ErrorMessage));
        RaisePropertyChanged(nameof(IsLoading));
        RaisePropertyChanged(nameof(IsReady));
        RaisePropertyChanged(nameof(IsEmpty));
        RaisePropertyChanged(nameof(HasError));
    }

    private void SetBackgroundUpdating(bool value)
    {
        if (!_isRouteActive || _isBackgroundUpdating == value)
        {
            return;
        }

        _isBackgroundUpdating = value;
        RaisePropertyChanged(nameof(IsBackgroundUpdating));
    }
}

public sealed class StaleResultGuard : IDisposable
{
    private readonly Lock _sync = new();
    private long _generation;
    private CancellationTokenSource? _current;
    private bool _disposed;

    public StaleResultToken Begin()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current?.Cancel();
            _current?.Dispose();
            _current = new CancellationTokenSource();
            _generation++;
            return new StaleResultToken(_generation, _current.Token);
        }
    }

    public bool IsCurrent(StaleResultToken token)
    {
        lock (_sync)
        {
            return token.Generation == _generation;
        }
    }

    public async Task RunLatestAsync<T>(
        Func<CancellationToken, Task<T>> load,
        Action<T> apply)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(apply);

        var token = Begin();
        try
        {
            var result = await load(token.CancellationToken);
            if (IsCurrent(token))
            {
                apply(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _current?.Cancel();
            _current?.Dispose();
            _current = null;
        }
    }
}

public readonly record struct StaleResultToken(long Generation, CancellationToken CancellationToken);
