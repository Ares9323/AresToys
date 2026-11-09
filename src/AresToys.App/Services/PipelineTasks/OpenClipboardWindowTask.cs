using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using AresToys.App.Views;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Opens the clipboard window. Toggle on re-invocation: pressing the shortcut again
/// while it's already up closes it. Inherits the legacy <c>arestoys.open-popup</c> task ID so
/// existing user hotkey bindings and the default Win+V profile keep working unchanged after
/// the legacy <c>OpenPopupTask</c> was retired.</summary>
public sealed class OpenClipboardWindowTask : IPipelineTask
{
    public const string TaskId = "arestoys.open-popup";

    private readonly IServiceProvider _services;

    public OpenClipboardWindowTask(IServiceProvider services) { _services = services; }

    public string Id => TaskId;
    public string DisplayName => "Open clipboard window";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            if (ClipboardWindow.IsOpen) { ClipboardWindow.RequestClose(); return; }

            // Capture the user's foreground window BEFORE we steal focus, so AutoPaster
            // (invoked by PasteSelectedCommand from the toolbar / Enter / Ctrl+digits) can
            // restore it and paste into the right target. Same step PopupWindowController
            // does for the legacy popup.
            var target = _services.GetRequiredService<TargetWindowTracker>();
            target.CaptureCurrentForeground();

            var window = _services.GetRequiredService<ClipboardWindow>();
            // PrepareAsync runs BEFORE Show so the row list paints already populated — no
            // Rows.Clear+refill flash. Also batches the per-category cleanup sweep + the
            // type-filter hydration so the user's first frame is final.
            await window.PrepareAsync();
            window.Show();
            window.Activate();
        });
        return Task.CompletedTask;
    }
}
