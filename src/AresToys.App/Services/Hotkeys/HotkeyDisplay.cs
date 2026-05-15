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
            _ => $"VK 0x{vk:X2}",
        };
    }
}
