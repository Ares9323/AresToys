namespace AresToys.App.Services.Launcher;

/// <summary>How the launched window should appear initially. Maps to <c>ProcessWindowStyle</c>
/// for the underlying ProcessStartInfo. GUI apps may ignore Maximized/Minimized hints, but
/// console processes and well-behaved Win32 apps honour them.</summary>
public enum LauncherWindowMode
{
    Normal,
    Maximized,
    Minimized,
    Hidden,
}

/// <summary>One mapping in the launcher overlay. <see cref="TabKey"/> namespaces the cell:
/// <c>"F"</c> for the global F1-F10 strip, <c>"1"</c>-<c>"9"</c> / <c>"0"</c> for one of the
/// ten user tabs. <see cref="KeyChar"/> is the printable identity inside that namespace —
/// "F1".."F10" for function keys, QWERTY chars for tab cells. Path is launched through the
/// shell so .exe / .lnk / .bat / file / folder / URL all just work.
/// <see cref="WindowTitle"/> + <see cref="ProcessName"/>: if either matches an already-running
/// window/process, the launcher activates that instead of starting a new instance — same
/// behaviour MaxLauncher exposes for apps you only ever want one copy of.</summary>
public sealed record LauncherCell(
    string TabKey,
    string KeyChar,
    string Label,
    string Path,
    string Args,
    bool RunAsAdmin = false,
    LauncherWindowMode WindowMode = LauncherWindowMode.Normal,
    string WindowTitle = "",
    string ProcessName = "",
    /// <summary>Optional override for the cell icon — when non-empty, the launcher displays
    /// this image instead of the default shell icon resolved from <see cref="Path"/>. Useful
    /// for paths whose default icon isn't visually distinctive (.exe with generic icon, .lnk
    /// pointing at a wrapped binary, URL targets without favicon support).</summary>
    string IconPath = "",
    /// <summary>Index of the icon to extract when <see cref="IconPath"/> points at a multi-
    /// icon container (.dll, .exe, .ico). 0 = first icon (default). Lets the user pick a
    /// specific icon out of e.g. shell32.dll which contains hundreds. Ignored for raster
    /// images that have a single icon (.png / .jpg / .bmp / .gif).</summary>
    int IconIndex = 0,
    /// <summary>Id of a pipeline profile (workflow) this cell runs instead of launching
    /// <see cref="Path"/> — the same wiring the tray's left / double / middle click uses, so any
    /// built-in or user-made workflow can sit on a launcher key. When set it takes over the cell's
    /// action: <see cref="Path"/>, <see cref="Args"/>, <see cref="RunAsAdmin"/> and
    /// <see cref="WindowMode"/> describe how to start a program and have no meaning for a
    /// workflow. Empty = ordinary launch cell.</summary>
    string WorkflowId = "")
{
    /// <summary>This cell runs a workflow rather than launching a path.</summary>
    public bool HasWorkflow => !string.IsNullOrWhiteSpace(WorkflowId);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Path) || HasWorkflow;

    public static LauncherCell Empty(string tabKey, string keyChar) =>
        new(tabKey, keyChar, string.Empty, string.Empty, string.Empty);

    /// <summary>Composite key for storing/looking up cells in a flat dictionary.</summary>
    public static string ComposeKey(string tabKey, string keyChar) => $"{tabKey}:{keyChar}";
    public string ComposedKey => ComposeKey(TabKey, KeyChar);
}

/// <summary>Persisted launcher window geometry (size + on-screen position). Stored as a single
/// JSON blob so updates are atomic — saving "size" and "position" separately would risk a
/// half-applied state if the app crashes between writes.</summary>
public sealed record LauncherGeometry(double Width, double Height, double Left, double Top);

/// <summary>The keyboard tab key the launcher uses for its function-key strip. Distinct from
/// the user tabs ("1".."9","0") so we can tell function-row cells apart at storage time.</summary>
public static class LauncherTabs
{
    public const string FunctionStrip = "F";
}

/// <summary>Static layout: which printable keys live where. Function row is global (always
/// visible). The 10 user tabs each share the same 30-key QWERTY/ASDF/ZXCV layout — switching
/// tabs swaps the cell mapping under the same physical keys, MaxLaunchpad-style.</summary>
public static class LauncherKeyboardLayout
{
    public static readonly IReadOnlyList<string> FunctionKeys = new[]
    {
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10",
    };

    public static readonly IReadOnlyList<string> TabKeys = new[]
    {
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "0",
    };

    public static readonly IReadOnlyList<string> Row1 = new[] { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" };
    public static readonly IReadOnlyList<string> Row2 = new[] { "A", "S", "D", "F", "G", "H", "J", "K", "L", ";" };
    public static readonly IReadOnlyList<string> Row3 = new[] { "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/" };

    /// <summary>All printable keys for one tab in display order (top-to-bottom, left-to-right).</summary>
    public static IEnumerable<string> AllTabKeyChars()
    {
        foreach (var k in Row1) yield return k;
        foreach (var k in Row2) yield return k;
        foreach (var k in Row3) yield return k;
    }
}

/// <summary>Snapshot of every configured launcher cell + tab titles. Returned by the store and
/// passed straight to the window. Always contains the full layout — empty cells are still in
/// <see cref="Cells"/>, callers don't have to juggle missing-key cases.</summary>
public sealed class LauncherState
{
    public LauncherState(IReadOnlyDictionary<string, LauncherCell> cells, IReadOnlyDictionary<string, string> tabTitles)
    {
        Cells = cells;
        TabTitles = tabTitles;
    }

    /// <summary>Every cell (function strip + every tab), indexed by <see cref="LauncherCell.ComposedKey"/>.</summary>
    public IReadOnlyDictionary<string, LauncherCell> Cells { get; }

    /// <summary>Tab key ("1".."9","0") → user-defined display title (e.g. "Software"). Missing
    /// entries default to empty in the UI; the user can right-click a tab to set one.</summary>
    public IReadOnlyDictionary<string, string> TabTitles { get; }

    public LauncherCell Get(string tabKey, string keyChar) =>
        Cells.TryGetValue(LauncherCell.ComposeKey(tabKey, keyChar), out var c)
            ? c
            : LauncherCell.Empty(tabKey, keyChar);

    public string TabTitle(string tabKey) =>
        TabTitles.TryGetValue(tabKey, out var t) ? t : string.Empty;
}
