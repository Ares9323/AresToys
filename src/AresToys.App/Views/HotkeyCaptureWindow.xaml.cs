using System.Runtime.InteropServices;
using System.Windows;
using AresToys.App.Services.Hotkeys;
using AresToys.Hotkeys;

namespace AresToys.App.Views;

public partial class HotkeyCaptureWindow : Wpf.Ui.Controls.FluentWindow
{
    // Hook-tracked modifier state. We can't trust GetAsyncKeyState here because we suppress
    // (return 1 from) every keystroke while the dialog is open — the OS skips its kernel-level
    // key-state update for suppressed events, so by the time the action key arrives Windows
    // thinks Win/Shift are released. Same approach PowerToys takes: track our own state based
    // on the hook events themselves.
    private HotkeyModifiers _liveModifiers;

    private IntPtr _hookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _mouseProc;
    private readonly HashSet<uint> _capturedMouseUps = [];

    public HotkeyCaptureWindow(bool canReset = false)
    {
        InitializeComponent();
        // Reset is meaningful only for built-in workflows (which have a seeded default to
        // restore); for custom workflows there's no default so we hide the button.
        if (!canReset) ResetButton.Visibility = System.Windows.Visibility.Collapsed;

        // Install a low-level keyboard hook while recording so we see (and suppress) every key,
        // including OS-reserved combos like Win+Shift+S. WPF's PreviewKeyDown alone wouldn't
        // catch those — the shell handles them at a layer below WPF's input system, so by the
        // time the WPF input thread sees Win+Shift+S the Snipping Tool overlay is already up.
        Loaded += (_, _) => InstallHook();
        Closed += (_, _) => UninstallHook();
        CancelButton.Click += (_, _) => { DialogResult = false; Close(); };
        PickButton.Click += (_, _) => OnPickFromList();
        ClearButton.Click += (_, _) =>
        {
            ClearRequested = true;
            CapturedModifiers = HotkeyModifiers.None;
            CapturedVirtualKey = 0;
            DialogResult = true;
            Close();
        };
        ResetButton.Click += (_, _) =>
        {
            ResetRequested = true;
            // We don't have the default values here — caller is the source of truth and runs
            // ResetAsync against the seeded profile data. We just signal "reset" via the flag.
            DialogResult = true;
            Close();
        };
    }

    public HotkeyModifiers CapturedModifiers { get; private set; }
    public uint CapturedVirtualKey { get; private set; }

    /// <summary>True when the user clicked "Clear binding". Caller treats this as
    /// <c>ClearAsync</c> rather than <c>UpdateAsync</c>.</summary>
    public bool ClearRequested { get; private set; }

    /// <summary>True when the user clicked "Reset to default". Caller runs <c>ResetAsync</c>.</summary>
    public bool ResetRequested { get; private set; }

    private void InstallHook()
    {
        _hookProc = HookProc;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null!), 0);
        _mouseProc = MouseHookProc;
        _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null!), 0);
    }

    private void UninstallHook()
    {
        if (_mouseHookHandle != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
            _mouseProc = null;
        }
        if (_hookHandle == IntPtr.Zero) return;
        _ = UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _hookProc = null;
    }

    // Right, side and middle buttons are taken as the key. Left stays a normal click (the dialog's own buttons
    // need it) unless a modifier is held, so Ctrl+Left Click can be recorded.
    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        if ((data.flags & LLMHF_INJECTED) != 0)
            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);

        var msg = (int)wParam.ToInt64();
        uint vk;
        bool isDown;
        switch (msg)
        {
            case WM_LBUTTONDOWN: vk = 0x01; isDown = true; break;
            case WM_LBUTTONUP:   vk = 0x01; isDown = false; break;
            case WM_RBUTTONDOWN: vk = 0x02; isDown = true; break;
            case WM_RBUTTONUP:   vk = 0x02; isDown = false; break;
            case WM_MBUTTONDOWN: vk = 0x04; isDown = true; break;
            case WM_MBUTTONUP:   vk = 0x04; isDown = false; break;
            case WM_XBUTTONDOWN:
            case WM_XBUTTONUP:
                vk = (data.mouseData >> 16) == 2 ? 0x06u : 0x05u;
                isDown = msg == WM_XBUTTONDOWN;
                break;
            default:
                return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        var plain = vk == 0x01 && _liveModifiers == HotkeyModifiers.None;
        if (plain && !_capturedMouseUps.Contains(vk))
            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);

        if (!isDown)
        {
            // The release of a captured press is swallowed; a stray release of anything else passes.
            return _capturedMouseUps.Remove(vk) ? (IntPtr)1 : CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        _capturedMouseUps.Add(vk);
        var modifiers = _liveModifiers;
        Dispatcher.BeginInvoke(() =>
        {
            CapturedModifiers = modifiers;
            CapturedVirtualKey = vk;
            DialogResult = true;
            Close();
        });
        return (IntPtr)1;
    }

    // Keys a keyboard may not have (F13-F24, media) and mouse buttons come from the list. The capture hook would
    // swallow the typing in its search box, so it is lifted while the picker is open.
    private void OnPickFromList()
    {
        UninstallHook();
        var picker = new KeyPickerWindow(null) { Owner = this };
        if (picker.ShowDialog() == true && picker.VirtualKey != 0)
        {
            CapturedModifiers = picker.Modifiers;
            CapturedVirtualKey = picker.VirtualKey;
            DialogResult = true;
            Close();
            return;
        }
        _liveModifiers = HotkeyModifiers.None;
        UpdatePreview();
        InstallHook();
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((data.flags & LLKHF_INJECTED) != 0)
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var msg = wParam.ToInt64();
        var isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        var isUp   = msg == WM_KEYUP   || msg == WM_SYSKEYUP;
        if (!isDown && !isUp) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var vk = data.vkCode;
        var modifierFlag = ModifierFor(vk);

        if (modifierFlag is { } flag)
        {
            // Update tracked state.
            if (isDown) _liveModifiers |= flag;
            else        _liveModifiers &= ~flag;
            Dispatcher.BeginInvoke(UpdatePreview);
            return (IntPtr)1; // suppress so Windows shell never sees the modifier sequence
        }

        if (isDown)
        {
            if (vk == VK_ESCAPE && _liveModifiers == HotkeyModifiers.None)
            {
                Dispatcher.BeginInvoke(() => { DialogResult = false; Close(); });
                return (IntPtr)1;
            }

            // Real key press → freeze modifiers + close.
            var modifiers = _liveModifiers;
            Dispatcher.BeginInvoke(() =>
            {
                CapturedModifiers = modifiers;
                CapturedVirtualKey = vk;
                DialogResult = true;
                Close();
            });
            return (IntPtr)1;
        }

        // Non-modifier keyup — swallow too so it doesn't leak after we close.
        return (IntPtr)1;
    }

    private static HotkeyModifiers? ModifierFor(uint vk) => vk switch
    {
        VK_SHIFT or VK_LSHIFT or VK_RSHIFT     => HotkeyModifiers.Shift,
        VK_CONTROL or VK_LCONTROL or VK_RCONTROL => HotkeyModifiers.Control,
        VK_MENU or VK_LMENU or VK_RMENU        => HotkeyModifiers.Alt,
        VK_LWIN or VK_RWIN                     => HotkeyModifiers.Win,
        _ => null,
    };

    private void UpdatePreview()
    {
        ComboPreview.Text = _liveModifiers == HotkeyModifiers.None
            ? "(waiting for keys…)"
            : HotkeyDisplay.Format(_liveModifiers, 0).Replace(" + VK 0x00", "…", StringComparison.Ordinal);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;
    private const uint LLMHF_INJECTED = 0x00000001;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x00000010;

    private const uint VK_ESCAPE = 0x1B;
    private const uint VK_SHIFT = 0x10;
    private const uint VK_LSHIFT = 0xA0;
    private const uint VK_RSHIFT = 0xA1;
    private const uint VK_CONTROL = 0x11;
    private const uint VK_LCONTROL = 0xA2;
    private const uint VK_RCONTROL = 0xA3;
    private const uint VK_MENU = 0x12;
    private const uint VK_LMENU = 0xA4;
    private const uint VK_RMENU = 0xA5;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);
}
