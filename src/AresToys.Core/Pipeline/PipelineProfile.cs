namespace AresToys.Core.Pipeline;

public sealed record PipelineProfile(
    string Id,
    string DisplayName,
    string Trigger,
    IReadOnlyList<PipelineStep> Steps,
    /// <summary>Optional global hotkey that runs this workflow when pressed. Null = no hotkey.</summary>
    HotkeyBinding? Hotkey = null,
    /// <summary>Marks profiles that ship with the app. Built-in workflows can be edited and reset
    /// to defaults but not deleted from the UI; user-created workflows can be deleted.</summary>
    bool IsBuiltIn = false);

/// <summary>
/// A keyboard shortcut bound to a workflow. <see cref="Modifiers"/> uses the same bitfield values
/// as <c>AresToys.Hotkeys.HotkeyModifiers</c> (Alt=1, Control=2, Shift=4, Win=8); kept as a raw int
/// here so <c>AresToys.Core</c> doesn't pull in the Hotkeys assembly.
/// </summary>
public sealed record HotkeyBinding(int Modifiers, uint VirtualKey);
