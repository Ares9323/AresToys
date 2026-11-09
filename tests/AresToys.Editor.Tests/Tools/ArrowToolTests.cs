using AresToys.Editor.Model;
using AresToys.Editor.Tools;
using Xunit;

namespace AresToys.Editor.Tests.Tools;

/// <summary>"Arrow tool" tests now exercise <see cref="LineTool"/> — Arrow and Line are the
/// same internal tool, the only difference is the cap defaults the toolbar buttons seed.
/// Kept under this filename so the legacy intent ("there should be tests for the arrow tool")
/// stays discoverable.</summary>
public class ArrowToolTests
{
    [Fact]
    public void Commit_AfterDrag_WithEndCap_ReturnsLineShapeWithEndCap()
    {
        var tool = new LineTool { EndCap = true };
        tool.Begin(10, 10, ShapeColor.Red, ShapeColor.Transparent, 2);
        tool.Update(50, 60);
        var shape = tool.Commit(50, 60);

        var line = Assert.IsType<LineShape>(shape);
        Assert.Equal(10, line.FromX);
        Assert.Equal(50, line.ToX);
        Assert.True(line.EndCap);
        Assert.False(line.StartCap);
    }

    [Fact]
    public void Commit_TooClose_ReturnsNull()
    {
        var tool = new LineTool { EndCap = true };
        tool.Begin(10, 10, ShapeColor.Red, ShapeColor.Transparent, 2);
        Assert.Null(tool.Commit(11, 11));
    }

    [Fact]
    public void Commit_BothCaps_PreservedOnShape()
    {
        var tool = new LineTool { StartCap = true, EndCap = true, TipStyle = LineTipStyle.FilledTriangle };
        tool.Begin(0, 0, ShapeColor.Red, ShapeColor.Transparent, 3);
        tool.Update(80, 0);
        var shape = tool.Commit(80, 0);

        var line = Assert.IsType<LineShape>(shape);
        Assert.True(line.StartCap);
        Assert.True(line.EndCap);
        Assert.Equal(LineTipStyle.FilledTriangle, line.TipStyle);
    }
}
