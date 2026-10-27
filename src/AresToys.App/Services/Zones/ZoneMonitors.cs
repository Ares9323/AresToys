using Microsoft.Extensions.Logging;
using static AresToys.App.Services.Zones.ZoneNative;

namespace AresToys.App.Services.Zones;

/// <summary>The one owner of the monitor set the zones work on. Every Zones component asks this class for monitors
/// and none calls the Win32 enumeration itself. Normally it hands out the real monitors. With "span zones across
/// monitors" on, and the same DPI scale on every monitor, it hands out ONE monitor with the id
/// <see cref="SpanId"/> whose work area is the bounding rectangle of all the work areas (as PowerToys does: a plain
/// min and max over the monitors, the taskbars are not treated separately). The set is read fresh on every call,
/// so plugging or unplugging a monitor is seen at once. Safe from any thread.</summary>
public sealed class ZoneMonitors
{
    /// <summary>The id of the combined monitor, and the key of its layout assignment.</summary>
    public const string SpanId = "span";

    private const double DpiTolerance = 0.01;

    private readonly ILogger<ZoneMonitors> _logger;
    private volatile bool _spanRequested;
    private volatile bool _dpiWarned;

    public ZoneMonitors(ILogger<ZoneMonitors> logger)
    {
        _logger = logger;
    }

    /// <summary>True when the option is on and every monitor has the same DPI scale, so the combined rectangle
    /// can be treated as one monitor. When the option is on but the scales differ this is false and a warning is
    /// logged once per settings change.</summary>
    public bool SpanActive => BuildSpan(ZoneMonitor.Enumerate()) is not null;

    /// <summary>Takes in the spanning option. Call it whenever the settings are loaded or saved.</summary>
    public void ApplySettings(WindowSnappingSettings settings)
    {
        _spanRequested = settings.SpanZonesAcrossMonitors;
        _dpiWarned = false;
    }

    /// <summary>The monitors the zones work on: the single combined monitor while spanning is active, otherwise
    /// every real monitor. Empty only when Windows refuses to enumerate.</summary>
    internal IReadOnlyList<ZoneMonitor> All()
    {
        var real = ZoneMonitor.Enumerate();
        return BuildSpan(real) is { } span ? [span] : real;
    }

    /// <summary>Every real monitor, whatever the spanning option says. Used to number the displays for the
    /// Identify button.</summary>
    internal IReadOnlyList<ZoneMonitor> Real() => ZoneMonitor.Enumerate();

    /// <summary>The monitor under the screen pixel (the nearest one when the point is outside every monitor), or
    /// the combined monitor while spanning is active.</summary>
    internal ZoneMonitor? FromPoint(int x, int y)
    {
        var real = ZoneMonitor.Enumerate();
        return BuildSpan(real) ?? ZoneMonitor.FromPoint(x, y);
    }

    /// <summary>The monitor that holds the center of the window (the nearest one when the center is off screen),
    /// or the combined monitor while spanning is active. Null when the window has no bounds.</summary>
    internal ZoneMonitor? ForWindow(nint hwnd)
    {
        if (hwnd == 0 || !GetWindowRect(hwnd, out var r)) return null;
        return FromPoint((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
    }

    /// <summary>The primary monitor, or the combined monitor while spanning is active.</summary>
    internal ZoneMonitor? Primary()
    {
        var all = All();
        return all.FirstOrDefault(m => m.IsPrimary) ?? (all.Count > 0 ? all[0] : null);
    }

    /// <summary>The monitor with the given id, or null when there is none now.</summary>
    internal ZoneMonitor? ById(string id)
        => All().FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>The REAL monitor the layout editor window opens on, by the "launch editor on" choice, whatever the
    /// spanning option says (the window itself sits on one display). As in PowerToys, a choice that cannot be resolved
    /// (no cursor position, no foreground window) falls back to the primary monitor. Null only when Windows refuses
    /// to enumerate.</summary>
    internal ZoneMonitor? EditorMonitor(ZoneEditorDisplay display)
    {
        var real = ZoneMonitor.Enumerate();
        if (real.Count == 0) return null;

        ZoneMonitor? chosen = null;
        switch (display)
        {
            case ZoneEditorDisplay.CursorMonitor:
                if (GetCursorPos(out var cursor)) chosen = ZoneMonitor.FromPoint(cursor.X, cursor.Y);
                break;
            case ZoneEditorDisplay.ActiveWindowMonitor:
                var foreground = GetForegroundWindow();
                if (foreground != 0 && GetWindowRect(foreground, out var r))
                {
                    chosen = ZoneMonitor.FromPoint((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
                }

                break;
        }

        return chosen ?? real.FirstOrDefault(m => m.IsPrimary) ?? real[0];
    }

    // The combined monitor, or null when the option is off, there is no monitor, or the DPI scales differ.
    private ZoneMonitor? BuildSpan(IReadOnlyList<ZoneMonitor> real)
    {
        if (!_spanRequested || real.Count == 0) return null;

        var scale = real[0].DpiScale;
        if (real.Any(m => Math.Abs(m.DpiScale - scale) > DpiTolerance))
        {
            if (!_dpiWarned)
            {
                _dpiWarned = true;
                _logger.LogWarning("Zones: zones spanning needs the same DPI on all monitors, the option has no effect now");
            }

            return null;
        }

        var left = real.Min(m => m.WorkArea.Left);
        var top = real.Min(m => m.WorkArea.Top);
        var right = real.Max(m => m.WorkArea.Right);
        var bottom = real.Max(m => m.WorkArea.Bottom);
        return new ZoneMonitor(SpanId, new PixelRect(left, top, right, bottom), scale, true);
    }
}
