using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using AresToys.App.Services.Launcher;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Opens the launcher overlay — a 3×10 keyboard grid where every printable key is
/// mapped to a path / shortcut / shell target. Acts on its own (no payload required), so it
/// can be the entire workflow behind a global shortcut, MaxLaunchpad-style.</summary>
public sealed class OpenLauncherMenuTask : IPipelineTask
{
    public const string TaskId = "arestoys.open-launcher-menu";

    private readonly IServiceProvider _services;

    public OpenLauncherMenuTask(IServiceProvider services) { _services = services; }

    public string Id => TaskId;
    public string DisplayName => "Open launcher menu";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        // The window lives in the non elevated satellite process, which decides between open and close:
        // the same shortcut becomes "open" on first press, "close" on the second. Fire and forget, the
        // bridge logs a failure and never blocks the caller.
        _ = _services.GetRequiredService<LauncherHostBridge>().ToggleAsync(dragMode: false);
        return Task.CompletedTask;
    }
}
