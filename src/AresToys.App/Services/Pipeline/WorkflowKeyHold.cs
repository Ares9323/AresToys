using AresToys.App.Services.Hotkeys;
using AresToys.Core.Pipeline;
using AresToys.Hotkeys;

namespace AresToys.App.Services.Pipeline;

/// <summary>The one place that reads whether a workflow's hotkey is still held: the key comes from the hotkey
/// settings, the held state from the keyboard hook.</summary>
internal sealed class WorkflowKeyHold : IWorkflowKeyHold
{
    private readonly HotkeyConfigService _hotkeys;
    private readonly KeyboardHook _hook;

    public WorkflowKeyHold(HotkeyConfigService hotkeys, KeyboardHook hook)
    {
        _hotkeys = hotkeys;
        _hook = hook;
    }

    public async Task<Func<bool>?> GetProbeAsync(string workflowId, CancellationToken cancellationToken)
    {
        var hotkey = await _hotkeys.GetEffectiveAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (hotkey.VirtualKey == 0) return null;
        var vk = (uint)hotkey.VirtualKey;
        // The hook swallows the key and its repeats, so only the hook knows it is down. Never GetAsyncKeyState: with
        // the KEYUP swallowed, Windows can keep a key it saw go down marked as down forever.
        return () => _hook.IsHeld(vk);
    }
}
