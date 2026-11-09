using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Pipeline step that opens the WPF editor on the captured bytes (<c>bag.payload_bytes</c>) and
/// blocks until the user saves or cancels. On save, the bytes in the bag — and the
/// <c>NewItem</c> ready to be inserted into history — are replaced with the annotated version,
/// so subsequent steps (upload, save-to-file, copy-image) see the edited image. On cancel, the
/// original bytes are kept and the pipeline continues unchanged.
/// </summary>
public sealed class OpenEditorBeforeUploadTask : IPipelineTask
{
    public const string TaskId = "arestoys.open-editor-before-upload";

    private readonly EditorLauncher _editor;
    private readonly ILogger<OpenEditorBeforeUploadTask> _logger;

    public OpenEditorBeforeUploadTask(EditorLauncher editor, ILogger<OpenEditorBeforeUploadTask> logger)
    {
        _editor = editor;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Open editor before upload";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Bag.TryGetValue(PipelineBagKeys.PayloadBytes, out var rawBytes) || rawBytes is not byte[] bytes)
        {
            _logger.LogWarning("OpenEditorBeforeUploadTask: bag key '{Key}' missing or not byte[]; skipping", PipelineBagKeys.PayloadBytes);
            return;
        }
        if (bytes.Length == 0) return;

        // Pipeline knobs:
        //   - 'fullscreen' → editor maximises on the active monitor + forces fit-to-viewport.
        //   - 'default_tool' → preselects a specific tool on open. Empty/null → use last-used.
        // Three-tier fallback for default_tool so the user-visible dropdown ALWAYS reflects the
        // tool the editor will actually open with, regardless of whether step.Config has the
        // key persisted yet:
        //   1. step.Config["default_tool"] (explicit user choice or DefaultConfigJson on add)
        //   2. catalog descriptor's StringParameter.DefaultValue (covers built-in profiles
        //      seeded before this parameter existed — the UI dropdown shows DefaultValue, the
        //      task now reads the same value)
        //   3. null → EditorLauncher falls through to EditorDefaultsStore (last-used)
        var fullscreen = config?["fullscreen"]?.GetValue<bool>() ?? false;
        // Distinguish three states for the default_tool key:
        //   - key absent      → the workflow predates this parameter; fall back to the catalog
        //                       descriptor's DefaultValue ("Crop") so legacy presets still get
        //                       a sensible override.
        //   - key present, "" → the user explicitly picked "(use last)" from the dropdown; do
        //                       NOT override — let EditorLauncher fall through to the persisted
        //                       last-used tool.
        //   - key present, X  → explicit override; pass X to the launcher.
        string? defaultTool;
        var hasKey = config is JsonObject obj && obj.ContainsKey("default_tool");
        if (hasKey)
        {
            try { defaultTool = config!["default_tool"]?.GetValue<string>() ?? string.Empty; }
            catch { defaultTool = string.Empty; /* legacy non-string */ }
        }
        else
        {
            var catalogDescriptor = AresToys.App.ViewModels.WorkflowActionCatalog.All
                .FirstOrDefault(d => d.TaskId == TaskId);
            var catalogParam = catalogDescriptor?.StringParameters?
                .FirstOrDefault(p => string.Equals(p.Key, "default_tool", StringComparison.Ordinal));
            defaultTool = catalogParam?.DefaultValue;
        }
        _logger.LogDebug("OpenEditorBeforeUploadTask: fullscreen={Fullscreen} defaultTool='{DefaultTool}'",
            fullscreen, defaultTool ?? "(null)");
        var abortOnCancel = config?["abortOnCancel"]?.GetValue<bool>() ?? false;
        var edited = await _editor.EditAsync(bytes, fullscreen, defaultTool, cancellationToken).ConfigureAwait(false);
        if (edited is null)
        {
            if (abortOnCancel)
            {
                // User opted in to "Esc / Cancel from the editor cancels the whole workflow".
                // Common use-case: a capture pipeline where the user wants to throw away the
                // screenshot if they decided not to edit (no save, no history entry, no upload).
                // Without the flag the pipeline just keeps the unedited bytes and proceeds.
                _logger.LogInformation("OpenEditorBeforeUploadTask: user cancelled and abortOnCancel=true → aborting workflow");
                context.Abort("editor cancelled by user");
                return;
            }
            _logger.LogInformation("OpenEditorBeforeUploadTask: user cancelled; keeping original bytes");
            return;
        }

        // Compare against the original bytes so downstream "skip if not modified" steps can
        // tell apart "user saved without changes" (identical bytes — editor confirmed but nothing
        // was edited) from "user saved a genuine edit". SequenceEqual is O(n) but capture-sized
        // payloads are typically 1–5 MB so the cost is in the sub-millisecond band — and runs
        // exactly once per edit, not per pipeline tick.
        var modified = !bytes.AsSpan().SequenceEqual(edited.AsSpan());

        // Replace the in-flight bytes everywhere subsequent steps look:
        // - bag.payload_bytes → seen by SaveToFile, CopyImage, Upload
        // - bag.new_item → seen by AddToHistory (rebuilt with the edited payload)
        context.Bag[PipelineBagKeys.PayloadBytes] = edited;
        if (context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawItem) && rawItem is NewItem item)
        {
            context.Bag[PipelineBagKeys.NewItem] = item with
            {
                Payload = edited,
                PayloadSize = edited.LongLength,
            };
        }

        if (modified) context.Bag[PipelineBagKeys.PayloadModified] = true;
        _logger.LogInformation("OpenEditorBeforeUploadTask: replaced payload with edited version ({Bytes} bytes, modified={Modified})", edited.Length, modified);
    }
}
