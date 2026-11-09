using System.Runtime.InteropServices;

namespace AresToys.Capture;

public sealed record MonitorInfo(string Name, int X, int Y, int Width, int Height, bool IsPrimary);

/// <summary>Enumerates physical monitors via <c>EnumDisplayMonitors</c>. WPF doesn't expose a clean
/// API for this (System.Windows.Forms.Screen requires WinForms reference), so we go through Win32.</summary>
public static class MonitorEnumeration
{
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfoW(hMonitor, ref info)) return true;
            var name = info.szDevice ?? $"Monitor {list.Count + 1}";
            var w = info.rcMonitor.Right - info.rcMonitor.Left;
            var h = info.rcMonitor.Bottom - info.rcMonitor.Top;
            var primary = (info.dwFlags & 1) != 0; // MONITORINFOF_PRIMARY
            list.Add(new MonitorInfo(name, info.rcMonitor.Left, info.rcMonitor.Top, w, h, primary));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The monitor currently under the mouse cursor — what ShareX calls "active monitor".
    /// Falls back to the primary monitor when the cursor sits in a gap between monitors (rare on
    /// modern desktop layouts but possible with mismatched resolutions). Returns <c>null</c> only
    /// if no monitors are detected at all.</summary>
    public static MonitorInfo? GetMonitorUnderCursor()
    {
        if (!GetCursorPos(out var pt)) pt = new POINT { X = 0, Y = 0 };
        var monitors = Enumerate();
        if (monitors.Count == 0) return null;
        foreach (var m in monitors)
        {
            if (pt.X >= m.X && pt.X < m.X + m.Width && pt.Y >= m.Y && pt.Y < m.Y + m.Height) return m;
        }
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);
}
