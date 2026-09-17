using AresToys.App.Services;
using Xunit;

namespace AresToys.App.Tests;

public sealed class MagnifierPlacementTests
{
    // A 140 px circle + a ~130×92 label card, on a 1920×1040 work area whose monitor starts at
    // x=1920 in the virtual screen (i.e. a secondary monitor to the right of the primary).
    private const double CircleW = 140, CircleH = 140, LabelsW = 130, LabelsH = 92;
    private const double Offset = 24, Margin = 8, Gap = 4;
    private static readonly MagnifierPlacement.Rect Primary = new(0, 0, 1920, 1040);

    private static MagnifierPlacement.Placement Place(
        double x, double y, MagnifierPlacement.Rect bounds, bool flippedX = false, bool flippedY = false)
        => MagnifierPlacement.Compute(x, y, CircleW, CircleH, LabelsW, LabelsH, bounds, flippedX, flippedY, Offset, Margin, Gap);

    [Fact]
    public void AwayFromEdges_SitsBelowRightOfCursor()
    {
        var p = Place(500, 500, Primary);
        Assert.Equal(500 + Offset, p.CircleY);
        Assert.Equal(500 + Offset + CircleH + Gap, p.LabelsY);
        Assert.True(p.CircleX > 500);
    }

    [Fact]
    public void NearBottom_FlipsAboveCursorSoTheLabelCardStaysVisible()
    {
        // Cursor 60 px from the bottom of the work area: below-cursor placement would need
        // 24 + 140 + 4 + 92 = 260 px.
        var p = Place(500, 980, Primary);
        var blockH = CircleH + Gap + LabelsH;
        Assert.Equal(980 - Offset - blockH, p.CircleY);
        Assert.True(p.LabelsY + LabelsH <= Primary.Bottom - Margin);
    }

    [Fact]
    public void NearRight_FlipsLeftOfCursor()
    {
        var p = Place(1890, 500, Primary);
        var blockW = Math.Max(CircleW, LabelsW);
        Assert.True(p.CircleX + CircleW <= Primary.Right - Margin);
        Assert.Equal(1890 - Offset - blockW + (blockW - CircleW) / 2, p.CircleX);
    }

    [Fact]
    public void BottomRightCorner_StaysFullyInsideBothAxes()
    {
        var p = Place(1915, 1035, Primary);
        Assert.True(p.CircleX >= Primary.Left + Margin);
        Assert.True(p.CircleY >= Primary.Top + Margin);
        Assert.True(Math.Max(p.CircleX + CircleW, p.LabelsX + LabelsW) <= Primary.Right - Margin);
        Assert.True(p.LabelsY + LabelsH <= Primary.Bottom - Margin);
    }

    /// <summary>The regression this fix is about: on a virtual screen the bottom of a
    /// NON-bottom-most monitor is nowhere near the bottom of the overlay window, so the old
    /// window-relative test never flipped and the card was drawn onto the monitor below / past
    /// the screen edge. Bounds here are the middle monitor of a vertical stack.</summary>
    [Fact]
    public void MonitorInTheMiddleOfTheVirtualScreen_ClampsToThatMonitorNotTheWindow()
    {
        var middle = new MagnifierPlacement.Rect(0, 1080, 1920, 1040);
        var p = Place(500, 2110, middle); // 10 px from that monitor's bottom edge

        Assert.True(p.CircleY >= middle.Top + Margin);
        Assert.True(p.LabelsY + LabelsH <= middle.Bottom - Margin);
    }

    [Fact]
    public void NegativeOriginMonitor_IsHandled()
    {
        // Monitor to the LEFT of the primary: origin is negative in virtual-screen space.
        var left = new MagnifierPlacement.Rect(-1920, 0, 1920, 1080);
        var p = Place(-30, 1070, left);

        Assert.True(p.CircleX >= left.Left + Margin);
        Assert.True(p.LabelsY + LabelsH <= left.Bottom - Margin);
    }

    // ── Hysteresis: once the block has moved to a side, it stays there ────────────────────

    [Fact]
    public void FlippedUp_StaysUpEvenWhereBelowWouldAlsoFit()
    {
        // Mid-screen, so both sides fit. Coming from a flipped state it must NOT snap back.
        var p = Place(500, 500, Primary, flippedY: true);

        var blockH = CircleH + Gap + LabelsH;
        Assert.True(p.FlippedY);
        Assert.Equal(500 - Offset - blockH, p.CircleY);
    }

    [Fact]
    public void FlippedLeft_StaysLeftEvenWhereRightWouldAlsoFit()
    {
        var p = Place(900, 500, Primary, flippedX: true);

        var blockW = Math.Max(CircleW, LabelsW);
        Assert.True(p.FlippedX);
        Assert.Equal(900 - Offset - blockW + (blockW - CircleW) / 2, p.CircleX);
    }

    [Fact]
    public void FlippedUp_ComesBackDownOnlyWhenTheUpperSideStopsFitting()
    {
        // Cursor near the TOP edge: there's no longer room above, so the block has to return
        // below the cursor. This is the only thing that may unstick it.
        var p = Place(500, 40, Primary, flippedY: true);

        Assert.False(p.FlippedY);
        Assert.Equal(40 + Offset, p.CircleY);
    }

    [Fact]
    public void WalkingAwayFromTheBottomCornerDoesNotOscillate()
    {
        // Replays a cursor moving up and left from the bottom-right corner, feeding each frame's
        // sides into the next. After the initial flip the sides must never change again.
        var flippedX = false;
        var flippedY = false;
        var flips = 0;
        for (var i = 0; i < 20; i++)
        {
            var p = Place(1900 - i * 40, 1030 - i * 40, Primary, flippedX, flippedY);
            if (p.FlippedX != flippedX || p.FlippedY != flippedY) flips++;
            flippedX = p.FlippedX;
            flippedY = p.FlippedY;
        }

        Assert.Equal(1, flips); // just the first one, at the corner
        Assert.True(flippedX);
        Assert.True(flippedY);
    }

    [Fact]
    public void MonitorShorterThanTheBlock_PinsToTheTopEdgeInsteadOfOverflowingUpwards()
    {
        // 200×200 work area: the block (140 wide, 236 tall) still fits horizontally but not
        // vertically. Horizontally it gets pushed left until it fits; vertically the top edge
        // wins, so the circle — the part the user is actually aiming with — stays on screen.
        var tiny = new MagnifierPlacement.Rect(0, 0, 200, 200);
        var p = Place(100, 100, tiny);

        Assert.Equal(tiny.Right - Margin - CircleW, p.CircleX);
        Assert.Equal(tiny.Top + Margin, p.CircleY);
    }
}
