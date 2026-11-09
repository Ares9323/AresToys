using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Launcher;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Workflow step: fire a launcher cell programmatically. User picks <c>tab</c>
/// (1-9 / 0) and <c>key</c> (QWERTY / punctuation / F1-F10) — if key is a function key
/// (F1..F10) the tab parameter is ignored since the F-strip is global.
/// Empty / unmapped cells surface a toast ("Launcher cell &lt;tab:key&gt; is empty") so the user
/// notices the workflow didn't do anything, rather than the pipeline silently moving on.</summary>
public sealed class LauncherTriggerKeyTask : IPipelineTask
{
    public const string TaskId = "arestoys.launcher.trigger-key";

    private readonly LauncherActionService _launcher;
    private readonly IToastNotifier _notifier;
    private readonly ILogger<LauncherTriggerKeyTask> _logger;

    public LauncherTriggerKeyTask(LauncherActionService launcher, IToastNotifier notifier, ILogger<LauncherTriggerKeyTask> logger)
    {
        _launcher = launcher;
        _notifier = notifier;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Trigger launcher key";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tab = (string?)config?["tab"] ?? string.Empty;
        var key = (string?)config?["key"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning("LauncherTriggerKeyTask: no 'key' configured; skipping");
            return;
        }

        var ok = await _launcher.FireAsync(tab, key, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            // Empty / unmapped cell → toast so the user sees the workflow stopped. The
            // ActionService already logged the detail; this is just the user-facing
            // confirmation that "yes, your hotkey fired, but the slot is empty".
            var label = string.IsNullOrEmpty(tab) || (key.Length >= 2 && key[0] == 'F' && char.IsDigit(key[1]))
                ? key
                : $"{tab}:{key}";
            _notifier.Show("AresToys", $"Launcher cell {label} is empty.");
        }
    }
}
