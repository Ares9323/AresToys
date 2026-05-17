using System.Globalization;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AresToys.Core.Imaging;
using AresToys.Editor.Persistence;
using AresToys.Editor.Rendering;
using AresToys.Editor.ViewModels;
using AresToys.Editor.Views;
using AresToys.Storage.Items;
using AresToys.Storage.Settings;

namespace AresToys.App.Services;

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
    private readonly ScreenColorPickerService _screenSampler;
    private readonly ILogger<EditorLauncher> _logger;

    public EditorLauncher(
        IServiceProvider services,
        IItemStore items,
        ColorRecentsStore recentsStore,
        EditorDefaultsStore defaultsStore,
        ISettingsStore settings,
        IImageEncoder encoder,
        ScreenColorPickerService screenSampler,
        ILogger<EditorLauncher> logger)
    {
        _services = services;
        _items = items;
        _recentsStore = recentsStore;
        _defaultsStore = defaultsStore;
        _settings = settings;
        _encoder = encoder;
        _screenSampler = screenSampler;
        _logger = logger;

        // Cross-assembly handoff: AresToys.Editor doesn't reference AresToys.App / ImageEffects,
        // so the Effects toolbar button delegates to a static Func set here. The handler
        // opens our ImageEffectsWindow preloaded with the editor's source image, awaits the
        // user's "Apply to editor" click (or cancel), and returns the rendered PNG bytes.
        // The editor swaps them in via an undoable ReplaceSourceCommand.
        AresToys.Editor.Views.EditorWindow.OpenEffectsHandler = OpenEffectsAsync;
        // Trace tool delegates here too: the editor doesn't reference AresToys.AI directly so
        // we resolve the tracer from DI on demand and own the Save dialog + file IO.
        AresToys.Editor.Views.EditorWindow.TraceHandler = TraceToSvgAsync;
        // Magic Eraser tool: same cross-assembly pattern. AresToys.AI's IBackgroundRemover is
        // resolved lazily so the ONNX runtime / model load only happens when the user
        // actually clicks the button — keeps editor open latency unchanged for users who
        // never use the feature.
        AresToys.Editor.Views.EditorWindow.RemoveBackgroundHandler = RemoveBackgroundAsync;
    }

    /// <summary>Wire the swatch eyedropper to the same ScreenColorPickerOverlay magnifier the
    /// tray / wheel / trace tool / image-effects swatches all use. The picker stays modal and
    /// visible underneath; the overlay opens nested-modal on top, the user picks a screen
    /// pixel via the magnifier, the overlay closes, and we push the colour back into the
    /// picker via the continuation. Avoids the Hide()/Show() dance on the picker which broke
    /// <c>_showingAsDialog</c> on .NET 10 and crashed the subsequent OK click.
    /// Re-applied on every editor open because <c>EditorWindow.OnClosing</c> nulls the
    /// delegate as part of its cleanup.</summary>
    private void InstallScreenSamplerHandler()
    {
        ColorSwatchButton.EyedropperHandler = continuation =>
        {
            var hex = _screenSampler.SampleAtCursor();
            if (hex is not null && TryParseHex(hex, out var sampled))
                continuation(sampled);
            else
                continuation(null);
            return null;
        };
    }

    /// <summary>Parse "#RRGGBB" (the form <see cref="ScreenColorPickerService"/> emits) into
    /// a fully-opaque <see cref="AresToys.Editor.Model.ShapeColor"/>. Returns false on any
    /// malformed input so the caller treats the sample as cancelled instead of writing a
    /// garbage colour into the picker.</summary>
    private static bool TryParseHex(string hex, out AresToys.Editor.Model.ShapeColor color)
    {
        color = AresToys.Editor.Model.ShapeColor.Black;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6) return false;
        try
        {
            var r = byte.Parse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var g = byte.Parse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var b = byte.Parse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            color = new AresToys.Editor.Model.ShapeColor(255, r, g, b);
            return true;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Open the BgRemoverWindow modeless on top of the editor, await its Closed
    /// event, return the user-approved cut-out PNG (or null on cancel / failure). The window
    /// runs the U2NetP inference once and then drives all post-processing locally so slider
    /// changes / brush strokes don't re-trigger ONNX. Mirrors the modeless +
    /// TaskCompletionSource pattern used by <see cref="OpenEffectsAsync"/>.</summary>
    private Task<byte[]?> RemoveBackgroundAsync(byte[] sourceBytes, CancellationToken ct)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.InvokeAsync(() =>
        {
            try
            {
                var remover = _services.GetRequiredService<AresToys.AI.IBackgroundRemover>();
                var window = new AresToys.App.Views.BgRemoverWindow(remover, sourceBytes)
                {
                    Owner = System.Windows.Application.Current.Windows
                        .OfType<System.Windows.Window>()
                        .FirstOrDefault(w => w.IsActive),
                };
                window.Closed += (_, _) => tcs.TrySetResult(window.ResultPng);
                window.Show();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EditorLauncher: failed to open BgRemoverWindow");
                tcs.TrySetResult(null);
            }
        });

        // Cancellation cookie: if the editor host signals ct (e.g. editor closes), let the
        // awaiter unblock with null. The window itself is responsible for tearing down on
        // its own Closed; we don't proactively close it here.
        if (ct.CanBeCanceled)
        {
            ct.Register(() => tcs.TrySetResult(null));
        }
        return tcs.Task;
    }

    /// <summary>Open <see cref="ImageEffectsWindow"/> with the editor's current source bytes
    /// preloaded, await the user's interaction, return the rendered PNG (or null on cancel).
    /// Modeless Show + TaskCompletionSource on Closed mirrors the same async pattern used by
    /// EditAsync — keeps other app windows interactive while the user picks effects.</summary>
    private Task<byte[]?> OpenEffectsAsync(byte[] sourceBytes, System.Windows.Window? owner)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.InvokeAsync(() =>
        {
            try
            {
                var presetStore = _services.GetRequiredService<AresToys.Storage.ImageEffects.IImageEffectPresetStore>();
                var settingsStore = _services.GetRequiredService<AresToys.Storage.Settings.ISettingsStore>();
                var vm = new AresToys.App.ViewModels.ImageEffects.ImageEffectsViewModel(
                    AresToys.ImageEffects.ImageEffectRegistry.Default, presetStore);
                vm.LoadSourceFromBytes(sourceBytes);

                var window = new AresToys.App.Views.ImageEffectsWindow(vm, settingsStore)
                {
                    Owner = owner
                };
                window.EnableEditorMode();
                window.Closed += (_, _) =>
                {
                    // ResultBytes is non-null only when the user clicked "Apply to editor";
                    // closing via X / Esc leaves it null, which the caller treats as cancel.
                    tcs.TrySetResult(window.ResultBytes);
                };
                window.Show();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EditorLauncher: opening effects editor failed");
                tcs.TrySetResult(null);
            }
        });

        return tcs.Task;
    }

    /// <summary>Trace handler: open <see cref="AresToys.App.Views.TraceWindow"/> preloaded with
    /// the editor's source bytes, await the user's Save (or cancel), and only THEN pop a
    /// SaveFileDialog parented to the editor to write the user-confirmed SVG to disk. Mirrors
    /// the modeless Show + TaskCompletionSource-on-Closed pattern from
    /// <see cref="OpenEffectsAsync"/> — keeps the rest of the app interactive while the user
    /// dials in trace parameters. Failure modes (potrace missing, tracer crash) surface inline
    /// in the preview window's "(no output)" placeholder, so the legacy MessageBox is gone.</summary>
    /// <summary>Open the TraceWindow and await its closure. The window handles its own
    /// "Save as…" file dialog inline (no launcher coordination needed), which lets the
    /// user save multiple variants without the window closing after each save. We just
    /// keep the editor's Trace button disabled until the trace window goes away.</summary>
    private async Task TraceToSvgAsync(byte[] sourceBytes, System.Windows.Window? owner)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await dispatcher.InvokeAsync(() =>
        {
            try
            {
                var tracer = _services.GetRequiredService<AresToys.AI.IImageTracer>();
                var presetStore = _services.GetRequiredService<TracePresetStore>();
                var window = new AresToys.App.Views.TraceWindow(tracer, presetStore, sourceBytes)
                {
                    Owner = owner
                };
                window.Closed += (_, _) => tcs.TrySetResult(true);
                window.Show();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EditorLauncher: opening trace window failed");
                tcs.TrySetResult(false);
            }
        });

        await tcs.Task.ConfigureAwait(false);
    }

    public async Task OpenAsync(long itemId, CancellationToken cancellationToken)
    {
        var record = await _items.GetByIdAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (record is null) return;
        if (record.Kind is not AresToys.Core.Domain.ItemKind.Image)
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
        InstallScreenSamplerHandler();

        var defaults = await _defaultsStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        // Global "Start editor maximized" preference (Settings → Settings tab). Applies only
        // to non-pipeline opens (this method = toast / history); pipeline tasks pass their own
        // fullscreen flag through EditAsync and bypass this default. We read here before the
        // dispatcher hop so the setting hits the same UI-thread block that constructs the
        // window — easier than a second Dispatcher.InvokeAsync just to query an int setting.
        var rawStartMax = await _settings.GetAsync("app.editor_start_maximized", cancellationToken).ConfigureAwait(false);
        var startMaximized = string.Equals(rawStartMax, "true", StringComparison.OrdinalIgnoreCase);

        // Alt+click no-match fallback: "select_any" → editor's AltClickFallback = SelectAny;
        // anything else (incl. unset / "place") → Place. Same per-open read pattern as the
        // start-maximized flag above so a Settings toggle takes effect on the next editor open
        // without restart. Renamed from shift_click_no_match in 0.1.6.
        var rawAltFallback = await _settings.GetAsync("editor.alt_click_no_match", cancellationToken).ConfigureAwait(false);
        var altFallback = string.Equals(rawAltFallback, "select_any", StringComparison.OrdinalIgnoreCase)
            ? AresToys.Editor.ViewModels.AltClickFallback.SelectAny
            : AresToys.Editor.ViewModels.AltClickFallback.Place;

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
            vm.FreehandStartCapDefault = defaults.FreehandStartCap;
            vm.FreehandEndCapDefault = defaults.FreehandEndCap;
            vm.LineStartCapDefault = defaults.LineStartCap;
            vm.LineEndCapDefault = defaults.LineEndCap;
            vm.ArrowStartCapDefault = defaults.ArrowStartCap;
            vm.ArrowEndCapDefault = defaults.ArrowEndCap;
            vm.LineTipStyleDefault = defaults.LineTipStyle;
            vm.AltClickFallback = altFallback;
            vm.ResetStepCounter();
            window.ApplyLocalization(BuildEditorLabels());
            window.Owner = System.Windows.Application.Current.MainWindow;
            if (startMaximized)
            {
                // Active monitor = the one currently under the cursor. Falls back to the primary
                // when the cursor sits in a multi-monitor gap (rare). Same pattern EditAsync
                // uses for the pipeline's fullscreen flag.
                var monitor = AresToys.Capture.MonitorEnumeration.GetMonitorUnderCursor();
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
                    vm.FreehandSmoothDefault,
                    vm.FreehandStartCapDefault, vm.FreehandEndCapDefault,
                    vm.LineStartCapDefault, vm.LineEndCapDefault,
                    vm.ArrowStartCapDefault, vm.ArrowEndCapDefault,
                    vm.LineTipStyleDefault);
                byte[]? png = null;
                if (window.Saved)
                {
                    // SavedPng is captured inside OnSaveClicked BEFORE Close runs — exporting
                    // here (post-Close) hits a detached visual tree and produces blank bytes.
                    // Adorners are already hidden for the capture (ExportCanvasPng path).
                    png = window.SavedPng;
                }
                doneTcs.TrySetResult((snap, window.Saved, png));
            };
            window.Show();
        });

        var (snapshot, saved, pngBytes) = await doneTcs.Task.ConfigureAwait(false);

        await _defaultsStore.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);

        if (!saved || pngBytes is null) return;

        var bytes = await EncodeForGlobalFormatAsync(pngBytes, cancellationToken).ConfigureAwait(false);

        // Save creates a NEW history entry rather than overwriting the original — preserves
        // the captured original for safety (user preference). Source = Manual to mark it as
        // a user-derived edit; Category copied from the original so the new item lands in
        // the same popup bucket. The pre-edit record stays untouched.
        var newItem = new NewItem(
            Kind: AresToys.Core.Domain.ItemKind.Image,
            Source: AresToys.Core.Domain.ItemSource.Manual,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: bytes,
            PayloadSize: bytes.LongLength,
            Category: string.IsNullOrEmpty(record.Category) ? "Clipboard" : record.Category);
        var newId = await _items.AddAsync(newItem, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("EditorLauncher: edit saved as new item {NewId} ({Bytes} bytes), original {OriginalId} preserved",
            newId, bytes.Length, itemId);

        // Also publish to the Windows clipboard so the user can paste the edited result
        // immediately without re-clicking the item in Win+V. SuppressNext keeps the listener
        // from re-ingesting our own write as a third duplicate item.
        await dispatcher.InvokeAsync(() =>
        {
            var listener = _services.GetService<AresToys.Clipboard.IClipboardListener>();
            listener?.SuppressNext();
            ClipboardImagePublisher.SetPng(bytes);
        });
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
        InstallScreenSamplerHandler();

        var defaults = await _defaultsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        // Workflow override wins over the last-used persisted default. Bad / unknown enum
        // values fall through silently — the editor keeps the persisted last-used so a stale
        // workflow config doesn't lock the user out of a usable tool.
        var resolvedTool = defaults.Tool;
        if (!string.IsNullOrWhiteSpace(defaultTool)
            && Enum.TryParse<AresToys.Editor.Tools.EditorTool>(defaultTool, ignoreCase: true, out var parsedTool))
        {
            resolvedTool = parsedTool;
        }
        _logger.LogInformation("EditorLauncher.EditAsync: opening with tool={Tool} (lastUsed={LastUsed}, override='{Override}')",
            resolvedTool, defaults.Tool, defaultTool ?? "(none)");

        // Same alt+click fallback hydration as OpenAsync — read once per editor open so a
        // Settings toggle takes effect on the next launch without restart.
        var rawAltFallback = await _settings.GetAsync("editor.alt_click_no_match", cancellationToken).ConfigureAwait(false);
        var altFallback = string.Equals(rawAltFallback, "select_any", StringComparison.OrdinalIgnoreCase)
            ? AresToys.Editor.ViewModels.AltClickFallback.SelectAny
            : AresToys.Editor.ViewModels.AltClickFallback.Place;

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
            vm.FreehandStartCapDefault = defaults.FreehandStartCap;
            vm.FreehandEndCapDefault = defaults.FreehandEndCap;
            vm.LineStartCapDefault = defaults.LineStartCap;
            vm.LineEndCapDefault = defaults.LineEndCap;
            vm.ArrowStartCapDefault = defaults.ArrowStartCap;
            vm.ArrowEndCapDefault = defaults.ArrowEndCap;
            vm.LineTipStyleDefault = defaults.LineTipStyle;
            vm.AltClickFallback = altFallback;
            vm.ResetStepCounter();
            window.ApplyLocalization(BuildEditorLabels());
            window.Owner = System.Windows.Application.Current.MainWindow;
            if (fullscreen)
            {
                // Active monitor = the one currently under the cursor. Falls back to the primary
                // when the cursor sits in a multi-monitor gap (rare). EnableFullscreen positions
                // + maximises the editor; the canvas opens at 1:1 inside (initial-fit was changed
                // to always 1:1 — fullscreen no longer implies upscale-to-fit).
                var monitor = AresToys.Capture.MonitorEnumeration.GetMonitorUnderCursor();
                if (monitor is not null)
                    window.EnableFullscreen(monitor.X, monitor.Y, monitor.Width, monitor.Height);
            }
            else
            {
                // Non-fullscreen pipeline open: pre-size the window so the canvas hosts the image
                // at 1:1 without scrolling when it fits in the work area. Read width/height from
                // the PNG header without realising the full bitmap (BitmapDecoder lazily decodes
                // metadata only when frames aren't accessed). Tiny corruption guard around it —
                // a malformed PNG just falls back to the XAML defaults (1200×820).
                if (TryReadImagePixelSize(sourcePngBytes, out var imgW, out var imgH))
                    window.SizeToImage(imgW, imgH);
            }
            window.Closed += (_, _) =>
            {
                // Snapshot whatever the user left selected, BEFORE the window unloads — reading
                // CurrentTool / *Color from a pool thread later would race the WPF binding system.
                var snap = new EditorDefaults(vm.OutlineColor, vm.FillColor, vm.StrokeWidth, vm.CurrentTool, vm.CurrentTextStyle,
                    vm.FreehandSmoothDefault,
                    vm.FreehandStartCapDefault, vm.FreehandEndCapDefault,
                    vm.LineStartCapDefault, vm.LineEndCapDefault,
                    vm.ArrowStartCapDefault, vm.ArrowEndCapDefault,
                    vm.LineTipStyleDefault);
                byte[]? png = null;
                int w = 0, h = 0;
                if (window.Saved)
                {
                    // SavedPng is captured inside OnSaveClicked BEFORE Close runs — see
                    // OpenAsync's matching note. W/H still resolved off CanvasHost (those
                    // are read from the source bitmap's PixelWidth/Height which survive the
                    // close because the BitmapSource is the same .png the user opened with).
                    var canvasHost = (Grid)window.FindName("CanvasHost")!;
                    (w, h) = ResolveExportPixels(canvasHost);
                    png = window.SavedPng;
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

    /// <summary>Open the editor on the file at <paramref name="path"/>, and on a successful save
    /// (a) write the edited bytes back to that file AND (b) commit them to the AresToys clipboard
    /// (history + Windows clipboard publish). Cancel leaves both file and history untouched.
    /// <para>
    /// Used by toast-button "Open in editor" flows where the user wanted a quick post-capture
    /// tweak but the originating workflow didn't include an AddToHistory step — without this
    /// auto-commit the edits would disappear the moment the editor closed (file overwritten but
    /// no AresToys-clipboard reference). Mirrors the OpenAsync(itemId) save behaviour, just
    /// keyed off a path instead of an existing history row.
    /// </para></summary>
    public async Task EditPathAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.File.Exists(path))
        {
            _logger.LogInformation("EditorLauncher.EditPathAsync: file gone {Path}", path);
            return;
        }
        var sourceBytes = await System.IO.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var edited = await EditAsync(sourceBytes, cancellationToken).ConfigureAwait(false);
        if (edited is null) return; // user cancelled — file + history untouched

        await System.IO.File.WriteAllBytesAsync(path, edited, cancellationToken).ConfigureAwait(false);

        // Commit a fresh history row pointing at the just-overwritten file so the user can recall
        // the edit from the Win+V popup later. BlobRef stays the same path the toast was about;
        // SearchText falls back to the filename so search inside AresToys finds it. Source =
        // Manual because the edit was a user gesture, not a workflow step.
        var fileName = System.IO.Path.GetFileName(path);
        var newItem = new NewItem(
            Kind: AresToys.Core.Domain.ItemKind.Image,
            Source: AresToys.Core.Domain.ItemSource.Manual,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: edited,
            PayloadSize: edited.LongLength,
            BlobRef: path,
            SearchText: string.IsNullOrEmpty(fileName) ? "Image" : fileName);
        var newId = await _items.AddAsync(newItem, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("EditorLauncher.EditPathAsync: edit saved as new history item {NewId} ({Bytes} bytes) at {Path}",
            newId, edited.Length, path);

        // Push to Windows clipboard so the user can paste immediately — same trailing step as
        // OpenAsync. SuppressNext keeps the listener from re-ingesting our own write as a
        // duplicate clipboard event.
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var listener = _services.GetService<AresToys.Clipboard.IClipboardListener>();
            listener?.SuppressNext();
            ClipboardImagePublisher.SetPng(edited);
        });
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
    /// <summary>Class-level resx lookup honouring the singleton's pinned culture. Needed
    /// outside <see cref="BuildEditorLabels"/> for ad-hoc dialog strings (Save dialog title,
    /// MessageBox bodies) — the local <c>Loc</c> closure inside BuildEditorLabels can't be
    /// reached from sibling methods. Same fallback semantics: returns the key itself if the
    /// resource is missing.</summary>
    private static string LocStatic(string key)
    {
        var culture = AresToys.App.Markup.LocalizedStrings.Instance.Culture
                      ?? System.Globalization.CultureInfo.CurrentUICulture;
        return AresToys.App.Resources.Strings.ResourceManager.GetString(key, culture) ?? key;
    }

    private static Dictionary<string, string> BuildEditorLabels()
    {
        var culture = AresToys.App.Markup.LocalizedStrings.Instance.Culture
                      ?? System.Globalization.CultureInfo.CurrentUICulture;
        string Loc(string key) =>
            AresToys.App.Resources.Strings.ResourceManager.GetString(key, culture) ?? key;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TitleBar"]                = Loc("Editor_TitleBar"),
            ["PropertiesTitleFormat"]   = Loc("Editor_PropertiesTitleFormat"),
            ["SharedProperties"]        = Loc("Editor_SharedProperties"),
            ["Properties"]              = Loc("Editor_Properties"),
            ["NoSelection"]             = Loc("Editor_NoSelection"),
            ["DefaultProperties"]       = Loc("Editor_DefaultProperties"),
            ["TooltipSave"]             = Loc("Editor_TooltipSave"),
            ["TooltipSaveAs"]           = Loc("Editor_TooltipSaveAs"),
            ["TooltipCancel"]           = Loc("Editor_TooltipCancel"),
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
            ["StartCap"]                = Loc("Editor_StartCap"),
            ["EndCap"]                  = Loc("Editor_EndCap"),
            ["TipStyle"]                = Loc("Editor_TipStyle"),
            ["TipStyleShareXCurve"]     = Loc("Editor_TipStyle_ShareXCurve"),
            ["TipStyleFilledTriangle"]  = Loc("Editor_TipStyle_FilledTriangle"),
            ["TooltipSmoothStroke"]     = Loc("Editor_TooltipSmoothStroke"),
            ["TooltipStartCap"]         = Loc("Editor_TooltipStartCap"),
            ["TooltipEndCap"]           = Loc("Editor_TooltipEndCap"),
            ["TooltipTipStyle"]         = Loc("Editor_TooltipTipStyle"),
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
            ["Tool_Trace"]              = Loc("Editor_Tool_Trace"),
            ["Tool_MagicEraser"]        = Loc("Editor_Tool_MagicEraser"),
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

    /// <summary>Read just the pixel dimensions out of a PNG/JPEG/etc. byte buffer without
    /// allocating the full decoded bitmap. WPF's <see cref="System.Windows.Media.Imaging.BitmapDecoder"/>
    /// reads metadata eagerly but only realises pixels on access; touching <c>PixelWidth</c>/
    /// <c>PixelHeight</c> on the first frame is enough. Returns false on any malformed input
    /// so the caller can fall through to its default sizing.</summary>
    private static bool TryReadImagePixelSize(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var ms = new System.IO.MemoryStream(bytes);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                ms,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return false;
            var frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
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
