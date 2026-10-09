using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// First step of the "upload selected file" workflow. Talks to the Shell.Application COM object
/// (the same automation surface PowerShell uses) to find the currently-foreground Explorer window
/// and pull the path of its first selected item. Loads it as bytes, fills the standard bag keys
/// (<see cref="PipelineBagKeys.PayloadBytes"/>, <see cref="PipelineBagKeys.FileExtension"/>,
/// <see cref="PipelineBagKeys.NewItem"/>) so every downstream step (history / upload / toast /
/// clipboard URL) is identical to a tray "Upload file…" invocation.
///
/// Caveats:
/// <list type="bullet">
///   <item>Only "real" Explorer windows are seen — third-party file managers (Total Commander,
///         Files app, OpenFileDialog) aren't in <c>Shell.Application.Windows()</c>.</item>
///   <item>Multi-selection: only the first item is taken. Bag layout is single-payload today;
///         multi-file routing would require a separate task.</item>
///   <item>Foreground window must be Explorer at trigger time — there's no "remember the last
///         active Explorer". A 50ms delay gives a tray-menu launch time to dismiss its popup so
///         <c>GetForegroundWindow</c> returns the underlying Explorer instead.</item>
/// </list>
/// </summary>
public sealed class CaptureSelectedExplorerFileTask : IPipelineTask
{
    public const string TaskId = "arestoys.capture-selected-explorer-file";

    private readonly ILogger<CaptureSelectedExplorerFileTask> _logger;

    public CaptureSelectedExplorerFileTask(ILogger<CaptureSelectedExplorerFileTask> logger)
    {
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Capture selected Explorer file";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Bag.ContainsKey(PipelineBagKeys.PayloadBytes))
        {
            _logger.LogDebug("CaptureSelectedExplorerFileTask: payload already in bag; skipping");
            return;
        }

        // Tray-menu dismissal grace period — without it GetForegroundWindow returns AresToys's
        // popup instead of the Explorer window the user wants to act on.
        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);

        // COM marshalling has to happen on an STA thread; the WPF dispatcher is the cleanest
        // STA we already own. ResolveSelectedFilePath returns null on every "no Explorer in
        // foreground / no selection / something blew up" path so the caller has one branch.
        var path = await Application.Current.Dispatcher.InvokeAsync(ResolveSelectedFilePath).Task.ConfigureAwait(false);
        if (string.IsNullOrEmpty(path))
        {
            _logger.LogInformation("CaptureSelectedExplorerFileTask: no Explorer file selected; aborting workflow");
            context.Abort("no selected file");
            return;
        }
        if (!File.Exists(path))
        {
            _logger.LogWarning("CaptureSelectedExplorerFileTask: resolved path doesn't exist on disk: {Path}", path);
            context.Abort("selected file missing");
            return;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) ext = "bin";
        var fileName = Path.GetFileName(path);

        context.Bag[PipelineBagKeys.PayloadBytes] = bytes;
        context.Bag[PipelineBagKeys.FileExtension] = ext;
        var kind = KindForExtension(ext);
        context.Bag[PipelineBagKeys.NewItem] = new NewItem(
            Kind: kind,
            Source: ItemSource.Manual,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: bytes,
            PayloadSize: bytes.LongLength,
            // Video history items reference their file instead of storing it (issue #28).
            BlobRef: kind == ItemKind.Video ? path : null,
            SearchText: fileName);

        _logger.LogInformation("CaptureSelectedExplorerFileTask: loaded '{File}' ({Bytes} bytes) from Explorer selection",
            fileName, bytes.Length);
    }

    /// <summary>Walk every open Shell window and return the first selected file in the one whose
    /// HWND matches <c>GetForegroundWindow</c>. Pure best-effort — returns null on any kind of
    /// failure (no foreground match, no selection, Explorer's automation surface throws). The
    /// dynamic dispatch keeps us free of a SHDocVw / Microsoft.mshtml interop reference.</summary>
    private string? ResolveSelectedFilePath()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                _logger.LogWarning("CaptureSelectedExplorerFileTask: Shell.Application ProgID not registered");
                return null;
            }
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;

            dynamic windows = shell.Windows();
            foreach (dynamic window in windows)
            {
                IntPtr hwnd;
                try { hwnd = new IntPtr((int)window.HWND); }
                catch { continue; } // Some shell windows don't expose HWND (e.g. older IE remnants)

                if (hwnd != foreground) continue;

                dynamic? document;
                try { document = window.Document; }
                catch { continue; } // Non-folder windows (e.g. legacy IE) throw on .Document

                dynamic items;
                try { items = document.SelectedItems(); }
                catch { return null; } // Folder window with no items / unsupported view mode

                foreach (dynamic item in items)
                {
                    string? path = item.Path as string;
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                return null; // Foreground Explorer matched but nothing selected
            }
            return null; // Foreground isn't an Explorer window we can introspect
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CaptureSelectedExplorerFileTask: COM call into Shell.Application failed");
            return null;
        }
    }

    private static ItemKind KindForExtension(string ext) => ext switch
    {
        "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" => ItemKind.Image,
        "mp4" or "mov" or "webm" or "mkv" => ItemKind.Video,
        "txt" or "md" or "log" or "csv" or "json" or "xml" or "yml" or "yaml" => ItemKind.Text,
        "html" or "htm" => ItemKind.Html,
        "rtf" => ItemKind.Rtf,
        _ => ItemKind.Files,
    };

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
