namespace Neuterradise.App.SystemServices.Resources;

/// <summary>
/// Combines activation and presenter state without allowing either event stream to erase the other.
/// A minimized window is always inactive from the governor's point of view.
/// </summary>
public sealed class WindowStateProjection
{
    private bool _active = true;
    private bool _minimized;

    public WindowResourceState Current => new(_active && !_minimized, _minimized);

    public WindowResourceState SetActive(bool active)
    {
        _active = active;
        return Current;
    }

    public WindowResourceState SetMinimized(bool minimized)
    {
        _minimized = minimized;
        return Current;
    }
}

public readonly record struct WindowResourceState(bool IsActive, bool IsMinimized);
