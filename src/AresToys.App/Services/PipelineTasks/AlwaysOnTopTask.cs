using System.Text.Json.Nodes;
using AresToys.App.Services.AlwaysOnTop;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Pins the foreground window above all others, or releases it if already pinned.
/// Single-step workflow that the Always on Top hotkey runs.</summary>
public sealed class AlwaysOnTopTask : IPipelineTask
{
    public const string TaskId = "arestoys.always-on-top";

    private readonly AlwaysOnTopService _service;

    public AlwaysOnTopTask(AlwaysOnTopService service)
    {
        _service = service;
    }

    public string Id => TaskId;
    public string DisplayName => "Always on top";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
        => await _service.ToggleForegroundAsync(cancellationToken).ConfigureAwait(false);
}
