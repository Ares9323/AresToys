using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AresToys.App.Resources;
using AresToys.App.Services;
using AresToys.Capture;
using AresToys.Storage.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace AresToys.App.Views;

/// <summary>What kind of edit is happening on the currently selected committed region. Move =
/// drag the whole rect; the eight compass directions = resize from that handle. Mirrors the
/// editor's pending-crop drag pattern so the UX is identical between the two surfaces.</summary>
internal enum RegionDragMode { None, Move, NW, N, NE, E, SE, S, SW, W }

/// <summary>The region capture session: freezes the virtual screen, lets the user drag one or more
/// rectangles (or click a window to snap to it), and hands back the picked region with its PNG
/// crop.
/// <para>The session owns all overlay state in physical virtual-screen pixels: committed regions,
/// selected index, drag mode, the rectangle being dragged, the hovered window, the zoom, the
/// toolbar spot and the lens flip flags. The windows are <see cref="RegionOverlayView"/>s, one per
/// monitor, placed and closed by <see cref="PerMonitorOverlayHost"/> (WPF hardware rendering loses
/// video memory on every Show of a window spanning several monitors, run 2294); they only draw
/// their monitor's part of this state and forward input here. A drag is captured by the view where
/// the button went down, so a selection still crosses monitors.</para></summary>
public class RegionOverlay
{
    private const int MagnifierBoxPx = 160;       // lens size in DIPs
    private const int MagnifierCursorOffset = 24;
    private const double ClickThresholdDips = 5;
    private const double GripSizeDips = 10;
    private const double ToolbarBottomMarginDips = 32;
    private const string ToolbarXSettingKey = "RegionOverlay_ToolbarScreenX";
    private const string ToolbarYSettingKey = "RegionOverlay_ToolbarScreenY";

    /// <summary>Order of the grips on a region: NW, N, NE, W, E, SW, S, SE.</summary>
    internal static readonly RegionDragMode[] GripModes =
    {
        RegionDragMode.NW, RegionDragMode.N, RegionDragMode.NE,
        RegionDragMode.W,                     RegionDragMode.E,
        RegionDragMode.SW, RegionDragMode.S, RegionDragMode.SE,
    };

    private readonly PerMonitorOverlayHost _host = new();
    private readonly List<RegionOverlayView> _views = new();
    private IReadOnlyList<OverlayMonitor> _monitors = Array.Empty<OverlayMonitor>();
    private BitmapSource? _snapshot;
    private int _snapshotLeft, _snapshotTop;
    private CaptureRegion? _result;

    /// <summary>Committed regions accumulated by successive drags before the user confirms, in
    /// physical virtual-screen pixels. Confirmation (Enter / double-click / Apply) collapses the
    /// list into a composite PNG via <see cref="BuildMultiRegionComposite"/>.</summary>
    private readonly List<Rect> _regions = new();
    private int _selected = -1;

    // Selection drag (a new rectangle being drawn).
    private Point? _dragStart;
    private Rect _selectionRect;
    private double _dragScale = 1;                // DPI scale of the view where the button went down

    // Move / resize of a committed region.
    private RegionDragMode _regionDragMode = RegionDragMode.None;
    private int _regionDragIndex = -1;
    private Point _regionDragStart;
    private Rect _regionDragOriginal;

    // Toolbar: its top-left in physical pixels, the view that draws it, and the drag state.
    private Point _toolbarPos;
    private RegionOverlayView? _toolbarView;
    private bool _toolbarDragging;
    private Point _toolbarGrabDips;               // grab point inside the toolbar, in DIPs

    private int _half = 8;                        // 17x17 sample by default
    private bool _flippedX, _flippedY;
    private int _lastX = int.MinValue, _lastY = int.MinValue;
    private bool _pendingDim;                     // dim rebuild queued for the next tick
    // Top-level windows enumerated once before the views exist. Used for snap-to-window.
    private IReadOnlyList<WindowSnapshot> _windows = Array.Empty<WindowSnapshot>();
    private WindowSnapshot? _hoveredWindow;

    private CompositeFormat? _regionCountFormat; // parsed once per session from the localized string
    private DispatcherTimer? _timer;
    private bool _ended;

    /// <summary>When true, the overlay closes on the FIRST committed region (single drag or one
    /// snap-to-window click) without waiting for Enter: single-shot semantics that match the
    /// pre-multi-region behaviour. Set by callers (e.g. the CaptureRegionTask pipeline task) per
    /// workflow. Default false = current multi-region UX.</summary>
    public bool AutoConfirmOnFirstSelection { get; set; }

    /// <summary>PNG bytes of the picked region, cropped from the screen snapshot taken at
    /// open-time. Non-null when <see cref="PickRegion"/> returned a region: callers can use these
    /// directly and skip a second BitBlt round-trip. Why: the freeze-then-crop flow eliminates the
    /// gap between the user's mouse-up and the capture, so transient UI (open dropdowns, hover
    /// popups) stays visible in the screenshot exactly as the user saw it during selection.</summary>
    public byte[]? PickedSnapshotBytes { get; private set; }

    /// <summary>Per-rect cropped PNGs when the user committed >1 region in the overlay. Each entry
    /// is the physical-pixel top-left of the rect plus its PNG bytes. Null on single-region commits
    /// or cancellation. Lets the pipeline produce N separate pinned windows (one per rect) instead
    /// of one composite-with-transparent-halo.</summary>
    public IReadOnlyList<(int X, int Y, byte[] Png)>? PickedMultiRegionParts { get; private set; }

    public RegionOverlay() : this(null, 0, 0) { }

    /// <summary>Construct the session with a pre-captured snapshot (from
    /// <see cref="PerMonitorOverlayHost.CaptureVirtualScreen"/>, taken at the earliest point of the
    /// caller's pipeline). Pass null to have <see cref="PickRegion"/> take its own snapshot.</summary>
    public RegionOverlay(BitmapSource? prefabSnapshot, int prefabLeft, int prefabTop)
    {
        if (prefabSnapshot is not null)
        {
            _snapshot = prefabSnapshot;
            _snapshotLeft = prefabLeft;
            _snapshotTop = prefabTop;
        }
    }

    /// <summary>Opens the overlay on every monitor and waits for a confirm or a cancel. Returns the
    /// picked region, or null on cancel (or when the screen could not be captured). Call it on the
    /// UI thread.</summary>
    public CaptureRegion? PickRegion()
    {
        // If the caller pre-supplied a snapshot, skip the capture: the prefab was taken at a moment
        // that respected the user's UI state (e.g. before any focus shift could close an open
        // dropdown). Taken before any view exists, so the overlay's own UI can't pollute it.
        if (_snapshot is null)
        {
            var (snapshot, left, top) = PerMonitorOverlayHost.CaptureVirtualScreen();
            _snapshot = snapshot;
            _snapshotLeft = left;
            _snapshotTop = top;
        }
        if (_snapshot is null) return null;

        var monitors = PerMonitorOverlayHost.EnumerateMonitors();
        if (monitors.Count == 0) return null;
        _monitors = monitors;

        // Enumerate visible windows for snap-on-hover. EnumWindows returns top-most z-order first
        // (which is what we want: clicking on overlapping windows snaps to the foreground one).
        // Before any view exists, so the overlay is not in the list.
        _windows = WindowEnumeration.EnumerateVisibleWindows();

        var views = new List<(Window View, OverlayMonitor Monitor)>(monitors.Count);
        foreach (var monitor in monitors)
        {
            var view = new RegionOverlayView(
                this, monitor,
                PerMonitorOverlayHost.CropToMonitor(_snapshot, _snapshotLeft, _snapshotTop, monitor),
                toolbarEnabled: !AutoConfirmOnFirstSelection);
            _views.Add(view);
            views.Add((view, monitor));
        }

        // Single 60 Hz tick (Render priority) driving the cursor label, the lens, the snap preview
        // and the dim rebuild, instead of MouseMove (200+/s on high-poll devices, redraw-time lag).
        // Started from inside the host's frame once every view has been shown, and never after the
        // session ended.
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(StartAfterShown));
        try
        {
            _host.Run(views);
        }
        finally
        {
            _ended = true;
            _timer.Stop();
        }
        return _result;
    }

    private async void StartAfterShown()
    {
        if (_ended) return;
        // Auto-confirm mode has no toolbar: the affordances it exposes (region count, Apply,
        // Cancel for accumulated rects) are meaningless when the overlay closes on the first
        // selection. The Esc shortcut still cancels.
        if (!AutoConfirmOnFirstSelection) await PositionToolbarFromSettingsOrDefaultAsync().ConfigureAwait(true);
        // A close during the await above must not start the timer on a finished session.
        if (_ended) return;
        _timer?.Start();
    }

    // ---- Ending -----------------------------------------------------------------------------

    /// <summary>Esc, toolbar Cancel: end without a result.</summary>
    internal void Cancel()
    {
        _result = null;
        End();
    }

    /// <summary>Toolbar Apply.</summary>
    internal void Apply()
    {
        if (_regions.Count > 0) ConfirmCommittedRegions();
    }

    private void End()
    {
        if (_ended) return;
        _ended = true;
        _timer?.Stop();
        _host.End();
    }

    // ---- Keys -------------------------------------------------------------------------------

    internal void HandleKey(KeyEventArgs e)
    {
        if (e.Handled || _ended) return;
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
            return;
        }
        // Enter / Return: confirm all committed regions and produce the final composite. Empty
        // list = no-op (the user has only seen the empty overlay; let them keep dragging).
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            if (_regions.Count > 0)
            {
                ConfirmCommittedRegions();
                e.Handled = true;
            }
            return;
        }
        // Delete removes the currently selected committed region. After delete the selection
        // clears (no auto-pick of an adjacent rect: the user re-clicks if they want another).
        if (e.Key == Key.Delete && _selected >= 0 && _selected < _regions.Count)
        {
            _regions.RemoveAt(_selected);
            _selected = -1;
            RefreshDim();
            RefreshRegions();
            UpdateToolbarStatus();
            e.Handled = true;
        }
    }

    // ---- Mouse (physical screen pixels, from the view that has the mouse) -------------------

    /// <summary>Left button down. Returns true when the view must capture the mouse for a drag.</summary>
    internal bool MouseDown(RegionOverlayView view, Point p, int clickCount)
    {
        if (_ended) return false;
        var scale = view.DpiScale;

        // Grips of the selected region claim the click first.
        if (_selected >= 0 && _selected < _regions.Count)
        {
            var half = GripSizeDips / 2 * scale;
            foreach (var (mode, gx, gy) in GripPoints(_regions[_selected]))
            {
                if (Math.Abs(p.X - gx) > half || Math.Abs(p.Y - gy) > half) continue;
                BeginRegionDrag(mode, _selected, p);
                return true;
            }
        }

        // Click on a committed region: select it AND start a move-drag in one gesture. Walk
        // top-down so the most-recent rect wins on overlap.
        for (var i = _regions.Count - 1; i >= 0; i--)
        {
            if (!_regions[i].Contains(p)) continue;
            // Double-click inside a committed region = confirm all (same as Enter / Apply). The
            // first click of the double already selected the rect and entered Move-drag mode,
            // which MouseUp tore down before this second down arrived.
            if (clickCount == 2)
            {
                _selected = i;
                RefreshRegions();
                ConfirmCommittedRegions();
                return false;
            }
            _selected = i;
            RefreshRegions();
            BeginRegionDrag(RegionDragMode.Move, i, p);
            return true;
        }
        _selected = -1;
        RefreshRegions();

        _dragStart = p;
        _dragScale = scale;
        _selectionRect = new Rect(p, new Size(0, 0));
        // While dragging the cursor-only label and the snap preview are hidden; the selection and
        // its size label take their place.
        foreach (var v in _views)
        {
            v.SetSelectionRect(_selectionRect);
            v.SetSnapRect(null);
            v.SetCursorLabel(null);
        }
        ShowSizeLabel(p, _selectionRect);
        return true;
    }

    internal bool BeginToolbarDrag(RegionOverlayView view, Point grabDips, Point p)
    {
        if (_ended || _toolbarView is null) return false;
        _toolbarDragging = true;
        _toolbarGrabDips = grabDips;
        return true;
    }

    internal void MouseMove(RegionOverlayView view, Point p)
    {
        if (_ended) return;

        // Toolbar drag preempts everything else: moving the toolbar shouldn't draw a new rect or
        // move a region.
        if (_toolbarDragging)
        {
            MoveToolbarTo(p);
            return;
        }

        // Move / resize on a committed region preempts the new-rect drag flow below.
        if (_regionDragMode != RegionDragMode.None)
        {
            ApplyRegionDrag(p);
            return;
        }

        if (_dragStart is not { } start) return;

        var x = Math.Min(start.X, p.X);
        var y = Math.Min(start.Y, p.Y);
        var w = Math.Abs(p.X - start.X);
        var h = Math.Abs(p.Y - start.Y);
        _selectionRect = new Rect(x, y, w, h);
        foreach (var v in _views) v.SetSelectionRect(_selectionRect);
        // Defer the dim rebuild to the next tick (~16 ms throttle) so the 1000 Hz mouse-move
        // stream doesn't queue up geometry rebuilds and starve the timer-driven updates.
        _pendingDim = true;
        ShowSizeLabel(p, _selectionRect);
    }

    internal void MouseUp(RegionOverlayView view, Point p)
    {
        if (_ended) return;

        // End an in-flight toolbar drag: persist the new position so the next overlay session
        // opens with the toolbar where the user left it.
        if (_toolbarDragging)
        {
            _toolbarDragging = false;
            PersistToolbarPosition();
            return;
        }

        // End an in-flight move/resize on a committed region: the rect was already mutated
        // incrementally by ApplyRegionDrag. Make sure the dim hole reflects the final position
        // even if the last pending tick hasn't fired yet.
        if (_regionDragMode != RegionDragMode.None)
        {
            _regionDragMode = RegionDragMode.None;
            _regionDragIndex = -1;
            RefreshDim();
            return;
        }

        if (_dragStart is not { } start) return;
        _dragStart = null;

        var rawW = Math.Abs(p.X - start.X);
        var rawH = Math.Abs(p.Y - start.Y);
        var rawX = Math.Min(start.X, p.X);
        var rawY = Math.Min(start.Y, p.Y);

        foreach (var v in _views) v.SetSelectionRect(null);
        HideSizeLabel();
        RefreshDim();

        // Click without (significant) drag: snap-capture the window under the cursor, but ADD to
        // the committed list rather than closing immediately. Multi-region: the user can click
        // multiple windows / drag multiple rects, then Enter to confirm all.
        if (rawW / _dragScale < ClickThresholdDips && rawH / _dragScale < ClickThresholdDips && _hoveredWindow is { } w)
        {
            CommitRegion(new Rect(w.X, w.Y, w.Width, w.Height));
            return;
        }

        if (rawW < 1 || rawH < 1) return;
        // Drag commits a rect. Stays open so the user can add more regions.
        CommitRegion(new Rect(rawX, rawY, rawW, rawH));
    }

    /// <summary>Wheel up: more zoom (smaller sample, bigger pixels). Range [3, 20] half = [7, 41]
    /// sample. The tick is re-keyed so the next frame re-renders with the new sample size, without
    /// the user having to nudge the mouse.</summary>
    internal void Zoom(int wheelDelta)
    {
        if (_ended) return;
        _half = Math.Clamp(_half + (wheelDelta > 0 ? -1 : 1), 3, 20);
        _lastX = int.MinValue;
        var text = $"Zoom: {_half * 2 + 1}px";
        foreach (var v in _views) v.SetZoomText(text);
    }

    // ---- Regions ----------------------------------------------------------------------------

    /// <summary>Append a new region and recompute the dim mask so the new rect "shows through" the
    /// dim. Auto-selects the just-added region for Delete-key convenience.</summary>
    private void CommitRegion(Rect rect)
    {
        _regions.Add(rect);
        _selected = _regions.Count - 1;
        RefreshDim();
        RefreshRegions();
        UpdateToolbarStatus();
        // Auto-confirm mode: every commit path (drag mouse-up, snap-to-window click) collapses to
        // "first valid region wins". One call site is enough because both funnel through here.
        if (AutoConfirmOnFirstSelection) ConfirmCommittedRegions();
    }

    private void BeginRegionDrag(RegionDragMode mode, int index, Point start)
    {
        _regionDragMode = mode;
        _regionDragIndex = index;
        _regionDragStart = start;
        _regionDragOriginal = _regions[index];
    }

    /// <summary>Apply the in-flight move/resize to the dragged region, refresh its border and
    /// grips, and queue a dim rebuild for the next tick.</summary>
    private void ApplyRegionDrag(Point current)
    {
        if (_regionDragIndex < 0 || _regionDragIndex >= _regions.Count) return;
        var dx = current.X - _regionDragStart.X;
        var dy = current.Y - _regionDragStart.Y;
        var o = _regionDragOriginal;
        double x = o.X, y = o.Y, w = o.Width, h = o.Height;

        if (_regionDragMode == RegionDragMode.Move)
        {
            x = o.X + dx; y = o.Y + dy;
        }
        else
        {
            // Edge / corner resize: each direction tweaks one or both axes.
            switch (_regionDragMode)
            {
                case RegionDragMode.NW: x = o.X + dx; y = o.Y + dy; w = o.Width - dx; h = o.Height - dy; break;
                case RegionDragMode.N:                y = o.Y + dy;                   h = o.Height - dy; break;
                case RegionDragMode.NE:               y = o.Y + dy; w = o.Width + dx; h = o.Height - dy; break;
                case RegionDragMode.E:                              w = o.Width + dx;                    break;
                case RegionDragMode.SE:                             w = o.Width + dx; h = o.Height + dy; break;
                case RegionDragMode.S:                                                h = o.Height + dy; break;
                case RegionDragMode.SW: x = o.X + dx;               w = o.Width - dx; h = o.Height + dy; break;
                case RegionDragMode.W:  x = o.X + dx;               w = o.Width - dx;                    break;
            }
            // Clamp to a 1 px minimum. We don't flip-on-cross like a paint app would; the user can
            // release and re-drag if they want to invert the rect, and a flip mid-drag would
            // relocate every grip under their cursor mid-gesture.
            if (w < 1) w = 1;
            if (h < 1) h = 1;
        }

        _regions[_regionDragIndex] = new Rect(x, y, w, h);
        RefreshRegions();
        _pendingDim = true;
    }

    /// <summary>The eight grip centres of a region in physical pixels, parallel to
    /// <see cref="GripModes"/>.</summary>
    internal static (RegionDragMode Mode, double X, double Y)[] GripPoints(Rect r)
    {
        var midX = r.Left + r.Width / 2;
        var midY = r.Top + r.Height / 2;
        return new[]
        {
            (GripModes[0], r.Left,  r.Top),
            (GripModes[1], midX,    r.Top),
            (GripModes[2], r.Right, r.Top),
            (GripModes[3], r.Left,  midY),
            (GripModes[4], r.Right, midY),
            (GripModes[5], r.Left,  r.Bottom),
            (GripModes[6], midX,    r.Bottom),
            (GripModes[7], r.Right, r.Bottom),
        };
    }

    private void RefreshRegions()
    {
        foreach (var v in _views) v.SetRegions(_regions, _selected);
    }

    /// <summary>Rebuild the dim mask with one hole per committed region (+ the in-flight selection
    /// when a drag is active), on every view. Move/resize already mutated <c>_regions</c>, so the
    /// moved rect must not be added a second time (even-odd would cancel the hole).</summary>
    private void RefreshDim()
    {
        _pendingDim = false;
        var holes = new List<Rect>(_regions);
        if (_dragStart is not null) holes.Add(_selectionRect);
        var array = holes.ToArray();
        foreach (var v in _views) v.SetDim(array);
    }

    /// <summary>Refresh the toolbar's status text + Apply button enabled state on every view.
    /// Called on every region commit / remove so the count stays in sync.</summary>
    private void UpdateToolbarStatus()
    {
        var count = _regions.Count;
        var text = count switch
        {
            0 => Strings.RegionOverlay_NoRegions,
            1 => Strings.RegionOverlay_OneRegion,
            _ => string.Format(CultureInfo.CurrentCulture, _regionCountFormat ??= CompositeFormat.Parse(Strings.RegionOverlay_RegionCount), count),
        };
        foreach (var v in _views) v.SetStatus(text, count > 0);
    }

    // ---- Tick -------------------------------------------------------------------------------

    private void Tick()
    {
        if (_ended) return;
        // Consume a pending dim rebuild. The dim path is the most expensive piece (a geometry with
        // N + 1 figures per view); doing it here at 60 Hz instead of per pointer event (up to
        // 1000 Hz on gaming mice) keeps the lens latency-free during a drag.
        if (_pendingDim) RefreshDim();

        if (!GetCursorPos(out var pt)) return;
        if (pt.X == _lastX && pt.Y == _lastY) return;
        _lastX = pt.X;
        _lastY = pt.Y;

        var target = ViewFor(pt.X, pt.Y);
        if (target is null) return;

        UpdateCursorLabel(target, pt.X, pt.Y);
        UpdateMagnifier(target, pt.X, pt.Y);
        UpdateSnapPreview(pt.X, pt.Y);
    }

    /// <summary>"X: {x}  Y: {y}" at the bottom-right of the monitor under the cursor; hidden on
    /// the others and while a selection is being dragged.</summary>
    private void UpdateCursorLabel(RegionOverlayView target, int x, int y)
    {
        var text = _dragStart is null ? $"X: {x}  Y: {y}" : null;
        foreach (var v in _views) v.SetCursorLabel(v == target ? text : null);
    }

    private void UpdateMagnifier(RegionOverlayView target, int x, int y)
    {
        var sampleSize = _half * 2 + 1;
        var snap = _snapshot;
        BitmapSource? lens = null;
        if (snap is not null)
        {
            // Crop relative to the snapshot's origin (= virtual-screen left/top in physical pixels).
            var rx = x - _half - _snapshotLeft;
            var ry = y - _half - _snapshotTop;
            if (rx < 0) rx = 0;
            if (ry < 0) ry = 0;
            if (rx + sampleSize > snap.PixelWidth) rx = snap.PixelWidth - sampleSize;
            if (ry + sampleSize > snap.PixelHeight) ry = snap.PixelHeight - sampleSize;
            if (rx >= 0 && ry >= 0 && sampleSize > 0)
                lens = new CroppedBitmap(snap, new Int32Rect(rx, ry, sampleSize, sampleSize));
        }

        // The circle and its coordinates label are one block that stays inside the work area of
        // the monitor under the cursor, flipping to the other side of the cursor (and staying
        // there while it fits) instead of being cut off by a screen edge.
        var (circleW, circleH, labelW, labelH) = target.MagnifierSizes();
        var cursorInView = target.ToDips(x, y);
        var placement = MagnifierPlacement.Compute(
            cursorInView.X, cursorInView.Y,
            circleW, circleH, labelW, labelH,
            target.WorkAreaInDips(),
            wasFlippedX: _flippedX,
            wasFlippedY: _flippedY,
            cursorOffset: MagnifierCursorOffset);
        _flippedX = placement.FlippedX;
        _flippedY = placement.FlippedY;

        foreach (var v in _views)
        {
            if (v != target) v.HideMagnifier();
        }
        target.ShowMagnifier(new RegionLensFrame(
            lens,
            Coords: $"X: {x}  Y: {y}",
            CrosshairSize: (double)MagnifierBoxPx / sampleSize,
            placement));
    }

    /// <summary>While idle (no drag), highlight the window under the cursor so the user knows that
    /// a click without drag will snap-capture it.</summary>
    private void UpdateSnapPreview(int x, int y)
    {
        if (_dragStart is not null)
        {
            foreach (var v in _views) v.SetSnapRect(null);
            _hoveredWindow = null;
            return;
        }
        var hover = WindowEnumeration.FindWindowAt(x, y, _windows);
        _hoveredWindow = hover;
        if (hover is null)
        {
            foreach (var v in _views) v.SetSnapRect(null);
            // The label is shared with the drag selection, which owns it while a drag runs.
            HideSizeLabel();
            return;
        }
        var rect = new Rect(hover.X, hover.Y, hover.Width, hover.Height);
        foreach (var v in _views) v.SetSnapRect(rect);

        // Same X / Y / W / H readout the drag shows, so the size of the detected window is visible
        // before clicking.
        ShowSizeLabel(new Point(x, y), rect, hover.X, hover.Y, hover.Width, hover.Height);
    }

    // ---- Labels: each one is drawn whole by the view under the cursor -----------------------

    private void ShowSizeLabel(Point cursor, Rect rect) =>
        ShowSizeLabel(cursor, rect, (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);

    private void ShowSizeLabel(Point cursor, Rect anchorRect, int x, int y, int w, int h)
    {
        var target = ViewFor((int)cursor.X, (int)cursor.Y);
        var text = $"X: {x}  Y: {y}  W: {w}  H: {h}";
        var anchor = new Point(anchorRect.X, anchorRect.Y);
        foreach (var v in _views) v.SetSizeLabel(v == target ? text : null, anchor);
    }

    private void HideSizeLabel()
    {
        foreach (var v in _views) v.SetSizeLabel(null, default);
    }

    // ---- Toolbar ----------------------------------------------------------------------------

    /// <summary>Open position: the saved spot clamped whole into the monitor that holds its centre,
    /// else (nothing saved, or its centre on no monitor) centred on the primary monitor's work
    /// area, 32 DIPs above its bottom.</summary>
    private async System.Threading.Tasks.Task PositionToolbarFromSettingsOrDefaultAsync()
    {
        string? rawX = null, rawY = null;
        var settings = ResolveSettingsStore();
        if (settings is not null)
        {
            try
            {
                rawX = await settings.GetAsync(ToolbarXSettingKey, default).ConfigureAwait(true);
                rawY = await settings.GetAsync(ToolbarYSettingKey, default).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // An unreadable settings store only costs the remembered spot: fall through to the default.
                rawX = rawY = null;
            }
        }
        // The views are closed once the session ended: nothing to place.
        if (_ended) return;

        if (double.TryParse(rawX, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(rawY, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            foreach (var view in _views)
            {
                var size = view.ToolbarSizePx();
                var b = view.Monitor.Bounds;
                if (!ContainsPoint(b, x + size.Width / 2, y + size.Height / 2)) continue;
                SetToolbar(view, ClampWhole(x, y, size, b));
                return;
            }
        }

        var primary = _views.FirstOrDefault(v => v.Monitor.IsPrimary) ?? _views[0];
        var psize = primary.ToolbarSizePx();
        var wa = primary.Monitor.WorkArea;
        SetToolbar(primary, new Point(
            Math.Round(wa.X + (wa.Width - psize.Width) / 2),
            Math.Round(wa.Y + wa.Height - psize.Height - ToolbarBottomMarginDips * primary.DpiScale)));
    }

    /// <summary>The toolbar follows the cursor, clamped whole inside the monitor under the cursor,
    /// and passes whole to the next monitor's view when the cursor crosses into it.</summary>
    private void MoveToolbarTo(Point p)
    {
        var target = ViewFor((int)p.X, (int)p.Y);
        if (target is null) return;
        var size = target.ToolbarSizePx();
        var scale = target.DpiScale;
        var x = p.X - _toolbarGrabDips.X * scale;
        var y = p.Y - _toolbarGrabDips.Y * scale;
        SetToolbar(target, ClampWhole(x, y, size, target.Monitor.Bounds));
    }

    private void SetToolbar(RegionOverlayView view, Point topLeftPx)
    {
        _toolbarView = view;
        _toolbarPos = topLeftPx;
        foreach (var v in _views)
        {
            if (v == view) v.ShowToolbarAt(topLeftPx);
            else v.HideToolbar();
        }
    }

    /// <summary>Keep a <paramref name="size"/> rectangle with its top-left at x/y whole inside the
    /// monitor bounds. The left/top edge wins when the toolbar is bigger than the monitor.</summary>
    private static Point ClampWhole(double x, double y, Size size, Int32Rect bounds) => new(
        Math.Round(Math.Max(bounds.X, Math.Min(x, bounds.X + bounds.Width - size.Width))),
        Math.Round(Math.Max(bounds.Y, Math.Min(y, bounds.Y + bounds.Height - size.Height))));

    private static bool ContainsPoint(Int32Rect b, double x, double y) =>
        x >= b.X && x < b.X + b.Width && y >= b.Y && y < b.Y + b.Height;

    private void PersistToolbarPosition()
    {
        var settings = ResolveSettingsStore();
        if (settings is null) return;
        // Fire-and-forget: the overlay closes shortly after a drag, no value in awaiting.
        _ = settings.SetAsync(ToolbarXSettingKey, _toolbarPos.X.ToString("R", CultureInfo.InvariantCulture), sensitive: false, default);
        _ = settings.SetAsync(ToolbarYSettingKey, _toolbarPos.Y.ToString("R", CultureInfo.InvariantCulture), sensitive: false, default);
    }

    private static ISettingsStore? ResolveSettingsStore()
    {
        if (System.Windows.Application.Current is App app) return app.Services.GetService<ISettingsStore>();
        return null;
    }

    // ---- Monitors ---------------------------------------------------------------------------

    /// <summary>The view on the monitor that holds the point, else on the nearest one.</summary>
    private RegionOverlayView? ViewFor(int screenX, int screenY)
    {
        var index = PerMonitorOverlayHost.NearestMonitor(_monitors, screenX, screenY);
        return index < 0 ? null : _views[index];
    }

    // ---- Result -----------------------------------------------------------------------------

    /// <summary>Bake the committed regions into the final result: bbox = union, output PNG = bbox
    /// dimensions with each region's screen pixels copied in and everything else transparent.
    /// Single-region collapses to a plain crop (same output as the legacy single-region flow);
    /// multi rect gives the composite plus the per-rect parts.</summary>
    private void ConfirmCommittedRegions()
    {
        if (_ended || _regions.Count == 0) return;

        // The regions are already absolute physical-pixel rects.
        var pixelRects = new List<(int X, int Y, int W, int H)>(_regions.Count);
        foreach (var r in _regions)
        {
            var px = (int)Math.Round(r.X);
            var py = (int)Math.Round(r.Y);
            var pw = (int)Math.Round(r.Width);
            var ph = (int)Math.Round(r.Height);
            if (pw > 0 && ph > 0) pixelRects.Add((px, py, pw, ph));
        }
        if (pixelRects.Count == 0) return;

        if (pixelRects.Count == 1)
        {
            // Single region: straight crop, no transparent halo. Keeps the simple-case PNG
            // identical for existing callers / consumers.
            var (sx, sy, sw, sh) = pixelRects[0];
            _result = new CaptureRegion(sx, sy, sw, sh,
                WindowTitle: _hoveredWindow?.Title); // window title only meaningful when single + snap
            PickedSnapshotBytes = TryCropSnapshot(sx, sy, sw, sh);
            End();
            return;
        }

        // Multi-region: bbox + composite (transparent outside any rect).
        var bx = pixelRects.Min(r => r.X);
        var by = pixelRects.Min(r => r.Y);
        var br = pixelRects.Max(r => r.X + r.W);
        var bb = pixelRects.Max(r => r.Y + r.H);
        var bboxW = br - bx;
        var bboxH = bb - by;
        _result = new CaptureRegion(bx, by, bboxW, bboxH);
        PickedSnapshotBytes = BuildMultiRegionComposite(pixelRects, bx, by, bboxW, bboxH);
        // Per-rect crops so workflows that want N independent images (Pin to screen) can use them
        // instead of the composite. Rects that lie outside the snapshot bounds (very rare) produce
        // null and are dropped from the list.
        var parts = new List<(int X, int Y, byte[] Png)>(pixelRects.Count);
        foreach (var r in pixelRects)
        {
            var png = TryCropSnapshot(r.X, r.Y, r.W, r.H);
            if (png is { Length: > 0 }) parts.Add((r.X, r.Y, png));
        }
        if (parts.Count > 0) PickedMultiRegionParts = parts;
        End();
    }

    /// <summary>Render the final multi-region PNG: bbox-sized transparent canvas with each region's
    /// pixels from the screen snapshot copied in at their bbox-relative offset.</summary>
    private byte[]? BuildMultiRegionComposite(IReadOnlyList<(int X, int Y, int W, int H)> rects, int bboxX, int bboxY, int bboxW, int bboxH)
    {
        if (_snapshot is null) return null;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            foreach (var rect in rects)
            {
                // Source coords relative to the snapshot's top-left in the virtual screen, clamped
                // to the snapshot bounds (rects can extend beyond on multi-mon setups when DPI
                // scaling rounds awkwardly).
                int sx = rect.X, sy = rect.Y, sw = rect.W, sh = rect.H;
                int rx = sx - _snapshotLeft;
                int ry = sy - _snapshotTop;
                if (rx < 0) { sw += rx; rx = 0; }
                if (ry < 0) { sh += ry; ry = 0; }
                if (rx + sw > _snapshot.PixelWidth) sw = _snapshot.PixelWidth - rx;
                if (ry + sh > _snapshot.PixelHeight) sh = _snapshot.PixelHeight - ry;
                if (sw <= 0 || sh <= 0) continue;
                var crop = new CroppedBitmap(_snapshot, new Int32Rect(rx, ry, sw, sh));
                dc.DrawImage(crop, new Rect(sx - bboxX, sy - bboxY, sw, sh));
            }
        }
        var rtb = new RenderTargetBitmap(bboxW, bboxH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Crop the screen snapshot to the picked region and PNG-encode it. Returns null if no
    /// snapshot is available or the requested region falls outside the snapshot bounds.
    /// Coordinates are virtual-screen absolute pixels; the snapshot's top-left is at
    /// <c>_snapshotLeft/Top</c> so we offset before cropping.</summary>
    private byte[]? TryCropSnapshot(int absX, int absY, int w, int h)
    {
        var snap = _snapshot;
        if (snap is null) return null;
        var srcX = absX - _snapshotLeft;
        var srcY = absY - _snapshotTop;
        if (srcX < 0 || srcY < 0 || srcX + w > snap.PixelWidth || srcY + h > snap.PixelHeight)
        {
            return null;
        }
        try
        {
            var cropped = new CroppedBitmap(snap, new Int32Rect(srcX, srcY, w, h));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch (ArgumentException) { return null; }
        catch (COMException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
