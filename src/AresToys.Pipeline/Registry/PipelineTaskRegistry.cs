using AresToys.Core.Pipeline;

namespace AresToys.Pipeline.Registry;

public sealed class PipelineTaskRegistry : IPipelineTaskRegistry
{
    private readonly Dictionary<string, IPipelineTask> _tasks;

    public PipelineTaskRegistry(IEnumerable<IPipelineTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        _tasks = new Dictionary<string, IPipelineTask>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (_tasks.ContainsKey(task.Id))
                throw new InvalidOperationException($"Duplicate pipeline task id: '{task.Id}'.");
            _tasks[task.Id] = task;
        }
    }

    public IPipelineTask? Resolve(string taskId)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        return _tasks.GetValueOrDefault(taskId);
    }

    public IReadOnlyCollection<IPipelineTask> All => _tasks.Values;
}
