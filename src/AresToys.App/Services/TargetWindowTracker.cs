using System.Diagnostics;
using System.Runtime.InteropServices;
using AresToys.App.Native;

namespace AresToys.App.Services;

public sealed class TargetWindowTracker
{
    private static readonly int OwnProcessId = Environment.ProcessId;
    private IntPtr _captured;

    public void CaptureCurrentForeground()
    {
        var hwnd = AppNativeMethods.GetForegroundWindow();
        if (hwnd != IntPtr.Zero)
        {
            if (!IsOwnProcess(hwnd))
            {
                _captured = hwnd;
                return;
            }
            // Foreground IS AresToys but NOT the clipboard popup itself — the user has an input
            // dialog of ours open (WebpageUrlDialog, HotkeyCaptureWindow, IconPickerDialog, etc.)
            // and the popup was invoked on top of it. They expect paste to land back into that
            // dialog's text box. The only own-process window we deliberately skip is the popup,
            // because pasting into itself would be a no-op / weird.
            if (hwnd != AresToys.App.Views.ClipboardWindow.CurrentHwnd)
            {
                _captured = hwnd;
                return;
            }
        }

        // Foreground is the popup itself (or zero — desktop). Same fallback as before: keep the
        // last valid foreign capture, otherwise scan EnumWindows for the topmost non-AresToys
        // window so paste at least lands somewhere plausible.
        if (_captured != IntPtr.Zero
            && AppNativeMethods.IsWindow(_captured)
            && !IsOwnProcess(_captured))
        {
            Debug.WriteLine($"[TargetWindowTracker] Foreground is the popup itself; keeping previous capture {_captured}.");
            return;
        }

        _captured = FindTopmostNonOwnWindow();
        Debug.WriteLine($"[TargetWindowTracker] Foreground is the popup; EnumWindows fallback found {_captured}.");
    }

    private static bool IsOwnProcess(IntPtr hwnd)
    {
        AppNativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid == OwnProcessId;
    }

    /// <summary>Scan top-level windows in Z-order (foreground → background) and return the
    /// first visible, non-AresToys, non-tool window. Tool windows (WS_EX_TOOLWINDOW) and
    /// no-activate windows are skipped so the popup doesn't try to paste into a system tray
    /// flyout, the Start menu, or our own toast surface. Returns IntPtr.Zero when nothing
    /// suitable is found — AutoPaster then skips the Ctrl+V step.</summary>
    private static IntPtr FindTopmostNonOwnWindow()
    {
        IntPtr best = IntPtr.Zero;
        AppNativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!AppNativeMethods.IsWindowVisible(hwnd)) return true;
            if (IsOwnProcess(hwnd)) return true;
            // Skip tool / no-activate windows — they're never paste targets. GetWindowLongPtr
            // is the x64-safe variant; GetWindowLong is unexported on 64-bit user32.
            var exStyle = (long)AppNativeMethods.GetWindowLongPtr(hwnd, AppNativeMethods.GWL_EXSTYLE);
            if ((exStyle & (AppNativeMethods.WS_EX_TOOLWINDOW | AppNativeMethods.WS_EX_NOACTIVATE)) != 0) return true;
            // Skip windows with no visible title — usually invisible helper surfaces.
            if (AppNativeMethods.GetWindowTextLength(hwnd) == 0) return true;
            best = hwnd;
            return false; // first match in Z-order wins
        }, IntPtr.Zero);
        return best;
    }

    public bool TryRestoreCaptured() => ForceForeground(_captured);

    /// <summary>The captured target HWND (or <see cref="IntPtr.Zero"/> when nothing's been
    /// captured yet). Exposed so AutoPaster can decide between SendInput Ctrl+V (cross-process)
    /// and a WPF programmatic paste (own-process) — the latter avoids a flaky foreground swap
    /// + key-injection round-trip when both windows live in our process.</summary>
    public IntPtr CapturedHwnd => _captured;

    /// <summary>Whether the captured HWND belongs to our own process. Mirrors the same check
    /// the capture loop uses internally.</summary>
    public bool CapturedIsOwnProcess => _captured != IntPtr.Zero && IsOwnProcess(_captured);

    /// <summary>Force any window to the foreground, bypassing Win32's anti-focus-stealing
    /// rules via the AttachThreadInput + alt-tap workaround. Used both to send focus TO the
    /// captured paste target (TryRestoreCaptured) and to bring focus BACK to the clipboard
    /// popup after a paste in pinned mode — the popup needs keyboard focus so the next
    /// Enter / Ctrl+digit lands on it instead of the target app.
    ///
    /// Returns false when the target hwnd is zero or its thread can't be queried; otherwise
    /// returns the result of SetForegroundWindow (which may still fail in heavily locked-down
    /// foreground states, but the alt-tap nudge handles the common case).</summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        // Win32 SetForegroundWindow has anti-focus-stealing rules: only the foreground process
        // (or one that just got input) can call it successfully. Two tricks combined make this reliable:
        //   1. AttachThreadInput between us and the target's thread to share the foreground state.
        //   2. Send a fake Alt key press to "unlock" the foreground assignment.
        var thisThread = AppNativeMethods.GetCurrentThreadId();
        var targetThread = AppNativeMethods.GetWindowThreadProcessId(hwnd, out _);

        if (targetThread == 0) return false;
        if (thisThread != targetThread)
        {
            AppNativeMethods.AttachThreadInput(thisThread, targetThread, true);
        }

        SendAltTap();
        var ok = AppNativeMethods.SetForegroundWindow(hwnd);

        if (thisThread != targetThread)
        {
            AppNativeMethods.AttachThreadInput(thisThread, targetThread, false);
        }
        return ok;
    }

    private static void SendAltTap()
    {
        var inputs = new AppNativeMethods.INPUT[2];
        inputs[0] = new AppNativeMethods.INPUT
        {
            type = AppNativeMethods.InputKeyboard,
            u = new AppNativeMethods.InputUnion { ki = new AppNativeMethods.KEYBDINPUT { wVk = AppNativeMethods.VkMenu } }
        };
        inputs[1] = new AppNativeMethods.INPUT
        {
            type = AppNativeMethods.InputKeyboard,
            u = new AppNativeMethods.InputUnion { ki = new AppNativeMethods.KEYBDINPUT { wVk = AppNativeMethods.VkMenu, dwFlags = AppNativeMethods.KeyEventfKeyUp } }
        };
        AppNativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<AppNativeMethods.INPUT>());
    }
}
