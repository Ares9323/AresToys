using static AresToys.App.Services.Zones.ZoneNative;

namespace AresToys.App.Services.Zones;

/// <summary>A monitor as the zones see it: <see cref="Id"/> is the GDI device name
/// (for example <c>\\.\DISPLAY1</c>), the key used for layout assignments; <see cref="WorkArea"/>
/// is the usable area in device pixels (taskbar excluded); <see cref="DpiScale"/> is the
/// effective DPI of the monitor divided by 96; <see cref="IsPrimary"/> marks the primary monitor. The combined
/// monitor of "span zones across monitors" is built by <see cref="ZoneMonitors"/> with the id
/// <see cref="ZoneMonitors.SpanId"/>. <see cref="FromPoint"/> and <see cref="Enumerate"/> read the real monitors
/// and are for <see cref="ZoneMonitors"/> only; every other component asks that class.</summary>
internal sealed record ZoneMonitor(string Id, PixelRect WorkArea, double DpiScale, bool IsPrimary = false)
{
    private const uint MonitorInfoPrimary = 1;

    // sizeof(MONITORINFOEXW): 4 + 16 + 16 + 4 + 32 UTF-16 units.
    private const int MonitorInfoExSize = 104;

    /// <summary>The monitor under the given screen pixel, or the nearest one when the point is
    /// outside every monitor. Null only when Windows refuses to describe the monitor.</summary>
    internal static ZoneMonitor? FromPoint(int x, int y)
    {
        var handle = MonitorFromPoint(new Point { X = x, Y = y }, MonitorDefaultToNearest);
        return handle == 0 ? null : Describe(handle);
    }

    /// <summary>Every connected monitor, in the order Windows enumerates them (the primary one first in
    /// practice). Empty only when Windows refuses to enumerate.</summary>
    internal static IReadOnlyList<ZoneMonitor> Enumerate()
    {
        var found = new List<ZoneMonitor>();
        bool Collect(nint handle, nint hdc, nint rect, nint data)
        {
            if (Describe(handle) is { } monitor) found.Add(monitor);
            return true;
        }

        EnumDisplayMonitors(0, 0, Collect, 0);
        return found;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    private static ZoneMonitor? Describe(nint handle)
    {
        var info = new MonitorInfoEx { Size = MonitorInfoExSize };
        if (!GetMonitorInfo(handle, ref info)) return null;

        ReadOnlySpan<ushort> units = info.Device;
        var name = System.Runtime.InteropServices.MemoryMarshal.Cast<ushort, char>(units);
        var end = name.IndexOf('\0');
        var id = (end >= 0 ? name[..end] : name).ToString();

        var scale = 1.0;
        if (GetDpiForMonitor(handle, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        var work = new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
        return new ZoneMonitor(id, work, scale, (info.Flags & MonitorInfoPrimary) != 0);
    }
}
