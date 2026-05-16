using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AresToys.App.Services.Launcher;

/// <summary>Resolves a Windows shell icon for any path through <c>SHGetFileInfo</c>. Same call
/// Explorer uses, so .exe / .lnk / files / folders / virtual targets all return the right
/// icon. Results are cached by absolute path — extracting an icon involves a P/Invoke and a
/// GDI handle, not free to do per-render. Cache survives the lifetime of the launcher window.</summary>
public sealed class IconService
{
    private readonly Dictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public BitmapSource? GetIcon(string? rawPath) => GetIcon(rawPath, iconIndex: 0);

    /// <summary>High-resolution variant — asks the Windows shell for an icon at a specific pixel
    /// size via <c>IShellItemImageFactory::GetImage</c>. This is the same API Explorer uses for
    /// its "Medium / Large / Extra Large icons" views and it correctly picks the highest-res
    /// frame out of multi-resolution <c>.ico</c> containers / <c>.lnk</c> overlays. Falls back
    /// to the legacy <see cref="SHGetFileInfo"/> path on any COM failure so callers always get
    /// SOMETHING to render. Cache key includes <paramref name="sizePx"/> so 32-and-64-px requests
    /// for the same file coexist in the cache without clobbering each other.</summary>
    public BitmapSource? GetIconAtSize(string? rawPath, int sizePx)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return null;
        if (sizePx <= 0) return GetIcon(rawPath, iconIndex: 0);
        string expanded;
        try { expanded = Environment.ExpandEnvironmentVariables(rawPath).Trim(); }
        catch { return null; }
        if (string.IsNullOrEmpty(expanded)) return null;

        var cacheKey = $"{expanded}|sz{sizePx}";
        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        // Try IShellItemImageFactory FIRST: it's the only path that actually upscales a small
        // source icon (e.g. a 16×16 favicon embedded in a .url) to the requested pixel size.
        // The imagelist path below preserves the icon's NATIVE pixel size inside a fixed-size
        // imagelist slot with transparent padding — so a .url whose IconFile is a 16-px favicon
        // comes back as a 16-px glyph in the top-left of an otherwise empty 48-px bitmap, which
        // WPF then renders as a tiny icon floating in the corner of the tile. SIIGBF_RESIZETOFIT
        // makes the shell actually scale.
        var bmp = LoadFromImageFile(expanded)
                  ?? ExtractViaShellItemImageFactory(expanded, sizePx)
                  ?? ExtractIconAtSize(expanded, sizePx)
                  ?? ExtractIcon(expanded);
        lock (_lock)
        {
            _cache[cacheKey] = bmp;
        }
        return bmp;
    }

    /// <summary>Resolve an icon for a path with an optional index into multi-icon containers
    /// (.dll/.exe/.ico). <paramref name="iconIndex"/> > 0 forces ExtractIconEx (so the user
    /// can pick e.g. the 4th icon out of shell32.dll); 0 falls through to the default shell
    /// icon for the path (raster images and unspecified extracts). Cached by composite key
    /// so the same (path, index) pair never re-extracts.</summary>
    public BitmapSource? GetIcon(string? rawPath, int iconIndex)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return null;
        string expanded;
        try { expanded = Environment.ExpandEnvironmentVariables(rawPath).Trim(); }
        catch { return null; }
        if (string.IsNullOrEmpty(expanded)) return null;

        var cacheKey = iconIndex == 0 ? expanded : $"{expanded}|{iconIndex}";
        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        var bmp = LoadFromImageFile(expanded)
                  ?? (iconIndex > 0 ? ExtractIconAt(expanded, iconIndex) : null)
                  ?? ExtractIcon(expanded);
        lock (_lock)
        {
            _cache[cacheKey] = bmp;
        }
        return bmp;
    }

    /// <summary>Resolve a cell icon: custom icon (if set) wins over the default shell icon for
    /// the launch path. The custom slot accepts both raster images (.png / .jpg / .bmp / .gif)
    /// and Windows icon files (.ico) — anything <see cref="LoadFromImageFile"/> or the shell
    /// can render. Falls back gracefully when the custom path is missing.</summary>
    public BitmapSource? GetIcon(string? customIconPath, string? fallbackPath, int customIconIndex = 0)
    {
        if (!string.IsNullOrWhiteSpace(customIconPath))
        {
            var custom = GetIcon(customIconPath, customIconIndex);
            if (custom is not null) return custom;
        }
        return GetIcon(fallbackPath);
    }

    /// <summary>Load a path with WPF's native imaging if it points at a recognised image
    /// extension. Returns null for non-images so the caller can fall through to shell-icon
    /// extraction. Handles .png/.jpg/.bmp/.gif and .ico (BitmapDecoder reads ICO containers).</summary>
    private static BitmapSource? LoadFromImageFile(string path)
    {
        if (!System.IO.File.Exists(path)) return null;
        var ext = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".ico")) return null;
        try
        {
            // OnLoad caches the bytes immediately so the file isn't kept open / locked.
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pull the N-th icon out of a multi-icon container (.dll, .exe, .ico). Useful
    /// for shell32.dll (hundreds of icons), imageres.dll, accessibility.dll, etc. Returns
    /// null if the index is out of range or the file isn't a multi-icon container.</summary>
    private static BitmapSource? ExtractIconAt(string path, int iconIndex)
    {
        var large = IntPtr.Zero;
        var small = IntPtr.Zero;
        try
        {
            // ExtractIconEx returns the count actually written. We ask for one icon (large),
            // index = the user-supplied position. Negative indexes are rare but supported by
            // the API (resource ID lookups); we don't surface them in the UI.
            var copied = ExtractIconEx(path, iconIndex, out large, out small, 1);
            if (copied == 0 || large == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(large,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            if (small != IntPtr.Zero) DestroyIcon(small);
        }
    }

    private static BitmapSource? ExtractIcon(string path)
    {
        // SHGFI_USEFILEATTRIBUTES tells the shell to look up the icon based on the file
        // extension alone — fast, doesn't touch the disk, and works even for paths that don't
        // resolve (URLs, missing files). For real files/folders we drop the flag so the shell
        // can return the actual file's icon (custom .ico for .lnk shortcuts, etc).
        var attrs = SHGFI_ICON | SHGFI_LARGEICON;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                attrs |= SHGFI_USEFILEATTRIBUTES;
        }
        catch { attrs |= SHGFI_USEFILEATTRIBUTES; }

        var info = default(SHFILEINFO);
        var hr = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(), attrs);
        if (hr == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();    // freeze so it can be used cross-thread + faster rendering
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);   // SHGetFileInfo gives ownership; we always release it
        }
    }

    /// <summary>Modern shell-icon path via <see cref="IShellItemImageFactory"/>::GetImage. Unlike
    /// the imagelist route below this one ACTUALLY scales the source icon to the requested pixel
    /// size: a tiny embedded favicon in a <c>.url</c> file gets upscaled to fill the tile instead
    /// of sitting as a 16-px glyph in the corner of a 48-px transparent canvas (which is exactly
    /// the bug this method fixes). The composited link-arrow overlay still comes through because
    /// IShellItemImageFactory uses Explorer's full icon pipeline. Returns null on COM failure so
    /// the caller can fall through to the legacy paths.</summary>
    private static BitmapSource? ExtractViaShellItemImageFactory(string path, int sizePx)
    {
        var iid = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"); // IID_IShellItemImageFactory
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var ppv) != 0 || ppv == IntPtr.Zero)
            return null;

        IShellItemImageFactory? factory = null;
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(ppv);
            var size = new SIZE { cx = sizePx, cy = sizePx };
            // SIIGBF.ResizeToFit (=0) + IconOnly — IconOnly skips the thumbnail path so we don't
            // accidentally get a content preview for image / video / pdf files (we want the file
            // type icon for tiles). ResizeToFit is the documented "scale the source to fit" flag.
            var hr = factory.GetImage(size, SIIGBF.ResizeToFit | SIIGBF.IconOnly, out hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero) return null;

            var src = Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap,
                IntPtr.Zero,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
            Marshal.Release(ppv);
        }
    }

    /// <summary>Extracts a shell icon with full Explorer treatment — <b>composited overlays</b>
    /// (the .lnk shortcut arrow, OneDrive cloud, "shared with people" pair) AND a resolution
    /// close to <paramref name="sizePx"/>. Bypasses managed COM interop entirely: uses raw
    /// IntPtr from <c>SHGetImageList</c> + <c>ImageList_GetIcon</c> in comctl32. The previous
    /// attempt with a hand-written <c>[ComImport] IImageList</c> interface kept failing
    /// silently (suspected vtable mismatch) — this is the same code path Explorer uses, no
    /// custom COM declaration in between.</summary>
    private static BitmapSource? ExtractIconAtSize(string path, int sizePx)
    {
        // Pick the closest stock imagelist. There's no 64-px native imagelist on Windows; we
        // use JUMBO 256 for anything > 48 and let WPF render-scale down to the user's target.
        int shil = sizePx switch
        {
            <= 16 => SHIL_SMALL,
            <= 32 => SHIL_LARGE,
            <= 48 => SHIL_EXTRALARGE,
            _     => SHIL_JUMBO,
        };

        // SHGFI_USEFILEATTRIBUTES = look up by extension only (fast, doesn't touch the file).
        // We drop it when the path resolves on disk so .lnk targets / custom .ico associations
        // come through. SYSICONINDEX returns iIcon = imagelist index; OVERLAYINDEX packs the
        // overlay index into the high byte of iIcon (Windows convention).
        //
        // SHGFI_ICON is required by MSDN for SHGFI_OVERLAYINDEX to populate the overlay byte;
        // without it some Windows versions silently report overlayIndex=0 even for .lnk files.
        // We don't use the returned HICON (we extract via ImageList_GetIcon below) but we DO
        // destroy it in the finally — leaking it would burn a GDI handle per cached icon.
        var flags = SHGFI_ICON | SHGFI_SYSICONINDEX | SHGFI_OVERLAYINDEX;
        uint attrs = 0;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                flags |= SHGFI_USEFILEATTRIBUTES;
                attrs = FILE_ATTRIBUTE_NORMAL;
            }
        }
        catch
        {
            flags |= SHGFI_USEFILEATTRIBUTES;
            attrs = FILE_ATTRIBUTE_NORMAL;
        }

        var info = default(SHFILEINFO);
        if (SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero)
            return null;

        // SHGFI_ICON populated info.hIcon — we don't use it (we extract via ImageList_GetIcon
        // below) but we own the handle and must release it. Burning the handle is the cost of
        // forcing SHGFI_OVERLAYINDEX to populate reliably; without SHGFI_ICON in the flags
        // some Windows versions return overlayIndex=0 even for .lnk files.
        if (info.hIcon != IntPtr.Zero) DestroyIcon(info.hIcon);

        // iIcon: low 24 bits = imagelist index, high 8 bits = overlay index (1-based; 0 = no
        // overlay). The link arrow is overlay 2 by convention; OneDrive sync icons use 1/3/4.
        int iconIndex = info.iIcon & 0x00FFFFFF;
        int overlayIndex = (info.iIcon >> 24) & 0xFF;

        var iid = IID_IImageList;
        IntPtr himl;
        if (SHGetImageList(shil, ref iid, out himl) != 0 || himl == IntPtr.Zero) return null;

        IntPtr hicon = IntPtr.Zero;
        try
        {
            // INDEXTOOVERLAYMASK: overlay index goes in bits 8–11 of the draw flags. The shell
            // composites the overlay glyph onto the base icon as part of ImageList_GetIcon.
            int drawFlags = ILD_TRANSPARENT;
            if (overlayIndex > 0) drawFlags |= (overlayIndex & 0x0F) << 8;

            hicon = ImageList_GetIcon(himl, iconIndex, drawFlags);
            if (hicon == IntPtr.Zero) return null;

            var src = Imaging.CreateBitmapSourceFromHIcon(hicon,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally
        {
            if (hicon != IntPtr.Zero) DestroyIcon(hicon);
            // We hold one ref via SHGetImageList; release it now that ImageList_GetIcon has
            // copied the icon into a fresh HICON we own.
            Marshal.Release(himl);
        }
    }

    // ── Win32 interop ──────────────────────────────────────────────────────────────

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;   // 32×32 in the legacy enumeration
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint SHGFI_SYSICONINDEX = 0x00004000;
    private const uint SHGFI_OVERLAYINDEX = 0x00000040;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    // Stock system-imagelist sizes. JUMBO 256 is the largest Windows exposes; anything bigger
    // requires upscaling which looks blurry, so we cap any user zoom there and let WPF scale
    // down to the rendered size.
    private const int SHIL_LARGE = 0;        // 32
    private const int SHIL_SMALL = 1;        // 16
    private const int SHIL_EXTRALARGE = 2;   // 48
    private const int SHIL_JUMBO = 4;        // 256
    private const int ILD_TRANSPARENT = 0x00000001;

    // SIIGBF (SHELL_ITEM_IMAGE_BIT_FLAGS) — flags for IShellItemImageFactory::GetImage. The
    // documented native names are SIIGBF_RESIZETOFIT etc.; we strip the prefix to satisfy
    // CA1712 (enum members shouldn't repeat the enum type name).
    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage([In] SIZE size, [In] SIIGBF flags, [Out] out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        out IntPtr ppv);

    // IID_IImageList — used only to pass the right interface GUID to SHGetImageList. We
    // never actually marshal a typed IImageList: instead SHGetImageList fills an IntPtr we
    // hand straight to comctl32's ImageList_GetIcon. Avoids the vtable-mismatch pitfalls of
    // a hand-rolled [ComImport] interface declaration.
    private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetImageList(int iImageList, [In] ref Guid riid, out IntPtr ppv);

    [DllImport("comctl32.dll", EntryPoint = "ImageList_GetIcon")]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, int flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex,
        out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);
}
