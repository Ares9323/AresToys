using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using AresToys.App.Resources;
using AresToys.App.Services.Shell;
using AresToys.Hotkeys;

namespace AresToys.App.Services.NewPlus;

/// <summary>
/// Proof of concept: Ctrl+D in an Explorer window duplicates the selected files and folders (name plus
/// " - Copy"), instead of Explorer's own Ctrl+D, which deletes. Same shape as
/// <see cref="NewPlusFolderShortcut"/>: a binding in the keyboard hook that applies only while an Explorer
/// window has the focus in its file list, the work on a thread of its own, and a copy of the exe with the
/// shell's token when this process is elevated. Off until the New+ tab switch turns it on.
/// </summary>
public sealed class ExplorerDuplicateShortcut
{
    private const string HookId = "explorer.duplicate";
    private const uint VkD = 0x44;

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public int rcLeft, rcTop, rcRight, rcBottom;
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private volatile bool _active;

    public ExplorerDuplicateShortcut(KeyboardHook hook)
        => hook.Register(HookId, HotkeyModifiers.Control, VkD, OnPressed, suppress: true, when: CanHandle);

    /// <summary>Turns the shortcut on or off. Cheap, called on every settings sync.</summary>
    public void SetActive(bool active) => _active = active;

    // Hook thread: only cheap window checks. The key is ours only in an Explorer window whose focus is
    // in the file list (not the address bar or the search box, where Ctrl+D means something else).
    private bool CanHandle()
    {
        if (!_active) return false;
        var root = NewPlusExplorer.ForegroundRoot();
        if (NewPlusExplorer.ClassOf(root) != "CabinetWClass") return false;
        var thread = GetWindowThreadProcessId(root, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info)) return false;
        return NewPlusExplorer.ClassOf(info.hwndFocus) is "DirectUIHWND" or "SysListView32";
    }

    private void OnPressed()
    {
        var root = NewPlusExplorer.ForegroundRoot();
        var culture = Markup.LocalizedStrings.Instance.Culture ?? CultureInfo.CurrentUICulture;
        if (NewPlusFolderShortcut.TryRunUnelevated(NewPlusHeadless.DuplicateSwitch, root, culture)) return;
        var thread = new Thread(() => Duplicate(root, culture)) { IsBackground = true, Name = "Explorer-Duplicate" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Copies what the window <paramref name="root"/> has selected and starts the rename of the first copy.
    /// Returns the rename thread (null when nothing was copied) so a headless caller can wait for it.</summary>
    internal static Thread? Duplicate(IntPtr root, CultureInfo culture)
    {
        try
        {
            if (ExplorerWindows.FindActive(root) is not { } found) return null;
            dynamic window = found.Window;
            var sources = new List<string>();
            foreach (dynamic item in window.Document.SelectedItems())
            {
                string path = item.Path;
                if (File.Exists(path) || Directory.Exists(path)) sources.Add(path);
            }
            if (sources.Count == 0) return null;

            var suffix = Strings.ResourceManager.GetString(nameof(Strings.NewPlus_CopySuffix), culture) ?? "Copy";
            string? first = null;
            foreach (var source in sources)
            {
                var folder = Path.GetDirectoryName(source);
                if (folder is null) continue;
                var isFolder = Directory.Exists(source);
                var stem = isFolder ? Path.GetFileName(source) : Path.GetFileNameWithoutExtension(source);
                var extension = isFolder ? string.Empty : Path.GetExtension(source);
                var target = NewPlusTemplates.MakeUniquePath(Path.Combine(folder, $"{stem} - {suffix}{extension}"));
                NewPlusNative.CopyWithShell(source, target);
                NewPlusNative.NotifyCreated(target);
                first ??= target;
            }
            return first is null ? null : NewPlusExplorer.BeginRename(first, root);
        }
        catch { return null; /* nothing selected, a copy refused or cancelled, or the window gone */ }
    }
}
