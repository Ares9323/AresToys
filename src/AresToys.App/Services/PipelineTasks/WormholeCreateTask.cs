using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Wormholes;
using AresToys.App.Views;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>"Create wormhole, smart". Detects the foreground Explorer window and:
/// <list type="bullet">
///   <item>If a single folder item is selected → create a wormhole for THAT folder, no dialog.</item>
///   <item>Otherwise (no selection, multi-select, file selected, no Explorer in foreground) →
///         open <see cref="NewWormholeDialog"/> so the user picks a folder.</item>
/// </list>
/// COM dance is the same shape as <c>CaptureSelectedExplorerFileTask</c>: dispatched on the
/// STA dispatcher, dynamic on Shell.Application, returns null on every "couldn't figure it out"
/// path so the caller has one branch.</summary>
public sealed class WormholeCreateTask : IPipelineTask
{
    public const string TaskId = "arestoys.wormhole-create";

    private readonly IWormholeWindowManager _manager;
    private readonly ILogger<WormholeCreateTask> _logger;

    public WormholeCreateTask(IWormholeWindowManager manager, ILogger<WormholeCreateTask> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Create wormhole";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Tray-menu dismissal grace period — without it GetForegroundWindow returns AresToys's
        // popup instead of the Explorer window the user wants to act on.
        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);

        var folder = await Application.Current.Dispatcher.InvokeAsync(ResolveExplorerSelectedFolder).Task.ConfigureAwait(true);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            // Auto-create flow: title = folder name, no dialog. Mirrors the Explorer right-click
            // verb and the cold-start handler in App.HandleCreateWormhole.
            var title = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(title)) title = "Wormhole";
            try { await _manager.CreateAsync(title, folder, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WormholeCreateTask auto-create failed for {Folder}", folder);
            }
            return;
        }

        // Fallback: dialog. Has to run on the UI thread (modal Window).
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var dlg = new NewWormholeDialog();
                if (dlg.ShowDialog() != true || dlg.Result is null) return;
                var choice = dlg.Result;
                _ = _manager.CreateAsync(choice.Title, choice.SourceFolder, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WormholeCreateTask dialog flow failed");
            }
        }).Task.ConfigureAwait(false);
    }

    /// <summary>Walk every open Explorer window and pick a folder to mirror. Two-pass:
    /// <list type="number">
    ///   <item>Try the foreground Explorer first — most natural ("the window I was looking at").</item>
    ///   <item>If foreground isn't Explorer (hotkey was pressed while another app had focus,
    ///         or the foreground briefly shifted around hotkey dispatch), fall back to ANY
    ///         Explorer window with a usable folder. If exactly one such window exists, use
    ///         it; if multiple disagree on what folder to pick, bail to the dialog rather
    ///         than guess wrong.</item>
    /// </list>
    /// For each candidate window: a single selected folder wins; otherwise the
    /// currently-viewed folder. Folder validity is confirmed via <c>Directory.Exists</c> —
    /// <c>item.IsFolder</c> from the shell COM surface lies on some namespace extensions.</summary>
    private string? ResolveExplorerSelectedFolder()
    {
        var foreground = GetForegroundWindow();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return null;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;
            dynamic windows = shell.Windows();

            // Pass-1 result: the foreground Explorer's pick. Pass-2 collector: every other
            // Explorer's pick + an ambiguity flag for when they disagree.
            string? fromForeground = null;
            string? fromAny = null;
            bool ambiguous = false;

            foreach (dynamic window in windows)
            {
                var pick = TryGetFolderPathFromWindow(window);
                if (pick is null) continue;

                IntPtr hwnd;
                try { hwnd = new IntPtr((int)window.HWND); }
                catch { hwnd = IntPtr.Zero; }

                if (hwnd != IntPtr.Zero && hwnd == foreground)
                {
                    fromForeground = pick;
                    break; // foreground match always wins, no need to keep scanning
                }

                if (fromAny is null) fromAny = pick;
                else if (!string.Equals(fromAny, pick, StringComparison.OrdinalIgnoreCase))
                    ambiguous = true;
            }

            if (fromForeground is not null) return fromForeground;
            if (fromAny is not null && !ambiguous) return fromAny;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WormholeCreateTask: Shell.Application introspection failed");
            return null;
        }
    }

    /// <summary>Resolve a folder path from a single Shell window. Selected folder (single
    /// item, no multi-select) wins; falls back to the currently-displayed folder when nothing
    /// is selected. Returns null on any non-folder selection / multi-select / COM hiccup —
    /// caller treats null as "this window doesn't have a usable pick".</summary>
    private static string? TryGetFolderPathFromWindow(dynamic window)
    {
        dynamic? document;
        try { document = window.Document; }
        catch { return null; }

        dynamic? items = null;
        try { items = document.SelectedItems(); } catch { items = null; }

        if (items is not null)
        {
            int count = 0;
            string? selectedPath = null;
            foreach (dynamic item in items)
            {
                count++;
                if (count > 1) return null; // multi-select disqualifies this window
                try { selectedPath = item.Path as string; } catch { }
            }
            if (count == 1)
            {
                if (!string.IsNullOrEmpty(selectedPath) && Directory.Exists(selectedPath))
                    return selectedPath;
                return null; // single item selected but not a folder → don't fall through to "current folder"
            }
        }

        // No selection → currently-viewed folder.
        try
        {
            string? currentPath = document.Folder.Self.Path as string;
            if (!string.IsNullOrEmpty(currentPath) && Directory.Exists(currentPath))
                return currentPath;
        }
        catch { /* shell folder without a filesystem path (Quick Access, This PC, etc.) */ }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
