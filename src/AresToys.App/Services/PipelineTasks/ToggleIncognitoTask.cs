using System.Text.Json.Nodes;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Toggles incognito mode (clipboard ingestion gate). Single-step workflow that the
/// incognito hotkey runs.</summary>
public sealed class ToggleIncognitoTask : IPipelineTask
{
    public const string TaskId = "arestoys.toggle-incognito";

    private readonly IncognitoModeService _incognito;

    public ToggleIncognitoTask(IncognitoModeService incognito)
    {
        _incognito = incognito;
    }

    public string Id => TaskId;
    public string DisplayName => "Toggle incognito";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
        => _incognito.ToggleAsync(cancellationToken);
}
