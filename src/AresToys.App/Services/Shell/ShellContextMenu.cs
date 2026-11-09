using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AresToys.App.Services.Shell;

/// <summary>Wraps the Windows shell <c>IContextMenu</c> COM machinery so we can show the same
/// right-click menu Explorer would show for a file or folder. The menu is read straight from the
/// shell namespace, so every installed extension (7-Zip, Git GUI, Notepad++, "Open in Terminal",
/// "Pin to Quick Access", "Send to", "Open with…", any third-party verb the user has installed)
/// shows up exactly as it does in Explorer — there's no curated allow-list to maintain.
///
/// Lifecycle: build-and-show is one-shot — call <see cref="Show"/>, await the user's pick, the
/// invocation runs synchronously, the helper disposes itself. The owner window must process WPF
/// messages while the menu is open (it does — TrackPopupMenuEx pumps a nested message loop), and
/// we install a temporary <see cref="HwndSourceHook"/> to forward the menu-drawing messages
/// (WM_INITMENUPOPUP, WM_DRAWITEM, WM_MEASUREITEM, WM_MENUCHAR) to <c>IContextMenu2</c>/<c>3</c>.
/// Without those hooks the "Send to" / "Open with…" submenus open empty because their owner-draw
/// callbacks never fire.
///
/// Multi-select is supported via <see cref="ShowMany"/> (passes an array of paths sharing the
/// same parent folder); the single-path overload is the common case and just delegates.</summary>
public sealed class ShellContextMenu
{
    /// <summary>Show the native shell context menu for a single filesystem path. <paramref name="screen"/>
    /// is the location where the popup's top-left corner anchors (use the mouse position from
    /// the triggering event, in screen coordinates). Returns silently on any failure — the user
    /// just sees no menu, which beats crashing on a broken shell extension.</summary>
    public static void Show(string path, Window owner, Point screen)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        ShowMany(new[] { path }, owner, screen);
    }

    /// <summary>Multi-select variant. All paths must share the same parent folder; the shell's
    /// IContextMenu API operates on a list of child PIDLs under one IShellFolder. Passing paths
    /// from different parents would require interfacing with CIDA / HDROP-shaped data, which we
    /// don't need today.</summary>
    public static void ShowMany(IReadOnlyList<string> paths, Window owner, Point screen)
    {
        if (paths.Count == 0) return;
        var ownerHwnd = new WindowInteropHelper(owner).Handle;
        if (ownerHwnd == IntPtr.Zero) return;

        var parentDir = Path.GetDirectoryName(Path.GetFullPath(paths[0]));
        if (string.IsNullOrEmpty(parentDir)) return;

        IntPtr parentPidl = IntPtr.Zero;
        var childPidls = new IntPtr[paths.Count];
        IShellFolder? parentFolder = null;
        IContextMenu? ctxMenu = null;
        IContextMenu2? ctxMenu2 = null;
        IContextMenu3? ctxMenu3 = null;
        IntPtr hMenu = IntPtr.Zero;
        HwndSource? hwndSource = null;
        HwndSourceHook? hookDelegate = null;

        try
        {
            // 1. Resolve the parent folder PIDL + IShellFolder. Then for each child path resolve
            //    its child PIDL relative to that parent. This gives us the (parent, child[]) pair
            //    GetUIObjectOf wants for IContextMenu.
            if (SHParseDisplayName(parentDir, IntPtr.Zero, out parentPidl, 0, out _) != 0 || parentPidl == IntPtr.Zero)
                return;
            if (SHBindToObject(IntPtr.Zero, parentPidl, IntPtr.Zero, ref IID_IShellFolder, out var folderPtr) != 0
                || folderPtr == IntPtr.Zero)
                return;
            parentFolder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            Marshal.Release(folderPtr);

            for (var i = 0; i < paths.Count; i++)
            {
                if (SHParseDisplayName(paths[i], IntPtr.Zero, out var fullChildPidl, 0, out _) != 0 || fullChildPidl == IntPtr.Zero)
                    return;
                try
                {
                    childPidls[i] = ILFindLastID(fullChildPidl);
                    // ILFindLastID returns a pointer INTO the absolute PIDL — we can't free the
                    // absolute one until we've cloned the child or copied it, otherwise the
                    // child pointer dangles. Clone via ILClone so each childPidl is independently
                    // owned and we can release it explicitly later.
                    childPidls[i] = ILClone(childPidls[i]);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(fullChildPidl);
                }
            }

            // 2. Ask the parent folder for an IContextMenu over the child set.
            var hr = parentFolder.GetUIObjectOf(ownerHwnd, (uint)childPidls.Length, childPidls,
                ref IID_IContextMenu, IntPtr.Zero, out var ctxPtr);
            if (hr != 0 || ctxPtr == IntPtr.Zero) return;
            ctxMenu = (IContextMenu)Marshal.GetObjectForIUnknown(ctxPtr);
            Marshal.Release(ctxPtr);

            // QI for v2/v3. Both are optional; missing them just means the "Send to" / "Open
            // with…" submenus stay empty (they can't render their owner-drawn items without the
            // forwarded messages). Most shell extensions implement at least v2.
            ctxMenu2 = ctxMenu as IContextMenu2;
            ctxMenu3 = ctxMenu as IContextMenu3;

            // 3. Build the HMENU and let the shell populate it.
            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return;
            const uint CMF_NORMAL = 0x00000000;
            const uint CMF_EXTENDEDVERBS = 0x00000100; // shows "extended verbs" only when Shift is held; we always include them so the menu matches Explorer with Shift held
            ctxMenu.QueryContextMenu(hMenu, 0, MIN_CMD, MAX_CMD, CMF_NORMAL | CMF_EXTENDEDVERBS);

            // 4. Install the message-forwarding hook on the owner window. The hook needs the
            //    current IContextMenu2/3 so we capture them in the closure rather than reading
            //    a field (no static state, multiple menus could in theory open back-to-back).
            hwndSource = HwndSource.FromHwnd(ownerHwnd);
            if (hwndSource is not null)
            {
                var capturedCm2 = ctxMenu2;
                var capturedCm3 = ctxMenu3;
                hookDelegate = (IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                    HandleMenuMessage(msg, w, l, capturedCm2, capturedCm3, ref handled);
                hwndSource.AddHook(hookDelegate);
            }

            // 4b. Flip the OS menu theme to dark before showing. uxtheme.dll exposes three
            //     undocumented ordinals (used by Notepad / Calculator since Win10 1809) that
            //     opt the calling app into the dark menu chrome the system uses for native UWP
            //     surfaces. Without these the popup paints with the white Win32 theme even when
            //     Windows itself is in dark mode — visually jarring on top of AresToys' dark UI.
            //     ForceDark unconditionally; the surrounding catch handles older Windows builds
            //     where the ordinals don't exist (rare — Win10 1903+ ships them).
            TryEnableDarkContextMenus(ownerHwnd);

            // 5. Show. TPM_RETURNCMD makes TrackPopupMenuEx return the picked command id (vs.
            //    posting a WM_COMMAND), which is the saner pattern when we own the dispatch.
            const uint TPM_RETURNCMD = 0x0100;
            const uint TPM_RIGHTBUTTON = 0x0002;
            var cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                (int)screen.X, (int)screen.Y, ownerHwnd, IntPtr.Zero);
            if (cmd >= MIN_CMD && cmd <= MAX_CMD)
            {
                // 6. Invoke the picked command. CMINVOKECOMMANDINFO is the v1 struct; for verbs
                //    that need parent-window context (file properties dialog, error popups) the
                //    hwnd field is what they parent to. CMD ids are 1-based offsets from MIN_CMD
                //    in the shell's expectation when packed via MAKEINTRESOURCEW.
                var ici = new CMINVOKECOMMANDINFO
                {
                    cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                    fMask = 0,
                    hwnd = ownerHwnd,
                    lpVerb = (IntPtr)(cmd - MIN_CMD),
                    lpParameters = IntPtr.Zero,
                    lpDirectory = IntPtr.Zero,
                    nShow = 1, // SW_SHOWNORMAL
                    dwHotKey = 0,
                    hIcon = IntPtr.Zero,
                };
                ctxMenu.InvokeCommand(ref ici);
            }
        }
        catch
        {
            // Swallow shell exceptions — third-party extensions throw plenty (DllSurrogate
            // crashes, broken InProcServer registrations, missing dependencies). The user gets
            // no menu, no AresToys crash. The custom AresToys menu stays available as fallback.
        }
        finally
        {
            if (hookDelegate is not null && hwndSource is not null)
                hwndSource.RemoveHook(hookDelegate);
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (ctxMenu is not null) Marshal.ReleaseComObject(ctxMenu);
            // ctxMenu2/3 are aliases for ctxMenu (same RCW), don't double-release.
            if (parentFolder is not null) Marshal.ReleaseComObject(parentFolder);
            for (var i = 0; i < childPidls.Length; i++)
                if (childPidls[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(childPidls[i]);
            if (parentPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(parentPidl);
        }
    }

    /// <summary>WPF window-proc hook. Forwards menu rendering messages to IContextMenu2/3 so
    /// owner-drawn items (Send to, Open with, third-party submenus) render correctly. Returning
    /// IntPtr.Zero with handled=false lets WPF's default proc keep handling messages we don't
    /// care about.</summary>
    private static IntPtr HandleMenuMessage(int msg, IntPtr wParam, IntPtr lParam,
        IContextMenu2? cm2, IContextMenu3? cm3, ref bool handled)
    {
        const int WM_INITMENUPOPUP = 0x0117;
        const int WM_DRAWITEM = 0x002B;
        const int WM_MEASUREITEM = 0x002C;
        const int WM_MENUCHAR = 0x0120;
        switch (msg)
        {
            case WM_INITMENUPOPUP:
            case WM_DRAWITEM:
            case WM_MEASUREITEM:
                if (cm3 is not null)
                {
                    cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out var res);
                    handled = true;
                    return res;
                }
                if (cm2 is not null)
                {
                    cm2.HandleMenuMsg((uint)msg, wParam, lParam);
                    handled = true;
                    return IntPtr.Zero;
                }
                break;
            case WM_MENUCHAR:
                // v3 only — keyboard mnemonics on owner-drawn items.
                if (cm3 is not null)
                {
                    cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out var res);
                    handled = true;
                    return res;
                }
                break;
        }
        return IntPtr.Zero;
    }

    // -----------------------------------------------------------------------------------------
    // Win32 / COM glue.
    //
    // Command id range: shell extensions allocate ids in the [idCmdFirst, idCmdLast) range we
    // pass to QueryContextMenu. Picking 1..0x7FFF gives us 32k slots which is way more than any
    // real menu uses; starting at 1 avoids the "0 means no command" trap.
    // -----------------------------------------------------------------------------------------

    private const uint MIN_CMD = 1;
    private const uint MAX_CMD = 0x7FFF;

    /// <summary>Opt the calling app into the dark menu theme. Called once per menu open
    /// (cheap — uxtheme caches the mode). Wrapped in try/catch because the ordinals are
    /// technically unsupported by Microsoft; missing entry points on a pre-1903 build just
    /// leave the menu painting in the legacy light theme.</summary>
    private static void TryEnableDarkContextMenus(IntPtr ownerHwnd)
    {
        try
        {
            // ForceDark over AllowDark because AresToys' chrome is dark-only; the menu must
            // match it regardless of what Windows itself is set to. If a future user requests
            // "follow system theme" instead, swap to PreferredAppMode.AllowDark.
            // The int return is the previous mode — we don't read it back (we set fresh each
            // open), but assigning to discard silences the CA1806 analyzer.
            _ = SetPreferredAppMode(PreferredAppMode.ForceDark);
            if (ownerHwnd != IntPtr.Zero) _ = AllowDarkModeForWindow(ownerHwnd, true);
            FlushMenuThemes();
        }
        catch
        {
            // Ordinal not present (Windows < 1903) or uxtheme.dll unavailable — the menu just
            // stays in the system default theme. Not worth notifying the user.
        }
    }

    private enum PreferredAppMode { Default, AllowDark, ForceDark, ForceLight, Max }

    /// <summary>uxtheme.dll #135 (1903+): SetPreferredAppMode. Before 1903 the same ordinal was
    /// the single-bool <c>AllowDarkModeForApp</c>; AresToys targets modern Windows so we use
    /// the 1903+ signature exclusively.</summary>
    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    private static extern int SetPreferredAppMode(PreferredAppMode mode);

    /// <summary>uxtheme.dll #133: AllowDarkModeForWindow. Pairs with SetPreferredAppMode to
    /// flag a specific HWND as dark-mode-aware so its native menus paint dark.</summary>
    [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowDarkModeForWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool allow);

    /// <summary>uxtheme.dll #136: FlushMenuThemes. Drops cached menu brushes so the next
    /// TrackPopupMenu paints with the current preferred theme rather than the snapshot taken
    /// at process start.</summary>
    [DllImport("uxtheme.dll", EntryPoint = "#136", SetLastError = true)]
    private static extern void FlushMenuThemes();

    private static Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern int SHBindToObject(IntPtr psf, IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", EntryPoint = "ILFindLastID")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll", EntryPoint = "ILClone")]
    private static extern IntPtr ILClone(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint flags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFO
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        // Only declare the slots we actually call — declaring stubs in order is required so the
        // vtable layout matches. ParseDisplayName, EnumObjects, BindToObject, BindToStorage,
        // CompareIDs, CreateViewObject all sit before GetUIObjectOf.
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
        [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
        [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11d0-8D10-00A0C90F2719"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
        [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }
}
