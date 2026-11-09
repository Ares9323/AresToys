using System.Globalization;
using Microsoft.Extensions.Logging;
using AresToys.Editor.Model;
using AresToys.Editor.Persistence;
using AresToys.Editor.Views;

namespace AresToys.App.Services;

/// <summary>Tray-launchable companion to <see cref="ScreenColorPickerService"/>: opens the full
/// HSB/RGB/CMYK colour picker dialog (the "wheel"-style — distinguished from the screen
/// "eyedropper") and returns the picked colour. Two entry-points:
/// <list type="bullet">
/// <item><description><see cref="ShowAsync"/> — the legacy "fire and forget" tray flow that
/// auto-copies hex to clipboard and pushes the recents list. Used by tray menu items.</description></item>
/// <item><description><see cref="PickAsync"/> — returns the <see cref="ShapeColor"/> WITHOUT
/// touching the clipboard, so pipeline steps can stash it in the bag and have a downstream
/// <c>arestoys.copy-color-*</c> task pick the output format.</description></item>
/// </list></summary>
public sealed class ColorWheelLauncher
{
    private readonly ColorRecentsStore _recents;
    private readonly ScreenColorPickerService _sampler;
    private readonly ILogger<ColorWheelLauncher> _logger;

    public ColorWheelLauncher(ColorRecentsStore recents, ScreenColorPickerService sampler, ILogger<ColorWheelLauncher> logger)
    {
        _recents = recents;
        _sampler = sampler;
        _logger = logger;
    }

    /// <summary>Open the picker, return the chosen colour (or null on Cancel). Doesn't touch the
    /// clipboard or recents list — caller decides.</summary>
    /// <param name="onPreview">Called on every colour change inside the dialog, before the user
    /// commits. Lets a caller recolour whatever it's about to apply the colour to while the user is
    /// still dragging, which is the difference between picking a colour and picking it blind. The
    /// caller is responsible for undoing the preview if the dialog comes back null (cancelled).</param>
    /// <param name="initialColour">Seeds the dialog. Null starts from the most recent colour, which
    /// is right for a fresh pick but wrong when editing something that already has a colour.</param>
    /// <param name="ownerHwnd">A window of ANOTHER process to centre on and own the dialog (the
    /// <c>AresToys.exe --pick-color --owner</c> case). Zero keeps the usual behaviour. If the owner cannot be
    /// used (it is gone, or a higher integrity level refuses an owned window), the dialog opens without it.</param>
    /// <param name="externalCaller">The request comes from another app: without an owner the dialog is centred
    /// on the monitor under the cursor, and it is forced to the foreground since the main window is not shown.</param>
    public async Task<ShapeColor?> PickAsync(Action<ShapeColor>? onPreview = null, ShapeColor? initialColour = null,
        IntPtr ownerHwnd = default, bool externalCaller = false)
    {
        var recents = await _recents.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        ColorSwatchButton.CurrentRecents = recents;
        var initial = initialColour ?? (recents.Count > 0 ? recents[0] : ShapeColor.Black);

        if (ownerHwnd != IntPtr.Zero && !IsWindow(ownerHwnd)) ownerHwnd = IntPtr.Zero;

        var dlg = BuildDialog(initial, onPreview, ownerHwnd, externalCaller);

        // Use ShowOwnerScopedDialog so any open editor / other independent dialog stays
        // interactive while the user is in the color picker, only the wheel's owner gets
        // disabled. Same pattern fixes the icon picker to editor freeze.
        bool? result;
        try
        {
            result = dlg.ShowOwnerScopedDialog();
        }
        catch (Exception ex) when (ownerHwnd != IntPtr.Zero)
        {
            // A foreign owner can be refused (UIPI between an elevated and a normal process): show it unowned.
            _logger.LogWarning(ex, "Color wheel: owner window {Owner} refused, opening without it", ownerHwnd);
            dlg = BuildDialog(initial, onPreview, IntPtr.Zero, externalCaller);
            result = dlg.ShowOwnerScopedDialog();
        }
        if (result != true) return null;
        // Push here, not only in ShowAsync: PickAsync is also the pipeline entry point, and a
        // colour the user confirmed with OK belongs in the recents ring no matter which surface
        // asked for it.
        await _recents.PushAsync(dlg.PickedColor, CancellationToken.None).ConfigureAwait(true);
        return dlg.PickedColor;
    }

    private ColorPickerWindow BuildDialog(ShapeColor initial, Action<ShapeColor>? onPreview, IntPtr ownerHwnd, bool externalCaller)
    {
        var dlg = new ColorPickerWindow(initial);
        if (onPreview is not null) dlg.ColorChanged += (_, c) => onPreview(c);
        if (ownerHwnd != IntPtr.Zero)
        {
            new System.Windows.Interop.WindowInteropHelper(dlg).Owner = ownerHwnd;
            dlg.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
        }
        else if (externalCaller)
        {
            // Placed by hand on the monitor under the cursor, in pixels, so the monitor's own scale is respected.
            dlg.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            dlg.SourceInitialized += (_, _) => CenterOnCursorMonitor(dlg);
            dlg.ContentRendered += (_, _) => CenterOnCursorMonitor(dlg);
        }
        else
        {
            dlg.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        }
        if (externalCaller)
        {
            // The main window stays hidden for an external request, so nothing else gives the dialog the foreground.
            dlg.ContentRendered += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                if (hwnd == IntPtr.Zero) return;
                dlg.Activate();
                if (!dlg.IsActive) TargetWindowTracker.ForceForeground(hwnd);
            };
        }
        // Cross-assembly localisation handoff: AresToys.Editor can't reach our resx, so we
        // resolve every translatable label here and push the dictionary into the dialog.
        // Technical channels (R/G/B/H/S/Hex/Dec/sRGB) stay literal and aren't included.
        dlg.ApplyLocalization(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Title"]      = AresToys.App.Resources.Strings.ColorPicker_Title,
            ["Recent"]     = AresToys.App.Resources.Strings.ColorPicker_Recent,
            ["Sampler"]    = AresToys.App.Resources.Strings.ColorPicker_Sampler,
            ["Wheel"]      = AresToys.App.Resources.Strings.ColorPicker_Wheel,
            ["Square"]     = AresToys.App.Resources.Strings.ColorPicker_Square,
            ["CopyAll"]    = AresToys.App.Resources.Strings.ColorPicker_CopyAll,
            ["CopyRgb"]    = AresToys.App.Resources.Strings.ColorPicker_CopyRgb,
            ["CopyCmyk"]   = AresToys.App.Resources.Strings.ColorPicker_CopyCmyk,
            ["CopyHsb"]    = AresToys.App.Resources.Strings.ColorPicker_CopyHsb,
            ["CopyLinear"] = AresToys.App.Resources.Strings.ColorPicker_CopyLinear,
            ["CopyBgra"]   = AresToys.App.Resources.Strings.ColorPicker_CopyBgra,
            ["CopyHex"]    = AresToys.App.Resources.Strings.ColorPicker_CopyHex,
            ["CopyDec"]    = AresToys.App.Resources.Strings.ColorPicker_CopyDec,
            ["Ok"]         = AresToys.App.Resources.Strings.Common_Ok,
            ["Cancel"]     = AresToys.App.Resources.Strings.Common_Cancel,
            // Tooltip keys — same naming scheme as Content keys, prefixed "Tooltip" on the
            // dialog side. Resolved at launcher time so the editor never reaches into our resx.
            ["TooltipCopyAll"]    = Loc("ColorPicker_TooltipCopyAll"),
            ["TooltipCopyRgb"]    = Loc("ColorPicker_TooltipCopyRgb"),
            ["TooltipCopyCmyk"]   = Loc("ColorPicker_TooltipCopyCmyk"),
            ["TooltipCopyHsb"]    = Loc("ColorPicker_TooltipCopyHsb"),
            ["TooltipCopyLinear"] = Loc("ColorPicker_TooltipCopyLinear"),
            ["TooltipCopyBgra"]   = Loc("ColorPicker_TooltipCopyBgra"),
            ["TooltipCopyHex"]    = Loc("ColorPicker_TooltipCopyHex"),
            ["TooltipCopyDec"]    = Loc("ColorPicker_TooltipCopyDec"),
            ["TooltipSampler"]    = Loc("ColorPicker_TooltipSampler"),
            ["TooltipSrgb"]       = Loc("ColorPicker_TooltipSrgb"),
            ["TooltipWheel"]      = Loc("ColorPicker_TooltipWheel"),
            ["TooltipSquare"]     = Loc("ColorPicker_TooltipSquare"),
        });

        // Local helper: resx lookup honouring the singleton's pinned culture. Lives here so
        // the dictionary literal stays terse instead of repeating the lookup chain N times.
        static string Loc(string key)
        {
            var culture = AresToys.App.Markup.LocalizedStrings.Instance.Culture
                          ?? System.Globalization.CultureInfo.CurrentUICulture;
            return AresToys.App.Resources.Strings.ResourceManager.GetString(key, culture) ?? key;
        }

        // Wire the eyedropper button so clicking 🔍 actually opens the screen sampler. The
        // picker itself has no DI access, hence the bridge here. The hex returned by the
        // sampler (#RRGGBB or null) is parsed into ShapeColor and pushed into the dialog via
        // ApplySampledColor — same way the editor wires it up.
        dlg.EyedropperRequested += (_, _) =>
        {
            var hex = _sampler.SampleAtCursor();
            if (hex is null) return;
            if (TryParseHex(hex, out var sampled))
            {
                dlg.ApplySampledColor(sampled);
            }
        };

        return dlg;
    }

    /// <summary>Centres <paramref name="dlg"/> in the work area of the monitor under the cursor. Works in pixels on
    /// the window handle; run once before the window shows and once after layout, since moving to a monitor with
    /// another scale can resize it.</summary>
    private static void CenterOnCursorMonitor(System.Windows.Window dlg)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var cursor) || !GetWindowRect(hwnd, out var rect)) return;
        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var work = info.rcWork;
        var x = work.Left + ((work.Right - work.Left) - (rect.Right - rect.Left)) / 2;
        var y = work.Top + ((work.Bottom - work.Top) - (rect.Bottom - rect.Top)) / 2;
        _ = SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int cbSize; public NativeRect rcMonitor; public NativeRect rcWork; public uint dwFlags; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>Tray flow: opens the picker and copies the hex to the clipboard. The recents push
    /// already happened inside <see cref="PickAsync"/>.</summary>
    public async Task ShowAsync()
    {
        var picked = await PickAsync().ConfigureAwait(true);
        if (picked is null) return;
        var hex = $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}";
        try { System.Windows.Clipboard.SetText(hex); }
        catch { /* clipboard occasionally locked by another app; the pick itself still stands */ }
        _logger.LogInformation("Color wheel: picked {Hex}, copied to clipboard", hex);
    }

    internal static bool TryParseHex(string hex, out ShapeColor color)
    {
        color = ShapeColor.Black;
        var s = hex.Trim().TrimStart('#');
        try
        {
            if (s.Length == 6)
            {
                var r = byte.Parse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var g = byte.Parse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var b = byte.Parse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                color = new ShapeColor(255, r, g, b);
                return true;
            }
        }
        catch (FormatException) { }
        return false;
    }
}
