namespace AresToys.Hotkeys;

/// <summary>Single key transition observed by a <see cref="KeyboardHook"/> stream listener.
/// Stream listeners are pure observers — they cannot suppress events; suppression remains
/// the exclusive concern of atomic bindings registered via <see cref="KeyboardHook.Register"/>.</summary>
/// <param name="VkCode">Win32 virtual-key code (see Microsoft.Win32.WinUser.VK_*).</param>
/// <param name="PrintableChar">Character produced by the key under the current keyboard layout
/// and modifier state, or <c>null</c> for non-character keys (function keys, modifiers, arrows,
/// nav keys, etc.) and dead-key states.</param>
/// <param name="IsDown"><c>true</c> for WM_KEYDOWN/WM_SYSKEYDOWN, <c>false</c> for WM_KEYUP/WM_SYSKEYUP.</param>
/// <param name="Modifiers">Modifier state at the moment of the event (Control/Alt/Shift/Win).</param>
public sealed record KeyEvent(uint VkCode, char? PrintableChar, bool IsDown, HotkeyModifiers Modifiers);
