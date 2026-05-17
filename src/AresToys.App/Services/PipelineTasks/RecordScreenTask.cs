using System.Text.Json.Nodes;
using AresToys.App.Services.Notifications;
using AresToys.Capture.Recording;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Toggles screen recording (start on first run, stop on second). The format (mp4 / gif) comes
/// from <c>config.format</c>. Used as the single step of the screen-recording workflows.
/// </summary>
public sealed class RecordScreenTask : IPipelineTask
{
    public const string TaskId = "arestoys.record-screen";

    private readonly Services.Recording.RecordingCoordinator _recorder;
    private readonly ToastBuilderService? _toast;

    public RecordScreenTask(Services.Recording.RecordingCoordinator recorder, ToastBuilderService? toast = null)
    {
        _recorder = recorder;
        _toast = toast;
    }

    public string Id => TaskId;
    public string DisplayName => "Toggle screen recording";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        var format = ((string?)config?["format"])?.ToLowerInvariant() switch
        {
            "gif" => RecordingFormat.Gif,
            _ => RecordingFormat.Mp4,
        };
        // Pass the pipeline context so the coordinator can populate bag.local_path /
        // bag.payload_bytes / bag.new_item on a successful stop.
        await _recorder.ToggleAsync(format, cancellationToken, context).ConfigureAwait(false);

        // Post-stop notification (only meaningful on a successful stop — start path returns
        // before there's anything to notify about). Gated on the showNotification config + the
        // presence of bag.local_path that the coordinator only sets when persisting succeeded.
        if ((bool?)config?["showNotification"] == true
            && _toast is not null
            && context.Bag.ContainsKey(PipelineBagKeys.LocalPath))
        {
            _toast.ShowFromBag(context, (string?)config?["notificationTitle"]);
        }
    }
}
