using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Wormholes;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Restores a named wormhole layout preset. The step's config carries a
/// <c>"preset"</c> string; if it matches a saved preset (case-insensitive) the manager applies it
/// to the live windows and binds the current monitor setup to it. A blank or non-matching name is
/// a logged no-op — the workflow keeps running. Backs the built-in "Switch wormhole preset"
/// profile (no default hotkey): the user types / picks the preset name in the workflow editor.</summary>
public sealed class WormholeRestorePresetTask : IPipelineTask
{
    public const string TaskId = "arestoys.wormhole-restore-preset";

    private readonly IWormholeWindowManager _manager;
    private readonly ILogger<WormholeRestorePresetTask> _logger;

    public WormholeRestorePresetTask(IWormholeWindowManager manager, ILogger<WormholeRestorePresetTask> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Switch wormhole preset";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = ((string?)config?["preset"])?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            _logger.LogDebug("WormholeRestorePresetTask: no preset name configured — no-op");
            return;
        }

        // Resolve the canonical name so the restore (and the logged feedback) is case-insensitive.
        var presets = await _manager.ListPresetsAsync(cancellationToken).ConfigureAwait(false);
        var match = presets.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            _logger.LogWarning("WormholeRestorePresetTask: no preset matches '{Name}' — no-op", name);
            return;
        }

        await _manager.RestorePresetAsync(match, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("WormholeRestorePresetTask: restored preset '{Name}'", match);
    }
}
