namespace AresToys.App.Services;

/// <summary>Pure geometry for the screen colour picker's magnifier block (the round zoom circle
/// plus the hex/RGB/coords label card underneath it). Kept free of WPF types so the flip / clamp
/// rules can be unit-tested: the overlay window itself spans the whole virtual screen, so the
/// naive "flip when the block would leave the WINDOW" test never fires at the bottom edge of a
/// non-bottom-most monitor and the card ended up drawn past the screen edge (issue #11).</summary>
public static class MagnifierPlacement
{
    /// <summary>Rectangle in the overlay's coordinate space (device-independent pixels relative
    /// to the overlay window's top-left).</summary>
    public readonly record struct Rect(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }

    /// <summary>Where to draw the two pieces of the magnifier block, plus which side of the
    /// cursor each axis ended up on. Feed <see cref="FlippedX"/> / <see cref="FlippedY"/> back
    /// into the next <see cref="Compute"/> call to keep the block on that side.</summary>
    public readonly record struct Placement(
        double CircleX, double CircleY, double LabelsX, double LabelsY, bool FlippedX, bool FlippedY);

    /// <summary>Compute the block position for a cursor at <paramref name="cursorX"/>/
    /// <paramref name="cursorY"/>.
    /// The block sits below-right of the cursor by default and flips to the opposite side when
    /// it would overflow <paramref name="bounds"/> (the work area of the monitor the cursor is
    /// on, NOT the whole virtual screen).
    ///
    /// The chosen side is STICKY: pass the previous frame's <paramref name="wasFlippedX"/> /
    /// <paramref name="wasFlippedY"/> and the block stays where it is for as long as it fits,
    /// flipping back only when the current side stops working. Without that hysteresis the
    /// block snapped back to below-right the instant the cursor had room again, so walking away
    /// from a screen corner made it jump across the cursor repeatedly.
    ///
    /// Circle and label card are treated as ONE block: the card is what overflows at the bottom,
    /// so ignoring its height is exactly what let it fall off-screen.</summary>
    public static Placement Compute(
        double cursorX, double cursorY,
        double circleW, double circleH,
        double labelsW, double labelsH,
        Rect bounds,
        bool wasFlippedX = false,
        bool wasFlippedY = false,
        double cursorOffset = 24,
        double margin = 8,
        double labelGap = 4)
    {
        var blockW = Math.Max(circleW, labelsW);
        var blockH = circleH + labelGap + labelsH;

        var (x, flippedX) = Resolve(cursorX, blockW, bounds.Left, bounds.Right, wasFlippedX, cursorOffset, margin);
        var (y, flippedY) = Resolve(cursorY, blockH, bounds.Top, bounds.Bottom, wasFlippedY, cursorOffset, margin);

        // Final clamp — keeps the block inside the monitor whatever the sides decided. Max() last
        // so the left/top edge wins when the block is bigger than the monitor.
        x = Math.Max(bounds.Left + margin, Math.Min(x, bounds.Right - blockW - margin));
        y = Math.Max(bounds.Top + margin, Math.Min(y, bounds.Bottom - blockH - margin));

        // Circle and label card are centred against each other inside the block.
        return new Placement(
            CircleX: x + (blockW - circleW) / 2,
            CircleY: y,
            LabelsX: x + (blockW - labelsW) / 2,
            LabelsY: y + circleH + labelGap,
            FlippedX: flippedX,
            FlippedY: flippedY);
    }

    /// <summary>One axis of the placement: keep the current side while it fits, otherwise switch
    /// to the other one — and only if THAT one fits, so a monitor too small for the block doesn't
    /// make the side oscillate.</summary>
    private static (double Position, bool Flipped) Resolve(
        double cursor, double blockSize, double min, double max, bool wasFlipped, double cursorOffset, double margin)
    {
        double PositionFor(bool flipped) => flipped ? cursor - cursorOffset - blockSize : cursor + cursorOffset;
        bool Fits(double p) => p >= min + margin && p + blockSize + margin <= max;

        var current = PositionFor(wasFlipped);
        if (Fits(current)) return (current, wasFlipped);

        var alternative = PositionFor(!wasFlipped);
        return Fits(alternative) ? (alternative, !wasFlipped) : (current, wasFlipped);
    }
}
