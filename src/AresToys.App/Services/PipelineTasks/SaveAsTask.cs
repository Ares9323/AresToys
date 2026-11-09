using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Opens a Save File dialog letting the user pick destination + filename, then writes
/// <c>bag.payload_bytes</c> there. After success the dialog's chosen path is stored back to
/// <c>bag.local_path</c> so subsequent steps (Show in Explorer, etc.) operate on the new file.
/// User-cancel is a no-op (workflow continues with the original local_path, if any).
/// </summary>
public sealed class SaveAsTask : IPipelineTask
{
    public const string TaskId = "arestoys.save-as";

    private readonly Notifications.ToastBuilderService? _toast;
    private readonly ILogger<SaveAsTask> _logger;

    public SaveAsTask(ILogger<SaveAsTask> logger, Notifications.ToastBuilderService? toast = null)
    {
        _logger = logger;
        _toast = toast;
    }

    public string Id => TaskId;
    public string DisplayName => "Save image as…";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        var bytes = context.Bag.TryGetValue(PipelineBagKeys.PayloadBytes, out var rawBytes) ? rawBytes as byte[] : null;
        // Screen recordings carry no payload_bytes (issue #28), only the saved file: copy that.
        var sourceFile = bytes is null && context.Bag.TryGetValue(PipelineBagKeys.LocalPath, out var rawSource)
                         && rawSource is string sp && File.Exists(sp)
            ? sp
            : null;
        if (bytes is null && sourceFile is null)
        {
            _logger.LogWarning("SaveAsTask: bag.payload_bytes missing; skipping");
            return;
        }
        var ext = context.Bag.TryGetValue(PipelineBagKeys.FileExtension, out var rawExt) && rawExt is string e ? e.TrimStart('.') : "png";
        // Pre-fill the dialog with whatever a save-to-file step produced earlier; otherwise just
        // a "arestoys" placeholder. The user can rename freely; the dialog enforces the extension.
        var initialPath = context.Bag.TryGetValue(PipelineBagKeys.LocalPath, out var rawPath) && rawPath is string p ? p : $"arestoys.{ext}";

        var picked = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save image as",
                FileName = Path.GetFileName(initialPath),
                InitialDirectory = Path.GetDirectoryName(initialPath) ?? string.Empty,
                Filter = $"{ext.ToUpperInvariant()} files (*.{ext})|*.{ext}|All files (*.*)|*.*",
                DefaultExt = ext,
                AddExtension = true,
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        });

        if (string.IsNullOrEmpty(picked))
        {
            _logger.LogDebug("SaveAsTask: user cancelled");
            return;
        }
        try
        {
            if (bytes is not null)
                await File.WriteAllBytesAsync(picked, bytes, cancellationToken).ConfigureAwait(false);
            else if (!string.Equals(Path.GetFullPath(sourceFile!), Path.GetFullPath(picked), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourceFile!, picked, overwrite: true);
            context.Bag[PipelineBagKeys.LocalPath] = picked;
            context.Bag[PipelineBagKeys.Text] = picked;
            _logger.LogDebug("SaveAsTask: saved to {Path}", picked);

            if ((bool?)config?["showNotification"] == true && _toast is not null)
            {
                _toast.ShowFromBag(context, (string?)config?["notificationTitle"]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SaveAsTask: failed to write {Path}", picked);
        }
    }
}
