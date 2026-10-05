using System.Globalization;
using System.Runtime.ExceptionServices;

namespace Neuterradise.App.SystemServices;

/// <summary>
/// Renderer-neutral primitives used by the application runtime and view models. The runtime never
/// references WPF or Uno types; the presentation layer maps these to its own at the edge.
/// </summary>
public readonly record struct Point(double X, double Y);

public readonly record struct Size(double Width, double Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Axis-aligned rectangle in device-independent units. Semantics match the WPF rectangle the
/// window geometry rules were written against: an empty intersection reports <see cref="IsEmpty"/>.</summary>
public readonly record struct Rect
{
    public Rect(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        _empty = false;
    }

    private Rect(bool empty)
    {
        _empty = empty;
    }

    private readonly bool _empty;

    public static Rect Empty { get; } = new(empty: true);

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public bool IsEmpty => _empty;

    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public Size Size => new(Width, Height);

    public Point Location => new(X, Y);

    public bool Contains(Point point) =>
        !IsEmpty && point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    /// <summary>True when <paramref name="rect"/> lies entirely inside this rectangle (edges included).</summary>
    public bool Contains(Rect rect) =>
        !IsEmpty && !rect.IsEmpty && rect.Left >= Left && rect.Top >= Top && rect.Right <= Right && rect.Bottom <= Bottom;

    public bool IntersectsWith(Rect other) => !Intersect(this, other).IsEmpty;

    public static Rect Intersect(Rect a, Rect b)
    {
        if (a.IsEmpty || b.IsEmpty)
        {
            return Empty;
        }

        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right < left || bottom < top ? Empty : new Rect(left, top, right - left, bottom - top);
    }

    public override string ToString() =>
        IsEmpty ? "Empty" : string.Create(CultureInfo.InvariantCulture, $"{X},{Y},{Width},{Height}");
}

public enum MouseButton
{
    Left,
    Middle,
    Right,
    XButton1,
    XButton2,
}

[Flags]
public enum ModifierKeys
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>The keys the runtime reasons about. The presentation layer maps its virtual keys onto these.</summary>
public enum Key
{
    None,
    A,
    D0,
    NumPad0,
    Add,
    Subtract,
    OemPlus,
    OemMinus,
    Enter,
    Escape,
    Space,
    Left,
    Right,
    Up,
    Down,
    Home,
    End,
    PageUp,
    PageDown,
    Delete,
    F,
    Tab,
}

/// <summary>
/// The one bridge from background work to the UI thread. The presentation host installs its UI
/// <see cref="SynchronizationContext"/> once at startup; until then (and in headless tests) work runs inline.
/// </summary>
public static class UiDispatch
{
    private static SynchronizationContext? _context;
    private static int _uiThreadId = -1;

    public static void Install(SynchronizationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _uiThreadId = Environment.CurrentManagedThreadId;
    }

    public static SynchronizationContext? Context => _context;

    public static bool CheckAccess() =>
        _context is null || Environment.CurrentManagedThreadId == _uiThreadId;

    /// <summary>Runs on the UI thread: inline when already there, otherwise queued.</summary>
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            action();
        }
        else
        {
            _context!.Post(static state => ((Action)state!)(), action);
        }
    }

    /// <summary>Queues on the UI thread even when already there.</summary>
    public static void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(static state => ((Action)state!)(), action);
        }
    }

    public static Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context!.Post(_ =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }, null);
        return completion.Task;
    }

    public static Task InvokeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            return action();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context!.Post(async _ =>
        {
            try
            {
                await action().ConfigureAwait(true);
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }, null);
        return completion.Task;
    }
}

/// <summary>
/// SynchronizationContext for dispatchers whose queue can reject work during shutdown. Rejection is
/// observable to callers; Send never waits for a callback that was not accepted.
/// </summary>
public class EnqueueSynchronizationContext(
    Func<bool> hasThreadAccess,
    Func<Action, bool> tryEnqueue) : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (!tryEnqueue(() => d(state)))
        {
            throw new InvalidOperationException("The UI dispatcher is shutting down and rejected the callback.");
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (hasThreadAccess())
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        ExceptionDispatchInfo? callbackFailure = null;
        if (!tryEnqueue(() =>
        {
            try
            {
                d(state);
            }
            catch (Exception exception)
            {
                callbackFailure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                done.Set();
            }
        }))
        {
            throw new InvalidOperationException("The UI dispatcher is shutting down and rejected the callback.");
        }

        done.Wait();
        callbackFailure?.Throw();
    }

    public override SynchronizationContext CreateCopy() => this;
}

/// <summary>
/// A request to show a derived image at a target decode width. View models carry this value instead
/// of a renderer image object; the presentation layer resolves it through its bounded decode cache,
/// cancels it on recycle, and never decodes at full resolution for a small surface.
/// </summary>
public sealed record ImageRef(string Path, int DecodeWidth, string? ResourceKey = null)
{
    public static ImageRef? FromPath(string? path, int decodeWidth, string? resourceKey = null) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : new ImageRef(path, Math.Max(0, decodeWidth), resourceKey);
}

/// <summary>A theme colour as #AARRGGBB channels, independent of any UI framework.</summary>
public readonly record struct ArgbColor(byte A, byte R, byte G, byte B)
{
    public static ArgbColor FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);

    public ArgbColor WithAlpha(byte alpha) => this with { A = alpha };

    public double Luminance
    {
        get
        {
            static double Channel(byte c)
            {
                var v = c / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(R)) + (0.7152 * Channel(G)) + (0.0722 * Channel(B));
        }
    }

    public static ArgbColor Lerp(ArgbColor a, ArgbColor b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new ArgbColor(
            (byte)Math.Round(a.A + ((b.A - a.A) * t)),
            (byte)Math.Round(a.R + ((b.R - a.R) * t)),
            (byte)Math.Round(a.G + ((b.G - a.G) * t)),
            (byte)Math.Round(a.B + ((b.B - a.B) * t)));
    }

    public string ToHex() => string.Create(CultureInfo.InvariantCulture, $"#{A:X2}{R:X2}{G:X2}{B:X2}");

    public override string ToString() => ToHex();
}

/// <summary>
/// Process-wide motion accessibility authority. naut follows the operating system animation
/// preference automatically; there is no separate application-level Reduced Motion preference.
/// </summary>
public static class ReducedMotionAuthority
{
    private static bool _system;

    public static event EventHandler? Changed;

    public static bool IsReduced => _system;

    public static bool SystemPreference => _system;

    public static void ApplySystem(bool systemPreference)
    {
        if (_system == systemPreference)
        {
            return;
        }

        _system = systemPreference;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
