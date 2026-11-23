using System.Globalization;
using System.IO;
using System.Windows;
using AresToys.App.Resources;

namespace AresToys.App.Services.FileLocks;

/// <summary>The "Unlock file" verb without a window (the owner's pick, card 2770): says so when nothing holds the item;
/// otherwise names the programs holding it, asks once and closes their handles. When the item is still held afterwards
/// (a loaded DLL, a program out of reach) the caller opens the locks window instead.</summary>
public static class QuickUnlock
{
    /// <summary>True when finished (nothing held it, the user said no, or it is now free); false when the locks
    /// window should open because something still holds the item.</summary>
    public static bool Run(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } fileName ? fileName : path;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Show(Format(Strings.FileLocks_NotFound, path), MessageBoxImage.Warning);
            return true;
        }

        var found = LockFinder.Find([path]);
        if (found.Processes.Count == 0)
        {
            var text = Format(Strings.FileLocks_QuickNotLocked, name);
            if (found.Partial) text += "\n\n" + Strings.FileLocks_Partial;
            Show(text, MessageBoxImage.Information);
            return true;
        }

        var holders = string.Join(", ", found.Processes.Select(p => $"{p.Name} ({p.ProcessId})"));
        if (MessageBox.Show(Format(Strings.FileLocks_QuickConfirm, name, holders), "AresToys",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return true;

        foreach (var process in found.Processes) LockActions.Unlock(process);
        if (LockFinder.Find([path]).Processes.Count > 0) return false;

        Show(Format(Strings.FileLocks_QuickDone, name), MessageBoxImage.Information);
        return true;
    }

    private static void Show(string text, MessageBoxImage image) => MessageBox.Show(text, "AresToys", MessageBoxButton.OK, image);

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
