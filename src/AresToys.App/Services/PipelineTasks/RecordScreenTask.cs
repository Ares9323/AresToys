using System.Text.Json.Nodes;
using AresToys.Capture.Recording;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Records the screen as MP4 into a temp file and emits the bytes into the pipeline bag.
/// Toggles start/stop (first run starts + awaits stop, second run stops). Format selection +
/// final destination + history insertion are delegated to downstream SaveVideoFileTask +
/// AddToHistoryTask — mirrors the image-capture pipeline shape.
/// </summary>
public sealed class RecordScreenTask : IPipelineTask
{
    public const string TaskId = "arestoys.record-screen";

    private readonly Services.Recording.RecordingCoordinator _recorder;

    public RecordScreenTask(Services.Recording.RecordingCoordinator recorder)
    {
        _recorder = recorder;
    }

    public string Id => TaskId;
    public string DisplayName => "Record screen";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        // Always record MP4 in pipeline mode — the downstream SaveVideoFileTask decides the
        // user-facing output format (mp4 / gif / webm) and transcodes via ffmpeg. Recording
        // always in the same encoder keeps the "start recording" UX fast and uniform; the
        // expensive palette pass (for gif) only happens if the user actually picks gif at save.
        await _recorder.ToggleAsync(RecordingFormat.Mp4, cancellationToken, context).ConfigureAwait(false);
    }
}
