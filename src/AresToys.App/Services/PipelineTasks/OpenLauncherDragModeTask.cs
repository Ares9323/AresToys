using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using AresToys.App.Services.Launcher;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Opens the launcher overlay already in drag-and-drop mode — the launcher stays
/// visible while the user drags files / shortcuts / folders out of Explorer onto cells. Same
/// window as <see cref="OpenLauncherMenuTask"/>, just opened with a flag flipped so the user
/// doesn't have to click the in-window toggle every time.</summary>
public sealed class OpenLauncherDragModeTask : IPipelineTask
{
    public const string TaskId = "arestoys.open-launcher-drag-mode";

    private readonly IServiceProvider _services;

    public OpenLauncherDragModeTask(IServiceProvider services) { _services = services; }

    public string Id => TaskId;
    public string DisplayName => "Open launcher (drag mode)";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        // Toggle: invoking again while the launcher is up closes it. Same UX as the plain
        // "Open launcher menu" task: one shortcut, two phases (summon / dismiss). The satellite
        // opens the window in drag mode.
        _ = _services.GetRequiredService<LauncherHostBridge>().ToggleAsync(dragMode: true);
        return Task.CompletedTask;
    }
}
