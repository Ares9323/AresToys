namespace AresToys.Hotkeys;

public sealed class HotkeyManager : IHotkeyManager
{
    private readonly IHotkeyRegistrar _registrar;
    private readonly Dictionary<string, int> _idByName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, HotkeyDefinition> _defByWmId = [];
    private IntPtr _hwnd;
    private int _nextWmId = 0x9000;

    /// <summary>Last <see cref="Dispatch"/> tick (ms) per WM hotkey id, for auto-repeat de-bounce.
    /// RegisterHotKey re-delivers WM_HOTKEY for as long as the user holds the combo down (the
    /// low-level KeyboardHook path de-bounces via physical KEYUP tracking; the RegisterHotKey path
    /// gets no KEYUP, so we time-gate instead). Two triggers ~400ms apart on a toggle hotkey cancel
    /// each other — the "toggle wormholes topmost" turning them on then straight back off, so the
    /// user has to press twice. A genuine re-press comes after letting go, well past the window.</summary>
    private readonly Dictionary<int, long> _lastDispatchTickByWmId = [];
    private const long AutoRepeatDebounceMs = 500;

    public HotkeyManager(IHotkeyRegistrar registrar)
    {
        _registrar = registrar;
    }

    public event EventHandler<HotkeyTriggeredEventArgs>? Triggered;

    public void Attach(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) throw new ArgumentException("Handle cannot be zero.", nameof(windowHandle));
        if (_hwnd != IntPtr.Zero && _hwnd != windowHandle)
            throw new InvalidOperationException("HotkeyManager is already attached to a different window.");
        _hwnd = windowHandle;
    }

    public bool Register(HotkeyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.IsValid()) throw new ArgumentException("Invalid hotkey definition.", nameof(definition));
        EnsureAttached();

        if (_idByName.ContainsKey(definition.Id)) return false;

        var wmId = _nextWmId++;
        var ok = _registrar.RegisterHotKey(_hwnd, wmId, definition.Modifiers, definition.VirtualKey);
        if (!ok) return false;

        _idByName[definition.Id] = wmId;
        _defByWmId[wmId] = definition;
        return true;
    }

    public bool Unregister(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!_idByName.TryGetValue(id, out var wmId)) return false;
        var ok = _registrar.UnregisterHotKey(_hwnd, wmId);
        _idByName.Remove(id);
        _defByWmId.Remove(wmId);
        return ok;
    }

    public bool Dispatch(int wmHotkeyId)
    {
        if (!_defByWmId.TryGetValue(wmHotkeyId, out var def)) return false;

        // Swallow OS auto-repeat: RegisterHotKey keeps firing WM_HOTKEY while the key is held.
        // If this dispatch lands within the repeat window of the previous one for the SAME hotkey,
        // treat it as a repeat and don't fire. Refresh the stamp even when swallowing so a long
        // hold keeps suppressing (each repeat re-arms the window); the next real press, which only
        // happens after the user releases and re-presses, falls outside the window and fires.
        var now = Environment.TickCount64;
        if (_lastDispatchTickByWmId.TryGetValue(wmHotkeyId, out var last)
            && now - last < AutoRepeatDebounceMs)
        {
            _lastDispatchTickByWmId[wmHotkeyId] = now;
            return false;
        }
        _lastDispatchTickByWmId[wmHotkeyId] = now;

        Triggered?.Invoke(this, new HotkeyTriggeredEventArgs(def));
        return true;
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        foreach (var (id, _) in _idByName.ToArray()) Unregister(id);
        _hwnd = IntPtr.Zero;
    }

    private void EnsureAttached()
    {
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("HotkeyManager.Attach(...) must be called before registering hotkeys.");
    }
}
