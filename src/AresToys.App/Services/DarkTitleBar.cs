using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AresToys.App.Services;

/// <summary>Win32 helper to flip a WPF window's titlebar to immersive-dark mode (Win10 1809+,
/// Win11). Used everywhere we have a system-drawn titlebar that we want to match the dark
/// chrome of the rest of the app — without this every dialog flashes a white titlebar that
/// breaks the dark theme. No-op on older Windows builds.</summary>
public static partial class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;            // build 19041+
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19; // build 18985–19040

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>Wires the dark-titlebar attribute to fire as soon as the window's HWND exists.
    /// Safe to call from a constructor — the SourceInitialized event is the right hook to set
    /// DWM attributes before the chrome paints, so the user never sees a white flash.</summary>
    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int useDark = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDark, sizeof(int));
            }
        };
    }

    /// <summary>Request rounded corners from the desktop window manager. Win11 (build 22000+)
    /// honours DWMWA_WINDOW_CORNER_PREFERENCE = ROUND; older Windows ignore the call. Lets us
    /// have rounded chrome without giving up <c>AllowsTransparency=False</c> (which is what a
    /// pure WPF Border CornerRadius would require). Safe to call on any Window.</summary>
    public static void ApplyRoundedCorners(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int rounded = DWMWCP_ROUND;
            // Return value < 0 means the attribute isn't supported on this Windows version —
            // ignore (no rounded corners on Win10, but the window still works).
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref rounded, sizeof(int));
        };
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;   // Win11 build 22000+
    private const int DWMWCP_ROUND = 2;                      // round on a small radius

    /// <summary>Suppress the white flash WPF Windows show during a DWM resize. Three layers
    /// of fix because the flash has two sources:
    ///   1. <c>WM_ERASEBKGND</c> — Win32 paints the window class brush (default white) before
    ///      WPF gets a chance to repaint. Returning 1 (handled) skips the erase pass.
    ///   2. The window class background brush — replaced with a solid dark brush via
    ///      SetClassLongPtr, so even if WM_ERASEBKGND does fire (some WPF-UI internals call
    ///      DefWindowProc which re-runs the default erase) the colour matches Surface1.
    ///   3. DWM-revealed area during resize — DWM expands its frame buffer with the caption /
    ///      border colour. DWMWA_BORDER_COLOR + DWMWA_CAPTION_COLOR set to dark make that area
    ///      blend into the rest of the window instead of flashing white.
    /// ClipboardWindow / LauncherWindow don't need this because <c>AllowsTransparency=True</c>
    /// bypasses the Win32 + DWM erase pipelines entirely. Apply to every FluentWindow
    /// (MainWindow, ImageEffectsWindow, GradientEditorWindow, QrGeneratorWindow, …) — anything
    /// with native DWM chrome.</summary>
    public static void SuppressResizeFlicker(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Layer 1 — message hook that short-circuits WM_ERASEBKGND.
            var src = HwndSource.FromHwnd(hwnd);
            src?.AddHook(WndProc);

            // Layer 2 — replace the window class brush so the rare unhooked erase doesn't
            // flash white. RGB colour stored as 0x00BBGGRR (Win32 COLORREF order). #1E1E1E
            // matches the dark surface roughly; close enough that one frame of mismatch is
            // imperceptible.
            try
            {
                var darkBrush = CreateSolidBrush(0x001E1E1E);
                _ = SetClassLongPtr(hwnd, GCLP_HBRBACKGROUND, darkBrush);
            }
            catch { /* GDI32 missing on bare server SKUs — no-op */ }

            // Layer 3 — DWM caption + border colour. Build 22000+ (Win11) honours these; older
            // Windows ignores them silently.
            int colorRef = 0x001E1E1E;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
        };

        static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_ERASEBKGND = 0x0014;
            if (msg == WM_ERASEBKGND)
            {
                handled = true;
                return new IntPtr(1);
            }
            return IntPtr.Zero;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static partial IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateSolidBrush(uint color);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int GCLP_HBRBACKGROUND = -10;
    private const int DWMWA_CAPTION_COLOR = 35;   // Win11 build 22000+
    private const int DWMWA_BORDER_COLOR = 34;    // Win11 build 22000+

    private const int WM_NCHITTEST = 0x0084;
    // Hit-test return codes — see https://learn.microsoft.com/windows/win32/inputdev/wm-nchittest
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;

    /// <summary>Widen the resize hit-test zones along the bottom + left + right edges and the
    /// two bottom corners. Default Windows resize border is 4–8 logical pixels which is hard
    /// to grab — particularly the corners, where the diagonal-resize cursor only appears in a
    /// tiny diamond. We hook WM_NCHITTEST and re-classify the outer <paramref name="cornerSize"/>
    /// pixels as resize zones, so the user gets the diagonal cursor across a much larger
    /// catchment area.
    ///
    /// Top corners are deliberately left at the system default — the FluentWindow titlebar
    /// puts the close / minimise / maximise / icon buttons there, and overriding HTCLOSE /
    /// HTMINBUTTON / HTMAXBUTTON / HTSYSMENU with a resize hit-test breaks them. Side and
    /// bottom edges have no titlebar competition, so we widen those freely.
    ///
    /// No-op while maximised (resize is disabled by Windows in that state) and on
    /// non-resizable windows. Works with WPF-UI FluentWindow + standard <see cref="Window"/>.</summary>
    public static void EnlargeResizeHitZones(Window window, int cornerSize = 12)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var src = HwndSource.FromHwnd(hwnd);
            src?.AddHook(MakeNcHitHook(window, cornerSize));
        };
    }

    private static HwndSourceHook MakeNcHitHook(Window window, int cornerSize) =>
        (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != WM_NCHITTEST) return IntPtr.Zero;
            // Skip when the user can't resize anyway. Maximised windows already disable resize
            // at the OS level so the hit-test override would just confuse the cursor.
            if (window.WindowState != WindowState.Normal) return IntPtr.Zero;
            if (window.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize) return IntPtr.Zero;

            // lParam packs cursor screen coords as low/high 16-bit words. Cast through int
            // so negative coords (multi-monitor with a primary on the right) survive sign-extend.
            var raw = lParam.ToInt32();
            int x = (short)(raw & 0xFFFF);
            int y = (short)((raw >> 16) & 0xFFFF);
            if (!GetWindowRect(hwnd, out var rect)) return IntPtr.Zero;

            // DPI-aware corner size: scale by the window's transform-to-device matrix so
            // 12 logical px is 12 px on a 1× monitor and ~18 px on a 1.5× monitor.
            int corner = ScalePx(window, cornerSize);
            // Side-edge resize is intentionally MUCH narrower than the corners (~4 logical px).
            // A 12-px right edge resize zone overlaps the WPF scrollbar (~17 px wide) and the
            // user cannot grab the thumb anymore — the resize cursor + drag wins. Corners stay
            // wide (12 px) so the diagonal-resize grab is still comfortable; the long flat side
            // gets the standard narrow band. Mirror for the left edge for symmetry.
            int sideEdge = ScalePx(window, 4);

            bool nearLeftEdge    = x >= rect.Left   && x <  rect.Left   + sideEdge;
            bool nearRightEdge   = x <  rect.Right  && x >= rect.Right  - sideEdge;
            bool nearLeftCorner  = x >= rect.Left   && x <  rect.Left   + corner;
            bool nearRightCorner = x <  rect.Right  && x >= rect.Right  - corner;
            bool nearBottom      = y <  rect.Bottom && y >= rect.Bottom - corner;
            // Side edges (LEFT / RIGHT) — only when not also near top, so we don't steal the
            // top-corner buttons (close / max).
            bool nearTop = y >= rect.Top && y < rect.Top + corner;

            if (nearBottom && nearLeftCorner)  { handled = true; return new IntPtr(HTBOTTOMLEFT); }
            if (nearBottom && nearRightCorner) { handled = true; return new IntPtr(HTBOTTOMRIGHT); }
            if (nearBottom)                    { handled = true; return new IntPtr(HTBOTTOM); }
            // Flat side edges: only the narrow band, not the full corner-wide stripe. Frees up
            // the scrollbar (and any docked content) from being captured as a resize grab.
            if (!nearTop && nearLeftEdge)      { handled = true; return new IntPtr(HTLEFT); }
            if (!nearTop && nearRightEdge)     { handled = true; return new IntPtr(HTRIGHT); }
            return IntPtr.Zero;
        };

    private static int ScalePx(Window window, int logicalPx)
    {
        try
        {
            var src = PresentationSource.FromVisual(window);
            var m = src?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
            var scale = (m.M11 + m.M22) * 0.5;
            return (int)Math.Round(logicalPx * (scale > 0 ? scale : 1.0));
        }
        catch
        {
            return logicalPx;
        }
    }
}
