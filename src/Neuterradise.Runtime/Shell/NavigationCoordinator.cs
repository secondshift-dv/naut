namespace Neuterradise.App.Shell;

public enum NavigationKind { Navigate, Replace, Back, ResetToTopLevel }
public sealed class NavigationChangedEventArgs(AppRoute previous, AppRoute current, NavigationKind kind) : EventArgs
{
    public AppRoute Previous { get; } = previous;
    public AppRoute Current { get; } = current;
    public NavigationKind Kind { get; } = kind;
}

public sealed class NavigationCoordinator
{
    public const int MaxBackStackEntries = 50;
    private readonly List<AppRoute> _backStack = [];
    private readonly List<AppRoute> _forwardStack = [];
    private AppRoute _currentRoute;
    public NavigationCoordinator(AppRoute? initialRoute = null) => _currentRoute = initialRoute ?? new HomeRoute();
    public event EventHandler<NavigationChangedEventArgs>? Navigated;
    public AppRoute CurrentRoute => _currentRoute;
    public bool CanGoBack => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;
    public IReadOnlyList<AppRoute> BackStack => _backStack.AsReadOnly();
    public IReadOnlyList<AppRoute> ForwardStack => _forwardStack.AsReadOnly();

    /// <summary>
    /// Replaces the in-memory record of the current route with an equivalent route carrying
    /// updated state (e.g. a Gallery query/page snapshot), without firing <see cref="Navigated"/>
    /// or touching the back/forward stacks. Call this immediately before <see cref="Navigate"/>
    /// so the state that gets pushed onto the back stack reflects what the user actually saw,
    /// letting Back restore it exactly.
    /// </summary>
    public void UpdateCurrentRoute(AppRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        _currentRoute = route;
    }

    public void Navigate(AppRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route == _currentRoute) return;
        PushBounded(_backStack, _currentRoute);
        _forwardStack.Clear();
        Transition(route, NavigationKind.Navigate);
    }
    public void Replace(AppRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route == _currentRoute) return;
        Transition(route, NavigationKind.Replace);
    }
    public void GoBack()
    {
        if (_backStack.Count == 0) return;
        PushBounded(_forwardStack, _currentRoute);
        var target = _backStack[^1]; _backStack.RemoveAt(_backStack.Count - 1);
        Transition(target, NavigationKind.Back);
    }
    public void GoForward()
    {
        if (_forwardStack.Count == 0) return;
        PushBounded(_backStack, _currentRoute);
        var target = _forwardStack[^1]; _forwardStack.RemoveAt(_forwardStack.Count - 1);
        Transition(target, NavigationKind.Navigate);
    }
    public void ResetToTopLevel(AppRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        _backStack.Clear(); _forwardStack.Clear();
        if (route != _currentRoute) Transition(route, NavigationKind.ResetToTopLevel);
    }
    private static void PushBounded(List<AppRoute> stack, AppRoute route)
    {
        stack.Add(route);
        if (stack.Count > MaxBackStackEntries) stack.RemoveRange(0, stack.Count - MaxBackStackEntries);
    }
    private void Transition(AppRoute route, NavigationKind kind)
    {
        var previous = _currentRoute; _currentRoute = route;
        Navigated?.Invoke(this, new NavigationChangedEventArgs(previous, route, kind));
    }
}
