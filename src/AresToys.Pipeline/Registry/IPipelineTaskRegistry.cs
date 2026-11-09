using AresToys.Core.Pipeline;

namespace AresToys.Pipeline.Registry;

public interface IPipelineTaskRegistry
{
    IPipelineTask? Resolve(string taskId);
    IReadOnlyCollection<IPipelineTask> All { get; }
}
