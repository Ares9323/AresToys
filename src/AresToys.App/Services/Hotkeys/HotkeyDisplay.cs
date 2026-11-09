using System.Text;
using AresToys.Hotkeys;

namespace AresToys.App.Services.Hotkeys;

/// <summary>Maps Win32 virtual-key codes + modifier flags to a user-readable string like
/// <c>"Ctrl + Alt + R"</c>. Used by the Settings → Hotkeys list and the rebind dialog.</summary>
public static class HotkeyDisplay
{
    public static string Format(HotkeyModifiers modifiers, uint virtualKey)
    {
        if (modifiers == HotkeyModifiers.None && virtualKey == 0) return "Click to set hotkey…";
        var sb = new StringBuilder();
        if ((modifiers & HotkeyModifiers.Control) != 0) Append(sb, "Ctrl");
        if ((modifiers & HotkeyModifiers.Alt) != 0) Append(sb, "Alt");
        if ((modifiers & HotkeyModifiers.Shift) != 0) Append(sb, "Shift");
        if ((modifiers & HotkeyModifiers.Win) != 0) Append(sb, "Win");
        Append(sb, KeyName(virtualKey));
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string token)
    {
        if (sb.Length > 0) sb.Append(" + ");
        sb.Append(token);
    }

    private static string KeyName(uint vk)
    {
        // ASCII letter / digit ranges produce sensible glyphs directly.
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();          // '0'-'9'
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();          // 'A'-'Z'
        if (vk >= 0x60 && vk <= 0x69) return $"Num {vk - 0x60}";              // VK_NUMPAD0..9
        if (vk >= 0x70 && vk <= 0x87) return $"F{vk - 0x6F}";                 // F1-F24
        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2C => "PrintScreen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x13 => "Pause",
            // VK_APPS = the "menu" key between Ctrl-right and the right Win key on most
            // keyboards (opens the active control's context menu — same as a right-click).
            0x5D => "Menu",
            // Numpad operator keys: VK_MULTIPLY..VK_DIVIDE = 0x6A..0x6F.
            0x6A => "Num *",
            0x6B => "Num +",
            0x6C => "Num Separator",
            0x6D => "Num -",
            0x6E => "Num .",
            0x6F => "Num /",
            0x90 => "NumLock",
            0x91 => "ScrollLock",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            // VK_OEM_102 — the "102nd key" on ISO / international keyboards (sits between
            // Shift-left and Z on italian, german, etc.). On italian layout it's the `<` key;
            // we label it generically since the same VK is used across many layouts.
            0xE2 => "< / >",
            _ => NamedKey(vk) ?? $"VK 0x{vk:X2}",
        };
    }

    /// <summary>Browser, volume, media and launch keys (VK_BROWSER_BACK..VK_LAUNCH_APP2). Keyboards
    /// send them but most cannot be recorded in a capture dialog, so the key picker lists them by
    /// these names and <see cref="KeyComboParser"/> reads the same names back.</summary>
    public static readonly (uint Vk, string Name)[] MediaKeys =
    [
        (0xA6, "Browser Back"), (0xA7, "Browser Forward"), (0xA8, "Browser Refresh"), (0xA9, "Browser Stop"),
        (0xAA, "Browser Search"), (0xAB, "Browser Favorites"), (0xAC, "Browser Home"),
        (0xAD, "Volume Mute"), (0xAE, "Volume Down"), (0xAF, "Volume Up"),
        (0xB0, "Next Track"), (0xB1, "Previous Track"), (0xB2, "Stop Media"), (0xB3, "Play/Pause"),
        (0xB4, "Launch Mail"), (0xB5, "Select Media"), (0xB6, "Launch App 1"), (0xB7, "Launch App 2"),
    ];

    /// <summary>Mouse buttons, sent as a click at the current pointer position. They sit in the same
    /// (modifiers, vk) slot as a key, using the Win32 VK_LBUTTON..VK_XBUTTON2 codes, so a combo such as
    /// Ctrl + Left Click needs no second format.</summary>
    public static readonly (uint Vk, string Name)[] MouseButtons =
    [
        (0x01, "Left Click"), (0x02, "Right Click"), (0x04, "Middle Click"), (0x05, "Mouse Button 4"), (0x06, "Mouse Button 5"),
    ];

    public static bool IsMouseButton(uint vk) => MouseButtons.Any(b => b.Vk == vk);

    private static string? NamedKey(uint vk)
    {
        foreach (var (key, name) in MediaKeys) if (key == vk) return name;
        foreach (var (key, name) in MouseButtons) if (key == vk) return name;
        return null;
    }
}
