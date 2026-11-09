using AresToys.Editor.Model;
using Xunit;

namespace AresToys.Editor.Tests.Model;

public class TextShapeTests
{
    [Fact]
    public void TextShape_carries_position_and_text_and_style()
    {
        var t = new TextShape(10, 20, 200, 40, "Hello", TextStyle.Default, ShapeColor.Red, ShapeColor.Transparent, 1);
        Assert.Equal(10, t.X);
        Assert.Equal(20, t.Y);
        Assert.Equal("Hello", t.Text);
        Assert.Equal(TextStyle.Default.FontFamily, t.Style.FontFamily);
    }

    [Fact]
    public void IsEmpty_when_text_is_blank()
    {
        var t = new TextShape(0, 0, 200, 40, "   ", TextStyle.Default, ShapeColor.Red, ShapeColor.Transparent, 1);
        Assert.True(t.IsEmpty);
    }

    [Fact]
    public void IsEmpty_false_when_text_present()
    {
        var t = new TextShape(0, 0, 200, 40, "Hi", TextStyle.Default, ShapeColor.Red, ShapeColor.Transparent, 1);
        Assert.False(t.IsEmpty);
    }
}
