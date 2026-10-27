using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using static AresToys.App.Services.AlwaysOnTop.AlwaysOnTopNative;

namespace AresToys.App.Services.AlwaysOnTop;

/// <summary>Click-through transparent overlay that draws a frame just outside a pinned window.
/// Positioned in device pixels with SetWindowPos so scaling and multi-monitor DPI need no
/// DIP conversion; only the ring thickness is converted to DIPs for WPF.</summary>
internal sealed class BorderWindow : Window
{
    private static readonly IVirtualDesktopManager? DesktopManager = CreateDesktopManager();

    private readonly nint _target;
    private int _thicknessPx;
    private int _cornerRadiusPx;
    private readonly Border _ring;
    private nint _hwnd;
    private bool _visible;
    private double _appliedScale;

    public BorderWindow(nint target, AlwaysOnTopSettings settings)
    {
        _target = target;
        _ring = new Border { IsHitTestVisible = false };
        ApplySettings(settings);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
        Content = _ring;

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var ex = GetWindowLongPtr(_hwnd, GwlExStyle);
        SetWindowLongPtr(_hwnd, GwlExStyle, ex | WsExTransparent | WsExToolWindow | WsExNoActivate);
        Show();
        ShowWindow(_hwnd, SwHide);
    }

    /// <summary>Re-reads the target's frame and shows, moves or hides the ring accordingly.</summary>
    public void Refresh()
    {
        if (!ShouldShow() || !TryGetFrame(out var frame))
        {
            HideFrame();
            return;
        }

        ApplyScale();
        var t = _thicknessPx;
        var flags = SwpNoActivate | (_visible ? 0 : SwpShowWindow);

        // Sit directly above the target in the topmost band so the ring never covers an
        // unrelated topmost window that is above the pinned one.
        var prev = AlwaysOnTopNative.GetWindow(_target, GwHwndPrev);
        nint insertAfter;
        if (prev == _hwnd)
        {
            insertAfter = 0;
            flags |= SwpNoZOrder;
        }
        else
        {
            insertAfter = prev != 0 ? prev : HwndTopmost;
        }

        SetWindowPos(_hwnd, insertAfter, frame.Left - t, frame.Top - t,
            frame.Right - frame.Left + 2 * t, frame.Bottom - frame.Top + 2 * t, flags);
        _visible = true;
    }

    /// <summary>Applies a new look to a live frame; the next <see cref="Refresh"/> resizes it.</summary>
    public void ApplySettings(AlwaysOnTopSettings settings)
    {
        _thicknessPx = settings.ThicknessPx;
        _cornerRadiusPx = settings.CornerRadiusPx;
        var brush = new SolidColorBrush(settings.Color);
        brush.Freeze();
        _ring.BorderBrush = brush;
        _ring.Opacity = settings.OpacityPercent / 100.0;
        _appliedScale = 0;
    }

    public void HideFrame()
    {
        if (!_visible) return;
        ShowWindow(_hwnd, SwHide);
        _visible = false;
    }

    private void ApplyScale()
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (Math.Abs(scale - _appliedScale) < 0.001) return;
        _appliedScale = scale;
        _ring.BorderThickness = new Thickness(_thicknessPx / scale);
        // Windows 11 frames have 8 px corners; the ring wraps them from outside.
        var radiusPx = _cornerRadiusPx != AlwaysOnTopSettings.AutoCorner
            ? _cornerRadiusPx
            : Environment.OSVersion.Version.Build >= 22000 ? 8 + _thicknessPx : 0;
        _ring.CornerRadius = new CornerRadius(radiusPx / scale);
    }

    private bool ShouldShow()
    {
        if (!IsWindowVisible(_target) || IsIconic(_target) || IsZoomed(_target)) return false;
        return OnCurrentDesktop(_target);
    }

    private bool TryGetFrame(out AlwaysOnTopNative.Rect frame)
        => DwmGetWindowAttribute(_target, DwmaExtendedFrameBounds, out frame, Marshal.SizeOf<AlwaysOnTopNative.Rect>()) == 0;

    private static bool IsWindowVisible(nint hwnd) => AresToys.App.Native.AppNativeMethods.IsWindowVisible(hwnd);

    private static bool OnCurrentDesktop(nint hwnd)
    {
        if (DesktopManager is null) return true;
        try
        {
            return DesktopManager.IsWindowOnCurrentVirtualDesktop(hwnd, out var onCurrent) != 0 || onCurrent;
        }
        catch (COMException)
        {
            return true;
        }
    }

    private static IVirtualDesktopManager? CreateDesktopManager()
    {
        try { return (IVirtualDesktopManager)new VirtualDesktopManagerCom(); }
        catch (COMException) { return null; }
    }
}
