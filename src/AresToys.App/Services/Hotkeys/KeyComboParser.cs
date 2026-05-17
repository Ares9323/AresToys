using AresToys.Hotkeys;

namespace AresToys.App.Services.Hotkeys;

/// <summary>Reverse of <see cref="HotkeyDisplay"/>: parses a user-readable combo string
/// (<c>"Ctrl + Shift + T"</c>, <c>"F12"</c>, <c>"Win + R"</c>) back into a (modifiers, vk) pair
/// so <c>PressKeyTask</c> can dispatch it via SendInput. Accepts both <c>"+"</c> and <c>" + "</c>
/// separators, case-insensitive modifier names, and the key-name vocabulary
/// <see cref="HotkeyDisplay"/> produces (single letter / digit, F1–F24, Num 0–9, named special
/// keys, oem punctuation). Returns null for empty / unparseable input so callers can skip
/// silently — workflow steps shouldn't crash on a malformed config.</summary>
public static class KeyComboParser
{
    public static (HotkeyModifiers Modifiers, uint VirtualKey)? Parse(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;

        var modifiers = HotkeyModifiers.None;
        uint vk = 0;

        // HotkeyDisplay outputs " + " (with spaces); we also accept the no-space form a user
        // might hand-type. Split eats both. Trim each token to be defensive.
        var tokens = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var raw in tokens)
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;

            var asMod = TryParseModifier(token);
            if (asMod is { } m) { modifiers |= m; continue; }

            // Only one action key per combo — last one wins if the string is malformed.
            var asKey = TryParseKey(token);
            if (asKey is { } k) vk = k;
        }

        if (vk == 0 && modifiers == HotkeyModifiers.None) return null;
        return (modifiers, vk);
    }

    private static HotkeyModifiers? TryParseModifier(string token) => token.ToLowerInvariant() switch
    {
        "ctrl" or "control" => HotkeyModifiers.Control,
        "shift" => HotkeyModifiers.Shift,
        "alt" or "menu" => HotkeyModifiers.Alt,
        "win" or "windows" or "super" or "meta" or "cmd" => HotkeyModifiers.Win,
        _ => null,
    };

    /// <summary>VK lookup mirroring <see cref="HotkeyDisplay.KeyName"/> in reverse. Anything not
    /// in this table or the structured ranges (A–Z / 0–9 / F1–F24 / Num 0–9) returns null so the
    /// outer parser knows the token wasn't an action key and can skip it.</summary>
    private static uint? TryParseKey(string token)
    {
        // Single ASCII letter / digit — direct VK code (A=0x41, 0=0x30).
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c >= '0' && c <= '9') return c;
            if (c >= 'A' && c <= 'Z') return c;
        }

        // Function keys F1–F24 → VK_F1 (0x70) + (n-1).
        if (token.Length >= 2 && (token[0] == 'F' || token[0] == 'f'))
        {
            if (int.TryParse(token[1..], out var n) && n >= 1 && n <= 24)
                return (uint)(0x70 + n - 1);
        }

        // Numpad digit "Num N" — HotkeyDisplay formats VK_NUMPAD0..9 as "Num 0".."Num 9".
        if (token.StartsWith("Num ", StringComparison.OrdinalIgnoreCase) && token.Length == 5)
        {
            var c = token[4];
            if (c >= '0' && c <= '9') return (uint)(0x60 + (c - '0'));
        }

        // Numpad operator keys — match HotkeyDisplay's labels.
        var op = token switch
        {
            "Num *" => 0x6A,
            "Num +" => 0x6B,
            "Num Separator" => 0x6C,
            "Num -" => 0x6D,
            "Num ." => 0x6E,
            "Num /" => 0x6F,
            _ => 0,
        };
        if (op != 0) return (uint)op;

        // Named special keys + OEM punctuation. Strict case-insensitive comparison so users can
        // type "enter" / "ENTER" / "Enter" equivalently.
        return token.ToLowerInvariant() switch
        {
            "backspace" => 0x08,
            "tab" => 0x09,
            "enter" or "return" or "newline" => 0x0D,
            "capslock" => 0x14,
            "esc" or "escape" => 0x1B,
            "space" or "spacebar" => 0x20,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "end" => 0x23,
            "home" => 0x24,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "printscreen" or "prtsc" => 0x2C,
            "insert" or "ins" => 0x2D,
            "delete" or "del" => 0x2E,
            "pause" or "break" => 0x13,
            "menu" or "apps" or "contextmenu" => 0x5D,
            "numlock" => 0x90,
            "scrolllock" => 0x91,
            ";" => 0xBA,
            "=" => 0xBB,
            "," => 0xBC,
            "-" => 0xBD,
            "." => 0xBE,
            "/" => 0xBF,
            "`" => 0xC0,
            "[" => 0xDB,
            "\\" => 0xDC,
            "]" => 0xDD,
            "'" => 0xDE,
            "< / >" or "oem102" => 0xE2,
            _ => TryParseHexVk(token),
        };
    }

    /// <summary>Fallback for unknown keys: HotkeyDisplay emits <c>"VK 0xNN"</c> for codes it
    /// doesn't have a friendly name for, so we round-trip that format. Lets the user persist
    /// any key — including media / browser / hardware-vendor virtual codes — through the combo
    /// string without the parser silently dropping it.</summary>
    private static uint? TryParseHexVk(string token)
    {
        // Accept "VK 0xNN" / "VK0xNN" / "0xNN" / bare hex.
        var t = token.Trim();
        if (t.StartsWith("VK ", StringComparison.OrdinalIgnoreCase)) t = t[3..].TrimStart();
        if (t.StartsWith("VK", StringComparison.OrdinalIgnoreCase)) t = t[2..].TrimStart();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return uint.TryParse(t, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 && v <= 0xFF
            ? v
            : null;
    }
}
