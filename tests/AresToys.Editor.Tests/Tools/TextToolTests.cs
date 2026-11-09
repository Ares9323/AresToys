using AresToys.Editor.Model;
using AresToys.Editor.Tools;
using Xunit;

namespace AresToys.Editor.Tests.Tools;

public class TextToolTests
{
    [Fact]
    public void Begin_sets_preview_with_empty_text()
    {
        var t = new TextTool(TextStyle.Default);
        t.Begin(10, 20, ShapeColor.Red, ShapeColor.Transparent, 1);
        Assert.NotNull(t.PreviewShape);
        var shape = Assert.IsType<TextShape>(t.PreviewShape);
        Assert.Equal("", shape.Text);
        Assert.Equal(10, shape.X);
        Assert.Equal(20, shape.Y);
    }

    [Fact]
    public void Commit_returns_null_when_text_is_empty()
    {
        var t = new TextTool(TextStyle.Default);
        t.Begin(10, 20, ShapeColor.Red, ShapeColor.Transparent, 1);
        Assert.Null(t.Commit(0, 0));
    }
}
