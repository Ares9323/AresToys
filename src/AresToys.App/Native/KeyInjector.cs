using System.Runtime.InteropServices;

namespace AresToys.App.Native;

/// <summary>
/// Helpers around <c>SendInput</c> for paste / press-key tasks. The non-trivial bit is
/// <see cref="ReleaseStickyModifiers"/>: when a workflow is triggered by a hotkey like
/// <c>Win+Shift+P</c>, the user is still physically holding Win/Shift when our SendInput
/// keystrokes fire — so the OS sees <c>Win+Ctrl+V</c> (which opens the audio mixer in Windows 11)
/// instead of plain <c>Ctrl+V</c>. Injecting key-up events for any modifier currently down
/// neutralizes that race so the target window receives the intended keystroke.
/// </summary>
internal static class KeyInjector
{
    private static readonly ushort[] StickyModifierVks =
    [
        AppNativeMethods.VkLWin, AppNativeMethods.VkRWin,
        AppNativeMethods.VkLShift, AppNativeMethods.VkRShift,
        AppNativeMethods.VkLMenu, AppNativeMethods.VkRMenu,
        AppNativeMethods.VkLControl, AppNativeMethods.VkRControl,
        // Enter isn't a "modifier" strictly, but it triggers IsDefault=True buttons on any WPF /
        // WinForms dialog that wins foreground from AutoPaster (e.g. our own WebpageUrlDialog).
        // If the user paste-by-Enter inside the AresToys popup with that dialog as paste target,
        // the still-held Enter is forwarded to the dialog right after the foreground swap and
        // activates the default button before our Ctrl+V lands. Injecting a synthetic Enter-up
        // here neutralises the race.
        AppNativeMethods.VkReturn,
    ];

    /// <summary>Inject key-up events for any modifier (or Enter) currently physically held. Does
    /// nothing if none are down. Returns the count of keys released so callers can size a
    /// settle delay.</summary>
    public static int ReleaseStickyModifiers()
    {
        var releases = new List<AppNativeMethods.INPUT>(StickyModifierVks.Length);
        foreach (var vk in StickyModifierVks)
        {
            if ((AppNativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0)
                releases.Add(MakeKey(vk, keyUp: true));
        }
        if (releases.Count == 0) return 0;
        var arr = releases.ToArray();
        AppNativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf<AppNativeMethods.INPUT>());
        return releases.Count;
    }

    public static AppNativeMethods.INPUT MakeKey(ushort virtualKey, bool keyUp) => new()
    {
        type = AppNativeMethods.InputKeyboard,
        u = new AppNativeMethods.InputUnion
        {
            ki = new AppNativeMethods.KEYBDINPUT
            {
                wVk = virtualKey,
                dwFlags = keyUp ? AppNativeMethods.KeyEventfKeyUp : 0,
            },
        },
    };
}
