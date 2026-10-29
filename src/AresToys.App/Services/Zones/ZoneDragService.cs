using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AresToys.Storage.Settings;
using Microsoft.Extensions.Logging;
using static AresToys.App.Services.Zones.ZoneNative;

namespace AresToys.App.Services.Zones;

/// <summary>Snaps a window into a zone when the user drags it by its title bar with Shift held.
/// Whether snapping is active is never stored: every 16 ms tick (and once more when the drag ends) it
/// is derived from the physical Shift key and the cursor, and a drop moves the window only into a zone
/// that is highlighted at that moment and was already shown on the last tick. UI thread only; the one
/// place where a window is placed into a zone. Start and Stop are safe to call twice.</summary>
public sealed class ZoneDragService : IDisposable
{
    private const int TickMilliseconds = 16;
    private const long ButtonUpGraceMilliseconds = 500;
    private const int PlaceAttempts = 3;
    private const int RestoreTolerance = 8;
    private const byte DragAlpha = 128; // about 50 percent, as PowerToys does
    private const int Windows11Build = 22000;

    private readonly ZoneLayoutStore _store;
    private readonly ZoneWindowTracker _tracker;
    private readonly ZoneMonitors _monitors;
    private readonly ISettingsStore _settings;
    private readonly ILogger<ZoneDragService> _logger;
    private readonly WinEventProc _hookProc;
    private readonly Dictionary<string, ZoneOverlayWindow> _overlays = new(StringComparer.Ordinal);

    private ZoneAppearance _appearance = ZoneAppearance.Default;
    private WindowSnappingSettings _options = WindowSnappingSettings.Default;
    private nint _hook;
    private bool _disposed;

    // Drag bookkeeping. Nothing here says whether snapping is on: that is re-derived each tick.
    private nint _dragWindow;
    private DispatcherTimer? _timer;
    private bool _lastTickHighlighted;
    private long _buttonUpSince;
    private int _anchor = -1;
    private string? _anchorMonitorId;

    // Per drag mouse toggles. They are reset at every drag start and end and never outlive a drag.
    // The previous button states exist only to find rising edges between two ticks.
    private bool _activationToggled;
    private bool _spanToggled;
    private bool _prevSecondary;
    private bool _prevExtra1;
    private bool _prevExtra2;
    private bool _prevMiddle;

    // The dragged window's layered state before it was made see through, so it can be put back exactly.
    private TransparencyState? _transparency;

    // Size before the first snap of each snapped window, so dragging it out restores that size.
    private readonly Dictionary<nint, SnapRecord> _snapRecords = [];

    // Zone rects per monitor and layout seen during the drag, so a tick does not recompute the geometry.
    private readonly Dictionary<string, ZoneCacheEntry> _zoneCache = new(StringComparer.Ordinal);

    public ZoneDragService(ZoneLayoutStore store, ZoneWindowTracker tracker, ZoneMonitors monitors, ISettingsStore settings, ILogger<ZoneDragService> logger)
    {
        _store = store;
        _tracker = tracker;
        _monitors = monitors;
        _settings = settings;
        _logger = logger;
        _hookProc = OnWinEvent;

        // The tracker re-places windows through this class, so there is one placement code path.
        _tracker.ZonePlacer = MoveIntoZone;
    }

    public bool IsRunning => _hook != 0;

    /// <summary>Loads the appearance, the options and the window memory, then installs the move/size hook on
    /// the UI thread. The window tracker runs exactly while this service does.</summary>
    public async Task StartAsync()
    {
        if (_disposed) return;

        var appearance = await LoadAppearanceAsync().ConfigureAwait(false);
        var options = await LoadOptionsAsync().ConfigureAwait(false);
        if (!_tracker.IsRunning) await _tracker.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var dispatcher = Application.Current.Dispatcher;
        await dispatcher.InvokeAsync(() => Install(appearance, options));
    }

    /// <summary>Re-reads the appearance settings; overlays already open pick them up on their next redraw.</summary>
    public async Task ReloadAppearanceAsync()
    {
        if (_disposed) return;

        var appearance = await LoadAppearanceAsync().ConfigureAwait(false);
        var dispatcher = Application.Current.Dispatcher;
        await dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            _appearance = appearance;
            RefreshOverlays();
        });
    }

    /// <summary>Re-reads the drag options (and the appearance, since the style option changes the colors). A drag
    /// in progress uses the new values from its next tick.</summary>
    public async Task ReloadSettingsAsync()
    {
        if (_disposed) return;

        var appearance = await LoadAppearanceAsync().ConfigureAwait(false);
        var options = await LoadOptionsAsync().ConfigureAwait(false);
        var dispatcher = Application.Current.Dispatcher;
        await dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            _appearance = appearance;
            _options = options;
            _monitors.ApplySettings(options);
            _tracker.ApplySettings(options);
            RefreshOverlays();
        });
    }

    // The look overlays draw: the saved appearance resolved through the style option.
    private ZoneAppearance EffectiveAppearance => _appearance.ForStyle(_options.ZoneStyle);

    private void RefreshOverlays()
    {
        var effective = EffectiveAppearance;
        foreach (var overlay in _overlays.Values) overlay.ApplyAppearance(effective, _options.ShowZoneNumber);
    }

    /// <summary>Removes the hook, stops the timer, closes the overlays and forgets any drag in progress.</summary>
    public void Stop()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            StopCore();
        }
        else
        {
            dispatcher.Invoke(StopCore);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private async Task<ZoneAppearance> LoadAppearanceAsync()
    {
        try
        {
            return await ZoneAppearance.LoadAsync(_settings, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Zones: could not read the appearance settings, using the defaults");
            return ZoneAppearance.Default;
        }
    }

    private async Task<WindowSnappingSettings> LoadOptionsAsync()
    {
        try
        {
            return await WindowSnappingSettings.LoadAsync(_settings, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Zones: could not read the snapping options, using the defaults");
            return WindowSnappingSettings.Default;
        }
    }

    private void Install(ZoneAppearance appearance, WindowSnappingSettings options)
    {
        // The options are refreshed even when the hook is already there (a second StartAsync).
        if (_disposed) return;
        _appearance = appearance;
        _options = options;
        _monitors.ApplySettings(options);
        _tracker.Start(options);
        if (_hook != 0) return;

        _hook = SetWinEventHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd, 0, _hookProc, 0, 0, 0);
        if (_hook == 0)
        {
            _logger.LogWarning("Zones: SetWinEventHook failed, window snapping is unavailable");
        }
    }

    private void StopCore()
    {
        if (_hook != 0)
        {
            UnhookWinEvent(_hook);
            _hook = 0;
        }

        EndDrag();
        _tracker.Stop();

        // With snapping off nothing is left half done: windows snapped earlier get their rounded corners back.
        foreach (var hwnd in _snapRecords.Where(r => r.Value.CornersDisabled).Select(r => r.Key).ToList())
        {
            SetCornerPreference(hwnd, DwmwcpDefault);
            _snapRecords[hwnd] = _snapRecords[hwnd] with { CornersDisabled = false };
        }
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (idObject != ObjidWindow || hwnd == 0) return;

            if (eventType == EventSystemMoveSizeStart)
            {
                BeginDrag(hwnd);
            }
            else if (eventType == EventSystemMoveSizeEnd)
            {
                CompleteDrag(hwnd);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Zones: move/size event {Event} failed", eventType);
            EndDrag();
        }
    }

    private void BeginDrag(nint hwnd)
    {
        EndDrag();

        // Resizing a snapped window by hand ends its snapped state: it gets its rounded corners back.
        if (_snapRecords.ContainsKey(hwnd) && IsSizingCursor())
        {
            ReleaseSnapRecord(hwnd);
            return;
        }

        if (!ShouldTrack(hwnd)) return;

        _dragWindow = hwnd;
        ResetToggleInputs();
        RestoreOriginalSize(hwnd);
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(TickMilliseconds) };
        _timer.Tick += OnTick;
        _timer.Start();
        OnTick(this, EventArgs.Empty);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Zones: drag tick failed");
            EndDrag();
        }
    }

    private void Tick()
    {
        if (_dragWindow == 0) return;

        if (!IsWindow(_dragWindow) || KeyDown(VkEscape))
        {
            EndDrag();
            return;
        }

        // The mouse is up but the system has not reported the end of the move: give it a moment, then drop the drag.
        if (LeftButtonDown())
        {
            _buttonUpSince = 0;
        }
        else
        {
            var now = Environment.TickCount64;
            if (_buttonUpSince == 0)
            {
                _buttonUpSince = now;
            }
            else if (now - _buttonUpSince > ButtonUpGraceMilliseconds)
            {
                EndDrag();
                return;
            }
        }

        PollToggleInputs();
        var snap = Evaluate();
        _lastTickHighlighted = snap.Indexes.Count > 0;
        if (snap.Active && _options.TransparentWhileDragging) MakeDragWindowTransparent(_dragWindow);
        else RestoreDragWindow();
        ShowOverlay(snap);
    }

    private void CompleteDrag(nint hwnd)
    {
        if (_dragWindow == 0 || hwnd != _dragWindow) return;

        // A drop snaps only into a zone the user was shown on the last tick and that is still highlighted now.
        var shownLastTick = _lastTickHighlighted;
        PollToggleInputs();
        var snap = Evaluate();
        PixelRect? target = null;
        if (shownLastTick && snap.Indexes.Count > 0 && !KeyDown(VkEscape))
        {
            var union = ZoneGeometry.Union(snap.Zones, snap.Indexes.ToArray());
            if (!union.IsEmpty) target = union;
        }

        EndDrag();
        if (target is not { } zone) return;

        MoveIntoZone(hwnd, zone);
        if (IsWindow(hwnd) && snap.Monitor is { } monitor && ResolveLayout(_store, monitor.Id) is { } layout)
        {
            _tracker.Assign(hwnd, layout.Id, snap.Indexes, monitor.Id);
        }
    }

    private void EndDrag()
    {
        // Put the window back first: this is the one cleanup every exit path (drop, Escape, error, Stop) goes through.
        RestoreDragWindow();
        _dragWindow = 0;
        _lastTickHighlighted = false;
        _buttonUpSince = 0;
        _activationToggled = false;
        _spanToggled = false;
        ClearAnchor();

        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        foreach (var overlay in _overlays.Values) overlay.Close();
        _overlays.Clear();
        _zoneCache.Clear();
    }

    /// <summary>Samples the mouse buttons that toggle snapping for the drag in progress. Each rising edge since
    /// the previous sample flips a flag: the secondary or an extra button flips the activation (when that option
    /// is on), the middle button flips multi zone spanning (when that option is on). The previous states are
    /// always refreshed, so turning an option on mid drag does not produce a phantom edge.</summary>
    private void PollToggleInputs()
    {
        var (secondary, extra1, extra2, middle) = ReadToggleButtons();

        if (_options.NonPrimaryMouseToggle)
        {
            if (secondary && !_prevSecondary) _activationToggled = !_activationToggled;
            if (extra1 && !_prevExtra1) _activationToggled = !_activationToggled;
            if (extra2 && !_prevExtra2) _activationToggled = !_activationToggled;
        }

        if (_options.MiddleClickSpanning && middle && !_prevMiddle) _spanToggled = !_spanToggled;

        _prevSecondary = secondary;
        _prevExtra1 = extra1;
        _prevExtra2 = extra2;
        _prevMiddle = middle;
    }

    /// <summary>Starts a drag with no toggles on and the buttons already held counted as "previous".</summary>
    private void ResetToggleInputs()
    {
        _activationToggled = false;
        _spanToggled = false;
        (_prevSecondary, _prevExtra1, _prevExtra2, _prevMiddle) = ReadToggleButtons();
    }

    // GetAsyncKeyState reports physical buttons: with swapped buttons the logical secondary one is the left.
    private static (bool Secondary, bool Extra1, bool Extra2, bool Middle) ReadToggleButtons()
        => (KeyDown(GetSystemMetrics(SmSwapButton) != 0 ? VkLButton : VkRButton),
            KeyDown(VkXButton1), KeyDown(VkXButton2), KeyDown(VkMButton));

    /// <summary>Reads Shift, Ctrl, the toggles, the cursor, the monitor and the layout right now and says whether
    /// snapping is active and which zones, if any, are highlighted. Snapping is active when Shift is down (or up,
    /// with "Shift to activate" off), flipped by the per drag activation toggle. With multi zone on (Ctrl held,
    /// or the per drag span toggle) the zone first hit becomes an anchor and every zone in the rectangle spanned
    /// by the anchor and the zone under the cursor is highlighted; without it the anchor is dropped and only the
    /// zone under the cursor is.</summary>
    private Snap Evaluate()
    {
        var shift = KeyDown(VkShift);
        var active = (_options.ShiftToActivate ? shift : !shift) != _activationToggled;
        if (!active)
        {
            ClearAnchor();
            return Snap.None;
        }

        if (!GetCursorPos(out var cursor))
        {
            ClearAnchor();
            return Snap.ActiveOnly;
        }

        var monitor = _monitors.FromPoint(cursor.X, cursor.Y);
        if (monitor is null || !TryGetZones(monitor, out var layout, out var zones))
        {
            ClearAnchor();
            return Snap.ActiveOnly;
        }

        var hits = ZoneHitTester.ZonesFromPoint(zones, cursor.X, cursor.Y, layout.SensitivityRadius, _options.OverlapAlgorithm);
        var current = hits.Count > 0 ? hits[0] : -1;

        var multi = KeyDown(VkControl) || (_options.MiddleClickSpanning && _spanToggled);
        if (!multi || _anchorMonitorId != monitor.Id)
        {
            ClearAnchor();
        }

        if (!multi)
        {
            // Every zone the cursor captured: near the line between two zones that is both of them.
            return new Snap(true, monitor, zones, hits);
        }

        if (_anchor < 0 && current >= 0)
        {
            _anchor = current;
            _anchorMonitorId = monitor.Id;
        }

        return new Snap(true, monitor, zones, _anchor >= 0 ? ZoneHitTester.CombinedRange(zones, [_anchor, current]) : []);
    }

    private void ClearAnchor()
    {
        _anchor = -1;
        _anchorMonitorId = null;
    }

    /// <summary>The layout in force on a monitor: its assignment, or the first saved layout when it has none (or the
    /// assigned one is gone), or null when no layout is saved (the monitor then does not snap). Shared with the
    /// window tracker so both always agree.</summary>
    internal static ZoneLayout? ResolveLayout(ZoneLayoutStore store, string monitorId)
    {
        var assigned = store.GetAssignment(monitorId);
        var layout = assigned is null ? null : store.Find(assigned);
        return layout ?? (store.Layouts is { Count: > 0 } all ? all[0] : null);
    }

    /// <summary>The layout of a monitor and its zone rectangles, or false when the monitor has no usable zones.
    /// The rectangles are computed once per monitor, area and layout, and the same list is returned until one of
    /// them changes (the overlay relies on that to skip redrawing).</summary>
    private bool TryGetZones(ZoneMonitor monitor, out ZoneLayout layout, out IReadOnlyList<PixelRect> zones)
    {
        layout = null!;
        zones = [];
        var resolved = ResolveLayout(_store, monitor.Id);
        if (resolved is null) return false;

        if (!_zoneCache.TryGetValue(monitor.Id, out var entry)
            || entry.Area != monitor.WorkArea || !ReferenceEquals(entry.Layout, resolved))
        {
            entry = new ZoneCacheEntry(monitor.WorkArea, resolved, ZoneGeometry.Compute(resolved, monitor.WorkArea));
            _zoneCache[monitor.Id] = entry;
        }

        if (entry.Zones.Count == 0) return false;
        layout = resolved;
        zones = entry.Zones;
        return true;
    }

    /// <summary>Shows what the snap says: nothing when snapping is off; otherwise the zones of the monitor under
    /// the cursor with the highlight, plus (with "show on all monitors") the zones of every other monitor with
    /// nothing highlighted. Overlays of monitors not shown are hidden, not closed.</summary>
    private void ShowOverlay(Snap snap)
    {
        var shown = new HashSet<string>(StringComparer.Ordinal);
        if (snap.Active)
        {
            IReadOnlyList<ZoneMonitor> monitors = _options.ShowOnAllMonitors
                ? _monitors.All()
                : snap.Monitor is null ? [] : [snap.Monitor];

            foreach (var monitor in monitors)
            {
                IReadOnlyList<PixelRect> zones;
                IReadOnlyList<int> highlight;
                if (snap.Monitor is not null && monitor.Id == snap.Monitor.Id)
                {
                    zones = snap.Zones;
                    highlight = snap.Indexes;
                }
                else if (TryGetZones(monitor, out _, out zones))
                {
                    highlight = [];
                }
                else
                {
                    continue;
                }

                if (!_overlays.TryGetValue(monitor.Id, out var overlay))
                {
                    overlay = new ZoneOverlayWindow(EffectiveAppearance, _options.ShowZoneNumber);
                    _overlays[monitor.Id] = overlay;
                }

                overlay.Present(monitor.WorkArea, zones, highlight);
                shown.Add(monitor.Id);
            }
        }

        foreach (var (id, overlay) in _overlays)
        {
            if (!shown.Contains(id)) overlay.HideOverlay();
        }
    }

    private bool ShouldTrack(nint hwnd)
    {
        // The shared eligibility test (also used by the window tracker); looked up once per drag.
        if (_overlays.Values.Any(o => o.Handle == hwnd) || !IsSnappable(hwnd, _options)) return false;

        // Only a move is snapped. The move/size loop starts for both, and a resize shows a sizing cursor.
        // The caption hit test is not used: the File Explorer tab strip is client area, not HTCAPTION.
        return !IsSizingCursor();
    }

    /// <summary>When the window was snapped earlier and still has the bounds it was placed at, puts it back to
    /// the size it had before the first snap, keeping the cursor at the same horizontal proportion and the same
    /// vertical offset inside it. A window resized by hand since the snap just loses its record.</summary>
    private void RestoreOriginalSize(nint hwnd)
    {
        if (!_snapRecords.TryGetValue(hwnd, out var record)) return;
        ReleaseSnapRecord(hwnd);

        // Without a recorded size (the window was maximized when first snapped) or with the option off the
        // record is just dropped.
        if (!_options.RestoreSizeOnUnsnap || record.OriginalBounds is not { } original) return;

        if (!GetWindowRect(hwnd, out var now) || !GetCursorPos(out var cursor)) return;
        if (IsZoomed(hwnd)) return;

        var snapped = record.SnappedBounds;
        if (Math.Abs(now.Left - snapped.Left) > RestoreTolerance || Math.Abs(now.Top - snapped.Top) > RestoreTolerance
            || Math.Abs(now.Right - snapped.Right) > RestoreTolerance || Math.Abs(now.Bottom - snapped.Bottom) > RestoreTolerance)
        {
            return;
        }

        var width = original.Width;
        var height = original.Height;
        if (_monitors.FromPoint(cursor.X, cursor.Y) is { } monitor)
        {
            width = Math.Min(width, monitor.WorkArea.Width);
            height = Math.Min(height, monitor.WorkArea.Height);
        }

        var oldWidth = now.Right - now.Left;
        var proportion = oldWidth > 0 ? (double)(cursor.X - now.Left) / oldWidth : 0.5;
        proportion = Math.Clamp(proportion, 0.0, 1.0);
        var offsetY = Math.Clamp(cursor.Y - now.Top, 0, Math.Max(height - 1, 0));
        var left = cursor.X - (int)Math.Round(proportion * width);
        var top = cursor.Y - offsetY;

        if (!SetWindowPos(hwnd, 0, left, top, width, height, SwpNoZOrder | SwpNoActivate))
        {
            _logger.LogDebug("Zones: restoring the size of {Window} failed, error {Error}", hwnd, Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Forgets a snapped window: its record goes and, when the snap had squared its corners, they are
    /// given back (the window's own default preference).</summary>
    private void ReleaseSnapRecord(nint hwnd)
    {
        _tracker.Unassign(hwnd);
        if (!_snapRecords.Remove(hwnd, out var record)) return;
        if (record.CornersDisabled) SetCornerPreference(hwnd, DwmwcpDefault);
    }

    private void SetCornerPreference(nint hwnd, int preference)
    {
        // The attribute exists on Windows 11 only.
        if (Environment.OSVersion.Version.Build < Windows11Build || !IsWindow(hwnd)) return;

        var value = preference;
        var result = DwmSetWindowAttribute(hwnd, DwmaWindowCornerPreference, ref value, sizeof(int));
        if (result != 0)
        {
            _logger.LogDebug("Zones: DwmSetWindowAttribute(corner preference {Preference}) failed for {Window}, HRESULT {Result:X8}",
                preference, hwnd, result);
        }
    }

    /// <summary>Makes the dragged window about half see through. The previous extended style and, when the window
    /// was already layered, its layered attributes are kept for <see cref="RestoreDragWindow"/>. A window that is
    /// layered but whose attributes cannot be read (it paints itself with UpdateLayeredWindow) is left alone.</summary>
    private void MakeDragWindowTransparent(nint hwnd)
    {
        if (_transparency is not null || !IsWindow(hwnd)) return;

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        var wasLayered = (exStyle & WsExLayered) != 0;
        uint colorKey = 0, flags = 0;
        byte alpha = 255;
        if (wasLayered && !GetLayeredWindowAttributes(hwnd, out colorKey, out alpha, out flags)) return;

        if (!wasLayered) SetWindowLongPtr(hwnd, GwlExStyle, exStyle | WsExLayered);
        if (!SetLayeredWindowAttributes(hwnd, 0, DragAlpha, LwaAlpha))
        {
            _logger.LogDebug("Zones: SetLayeredWindowAttributes failed for {Window}", hwnd);
            if (!wasLayered) SetWindowLongPtr(hwnd, GwlExStyle, exStyle);
            return;
        }

        _transparency = new TransparencyState(hwnd, exStyle, wasLayered, colorKey, alpha, flags);
    }

    /// <summary>Undoes <see cref="MakeDragWindowTransparent"/>: a window that was layered gets its attributes back;
    /// one that was not gets its old extended style, which drops the layered bit and with it the alpha.</summary>
    private void RestoreDragWindow()
    {
        if (_transparency is not { } state) return;
        _transparency = null;
        if (!IsWindow(state.Window)) return;

        if (state.WasLayered) SetLayeredWindowAttributes(state.Window, state.ColorKey, state.Alpha, state.Flags);
        else SetWindowLongPtr(state.Window, GwlExStyle, state.ExStyle);
    }

    private void PruneSnapRecords()
    {
        foreach (var hwnd in _snapRecords.Keys.Where(h => !IsWindow(h)).ToList()) _snapRecords.Remove(hwnd);
    }

    /// <summary>Places the window so its visible frame fills the zone. The invisible resize border of a
    /// window is measured (extended frame bounds against the window rect) and added to the rect; the check
    /// is repeated because a window crossing to a monitor with another DPI may resize itself on arrival.</summary>
    private void MoveIntoZone(nint hwnd, PixelRect zone)
    {
        if (!IsWindow(hwnd) || IsIconic(hwnd)) return;

        // The size before the first snap is kept; a maximized window is not recorded.
        var wasZoomed = IsZoomed(hwnd);
        PixelRect? original = null;
        if (!wasZoomed && !_snapRecords.ContainsKey(hwnd) && GetWindowRect(hwnd, out var before))
        {
            original = new PixelRect(before.Left, before.Top, before.Right, before.Bottom);
        }

        if (wasZoomed) ShowWindow(hwnd, SwRestore);
        PlaceInZone(hwnd, zone);

        var corners = _options.DisableRoundedCorners;
        if (corners) SetCornerPreference(hwnd, DwmwcpDoNotRound);

        if (GetWindowRect(hwnd, out var placed))
        {
            var snapped = new PixelRect(placed.Left, placed.Top, placed.Right, placed.Bottom);
            _snapRecords[hwnd] = _snapRecords.TryGetValue(hwnd, out var existing)
                ? existing with { SnappedBounds = snapped, CornersDisabled = existing.CornersDisabled || corners }
                : new SnapRecord(original, snapped, corners);
        }
        else if (corners)
        {
            // No bounds, so no record to restore from later: give the corners back right away.
            SetCornerPreference(hwnd, DwmwcpDefault);
        }

        PruneSnapRecords();
    }

    private void PlaceInZone(nint hwnd, PixelRect zone)
    {
        for (var attempt = 0; attempt < PlaceAttempts; attempt++)
        {
            if (!GetWindowRect(hwnd, out var outer)) return;
            var visible = DwmGetWindowAttribute(hwnd, DwmaExtendedFrameBounds, out var frame, Marshal.SizeOf<ZoneNative.Rect>()) == 0
                ? frame
                : outer;

            if (visible.Left == zone.Left && visible.Top == zone.Top
                && visible.Right == zone.Right && visible.Bottom == zone.Bottom)
            {
                return;
            }

            var insetLeft = visible.Left - outer.Left;
            var insetTop = visible.Top - outer.Top;
            var insetRight = outer.Right - visible.Right;
            var insetBottom = outer.Bottom - visible.Bottom;

            if (!SetWindowPos(hwnd, 0, zone.Left - insetLeft, zone.Top - insetTop,
                    zone.Width + insetLeft + insetRight, zone.Height + insetTop + insetBottom,
                    SwpNoZOrder | SwpNoActivate))
            {
                // Typically an elevated window that a normal process may not move.
                _logger.LogDebug("Zones: SetWindowPos failed for {Window}, error {Error}", hwnd, Marshal.GetLastPInvokeError());
                return;
            }
        }
    }

    private static bool KeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    // GetAsyncKeyState reports the physical buttons, so with swapped buttons the logical left one is the right.
    private static bool LeftButtonDown() => KeyDown(GetSystemMetrics(SmSwapButton) != 0 ? VkRButton : VkLButton);

    /// <summary>What one evaluation found. <c>Active</c> says snapping is on for this tick; <c>Monitor</c> is the
    /// monitor under the cursor and is null when it has no usable zones.</summary>
    private readonly record struct Snap(bool Active, ZoneMonitor? Monitor, IReadOnlyList<PixelRect> Zones, IReadOnlyList<int> Indexes)
    {
        public static readonly Snap None = new(false, null, [], []);
        public static readonly Snap ActiveOnly = new(true, null, [], []);
    }

    /// <summary>Outer bounds of a window before its first snap (null when it was maximized) and where the last
    /// snap placed it, and whether the snap switched off its rounded corners.</summary>
    private readonly record struct SnapRecord(PixelRect? OriginalBounds, PixelRect SnappedBounds, bool CornersDisabled);

    private readonly record struct ZoneCacheEntry(PixelRect Area, ZoneLayout Layout, IReadOnlyList<PixelRect> Zones);

    private readonly record struct TransparencyState(nint Window, nint ExStyle, bool WasLayered, uint ColorKey, byte Alpha, uint Flags);
}
