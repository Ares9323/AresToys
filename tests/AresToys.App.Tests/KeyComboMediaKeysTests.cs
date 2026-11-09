using AresToys.App.Services.Hotkeys;
using AresToys.Hotkeys;
using Xunit;

namespace AresToys.App.Tests;

public class KeyComboMediaKeysTests
{
    [Fact]
    public void EveryMediaKey_RoundTripsThroughFormatAndParse()
    {
        foreach (var (vk, _) in HotkeyDisplay.MediaKeys)
        {
            var text = HotkeyDisplay.Format(HotkeyModifiers.Control | HotkeyModifiers.Shift, vk);

            var parsed = KeyComboParser.Parse(text);

            Assert.NotNull(parsed);
            Assert.Equal(vk, parsed!.Value.VirtualKey);
            Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, parsed.Value.Modifiers);
            Assert.DoesNotContain("VK 0x", text);
        }
    }

    [Fact]
    public void F24WithModifiers_RoundTrips()
    {
        var parsed = KeyComboParser.Parse(HotkeyDisplay.Format(HotkeyModifiers.Shift, 0x87));

        Assert.Equal(0x87u, parsed!.Value.VirtualKey);
        Assert.Equal(HotkeyModifiers.Shift, parsed.Value.Modifiers);
    }

    [Fact]
    public void MouseButtons_RoundTripWithModifiers()
    {
        foreach (var (vk, name) in HotkeyDisplay.MouseButtons)
        {
            var text = HotkeyDisplay.Format(HotkeyModifiers.Control, vk);

            var parsed = KeyComboParser.Parse(text);

            Assert.Equal("Ctrl + " + name, text);
            Assert.Equal(vk, parsed!.Value.VirtualKey);
            Assert.True(HotkeyDisplay.IsMouseButton(vk));
        }
    }
}
