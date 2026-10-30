using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Dictation;
using AresToys.App.Services.Hotkeys;
using AresToys.Core.Pipeline;
using AresToys.Hotkeys;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Dictation: records while the key that started the workflow is held, and types what was said when it is
/// released. The key is the workflow's own hotkey (a keyboard key or a mouse button), read from the hotkey settings.</summary>
public sealed class DictateTask : IPipelineTask
{
    public const string TaskId = "arestoys.dictate";

    private readonly DictationService _dictation;
    private readonly HotkeyConfigService _hotkeys;
    private readonly KeyboardHook _hook;
    private readonly ILogger<DictateTask> _logger;

    public DictateTask(DictationService dictation, HotkeyConfigService hotkeys, KeyboardHook hook, ILogger<DictateTask> logger)
    {
        _hook = hook;
        _dictation = dictation;
        _hotkeys = hotkeys;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Dictate";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        if (!context.Bag.TryGetValue(PipelineBagKeys.WorkflowId, out var idObject) || idObject is not string workflowId) return;

        var hotkey = await _hotkeys.GetEffectiveAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (hotkey.VirtualKey == 0)
        {
            // Started from a menu or by hand: there is no key to hold, so there is nothing to wait for.
            _logger.LogInformation("Dictate: workflow {Id} has no hotkey to hold", workflowId);
            return;
        }

        var vk = (int)hotkey.VirtualKey;
        // The hotkey hook swallows the key, so Windows never records it as down: ask the hook itself.
        await _dictation.RunTakeAsync(() => _hook.IsHeld((uint)vk) || (GetAsyncKeyState(vk) & 0x8000) != 0, cancellationToken).ConfigureAwait(false);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
