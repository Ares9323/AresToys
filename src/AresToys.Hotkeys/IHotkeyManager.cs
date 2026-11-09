namespace AresToys.Hotkeys;

public interface IHotkeyManager : IDisposable
{
    /// <summary>Raised whenever a registered hotkey fires (always on the message-loop thread).</summary>
    event EventHandler<HotkeyTriggeredEventArgs>? Triggered;

    /// <summary>Bind the manager to a window handle that is part of the message loop. Must be called once.</summary>
    void Attach(IntPtr windowHandle);

    /// <summary>Register a hotkey. Returns false if Win32 refuses (already taken, invalid combination).</summary>
    bool Register(HotkeyDefinition definition);

    /// <summary>Unregister by definition Id. Returns false if not registered.</summary>
    bool Unregister(string id);

    /// <summary>Forward a WM_HOTKEY message received on the attached window. Returns true if handled.</summary>
    bool Dispatch(int wmHotkeyId);
}

/// <summary>Internal abstraction for the Win32 calls; allows the manager to be tested without OS interop.</summary>
public interface IHotkeyRegistrar
{
    bool RegisterHotKey(IntPtr hwnd, int id, HotkeyModifiers modifiers, uint virtualKey);
    bool UnregisterHotKey(IntPtr hwnd, int id);
}
