namespace AresToys.App.Services.Pins;

/// <summary>Rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>Width and height of the overlap with <paramref name="other"/> (0 when disjoint).</summary>
    public (int Width, int Height) Overlap(PixelRect other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return w > 0 && h > 0 ? (w, h) : (0, 0);
    }
}

/// <summary>Pure geometry for restoring a pin: keep the saved position when enough of the window
/// is still on a connected monitor, otherwise centre it on the primary one. Same idea as the
/// wormholes' off-screen snap, but in physical pixels so mixed-DPI setups behave.</summary>
public static class PinPlacement
{
    /// <summary>Side of the square that must stay visible (or the whole window when smaller).</summary>
    public const int MinVisiblePixels = 32;

    /// <summary>Returns the rectangle to restore at. <paramref name="monitors"/> are the monitor
    /// bounds; <paramref name="primary"/> is where an orphaned pin goes (null = first monitor).
    /// With no monitor at all the input comes back unchanged.</summary>
    public static PixelRect EnsureVisible(PixelRect window, IReadOnlyList<PixelRect> monitors, PixelRect? primary)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0) return window;

        var needW = Math.Max(1, Math.Min(MinVisiblePixels, window.Width));
        var needH = Math.Max(1, Math.Min(MinVisiblePixels, window.Height));
        foreach (var m in monitors)
        {
            // A square, not just an area: a 10 px sliver along an edge is too thin to grab.
            var (w, h) = window.Overlap(m);
            if (w >= needW && h >= needH) return window;
        }

        var target = primary ?? monitors[0];
        var x = target.X + (target.Width - window.Width) / 2;
        var y = target.Y + (target.Height - window.Height) / 2;
        // A pin bigger than the monitor keeps its top-left corner on screen, where the drag
        // surface and the hover toolbar are.
        x = Math.Max(target.X, x);
        y = Math.Max(target.Y, y);
        return window with { X = x, Y = y };
    }
}
