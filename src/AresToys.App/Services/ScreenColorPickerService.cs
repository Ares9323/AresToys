using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.App.Views;

namespace AresToys.App.Services;

/// <summary>Triggered by a global hotkey, opens a full-screen magnifier overlay so the user can
/// pick a pixel precisely. The hex is copied to clipboard and announced via toast.</summary>
public sealed class ScreenColorPickerService
{
    private readonly IToastNotifier _notifier;
    private readonly AresToys.Editor.Persistence.ColorRecentsStore _recents;
    private readonly ILogger<ScreenColorPickerService> _logger;
    private bool _busy;

    public ScreenColorPickerService(
        IToastNotifier notifier,
        AresToys.Editor.Persistence.ColorRecentsStore recents,
        ILogger<ScreenColorPickerService> logger)
    {
        _notifier = notifier;
        _recents = recents;
        _logger = logger;
    }

    /// <summary>Open the overlay and wait for the user to click a pixel. Idempotent — a second hotkey
    /// press while already picking is a no-op. Auto-copies hex to clipboard + shows a toast — use
    /// this for the standalone tray / hotkey flow where there's no pipeline downstream.</summary>
    public string? PickAtCursor()
    {
        var hex = SampleAtCursor();
        if (hex is null) return null;
        CopyHexToClipboard(hex);
        _notifier.Show("Color picked", $"{hex} copied to clipboard");
        return hex;
    }

    /// <summary>Lower-level variant: opens the overlay and returns the picked hex (or null on
    /// cancel) WITHOUT touching the clipboard or showing a toast. Pipeline tasks call this so
    /// they can stash the colour in <see cref="AresToys.Core.Pipeline.PipelineBagKeys.Color"/> and
    /// let downstream <c>arestoys.copy-color-*</c> steps decide what format to emit.</summary>
    public string? SampleAtCursor()
    {
        if (_busy) return null;
        _busy = true;
        try
        {
            string? hex = null;
            Application.Current.Dispatcher.Invoke(() =>
            {
                var overlay = new ScreenColorPickerOverlay();
                if (overlay.ShowDialog() == true) hex = overlay.PickedHex;
            });
            if (hex is null) { _logger.LogInformation("ScreenColorPicker: cancelled"); return null; }
            _logger.LogInformation("ScreenColorPicker: picked {Hex}", hex);
            // Every eyedropper sample lands in the shared "Recent colors" ring — this is the
            // single choke point for all sampler flows (tray hotkey, pipeline task, and the 🔍
            // button inside the colour picker), so one push here covers them all.
            PushToRecents(hex);
            return hex;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScreenColorPicker failed");
            return null;
        }
        finally { _busy = false; }
    }

    /// <summary>Fire-and-forget push of a sampled "#RRGGBB" into the recents ring. Faults are
    /// swallowed: a settings-store hiccup must never take down the pick itself (the user still
    /// gets the hex on the clipboard / in the pipeline bag).</summary>
    private void PushToRecents(string hex)
    {
        if (!TryParseHex(hex, out var color)) return;
        _ = Task.Run(async () =>
        {
            try { await _recents.PushAsync(color, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "ScreenColorPicker: recents push failed"); }
        });
    }

    private static bool TryParseHex(string hex, out AresToys.Editor.Model.ShapeColor color)
    {
        color = AresToys.Editor.Model.ShapeColor.Black;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6) return false;
        try
        {
            var r = byte.Parse(s.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            var g = byte.Parse(s.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            var b = byte.Parse(s.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            color = new AresToys.Editor.Model.ShapeColor(255, r, g, b);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static void CopyHexToClipboard(string hex)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            try { System.Windows.Clipboard.SetText(hex); }
            catch (System.Runtime.InteropServices.COMException) { /* clipboard occasionally locked; ignore */ }
        });
    }
}
