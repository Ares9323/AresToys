using System.Text.Json.Nodes;
using System.Windows;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Opens the dialog-style HSB/RGB/CMYK colour picker (wheel + numeric inputs) and
/// stashes the user's pick in <see cref="PipelineBagKeys.Color"/>. Downstream
/// <c>arestoys.copy-color-*</c> steps choose the output format. Cancelling aborts the pipeline.</summary>
public sealed class ColorPickerTask : IPipelineTask
{
    public const string TaskId = "arestoys.color-picker";

    private readonly ColorWheelLauncher _launcher;

    public ColorPickerTask(ColorWheelLauncher launcher)
    {
        _launcher = launcher;
    }

    public string Id => TaskId;
    public string DisplayName => "Color picker";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        var picked = await Application.Current.Dispatcher
            .InvokeAsync(() => _launcher.PickAsync()).Task
            .Unwrap()
            .ConfigureAwait(false);
        if (picked is null)
        {
            context.Abort("color picker cancelled");
            return;
        }
        context.Bag[PipelineBagKeys.Color] = picked;
    }
}
