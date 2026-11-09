namespace AresToys.App.Services;

/// <summary>Time ↔ pixel arithmetic for the trim dialog's timeline: where to place the two handles,
/// how wide the bands of footage being discarded are, and which time a click or drag lands on.
///
/// Separate from the window so it can be tested without a visual tree — the same reason
/// <see cref="MagnifierPlacement"/> lives apart from the magnifier. Every entry point tolerates a
/// zero width (the control hasn't been laid out yet) and a zero duration (the media hasn't opened
/// yet), because both happen in the first moments after the dialog is shown.</summary>
public static class TrimTimelineGeometry
{
    /// <summary>Horizontal offset of <paramref name="t"/> on a track <paramref name="width"/> wide.</summary>
    public static double TimeToX(TimeSpan t, TimeSpan duration, double width)
    {
        if (width <= 0 || duration <= TimeSpan.Zero) return 0;
        var ratio = t.TotalSeconds / duration.TotalSeconds;
        return Math.Clamp(ratio, 0, 1) * width;
    }

    /// <summary>The time under <paramref name="x"/>, clamped to the clip: a drag continues to fire
    /// while the pointer is outside the control, and the handle has to stop at the ends.</summary>
    public static TimeSpan XToTime(double x, TimeSpan duration, double width)
    {
        if (width <= 0 || duration <= TimeSpan.Zero) return TimeSpan.Zero;
        var ratio = Math.Clamp(x / width, 0, 1);
        return TimeSpan.FromSeconds(ratio * duration.TotalSeconds);
    }

    /// <summary>Lay out the three bands of the track in pixels: the head being discarded (from the
    /// left edge), then the offset and width of the part being kept. The tail being discarded is
    /// whatever remains to the right, so callers can anchor it to the right edge.
    ///
    /// Handles that have crossed (start after end) are ordered here rather than producing negative
    /// widths, which WPF would throw on: mid-drag the caller hasn't normalised them yet.</summary>
    public static (double HeadWidth, double KeepX, double KeepWidth) Bands(
        TimeSpan start, TimeSpan end, TimeSpan duration, double width)
    {
        if (end < start) (start, end) = (end, start);
        var keepX = TimeToX(start, duration, width);
        var keepEnd = TimeToX(end, duration, width);
        var keepWidth = Math.Max(0, keepEnd - keepX);
        return (Math.Max(0, keepX), keepX, keepWidth);
    }
}
