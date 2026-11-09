namespace AresToys.Hotkeys;

public sealed class HotkeyTriggeredEventArgs : EventArgs
{
    public HotkeyTriggeredEventArgs(HotkeyDefinition definition)
    {
        Definition = definition;
    }
    public HotkeyDefinition Definition { get; }
}
