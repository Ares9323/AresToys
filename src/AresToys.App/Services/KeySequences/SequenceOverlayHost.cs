using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.App.Views;

namespace AresToys.App.Services.KeySequences;

/// <summary>
/// Singleton service that owns the lifecycle of the WPF <see cref="KeySequenceOverlay"/> window
/// and translates the <see cref="ISequenceOverlay"/> contract into concrete window operations.
/// Positions the overlay according to <see cref="KeySequenceModuleSettings.Position"/>:
///   <list type="bullet">
///   <item>MouseCursor: <c>GetCursorPos</c> + small vertical offset.</item>
///   <item>LastPosition: restored from <see cref="KeySequenceModuleSettings.LastX"/> / LastY,
///         saved on close after drag-to-move. Falls back to FixedCenter on first run.</item>
///   <item>FixedTop / FixedCenter / FixedBottom: horizontally centered on the monitor under the
///         mouse cursor, anchored to top / center / bottom of that monitor's work area.</item>
///   </list>
/// </summary>
public sealed class SequenceOverlayHost : ISequenceOverlay
{
    private readonly KeySequenceModuleSettings _settings;
    private readonly ILogger<SequenceOverlayHost> _logger;
    private KeySequenceOverlay? _window;

    public SequenceOverlayHost(KeySequenceModuleSettings settings, ILogger<SequenceOverlayHost> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsVisible => _window?.IsVisible ?? false;

    public void Show(IReadOnlyList<OverlayOption> options, Action<OverlayOption> onConfirm, Action onDismiss)
    {
        _logger.LogInformation("KS-DEBUG: overlay.Show called with {N} options.", options.Count);
        EnsureWindow();
        _window!.SetOptions(options, onConfirm, onDismiss);
        // First Show off-screen so WPF runs its layout pass and populates ActualWidth/Height —
        // positioning BEFORE Show() leaves both at 0 (SizeToContent doesn't compute until the
        // window is in the visual tree). Then snap to the real target position. The offscreen
        // flash is invisible because the window has ShowActivated=false and the user's foreground
        // app keeps focus the whole time.
        _window.Left = -32000;
        _window.Top = -32000;
        _window.Show();
        _window.UpdateLayout();
        PositionOverlay();
        _logger.LogInformation("KS-DEBUG: overlay shown at ({Left}, {Top}) size {W}x{H}.", _window.Left, _window.Top, _window.ActualWidth, _window.ActualHeight);
    }

    public void Close()
    {
        if (_window is null) return;
        // Capture the position the user dragged the overlay to (if visible) BEFORE hiding so
        // LastPosition mode can restore it next time. Hidden / never-shown windows have no
        // meaningful Left/Top so we skip the persist in that case.
        if (_window.IsVisible)
        {
            var x = (int)_window.Left;
            var y = (int)_window.Top;
            _window.Hide();
            // Fire-and-forget — saving is non-blocking; if it races a process exit the user
            // simply loses the most recent drag, which is acceptable.
            _ = _settings.SaveLastPositionAsync(x, y, CancellationToken.None);
        }
        _window.RaiseDismiss();
    }

    public void SelectNext()
    {
        _logger.LogInformation("KS-DEBUG: overlay.SelectNext (window?.count={N}, idx={I}).", _window?.OptionCount, _window?.SelectedIndex);
        _window?.SelectNext();
    }
    public void SelectPrevious()
    {
        _logger.LogInformation("KS-DEBUG: overlay.SelectPrevious (window?.count={N}, idx={I}).", _window?.OptionCount, _window?.SelectedIndex);
        _window?.SelectPrevious();
    }
    public void ConfirmCurrent()
    {
        _logger.LogInformation("KS-DEBUG: overlay.ConfirmCurrent (window?.count={N}, idx={I}, hasCb={Cb}).", _window?.OptionCount, _window?.SelectedIndex, _window?.HasConfirmCallback);
        _window?.ConfirmCurrent();
    }

    private void EnsureWindow()
    {
        if (_window is not null) return;
        _window = new KeySequenceOverlay();
        // Don't make it owned by MainWindow — owned windows follow the owner's z-order, and the
        // overlay needs to float above whatever app the user is typing into.
    }

    private void PositionOverlay()
    {
        if (_window is null) return;
        _window.UpdateLayout(); // ensure ActualWidth/Height reflect the current item list
        var size = new System.Windows.Size(
            _window.ActualWidth > 0 ? _window.ActualWidth : _window.MinWidth,
            _window.ActualHeight > 0 ? _window.ActualHeight : 80);

        // All positioning math runs in DIPs (= WPF Window.Left/Top units). Win32 GetCursorPos /
        // GetMonitorInfo give physical pixels; we convert via PhysicalToDip before using them.
        var point = _settings.Position switch
        {
            OverlayPositionMode.MouseCursor => GetMousePointDip(),
            OverlayPositionMode.LastPosition => GetLastPositionPoint() ?? GetFixedAnchorPointDip(OverlayPositionMode.FixedCenter, size),
            OverlayPositionMode.FixedTop => GetFixedAnchorPointDip(OverlayPositionMode.FixedTop, size),
            OverlayPositionMode.FixedBottom => GetFixedAnchorPointDip(OverlayPositionMode.FixedBottom, size),
            OverlayPositionMode.CaretRight => TryGetCaretAnchorDip(rightOfCaret: true, size) ?? GetFixedAnchorPointDip(OverlayPositionMode.FixedCenter, size),
            OverlayPositionMode.CaretTop => TryGetCaretAnchorDip(rightOfCaret: false, size) ?? GetFixedAnchorPointDip(OverlayPositionMode.FixedCenter, size),
            _ => GetFixedAnchorPointDip(OverlayPositionMode.FixedCenter, size),
        };

        var clamped = ClampToActiveMonitorDip(point, size);
        _window.Left = clamped.X;
        _window.Top = clamped.Y;
    }

    /// <summary>Convert a screen-space pixel point into WPF DIPs using the overlay window's
    /// composition transform. Critical on high-DPI monitors (scaling ≠ 100%): without this,
    /// raw GetCursorPos coordinates land the overlay shifted away from where the user expects.
    /// Falls back to identity (no scaling) if the window isn't yet hooked into the visual tree.</summary>
    private System.Windows.Point PhysicalToDip(double x, double y)
    {
        if (_window is not null)
        {
            var src = PresentationSource.FromVisual(_window);
            if (src?.CompositionTarget is { } target)
            {
                return target.TransformFromDevice.Transform(new System.Windows.Point(x, y));
            }
        }
        return new System.Windows.Point(x, y);
    }

    private System.Windows.Point? GetLastPositionPoint()
    {
        // LastX/LastY were captured from Window.Left/Top which is already DIPs — pass through.
        if (_settings.LastX is not int x || _settings.LastY is not int y) return null;
        return new System.Windows.Point(x, y);
    }

    private System.Windows.Point GetMousePointDip()
    {
        if (!GetCursorPos(out var p)) return new System.Windows.Point(100, 100);
        var dip = PhysicalToDip(p.X, p.Y);
        return new System.Windows.Point(dip.X + 12, dip.Y + 20);
    }

    /// <summary>Best-effort caret anchor via Win32 <c>GetGUIThreadInfo.rcCaret</c>. Returns null
    /// when the focused control doesn't publish a caret rect (Chromium / Electron / WinUI), or
    /// when any Win32 call along the chain fails. Coordinates returned in DIPs (rcCaret +
    /// ClientToScreen yield physical pixels, converted via <see cref="PhysicalToDip"/>).
    /// <paramref name="rightOfCaret"/>: true → anchor just RIGHT of the caret rect at its top;
    /// false → anchor ABOVE the caret line (uses <paramref name="overlaySize"/> to offset by the
    /// overlay's height so the bottom edge sits just above the caret).</summary>
    private System.Windows.Point? TryGetCaretAnchorDip(bool rightOfCaret, System.Windows.Size overlaySize)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        var threadId = GetWindowThreadProcessId(fg, out _);
        if (threadId == 0) return null;

        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(threadId, ref info)) return null;
        if (info.hwndCaret == IntPtr.Zero) return null;
        if (info.rcCaret.Right == 0 && info.rcCaret.Bottom == 0) return null;

        // rcCaret is in client coords of hwndCaret — convert to screen coords first.
        POINT pt;
        if (rightOfCaret)
        {
            // Right edge + small gap, top of the line.
            pt = new POINT { X = info.rcCaret.Right + 4, Y = info.rcCaret.Top };
            if (!ClientToScreen(info.hwndCaret, ref pt)) return null;
            return PhysicalToDip(pt.X, pt.Y);
        }
        // CaretTop: align left edge with caret left, bottom edge with caret top - small gap.
        // ClientToScreen converts in physical pixels; subtract the overlay height AFTER the
        // DIP conversion (overlaySize is already in DIPs).
        pt = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
        if (!ClientToScreen(info.hwndCaret, ref pt)) return null;
        var anchor = PhysicalToDip(pt.X, pt.Y);
        return new System.Windows.Point(anchor.X, anchor.Y - overlaySize.Height - 4);
    }

    /// <summary>Compute the screen position for a fixed anchor (Top / Center / Bottom). Anchors
    /// horizontally centered on the monitor containing the mouse cursor — likelier to be the
    /// screen the user is actively working on than blindly defaulting to the primary monitor.
    /// A 60px vertical margin on Top / Bottom keeps the overlay from kissing the screen edge
    /// (and on Bottom keeps it above the Windows taskbar).</summary>
    private System.Windows.Point GetFixedAnchorPointDip(OverlayPositionMode anchor, System.Windows.Size size)
    {
        const int VerticalMargin = 60;
        var work = GetActiveWorkAreaDip();
        var centerX = work.Left + work.Width / 2.0 - size.Width / 2.0;
        var y = anchor switch
        {
            OverlayPositionMode.FixedTop => work.Top + VerticalMargin,
            OverlayPositionMode.FixedBottom => work.Bottom - size.Height - VerticalMargin,
            _ => work.Top + work.Height / 2.0 - size.Height / 2.0,
        };
        return new System.Windows.Point(centerX, y);
    }

    private System.Windows.Rect GetActiveWorkAreaDip()
    {
        if (GetCursorPos(out var p))
        {
            var monitor = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var tl = PhysicalToDip(info.rcWork.Left, info.rcWork.Top);
                    var br = PhysicalToDip(info.rcWork.Right, info.rcWork.Bottom);
                    return new System.Windows.Rect(tl, br);
                }
            }
        }
        // SystemParameters.WorkArea is already in DIPs.
        return SystemParameters.WorkArea;
    }

    private System.Windows.Point ClampToActiveMonitorDip(System.Windows.Point desired, System.Windows.Size size)
    {
        // Resolve the monitor under the desired DIP point: convert DIPs back to pixels for the
        // MonitorFromPoint call (works correctly with multi-monitor + per-monitor DPI). Falls
        // back to SystemParameters.WorkArea if the Win32 calls fail.
        if (_window is not null)
        {
            var src = PresentationSource.FromVisual(_window);
            if (src?.CompositionTarget is { } target)
            {
                var physical = target.TransformToDevice.Transform(desired);
                var pt = new POINT { X = (int)physical.X, Y = (int)physical.Y };
                var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        var tl = PhysicalToDip(info.rcWork.Left, info.rcWork.Top);
                        var br = PhysicalToDip(info.rcWork.Right, info.rcWork.Bottom);
                        var x = Math.Max(tl.X, Math.Min(desired.X, br.X - size.Width));
                        var y = Math.Max(tl.Y, Math.Min(desired.Y, br.Y - size.Height));
                        return new System.Windows.Point(x, y);
                    }
                }
            }
        }
        var fallback = SystemParameters.WorkArea;
        var fx = Math.Max(fallback.Left, Math.Min(desired.X, fallback.Right - size.Width));
        var fy = Math.Max(fallback.Top, Math.Min(desired.Y, fallback.Bottom - size.Height));
        return new System.Windows.Point(fx, fy);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
}
