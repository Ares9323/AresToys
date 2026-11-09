using System.Runtime.InteropServices;

namespace AresToys.App.Services.Launcher;

/// <summary>Translates a stored launcher key (US-canonical: "Q", ";", ",", ".", "/") into the
/// printable glyph the user's current keyboard layout produces for that physical key. On a
/// US keyboard ";" stays ";". On an Italian keyboard the same physical key (right of L)
/// produces "ò", and the slash position produces "-" — so the cell label should show "ò"
/// and "-" to match what's printed on the keycaps. Storage stays canonical so KeyDown
/// matching keeps working across layouts (Key.OemSemicolon always means "the right-of-L
/// key" regardless of what character that key emits).</summary>
public static partial class KeyboardLayoutMapper
{
    private const uint MAPVK_VK_TO_CHAR = 2;
    private const uint MAPVK_VSC_TO_VK_EX = 3;

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

    /// <summary>Resolve the display glyph for a stored key. Multi-char inputs (e.g. "F1") are
    /// returned unchanged — the function-row labels are layout-independent.
    /// <para>The pipeline is scancode → VK → char rather than just VK → char: VK_OEM_*
    /// values are not stable across layouts (an Italian layout may bind VK_OEM_1 to a
    /// different physical key than the US "right-of-L"), but the hardware scancode is. We
    /// map our canonical US storage char to its US-physical-position scancode and ask the
    /// active layout what character that position produces.</para></summary>
    public static string GetDisplayChar(string storageKey)
    {
        if (string.IsNullOrEmpty(storageKey) || storageKey.Length != 1) return storageKey;
        var scancode = StorageKeyToScancode(storageKey[0]);
        if (scancode == 0) return storageKey;

        // GetKeyboardLayout(0) returns the active keyboard layout for the calling thread —
        // independent of UI display language. Same HKL Alt+Shift / Win+Space toggle between.
        var hkl = GetKeyboardLayout(0);

        // VSC → VK with the active layout: a fixed scancode (e.g. 0x27 = "right-of-L slot")
        // resolves to whichever virtual-key the layout assigns to that physical position.
        var vk = MapVirtualKeyEx(scancode, MAPVK_VSC_TO_VK_EX, hkl);
        if (vk == 0) return storageKey;

        var ch = MapVirtualKeyEx(vk, MAPVK_VK_TO_CHAR, hkl);
        if (ch == 0) return storageKey;
        // Strip the dead-key high bit (set when the layout returns a combining char).
        var lo = (char)(ch & 0xFFFF);
        if (char.IsControl(lo)) return storageKey;
        return char.ToUpperInvariant(lo).ToString();
    }

    /// <summary>Map our canonical storage chars to their US-keyboard physical scancodes. The
    /// scancode tells Windows which physical key on the keyboard the user means; the active
    /// layout then decides what character that key emits. So a stored ";" (US right-of-L)
    /// becomes "ò" on Italian, and "/" (US right-of-period) becomes "-" — same physical
    /// position, layout-specific glyph.</summary>
    private static uint StorageKeyToScancode(char c) => c switch
    {
        'Q' => 0x10, 'W' => 0x11, 'E' => 0x12, 'R' => 0x13, 'T' => 0x14,
        'Y' => 0x15, 'U' => 0x16, 'I' => 0x17, 'O' => 0x18, 'P' => 0x19,
        'A' => 0x1E, 'S' => 0x1F, 'D' => 0x20, 'F' => 0x21, 'G' => 0x22,
        'H' => 0x23, 'J' => 0x24, 'K' => 0x25, 'L' => 0x26, ';' => 0x27,
        'Z' => 0x2C, 'X' => 0x2D, 'C' => 0x2E, 'V' => 0x2F, 'B' => 0x30,
        'N' => 0x31, 'M' => 0x32, ',' => 0x33, '.' => 0x34, '/' => 0x35,
        _   => 0,
    };
}
