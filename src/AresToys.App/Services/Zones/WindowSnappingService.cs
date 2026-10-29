using System.Runtime.InteropServices;
using System.Windows;
using AresToys.App.Views;
using AresToys.Storage.Settings;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Zones;

/// <summary>The one place that starts and stops Window snapping and opens its editor. The feature is
/// opt in: until the enabled key says "true" the drag service stays stopped. The pipeline tasks, the
/// tray and the Settings tab all go through this facade.</summary>
public sealed partial class WindowSnappingService : IDisposable
{
    public const string EnabledKey = "windowsnapping.enabled";

    private readonly ZoneLayoutStore _store;
    private readonly ZoneDragService _drag;
    private readonly ZoneKeyboardSnapper _keyboard;
    private readonly ZoneWindowTracker _tracker;
    private readonly ZoneMonitors _monitors;
    private readonly ISettingsStore _settings;
    private readonly ILogger<WindowSnappingService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile bool _enabled;
    private bool _disposed;
    private ZoneEditorWindow? _editor; // UI thread only

    public WindowSnappingService(
        ZoneLayoutStore store,
        ZoneDragService drag,
        ZoneKeyboardSnapper keyboard,
        ZoneWindowTracker tracker,
        ZoneMonitors monitors,
        ISettingsStore settings,
        ILogger<WindowSnappingService> logger)
    {
        _store = store;
        _drag = drag;
        _keyboard = keyboard;
        _tracker = tracker;
        _monitors = monitors;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>False until <see cref="InitializeAsync"/> reads the key. A missing key means disabled.</summary>
    public bool IsEnabled => _enabled;

    public event EventHandler? EnabledChanged;

    /// <summary>Loads the layouts, reads the enabled key and starts snapping when it is on. Never throws.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _store.LoadAsync(ct).ConfigureAwait(false);

            // The editor needs the spanning option even while snapping is off.
            await ApplyMonitorSettingsAsync(ct).ConfigureAwait(false);
            var raw = await _settings.GetAsync(EnabledKey, ct).ConfigureAwait(false);
            _enabled = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
            if (_enabled)
            {
                await _drag.StartAsync().ConfigureAwait(false);
                await StartKeyboardAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Window snapping initialization failed");
        }
        finally
        {
            _gate.Release();
        }
        Raise();
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await _settings.SetAsync(EnabledKey, enabled ? "true" : "false", false, CancellationToken.None)
                .ConfigureAwait(false);
            _enabled = enabled;
            if (enabled)
            {
                await _drag.StartAsync().ConfigureAwait(false);
                await StartKeyboardAsync().ConfigureAwait(false);
            }
            else
            {
                _keyboard.Stop();
                _drag.Stop();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Window snapping could not be switched {State}", enabled ? "on" : "off");
        }
        finally
        {
            _gate.Release();
        }
        Raise();
    }

    public Task ToggleAsync() => SetEnabledAsync(!_enabled);

    public Task ReloadAppearanceAsync() => _drag.ReloadAppearanceAsync();

    /// <summary>Re-reads the drag options (<see cref="WindowSnappingSettings"/>) and the appearance. Call it after
    /// saving them; a drag in progress picks the new values up on its next tick.</summary>
    public async Task ReloadSettingsAsync()
    {
        await _drag.ReloadSettingsAsync().ConfigureAwait(false);
        await ApplyKeyboardSettingsAsync().ConfigureAwait(false);
    }

    /// <summary>Hands the spanning option to <see cref="ZoneMonitors"/>, so every component sees one monitor set.</summary>
    private async Task ApplyMonitorSettingsAsync(CancellationToken ct)
    {
        try
        {
            var options = await WindowSnappingSettings.LoadAsync(_settings, ct).ConfigureAwait(false);
            _monitors.ApplySettings(options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Window snapping: the monitor options could not be applied");
        }
    }

    /// <summary>Lets the keyboard snapper run (its hook goes in only while "Override Windows Snap" is on) and gives it
    /// the saved options. Called with snapping just switched on.</summary>
    private async Task StartKeyboardAsync()
    {
        _keyboard.Start();
        await ApplyKeyboardSettingsAsync().ConfigureAwait(false);
    }

    /// <summary>Hands the saved options to the keyboard snapper, which installs or removes its hook to match. A no
    /// op while snapping is off (the snapper was not started).</summary>
    private async Task ApplyKeyboardSettingsAsync()
    {
        try
        {
            var options = await WindowSnappingSettings.LoadAsync(_settings, CancellationToken.None).ConfigureAwait(false);
            _monitors.ApplySettings(options);
            _keyboard.ApplySettings(options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Window snapping: the keyboard options could not be applied");
        }
    }

    /// <summary>Shows the layout editor, or activates it when it is already open. Safe from any thread.</summary>
    public void OpenEditor(string? layoutId = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || _disposed) return;
        _ = dispatcher.InvokeAsync(() => OpenEditorAsync(layoutId));
    }

    private async Task OpenEditorAsync(string? layoutId)
    {
        try
        {
            if (_editor is not null)
            {
                if (_editor.WindowState == WindowState.Minimized) _editor.WindowState = WindowState.Normal;
                _editor.Activate();
                return;
            }

            // The display is chosen now, by the setting, from where the pointer or the active window is at this moment.
            var options = await WindowSnappingSettings.LoadAsync(_settings, CancellationToken.None);
            _monitors.ApplySettings(options);
            var launchMonitor = _monitors.EditorMonitor(options.LaunchEditorOn);

            var appearance = await ZoneAppearance.LoadAsync(_settings, CancellationToken.None);
            // A second call can land while the appearance was loading.
            if (_editor is not null)
            {
                _editor.Activate();
                return;
            }

            var savedSize = await ZoneEditorWindow.LoadSizeAsync(_settings, CancellationToken.None);
            if (_editor is not null)
            {
                _editor.Activate();
                return;
            }

            var window = new ZoneEditorWindow(_store, _monitors, appearance, layoutId, _settings, savedSize, launchMonitor);
            _editor = window;
            window.Closed += (_, _) => _editor = null;
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            _editor = null;
            _logger.LogError(ex, "Window snapping editor could not be opened");
        }
    }

    /// <summary>Brings the next (or previous) window of the foreground window's zone to the front, going round
    /// the list in the order the windows were snapped in. Returns true when a window was activated; false when
    /// snapping is off, the foreground window is not snapped, or it has no neighbour. Safe from any thread.</summary>
    public bool CycleWindowInZone(bool next)
    {
        if (_disposed || !_enabled) return false;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return false;
        return dispatcher.CheckAccess() ? CycleOnUi(next) : dispatcher.Invoke(() => CycleOnUi(next));
    }

    private bool CycleOnUi(bool next)
    {
        try
        {
            var foreground = ZoneNative.GetForegroundWindow();
            if (foreground == 0) return false;

            var windows = _tracker.WindowsInSameZone(foreground);
            if (windows.Count < 2) return false;

            var at = -1;
            for (var i = 0; i < windows.Count; i++)
            {
                if (windows[i] == foreground)
                {
                    at = i;
                    break;
                }
            }

            if (at < 0) return false;
            var target = windows[(at + (next ? 1 : windows.Count - 1)) % windows.Count];
            return Activate(target);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Window snapping: cycling the windows of the zone failed");
            return false;
        }
    }

    /// <summary>Gives the window the foreground. The hotkey that triggered this was handled by this process, so
    /// SetForegroundWindow normally succeeds. When Windows refuses, the input queue of this thread is attached to
    /// the one of the current foreground window, which makes the call legal, and it is tried once more.</summary>
    private bool Activate(nint hwnd)
    {
        if (ZoneNative.IsIconic(hwnd)) ZoneNative.ShowWindow(hwnd, ZoneNative.SwRestore);
        if (SetForegroundWindow(hwnd)) return true;

        var foreground = ZoneNative.GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : ZoneNative.GetWindowThreadProcessId(foreground, out _);
        var thisThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thisThread
            && AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            if (SetForegroundWindow(hwnd)) return true;
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, foregroundThread, false);
        }

        _logger.LogDebug("Window snapping: Windows refused to bring {Window} to the front", hwnd);
        return false;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    private void Raise()
    {
        try { EnabledChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.LogError(ex, "EnabledChanged handler threw"); }
    }

    public void Dispose()
    {
        _disposed = true;
        _gate.Dispose();
    }
}
