namespace AresToys.Core.Pipeline;

/// <summary>Tells whether the hotkey that started a workflow is still held. Pipeline cannot reference Hotkeys, so the
/// executor resolves this from <see cref="PipelineContext.Services"/>, as it does <see cref="ICancelHotkeyRegistry"/>.
/// Repeat's "while held" mode and the Dictate task both read the held state through it.</summary>
public interface IWorkflowKeyHold
{
    /// <summary>A check that returns true while the workflow's hotkey (keyboard key or mouse button) is down, or null
    /// when the workflow has no hotkey (started from a menu, a tray item or by hand), so there is no key to hold.</summary>
    Task<Func<bool>?> GetProbeAsync(string workflowId, CancellationToken cancellationToken);
}
