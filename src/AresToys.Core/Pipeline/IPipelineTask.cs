using System.Text.Json.Nodes;

namespace AresToys.Core.Pipeline;

public interface IPipelineTask
{
    string Id { get; }
    string DisplayName { get; }
    PipelineTaskKind Kind { get; }

    JsonNode? ConfigSchema => null;

    Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken);
}
