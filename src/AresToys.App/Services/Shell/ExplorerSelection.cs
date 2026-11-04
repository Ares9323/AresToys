using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Shell;

/// <summary>Reads the selection of the foreground Explorer window, shared by every pipeline task that
/// acts on the files the user has selected in Explorer. The lookup of the active tab lives in
/// <see cref="ExplorerWindows"/>.</summary>
public static class ExplorerSelection
{
    /// <summary>Returns the paths of all selected items in the active tab of the foreground Explorer
    /// window, in selection order. Folders are included; callers filter what they accept. Best effort:
    /// returns an empty list when the foreground window is not an Explorer window, nothing is selected,
    /// or Explorer's automation surface throws. Must run on an STA thread (the WPF dispatcher).</summary>
    public static IReadOnlyList<string> GetSelectedPaths(ILogger? logger = null)
        => ExplorerWindows.GetActiveSelection(logger: logger);
}
