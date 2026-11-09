using AresToys.Editor.Model;
using AresToys.Editor.Tools;
using Xunit;

namespace AresToys.Editor.Tests.Tools;

public class FreehandToolTests
{
    [Fact]
    public void Commit_AccumulatesAllPoints()
    {
        var tool = new FreehandTool();
        tool.Begin(0, 0, ShapeColor.Red, ShapeColor.Transparent, 2);
        tool.Update(5, 5);
        tool.Update(10, 10);
        tool.Update(15, 5);
        var shape = tool.Commit(20, 0);

        var freehand = Assert.IsType<FreehandShape>(shape);
        Assert.True(freehand.Points.Count >= 4);
    }

    [Fact]
    public void Commit_OnlyOnePoint_ReturnsNull()
    {
        var tool = new FreehandTool();
        tool.Begin(10, 10, ShapeColor.Red, ShapeColor.Transparent, 2);
        Assert.Null(tool.Commit(10, 10));
    }
}
