using System.Globalization;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShareQ.Core.Imaging;
using ShareQ.Editor.Persistence;
using ShareQ.Editor.Rendering;
using ShareQ.Editor.ViewModels;
using ShareQ.Editor.Views;
using ShareQ.Storage.Items;
using ShareQ.Storage.Settings;

namespace ShareQ.App.Services;

public sealed class EditorLauncher
{
    private const string ImageFormatKey = "capture.image_format";
    private const string JpegQualityKey = "capture.jpeg_quality";

    private readonly IServiceProvider _services;
    private readonly IItemStore _items;
    private readonly ColorRecentsStore _recentsStore;
    private readonly EditorDefaultsStore _defaultsStore;
    private readonly ISettingsStore _settings;
    private readonly IImageEncoder _encoder;
    private readonly ILogger<EditorLauncher> _logger;

    public EditorLauncher(
        IServiceProvider services,
        IItemStore items,
        ColorRecentsStore recentsStore,
        EditorDefaultsStore defaultsStore,
        ISettingsStore settings,
        IImageEncoder encoder,
        ILogger<EditorLauncher> logger)
    {
        _services = services;
        _items = items;
        _recentsStore = recentsStore;
        _defaultsStore = defaultsStore;
        _settings = settings;
        _encoder = encoder;
        _logger = logger;
    }

    public async Task OpenAsync(long itemId, CancellationToken cancellationToken)
    {
        var record = await _items.GetByIdAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (record is null) return;
        if (record.Kind is not ShareQ.Core.Domain.ItemKind.Image)
        {
            _logger.LogInformation("EditorLauncher: skipping non-image item {Id}", itemId);
            return;
        }
        // Defensive: legacy items recorded BEFORE we had ItemKind.Video are stored as Image but
        // contain mp4/gif bytes. Detect via BlobRef extension and bail out instead of crashing
        // BitmapImage decode on non-image content.
        if (!string.IsNullOrEmpty(record.BlobRef))
        {
            var ext = System.IO.Path.GetExtension(record.BlobRef).ToLowerInvariant();
            if (ext is ".mp4" or ".webm" or ".mkv" or ".gif" or ".webp" or ".mov")
            {
                _logger.LogInformation("EditorLauncher: item {Id} has video/animation extension {Ext}; skipping", itemId, ext);
                return;
            }
        }

        var recents = await _recentsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        ColorSwatchButton.CurrentRecents = recents;
        ColorSwatchButton.OnColorPicked = c =>
        {
            _ = _recentsStore.PushAsync(c, CancellationToken.None);
        };

        var defaults = await _defaultsStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        // Global "Start editor maximized" preference (Settings → Settings tab). Applies only
        // to non-pipeline opens (this method = toast / history); pipeline tasks pass their own
        // fullscreen flag through EditAsync and bypass this default. We read here before the
        // dispatcher hop so the setting hits the same UI-thread block that constructs the
        // window — easier than a second Dispatcher.InvokeAsync just to query an int setting.
        var rawStartMax = await _settings.GetAsync("app.editor_start_maximized", cancellationToken).ConfigureAwait(false);
        var startMaximized = string.Equals(rawStartMax, "true", StringComparison.OrdinalIgnoreCase);

        // Modeless Show() instead of ShowDialog so the editor doesn't take the app-wide modal
        // lock (toast → click → editor used to freeze MainWindow / ClipboardWindow / tray
        // dialogs until close). Same pattern EditAsync uses for pipeline opens; the
        // TaskCompletionSource on Closed restores the "OpenAsync returns when the editor is
        // dismissed" semantics so callers see no behavioural change.
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        var doneTcs = new TaskCompletionSource<(EditorDefaults Snapshot, bool Saved, byte[]? Png)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await dispatcher.InvokeAsync(() =>
        {
            var window = _services.GetRequiredService<EditorWindow>();
            var vm = (EditorViewModel)window.DataContext;
            vm.SourcePngBytes = record.Payload.ToArray();
            vm.EditingItemId = itemId;
            vm.OutlineColor = defaults.Outline;
            vm.FillColor = defaults.Fill;
            vm.StrokeWidth = defaults.StrokeWidth;
            vm.CurrentTool = defaults.Tool;
            vm.CurrentTextStyle = defaults.TextStyle;
            vm.FreehandSmoothDefault = defaults.FreehandSmooth;
            vm.FreehandEndArrowDefault = defaults.FreehandEndArrow;
            vm.ResetStepCounter();
            window.ApplyLocalization(BuildEditorLabels());
            window.Owner = System.Windows.Application.Current.MainWindow;
            if (startMaximized)
            {
                // Active monitor = the one currently under the cursor. Falls back to the primary
                // when the cursor sits in a multi-monitor gap (rare). Same pattern EditAsync
                // uses for the pipeline's fullscreen flag.
                var monitor = ShareQ.Capture.MonitorEnumeration.GetMonitorUnderCursor();
                if (monitor is not null)
                    window.EnableFullscreen(monitor.X, monitor.Y, monitor.Width, monitor.Height);
            }
            window.Closed += (_, _) =>
            {
                // Snapshot ON the UI thread before the window unloads — reading VM state from
                // a pool thread later would race the WPF binding system. Same precaution as
                // EditAsync.
                var snap = new EditorDefaults(vm.OutlineColor, vm.FillColor, vm.StrokeWidth,
                    vm.CurrentTool, vm.CurrentTextStyle,
                    vm.FreehandSmoothDefault, vm.FreehandEndArrowDefault);
                byte[]? png = null;
                if (window.Saved)
                {
                    var canvasHost = (Grid)window.FindName("CanvasHost")!;
                    var (exportW, exportH) = ResolveExportPixels(canvasHost);
                    png = CanvasPngExporter.Export(canvasHost, exportW, exportH);
                }
                doneTcs.TrySetResult((snap, window.Saved, png));
            };
            window.Show();
        });

        var (snapshot, saved, pngBytes) = await doneTcs.Task.ConfigureAwait(false);

        await _defaultsStore.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);

        if (!saved || pngBytes is null) return;

        var bytes = await EncodeForGlobalFormatAsync(pngBytes, cancellationToken).ConfigureAwait(false);
        await _items.UpdatePayloadAsync(itemId, bytes, bytes.LongLength, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("EditorLauncher: saved {Bytes} bytes back to item {Id}", bytes.Length, itemId);
    }

    /// <summary>
    /// Open the editor on raw PNG bytes (no <c>ItemStore</c> round-trip). Used by the pipeline's
    /// "Open editor before upload" step so the user can annotate the capture before subsequent
    /// steps (upload, copy-image, save) see it. Returns the edited PNG bytes on save, or null
    /// when the user cancelled — in that case the caller keeps the original bytes.
    /// </summary>
    public Task<byte[]?> EditAsync(byte[] sourcePngBytes, CancellationToken cancellationToken)
        => EditAsync(sourcePngBytes, fullscreen: false, defaultTool: null, cancellationToken);

    /// <summary>Same flow as the simple <see cref="EditAsync(byte[], CancellationToken)"/>,
    /// plus optional pipeline knobs: <paramref name="fullscreen"/> places the editor on the
    /// active monitor and forces fit-to-viewport, and <paramref name="defaultTool"/> preselects
    /// a specific drawing tool ("Crop", "Rectangle", …) on open — winning over whatever the
    /// user last left selected. <paramref name="defaultTool"/> = null / empty falls back to
    /// the persisted <see cref="EditorDefaults"/> ("last used" semantics).</summary>
    public async Task<byte[]?> EditAsync(byte[] sourcePngBytes, bool fullscreen, string? defaultTool, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePngBytes);
        if (sourcePngBytes.Length == 0) return null;

        var recents = await _recentsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        ColorSwatchButton.CurrentRecents = recents;
        ColorSwatchButton.OnColorPicked = c => _ = _recentsStore.PushAsync(c, CancellationToken.None);

        var defaults = await _defaultsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        // Workflow override wins over the last-used persisted default. Bad / unknown enum
        // values fall through silently — the editor keeps the persisted last-used so a stale
        // workflow config doesn't lock the user out of a usable tool.
        var resolvedTool = defaults.Tool;
        if (!string.IsNullOrWhiteSpace(defaultTool)
            && Enum.TryParse<ShareQ.Editor.Tools.EditorTool>(defaultTool, ignoreCase: true, out var parsedTool))
        {
            resolvedTool = parsedTool;
        }
        _logger.LogInformation("EditorLauncher.EditAsync: opening with tool={Tool} (lastUsed={LastUsed}, override='{Override}')",
            resolvedTool, defaults.Tool, defaultTool ?? "(none)");

        // Editor is WPF — must be created on the UI thread. We use Show() (modeless) instead
        // of ShowDialog() so the user can keep multiple editors open in parallel: rapid-fire
        // PrintScreen / region-capture invocations each produce their own independent window.
        // ShowDialog used to disable every other window in the app (modal stack), which made
        // only the topmost editor usable.
        //
        // The TaskCompletionSource is what restores the "EditAsync returns when the user
        // saves or cancels" semantics: hooked off the Closed event, completed with the
        // editor's snapshot at that point.
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        var snapshotTcs = new TaskCompletionSource<(EditorDefaults Snapshot, bool Saved, byte[]? Png, int W, int H)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await dispatcher.InvokeAsync(() =>
        {
            var window = _services.GetRequiredService<EditorWindow>();
            var vm = (EditorViewModel)window.DataContext;
            vm.SourcePngBytes = sourcePngBytes;
            vm.EditingItemId = 0; // synthetic — there's no DB item to write back to
            vm.OutlineColor = defaults.Outline;
            vm.FillColor = defaults.Fill;
            vm.StrokeWidth = defaults.StrokeWidth;
            vm.CurrentTool = resolvedTool;
            vm.CurrentTextStyle = defaults.TextStyle;
            vm.FreehandSmoothDefault = defaults.FreehandSmooth;
            vm.FreehandEndArrowDefault = defaults.FreehandEndArrow;
            vm.ResetStepCounter();
            window.ApplyLocalization(BuildEditorLabels());
            window.Owner = System.Windows.Application.Current.MainWindow;
            if (fullscreen)
            {
                // Active monitor = the one currently under the cursor. Falls back to the primary
                // when the cursor sits in a multi-monitor gap (rare). EnableFullscreen positions
                // + maximises the editor and flips its initial fit pass to "fit-to-viewport".
                var monitor = ShareQ.Capture.MonitorEnumeration.GetMonitorUnderCursor();
                if (monitor is not null)
                    window.EnableFullscreen(monitor.X, monitor.Y, monitor.Width, monitor.Height);
            }
            window.Closed += (_, _) =>
            {
                // Snapshot whatever the user left selected, BEFORE the window unloads — reading
                // CurrentTool / *Color from a pool thread later would race the WPF binding system.
                var snap = new EditorDefaults(vm.OutlineColor, vm.FillColor, vm.StrokeWidth, vm.CurrentTool, vm.CurrentTextStyle,
                    vm.FreehandSmoothDefault, vm.FreehandEndArrowDefault);
                byte[]? png = null;
                int w = 0, h = 0;
                if (window.Saved)
                {
                    var canvasHost = (Grid)window.FindName("CanvasHost")!;
                    (w, h) = ResolveExportPixels(canvasHost);
                    png = CanvasPngExporter.Export(canvasHost, w, h);
                }
                snapshotTcs.TrySetResult((snap, window.Saved, png, w, h));
            };
            window.Show();
        }).Task.ConfigureAwait(false);

        var (snapshot, wasSaved, exportedPng, exportW, exportH) = await snapshotTcs.Task.ConfigureAwait(false);

        // Persist the editor defaults BEFORE returning so a subsequent EditAsync call (or any
        // other surface that reads via LoadAsync) sees the new value.
        await _defaultsStore.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("EditorLauncher.EditAsync: persisted tool={Tool} on close", snapshot.Tool);

        if (!wasSaved || exportedPng is null) return null;
        // EditAsync feeds the pipeline (upload / save-to-file / etc.), so the bytes need to
        // match the globally-chosen format the same way capture does — otherwise a JPEG user
        // would see PNGs come out of "Open editor before upload" while every other capture
        // path produces JPEGs.
        var edited = await EncodeForGlobalFormatAsync(exportedPng, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("EditorLauncher.EditAsync: returning {Bytes} edited bytes ({W}×{H})", edited.Length, exportW, exportH);
        return edited;
    }

    /// <summary>Re-encode the editor's PNG export into the user's globally-configured image
    /// format. Mirrors <see cref="CaptureCoordinator"/>'s post-capture step so a screenshot
    /// goes through the same pipeline regardless of whether it was edited in between. PNG is
    /// the short-circuit (default) — everything else round-trips through <see cref="IImageEncoder"/>;
    /// failures fall back to the original PNG so a misconfiguration doesn't break the save flow.</summary>
    private async Task<byte[]> EncodeForGlobalFormatAsync(byte[] pngBytes, CancellationToken cancellationToken)
    {
        var rawFormat = await _settings.GetAsync(ImageFormatKey, cancellationToken).ConfigureAwait(false);
        var format = ImageFormatExtensions.TryParse(rawFormat) ?? ImageFormat.Png;
        if (format == ImageFormat.Png) return pngBytes;

        var rawQuality = await _settings.GetAsync(JpegQualityKey, cancellationToken).ConfigureAwait(false);
        var quality = int.TryParse(rawQuality, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q)
            ? Math.Clamp(q, 1, 100) : 90;
        try
        {
            return _encoder.Encode(pngBytes, format, quality);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EditorLauncher: re-encode to {Format} failed — keeping PNG", format);
            return pngBytes;
        }
    }

    /// <summary>Resolve every translatable string used by <see cref="EditorWindow"/> and stuff
    /// it into a dictionary the editor can apply via <c>ApplyLocalization</c>. The editor
    /// assembly can't reach our resx, so the host is the bridge — same handoff pattern used
    /// for <c>ColorPickerWindow</c>. Resolution honours the singleton's pinned culture so
    /// language switches at runtime take effect on the next editor open without a restart.</summary>
    private static Dictionary<string, string> BuildEditorLabels()
    {
        var culture = ShareQ.App.Markup.LocalizedStrings.Instance.Culture
                      ?? System.Globalization.CultureInfo.CurrentUICulture;
        string Loc(string key) =>
            ShareQ.App.Resources.Strings.ResourceManager.GetString(key, culture) ?? key;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TitleBar"]                = Loc("Editor_TitleBar"),
            ["PropertiesTitleFormat"]   = Loc("Editor_PropertiesTitleFormat"),
            ["SharedProperties"]        = Loc("Editor_SharedProperties"),
            ["Properties"]              = Loc("Editor_Properties"),
            ["NoSelection"]             = Loc("Editor_NoSelection"),
            ["DefaultProperties"]       = Loc("Editor_DefaultProperties"),
            ["Save"]                    = Loc("Editor_Save"),
            ["SaveAs"]                  = Loc("Editor_SaveAs"),
            ["Cancel"]                  = Loc("Common_Cancel"),
            ["TooltipSave"]             = Loc("Editor_TooltipSave"),
            ["TooltipSaveAs"]           = Loc("Editor_TooltipSaveAs"),
            ["Outline"]                 = Loc("Editor_Outline"),
            ["Fill"]                    = Loc("Editor_Fill"),
            ["Text"]                    = Loc("Editor_Text"),
            ["Stroke"]                  = Loc("Editor_Stroke"),
            ["TextColor"]               = Loc("Editor_TextColor"),
            ["Font"]                    = Loc("Editor_Font"),
            ["Size"]                    = Loc("Editor_Size"),
            ["Bold"]                    = Loc("Editor_Bold"),
            ["Italic"]                  = Loc("Editor_Italic"),
            ["Alignment"]               = Loc("Editor_Alignment"),
            ["Rotation"]                = Loc("Editor_Rotation"),
            ["Effect"]                  = Loc("Editor_Effect"),
            ["BlurRadius"]              = Loc("Editor_BlurRadius"),
            ["PixelBlockSize"]          = Loc("Editor_PixelBlockSize"),
            ["SpotlightDim"]            = Loc("Editor_SpotlightDim"),
            ["SpotlightBlur"]           = Loc("Editor_SpotlightBlur"),
            ["EdgeBlur"]                = Loc("Editor_EdgeBlur"),
            ["SmoothStroke"]            = Loc("Editor_SmoothStroke"),
            ["EndArrow"]                = Loc("Editor_EndArrow"),
            ["TooltipSmoothStroke"]     = Loc("Editor_TooltipSmoothStroke"),
            ["TooltipEndArrow"]         = Loc("Editor_TooltipEndArrow"),
            ["ApplyToSelected"]         = Loc("Editor_ApplyToSelected"),
            ["TooltipApplyToSelected"]  = Loc("Editor_TooltipApplyToSelected"),
            ["SetAsDefault"]            = Loc("Editor_SetAsDefault"),
            ["TooltipSetAsDefault"]     = Loc("Editor_TooltipSetAsDefault"),
            ["Undo"]                    = Loc("Editor_Undo"),
            ["Redo"]                    = Loc("Editor_Redo"),
            ["Tool_Select"]             = Loc("Editor_Tool_Select"),
            ["Tool_Rectangle"]          = Loc("Editor_Tool_Rectangle"),
            ["Tool_Ellipse"]            = Loc("Editor_Tool_Ellipse"),
            ["Tool_Line"]               = Loc("Editor_Tool_Line"),
            ["Tool_Arrow"]              = Loc("Editor_Tool_Arrow"),
            ["Tool_Freehand"]           = Loc("Editor_Tool_Freehand"),
            ["Tool_Text"]               = Loc("Editor_Tool_Text"),
            ["Tool_Step"]               = Loc("Editor_Tool_Step"),
            ["Tool_Image"]              = Loc("Editor_Tool_Image"),
            ["Tool_Blur"]               = Loc("Editor_Tool_Blur"),
            ["Tool_Pixelate"]           = Loc("Editor_Tool_Pixelate"),
            ["Tool_Spotlight"]          = Loc("Editor_Tool_Spotlight"),
            ["Tool_SmartEraser"]        = Loc("Editor_Tool_SmartEraser"),
            ["Tool_Crop"]               = Loc("Editor_Tool_Crop"),
            ["Tool_Resize"]             = Loc("Editor_Tool_Resize"),
            ["Shape_Rectangle"]         = Loc("Editor_Shape_Rectangle"),
            ["Shape_Ellipse"]           = Loc("Editor_Shape_Ellipse"),
            ["Shape_Arrow"]             = Loc("Editor_Shape_Arrow"),
            ["Shape_Line"]              = Loc("Editor_Shape_Line"),
            ["Shape_Freehand"]          = Loc("Editor_Shape_Freehand"),
            ["Shape_Text"]              = Loc("Editor_Shape_Text"),
            ["Shape_StepCounter"]       = Loc("Editor_Shape_StepCounter"),
            ["Shape_Image"]             = Loc("Editor_Shape_Image"),
            ["Shape_Blur"]              = Loc("Editor_Shape_Blur"),
            ["Shape_Pixelate"]          = Loc("Editor_Shape_Pixelate"),
            ["Shape_Spotlight"]         = Loc("Editor_Shape_Spotlight"),
            ["Shape_SmartEraser"]       = Loc("Editor_Shape_SmartEraser"),
        };
    }

    /// <summary>Find the source bitmap inside the editor's <c>CanvasHost</c> Grid and return its
    /// pixel dimensions — the canonical export size. Falls back to the host's ActualWidth/Height
    /// only when the source can't be located (defensive, shouldn't happen in normal flow).</summary>
    private static (int W, int H) ResolveExportPixels(System.Windows.Controls.Grid canvasHost)
    {
        foreach (var child in canvasHost.Children)
        {
            if (child is System.Windows.Controls.Image img &&
                img.Source is System.Windows.Media.Imaging.BitmapSource src)
            {
                return (src.PixelWidth, src.PixelHeight);
            }
        }
        return ((int)Math.Round(canvasHost.ActualWidth), (int)Math.Round(canvasHost.ActualHeight));
    }
}
