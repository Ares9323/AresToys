using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using AresToys.App.Views;
using AresToys.Capture;
using AresToys.Storage.Settings;

namespace AresToys.App.Services;

/// <summary>
/// Tray-launchable "Pin to screen" feature, mirroring ShareX. Asks the user where the image
/// should come from (screen region / clipboard / file), then opens a <see cref="PinnedImageWindow"/>
/// with the chosen content. The "from screen" path leaves the captured rectangle pinned at its
/// original on-screen coordinates so it visually replaces what was there.
/// </summary>
public sealed class PinToScreenLauncher
{
    private readonly ICaptureSource _captureSource;
    private readonly ISettingsStore _settings;
    private readonly EditorLauncher _editor;
    private readonly ILogger<PinToScreenLauncher> _logger;
    private readonly ILogger<PinnedImageWindow> _windowLogger;

    public PinToScreenLauncher(
        ICaptureSource captureSource,
        ISettingsStore settings,
        EditorLauncher editor,
        ILogger<PinToScreenLauncher> logger,
        ILogger<PinnedImageWindow> windowLogger)
    {
        _captureSource = captureSource;
        _settings = settings;
        _editor = editor;
        _logger = logger;
        _windowLogger = windowLogger;
    }

    /// <summary>Show the chooser and dispatch to the chosen source. UI-thread only. The chooser
    /// is shown <c>Show()</c>-modelessly (not <c>ShowDialog()</c>) so the rest of the app stays
    /// interactive while it's up; we await the window's <c>CompletionTask</c> for the user's
    /// pick.</summary>
    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        var chooser = new PinSourceChooserWindow();
        chooser.Show();
        var picked = await chooser.CompletionTask.ConfigureAwait(true);
        if (picked == PinSource.Cancelled) return;

        switch (picked)
        {
            case PinSource.Screen:    await FromScreenAsync(cancellationToken).ConfigureAwait(true); break;
            case PinSource.Clipboard: await FromClipboardAsync(cancellationToken).ConfigureAwait(true); break;
            case PinSource.File:      await FromFileAsync(cancellationToken).ConfigureAwait(true); break;
        }
    }

    private async Task FromScreenAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Pin from screen: opening region overlay");
        var overlay = new RegionOverlayWindow();
        var region = overlay.PickRegion();
        if (region is null || region.IsEmpty)
        {
            _logger.LogInformation("Pin from screen: cancelled (empty region or Esc)");
            return;
        }
        _logger.LogInformation("Pin from screen: region picked at ({X}, {Y}) size {W}×{H} (physical pixels)",
            region.X, region.Y, region.Width, region.Height);

        try
        {
            var captured = await _captureSource.CaptureAsync(region, cancellationToken).ConfigureAwait(true);
            var bitmap = DecodePng(captured.PngBytes);
            if (bitmap is null)
            {
                _logger.LogWarning("Pin from screen: bitmap decode failed");
                return;
            }
            _logger.LogInformation("Pin from screen: bitmap decoded {W}×{H} px", bitmap.PixelWidth, bitmap.PixelHeight);
            var border = await PinnedImageWindow.LoadStickyBorderAsync(_settings, cancellationToken).ConfigureAwait(true);
            _logger.LogInformation("Pin from screen: sticky border = {Border} DIPs", border);
            var w = new PinnedImageWindow(bitmap, initialScreenPos: (region.X, region.Y),
                settings: _settings, editor: _editor, initialBorderThickness: border, logger: _windowLogger);
            w.ShowAtCapturedPixel();
            _logger.LogInformation("Pin from screen: window shown — Left={Left}, Top={Top} (DIPs)", w.Left, w.Top);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pin from screen: capture failed");
        }
    }

    private async Task FromClipboardAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsImage()) return;
            var bmp = System.Windows.Clipboard.GetImage();
            if (bmp is null) return;
            bmp.Freeze();
            var border = await PinnedImageWindow.LoadStickyBorderAsync(_settings, cancellationToken).ConfigureAwait(true);
            var w = new PinnedImageWindow(bmp, settings: _settings, editor: _editor, initialBorderThickness: border, logger: _windowLogger);
            w.ShowAtCapturedPixel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PinToScreenLauncher: failed to read clipboard image");
        }
    }

    private async Task FromFileAsync(CancellationToken cancellationToken)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick image to pin",
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            var bitmap = DecodePng(bytes);
            if (bitmap is null) return;
            var border = await PinnedImageWindow.LoadStickyBorderAsync(_settings, cancellationToken).ConfigureAwait(true);
            var w = new PinnedImageWindow(bitmap, settings: _settings, editor: _editor, initialBorderThickness: border, logger: _windowLogger);
            w.ShowAtCapturedPixel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PinToScreenLauncher: failed to load file {Path}", dlg.FileName);
        }
    }

    /// <summary>Decode arbitrary image bytes (PNG / JPG / BMP / GIF / TIFF — anything WIC handles).
    /// Frozen so the bitmap can be assigned across threads / shown by long-lived windows.</summary>
    private static BitmapSource? DecodePng(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
