using System.Text.Json.Nodes;
using AresToys.App.Services.Zones;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Turns Window snapping on when it is off and off when it is on.</summary>
public sealed class ToggleWindowSnappingTask : IPipelineTask
{
    public const string TaskId = "arestoys.window-snapping-toggle";

    private readonly WindowSnappingService _service;

    public ToggleWindowSnappingTask(WindowSnappingService service)
    {
        _service = service;
    }

    public string Id => TaskId;
    public string DisplayName => "Toggle window snapping";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
        => await _service.ToggleAsync().ConfigureAwait(false);
}
