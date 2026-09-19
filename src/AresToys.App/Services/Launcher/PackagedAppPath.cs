using System.Runtime.InteropServices;

namespace AresToys.App.Services.Launcher;

/// <summary>Translates AppUserModelIDs into something the launcher can actually run.
///
/// Packaged apps (MSIX / UWP / Store — WhatsApp, Calculator, Claude…) have no executable the
/// shell will launch by path: they're identified by an <b>AppUserModelID</b> shaped
/// <c>&lt;PackageFamilyName&gt;!&lt;AppId&gt;</c>, e.g.
/// <c>5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App</c>. Dragging one off the Start menu delivers
/// exactly that string through <c>DataFormats.FileDrop</c> — it looks like a path but isn't one.
/// Handed to ShellExecute it fails with ERROR_FILE_NOT_FOUND, and <c>SHCreateItemFromParsingName</c>
/// can't resolve an icon for it either, so the cell ends up mapped-but-dead with a nonsense label.
///
/// The cure is one prefix: <c>shell:AppsFolder\&lt;AUMID&gt;</c> is a valid shell parsing name, and
/// that single form works across every consumer we have — ShellExecute launches the app,
/// <see cref="IconService"/> resolves the real tile icon, and the shell hands us the app's
/// display name for the cell label. So we normalise at the drop and again at launch time (the
/// latter so cells already persisted in their broken form start working without a re-drop).</summary>
public static class PackagedAppPath
{
    /// <summary>Prefix that turns an AUMID into a shell parsing name. Backslash included.</summary>
    public const string AppsFolderPrefix = @"shell:AppsFolder\";

    /// <summary>Characters that can only appear in a path, a URI or a quoted argument — never in
    /// an AUMID. Cached (CA1870) because the shape test runs on every drop and every launch.</summary>
    private static readonly System.Buffers.SearchValues<char> Pathish =
        System.Buffers.SearchValues.Create(@"\/:""<>|");

    /// <summary>Shape test for an AppUserModelID: <c>&lt;Name&gt;_&lt;PublisherId&gt;!&lt;AppId&gt;</c>.
    /// Deliberately strict, because a false positive would rewrite a legitimate target: an AUMID
    /// carries exactly one <c>!</c>, no path separators, no drive letter or URI scheme (i.e. no
    /// <c>:</c>), and its package family name always ends in <c>_&lt;publisherId&gt;</c>. That last
    /// rule is what keeps a real file called <c>Wow! great_app.lnk</c> out of here. Pure string
    /// logic — no disk or shell access, so it behaves identically on any machine.</summary>
    public static bool LooksLikeAppUserModelId(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var s = candidate.Trim();

        if (s.AsSpan().IndexOfAny(Pathish) >= 0) return false;

        var bang = s.IndexOf('!');
        if (bang <= 0 || bang == s.Length - 1) return false;
        if (s.IndexOf('!', bang + 1) >= 0) return false;   // exactly one separator

        var familyName = s[..bang];
        // PackageFamilyName is always "<name>_<publisherId>"; require a non-empty name before
        // the underscore so a bare "_pub!App" doesn't qualify.
        var underscore = familyName.LastIndexOf('_');
        return underscore > 0 && underscore < familyName.Length - 1;
    }

    /// <summary>Rewrite a launch target into its runnable form: a bare AUMID becomes
    /// <c>shell:AppsFolder\&lt;AUMID&gt;</c>, everything else (paths, URLs, already-normalised
    /// shell parsing names, env-var templates) comes back untouched. Idempotent, null-safe.</summary>
    public static string Normalize(string? rawPath)
    {
        if (string.IsNullOrEmpty(rawPath)) return rawPath ?? string.Empty;
        return LooksLikeAppUserModelId(rawPath)
            ? AppsFolderPrefix + rawPath.Trim()
            : rawPath;
    }

    /// <summary>True when the path is the normalised AppsFolder form. Callers use this to skip
    /// the filesystem assumptions that don't hold for it — deriving a working directory from it,
    /// or passing it to <c>explorer /select,</c>.</summary>
    public static bool IsAppsFolderPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Trim().StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Pull the AUMID back out of either form (normalised or bare). Returns null when
    /// the path isn't a packaged-app target at all.</summary>
    public static string? TryExtractAppUserModelId(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var s = path.Trim();
        if (IsAppsFolderPath(s))
        {
            var aumid = s[AppsFolderPrefix.Length..].Trim();
            return aumid.Length == 0 ? null : aumid;
        }
        return LooksLikeAppUserModelId(s) ? s : null;
    }

    /// <summary>Readable cell label derived from the AUMID alone, for when the shell won't give
    /// us a display name (app uninstalled, or the id was typed by hand). Takes the package name,
    /// drops the <c>_publisherId</c> suffix and the reverse-DNS prefix Store packages carry:
    /// <c>5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App</c> → <c>WhatsAppDesktop</c>. Returns null
    /// for non-packaged paths so callers can fall back to their normal filename logic.</summary>
    public static string? FallbackLabel(string? path)
    {
        var aumid = TryExtractAppUserModelId(path);
        if (aumid is null) return null;

        var familyName = aumid[..aumid.IndexOf('!')];
        var name = familyName[..familyName.LastIndexOf('_')];
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < name.Length - 1) name = name[(lastDot + 1)..];
        return name.Length == 0 ? null : name;
    }

    /// <summary>Ask the shell for the app's user-facing name ("WhatsApp", not
    /// "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App"). Works on the normalised form; bare AUMIDs
    /// are normalised on the way in. Returns null on any failure — an uninstalled app, a typo'd
    /// id, or a shell that won't parse the name — so the caller can fall back to
    /// <see cref="FallbackLabel"/>.</summary>
    public static string? TryGetDisplayName(string? path)
    {
        var target = Normalize(path);
        if (!IsAppsFolderPath(target)) return null;

        var iid = IID_IShellItem;
        if (SHCreateItemFromParsingName(target, IntPtr.Zero, ref iid, out var ppv) != 0 || ppv == IntPtr.Zero)
            return null;

        IShellItem? item = null;
        var pszName = IntPtr.Zero;
        try
        {
            item = (IShellItem)Marshal.GetObjectForIUnknown(ppv);
            if (item.GetDisplayName(SIGDN_NORMALDISPLAY, out pszName) != 0 || pszName == IntPtr.Zero)
                return null;
            var name = Marshal.PtrToStringUni(pszName);
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pszName != IntPtr.Zero) Marshal.FreeCoTaskMem(pszName);
            if (item is not null) Marshal.ReleaseComObject(item);
            Marshal.Release(ppv);
        }
    }

    // ── Win32 / COM interop ────────────────────────────────────────────────────────

    /// <summary>SIGDN_NORMALDISPLAY — the name as Explorer shows it in a folder view.</summary>
    private const int SIGDN_NORMALDISPLAY = 0x00000000;

    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        // Full vtable order matters even though we only call GetDisplayName — the slots above it
        // have to be declared so the third entry lands on the right function pointer.
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(int sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        out IntPtr ppv);
}
