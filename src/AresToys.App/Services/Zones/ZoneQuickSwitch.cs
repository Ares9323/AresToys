using AresToys.Hotkeys;
using Microsoft.Extensions.Logging;
using static AresToys.App.Services.Zones.ZoneNative;

namespace AresToys.App.Services.Zones;

/// <summary>Quick layout switch (PowerToys "Enable quick layout switch"): every layout can have a quick key from 0 to 9
/// (kept by <see cref="ZoneLayoutStore"/>), and pressing Win+Ctrl+Alt+digit anywhere makes the monitor under the cursor
/// use that layout. The ten combinations (and the numpad digits, as PowerToys accepts them) are bindings on the app's one
/// low level <see cref="KeyboardHook"/>, present only while snapping is running and the option is on. The same switch is
/// used by <see cref="ZoneDragService"/> for the digit pressed during a drag, through <see cref="ApplyQuickKeyAsync"/>.
/// The switch itself is just <see cref="ZoneLayoutStore.SetAssignmentAsync"/>: this class stores nothing.</summary>
public sealed class ZoneQuickSwitch : IDisposable
{
    private const string IdPrefix = "windowsnapping.quickkey.";

    private readonly ZoneLayoutStore _store;
    private readonly ZoneMonitors _monitors;
    private readonly KeyboardHook _hook;
    private readonly ILogger<ZoneQuickSwitch> _logger;
    private readonly object _gate = new();
    private bool _started;
    private bool _enabled;
    private bool _registered;
    private bool _disposed;

    public ZoneQuickSwitch(ZoneLayoutStore store, ZoneMonitors monitors, KeyboardHook hook, ILogger<ZoneQuickSwitch> logger)
    {
        _store = store;
        _monitors = monitors;
        _hook = hook;
        _logger = logger;
    }

    /// <summary>True while the ten combinations are bound.</summary>
    public bool IsRegistered
    {
        get { lock (_gate) return _registered; }
    }

    /// <summary>Lets the switch run: the combinations are bound when the option is on. Call it when snapping starts.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _started = true;
            Reconcile();
        }
    }

    /// <summary>Unbinds every combination. Call it when snapping stops.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _started = false;
            Reconcile();
        }
    }

    /// <summary>Takes in new options: the combinations are bound when the option turned on (and the switch was started)
    /// and unbound when it turned off.</summary>
    public void ApplySettings(WindowSnappingSettings settings)
    {
        lock (_gate)
        {
            _enabled = settings.QuickLayoutSwitch;
            Reconcile();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Reconcile();
        }
    }

    /// <summary>Gives the monitor under the point (the one combined monitor while zones span the monitors) the layout
    /// that has this quick key. Returns false when no layout has
    /// the key (or no monitor can be found at the point); true when the monitor now uses that layout, also when it
    /// already did.</summary>
    public async Task<bool> ApplyQuickKeyAsync(int key, int cursorX, int cursorY)
    {
        if (_store.FindByQuickKey(key) is not { } layoutId) return false;
        if (_monitors.FromPoint(cursorX, cursorY) is not { } monitor) return false;

        if (_store.GetAssignment(monitor.Id) != layoutId)
        {
            await _store.SetAssignmentAsync(monitor.Id, layoutId, CancellationToken.None).ConfigureAwait(false);
        }

        return true;
    }

    // Caller holds _gate. Binds or unbinds so the state matches: started, option on, not disposed.
    private void Reconcile()
    {
        var want = _started && _enabled && !_disposed;
        if (want == _registered) return;

        for (var digit = 0; digit <= 9; digit++)
        {
            foreach (var (suffix, vk) in new[] { ("", VkDigit0 + digit), (".numpad", VkNumpad0 + digit) })
            {
                var id = IdPrefix + digit + suffix;
                if (!want)
                {
                    _hook.Unregister(id);
                    continue;
                }

                var captured = digit;
                try
                {
                    _hook.Register(id, HotkeyModifiers.Win | HotkeyModifiers.Control | HotkeyModifiers.Alt, (uint)vk,
                        () => OnHotkey(captured), suppress: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Window snapping: binding quick key {Digit} failed", digit);
                }
            }
        }

        _registered = want;
    }

    // Runs on a thread pool thread (the hook hands callbacks off the hook thread).
    private void OnHotkey(int key)
    {
        _ = SwitchAtCursorAsync(key);
    }

    private async Task SwitchAtCursorAsync(int key)
    {
        try
        {
            if (!GetCursorPos(out var cursor)) return;
            await ApplyQuickKeyAsync(key, cursor.X, cursor.Y).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Window snapping: quick layout switch to key {Key} failed", key);
        }
    }
}
