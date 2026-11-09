using AresToys.Hotkeys.Native;

namespace AresToys.Hotkeys;

public sealed class Win32HotkeyRegistrar : IHotkeyRegistrar
{
    public bool RegisterHotKey(IntPtr hwnd, int id, HotkeyModifiers modifiers, uint virtualKey)
        => HotkeyNativeMethods.RegisterHotKey(hwnd, id, (uint)modifiers, virtualKey);

    public bool UnregisterHotKey(IntPtr hwnd, int id)
        => HotkeyNativeMethods.UnregisterHotKey(hwnd, id);
}
