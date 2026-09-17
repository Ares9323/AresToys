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
    public async Task<ShapeColor?> PickAsync()
    {
        var recents = await _recents.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        ColorSwatchButton.CurrentRecents = recents;
        var initial = recents.Count > 0 ? recents[0] : ShapeColor.Black;

        var dlg = new ColorPickerWindow(initial);
        dlg.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
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

        // Use ShowOwnerScopedDialog so any open editor / other independent dialog stays
        // interactive while the user is in the color picker — only the wheel's owner gets
        // disabled. Same pattern fixes the icon picker → editor freeze.
        if (dlg.ShowOwnerScopedDialog() != true) return null;
        // Push here, not only in ShowAsync: PickAsync is also the pipeline entry point, and a
        // colour the user confirmed with OK belongs in the recents ring no matter which surface
        // asked for it.
        await _recents.PushAsync(dlg.PickedColor, CancellationToken.None).ConfigureAwait(true);
        return dlg.PickedColor;
    }

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
