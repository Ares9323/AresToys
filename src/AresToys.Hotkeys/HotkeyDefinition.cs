namespace AresToys.Hotkeys;

public sealed record HotkeyDefinition(string Id, HotkeyModifiers Modifiers, uint VirtualKey)
{
    public bool IsValid()
        => !string.IsNullOrWhiteSpace(Id) && VirtualKey != 0;
}
