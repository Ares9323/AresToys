using System.Text.Json.Nodes;
using System.Windows;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Show + activate the main Settings window. Trivial UI step exposed as a pipeline
/// task so any workflow (tray click, hotkey, custom user flow) can route to "open Settings"
/// without each call site re-implementing the show/activate dance. The MainWindow is a
/// singleton in DI; multiple invocations re-show the same instance instead of stacking
/// duplicates.</summary>
public sealed class OpenSettingsTask : IPipelineTask
{
    public const string TaskId = "arestoys.open-settings";

    private readonly IServiceProvider _services;

    public OpenSettingsTask(IServiceProvider services)
    {
        _services = services;
    }

    public string Id => TaskId;
    public string DisplayName => "Open settings window";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        // Optional tab navigation — when the user picks a section in the workflow editor we
        // route SettingsViewModel.SelectedTab to it on every invocation. Empty / missing /
        // unparseable raw → leave the previously-selected tab alone (matches "open settings"
        // semantics, no surprise reset).
        var rawTab = ((string?)config?["tab"])?.Trim();

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var window = (MainWindow)_services.GetService(typeof(MainWindow))!;
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();

            if (!string.IsNullOrEmpty(rawTab)
                && window.DataContext is AresToys.App.ViewModels.SettingsViewModel vm
                && Enum.TryParse<AresToys.App.ViewModels.SettingsTab>(rawTab, ignoreCase: true, out var tab))
            {
                vm.SelectedTab = tab;
            }
        });
        return Task.CompletedTask;
    }
}
