using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using AresToys.App.Services;
using AresToys.App.Views;
using AresToys.Clipboard;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Pops a small always-on-top window with the captured image pinned to the screen — useful for
/// keeping a reference visible while working in another app. Reads <c>bag.payload_bytes</c>
/// (raw image bytes from a capture step). The window is independent of the workflow lifetime:
/// once shown it stays put until the user closes it.
/// </summary>
public sealed class PinToScreenTask : IPipelineTask
{
    public const string TaskId = "arestoys.pin-to-screen";

    private readonly ISettingsStore _settings;
    private readonly EditorLauncher _editor;
    private readonly IItemStore _items;
    private readonly CaptureImageOutputService _outputEncoder;
    private readonly IClipboardListener? _listener;
    private readonly ILogger<PinToScreenTask> _logger;
    private readonly ILogger<PinnedImageWindow> _windowLogger;

    public PinToScreenTask(
        ISettingsStore settings,
        EditorLauncher editor,
        IItemStore items,
        CaptureImageOutputService outputEncoder,
        ILogger<PinToScreenTask> logger,
        ILogger<PinnedImageWindow> windowLogger,
        IClipboardListener? listener = null)
    {
        _settings = settings;
        _editor = editor;
        _items = items;
        _outputEncoder = outputEncoder;
        _listener = listener;
        _logger = logger;
        _windowLogger = windowLogger;
    }

    public string Id => TaskId;
    public string DisplayName => "Pin to screen";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        // Read sticky border off the UI thread first; the window ctor expects it synchronously
        // so it can place itself in one pass without an async race vs. WPF's first paint.
        var border = await PinnedImageWindow.LoadStickyBorderAsync(_settings, cancellationToken).ConfigureAwait(false);

        // Multi-region branch: when the capture step committed >1 rects, spawn one pinned
        // window per rect at its own on-screen origin. Pre-0.1.23 the bag only carried the
        // bbox-sized composite (with a transparent halo), which produced one giant pin that
        // also covered the empty area between the rects — opposite of what the user wanted.
        if (context.Bag.TryGetValue(PipelineBagKeys.MultiRegionParts, out var partsObj)
            && partsObj is IReadOnlyList<(int X, int Y, byte[] Png)> parts
            && parts.Count > 0)
        {
            _logger.LogInformation("PinToScreen: spawning {Count} independent windows (multi-region split)", parts.Count);
            foreach (var (px, py, png) in parts)
            {
                var bmp = TryDecode(png);
                if (bmp is null) continue;
                SpawnPin(bmp, (px, py), border);
            }
            return;
        }

        if (!context.Bag.TryGetValue(PipelineBagKeys.PayloadBytes, out var raw) || raw is not byte[] bytes)
        {
            _logger.LogWarning("PinToScreenTask: bag.payload_bytes missing; skipping");
            return;
        }
        var bitmap = TryDecode(bytes);
        if (bitmap is null) return;

        // Did a previous step (CaptureRegionTask, monitor capture, …) record where the bitmap
        // came from? If yes, replay the same on-screen origin so the pinned window covers the
        // captured pixels in place — the ShareX-style "stay where you grabbed" behaviour.
        (int X, int Y)? screenPos = null;
        if (context.Bag.TryGetValue(PipelineBagKeys.CaptureScreenPos, out var posObj) && posObj is ValueTuple<int, int> pos)
        {
            screenPos = pos;
            _logger.LogInformation("PinToScreen: bag has screen pos ({X}, {Y}) — pin will land there", pos.Item1, pos.Item2);
        }
        else
        {
            _logger.LogInformation("PinToScreen: no screen pos in bag — falling back to active-monitor centring");
        }

        SpawnPin(bitmap, screenPos, border);
    }

    private BitmapSource? TryDecode(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PinToScreenTask: failed to decode payload as image");
            return null;
        }
    }

    private void SpawnPin(BitmapSource bitmap, (int X, int Y)? screenPos, int border)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // No Owner = independent top-level window. Topmost survives the workflow's lifetime.
            // Activate so it grabs focus even when triggered from tray (MainWindow hidden).
            var w = new PinnedImageWindow(
                bitmap,
                initialScreenPos: screenPos,
                settings: _settings,
                editor: _editor,
                items: _items,
                listener: _listener,
                outputEncoder: _outputEncoder,
                initialBorderThickness: border,
                logger: _windowLogger);
            w.ShowAtCapturedPixel();
        });
    }
}
