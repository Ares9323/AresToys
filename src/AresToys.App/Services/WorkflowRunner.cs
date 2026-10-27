using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;
using AresToys.Pipeline;
using AresToys.Pipeline.Profiles;

namespace AresToys.App.Services;

/// <summary>
/// Runs a workflow (pipeline profile) by id from a fresh <see cref="PipelineContext"/>. Used by
/// the global hotkey hook and by the tray menu when it wants to invoke a workflow without
/// pre-populating any bag keys (steps capture / load their own input).
/// </summary>
public sealed class WorkflowRunner
{
    private static readonly IReadOnlyDictionary<string, object> EmptyBag = new Dictionary<string, object>();

    private readonly PipelineExecutor _executor;
    private readonly IPipelineProfileStore _profiles;
    private readonly IServiceProvider _services;
    private readonly ILogger<WorkflowRunner> _logger;

    public WorkflowRunner(
        PipelineExecutor executor,
        IPipelineProfileStore profiles,
        IServiceProvider services,
        ILogger<WorkflowRunner> logger)
    {
        _executor = executor;
        _profiles = profiles;
        _services = services;
        _logger = logger;
    }

    public Task RunAsync(string workflowId, CancellationToken cancellationToken)
        => RunAsync(workflowId, EmptyBag, cancellationToken);

    /// <summary>Runs a workflow with a bag already holding <paramref name="initialBag"/>, for
    /// callers that know the input up front (the Explorer resize verbs seed
    /// <see cref="PipelineBagKeys.SelectedFiles"/>). The entries are copied into the fresh
    /// context, so the caller's dictionary is never shared with the run.</summary>
    public async Task RunAsync(
        string workflowId, IReadOnlyDictionary<string, object> initialBag, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        ArgumentNullException.ThrowIfNull(initialBag);
        var profile = await _profiles.GetAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            _logger.LogWarning("WorkflowRunner: workflow '{Id}' not found", workflowId);
            return;
        }
        var ctx = new PipelineContext(_services);
        foreach (var (key, value) in initialBag) ctx.Bag[key] = value;
        await _executor.RunAsync(profile, ctx, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Start a workflow and return immediately, swallowing whatever it throws into the
    /// log. For callers that have nowhere to put an exception and can't wait for the run: the
    /// launcher dismisses itself the moment a key is pressed, so it can't sit blocked on a workflow
    /// that takes seconds or opens UI of its own. An empty id is ignored, and a workflow deleted
    /// after it was wired up is a warning rather than a crash.</summary>
    public void RunDetached(string? workflowId) => RunDetached(workflowId, EmptyBag);

    /// <summary>Same as <see cref="RunDetached(string?)"/> but the run starts with
    /// <paramref name="initialBag"/> already in the bag.</summary>
    public void RunDetached(string? workflowId, IReadOnlyDictionary<string, object> initialBag)
    {
        if (string.IsNullOrWhiteSpace(workflowId)) return;
        ArgumentNullException.ThrowIfNull(initialBag);
        var id = workflowId.Trim();
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(id, initialBag, CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("WorkflowRunner: ran workflow {Id} (detached)", id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WorkflowRunner: detached workflow {Id} failed", id);
            }
        });
    }
}
