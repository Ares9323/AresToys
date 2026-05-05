using System.Globalization;
using Microsoft.Extensions.Logging;
using ShareQ.Editor.Model;
using ShareQ.Editor.Persistence;
using ShareQ.Editor.Views;

namespace ShareQ.App.Services;

/// <summary>Tray-launchable companion to <see cref="ScreenColorPickerService"/>: opens the full
/// HSB/RGB/CMYK colour picker dialog (the "wheel"-style — distinguished from the screen
/// "eyedropper") and returns the picked colour. Two entry-points:
/// <list type="bullet">
/// <item><description><see cref="ShowAsync"/> — the legacy "fire and forget" tray flow that
/// auto-copies hex to clipboard and pushes the recents list. Used by tray menu items.</description></item>
/// <item><description><see cref="PickAsync"/> — returns the <see cref="ShapeColor"/> WITHOUT
/// touching the clipboard, so pipeline steps can stash it in the bag and have a downstream
/// <c>shareq.copy-color-*</c> task pick the output format.</description></item>
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
    public async Task<ShapeColor?> PickAsync()
    {
        var recents = await _recents.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        ColorSwatchButton.CurrentRecents = recents;
        var initial = recents.Count > 0 ? recents[0] : ShapeColor.Black;

        var dlg = new ColorPickerWindow(initial);
        dlg.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        // Cross-assembly localisation handoff: ShareQ.Editor can't reach our resx, so we
        // resolve every translatable label here and push the dictionary into the dialog.
        // Technical channels (R/G/B/H/S/Hex/Dec/sRGB) stay literal and aren't included.
        dlg.ApplyLocalization(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Title"]      = ShareQ.App.Resources.Strings.ColorPicker_Title,
            ["Recent"]     = ShareQ.App.Resources.Strings.ColorPicker_Recent,
            ["Sampler"]    = ShareQ.App.Resources.Strings.ColorPicker_Sampler,
            ["Wheel"]      = ShareQ.App.Resources.Strings.ColorPicker_Wheel,
            ["Square"]     = ShareQ.App.Resources.Strings.ColorPicker_Square,
            ["CopyAll"]    = ShareQ.App.Resources.Strings.ColorPicker_CopyAll,
            ["CopyRgb"]    = ShareQ.App.Resources.Strings.ColorPicker_CopyRgb,
            ["CopyCmyk"]   = ShareQ.App.Resources.Strings.ColorPicker_CopyCmyk,
            ["CopyHsb"]    = ShareQ.App.Resources.Strings.ColorPicker_CopyHsb,
            ["CopyLinear"] = ShareQ.App.Resources.Strings.ColorPicker_CopyLinear,
            ["CopyBgra"]   = ShareQ.App.Resources.Strings.ColorPicker_CopyBgra,
            ["CopyHex"]    = ShareQ.App.Resources.Strings.ColorPicker_CopyHex,
            ["CopyDec"]    = ShareQ.App.Resources.Strings.ColorPicker_CopyDec,
            ["Ok"]         = ShareQ.App.Resources.Strings.Common_Ok,
            ["Cancel"]     = ShareQ.App.Resources.Strings.Common_Cancel,
        });

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

        if (dlg.ShowDialog() != true) return null;
        return dlg.PickedColor;
    }

    /// <summary>Tray flow: opens the picker, copies hex to clipboard, pushes to recents.</summary>
    public async Task ShowAsync()
    {
        var picked = await PickAsync().ConfigureAwait(true);
        if (picked is null) return;
        var hex = $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}";
        try { System.Windows.Clipboard.SetText(hex); }
        catch { /* clipboard locked — recents push below still happens */ }
        _logger.LogInformation("Color wheel: picked {Hex}, copied to clipboard", hex);
        await _recents.PushAsync(picked, CancellationToken.None).ConfigureAwait(false);
    }

    private static bool TryParseHex(string hex, out ShapeColor color)
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
