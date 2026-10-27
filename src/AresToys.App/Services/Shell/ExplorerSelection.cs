using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Shell;

/// <summary>Reads the selection of the foreground Explorer window. The one place that talks to
/// <c>Shell.Application</c> for this, shared by every pipeline task that acts on the files the
/// user has selected in Explorer.</summary>
public static class ExplorerSelection
{
    /// <summary>Walks every open Shell window and returns the paths of all selected items in the
    /// one whose HWND matches <c>GetForegroundWindow</c>, in selection order. Folders are
    /// included; callers filter what they accept. Best effort: returns an empty list when the
    /// foreground window is not an Explorer window, nothing is selected, or Explorer's
    /// automation surface throws. Must run on an STA thread (the WPF dispatcher). The dynamic
    /// dispatch keeps us free of a SHDocVw / Microsoft.mshtml interop reference.</summary>
    public static IReadOnlyList<string> GetSelectedPaths(ILogger? logger = null)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return Array.Empty<string>();

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                logger?.LogWarning("ExplorerSelection: Shell.Application ProgID not registered");
                return Array.Empty<string>();
            }
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return Array.Empty<string>();

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
                catch { return Array.Empty<string>(); } // Folder window with no items / unsupported view mode

                var paths = new List<string>();
                foreach (dynamic item in items)
                {
                    string? path = item.Path as string;
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                }
                return paths; // Empty when the foreground Explorer matched but nothing is selected
            }
            return Array.Empty<string>(); // Foreground isn't an Explorer window we can introspect
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "ExplorerSelection: COM call into Shell.Application failed");
            return Array.Empty<string>();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
