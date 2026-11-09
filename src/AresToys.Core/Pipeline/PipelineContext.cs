namespace AresToys.Core.Pipeline;

public sealed class PipelineContext
{
    public PipelineContext(IServiceProvider services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Bag = new Dictionary<string, object>(StringComparer.Ordinal);
    }

    public IServiceProvider Services { get; }
    public Dictionary<string, object> Bag { get; }
    public bool Aborted { get; private set; }
    public string? AbortReason { get; private set; }

    public void Abort(string reason)
    {
        if (Aborted) return;
        Aborted = true;
        AbortReason = reason ?? throw new ArgumentNullException(nameof(reason));
    }
}
