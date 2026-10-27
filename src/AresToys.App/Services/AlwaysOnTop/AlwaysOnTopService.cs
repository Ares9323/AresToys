using System.Windows.Threading;
using AresToys.Storage.Settings;
using static AresToys.App.Services.AlwaysOnTop.AlwaysOnTopNative;

namespace AresToys.App.Services.AlwaysOnTop;

/// <summary>Pins windows above all others (HWND_TOPMOST) and draws a frame around each pinned
/// window. The single write path for the topmost state: the pipeline task only calls
/// <see cref="ToggleForegroundAsync"/>. Mirrors PowerToys Always On Top: WinEvent hooks follow
/// move, minimize and destroy; a slow timer covers virtual desktop switches and maximize.</summary>
public sealed class AlwaysOnTopService : IDisposable
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    private readonly ISettingsStore _settings;
    private readonly Dictionary<nint, BorderWindow?> _pinned = new();
    private readonly List<nint> _hooks = new();
    private readonly WinEventProc _hookProc;
    private DispatcherTimer? _timer;
    private bool _disposed;

    public AlwaysOnTopService(ISettingsStore settings)
    {
        _settings = settings;
        _hookProc = OnWinEvent;
    }

    public event EventHandler? PinnedChanged;

    /// <summary>Handles of the windows currently pinned by AresToys. UI thread only.</summary>
    public IReadOnlyCollection<nint> PinnedWindows => _pinned.Keys;

    /// <summary>Pins the window that is foreground right now, or unpins it if it is already
    /// topmost. Returns whether the window ended up pinned.</summary>
    public async Task<bool> ToggleForegroundAsync(CancellationToken cancellationToken)
    {
        var target = AresToys.App.Native.AppNativeMethods.GetForegroundWindow();
        if (target == 0 || IsShellWindow(target)) return false;

        var settings = await AlwaysOnTopSettings.LoadAsync(_settings, cancellationToken).ConfigureAwait(false);
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        return await dispatcher.InvokeAsync(() => Toggle(target, settings));
    }

    /// <summary>Re-reads the frame settings and applies them to every window already pinned,
    /// so a change in the settings tab shows up without releasing and pinning again.</summary>
    public async Task ReloadSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await AlwaysOnTopSettings.LoadAsync(_settings, cancellationToken).ConfigureAwait(false);
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        await dispatcher.InvokeAsync(() => ApplySettings(settings));
    }

    private void ApplySettings(AlwaysOnTopSettings settings)
    {
        if (_disposed) return;
        foreach (var (hwnd, border) in _pinned.ToArray())
        {
            if (!settings.ShowFrame)
            {
                border?.Close();
                _pinned[hwnd] = null;
            }
            else if (border is null)
            {
                var created = new BorderWindow(hwnd, settings);
                created.Refresh();
                _pinned[hwnd] = created;
            }
            else
            {
                border.ApplySettings(settings);
                border.Refresh();
            }
        }
    }

    private bool Toggle(nint target, AlwaysOnTopSettings settings)
    {
        if (_disposed || !AresToys.App.Native.AppNativeMethods.IsWindow(target)) return false;

        var topmost = (GetWindowLongPtr(target, GwlExStyle) & WsExTopmost) != 0;
        if (_pinned.ContainsKey(target) || topmost)
        {
            Unpin(target, restoreZOrder: true);
            return false;
        }

        SetWindowPos(target, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        BorderWindow? border = null;
        if (settings.ShowFrame)
        {
            border = new BorderWindow(target, settings);
            border.Refresh();
        }

        _pinned[target] = border;
        EnsureTracking();
        PinnedChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Unpin(nint target, bool restoreZOrder)
    {
        if (_pinned.Remove(target, out var border))
        {
            border?.Close();
        }

        if (restoreZOrder)
        {
            SetWindowPos(target, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }

        if (_pinned.Count == 0) StopTracking();
        PinnedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureTracking()
    {
        if (_hooks.Count == 0)
        {
            _hooks.Add(SetWinEventHook(EventSystemMinimizeStart, EventSystemMinimizeEnd, 0, _hookProc, 0, 0, 0));
            _hooks.Add(SetWinEventHook(EventObjectDestroy, EventObjectDestroy, 0, _hookProc, 0, 0, 0));
            _hooks.Add(SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, 0, _hookProc, 0, 0, 0));
        }

        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => RefreshAll();
            _timer.Start();
        }
    }

    private void StopTracking()
    {
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        _timer?.Stop();
        _timer = null;
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != ObjidWindow || !_pinned.TryGetValue(hwnd, out var border)) return;

        switch (eventType)
        {
            case EventObjectDestroy:
                Unpin(hwnd, restoreZOrder: false);
                break;
            case EventSystemMinimizeStart:
                border?.HideFrame();
                break;
            default:
                border?.Refresh();
                break;
        }
    }

    private void RefreshAll()
    {
        foreach (var (hwnd, border) in _pinned.ToArray())
        {
            if (!AresToys.App.Native.AppNativeMethods.IsWindow(hwnd))
            {
                Unpin(hwnd, restoreZOrder: false);
                continue;
            }

            border?.Refresh();
        }
    }

    private static bool IsShellWindow(nint hwnd)
    {
        var buffer = new char[64];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 && ShellClasses.Contains(new string(buffer, 0, length));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var hwnd in _pinned.Keys.ToArray())
        {
            Unpin(hwnd, restoreZOrder: true);
        }

        StopTracking();
    }
}
