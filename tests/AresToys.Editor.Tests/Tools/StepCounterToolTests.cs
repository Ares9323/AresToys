using AresToys.Editor.Model;
using AresToys.Editor.Tools;
using Xunit;

namespace AresToys.Editor.Tests.Tools;

public class StepCounterToolTests
{
    [Fact]
    public void Sequential_clicks_yield_incrementing_numbers()
    {
        var t = new StepCounterTool();
        t.Begin(10, 10, ShapeColor.Red, ShapeColor.Transparent, 2);
        var first = (StepCounterShape)t.Commit(10, 10)!;
        t.Begin(20, 20, ShapeColor.Red, ShapeColor.Transparent, 2);
        var second = (StepCounterShape)t.Commit(20, 20)!;
        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
    }

    [Fact]
    public void Reset_restarts_counter_at_one()
    {
        var t = new StepCounterTool();
        t.Begin(0, 0, ShapeColor.Red, ShapeColor.Transparent, 2);
        t.Commit(0, 0);
        t.Reset();
        t.Begin(0, 0, ShapeColor.Red, ShapeColor.Transparent, 2);
        var s = (StepCounterShape)t.Commit(0, 0)!;
        Assert.Equal(1, s.Number);
    }

    [Fact]
    public void Update_during_drag_repositions_preview()
    {
        var t = new StepCounterTool();
        t.Begin(0, 0, ShapeColor.Red, ShapeColor.Transparent, 2);
        t.Update(50, 60);
        var preview = (StepCounterShape)t.PreviewShape!;
        Assert.Equal(50, preview.CenterX);
        Assert.Equal(60, preview.CenterY);
    }
}
