using AresToys.App.Services;
using Xunit;

namespace AresToys.App.Tests;

/// <summary>Time ↔ pixel conversions behind the trim timeline: where the handles sit, how wide the
/// discarded bands are, and what time a click or a drag lands on. Pure arithmetic, but the kind
/// that fails silently (a handle a few pixels off, a drag that drifts) so it's worth pinning.</summary>
public sealed class TrimTimelineGeometryTests
{
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);
    private const double Width = 200;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 100)]
    [InlineData(10, 200)]
    [InlineData(2.5, 50)]
    public void TimeMapsOntoTheTrackProportionally(double seconds, double expectedX)
    {
        Assert.Equal(expectedX, TrimTimelineGeometry.TimeToX(TimeSpan.FromSeconds(seconds), Ten, Width), precision: 6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 5)]
    [InlineData(200, 10)]
    public void PixelsMapBackOntoTime(double x, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, TrimTimelineGeometry.XToTime(x, Ten, Width).TotalSeconds, precision: 6);
    }

    [Fact]
    public void ARoundTripThroughBothDirectionsLandsWhereItStarted()
    {
        var t = TimeSpan.FromSeconds(3.7);
        var back = TrimTimelineGeometry.XToTime(TrimTimelineGeometry.TimeToX(t, Ten, Width), Ten, Width);
        Assert.Equal(t.TotalSeconds, back.TotalSeconds, precision: 6);
    }

    [Fact]
    public void APointerDraggedPastEitherEndStaysOnTheTrack()
    {
        Assert.Equal(TimeSpan.Zero, TrimTimelineGeometry.XToTime(-50, Ten, Width));
        Assert.Equal(Ten, TrimTimelineGeometry.XToTime(9999, Ten, Width));
    }

    [Fact]
    public void ADegenerateTrackOrClipDoesNotDivideByZero()
    {
        Assert.Equal(0, TrimTimelineGeometry.TimeToX(TimeSpan.FromSeconds(5), Ten, 0));
        Assert.Equal(0, TrimTimelineGeometry.TimeToX(TimeSpan.FromSeconds(5), TimeSpan.Zero, Width));
        Assert.Equal(TimeSpan.Zero, TrimTimelineGeometry.XToTime(50, TimeSpan.Zero, Width));
        Assert.Equal(TimeSpan.Zero, TrimTimelineGeometry.XToTime(50, Ten, 0));
    }

    [Fact]
    public void TheDiscardedBandsAreTheHeadAndTailOutsideTheSelection()
    {
        // Keeping 2s→8s of a 10s clip on a 200px track: 40px of head and 40px of tail get cut.
        var (headWidth, keepX, keepWidth) = TrimTimelineGeometry.Bands(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), Ten, Width);

        Assert.Equal(40, headWidth, precision: 6);
        Assert.Equal(40, keepX, precision: 6);
        Assert.Equal(120, keepWidth, precision: 6);
    }

    [Fact]
    public void SelectingEverythingLeavesNothingMarkedForRemoval()
    {
        var (headWidth, keepX, keepWidth) = TrimTimelineGeometry.Bands(TimeSpan.Zero, Ten, Ten, Width);

        Assert.Equal(0, headWidth, precision: 6);
        Assert.Equal(0, keepX, precision: 6);
        Assert.Equal(Width, keepWidth, precision: 6);
    }

    [Fact]
    public void BandsNeverComeBackNegativeWhenTheHandlesCross()
    {
        // Reachable mid-drag, before the caller re-orders the two handles.
        var (headWidth, keepX, keepWidth) = TrimTimelineGeometry.Bands(
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(2), Ten, Width);

        Assert.True(headWidth >= 0);
        Assert.True(keepX >= 0);
        Assert.True(keepWidth >= 0);
    }
}
