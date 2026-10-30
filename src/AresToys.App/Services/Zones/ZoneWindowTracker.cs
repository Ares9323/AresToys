using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AresToys.Storage.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using static AresToys.App.Services.Zones.ZoneNative;

namespace AresToys.App.Services.Zones;

/// <summary>Where a snapped window sits: the layout and the zone numbers it was snapped into, on which monitor.</summary>
public sealed record ZoneAssignment(string LayoutId, IReadOnlyList<int> ZoneIndexes, string MonitorId);

/// <summary>The one owner of window memory for Window snapping. It remembers which window sits in which zones,
/// keeps those windows in their zones when the screen or a layout changes, and remembers the last zone of each
/// app so a newly created window can go back there. Everything it does is event driven: the hooks, timers and
/// subscriptions are installed in <see cref="Start"/> and removed in <see cref="Stop"/>, so nothing runs while
/// snapping is off. UI thread only, except <see cref="LoadAsync"/>. The writers are the drag service now and the
/// keyboard snapper later; windows are placed through <see cref="ZonePlacer"/>, so the placement code stays in
/// the drag service.</summary>
public sealed class ZoneWindowTracker : IDisposable
{
    public const string KeyAppZoneHistory = "windowsnapping.app_zone_history";

    private const int DisplayDebounceMilliseconds = 800;
    private const int LayoutDebounceMilliseconds = 300;
    private const int SaveDebounceMilliseconds = 1000;
    private const int NewWindowDelayMilliseconds = 250;
    private const int MaxHistoryEntries = 256;

    // A remembered window title is stored next to the app entry as "path", this separator, "title" (lower case).
    // They are capped on their own so a title that keeps changing cannot push the app entries out.
    private const char TitleSeparator = '\n';
    private const int TitlePollMilliseconds = 300;
    private const int TitleStableMilliseconds = 900;
    private const int TitleWaitMilliseconds = 6000;
    private const int MaxTitleEntries = 128;

    /// <summary>Lower case, without the status symbols some apps put in front of a caption (a spinner or a
    /// star before the tab name), so the same tab matches whichever symbol it shows at the moment.</summary>
    internal static string NormalizeTitle(string title)
    {
        var text = title.Trim();
        var start = 0;
        while (start < text.Length && !char.IsLetterOrDigit(text[start])) start++;
        return text[start..].ToLowerInvariant();
    }

    private static string TitleKey(string path, string title) => path.ToLowerInvariant() + TitleSeparator + title;

    /// <summary>True when the two (lower case) titles are the same or one contains the other. An empty title
    /// matches nothing: a window that has no caption yet is placed by its app.</summary>
    internal static bool TitlesMatch(string remembered, string current)
        => remembered.Length > 0 && current.Length > 0
            && (current.Contains(remembered, StringComparison.Ordinal) || remembered.Contains(current, StringComparison.Ordinal));
    private const int HandledPruneThreshold = 256;

    private readonly ZoneLayoutStore _store;
    private readonly ZoneMonitors _monitors;
    private readonly ISettingsStore _settings;
    private readonly ILogger<ZoneWindowTracker> _logger;
    private readonly WinEventProc _hookProc;
    private readonly EventHandler _onStoreChanged;
    private readonly EventHandler _onDisplaySettingsChanged;
    private readonly UserPreferenceChangedEventHandler _onUserPreferenceChanged;

    // Window memory in this run, by window handle.
    private readonly Dictionary<nint, ZoneAssignment> _assignments = [];

    // When each window was first assigned, as a growing number. Kept when the same window is assigned again,
    // so the order of the windows in a zone is the order they were snapped in.
    private readonly Dictionary<nint, long> _order = [];
    private long _orderCounter;

    // The last known zone of each app (lower cased exe path). Read from the settings on load, written back
    // after a pause. Locked because LoadAsync may run off the UI thread.
    private readonly Dictionary<string, HistoryEntry> _history = new(StringComparer.Ordinal);
    private readonly object _historyLock = new();

    // New window handling: windows waiting out the settle delay, and windows already decided on (or already
    // open when the tracker started), which are never touched.
    private readonly Dictionary<nint, DispatcherTimer> _pending = [];
    private readonly HashSet<nint> _handled = [];

    private WindowSnappingSettings _options = WindowSnappingSettings.Default;
    private volatile bool _running;
    private bool _disposed;
    private bool _historyDirty;
    private nint _showHook;
    private nint _uncloakedHook;
    private DispatcherTimer? _displayTimer;
    private DispatcherTimer? _layoutTimer;
    private DispatcherTimer? _saveTimer;

    public ZoneWindowTracker(ZoneLayoutStore store, ZoneMonitors monitors, ISettingsStore settings, ILogger<ZoneWindowTracker> logger)
    {
        _store = store;
        _monitors = monitors;
        _settings = settings;
        _logger = logger;
        _hookProc = OnWinEvent;
        _onStoreChanged = (_, _) => PostToUi(() => ScheduleReapply(ref _layoutTimer, LayoutDebounceMilliseconds,
            _options.ReapplyOnLayoutChange, "layout changed"));
        _onDisplaySettingsChanged = (_, _) => OnDisplayEvent();
        _onUserPreferenceChanged = (_, e) =>
        {
            // A change of the work area (taskbar size, auto hide) is reported under the Desktop category.
            if (e.Category == UserPreferenceCategory.Desktop) OnDisplayEvent();
        };
    }

    /// <summary>Places a window so its visible frame fills a zone rectangle. The drag service sets it to its
    /// own placement (frame compensation, snap record, corners), so there is one placement code path. Without
    /// it the tracker remembers windows but cannot move any.</summary>
    internal Action<nint, PixelRect>? ZonePlacer { get; set; }

    public bool IsRunning => _running;

    /// <summary>Loads the last known zone of each app. Safe from any thread; a damaged value is logged and
    /// replaced by an empty history.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        string? raw;
        try
        {
            raw = await _settings.GetAsync(KeyAppZoneHistory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Zones: could not read the app zone history");
            return;
        }

        Dictionary<string, HistoryEntry>? loaded = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                loaded = JsonSerializer.Deserialize<Dictionary<string, HistoryEntry>>(raw);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Zones: the app zone history is damaged, starting empty");
            }
        }

        lock (_historyLock)
        {
            _history.Clear();
            if (loaded is null) return;
            foreach (var (path, entry) in loaded)
            {
                if (string.IsNullOrEmpty(path) || entry is null || string.IsNullOrEmpty(entry.LayoutId)
                    || entry.MonitorId is null || entry.Zones is not { Length: > 0 })
                {
                    continue;
                }

                _history[path.ToLowerInvariant()] = entry;
            }
        }
    }

    /// <summary>Installs the event wiring for the given options: the layout store subscription, the system
    /// display events, and the window creation hooks when a new window option is on. Safe to call twice.</summary>
    public void Start(WindowSnappingSettings options)
    {
        if (_disposed) return;
        _options = options;
        if (!_running)
        {
            _running = true;
            _store.Changed += _onStoreChanged;
            SystemEvents.DisplaySettingsChanged += _onDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged += _onUserPreferenceChanged;
        }

        UpdateWindowHooks();
    }

    /// <summary>Takes in new options: a new window hook is installed or removed to match. Does nothing while stopped.</summary>
    public void ApplySettings(WindowSnappingSettings options)
    {
        if (_disposed) return;
        _options = options;
        if (_running) UpdateWindowHooks();
    }

    /// <summary>Removes every hook, timer and subscription, writes the app history if it changed, and forgets
    /// the window assignments (a window handle means nothing once snapping has been off).</summary>
    public void Stop()
    {
        StopCore();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) StopCore();
        else dispatcher.Invoke(StopCore);
    }

    /// <summary>Remembers that a window sits in the given zones, and records them as the last known zone of
    /// its app. UI thread only.</summary>
    public void Assign(nint hwnd, string layoutId, IReadOnlyList<int> zoneIndexes, string monitorId)
    {
        if (hwnd == 0 || zoneIndexes.Count == 0) return;

        var indexes = zoneIndexes.ToArray();
        _assignments[hwnd] = new ZoneAssignment(layoutId, indexes, monitorId);
        if (!_order.ContainsKey(hwnd)) _order[hwnd] = ++_orderCounter;
        foreach (var gone in _assignments.Keys.Where(h => !IsWindow(h)).ToList()) Unassign(gone);

        var path = GetProcessPath(hwnd);
        if (string.IsNullOrEmpty(path)) return;

        var title = _options.MatchNewWindowsByTitle ? NormalizeTitle(GetWindowTitle(hwnd)) : string.Empty;

        lock (_historyLock)
        {
            var entry = new HistoryEntry
            {
                LayoutId = layoutId,
                Zones = indexes,
                MonitorId = monitorId,
                Stamp = DateTime.UtcNow.Ticks,
            };
            _history[path.ToLowerInvariant()] = entry;
            if (title.Length > 0)
            {
                _history[TitleKey(path, title)] = entry;
                var titled = _history.Where(e => e.Key.Contains(TitleSeparator)).OrderBy(e => e.Value.Stamp).ToList();
                foreach (var old in titled.Take(Math.Max(0, titled.Count - MaxTitleEntries))) _history.Remove(old.Key);
            }

            _historyDirty = true;
        }

        if (_running) RestartTimer(ref _saveTimer, SaveDebounceMilliseconds, FlushHistory);
        else FlushHistory();
    }

    /// <summary>Forgets a window. The app history is kept: it is the last known zone. UI thread only.</summary>
    public void Unassign(nint hwnd)
    {
        _assignments.Remove(hwnd);
        _order.Remove(hwnd);
    }

    /// <summary>The windows that sit in the same zones as the given one (same layout, same monitor, same set of
    /// zone numbers), in the order they were snapped in, the given window included. Windows that are gone or
    /// minimized are left out. Empty when the window is not assigned. UI thread only.</summary>
    public IReadOnlyList<nint> WindowsInSameZone(nint hwnd)
    {
        if (!_assignments.TryGetValue(hwnd, out var mine)) return [];

        var key = mine.ZoneIndexes.Distinct().OrderBy(i => i).ToArray();
        return _assignments
            .Where(e => e.Key == hwnd
                || (IsWindow(e.Key) && !IsIconic(e.Key)
                    && e.Value.LayoutId == mine.LayoutId && e.Value.MonitorId == mine.MonitorId
                    && e.Value.ZoneIndexes.Distinct().OrderBy(i => i).SequenceEqual(key)))
            .OrderBy(e => _order.GetValueOrDefault(e.Key))
            .Select(e => e.Key)
            .ToList();
    }

    public bool TryGet(nint hwnd, out ZoneAssignment assignment)
    {
        if (_assignments.TryGetValue(hwnd, out var found))
        {
            assignment = found;
            return true;
        }

        assignment = null!;
        return false;
    }

    /// <summary>Puts every assigned window back into its zones on the current work area of its monitor. The
    /// monitor is the one the window was assigned on; when that monitor is gone, the monitor the window is on now.
    /// The layout is the one currently in force on that monitor, so a layout that was edited or reassigned is
    /// followed. An entry is dropped when its window is gone, was maximized, was moved to another monitor by
    /// other means, or its zone numbers no longer exist in the layout. Minimized windows are left as they are.</summary>
    public void ReapplyAll(string reason)
    {
        if (ZonePlacer is not { } place || _assignments.Count == 0) return;

        _logger.LogDebug("Zones: re-placing {Count} snapped windows ({Reason})", _assignments.Count, reason);
        var monitors = _monitors.All();
        if (monitors.Count == 0) return;

        foreach (var (hwnd, assignment) in _assignments.ToList())
        {
            try
            {
                if (!IsWindow(hwnd) || IsZoomed(hwnd))
                {
                    Unassign(hwnd);
                    continue;
                }

                if (IsIconic(hwnd)) continue;

                var current = MonitorOf(hwnd);
                var monitor = monitors.FirstOrDefault(m => m.Id == assignment.MonitorId);
                if (monitor is null)
                {
                    monitor = current is null ? null : monitors.FirstOrDefault(m => m.Id == current.Id);
                }
                else if (current is not null && current.Id != monitor.Id)
                {
                    // Moved to another monitor by other means: it is not in that zone any more.
                    Unassign(hwnd);
                    continue;
                }

                var layout = monitor is null ? null : ZoneDragService.ResolveLayout(_store, monitor.Id);
                if (monitor is null || layout is null
                    || !TryUnion(layout, monitor.WorkArea, assignment.ZoneIndexes, out var target))
                {
                    Unassign(hwnd);
                    continue;
                }

                place(hwnd, target);
                _assignments[hwnd] = assignment with { LayoutId = layout.Id, MonitorId = monitor.Id };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Zones: re-placing {Window} failed", hwnd);
            }
        }
    }

    private void StopCore()
    {
        if (_running)
        {
            _running = false;
            _store.Changed -= _onStoreChanged;
            SystemEvents.DisplaySettingsChanged -= _onDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= _onUserPreferenceChanged;
        }

        RemoveWindowHooks();
        StopTimer(ref _displayTimer);
        StopTimer(ref _layoutTimer);
        StopTimer(ref _saveTimer);
        foreach (var timer in _pending.Values) timer.Stop();
        _pending.Clear();
        _handled.Clear();
        _assignments.Clear();
        _order.Clear();
        FlushHistory();
    }

    // Display and work area changes

    private void OnDisplayEvent()
        => PostToUi(() => ScheduleReapply(ref _displayTimer, DisplayDebounceMilliseconds,
            _options.KeepInZonesOnDisplayChange, "display or work area changed"));

    private void ScheduleReapply(ref DispatcherTimer? timer, int milliseconds, bool enabled, string reason)
    {
        if (!_running || !enabled) return;
        RestartTimer(ref timer, milliseconds, () => ReapplyAll(reason));
    }

    // Newly created windows

    private bool WantsNewWindows => _options.MoveNewWindowsToLastZone || _options.MoveNewWindowsToActiveMonitor;

    private void UpdateWindowHooks()
    {
        if (!WantsNewWindows)
        {
            RemoveWindowHooks();
            foreach (var timer in _pending.Values) timer.Stop();
            _pending.Clear();
            return;
        }

        if (_showHook != 0) return;

        // Windows that exist now are never moved: they are marked as decided before the hooks go in.
        MarkExistingWindows();
        _showHook = SetWinEventHook(EventObjectShow, EventObjectShow, 0, _hookProc, 0, 0, 0);
        _uncloakedHook = SetWinEventHook(EventObjectUncloaked, EventObjectUncloaked, 0, _hookProc, 0, 0, 0);
        if (_showHook == 0)
        {
            _logger.LogWarning("Zones: SetWinEventHook failed, new windows will not be moved");
        }
    }

    private void RemoveWindowHooks()
    {
        if (_showHook != 0) UnhookWinEvent(_showHook);
        if (_uncloakedHook != 0) UnhookWinEvent(_uncloakedHook);
        _showHook = 0;
        _uncloakedHook = 0;
    }

    private void MarkExistingWindows()
    {
        _handled.Clear();
        for (var hwnd = GetWindow(GetDesktopWindow(), GwChild); hwnd != 0; hwnd = GetWindow(hwnd, GwHwndNext))
        {
            if (IsWindowVisible(hwnd)) _handled.Add(hwnd);
        }
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            // EVENT_OBJECT_SHOW also fires for menus, tooltips and child controls: only a window itself counts.
            if (!_running || idObject != ObjidWindow || idChild != ChildIdSelf || hwnd == 0) return;
            if (_handled.Contains(hwnd) || _pending.ContainsKey(hwnd) || _assignments.ContainsKey(hwnd)) return;
            if (!LooksLikeAppWindow(hwnd)) return;

            // Give the app a moment to size and place itself before the window is moved.
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(NewWindowDelayMilliseconds),
            };
            // With the title option on, a terminal opens under a generic caption and its tab is named a moment
            // later: the window waits until the caption has changed and stayed put, or the wait runs out.
            var byTitle = _options.MoveNewWindowsToLastZone && _options.MatchNewWindowsByTitle;
            var started = Environment.TickCount64;
            var firstTitle = byTitle ? GetWindowTitle(hwnd) : string.Empty;
            var lastTitle = firstTitle;
            var stableSince = started;
            timer.Tick += (_, _) =>
            {
                if (byTitle && IsWindow(hwnd))
                {
                    var now = Environment.TickCount64;
                    var title = GetWindowTitle(hwnd);
                    if (title != lastTitle)
                    {
                        lastTitle = title;
                        stableSince = now;
                    }

                    var settled = title != firstTitle && now - stableSince >= TitleStableMilliseconds;
                    if (!settled && now - started < TitleWaitMilliseconds)
                    {
                        timer.Interval = TimeSpan.FromMilliseconds(TitlePollMilliseconds);
                        return;
                    }
                }

                timer.Stop();
                _pending.Remove(hwnd);
                ProcessNewWindow(hwnd);
            };
            _pending[hwnd] = timer;
            timer.Start();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Zones: window event {Event} failed", eventType);
        }
    }

    /// <summary>The cheap part of the test for a window that has just appeared: a top level window with a
    /// title bar that is not a tool window and not one of ours. The shared <see cref="IsSnappable"/> (child
    /// windows, excluded apps, minimized) is applied after the settle delay.</summary>
    private static bool LooksLikeAppWindow(nint hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        var style = GetWindowLongPtr(hwnd, GwlStyle);
        if ((style & WsChild) != 0 || (style & WsCaption) != WsCaption) return false;
        if ((GetWindowLongPtr(hwnd, GwlExStyle) & WsExToolWindow) != 0) return false;
        if (GetAncestor(hwnd, GaRoot) != hwnd) return false;
        return GetWindowThreadProcessId(hwnd, out var processId) != 0 && processId != (uint)Environment.ProcessId;
    }

    private static bool IsCloaked(nint hwnd)
        => DwmGetWindowAttributeInt(hwnd, DwmaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private void ProcessNewWindow(nint hwnd)
    {
        try
        {
            if (!_running || !WantsNewWindows || !IsWindow(hwnd)) return;
            if (_assignments.ContainsKey(hwnd) || _handled.Contains(hwnd)) return;

            // A cloaked window (another virtual desktop, or a UWP frame not shown yet) is looked at again when
            // it is uncloaked; the same goes for a window that is not eligible yet.
            if (IsCloaked(hwnd) || !LooksLikeAppWindow(hwnd) || !IsSnappable(hwnd, _options)) return;

            if (_handled.Count > HandledPruneThreshold) _handled.RemoveWhere(h => !IsWindow(h));
            _handled.Add(hwnd);

            if (_options.MoveNewWindowsToLastZone && TryMoveToLastZone(hwnd)) return;
            if (_options.MoveNewWindowsToActiveMonitor) MoveToActiveMonitor(hwnd);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Zones: moving the new window {Window} failed", hwnd);
        }
    }

    /// <summary>Moves a new window into the zones its app was last snapped into. The monitors are tried in the
    /// order active (under the cursor), the one the window opened on, primary, then the rest, and the first one
    /// whose layout in force is the remembered layout wins. When none matches, the remembered monitor is used with
    /// whatever layout it has now, as long as the remembered zone numbers exist in it.</summary>
    private bool TryMoveToLastZone(nint hwnd)
    {
        if (ZonePlacer is not { } place) return false;
        var path = GetProcessPath(hwnd)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(path)) return false;

        HistoryEntry? entry = null;
        lock (_historyLock)
        {
            if (_options.MatchNewWindowsByTitle)
            {
                var title = NormalizeTitle(GetWindowTitle(hwnd));
                var prefix = path + TitleSeparator;
                entry = _history
                    .Where(e => e.Key.StartsWith(prefix, StringComparison.Ordinal)
                        && TitlesMatch(e.Key[prefix.Length..], title))
                    .OrderByDescending(e => e.Value.Stamp)
                    .Select(e => e.Value)
                    .FirstOrDefault();
            }

            entry ??= _history.GetValueOrDefault(path);
        }

        if (entry?.Zones is null || entry.LayoutId is null) return false;

        var monitors = _monitors.All();
        var ordered = new List<ZoneMonitor>();
        void Add(ZoneMonitor? m)
        {
            if (m is not null && ordered.All(o => o.Id != m.Id)) ordered.Add(m);
        }

        Add(CursorMonitor());
        Add(MonitorOf(hwnd));
        Add(monitors.FirstOrDefault(m => m.IsPrimary));
        foreach (var m in monitors) Add(m);

        foreach (var monitor in ordered)
        {
            var layout = ZoneDragService.ResolveLayout(_store, monitor.Id);
            if (layout is null || layout.Id != entry.LayoutId) continue;
            if (SnapTo(hwnd, place, layout, monitor, entry.Zones)) return true;
        }

        var remembered = monitors.FirstOrDefault(m => m.Id == entry.MonitorId);
        var rememberedLayout = remembered is null ? null : ZoneDragService.ResolveLayout(_store, remembered.Id);
        return remembered is not null && rememberedLayout is not null
            && SnapTo(hwnd, place, rememberedLayout, remembered, entry.Zones);
    }

    private bool SnapTo(nint hwnd, Action<nint, PixelRect> place, ZoneLayout layout, ZoneMonitor monitor, IReadOnlyList<int> indexes)
    {
        if (!TryUnion(layout, monitor.WorkArea, indexes, out var target)) return false;
        place(hwnd, target);
        Assign(hwnd, layout.Id, indexes, monitor.Id);
        return true;
    }

    /// <summary>Moves a new window that opened on another monitor than the active one (the monitor under the
    /// cursor, which is where the user is working; the foreground window is no use because it is usually the new
    /// window itself) to the active monitor. The window keeps its size, is shrunk only if it cannot fit, and
    /// keeps its relative position, clamped inside the work area.</summary>
    private void MoveToActiveMonitor(nint hwnd)
    {
        if (IsZoomed(hwnd) || !GetWindowRect(hwnd, out var rect)) return;
        var active = CursorMonitor();
        var origin = MonitorOf(hwnd);
        if (active is null || origin is null || origin.Id == active.Id) return;

        var width = Math.Min(rect.Right - rect.Left, active.WorkArea.Width);
        var height = Math.Min(rect.Bottom - rect.Top, active.WorkArea.Height);
        var left = Math.Clamp(rect.Left - origin.WorkArea.Left + active.WorkArea.Left,
            active.WorkArea.Left, active.WorkArea.Right - width);
        var top = Math.Clamp(rect.Top - origin.WorkArea.Top + active.WorkArea.Top,
            active.WorkArea.Top, active.WorkArea.Bottom - height);

        if (!SetWindowPos(hwnd, 0, left, top, width, height, SwpNoZOrder | SwpNoActivate))
        {
            _logger.LogDebug("Zones: moving {Window} to the active monitor failed, error {Error}", hwnd,
                System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        }
    }

    // Helpers

    private ZoneMonitor? CursorMonitor()
        => GetCursorPos(out var cursor) ? _monitors.FromPoint(cursor.X, cursor.Y) : null;

    /// <summary>The monitor that holds the center of the window (the nearest one when the center is off screen).
    /// With spanning on this is always the one combined monitor.</summary>
    private ZoneMonitor? MonitorOf(nint hwnd) => _monitors.ForWindow(hwnd);

    private static bool TryUnion(ZoneLayout layout, PixelRect workArea, IReadOnlyList<int> indexes, out PixelRect union)
    {
        union = default;
        var zones = ZoneGeometry.Compute(layout, workArea);
        if (indexes.Count == 0 || indexes.Any(i => i < 0 || i >= zones.Count)) return false;

        union = ZoneGeometry.Union(zones, indexes.ToArray());
        return !union.IsEmpty;
    }

    private static void PostToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private void RestartTimer(ref DispatcherTimer? timer, int milliseconds, Action onTick)
    {
        timer?.Stop();
        var fresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        fresh.Tick += (_, _) =>
        {
            fresh.Stop();
            try
            {
                onTick();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Zones: a window memory step failed");
            }
        };
        timer = fresh;
        fresh.Start();
    }

    private static void StopTimer(ref DispatcherTimer? timer)
    {
        timer?.Stop();
        timer = null;
    }

    // App history persistence

    private void FlushHistory()
    {
        string json;
        lock (_historyLock)
        {
            if (!_historyDirty) return;
            _historyDirty = false;
            if (_history.Count > MaxHistoryEntries)
            {
                foreach (var path in _history.OrderBy(e => e.Value.Stamp).Take(_history.Count - MaxHistoryEntries)
                             .Select(e => e.Key).ToList())
                {
                    _history.Remove(path);
                }
            }

            json = JsonSerializer.Serialize(_history);
        }

        _ = SaveHistoryAsync(json);
    }

    private async Task SaveHistoryAsync(string json)
    {
        try
        {
            await _settings.SetAsync(KeyAppZoneHistory, json, false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Zones: could not save the app zone history");
        }
    }

    /// <summary>The last zones an app was snapped into. Public settable members so the JSON serializer can fill it.</summary>
    private sealed class HistoryEntry
    {
        public string? LayoutId { get; set; }
        public int[]? Zones { get; set; }
        public string? MonitorId { get; set; }
        public long Stamp { get; set; }
    }
}
