using Xunit;

namespace AresToys.Hotkeys.Tests;

public class HotkeyDefinitionTests
{
    [Fact]
    public void IsValid_ReturnsTrue_ForNormalDefinition()
    {
        var def = new HotkeyDefinition("popup", HotkeyModifiers.Win, VirtualKey: 0x56);
        Assert.True(def.IsValid());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsValid_ReturnsFalse_ForEmptyOrWhitespaceId(string? id)
    {
        var def = new HotkeyDefinition(id!, HotkeyModifiers.Win, 0x56);
        Assert.False(def.IsValid());
    }

    [Fact]
    public void IsValid_ReturnsFalse_ForZeroVirtualKey()
    {
        var def = new HotkeyDefinition("popup", HotkeyModifiers.Win, VirtualKey: 0);
        Assert.False(def.IsValid());
    }
}
