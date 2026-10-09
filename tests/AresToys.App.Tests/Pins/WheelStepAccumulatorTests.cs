using AresToys.App.Views;
using Xunit;

namespace AresToys.App.Tests.Pins;

public sealed class WheelStepAccumulatorTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void MouseNotch_IsExactlyOneStepEachWay()
    {
        var acc = new WheelStepAccumulator();
        Assert.Equal(1, acc.Add(120, Ms(0)));
        Assert.Equal(1, acc.Add(120, Ms(10)));
        Assert.Equal(-1, acc.Add(-120, Ms(20)));
    }

    [Fact]
    public void FastDoubleNotch_IsTwoSteps()
    {
        var acc = new WheelStepAccumulator();
        Assert.Equal(2, acc.Add(240, Ms(0)));
    }

    [Fact]
    public void TouchpadSmallDeltas_StepOncePer120()
    {
        var acc = new WheelStepAccumulator();
        var steps = 0;
        for (var i = 0; i < 12; i++) steps += acc.Add(10, Ms(i * 16)); // 120 in total
        Assert.Equal(1, steps);
        for (var i = 0; i < 11; i++) steps += acc.Add(10, Ms(200 + i * 16)); // 110 more
        Assert.Equal(1, steps);
        Assert.Equal(1, acc.Add(10, Ms(400)));
    }

    [Fact]
    public void DirectionChange_DropsTheRemainder()
    {
        var acc = new WheelStepAccumulator();
        Assert.Equal(0, acc.Add(100, Ms(0)));
        Assert.Equal(0, acc.Add(-30, Ms(10)));   // not 70 left: the 100 is gone
        Assert.Equal(-1, acc.Add(-90, Ms(20)));
    }

    [Fact]
    public void Idle_DropsTheRemainder()
    {
        var acc = new WheelStepAccumulator();
        Assert.Equal(0, acc.Add(100, Ms(0)));
        Assert.Equal(0, acc.Add(30, Ms(0) + WheelStepAccumulator.IdleReset + Ms(1)));
        Assert.Equal(0, acc.Add(80, Ms(400)));   // 30 + 80 = 110, still short of a step
        Assert.Equal(1, acc.Add(10, Ms(410)));
    }

    [Fact]
    public void ZeroDelta_IsIgnored()
    {
        var acc = new WheelStepAccumulator();
        Assert.Equal(0, acc.Add(0, Ms(0)));
        Assert.Equal(1, acc.Add(120, Ms(5)));
    }
}
