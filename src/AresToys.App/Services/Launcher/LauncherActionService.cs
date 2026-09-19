using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Launcher;

/// <summary>Headless equivalent of <c>LauncherWindow.FireCell</c> — resolves a cell by
/// (tab, key) and executes its action (activate existing window if applicable, otherwise
/// Process.Start with the cell's path / args / window-mode / runas hints). Extracted as a
/// service so workflow tasks can invoke launcher cells from a pipeline without summoning the
/// overlay window first.
///
/// Does NOT touch the launcher window: the window's own fire path stays unchanged because it
/// also handles the post-fire BeginHide gesture. This service is the "headless" variant used
/// by automation (workflows, future CLI surfaces).</summary>
public sealed class LauncherActionService
{
    private readonly LauncherStore _store;
    private readonly ILogger<LauncherActionService> _logger;

    public LauncherActionService(LauncherStore store, ILogger<LauncherActionService> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Look up the cell at (tabKey, keyChar) in the persisted launcher state and
    /// execute its action. Returns true on success, false when the cell is unmapped / empty
    /// — the caller can decide whether that's an error worth surfacing. Function-strip cells
    /// ignore the tab argument: they live under the dedicated "F" namespace regardless of
    /// which numeric tab is currently active, mirroring the F1-F10 strip's global behaviour
    /// in the launcher UI.</summary>
    public async Task<bool> FireAsync(string tabKey, string keyChar, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tabKey);
        ArgumentNullException.ThrowIfNull(keyChar);

        // Normalise: F1-F10 are global, force the F namespace regardless of what the caller
        // passed in for tab. Lets a workflow declare key="F3" without having to also set
        // tab="F" — the function-key vs numeric-tab distinction stays an internal detail.
        var effectiveTab = keyChar.StartsWith('F') && keyChar.Length >= 2 && char.IsDigit(keyChar[1])
            ? LauncherTabs.FunctionStrip
            : tabKey;

        var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var cell = state.Get(effectiveTab, keyChar);
        if (!cell.IsConfigured)
        {
            _logger.LogInformation("LauncherActionService: cell {Tab}:{Key} is empty, nothing to fire", effectiveTab, keyChar);
            return false;
        }

        try
        {
            // Activate-if-running: when the cell declares a window title or process name,
            // try to focus an existing instance instead of spawning a new one. Same path as
            // LauncherWindow.FireCell so a workflow-fired cell behaves identically to a
            // hotkey-fired one.
            if (WindowActivator.TryActivate(cell.WindowTitle, cell.ProcessName))
            {
                _logger.LogInformation("LauncherActionService: activated existing window for {Key} (title='{Title}' proc='{Proc}')",
                    cell.ComposedKey, cell.WindowTitle, cell.ProcessName);
                return true;
            }

            // Same packaged-app normalisation the window's fire path applies: a bare
            // AppUserModelID only launches through its shell:AppsFolder parsing name. See
            // PackagedAppPath.
            var path = PackagedAppPath.Normalize(Environment.ExpandEnvironmentVariables(cell.Path));
            var args = Environment.ExpandEnvironmentVariables(cell.Args ?? string.Empty);
            string workingDir = string.Empty;
            if (!PackagedAppPath.IsAppsFolderPath(path))
            {
                try { workingDir = Path.GetDirectoryName(path) ?? string.Empty; } catch { /* ignore */ }
            }

            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = args,
                UseShellExecute = true,    // .lnk / .bat / URL resolution + Verb=runas UAC prompt
                WorkingDirectory = workingDir,
                WindowStyle = MapWindowMode(cell.WindowMode),
            };
            if (cell.RunAsAdmin) psi.Verb = "runas";

            Process.Start(psi);
            _logger.LogInformation("LauncherActionService: fired {Key} → {Path} {Args} (admin={Admin}, mode={Mode})",
                cell.ComposedKey, path, args, cell.RunAsAdmin, cell.WindowMode);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LauncherActionService: failed to launch cell {Key} → {Path}",
                cell.ComposedKey, cell.Path);
            return false;
        }
    }

    private static ProcessWindowStyle MapWindowMode(LauncherWindowMode mode) => mode switch
    {
        LauncherWindowMode.Maximized => ProcessWindowStyle.Maximized,
        LauncherWindowMode.Minimized => ProcessWindowStyle.Minimized,
        LauncherWindowMode.Hidden    => ProcessWindowStyle.Hidden,
        _                            => ProcessWindowStyle.Normal,
    };
}
