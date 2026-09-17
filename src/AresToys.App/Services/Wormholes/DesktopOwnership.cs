using System.Runtime.InteropServices;

namespace AresToys.App.Services.Wormholes;

/// <summary>Makes a wormhole survive "Show desktop".
///
/// Measured behaviour (Win11 26200, probe against a window built exactly like a wormhole:
/// <c>WindowStyle=None</c>, <c>AllowsTransparency</c>, <c>ShowInTaskbar=False</c>): Show desktop
/// — Win+D, the taskbar's far-corner button, <c>Shell.Application.ToggleDesktop</c> — does NOT
/// minimize or hide these windows. No <c>SC_MINIMIZE</c>, no <c>SWP_HIDEWINDOW</c>, no
/// <c>WM_SIZE</c>/minimized: <c>IsWindowVisible</c> stays true, <c>IsIconic</c> stays false and
/// the rect never changes. All the shell does is raise the desktop (<c>Progman</c>) to the top of
/// the z-order, which BURIES the wormhole. That's why intercepting minimize/hide messages can't
/// work — none of them are ever sent.
///
/// The fix is therefore about z-order: make the wormhole an OWNED window of the desktop. An owned
/// window is always drawn above its owner, so when Progman comes forward the wormhole comes with
/// it. <see cref="GWLP_HWNDPARENT"/> sets the owner, NOT the parent — the window stays top-level
/// and keeps screen coordinates, which is what makes this safe where the old
/// <see cref="DesktopLayerHost"/> <c>SetParent</c> approach was not (that one shifted every
/// wormhole by the virtual-origin delta and had to be disabled).
///
/// Verified side effects: none of the ones that would matter. An owned wormhole still drops
/// behind a normal app that takes the foreground (no implicit topmost), an explicit
/// <c>Topmost=true</c> still wins over that app, and activating the wormhole still brings it
/// forward.</summary>
internal static class DesktopOwnership
{
    /// <summary>Index for <c>SetWindowLongPtr</c> that reads/writes a top-level window's OWNER.
    /// The name is a historical Win32 wart: for a non-child window this slot is the owner.</summary>
    private const int GWLP_HWNDPARENT = -8;

    /// <summary>Handle of the desktop window to own wormholes from, or <see cref="IntPtr.Zero"/>
    /// when the shell isn't in a state we recognise (Explorer restarting, a replacement shell).
    /// Progman is the right target: it's what Show desktop raises.</summary>
    public static IntPtr FindDesktopWindow()
    {
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero) return progman;
        // Some configurations leave a visible top-level WorkerW hosting the desktop instead.
        return FindWindow("WorkerW", null);
    }

    /// <summary>Make <paramref name="hwnd"/> an owned window of the desktop. Returns false when
    /// the desktop can't be located or the call fails — the caller just keeps the previous
    /// (plain top-level) behaviour, which is exactly what the feature toggle turns off anyway.</summary>
    public static bool Attach(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var desktop = FindDesktopWindow();
        if (desktop == IntPtr.Zero) return false;
        if (GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) == desktop) return true; // already owned
        Marshal.SetLastSystemError(0);
        SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, desktop);
        return GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) == desktop;
    }

    /// <summary>Drop the desktop ownership, restoring plain top-level behaviour (the wormhole
    /// then gets buried by Show desktop again). Used when the user turns the setting off.</summary>
    public static bool Detach(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) == IntPtr.Zero) return true;
        SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, IntPtr.Zero);
        return GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) == IntPtr.Zero;
    }

    /// <summary>Broadcast message Explorer sends to every top-level window after the shell
    /// restarts. The old Progman handle dies with the old Explorer, so ownership has to be
    /// re-established against the new one.</summary>
    public static uint TaskbarCreatedMessage { get; } = RegisterWindowMessage("TaskbarCreated");

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);
}
