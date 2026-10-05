using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Shell;

/// <summary>
/// Pure window geometry for the product contract. Everything is in device-independent units and works on
/// monitor <em>work areas</em>, so minimum sizing, startup placement, clamping, and monitor choice
/// can be verified without a live window or a real desktop.
/// </summary>
public static class WindowGeometry
{
    /// <summary>Smallest supported window. 800 × 600 is a real, reachable size.</summary>
    public const double MinimumWidth = 800;

    public const double MinimumHeight = 600;

    /// <summary>Preferred startup width before monitor clamping is applied.</summary>
    public const double PreferredWidth = 1400;

    public const double PreferredHeight = 900;

    /// <summary>Fraction of the work area used for the initial size when the preferred size does not fit.</summary>
    public const double WorkAreaFraction = 0.92;

    /// <summary>How much of the window must remain on a monitor for a saved position to be usable.</summary>
    public const double RequiredVisibleWidth = 220;

    public const double RequiredVisibleHeight = 80;

    /// <summary>Minimum size the window may take on the given work area.</summary>
    public static Size MinimumFor(Rect workArea) =>
        new(Math.Min(MinimumWidth, workArea.Width), Math.Min(MinimumHeight, workArea.Height));

    /// <summary>
    /// Computes a monitor-appropriate initial size for a fresh installation. This is an internal
    /// startup rule, not a user-selectable preset.
    /// </summary>
    public static Size DefaultSize(Rect workArea)
    {
        var minimum = MinimumFor(workArea);
        var width = Math.Min(PreferredWidth, workArea.Width * WorkAreaFraction);
        var height = Math.Min(PreferredHeight, workArea.Height * WorkAreaFraction);
        width = Math.Max(minimum.Width, width);
        height = Math.Max(minimum.Height, height);
        return new Size(Math.Floor(width), Math.Floor(height));
    }

    /// <summary>
    /// True when a rectangle still lands on some connected monitor with enough of the title bar
    /// reachable. The primary monitor is not special: a window on a secondary monitor that is still
    /// connected is on screen.
    /// </summary>
    public static bool IsOnScreen(Rect bounds, IReadOnlyList<Rect> monitorWorkAreas)
    {
        ArgumentNullException.ThrowIfNull(monitorWorkAreas);

        foreach (var area in monitorWorkAreas)
        {
            var overlap = Rect.Intersect(bounds, area);
            if (!overlap.IsEmpty
                && overlap.Width >= Math.Min(RequiredVisibleWidth, bounds.Width)
                && overlap.Height >= Math.Min(RequiredVisibleHeight, bounds.Height))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The work area of the monitor the rectangle mostly lies on; the fallback (normally the primary
    /// monitor) when it touches none of them, for example because its monitor was unplugged.
    /// </summary>
    public static Rect SelectWorkArea(Rect bounds, IReadOnlyList<Rect> monitorWorkAreas, Rect fallback)
    {
        ArgumentNullException.ThrowIfNull(monitorWorkAreas);

        var best = fallback;
        var bestArea = 0.0;
        foreach (var area in monitorWorkAreas)
        {
            var overlap = Rect.Intersect(bounds, area);
            if (overlap.IsEmpty)
            {
                continue;
            }

            var overlapArea = overlap.Width * overlap.Height;
            if (overlapArea > bestArea)
            {
                best = area;
                bestArea = overlapArea;
            }
        }

        return best;
    }

    /// <summary>Shrinks the rectangle to fit the work area and moves it fully inside.</summary>
    public static Rect ClampToWorkArea(Rect bounds, Rect workArea)
    {
        var minimum = MinimumFor(workArea);
        var width = Math.Clamp(bounds.Width, minimum.Width, Math.Max(minimum.Width, workArea.Width));
        var height = Math.Clamp(bounds.Height, minimum.Height, Math.Max(minimum.Height, workArea.Height));
        var left = Math.Clamp(bounds.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        var top = Math.Clamp(bounds.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        return new Rect(left, top, width, height);
    }

    public static Rect CenterIn(Size size, Rect workArea) =>
        ClampToWorkArea(
            new Rect(
                Math.Floor(workArea.Left + ((workArea.Width - size.Width) / 2)),
                Math.Floor(workArea.Top + ((workArea.Height - size.Height) / 2)),
                size.Width,
                size.Height),
            workArea);

    /// <summary>
    /// Decides the restore rectangle and maximized state the window opens with. A saved placement on
    /// any still-connected monitor is honoured (clamped to that monitor); otherwise the window is
    /// centred on the fallback monitor, keeping the saved size when there was one.
    /// </summary>
    public static StartupPlacement ResolveStartup(
        WindowPlacementConfiguration? saved,
        IReadOnlyList<Rect> monitorWorkAreas,
        Rect fallbackWorkArea)
    {
        ArgumentNullException.ThrowIfNull(monitorWorkAreas);

        if (saved is { IsUsable: true })
        {
            var bounds = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);
            if (IsOnScreen(bounds, monitorWorkAreas))
            {
                var workArea = SelectWorkArea(bounds, monitorWorkAreas, fallbackWorkArea);
                return new StartupPlacement(ClampToWorkArea(bounds, workArea), workArea, saved.IsMaximized, RestoredSaved: true);
            }

            var size = ClampToWorkArea(new Rect(0, 0, saved.Width, saved.Height), fallbackWorkArea).Size;
            return new StartupPlacement(CenterIn(size, fallbackWorkArea), fallbackWorkArea, saved.IsMaximized, RestoredSaved: false);
        }

        return new StartupPlacement(
            CenterIn(DefaultSize(fallbackWorkArea), fallbackWorkArea),
            fallbackWorkArea,
            IsMaximized: false,
            RestoredSaved: false);
    }
}

public readonly record struct StartupPlacement(Rect Bounds, Rect WorkArea, bool IsMaximized, bool RestoredSaved);
