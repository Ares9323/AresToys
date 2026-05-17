using AresToys.App.Services.Plugins;

namespace AresToys.App.ViewModels;

/// <summary>
/// Builds the runtime list of <see cref="WorkflowActionDescriptor"/>s the user can pick from in
/// the workflow editor. After the Upload consolidation refactor this is just the static catalog —
/// the picker no longer carries one entry per enabled uploader. Users instead pick the unified
/// "Upload to cloud service" / "Shorten URL" entries and choose the destination from a dropdown
/// fed by the runtime plugin registry (see <see cref="WorkflowActionCatalog.OptionsProviders"/>
/// "uploader_ids" / "shortener_ids"). The provider class stays in place in case we need to
/// reintroduce dynamic entries later.
/// </summary>
public sealed class WorkflowActionProvider
{
    private readonly PluginRegistry _registry;

    public WorkflowActionProvider(PluginRegistry registry)
    {
        _registry = registry;
    }

    public Task<IReadOnlyList<WorkflowActionDescriptor>> GetAllAsync(CancellationToken cancellationToken)
        => Task.FromResult(WorkflowActionCatalog.All);
}
