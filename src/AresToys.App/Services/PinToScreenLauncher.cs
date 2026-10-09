using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using AresToys.App.Services.Pins;
using AresToys.App.Views;
using AresToys.Capture;
using AresToys.Clipboard;
using AresToys.Storage.Items;
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
    private readonly IItemStore _items;
    private readonly CaptureImageOutputService _outputEncoder;
    private readonly IClipboardListener? _listener;
    private readonly ILogger<PinToScreenLauncher> _logger;
    private readonly ILogger<PinnedImageWindow> _windowLogger;
    private readonly ILogger<PinnedVideoWindow>? _videoWindowLogger;
    private readonly IToastNotifier? _notifier;
    private readonly PinPersistenceService? _persistence;

    /// <summary>Same key the clipboard panel's preview mute toggle persists to: a pinned video
    /// starts with the user's last preview choice.</summary>
    private const string PreviewMutedSettingKey = "clipboard.preview.muted";

    public PinToScreenLauncher(
        ICaptureSource captureSource,
        ISettingsStore settings,
        EditorLauncher editor,
        IItemStore items,
        CaptureImageOutputService outputEncoder,
        ILogger<PinToScreenLauncher> logger,
        ILogger<PinnedImageWindow> windowLogger,
        IClipboardListener? listener = null,
        IToastNotifier? notifier = null,
        ILogger<PinnedVideoWindow>? videoWindowLogger = null,
        PinPersistenceService? persistence = null)
    {
        _captureSource = captureSource;
        _settings = settings;
        _editor = editor;
        _items = items;
        _outputEncoder = outputEncoder;
        _listener = listener;
        _logger = logger;
        _windowLogger = windowLogger;
        _notifier = notifier;
        _videoWindowLogger = videoWindowLogger;
        _persistence = persistence;
    }

    /// <summary>Show a freshly created image pin and hand it to the persistence service.</summary>
    private void ShowPin(PinnedImageWindow window)
    {
        window.ShowAtCapturedPixel();
        _persistence?.TrackImage(window);
    }

    /// <summary>Bring back the pins that were open when the app last exited, at their saved
    /// pixels, zoom, opacity and border, without taking focus. Entries whose file is gone or
    /// can't be decoded are pruned. UI-thread only.</summary>
    public async Task RestorePinnedAsync(CancellationToken cancellationToken)
    {
        if (_persistence is null) return;
        if (!await _persistence.LoadEnabledAsync(cancellationToken).ConfigureAwait(true)) return;
        var records = await Task.Run(_persistence.LoadForRestore, cancellationToken).ConfigureAwait(true);
        if (records.Count == 0) return;

        var monitors = MonitorEnumeration.Enumerate();
        var monitorRects = monitors.Select(m => new PixelRect(m.X, m.Y, m.Width, m.Height)).ToList();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) is { } p ? new PixelRect(p.X, p.Y, p.Width, p.Height) : (PixelRect?)null;
        _logger.LogInformation("Pins: restoring {Count} pinned window(s)", records.Count);

        foreach (var record in records)
        {
            try
            {
                var saved = new PixelRect(record.X, record.Y, record.Width, record.Height);
                var target = PinPlacement.EnsureVisible(saved, monitorRects, primary);
                var snapped = target != saved;
                if (snapped)
                    _logger.LogInformation("Pins: pin {Id} was off screen at ({X}, {Y}), moved to ({NX}, {NY})",
                        record.Id, saved.X, saved.Y, target.X, target.Y);

                if (record.Kind == PinKind.Image)
                {
                    var path = _persistence.ImagePath(record);
                    var bitmap = await Task.Run(() => DecodeFile(path), cancellationToken).ConfigureAwait(true);
                    if (bitmap is null)
                    {
                        _logger.LogWarning("Pins: image {File} of pin {Id} can't be decoded, entry pruned", record.FileName, record.Id);
                        _persistence.Forget(record.Id);
                        continue;
                    }
                    var w = new PinnedImageWindow(bitmap, settings: _settings, editor: _editor,
                        initialBorderThickness: record.Border, logger: _windowLogger,
                        items: _items, listener: _listener, outputEncoder: _outputEncoder)
                    { PinId = record.Id };
                    w.ApplyRestoredState(record.Scale, record.Opacity, record.DpiScaleX, record.DpiScaleY, record.Locked);
                    w.ShowRestored(target.X, target.Y);
                    _persistence.TrackImage(w, alreadyPersisted: true);
                }
                else
                {
                    var v = new PinnedVideoWindow(record.SourcePath!, record.Border, muted: record.Muted,
                        logger: _videoWindowLogger)
                    { PinId = record.Id };
                    v.ApplyRestoredState(record.Scale, record.Opacity, record.DpiScaleX, record.DpiScaleY, record.Width, record.Height, record.Locked);
                    v.ShowRestored(target.X, target.Y);
                    _persistence.TrackVideo(v, alreadyPersisted: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pins: restoring pin {Id} failed, entry pruned", record.Id);
                _persistence.Forget(record.Id);
            }
        }
    }

    /// <summary>Decode a persisted PNG on a worker thread. Frozen, so the UI thread can show it.</summary>
    private static BitmapSource? DecodeFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException
                                      or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    /// <summary>Pin already-encoded image bytes (PNG / JPG / BMP / GIF first frame, anything WIC
    /// decodes), centred on screen. Used by the clipboard panel's "Pin to screen" on image
    /// entries. UI-thread only. Returns false (after a toast) when the bytes can't be decoded.</summary>
    public async Task<bool> PinImageAsync(byte[] imageBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        BitmapSource? bitmap;
        try { bitmap = DecodePng(imageBytes); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PinToScreenLauncher: image decode failed");
            bitmap = null;
        }
        if (bitmap is null)
        {
            NotifyFailure(Resources.Strings.PinToScreen_ImageUnavailable);
            return false;
        }
        return await PinBitmapAsync(bitmap, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Pin a decoded bitmap, centred on screen. UI-thread only.</summary>
    public async Task<bool> PinBitmapAsync(BitmapSource bitmap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (!bitmap.IsFrozen && bitmap.CanFreeze) bitmap.Freeze();
        var border = await PinnedImageWindow.LoadStickyBorderAsync(_settings, cancellationToken).ConfigureAwait(true);
        var w = new PinnedImageWindow(bitmap, settings: _settings, editor: _editor, initialBorderThickness: border, logger: _windowLogger,
            items: _items, listener: _listener, outputEncoder: _outputEncoder);
        ShowPin(w);
        return true;
    }

    /// <summary>Pin a video or animated GIF file: a looping <see cref="PinnedVideoWindow"/>
    /// streaming from disk. UI-thread only. A missing file fails right away; a format the OS
    /// can't decode fails asynchronously (MediaFailed). Both end in a toast.</summary>
    public async Task<bool> PinVideoAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _logger.LogWarning("PinToScreenLauncher: video file not found ({Path})", path);
            NotifyFailure(Resources.Strings.PinToScreen_VideoUnavailable);
            return false;
        }
        var border = await PinnedImageWindow.LoadStickyBorderAsync(_settings, cancellationToken).ConfigureAwait(true);
        var mutedRaw = await _settings.GetAsync(PreviewMutedSettingKey, cancellationToken).ConfigureAwait(true);
        var muted = mutedRaw != "0" && !string.Equals(mutedRaw, "false", StringComparison.OrdinalIgnoreCase);
        var w = new PinnedVideoWindow(path, border, muted: muted,
            onFailed: _ => NotifyFailure(Resources.Strings.PinToScreen_VideoUnavailable),
            logger: _videoWindowLogger);
        w.Show();
        w.Activate();
        _persistence?.TrackVideo(w);
        return true;
    }

    private void NotifyFailure(string message)
        => _notifier?.Show(Resources.Strings.Clipboard_MenuPinToScreen, message);

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
        // AutoConfirmOnFirstSelection default = false → multi-region is enabled: user can drag
        // several rects, press Enter to commit them all. We pick the per-rect PNGs from the
        // overlay's PickedMultiRegionParts (set when >1 rect is committed) so each rect lands
        // in its own pinned window at its original on-screen origin.
        var overlay = new RegionOverlayWindow();
        var region = overlay.PickRegion();
        if (region is null || region.IsEmpty)
        {
            _logger.LogInformation("Pin from screen: cancelled (empty region or Esc)");
            return;
        }

        try
        {
            var border = await PinnedImageWindow.LoadStickyBorderAsync(_settings, cancellationToken).ConfigureAwait(true);

            if (overlay.PickedMultiRegionParts is { Count: > 1 } parts)
            {
                _logger.LogInformation("Pin from screen: spawning {Count} pinned windows (multi-region)", parts.Count);
                foreach (var (px, py, png) in parts)
                {
                    var bmp = DecodePng(png);
                    if (bmp is null) continue;
                    var win = new PinnedImageWindow(bmp, initialScreenPos: (px, py),
                        settings: _settings, editor: _editor, initialBorderThickness: border, logger: _windowLogger,
                items: _items, listener: _listener, outputEncoder: _outputEncoder);
                    ShowPin(win);
                }
                return;
            }

            _logger.LogInformation("Pin from screen: region picked at ({X}, {Y}) size {W}×{H} (physical pixels)",
                region.X, region.Y, region.Width, region.Height);
            var captured = await _captureSource.CaptureAsync(region, cancellationToken).ConfigureAwait(true);
            var bitmap = DecodePng(captured.PngBytes);
            if (bitmap is null)
            {
                _logger.LogWarning("Pin from screen: bitmap decode failed");
                return;
            }
            _logger.LogInformation("Pin from screen: bitmap decoded {W}×{H} px, sticky border = {Border} DIPs",
                bitmap.PixelWidth, bitmap.PixelHeight, border);
            var w = new PinnedImageWindow(bitmap, initialScreenPos: (region.X, region.Y),
                settings: _settings, editor: _editor, initialBorderThickness: border, logger: _windowLogger,
                items: _items, listener: _listener, outputEncoder: _outputEncoder);
            ShowPin(w);
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
            var bmp = ReadClipboardImage();
            if (bmp is null) return;
            var border = await PinnedImageWindow.LoadStickyBorderAsync(_settings, cancellationToken).ConfigureAwait(true);
            var w = new PinnedImageWindow(bmp, settings: _settings, editor: _editor, initialBorderThickness: border, logger: _windowLogger,
                items: _items, listener: _listener, outputEncoder: _outputEncoder);
            ShowPin(w);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PinToScreenLauncher: failed to read clipboard image");
        }
    }

    /// <summary>The clipboard image with its alpha channel when the source provides one: the
    /// registered "PNG" format first, then a single image file copied in Explorer, and only then
    /// <see cref="System.Windows.Clipboard.GetImage"/>, which reads the DIB and loses
    /// transparency.</summary>
    private static BitmapSource? ReadClipboardImage()
    {
        if (System.Windows.Clipboard.GetData("PNG") is MemoryStream png && png.Length > 0)
        {
            var decoded = DecodePng(png.ToArray());
            if (decoded is not null) return decoded;
        }
        if (System.Windows.Clipboard.ContainsFileDropList()
            && System.Windows.Clipboard.GetFileDropList() is { Count: 1 } files
            && files[0] is { } path
            && ImageFileExtensions.Contains(Path.GetExtension(path))
            && File.Exists(path))
        {
            var decoded = DecodePng(File.ReadAllBytes(path));
            if (decoded is not null) return decoded;
        }
        if (!System.Windows.Clipboard.ContainsImage()) return null;
        var bmp = System.Windows.Clipboard.GetImage();
        bmp?.Freeze();
        return bmp;
    }

    private static readonly HashSet<string> ImageFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico",
    };

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
            var w = new PinnedImageWindow(bitmap, settings: _settings, editor: _editor, initialBorderThickness: border, logger: _windowLogger,
                items: _items, listener: _listener, outputEncoder: _outputEncoder);
            ShowPin(w);
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
        // WebP goes through Skia: the WIC WebP codec (Windows "WebP Image Extension") decodes to
        // Bgr32 and drops the alpha channel, so a transparent WebP would pin on black.
        if (IsWebP(bytes))
        {
            using var webp = SkiaSharp.SKBitmap.Decode(bytes);
            if (webp is not null) return ImageEffects.SkiaToWpfBitmap.Convert(webp);
        }
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>RIFF container with a WEBP form type.</summary>
    private static bool IsWebP(byte[] bytes)
        => bytes.Length > 12
           && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
           && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';
}
