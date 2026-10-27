using System.Text.Json.Nodes;
using AresToys.App.Services.Zones;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Brings the next window snapped into the same zone as the foreground window to the front.
/// Does nothing when Window snapping is off or the foreground window is not snapped.</summary>
public sealed class NextWindowInZoneTask : IPipelineTask
{
    public const string TaskId = "arestoys.window-snapping-next-window";

    private readonly WindowSnappingService _service;

    public NextWindowInZoneTask(WindowSnappingService service)
    {
        _service = service;
    }

    public string Id => TaskId;
    public string DisplayName => "Next window in zone";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        _service.CycleWindowInZone(true);
        return Task.CompletedTask;
    }
}
