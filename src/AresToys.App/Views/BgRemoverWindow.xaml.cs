using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AresToys.AI;
using AresToys.App.Services;
using AresToys.App.ViewModels;
using SkiaSharp;

namespace AresToys.App.Views;

/// <summary>Illustrator-style "remove background" preview. Constructed with source PNG
/// bytes; emits <see cref="ResultPng"/> on Apply. Modeless — host opens via <c>Show()</c>
/// and awaits <see cref="System.Windows.Window.Closed"/> through a <c>TaskCompletionSource</c>
/// (mirrors <c>TraceWindow</c> / <c>ImageEffectsWindow</c>'s editor handoff).
///
/// Pipeline:
/// <list type="number">
///   <item>On Loaded: run U2NetP inference once. Cache the saliency mask.</item>
///   <item>On slider change: rebuild processed mask + composite (no ONNX rerun).</item>
///   <item>On brush stroke: modify brush overlay layer, recomposite (mask cache reused).</item>
///   <item>On Apply: encode the current composite as PNG, return via <see cref="ResultPng"/>.</item>
/// </list>
/// State sequencing: <c>_sourceBitmap</c> + <c>_rawMaskBitmap</c> are immutable after init;
/// <c>_brushOverlayBitmap</c> grows from user paint; the displayed preview is the result of
/// running <see cref="BgRemovalProcessor.BuildComposite"/> over those three.</summary>
// CA1001: the SKBitmap fields are disposed deterministically in OnClosed (the only
// "lifecycle event" a Window meaningfully has). Implementing IDisposable on a Window class
// that's already managed by WPF lifecycle would just add a redundant API surface.
#pragma warning disable CA1001
public sealed partial class BgRemoverWindow : Wpf.Ui.Controls.FluentWindow
#pragma warning restore CA1001
{
    private readonly IBackgroundRemover _remover;
    private readonly byte[] _sourcePng;
    private readonly BgRemoverViewModel _vm;

    private SKBitmap? _sourceBitmap;
    private SKBitmap? _rawMaskBitmap;
    /// <summary>Cached processed mask = rawMask after threshold/feather/edge offset applied.
    /// Recomputed only when a slider changes, NOT on every brush stroke — this is the cache
    /// that keeps brush feedback smooth. Without it the threshold + Skia blur + threshold
    /// passes ran on every mouse move, eating ~200-500 ms per frame on 1080p sources.</summary>
    private SKBitmap? _processedMaskBitmap;
    /// <summary>Persistent brush overlay — accumulates Add/Remove from completed strokes.
    /// Encoding: R = Add strength, G = Remove strength, A = max(R,G). Two channels keep
    /// Add and Remove independent so a Remove stroke after an Add stroke at the same pixel
    /// correctly lowers alpha (the previous single-channel encoding had Lighten/SrcOver
    /// "max-out" the existing R, ignoring the new Remove).</summary>
    private SKBitmap? _brushOverlayBitmap;
    /// <summary>Transient overlay holding only the IN-FLIGHT stroke. Cleared at mouse-down,
    /// painted into during the drag with Lighten blend (caps each pixel at the gradient
    /// peak, so falloff is preserved end-to-end), merged into <see cref="_brushOverlayBitmap"/>
    /// at mouse-up via channel-wise SrcOver-like buildup. Without this split, dense overlapping
    /// stamps along a continuous drag built up to ~255 alpha even at low hardness — the
    /// gradient was only visible at the very lateral edges of the swept region.</summary>
    private SKBitmap? _strokeBufferBitmap;
    private CancellationTokenSource? _refreshCts;
    /// <summary>Auto-hides the centred brush-cursor preview after a slider tweak. See
    /// <see cref="ShowBrushCursorAtCenter"/> for why.</summary>
    private System.Windows.Threading.DispatcherTimer? _centredCursorHideTimer;
    /// <summary>True while the user is actively dragging a "heavy" slider (Threshold /
    /// Feather / Edge offset). The VM property still updates in real-time so the slider
    /// thumb + textbox stay in sync, but we skip the Skia pipeline rerun until the thumb
    /// is released — otherwise a fast drag spammed 30+ blur+threshold passes for values
    /// the user never wanted to land on.</summary>
    private bool _draggingHeavySlider;

    /// <summary>Pre-allocated WriteableBitmap for the cutout preview. Allocated once at the
    /// source's dimensions; every refresh writes pixel data into its back buffer in-place
    /// instead of building a fresh BitmapImage / WriteableBitmap each frame.</summary>
    private WriteableBitmap? _previewWriteable;
    /// <summary>Reused BGRA byte buffer for <see cref="BgRemovalProcessor.CompositeIntoBuffer"/>.
    /// Same lifetime as <see cref="_previewWriteable"/>; reuse skips allocator overhead.</summary>
    private byte[]? _compositeBuffer;

    /// <summary>Brush drag state. Null when not dragging; otherwise holds the mask-space
    /// coordinates of the previous mouse sample so we can interpolate strokes between low-
    /// frequency mouse events (without this, fast drags miss painted segments and leave
    /// a dotted trail instead of a continuous stroke).</summary>
    private SKPoint? _lastBrushPoint;

    /// <summary>Pan-drag state. Set when middle-button starts a pan gesture; null otherwise.
    /// Records the mouse anchor in viewport coords + the translation values at drag start
    /// so the move handler can compute deltas without integrating frame-to-frame error.</summary>
    private Point? _panStart;
    private double _panStartTx, _panStartTy;

    /// <summary>Last mouse position in CutoutHost coords. Used to re-center the brush cursor
    /// when its size changes via Shift+Wheel — without this, the cursor ring stays at the
    /// previous position until the user moves the mouse, which feels broken.</summary>
    private Point? _lastMouseOnHost;

    /// <summary>Undo stack of brush-overlay snapshots. Each entry is a copy of the overlay
    /// pixel buffer at the moment a stroke (or Reset) started; popping restores the previous
    /// state. Capped at <see cref="MaxUndoLevels"/> to bound memory — the oldest snapshot
    /// is dropped when a new one would exceed the cap.</summary>
    private readonly LinkedList<byte[]> _undoStack = new();
    private const int MaxUndoLevels = 20;


    /// <summary>Final PNG bytes set on Apply — null when the user cancelled. Host reads this
    /// after the window closes to decide whether to swap the editor's source.</summary>
    public byte[]? ResultPng { get; private set; }

    public BgRemoverWindow(IBackgroundRemover remover, byte[] sourcePng)
    {
        _remover = remover;
        _sourcePng = sourcePng;
        _vm = new BgRemoverViewModel { StatusText = string.Empty };
        InitializeComponent();
        DataContext = _vm;

        // Show the source on the left immediately so the user has visual feedback while
        // the model warms up. The right pane stays empty until inference finishes.
        SourcePreview.Source = LoadBitmapImage(sourcePng);

        _vm.PropertyChanged += OnVmPropertyChanged;
        Loaded += async (_, _) => await InitializeAsync().ConfigureAwait(true);
        Closed += OnClosed;
        // Window resize → recompute display scale for the brush cursor so its visible
        // diameter stays accurate to the brush's source-pixel radius across zoom levels.
        CutoutHost.SizeChanged += (_, _) => UpdateBrushCursor();

        // Window-level keyboard: X toggles brush mode, Ctrl+Z undoes the last stroke,
        // Alt held redraws the cursor in the inverted-mode colour. Ctrl+Z is on PREVIEW
        // (tunnel) so it pre-empts any focused TextBox's built-in undo handler — without
        // that, clicking into the size/hardness textbox and pressing Ctrl+Z just no-op'd
        // the textbox's empty history, leaving brush strokes un-undoable.
        PreviewKeyDown += OnWindowPreviewKeyDown;
        KeyDown += OnWindowKeyDown;
        KeyUp += OnWindowKeyUp;
        Focusable = true;
    }

    private async Task InitializeAsync()
    {
        _vm.IsInferenceRunning = true;
        _vm.StatusText = LocalizedStatus("BgRemover_StatusRunning");
        try
        {
            _sourceBitmap = SKBitmap.Decode(_sourcePng);
            if (_sourceBitmap is null)
            {
                _vm.StatusText = LocalizedStatus("BgRemover_StatusDecodeFailed");
                return;
            }
            // Allocate the brush overlay AND stroke buffer at source dims. Both fully
            // transparent (= no override anywhere). The stroke buffer is reused across
            // strokes via Erase() at mouse-down rather than re-allocating.
            _brushOverlayBitmap = new SKBitmap(_sourceBitmap.Width, _sourceBitmap.Height,
                SKColorType.Bgra8888, SKAlphaType.Premul);
            _brushOverlayBitmap.Erase(SKColors.Transparent);
            _strokeBufferBitmap = new SKBitmap(_sourceBitmap.Width, _sourceBitmap.Height,
                SKColorType.Bgra8888, SKAlphaType.Premul);
            _strokeBufferBitmap.Erase(SKColors.Transparent);

            // Run U2NetP and grab just the saliency mask. Slow first call (session warmup
            // ~150 ms); subsequent calls within the same process are inference-only.
            var maskPng = await _remover.ExtractAlphaMaskAsync(_sourcePng, CancellationToken.None)
                .ConfigureAwait(true);
            if (maskPng is null || maskPng.Length == 0)
            {
                _vm.StatusText = LocalizedStatus("BgRemover_StatusModelUnavailable");
                return;
            }
            _rawMaskBitmap = SKBitmap.Decode(maskPng);
            if (_rawMaskBitmap is null)
            {
                _vm.StatusText = LocalizedStatus("BgRemover_StatusDecodeFailed");
                return;
            }
            // Pre-allocate the preview WriteableBitmap + reusable byte buffer at source dims.
            // The buffer is sized by source.RowBytes × height (= source.Width × 4 × height for
            // BGRA8888). Allocating once keeps the per-frame brush path allocator-free.
            var w = _sourceBitmap.Width;
            var h = _sourceBitmap.Height;
            _previewWriteable = new WriteableBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            _compositeBuffer = new byte[_sourceBitmap.RowBytes * h];

            _vm.StatusText = LocalizedStatus("BgRemover_StatusReady");
            RefreshProcessedMask();
            RefreshComposite();
        }
        finally
        {
            _vm.IsInferenceRunning = false;
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BgRemoverViewModel.Threshold)
                            or nameof(BgRemoverViewModel.FeatherPx)
                            or nameof(BgRemoverViewModel.EdgeOffsetPx))
        {
            // Slider change → debounced full pipeline refresh, BUT only if we're not
            // mid-drag on a heavy slider. DragCompleted runs ScheduleRefresh manually.
            // Keyboard arrows + track clicks don't raise DragStarted so they fall through
            // here and apply immediately — exactly right for those discrete edits.
            if (!_draggingHeavySlider) ScheduleRefresh();
        }
        else if (e.PropertyName == nameof(BgRemoverViewModel.BackgroundOpacity))
        {
            // Background-opacity is preview-only (not part of the mask pipeline) — skip the
            // processedMask recompute and just re-composite. Cheap path: no Skia blur passes,
            // just the byte-loop blend.
            RefreshComposite();
        }
        else if (e.PropertyName is nameof(BgRemoverViewModel.BrushSizePx)
                                or nameof(BgRemoverViewModel.BrushMode)
                                or nameof(BgRemoverViewModel.BrushHardness))
        {
            // Brush params change → resize + recolour the cursor ring AND re-center it under
            // the last known mouse position (so a Shift+Wheel hardness/size tweak doesn't
            // leave the ring stranded at its previous spot until the next mouse move).
            // If the mouse isn't over the preview at all (slider-driven change), flash the
            // ring at the centre of the pane for ~1.5 s so the user sees the new dimensions
            // without having to first move the mouse over the preview.
            UpdateBrushCursor();
            if (CutoutHost.IsMouseOver && _lastMouseOnHost is { } p)
            {
                UpdateBrushCursorPosition(p);
            }
            else
            {
                ShowBrushCursorAtCenter();
            }
        }
    }

    private void ScheduleRefresh()
    {
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        _ = DebounceRefreshAsync(cts.Token);
    }

    private async Task DebounceRefreshAsync(CancellationToken ct)
    {
        try { await Task.Delay(80, ct).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;
        RefreshProcessedMask();
        RefreshComposite();
    }

    /// <summary>Recompute the processed mask cache from <see cref="_rawMaskBitmap"/> +
    /// current VM params (threshold / feather / edge offset). Called on slider edits ONLY —
    /// brush strokes never invalidate this cache, that's the whole point of caching here:
    /// a brush stroke shouldn't pay the cost of re-running the Skia blur passes.</summary>
    private void RefreshProcessedMask()
    {
        if (_rawMaskBitmap is null) return;
        var fresh = BgRemovalProcessor.ProcessMask(_rawMaskBitmap, _vm.ToParams());
        _processedMaskBitmap?.Dispose();
        _processedMaskBitmap = fresh;
    }

    /// <summary>Rebuild the right-pane preview from the cached processed mask + brush overlay.
    /// Fast path: copies pixel buffers via <see cref="System.Runtime.InteropServices.Marshal.Copy"/>
    /// and runs a single managed loop to blend source + mask + brush into the pre-allocated
    /// composite buffer, then writes it into the pre-allocated WriteableBitmap. Total cost
    /// on 1080p ≈ 30-50 ms — well inside a 16 ms double-frame budget for brush feedback.</summary>
    private void RefreshComposite()
    {
        if (_sourceBitmap is null || _processedMaskBitmap is null
            || _previewWriteable is null || _compositeBuffer is null) return;
        try
        {
            // Background opacity in 0..255 (VM stores 0..100). Preview-only — Apply path
            // builds its own composite below with bg=0 so the saved PNG keeps full transparency.
            var bgFloor = (byte)Math.Clamp(_vm.BackgroundOpacity * 255 / 100, 0, 255);
            BgRemovalProcessor.CompositeIntoBuffer(
                _sourceBitmap, _processedMaskBitmap, _brushOverlayBitmap, _strokeBufferBitmap, _compositeBuffer, bgFloor);
            var w = _sourceBitmap.Width;
            var h = _sourceBitmap.Height;
            var stride = _sourceBitmap.RowBytes;
            _previewWriteable.WritePixels(new Int32Rect(0, 0, w, h),
                _compositeBuffer, stride, 0);
            // Assign once on the first refresh; later refreshes reuse the same WriteableBitmap
            // so WPF skips re-binding (it can detect the buffer was updated via dirty rect).
            if (CutoutPreview.Source != _previewWriteable)
                CutoutPreview.Source = _previewWriteable;
        }
        catch
        {
            // Pipeline failures (out-of-memory on huge sources, etc.) just leave the previous
            // preview in place — better than tearing the UI for a parameter tweak that didn't
            // work out.
        }
    }

    // -------- Mouse handling on the cutout viewport --------
    // All events fire on CutoutHost (the outer Grid) and bubble up from the Image / Canvas
    // children. We dispatch in-place between brush, pan, zoom, and brush-size adjustment so
    // there's a single source of truth for which gesture is active. Brush coordinates use
    // e.GetPosition(CutoutPreview) so the bitmap-space math sees pre-transform coords (so
    // panning / zooming doesn't break MapToBitmap).

    private void OnHostMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Always grab focus so window-level keyboard shortcuts (X toggle, Ctrl+Z undo) keep
        // working after the user clicks into the preview. WPF doesn't focus on click for
        // controls without IsTabStop set true, so we do it explicitly.
        Focus();
        // Pan: middle button (LMB always paints since "Off" mode was removed by user request).
        if (e.ChangedButton == MouseButton.Middle)
        {
            _panStart = e.GetPosition(CutoutHost);
            _panStartTx = ZoomTranslate.X;
            _panStartTy = ZoomTranslate.Y;
            CutoutHost.CaptureMouse();
            CutoutHost.Cursor = System.Windows.Input.Cursors.SizeAll;
            e.Handled = true;
            return;
        }
        // Brush: LMB. Snapshot the overlay buffer first so the whole stroke can be undone
        // as one Ctrl+Z. Anything other than LMB is ignored.
        if (e.ChangedButton != MouseButton.Left || _brushOverlayBitmap is null
            || _strokeBufferBitmap is null) return;
        e.Handled = true;
        CutoutHost.CaptureMouse();
        SnapshotForUndo();
        // Reset the in-flight stroke buffer to transparent so this stroke starts from a
        // clean slate (the previous stroke was already merged on its mouse-up).
        _strokeBufferBitmap.Erase(SKColors.Transparent);
        _lastBrushPoint = MapToBitmap(e.GetPosition(CutoutPreview));
        if (_lastBrushPoint is { } p)
        {
            PaintBrush(p, p);
            RefreshComposite();
        }
    }

    private void OnHostMouseMove(object sender, MouseEventArgs e)
    {
        _lastMouseOnHost = e.GetPosition(CutoutHost);
        UpdateBrushCursorPosition(_lastMouseOnHost.Value);

        // Pan in progress?
        if (_panStart is { } panStart)
        {
            ZoomTranslate.X = _panStartTx + (_lastMouseOnHost.Value.X - panStart.X);
            ZoomTranslate.Y = _panStartTy + (_lastMouseOnHost.Value.Y - panStart.Y);
            return;
        }
        // Brush stroke in progress
        if (_brushOverlayBitmap is null) return;
        if (e.LeftButton != MouseButtonState.Pressed || _lastBrushPoint is not { } prev) return;
        var bmpCur = MapToBitmap(e.GetPosition(CutoutPreview));
        if (bmpCur is not { } cur) return;
        PaintBrush(prev, cur);
        _lastBrushPoint = cur;
        RefreshComposite();
    }

    private void OnHostMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panStart is not null)
        {
            _panStart = null;
            if (CutoutHost.IsMouseCaptured) CutoutHost.ReleaseMouseCapture();
            CutoutHost.Cursor = System.Windows.Input.Cursors.Arrow;
            return;
        }
        if (_lastBrushPoint is not null)
        {
            // Stroke ended — bake the in-flight stamps into the persistent overlay so the
            // next stroke can build up on top of them.
            MergeStrokeBuffer();
            RefreshComposite();
        }
        _lastBrushPoint = null;
        if (CutoutHost.IsMouseCaptured) CutoutHost.ReleaseMouseCapture();
    }

    private void OnHostMouseEnter(object sender, MouseEventArgs e)
    {
        // Cancel any centred-cursor auto-hide pending from a slider tweak — the user is
        // moving back over the preview, real position-tracking takes over.
        _centredCursorHideTimer?.Stop();
        UpdateBrushCursor();
    }

    /// <summary>Position the brush cursor ring in the centre of the preview pane and start
    /// the auto-hide timer. Used when the user adjusts brush size / hardness / mode via
    /// slider while the mouse is OVER the slider itself (not over the preview); without
    /// this the cursor would stay invisible and the user wouldn't see the new dimensions
    /// until they moved the mouse over the preview.</summary>
    private void ShowBrushCursorAtCenter()
    {
        if (BrushCursor.Visibility != Visibility.Visible) return; // size 0 → nothing to show
        var cx = CutoutHost.ActualWidth / 2.0;
        var cy = CutoutHost.ActualHeight / 2.0;
        UpdateBrushCursorPosition(new Point(cx, cy));
        _centredCursorHideTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        _centredCursorHideTimer.Tick -= OnCentredCursorHideTick;
        _centredCursorHideTimer.Tick += OnCentredCursorHideTick;
        _centredCursorHideTimer.Stop();
        _centredCursorHideTimer.Start();
    }

    private void OnCentredCursorHideTick(object? sender, EventArgs e)
    {
        _centredCursorHideTimer?.Stop();
        // Only hide if the mouse still isn't over the preview — if the user moved over it
        // mid-timer, MouseEnter already cancelled us, but defend in depth.
        if (!CutoutHost.IsMouseOver)
        {
            BrushCursor.Visibility = Visibility.Collapsed;
            BrushCursorCore.Visibility = Visibility.Collapsed;
        }
    }

    private void OnHostMouseLeave(object sender, MouseEventArgs e)
    {
        _lastBrushPoint = null;
        _panStart = null;
        if (CutoutHost.IsMouseCaptured) CutoutHost.ReleaseMouseCapture();
        CutoutHost.Cursor = System.Windows.Input.Cursors.Arrow;
        BrushCursor.Visibility = Visibility.Collapsed;
        BrushCursorCore.Visibility = Visibility.Collapsed;
    }

    /// <summary>Wheel: Ctrl+Wheel = zoom anchored at mouse, Shift+Wheel = brush hardness,
    /// Wheel alone = brush size. Step sizes are tuned for "feels right at any value":
    /// brush size scales with current value (max(2, size/10)), hardness uses fixed 5%
    /// notches (small range), zoom uses 1.2× per notch.</summary>
    private void OnHostPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            DoZoom(e.Delta, e.GetPosition(CutoutHost));
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            var step = e.Delta > 0 ? 5 : -5;
            _vm.BrushHardness = Math.Clamp(_vm.BrushHardness + step, 0, 100);
        }
        else
        {
            var step = Math.Max(2, _vm.BrushSizePx / 10);
            var newSize = _vm.BrushSizePx + (e.Delta > 0 ? step : -step);
            _vm.BrushSizePx = Math.Clamp(newSize, 5, 200);
        }
        e.Handled = true;
    }

    /// <summary>Apply a zoom step centred on <paramref name="mouseInHost"/>. After the new
    /// scale is set, the translate is recomputed so the layer point that was under the cursor
    /// before still sits there — anchors zoom on mouse position so users zoom INTO whatever
    /// they were looking at instead of toward the canvas centre.</summary>
    private void DoZoom(int wheelDelta, Point mouseInHost)
    {
        var oldScale = ZoomScale.ScaleX;
        var factor = wheelDelta > 0 ? 1.2 : 1.0 / 1.2;
        var newScale = Math.Clamp(oldScale * factor, 0.1, 20.0);
        if (Math.Abs(newScale - oldScale) < 0.0001) return;

        // Inverse-transform the cursor position to layer-local pre-transform coords. With
        // RenderTransformOrigin (0,0), the transform chain is Translate(tx,ty) ∘ Scale(s):
        //   viewport = layer * s + t  →  layer = (viewport - t) / s
        var layerX = (mouseInHost.X - ZoomTranslate.X) / oldScale;
        var layerY = (mouseInHost.Y - ZoomTranslate.Y) / oldScale;

        ZoomScale.ScaleX = newScale;
        ZoomScale.ScaleY = newScale;
        // Solve for new translate so layerX/Y still maps to the same viewport position:
        //   mouseInHost = layerX * newScale + newTx  →  newTx = mouseInHost - layerX * newScale
        ZoomTranslate.X = mouseInHost.X - layerX * newScale;
        ZoomTranslate.Y = mouseInHost.Y - layerY * newScale;
        // Brush cursor sits outside the zoom layer, so its diameter has to be recomputed
        // manually whenever zoom changes — otherwise the ring keeps its fit-scale size while
        // the painted footprint grows / shrinks with zoom.
        UpdateBrushCursor();
        UpdateBrushCursorPosition(mouseInHost);
    }

    private void OnResetViewClicked(object sender, RoutedEventArgs e)
    {
        ZoomScale.ScaleX = 1;
        ZoomScale.ScaleY = 1;
        ZoomTranslate.X = 0;
        ZoomTranslate.Y = 0;
        UpdateBrushCursor();
        if (_lastMouseOnHost is { } p) UpdateBrushCursorPosition(p);
    }

    /// <summary>Show/hide + resize the brush cursor visualizer based on current brush mode +
    /// size + hardness + Alt modifier. Called on cursor enter, brush mode toggle, brush size
    /// edit, Alt key press/release, AND after a zoom change. The ring sits in
    /// <see cref="BrushCursorHost"/> which is OUTSIDE the zoom layer (so we control its
    /// thickness independently of the zoom transform), but its diameter is scaled to match
    /// the brush's actual on-screen footprint = source-pixel radius × fit-scale × user-zoom.
    /// Without the user-zoom factor the ring stayed sized for fit-to-window scale while the
    /// painted footprint grew with zoom, so at any zoom &gt; 1 the visible brush no longer
    /// matched the preview.</summary>
    private void UpdateBrushCursor()
    {
        if (_sourceBitmap is null
            || CutoutHost.ActualWidth <= 0 || CutoutHost.ActualHeight <= 0)
        {
            BrushCursor.Visibility = Visibility.Collapsed;
            return;
        }
        // fit-to-window scale: how big a 1-source-pixel feature is on screen at zoom=1.
        // CutoutHost dimensions are post-layout but pre-zoom-transform — exactly what we want
        // for the "fit" measurement. ZoomScale.ScaleX then folds in the user's current zoom so
        // the ring tracks the painted footprint (which is in the zoomed image layer).
        var fitScale = Math.Min(
            CutoutHost.ActualWidth / _sourceBitmap.Width,
            CutoutHost.ActualHeight / _sourceBitmap.Height);
        var zoom = ZoomScale.ScaleX;
        if (zoom <= 0) zoom = 1.0;
        var displaySize = _vm.BrushSizePx * fitScale * zoom;
        BrushCursor.Width = displaySize;
        BrushCursor.Height = displaySize;
        BrushCursor.Visibility = Visibility.Visible;
        // Resolve the effective mode considering Alt-invert: held Alt flips Add↔Remove for
        // the cursor preview AND the actual paint. Cursor colour mirrors the effective mode
        // so the user sees what their next stroke will do.
        var effectiveAdd = _vm.BrushMode == BgBrushMode.Add;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) effectiveAdd = !effectiveAdd;
        var stroke = effectiveAdd
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(120, 255, 120))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 120, 120));
        BrushCursor.Stroke = stroke;

        // Inner ring (dashed) shows where the hard core ends. Hidden at hardness ≥ 99%
        // (it'd just overlap the outer ring uselessly). Same stroke colour so the two
        // rings read as a unit; the dash differentiates them at a glance.
        var hardness01 = Math.Clamp(_vm.BrushHardness, 0, 100) / 100.0;
        if (hardness01 < 0.99)
        {
            var innerSize = displaySize * hardness01;
            BrushCursorCore.Width = innerSize;
            BrushCursorCore.Height = innerSize;
            BrushCursorCore.Stroke = stroke;
            BrushCursorCore.Visibility = Visibility.Visible;
        }
        else
        {
            BrushCursorCore.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Move the brush cursor ring so its centre sits under the mouse pointer.
    /// Lightweight — runs on every MouseMove + after brush size/mode/hardness changes.
    /// Mouse position is in CutoutHost coords; BrushCursorHost is a sibling Canvas in the
    /// same Grid (post-transform space), so we can use the coords directly without any
    /// translation.</summary>
    private void UpdateBrushCursorPosition(Point mouseOnHost)
    {
        if (BrushCursor.Visibility != Visibility.Visible) return;
        var radius = BrushCursor.Width / 2.0;
        Canvas.SetLeft(BrushCursor, mouseOnHost.X - radius);
        Canvas.SetTop(BrushCursor, mouseOnHost.Y - radius);
        if (BrushCursorCore.Visibility == Visibility.Visible)
        {
            var innerRadius = BrushCursorCore.Width / 2.0;
            Canvas.SetLeft(BrushCursorCore, mouseOnHost.X - innerRadius);
            Canvas.SetTop(BrushCursorCore, mouseOnHost.Y - innerRadius);
        }
    }

    /// <summary>Project a control-space point onto the source bitmap's pixel grid. The Image
    /// uses Stretch=Uniform so the bitmap may be letterboxed inside the control — points
    /// outside the bitmap area return null. Without this the user would brush off-image and
    /// produce no-op strokes that confuse the "is the brush working?" feedback loop.</summary>
    private SKPoint? MapToBitmap(Point controlPoint)
    {
        if (_sourceBitmap is null) return null;
        var ctlW = CutoutPreview.ActualWidth;
        var ctlH = CutoutPreview.ActualHeight;
        if (ctlW <= 0 || ctlH <= 0) return null;
        var bmpW = _sourceBitmap.Width;
        var bmpH = _sourceBitmap.Height;
        // Uniform stretch: scale = min(ctlW/bmpW, ctlH/bmpH), centred letterbox.
        var scale = Math.Min(ctlW / bmpW, ctlH / bmpH);
        var renderedW = bmpW * scale;
        var renderedH = bmpH * scale;
        var offsetX = (ctlW - renderedW) / 2.0;
        var offsetY = (ctlH - renderedH) / 2.0;
        var bx = (controlPoint.X - offsetX) / scale;
        var by = (controlPoint.Y - offsetY) / scale;
        if (bx < 0 || by < 0 || bx >= bmpW || by >= bmpH) return null;
        return new SKPoint((float)bx, (float)by);
    }

    /// <summary>Paint a brush stroke onto <see cref="_strokeBufferBitmap"/> (the transient
    /// in-flight stroke layer) by stamping soft-edged circles densely from <paramref name="from"/>
    /// to <paramref name="to"/>. Each stamp uses Lighten blend mode so the per-pixel alpha
    /// caps at the gradient peak — overlapping stamps within a stroke don't accumulate past
    /// the hardness-controlled max, which keeps the falloff visible even on long drags.
    /// The committed overlay (multi-stroke buildup) lives in <see cref="_brushOverlayBitmap"/>
    /// and is updated only on mouse-up via <see cref="MergeStrokeBuffer"/>.
    /// <para>Stamp colour encodes mode in the R/G channels: Add → R=value, G=0; Remove →
    /// R=0, G=value. Two channels keep Add and Remove independent so a later Remove stroke
    /// over an earlier Add stroke at the same pixel correctly lowers final alpha (with the
    /// previous single-channel encoding, Lighten/SrcOver max'd-out the existing R and the
    /// Remove was effectively a no-op).</para></summary>
    private void PaintBrush(SKPoint from, SKPoint to)
    {
        if (_strokeBufferBitmap is null) return;
        var addMode = _vm.BrushMode == BgBrushMode.Add;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) addMode = !addMode;
        var radius = _vm.BrushSizePx / 2f;
        if (radius <= 0) return;
        var hardness = Math.Clamp(_vm.BrushHardness / 100f, 0f, 1f);

        using var canvas = new SKCanvas(_strokeBufferBitmap);

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dist = MathF.Sqrt(dx * dx + dy * dy);
        var stepSize = Math.Max(1f, radius / 4f);
        var steps = Math.Max(1, (int)MathF.Ceiling(dist / stepSize));
        for (var i = 0; i <= steps; i++)
        {
            var t = steps == 0 ? 0f : i / (float)steps;
            var px = from.X + dx * t;
            var py = from.Y + dy * t;
            StampBrush(canvas, new SKPoint(px, py), radius, hardness, addMode);
        }
    }

    private static void StampBrush(SKCanvas canvas, SKPoint center, float radius, float hardness, bool addMode)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            // Lighten = max-per-channel. Within a stroke, overlapping stamps' alpha caps at
            // the gradient peak (the maxAlpha we set on the core). The R/G channels are
            // disjoint between Add and Remove, so Lighten's per-channel max just ramps the
            // appropriate channel without touching the other.
            BlendMode = SKBlendMode.Lighten,
        };
        if (hardness >= 0.999f)
        {
            // Hard 100% stamp — full-strength solid color, no falloff. R for Add, G for Remove.
            paint.Color = addMode
                ? new SKColor(255, 0, 0, 255)
                : new SKColor(0, 255, 0, 255);
        }
        else
        {
            // Combined hardness + strength: lower hardness shrinks both per-stamp max alpha
            // AND grows the falloff radius. The 0.20 floor keeps hardness=0% weakly visible
            // (so dragging the slider to zero doesn't turn the brush "off").
            var maxByte = (byte)(255 * (0.20f + 0.80f * hardness));
            var coreStop = hardness;
            var core = addMode
                ? new SKColor(maxByte, 0, 0, maxByte)
                : new SKColor(0, maxByte, 0, maxByte);
            var rim = new SKColor(0, 0, 0, 0);
            paint.Shader = SKShader.CreateRadialGradient(
                center, radius,
                new[] { core, core, rim },
                new[] { 0f, coreStop, 1f },
                SKShaderTileMode.Clamp);
        }
        canvas.DrawCircle(center, radius, paint);
    }

    /// <summary>Merge <see cref="_strokeBufferBitmap"/> into <see cref="_brushOverlayBitmap"/>
    /// at mouse-up using channel-wise SrcOver-like buildup. Per channel: D' = S + D*(255-S)/255
    /// — gives the natural "second stroke at hardness=20% on top of an existing 91-strength
    /// area pushes it up to ~149, etc." progression. Capped at 255 (saturation). Alpha is
    /// recomputed as max(R, G) so callers reading the overlay's alpha as "is override active"
    /// keep working.</summary>
    private void MergeStrokeBuffer()
    {
        if (_strokeBufferBitmap is null || _brushOverlayBitmap is null) return;
        var w = _strokeBufferBitmap.Width;
        var h = _strokeBufferBitmap.Height;
        var rowBytes = _strokeBufferBitmap.RowBytes;
        var len = rowBytes * h;
        var sBuf = new byte[len];
        var oBuf = new byte[len];
        System.Runtime.InteropServices.Marshal.Copy(_strokeBufferBitmap.GetPixels(), sBuf, 0, len);
        System.Runtime.InteropServices.Marshal.Copy(_brushOverlayBitmap.GetPixels(), oBuf, 0, len);
        for (var i = 0; i < len; i += 4)
        {
            int sR = sBuf[i + 2];
            int sG = sBuf[i + 1];
            if (sR == 0 && sG == 0) continue;
            int dR = oBuf[i + 2];
            int dG = oBuf[i + 1];
            int nR = sR + dR * (255 - sR) / 255;
            int nG = sG + dG * (255 - sG) / 255;
            if (nR > 255) nR = 255;
            if (nG > 255) nG = 255;
            oBuf[i + 0] = 0;
            oBuf[i + 1] = (byte)nG;
            oBuf[i + 2] = (byte)nR;
            oBuf[i + 3] = (byte)Math.Max(nR, nG);
        }
        System.Runtime.InteropServices.Marshal.Copy(oBuf, 0, _brushOverlayBitmap.GetPixels(), len);
        _strokeBufferBitmap.Erase(SKColors.Transparent);
    }

    private void OnResetBrushClicked(object sender, RoutedEventArgs e)
    {
        if (_brushOverlayBitmap is null) return;
        SnapshotForUndo(); // so Ctrl+Z can revert the reset
        _brushOverlayBitmap.Erase(SKColors.Transparent);
        RefreshComposite();
    }

    // -------- Undo (brush-overlay snapshots) --------

    /// <summary>Push the current overlay buffer onto the undo stack. Called at the start of
    /// every stroke (mouse-down) and before <see cref="OnResetBrushClicked"/> empties the
    /// overlay. The snapshot is a fresh byte[] copy of the overlay's pixel buffer; no SKBitmap
    /// allocation, no native handle to manage.</summary>
    private void SnapshotForUndo()
    {
        if (_brushOverlayBitmap is null) return;
        var len = _brushOverlayBitmap.RowBytes * _brushOverlayBitmap.Height;
        var snapshot = new byte[len];
        System.Runtime.InteropServices.Marshal.Copy(
            _brushOverlayBitmap.GetPixels(), snapshot, 0, len);
        _undoStack.AddFirst(snapshot);
        // Bound memory: drop the oldest snapshot once we exceed the cap. 20 strokes × ~8 MB
        // per 1080p source = ~160 MB peak in the worst case, acceptable for a creative-tool
        // session that's typically a handful of corrections.
        while (_undoStack.Count > MaxUndoLevels) _undoStack.RemoveLast();
    }

    /// <summary>Pop the most recent overlay snapshot back into <see cref="_brushOverlayBitmap"/>
    /// and refresh the composite. No-op when the stack is empty (Ctrl+Z at the start of a
    /// session does nothing — same as every editor).</summary>
    private void Undo()
    {
        if (_undoStack.Count == 0 || _brushOverlayBitmap is null) return;
        var snapshot = _undoStack.First!.Value;
        _undoStack.RemoveFirst();
        var len = _brushOverlayBitmap.RowBytes * _brushOverlayBitmap.Height;
        if (snapshot.Length != len) return; // shouldn't happen unless source dims changed
        System.Runtime.InteropServices.Marshal.Copy(
            snapshot, 0, _brushOverlayBitmap.GetPixels(), len);
        RefreshComposite();
    }

    // -------- Keyboard handling --------

    /// <summary>Handles Ctrl+Z on the tunnel pass so it intercepts BEFORE a focused TextBox
    /// (e.g. the size / hardness numeric inputs) gets the chance to run its built-in undo
    /// on the typed-character history. Without preview-handling, clicking inside the size
    /// box and pressing Ctrl+Z would just no-op the textbox's undo stack, leaving brush
    /// strokes un-undoable until the user clicked away.</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Undo();
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // X: toggle brush mode Add ↔ Remove (Photoshop convention). Stays on regular KeyDown
        // so a TextBox with focus swallows the keystroke (the user is typing in it) — without
        // that, any "x" they type into the size field would also flip the mode.
        if (e.Key == Key.X)
        {
            _vm.BrushMode = _vm.BrushMode == BgBrushMode.Add ? BgBrushMode.Remove : BgBrushMode.Add;
            e.Handled = true;
            return;
        }
        // Alt: redraw the brush cursor in the inverted-mode colour so the user sees what the
        // next stroke (with Alt held) will do. Alt arrives as e.SystemKey because it's a
        // system modifier; ignore the bare Key check for it.
        if (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt
            || e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
        {
            UpdateBrushCursor();
        }
    }

    // -------- Heavy-slider drag tracking --------

    private void OnHeavySliderDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _draggingHeavySlider = true;
    }

    private void OnHeavySliderDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _draggingHeavySlider = false;
        // Apply the final value now that the user committed. Mirrors the logic OnVmPropertyChanged
        // would have run on a normal slider change.
        ScheduleRefresh();
    }

    private void OnWindowKeyUp(object sender, KeyEventArgs e)
    {
        if (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt
            || e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
        {
            UpdateBrushCursor();
        }
    }

    // -------- Footer buttons --------

    private void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        if (_sourceBitmap is null || _rawMaskBitmap is null) { Close(); return; }
        try
        {
            using var composite = BgRemovalProcessor.BuildComposite(
                _sourceBitmap, _rawMaskBitmap, _brushOverlayBitmap, _vm.ToParams());
            using var image = SKImage.FromBitmap(composite);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            ResultPng = data.ToArray();
        }
        catch
        {
            // Apply failed — fall through with ResultPng=null so the editor keeps the original.
            ResultPng = null;
        }
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshCts?.Cancel();
        _centredCursorHideTimer?.Stop();
        _sourceBitmap?.Dispose();
        _rawMaskBitmap?.Dispose();
        _processedMaskBitmap?.Dispose();
        _brushOverlayBitmap?.Dispose();
        _strokeBufferBitmap?.Dispose();
        // _previewWriteable + _compositeBuffer are managed (no native resources) — GC handles.
    }

    // -------- Bitmap helpers --------

    /// <summary>Build a frozen <see cref="BitmapImage"/> from PNG bytes. Used for the static
    /// source preview which doesn't change during the session — lets WPF cache the decode
    /// instead of re-decoding for each redraw cycle.</summary>
    private static BitmapImage LoadBitmapImage(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Resolve a localised status string by key, falling back to the bare key when
    /// the resource isn't found (so missing translations show up as obviously-wrong text
    /// instead of silently displaying nothing).</summary>
    private static string LocalizedStatus(string key)
    {
        try
        {
            return AresToys.App.Resources.Strings.ResourceManager.GetString(key,
                System.Globalization.CultureInfo.CurrentUICulture) ?? key;
        }
        catch
        {
            return key;
        }
    }
}
