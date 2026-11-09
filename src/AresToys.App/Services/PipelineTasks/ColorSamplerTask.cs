using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using AresToys.Core.Pipeline;
using AresToys.Editor.Model;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Opens the magnifier-style on-screen eyedropper at the cursor — samples a pixel from
/// any visible window. The picked colour is stashed in <see cref="PipelineBagKeys.Color"/> so a
/// downstream <c>arestoys.copy-color-*</c> step can emit it in whatever format the user wants
/// (hex, rgb, rgba, FLinearColor, …). If the user cancels the overlay, the pipeline aborts.</summary>
public sealed class ColorSamplerTask : IPipelineTask
{
    public const string TaskId = "arestoys.color-sampler";

    private readonly ScreenColorPickerService _sampler;

    public ColorSamplerTask(ScreenColorPickerService sampler)
    {
        _sampler = sampler;
    }

    public string Id => TaskId;
    public string DisplayName => "Color sampler";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        // PickAtCursor* opens an overlay; must run on the WPF UI thread. We use the no-clipboard
        // variant so downstream steps can decide the output format — and there's no double-write
        // when the user composes a "sample → copy as RGB" workflow.
        var hex = await Application.Current.Dispatcher.InvokeAsync(() => _sampler.SampleAtCursor()).Task.ConfigureAwait(false);
        if (hex is null)
        {
            context.Abort("color sampler cancelled");
            return;
        }
        if (TryParseHex(hex, out var c))
        {
            context.Bag[PipelineBagKeys.Color] = c;
        }
    }

    private static bool TryParseHex(string hex, out ShapeColor color)
    {
        color = ShapeColor.Black;
        var s = hex.Trim().TrimStart('#');
        try
        {
            if (s.Length == 6)
            {
                var r = byte.Parse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var g = byte.Parse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var b = byte.Parse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                color = new ShapeColor(255, r, g, b);
                return true;
            }
        }
        catch (FormatException) { }
        return false;
    }
}
