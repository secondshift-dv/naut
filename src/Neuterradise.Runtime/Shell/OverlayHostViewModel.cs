using System.Windows.Input;

namespace Neuterradise.App.Shell;

public sealed class OverlayHostViewModel : ObservableObject
{

    public const int MaxDepth = 2;

    private readonly List<OverlayEntry> _stack = [];
    private readonly RelayCommand _confirmCommand;
    private readonly RelayCommand _dismissCommand;

    public OverlayHostViewModel()
    {
        _confirmCommand = new RelayCommand(_ => ConfirmTop(), _ => HasOverlay);
        _dismissCommand = new RelayCommand(_ => CloseTop(), _ => HasOverlay);
    }

    public ICommand ConfirmCommand => _confirmCommand;

    public ICommand DismissCommand => _dismissCommand;

    public OverlayRequest? Current => _stack.Count > 0 ? _stack[^1].Request : null;

    public bool HasOverlay => _stack.Count > 0;

    public int Depth => _stack.Count;

    public bool BlocksBackgroundInput => Current?.BlocksBackgroundInput ?? false;

    public void Push(
        OverlayRequest request,
        Action? restoreFocus = null,
        Action<OverlayOutcome>? onClosed = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!UiDispatch.CheckAccess())
        {
            UiDispatch.Post(() => Push(request, restoreFocus, onClosed));
            return;
        }

        if (_stack.Count >= MaxDepth)
        {
            throw new InvalidOperationException(
                $"The overlay stack is bounded to {MaxDepth}; replace the current overlay's content instead of stacking another.");
        }

        _stack.Add(new OverlayEntry(request, restoreFocus, onClosed));
        RaiseOverlayChanged();
    }

    public void CloseTop() => Close(OverlayOutcome.Dismissed);

    public void ConfirmTop() => Close(OverlayOutcome.Confirmed);

    public void RequestEscapeClose()
    {
        if (_stack.Count > 0 && _stack[^1].Request.IsDismissableByEscape)
        {
            Close(OverlayOutcome.Dismissed);
        }
    }

    private void Close(OverlayOutcome outcome)
    {
        if (!UiDispatch.CheckAccess())
        {
            UiDispatch.Post(() => Close(outcome));
            return;
        }

        if (_stack.Count == 0)
        {
            return;
        }

        var entry = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);

        if (outcome == OverlayOutcome.Confirmed
            && entry.Request is ProfilePickerOverlayRequest pickerRequest
            && pickerRequest.SelectedCandidate is not null)
        {
            pickerRequest.OnProfileSelected(pickerRequest.SelectedCandidate);
        }
        entry.OnClosed?.Invoke(outcome);
        entry.RestoreFocus?.Invoke();

        RaiseOverlayChanged();
    }

    private void RaiseOverlayChanged()
    {
        RaisePropertyChanged(nameof(Current));
        RaisePropertyChanged(nameof(HasOverlay));
        RaisePropertyChanged(nameof(Depth));
        RaisePropertyChanged(nameof(BlocksBackgroundInput));

        _confirmCommand.RaiseCanExecuteChanged();
        _dismissCommand.RaiseCanExecuteChanged();
    }

    private sealed record OverlayEntry(
        OverlayRequest Request,
        Action? RestoreFocus,
        Action<OverlayOutcome>? OnClosed);
}
