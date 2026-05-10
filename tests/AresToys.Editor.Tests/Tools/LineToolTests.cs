using AresToys.Editor.Model;
using AresToys.Editor.Tools;
using Xunit;

namespace AresToys.Editor.Tests.Tools;

public class LineToolTests
{
    [Fact]
    public void Commit_AfterDrag_ReturnsLineShape()
    {
        var tool = new LineTool();
        tool.Begin(10, 10, ShapeColor.Red, ShapeColor.Transparent, 2);
        tool.Update(50, 60);
        var shape = tool.Commit(50, 60);

        var line = Assert.IsType<LineShape>(shape);
        Assert.Equal(10, line.FromX);
        Assert.Equal(60, line.ToY);
        // Default LineTool has no caps — produces a plain line.
        Assert.False(line.StartCap);
        Assert.False(line.EndCap);
    }

    [Fact]
    public void Commit_TooClose_ReturnsNull()
    {
        var tool = new LineTool();
        tool.Begin(10, 10, ShapeColor.Red, ShapeColor.Transparent, 2);
        Assert.Null(tool.Commit(11, 11));
    }
}
