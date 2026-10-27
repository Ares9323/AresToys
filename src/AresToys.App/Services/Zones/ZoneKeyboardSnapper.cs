using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AresToys.Hotkeys;
using Microsoft.Extensions.Logging;
using static AresToys.App.Services.Zones.ZoneNative;

namespace AresToys.App.Services.Zones;

/// <summary>The owner of keyboard driven moves for Window snapping (PowerToys "Override Windows Snap"): with the option
/// on, Win+Left, Win+Right, Win+Up and Win+Down move the foreground window between zones instead of running the Windows
/// snap layouts. There is no hook of its own: the four combinations are bindings on the app's one low level
/// <see cref="KeyboardHook"/> (the one every global hotkey uses, which already keeps Win combos away from the shell),
/// present only while snapping is running and the option is on. The binding's per event test only reads state cached in
/// fields (<see cref="_foregroundEligible"/> is refreshed on the UI thread when the foreground window changes), so the
/// hook thread never looks a window up. A match is swallowed; the move is queued on the UI dispatcher, where windows are
/// placed through the shared placer of <see cref="ZoneWindowTracker"/> and the assignment is recorded with
/// <see cref="ZoneWindowTracker.Assign"/>. UI thread only; the public methods may be called from any thread.</summary>
public sealed class ZoneKeyboardSnapper : IDisposable
{
    private const string IdPrefix = "windowsnapping.arrow.";
    private const int MaxPendingMoves = 8;

    // A press that is seen but not swallowed is logged at most this often, so a held key does not flood the log.
    private const long RefusedLogIntervalMilliseconds = 1000;

    // A candidate zone whose center is this many times further sideways than ahead is not "in that direction".
    private const double MaxTangent = 10.0;

    private static readonly (string Name, int VirtualKey)[] Arrows =
    [
        ("left", VkLeft), ("up", VkUp), ("right", VkRight), ("down", VkDown),
    ];

    private readonly ZoneLayoutStore _store;
    private readonly ZoneWindowTracker _tracker;
    private readonly ZoneMonitors _monitors;
    private readonly KeyboardHook _hook;
    private readonly ILogger<ZoneKeyboardSnapper> _logger;
    private readonly WinEventProc _foregroundProc;
    private readonly int _processId = Environment.ProcessId;

    private WindowSnappingSettings _options = WindowSnappingSettings.Default;
    private nint _foregroundHook;
    private bool _started;
    private bool _disposed;
    private int _pendingMoves;
    private long _lastRefusedLog = long.MinValue / 2;

    // Read by the hook thread, written on the UI thread.
    private volatile bool _registered;
    private volatile bool _foregroundEligible;

    public ZoneKeyboardSnapper(ZoneLayoutStore store, ZoneWindowTracker tracker, ZoneMonitors monitors, KeyboardHook hook, ILogger<ZoneKeyboardSnapper> logger)
    {
        _store = store;
        _tracker = tracker;
        _monitors = monitors;
        _hook = hook;
        _logger = logger;
        _foregroundProc = OnForegroundChanged;
    }

    /// <summary>True while the four arrow combinations are bound on the shared keyboard hook.</summary>
    public bool IsHooked => _registered;

    /// <summary>Lets the snapper run: the bindings go in when the option is on. Call it when snapping starts.</summary>
    public void Start() => OnUi(() =>
    {
        _started = true;
        Reconcile();
    });

    /// <summary>Removes the bindings. Call it when snapping stops.</summary>
    public void Stop() => OnUi(() =>
    {
        _started = false;
        Reconcile();
    });

    /// <summary>Takes in new options: the bindings go in when the option turned on (and the snapper was started) and
    /// come out when it turned off. The cached eligibility of the foreground window is worked out again, since the
    /// excluded apps and the child window option take part in it.</summary>
    public void ApplySettings(WindowSnappingSettings settings) => OnUi(() =>
    {
        _options = settings;
        Reconcile();
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        OnUi(Reconcile);
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    private void Reconcile()
    {
        var wanted = _started && !_disposed && _options.OverrideWindowsSnap;
        if (wanted)
        {
            if (_registered) RefreshForeground();
            else Bind();
        }
        else if (_registered)
        {
            Unbind();
        }
    }

    // Binding (UI thread)

    private void Bind()
    {
        // The foreground hook keeps the cached eligibility current. It is delivered on this (UI) thread.
        if (_foregroundHook == 0)
        {
            _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, _foregroundProc, 0, 0, 0);
            if (_foregroundHook == 0)
            {
                _logger.LogWarning("Zones: SetWinEventHook failed, keyboard snapping is unavailable");
                return;
            }
        }

        _registered = true;
        RefreshForeground();

        foreach (var (name, virtualKey) in Arrows)
        {
            var key = virtualKey;
            _hook.Register(IdPrefix + name, HotkeyModifiers.Win, (uint)virtualKey, () => OnSwallowed(key),
                suppress: true, when: () => CanHandle(key), repeat: true);
        }

        _logger.LogInformation("Zones: keyboard snapping hook installed (Win+arrows bound on the shared keyboard hook, shared hook installed: {Installed})",
            _hook.IsInstalled);
        if (!_hook.IsInstalled)
        {
            _logger.LogWarning("Zones: the shared keyboard hook is not installed yet, Win+arrows are taken over once it is");
        }
    }

    private void Unbind()
    {
        foreach (var (name, _) in Arrows) _hook.Unregister(IdPrefix + name);

        _registered = false;
        _foregroundEligible = false;
        if (_foregroundHook != 0)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
        }

        _logger.LogInformation("Zones: keyboard snapping hook removed");
    }

    // The foreground cache (UI thread)

    private void OnForegroundChanged(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
        => RefreshForeground();

    /// <summary>Works out once, off the hook thread, whether Win+arrow may be taken over for the foreground window: it is
    /// not one of ours and passes the shared eligibility test (which may look the process up for the excluded apps).</summary>
    private void RefreshForeground()
    {
        if (!_registered) return;

        try
        {
            var hwnd = GetForegroundWindow();
            _foregroundEligible = hwnd != 0 && !IsOwnWindow(hwnd) && IsSnappable(hwnd, _options);
        }
        catch (Exception ex)
        {
            _foregroundEligible = false;
            _logger.LogWarning(ex, "Zones: checking the foreground window for keyboard snapping failed");
        }
    }

    private bool IsOwnWindow(nint hwnd)
        => GetWindowThreadProcessId(hwnd, out var processId) != 0 && processId == (uint)_processId;

    // The hook thread

    /// <summary>The per event test of the bindings. It runs on the hook thread for a Win+arrow with no other modifier, so
    /// it reads fields only: no window lookup, no logging, no input injection.</summary>
    private bool CanHandle(int key)
    {
        if (!_tracker.IsRunning || _tracker.ZonePlacer is null)
        {
            NoteRefused(key, "snapping is not running", refreshForeground: false);
            return false;
        }

        if (!_foregroundEligible)
        {
            NoteRefused(key, "foreground window not eligible", refreshForeground: true);
            return false;
        }

        return true;
    }

    /// <summary>Records that a Win+arrow was seen and left to Windows, at most once per second. The hook thread only
    /// compares a time stamp; the log line, and the refresh of a cached answer that may have gone stale (a window that
    /// became visible after it took the foreground), run on the thread pool.</summary>
    private void NoteRefused(int key, string reason, bool refreshForeground)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastRefusedLog);
        if (now - last < RefusedLogIntervalMilliseconds) return;
        if (Interlocked.CompareExchange(ref _lastRefusedLog, now, last) != last) return;

        _ = ThreadPool.QueueUserWorkItem(_ =>
        {
            _logger.LogInformation("Win+{Key} not swallowed: {Reason}", KeyName(key), reason);
            if (refreshForeground) Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshForeground));
        });
    }

    // Runs on a thread pool thread (the shared hook hands callbacks off the hook thread). The key was swallowed, so
    // Windows does not move the window: only our move runs.
    private void OnSwallowed(int key)
    {
        _logger.LogInformation("Win+{Key} swallowed, foreground eligible", KeyName(key));
        QueueMove(key);
    }

    private static string KeyName(int key) => key switch
    {
        VkLeft => "Left",
        VkRight => "Right",
        VkUp => "Up",
        _ => "Down",
    };

    private void QueueMove(int key)
    {
        // Holding the key repeats it faster than moves complete; the surplus repeats are dropped.
        if (Interlocked.Increment(ref _pendingMoves) > MaxPendingMoves)
        {
            Interlocked.Decrement(ref _pendingMoves);
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Interlocked.Decrement(ref _pendingMoves);
            return;
        }

        _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            try
            {
                Move(key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Zones: moving the window by keyboard failed");
            }
            finally
            {
                Interlocked.Decrement(ref _pendingMoves);
            }
        });
    }

    // The move

    private enum Direction { Left, Right, Up, Down }

    /// <summary>A monitor with the layout in force on it and that layout's zone rectangles.</summary>
    private sealed record MonitorZones(ZoneMonitor Monitor, ZoneLayout Layout, IReadOnlyList<PixelRect> Zones);

    private readonly record struct Target(MonitorZones View, int Zone);

    private void Move(int key)
    {
        if (!_started || !_registered || !_options.OverrideWindowsSnap) return;
        if (_tracker.ZonePlacer is not { } place) return;

        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || IsOwnWindow(hwnd) || !IsSnappable(hwnd, _options)) return;

        var direction = key switch
        {
            VkLeft => Direction.Left,
            VkRight => Direction.Right,
            VkUp => Direction.Up,
            _ => Direction.Down,
        };

        var views = new List<MonitorZones>();
        foreach (var monitor in _monitors.All())
        {
            if (ZoneDragService.ResolveLayout(_store, monitor.Id) is not { } layout) continue;
            var zones = ZoneGeometry.Compute(layout.Grid, monitor.WorkArea);
            if (zones.Count > 0) views.Add(new MonitorZones(monitor, layout, zones));
        }

        if (views.Count == 0) return;

        var windowRect = VisibleBounds(hwnd);
        if (windowRect.IsEmpty) return;

        var current = _monitors.FromPoint((windowRect.Left + windowRect.Right) / 2, (windowRect.Top + windowRect.Bottom) / 2);
        var view = current is null ? null : views.FirstOrDefault(v => v.Monitor.Id == current.Id);
        if (view is null) return;

        // The tracked zones when they still describe where the window is; otherwise the window counts as not snapped.
        int[] assigned = [];
        if (_tracker.TryGet(hwnd, out var assignment)
            && assignment.MonitorId == view.Monitor.Id
            && assignment.LayoutId == view.Layout.Id
            && assignment.ZoneIndexes.Count > 0
            && assignment.ZoneIndexes.All(i => i >= 0 && i < view.Zones.Count && !view.Zones[i].IsEmpty))
        {
            assigned = [.. assignment.ZoneIndexes];
        }

        var target = _options.MoveBasedOn == ZoneMoveBasis.ZoneIndex
            ? ChooseByIndex(direction, view, views, assigned, windowRect)
            : ChooseByPosition(direction, view, views, assigned, windowRect);
        if (target is not { } chosen) return;

        // The one placement code path, then the one write path for the assignment.
        place(hwnd, chosen.View.Zones[chosen.Zone]);
        if (IsWindow(hwnd))
        {
            _tracker.Assign(hwnd, chosen.View.Layout.Id, [chosen.Zone], chosen.View.Monitor.Id);
        }
    }

    private static PixelRect VisibleBounds(nint hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DwmaExtendedFrameBounds, out var frame, Marshal.SizeOf<ZoneNative.Rect>()) == 0)
        {
            return new PixelRect(frame.Left, frame.Top, frame.Right, frame.Bottom);
        }

        return GetWindowRect(hwnd, out var outer)
            ? new PixelRect(outer.Left, outer.Top, outer.Right, outer.Bottom)
            : new PixelRect(0, 0, 0, 0);
    }

    // Relative position

    /// <summary>The zone that lies in the pressed direction from the window or from the zones it sits in. Without
    /// one, the neighbouring monitor is entered (when moving across monitors is on); otherwise nothing moves.</summary>
    private Target? ChooseByPosition(Direction direction, MonitorZones view, List<MonitorZones> views, int[] assigned, PixelRect windowRect)
    {
        var from = assigned.Length > 0 ? ZoneGeometry.Union(view.Zones, assigned) : windowRect;
        var zone = PickByDirection(direction, from, view.Zones, assigned);
        if (zone >= 0) return new Target(view, zone);

        if (_options.MoveAcrossMonitors && EnterNeighbour(direction, view, views, from) is { } entered) return entered;

        // Nothing lies ahead. A window that is not snapped yet first snaps where it is (like the zone index mode does),
        // so the first press never does nothing.
        if (assigned.Length == 0)
        {
            var inPlace = MostOverlappedZone(view.Zones, windowRect);
            return inPlace >= 0 ? new Target(view, inPlace) : null;
        }

        // A snapped window at the end of the row: wrap to the opposite side, the way PowerToys cycles. The search
        // starts again from a rectangle placed just beyond the opposite edge of the work area.
        var work = view.Monitor.WorkArea;
        var wrapFrom = direction switch
        {
            Direction.Left => new PixelRect(work.Right, from.Top, work.Right + from.Width, from.Bottom),
            Direction.Right => new PixelRect(work.Left - from.Width, from.Top, work.Left, from.Bottom),
            Direction.Up => new PixelRect(from.Left, work.Bottom, from.Right, work.Bottom + from.Height),
            _ => new PixelRect(from.Left, work.Top - from.Height, from.Right, work.Top),
        };
        var wrapped = PickByDirection(direction, wrapFrom, view.Zones, assigned);
        return wrapped >= 0 ? new Target(view, wrapped) : null;
    }

    /// <summary>The same weighting as PowerToys ChooseNextZoneByPosition: the zone centre must lie ahead of the
    /// origin centre, the distance is measured along an ellipse whose long axis points the pressed way (so a zone
    /// straight ahead beats one off to the side), and zones too far off the axis are ignored. Zones that overlap the
    /// origin along the other axis come first, so Right from the top zone stays in the top row.
    /// <paramref name="excluded"/> holds the zones the window already fills.</summary>
    private static int PickByDirection(Direction direction, PixelRect from, IReadOnlyList<PixelRect> zones, int[] excluded)
    {
        var horizontal = direction is Direction.Left or Direction.Right;
        var sign = direction is Direction.Right or Direction.Down ? 1.0 : -1.0;
        var originX = (from.Left + from.Right) / 2.0;
        var originY = (from.Top + from.Bottom) / 2.0;

        var bestOverlapping = -1;
        var bestOverlappingScore = double.MaxValue;
        var best = -1;
        var bestScore = double.MaxValue;
        for (var i = 0; i < zones.Count; i++)
        {
            if (zones[i].IsEmpty || Array.IndexOf(excluded, i) >= 0) continue;

            var z = zones[i];
            var dx = (z.Left + z.Right) / 2.0 - originX;
            var dy = (z.Top + z.Bottom) / 2.0 - originY;
            var ahead = (horizontal ? dx : dy) * sign;
            var aside = Math.Abs(horizontal ? dy : dx);
            if (ahead <= 0 || aside / ahead > MaxTangent) continue;

            var score = ahead / 4.0 + aside * aside / ahead;
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }

            var overlaps = horizontal
                ? Math.Min(z.Bottom, from.Bottom) > Math.Max(z.Top, from.Top)
                : Math.Min(z.Right, from.Right) > Math.Max(z.Left, from.Left);
            if (overlaps && score < bestOverlappingScore)
            {
                bestOverlappingScore = score;
                bestOverlapping = i;
            }
        }

        return bestOverlapping >= 0 ? bestOverlapping : best;
    }

    /// <summary>Enters the nearest monitor lying beyond this one in the pressed direction, at the zone on the
    /// entering edge that lines up with where the window was. Null when there is no such monitor.</summary>
    private static Target? EnterNeighbour(Direction direction, MonitorZones view, List<MonitorZones> views, PixelRect from)
    {
        var horizontal = direction is Direction.Left or Direction.Right;
        var here = view.Monitor.WorkArea;
        var sideCentre = horizontal ? (from.Top + from.Bottom) / 2.0 : (from.Left + from.Right) / 2.0;

        MonitorZones? neighbour = null;
        var bestScore = double.MaxValue;
        foreach (var other in views)
        {
            if (ReferenceEquals(other, view)) continue;

            var area = other.Monitor.WorkArea;
            var gap = direction switch
            {
                Direction.Right => area.Left - here.Right,
                Direction.Left => here.Left - area.Right,
                Direction.Down => area.Top - here.Bottom,
                _ => here.Top - area.Bottom,
            };
            if (gap < 0) continue;

            var sideStart = horizontal ? area.Top : area.Left;
            var sideEnd = horizontal ? area.Bottom : area.Right;
            var offSide = sideCentre < sideStart ? sideStart - sideCentre : sideCentre > sideEnd ? sideCentre - sideEnd : 0.0;
            var score = gap + 2.0 * offSide;
            if (score < bestScore)
            {
                bestScore = score;
                neighbour = other;
            }
        }

        if (neighbour is null) return null;

        // Where the window sat along the edge, as a fraction, carried over to the other monitor.
        var hereStart = horizontal ? here.Top : here.Left;
        var hereSize = Math.Max(horizontal ? here.Height : here.Width, 1);
        var fraction = Math.Clamp((sideCentre - hereStart) / hereSize, 0.0, 1.0);
        var area2 = neighbour.Monitor.WorkArea;
        var mapped = (horizontal ? area2.Top : area2.Left) + fraction * (horizontal ? area2.Height : area2.Width);

        var zone = ZoneOnEdge(direction, neighbour, mapped);
        return zone < 0 ? null : new Target(neighbour, zone);
    }

    /// <summary>Among the zones touching the edge a move in <paramref name="direction"/> enters through (within a
    /// small margin, since spacing leaves a gap at the edge), the one whose span along the edge is nearest
    /// <paramref name="mapped"/>.</summary>
    private static int ZoneOnEdge(Direction direction, MonitorZones view, double mapped)
    {
        var horizontal = direction is Direction.Left or Direction.Right;
        var area = view.Monitor.WorkArea;
        var margin = Math.Max(4.0, (horizontal ? area.Width : area.Height) * 0.05);

        double EdgeDistance(PixelRect z) => direction switch
        {
            Direction.Right => z.Left - area.Left,
            Direction.Left => area.Right - z.Right,
            Direction.Down => z.Top - area.Top,
            _ => area.Bottom - z.Bottom,
        };

        var nearest = double.MaxValue;
        foreach (var z in view.Zones)
        {
            if (!z.IsEmpty) nearest = Math.Min(nearest, EdgeDistance(z));
        }

        var best = -1;
        var bestOff = double.MaxValue;
        var bestCentreOff = double.MaxValue;
        for (var i = 0; i < view.Zones.Count; i++)
        {
            var z = view.Zones[i];
            if (z.IsEmpty || EdgeDistance(z) > nearest + margin) continue;

            var start = horizontal ? z.Top : z.Left;
            var end = horizontal ? z.Bottom : z.Right;
            var off = mapped < start ? start - mapped : mapped > end ? mapped - end : 0.0;
            var centreOff = Math.Abs((start + end) / 2.0 - mapped);
            if (off < bestOff || (off == bestOff && centreOff < bestCentreOff))
            {
                best = i;
                bestOff = off;
                bestCentreOff = centreOff;
            }
        }

        return best;
    }

    // Zone number

    /// <summary>Left and Up go to the previous zone number, Right and Down to the next one. At either end the move
    /// continues on the next or previous monitor (when moving across monitors is on and there is another one) or wraps
    /// to the other end of the same layout. A window that is not snapped starts from the zone it overlaps most, or from
    /// the first (next) or last (previous) zone when it overlaps none.</summary>
    private Target? ChooseByIndex(Direction direction, MonitorZones view, List<MonitorZones> views, int[] assigned, PixelRect windowRect)
    {
        var forward = direction is Direction.Right or Direction.Down;
        var origin = assigned;
        if (origin.Length == 0)
        {
            var overlapped = MostOverlappedZone(view.Zones, windowRect);
            if (overlapped < 0) return new Target(view, forward ? 0 : view.Zones.Count - 1);
            origin = [overlapped];
        }

        var step = forward ? origin.Max() + 1 : origin.Min() - 1;
        if (step >= 0 && step < view.Zones.Count) return new Target(view, step);

        if (_options.MoveAcrossMonitors && views.Count > 1)
        {
            var ordered = views.OrderBy(v => v.Monitor.WorkArea.Left).ThenBy(v => v.Monitor.WorkArea.Top).ToList();
            var at = ordered.FindIndex(v => ReferenceEquals(v, view));
            var next = ordered[((at + (forward ? 1 : -1)) % ordered.Count + ordered.Count) % ordered.Count];
            return new Target(next, forward ? 0 : next.Zones.Count - 1);
        }

        return new Target(view, forward ? 0 : view.Zones.Count - 1);
    }

    private static int MostOverlappedZone(IReadOnlyList<PixelRect> zones, PixelRect window)
    {
        var best = -1;
        long bestArea = 0;
        for (var i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            var width = Math.Min(z.Right, window.Right) - Math.Max(z.Left, window.Left);
            var height = Math.Min(z.Bottom, window.Bottom) - Math.Max(z.Top, window.Top);
            if (width <= 0 || height <= 0) continue;

            var area = (long)width * height;
            if (area > bestArea)
            {
                bestArea = area;
                best = i;
            }
        }

        return best;
    }
}
