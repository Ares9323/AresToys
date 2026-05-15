using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using AresToys.App.Services.Launcher;
using AresToys.App.Services.Wormholes;

namespace AresToys.App.Views;

public partial class WormholeWindow : Window
{
    // Segoe Fluent Icons code points pinned via ConvertFromUtf32 so the source stays pure
    // ASCII (some tooling round-trips strip or re-encode private-use glyphs inline).
    private static readonly string ChevronUpGlyph   = char.ConvertFromUtf32(0xE70E);
    private static readonly string ChevronDownGlyph = char.ConvertFromUtf32(0xE70D);
    private static readonly string LockClosedGlyph  = char.ConvertFromUtf32(0xE72E);
    private static readonly string LockOpenGlyph    = char.ConvertFromUtf32(0xE785);
    // (Per-kind glyph removed when the Shortcuts/Portal distinction was unified into a single
    // "folder mirror" type — every wormhole is the same kind now, the chrome doesn't need to
    // disambiguate.)

    /// <summary>Hard cap on Portal items rendered per wormhole. Mirrors the spec §6.4 default.
    /// Beyond this we emit a one-shot toast-style banner and truncate — large folders block
    /// the dispatcher today (no virtualisation in MVP).</summary>
    private const int PortalItemCap = 500;

    private readonly WormholeRecord _record;
    private readonly Action _onPersist;
    private readonly IconService _icons;
    private readonly string _wormholesRoot;
    /// <summary>Shared defaults — icon size + opacity — read on every <see cref="EffectiveIconSize"/>
    /// + <see cref="ApplyAppearance"/> call. Null when the manager didn't wire one (older tests
    /// / direct construction); in that case we fall through to <see cref="DesktopIconSize"/>
    /// for icon size and the legacy 0.95 hardcoded opacity.</summary>
    private readonly Services.Wormholes.WormholeDefaultsService? _defaults;
    private readonly ObservableCollection<WormholeItemViewModel> _items = new();
    private bool _isClosingFromManager;
    private bool _portalItemCapReached;

    public WormholeWindow(
        WormholeRecord record,
        Action onPersist,
        IconService icons,
        string wormholesRoot,
        Services.Wormholes.WormholeDefaultsService? defaults = null)
    {
        _record = record;
        _onPersist = onPersist;
        _icons = icons;
        _wormholesRoot = wormholesRoot;
        _defaults = defaults;
        InitializeComponent();
        DataContext = record;
        ItemsHost.ItemsSource = _items;

        Left = record.Geometry.X;
        Top = record.Geometry.Y;
        Width = record.Geometry.Width;
        Height = record.Geometry.Height;

        ApplyLockState();
        ApplyRollState();
        ApplyAppearance();

        RefreshPortalItems();
        EmptyStateHint.Text = "Folder is empty — drop files here to add them.";
        UpdateEmptyStateVisibility();

        // Wire the geometry-persist handlers on Loaded so WPF's initial reposition during the
        // very first Show() — which can clamp our requested Left/Top to the nearest visible
        // monitor's work area on PerMonitorV2 / multi-monitor setups — doesn't fire a persist
        // mid-boot. That synchronous SaveAsync from a startup-time LocationChanged caused
        // "Collection was modified" inside WormholeWindowManager.InitializeAsync's foreach
        // (the records list it was iterating was the same instance SaveAsync mutates).
        // After Loaded the position is settled, and any later LocationChanged is user-driven.
        Loaded += OnLoadedHookGeometryPersist;
        Closing += (_, _) =>
        {
            if (_isClosingFromManager) return;
            _onPersist();
        };
        // Ctrl+MouseWheel zooms tile size like Explorer's icon view. Tunneling handler on the
        // window so it fires regardless of whether the cursor is over the ListBox or the empty
        // state — and we can mark e.Handled = true before the ListBox sees it (which would
        // otherwise scroll the content while we're trying to zoom).
        PreviewMouseWheel += OnPreviewMouseWheelZoom;

        // Edge-resize via WM_NCHITTEST. WindowStyle=None + AllowsTransparency=True kills WPF's
        // native resize border; we synthesise hit-zones manually so the cursor switches to the
        // correct resize arrow within 8 px of any edge and Windows drives the actual resize.
        SourceInitialized += (_, _) =>
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            var src = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
            src?.AddHook(WindowProcResizeHook);
        };
    }

    // WM_NCHITTEST handler. Constants from winuser.h: HTCLIENT=1, HTLEFT=10, HTRIGHT=11,
    // HTTOP=12, HTTOPLEFT=13, HTTOPRIGHT=14, HTBOTTOM=15, HTBOTTOMLEFT=16, HTBOTTOMRIGHT=17.
    // Returning any HT*EDGE/HT*CORNER tells DefWindowProc to begin a resize loop using the
    // appropriate cursor — no further code needed on our side. Skipped when the wormhole is
    // locked (must not resize) or rolled (only the header strip is visible; resize would
    // expose a torn layout).
    //
    // We also intercept WM_NCLBUTTONDBLCLK on those same edge codes: Windows' default is to
    // maximise vertically on HTTOP/HTBOTTOM dblclick (and horizontally on HTLEFT/HTRIGHT).
    // For a wormhole that's the wrong gesture — the user is usually trying to dblclick the
    // header to roll up but accidentally clipping the 8 px resize edge. Swallowing the
    // message keeps the vertical-maximise out of the way without disabling resize-drag.
    private IntPtr WindowProcResizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        const int WM_NCLBUTTONDBLCLK = 0x00A3;

        if (msg == WM_NCLBUTTONDBLCLK)
        {
            int ht = wParam.ToInt32();
            // Any edge or corner dblclick → swallow. HTCAPTION (=2) double-click is the
            // normal restore/maximise gesture and isn't returned by our hit test, so it
            // wouldn't land here anyway; this matches every HT code we DO return from below.
            if (ht is 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17)
            {
                handled = true;
                return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        if (msg != WM_NCHITTEST) return IntPtr.Zero;
        if (_record.IsLocked || _record.IsRolled) return IntPtr.Zero;

        // lParam packs screen X in the low word and screen Y in the high word. Sign-extension
        // matters on multi-monitor setups where the secondary monitor sits at negative coords.
        int xScreen = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int yScreen = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        var pos = PointFromScreen(new Point(xScreen, yScreen));

        const double edge = 8;
        bool left   = pos.X >= 0 && pos.X < edge;
        bool right  = pos.X <= ActualWidth  && pos.X > ActualWidth  - edge;
        bool top    = pos.Y >= 0 && pos.Y < edge;
        bool bottom = pos.Y <= ActualHeight && pos.Y > ActualHeight - edge;

        int hit = (top, bottom, left, right) switch
        {
            (true,  _,    true,  _   ) => 13, // HTTOPLEFT
            (true,  _,    _,    true ) => 14, // HTTOPRIGHT
            (_,     true, true,  _   ) => 16, // HTBOTTOMLEFT
            (_,     true, _,    true ) => 17, // HTBOTTOMRIGHT
            (true,  _,    _,    _    ) => 12, // HTTOP
            (_,     true, _,    _    ) => 15, // HTBOTTOM
            (_,     _,    true, _    ) => 10, // HTLEFT
            (_,     _,    _,    true ) => 11, // HTRIGHT
            _ => 0,
        };
        if (hit == 0) return IntPtr.Zero;   // not on an edge — let WPF handle normally
        handled = true;
        return new IntPtr(hit);
    }

    /// <summary>Effective tile icon size for this wormhole. Fallback chain:
    /// per-wormhole override (<see cref="WormholeRecord.IconSizePx"/> &gt; 0) →
    /// app-wide default (<see cref="Services.Wormholes.WormholeDefaultsService.DefaultIconSizePx"/> &gt; 0) →
    /// user's Windows desktop icon size (<see cref="DesktopIconSize"/>). The middle layer is
    /// what the Settings → Wormholes "Default icon size" slider drives.</summary>
    private int EffectiveIconSize()
    {
        if (_record.IconSizePx > 0) return _record.IconSizePx;
        if (_defaults is { DefaultIconSizePx: > 0 } d) return d.DefaultIconSizePx;
        return DesktopIconSize.Get();
    }

    /// <summary>Tile padding read from the app-wide default. No per-wormhole override slot yet
    /// (one knob is plenty until proven otherwise) — every wormhole uses the same density set
    /// in Settings → Wormholes.</summary>
    private int EffectiveTilePadding() => _defaults?.DefaultTilePaddingPx ?? 4;

    /// <summary>Effective opacity: per-wormhole override wins, else the app-wide default
    /// (<see cref="Services.Wormholes.WormholeDefaultsService.DefaultOpacity"/>). Final fallback
    /// is the legacy 0.95 that the XAML used to hardcode — preserves visuals if the manager
    /// somehow didn't wire a defaults service.</summary>
    private double EffectiveOpacity()
    {
        if (_record.Appearance.OpacityOverride is { } v) return v;
        return _defaults?.DefaultOpacity ?? 0.95;
    }

    /// <summary>Apply opacity to the two backdrop layers — body + header — leaving the
    /// content overlay (icon tiles, labels, chrome buttons) at full opacity. Setting Opacity
    /// on the root <c>OuterFrame</c> would cascade to every descendant including the icons,
    /// which the user explicitly didn't want ("opacity solo allo sfondo e all'header").</summary>
    private void ApplyAppearance()
    {
        var op = EffectiveOpacity();
        BodyBackdrop.Opacity = op;
        HeaderBackdrop.Opacity = op;
    }

    /// <summary>Cheap path: only the opacity changed (Settings slider drag). Skips
    /// RebuildItems so the slider stays fluid even with many wormholes open.</summary>
    internal void RefreshOpacity() => ApplyAppearance();

    /// <summary>Expensive path: the icon size changed → every item VM has to be
    /// re-constructed so it asks <see cref="IconService.GetIconAtSize"/> for the new
    /// resolution. Use sparingly (typically once per NumberBox commit, not on every drag
    /// tick).</summary>
    internal void RefreshIconSize() => RebuildItems();

    private void OnPreviewMouseWheelZoom(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
        // Step: 8 px per wheel tick. Enough granularity to land on Windows' standard preset
        // sizes (32 / 48 / 64 / 96) within 1–4 ticks while still feeling smooth.
        const int step = 8;
        const int min = 24;
        const int max = 256;
        var current = EffectiveIconSize();
        var next = e.Delta > 0 ? current + step : current - step;
        next = Math.Clamp(next, min, max);
        if (next == current) { e.Handled = true; return; }
        _record.IconSizePx = next;
        _onPersist();
        RebuildItems();
        e.Handled = true; // prevent ListBox from scrolling while the user is zooming
    }

    internal void CloseFromManager()
    {
        _isClosingFromManager = true;
        Close();
    }

    /// <summary>Pick a sensible owner for MessageBox / sub-dialogs spawned from the wormhole.
    /// We never use <c>this</c> because the wormhole is parented to <c>WorkerW</c> (desktop
    /// layer) — a dialog inheriting that parenting falls behind every other app the moment it
    /// shows. <c>Application.MainWindow</c> (the AresToys settings window) is normally hidden
    /// but its handle is created, which is enough to give MessageBox a real top-level owner;
    /// when it's null we let MessageBox position itself at screen centre, no owner.</summary>
    private static Window? OwnerForDialogs()
    {
        var main = Application.Current?.MainWindow;
        if (main is null) return null;
        var helper = new System.Windows.Interop.WindowInteropHelper(main);
        return helper.Handle != IntPtr.Zero ? main : null;
    }

    /// <summary>Re-apply title / lock / roll state from the underlying record. Called by
    /// <see cref="AresToys.App.Services.Wormholes.WormholeWindowManager.ReconcileAsync"/> when the
    /// record was mutated from outside the chrome (typically the Wormholes Settings tab toggling
    /// Lock or renaming). <c>WormholeRecord</c> isn't INPC, so we force the DataContext re-bind
    /// in the same way <see cref="CommitInlineRename"/> does.</summary>
    public void RefreshFromRecord()
    {
        DataContext = null;
        DataContext = _record;
        ApplyLockState();
        ApplyRollState();
    }

    /// <summary>Re-enumerate the watched folder and rebuild the item tiles. Called by the
    /// manager after the global icon-size / tile-padding defaults change so the live window
    /// adopts the new dimensions without restart.</summary>
    internal void RebuildItems()
    {
        RefreshPortalItems();
        UpdateEmptyStateVisibility();
    }

    /// <summary>One-shot Loaded handler that subscribes the geometry-persist hooks AFTER the
    /// initial Show + reposition has settled. Self-unsubscribes so a re-Loaded event (rare for
    /// a top-level window but possible) doesn't double-subscribe and double-persist every drag.</summary>
    private void OnLoadedHookGeometryPersist(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedHookGeometryPersist;
        LocationChanged += (_, _) =>
        {
            if (_isClosingFromManager) return;
            _record.Geometry.X = Left;
            _record.Geometry.Y = Top;
            _onPersist();
        };
        SizeChanged += (_, args) =>
        {
            if (_isClosingFromManager) return;
            if (!args.HeightChanged && !args.WidthChanged) return;
            _record.Geometry.Width = Width;
            if (!_record.IsRolled)
            {
                _record.Geometry.Height = Height;
                _record.Geometry.UnrolledHeight = Height;
            }
            _onPersist();
        };
    }

    public event EventHandler<Guid>? DeleteRequested;

    /// <summary>Re-enumerate the source folder and rebuild <see cref="_items"/>. Called from
    /// the manager's <c>FolderWatcher.Changed</c> handler (debounced 300 ms) and from the
    /// hamburger "Refresh" entry.</summary>
    public void RefreshPortalItems()
    {
        var portal = _record.Portal;
        if (portal is null) return;

        _items.Clear();
        _portalItemCapReached = false;
        try
        {
            if (!Directory.Exists(portal.SourcePath))
            {
                // Source went away (drive ejected, folder deleted, manual rename): swap to
                // the dedicated error panel which shows the missing path + a "Pick a new
                // folder…" button so the user can repoint without going through the chrome
                // hamburger. EmptyStateHint stays hidden — keeping both visible would
                // crowd the small wormhole real estate.
                ShowSourceMissingState(portal.SourcePath);
                return;
            }
            // Path resolves again — back to the normal empty / populated states.
            SourceMissingPanel.Visibility = Visibility.Collapsed;
            EmptyStateHint.Text = "Folder is empty — drop files here to add them.";

            // Source enumeration: folders + files (or just files if the user disabled subdir
            // listing). The sort mode (Name / Modified / Type) chosen via the hamburger menu
            // is applied in SortPortalEntries; folders are always grouped first regardless.
            IEnumerable<string> entries = portal.IncludeSubdirectoriesAsItems
                ? Directory.EnumerateDirectories(portal.SourcePath).Concat(Directory.EnumerateFiles(portal.SourcePath))
                : Directory.EnumerateFiles(portal.SourcePath);

            var ordered = SortPortalEntries(entries, portal.SortMode).ToList();
            foreach (var path in ordered)
            {
                if (_items.Count >= PortalItemCap)
                {
                    _portalItemCapReached = true;
                    break;
                }
                _items.Add(new WormholeItemViewModel(path, _icons, EffectiveIconSize(), EffectiveTilePadding()));
            }
        }
        catch (UnauthorizedAccessException)
        {
            EmptyStateHint.Text = "Access denied to source folder.";
        }
        catch (Exception)
        {
            // Defensive: rare IO failures shouldn't crash the wormhole. The next debounce tick
            // will retry.
            EmptyStateHint.Text = "Couldn't read source folder.";
        }

        UpdateEmptyStateVisibility();
        if (_portalItemCapReached)
        {
            // The cap is rare enough that a MessageBox would feel heavy on every refresh; we
            // surface it via the empty-state slot so it stays visible but unobtrusive even
            // while items render around it.
            EmptyStateHint.Text = $"Folder has more than {PortalItemCap} entries — only the first {PortalItemCap} are shown.";
            EmptyStateHint.Visibility = Visibility.Visible;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Header gestures
    // -----------------------------------------------------------------------------------------

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Middle-click anywhere on the header → open the source folder in Explorer. Available
        // even when the wormhole is locked (read-only gesture, doesn't move/resize the window)
        // and ignores click count so a quick Browser-style middle-click works on the first try.
        if (e.ChangedButton == MouseButton.Middle && e.ButtonState == MouseButtonState.Pressed)
        {
            OpenSourceFolder();
            e.Handled = true;
            return;
        }
        if (_record.IsLocked) return;
        if (e.ClickCount == 2) { ToggleRoll(); return; }
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>Launch Explorer on the wormhole's source folder. Mirrors the Wormhole row's
    /// OpenFolder command in the settings tab — duplicated rather than reaching across to the
    /// VM because this window has its own _record snapshot and there's no row VM bound to it.
    /// Silent no-op when the path is empty (broken/unlinked wormhole) or Explorer launch fails;
    /// the user already sees the folder-missing chrome in that case.</summary>
    private void OpenSourceFolder()
    {
        var folder = _record.Portal?.SourcePath;
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch
        {
            // Explorer failed (folder missing / drive offline / ACL); the visible error chrome
            // on the wormhole already advertises the broken state, no need to nag with a dialog.
        }
    }

    private void OnContentAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_record.IsLocked) return;
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    private void OnChevronClicked(object sender, RoutedEventArgs e) => ToggleRoll();

    private void OnLockClicked(object sender, RoutedEventArgs e)
    {
        _record.IsLocked = !_record.IsLocked;
        ApplyLockState();
        _onPersist();
    }

    // -----------------------------------------------------------------------------------------
    // Hamburger menu
    // -----------------------------------------------------------------------------------------

    private void OnHamburgerClicked(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var openFolder = new System.Windows.Controls.MenuItem { Header = "Open folder" };
        openFolder.Click += (_, _) => OpenAssociatedFolder();
        menu.Items.Add(openFolder);

        var refresh = new System.Windows.Controls.MenuItem { Header = "Refresh" };
        refresh.Click += (_, _) => RefreshPortalItems();
        menu.Items.Add(refresh);

        var changeFolder = new System.Windows.Controls.MenuItem { Header = "Change folder…" };
        changeFolder.Click += (_, _) => OnChangeFolder();
        menu.Items.Add(changeFolder);

        // Sort by submenu. The chosen mode persists on the per-record Portal config and the
        // items list rebuilds immediately.
        var sortBy = new System.Windows.Controls.MenuItem { Header = "Sort by" };
        var sortModes = new[] { "Name", "Modified", "Type" };
        var currentSort = GetCurrentSortMode();
        foreach (var mode in sortModes)
        {
            var captured = mode;
            var item = new System.Windows.Controls.MenuItem
            {
                Header = mode,
                IsCheckable = true,
                IsChecked = string.Equals(currentSort, captured, StringComparison.OrdinalIgnoreCase),
                StaysOpenOnClick = false,
            };
            item.Click += (_, _) => SetSortMode(captured);
            sortBy.Items.Add(item);
        }
        menu.Items.Add(sortBy);

        // Reset zoom: visible only when this wormhole carries a per-record override
        // (Ctrl+Wheel was used at some point). Clearing it falls back through the chain
        // to the app-wide default + DesktopIconSize, which the user can read off the
        // Settings → Wormholes panel.
        if (_record.IconSizePx > 0)
        {
            var resetZoom = new System.Windows.Controls.MenuItem { Header = "Reset zoom to default" };
            resetZoom.Click += (_, _) =>
            {
                _record.IconSizePx = 0;
                _onPersist();
                RebuildItems();
            };
            menu.Items.Add(resetZoom);
        }

        menu.Items.Add(new System.Windows.Controls.Separator());

        var rename = new System.Windows.Controls.MenuItem { Header = "Rename" };
        rename.Click += (_, _) => BeginInlineRename();
        menu.Items.Add(rename);

        var hide = new System.Windows.Controls.MenuItem { Header = "Hide this wormhole" };
        hide.Click += (_, _) =>
        {
            _record.IsHidden = true;
            _onPersist();
            CloseFromManager();
        };
        menu.Items.Add(hide);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var delete = new System.Windows.Controls.MenuItem { Header = "Delete wormhole…" };
        delete.Click += (_, _) =>
        {
            var confirm = MessageBox.Show(OwnerForDialogs(),
                $"Delete wormhole \"{_record.Title}\"?\n\nThe source folder on disk is NOT touched.",
                "AresToys",
                MessageBoxButton.OKCancel, MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (confirm != MessageBoxResult.OK) return;

            // Second prompt: if the source folder is on disk AND empty, offer to remove it.
            // Never auto-delete a folder with content — that's the user's data and the first
            // dialog promised we wouldn't touch it. Empty folders are a common leftover after a
            // "create wormhole, move stuff elsewhere, delete wormhole" workflow.
            if (_record.Portal is { SourcePath: { Length: > 0 } src }
                && Directory.Exists(src) && IsEmptyDirectory(src))
            {
                var alsoDelete = MessageBox.Show(OwnerForDialogs(),
                    $"The source folder is empty:\n\n  {src}\n\nDelete it from disk as well?",
                    "AresToys", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (alsoDelete == MessageBoxResult.Yes)
                {
                    try { Directory.Delete(src); }
                    catch (Exception ex)
                    {
                        // Best-effort: a permission denied / locked-by-AV failure here shouldn't
                        // block the wormhole record deletion below. Inform the user so they can
                        // clean up by hand if they care.
                        MessageBox.Show(OwnerForDialogs(),
                            "The wormhole was deleted, but the empty folder couldn't be removed:\n" + ex.Message,
                            "AresToys", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            DeleteRequested?.Invoke(this, _record.Id);
        };
        menu.Items.Add(delete);

        menu.PlacementTarget = (FrameworkElement)sender;
        menu.IsOpen = true;
    }

    private void OpenAssociatedFolder()
    {
        var folder = _record.Portal?.SourcePath;
        if (string.IsNullOrWhiteSpace(folder))
        {
            MessageBox.Show(OwnerForDialogs(),"No folder is associated with this wormhole.", "AresToys",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(OwnerForDialogs(),"Couldn't open the folder:\n" + ex.Message,
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Toggle the wormhole into the "source folder unavailable" state — hides the
    /// items + empty-state hint, surfaces the dedicated panel with the missing path and a
    /// re-link button. Called from <see cref="RefreshPortalItems"/> when Directory.Exists
    /// returns false. The watcher keeps the old path; if the folder reappears (USB plugged
    /// back in, network share resumes) the next refresh tick clears this state automatically.</summary>
    private void ShowSourceMissingState(string missingPath)
    {
        SourceMissingPath.Text = string.IsNullOrEmpty(missingPath) ? "(no path set)" : missingPath;
        SourceMissingPanel.Visibility = Visibility.Visible;
        EmptyStateHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>Re-link button on the source-missing error panel. Reuses the same folder-pick
    /// flow as the hamburger "Change folder…" entry — keeps a single place where the
    /// SourcePath mutation + persist + refresh live.</summary>
    private void OnRelinkSourceClicked(object sender, RoutedEventArgs e) => OnChangeFolder();

    private void OnChangeFolder()
    {
        if (_record.Portal is null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pick the new source folder for this wormhole",
            InitialDirectory = Directory.Exists(_record.Portal.SourcePath)
                ? _record.Portal.SourcePath
                : Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != true) return;
        _record.Portal.SourcePath = dlg.FolderName;
        _onPersist();
        // The manager listens for an explicit event to re-Start its watcher on the new path.
        // For the MVP we just refresh once and let the user toggle Refresh manually; the
        // watcher will reattach correctly on the next app launch. (Live watcher swap = polish.)
        RefreshPortalItems();
    }

    // -----------------------------------------------------------------------------------------
    // Inline rename
    // -----------------------------------------------------------------------------------------

    private void BeginInlineRename()
    {
        TitleEditor.Text = _record.Title;
        TitleEditor.Visibility = Visibility.Visible;
        TitleText.Visibility = Visibility.Collapsed;
        TitleEditor.Focus();
        TitleEditor.SelectAll();
    }

    private void CommitInlineRename()
    {
        var next = (TitleEditor.Text ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(next) && next != _record.Title)
        {
            _record.Title = next;
            DataContext = null;
            DataContext = _record;
            _onPersist();
        }
        EndInlineRename();
    }

    private void EndInlineRename()
    {
        TitleEditor.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
    }

    private void OnTitleEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitInlineRename(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndInlineRename(); e.Handled = true; }
    }

    private void OnTitleEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (TitleEditor.Visibility == Visibility.Visible) CommitInlineRename();
    }

    // -----------------------------------------------------------------------------------------
    // Drop handling — branches on Data vs Portal kind
    // -----------------------------------------------------------------------------------------

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Effects = ResolveDropEffect(e);
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ResolveDropEffect(e);
        e.Handled = true;
    }

    private DragDropEffects ResolveDropEffect(DragEventArgs e)
    {
        if (_record.IsLocked) return DragDropEffects.None;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.None;
        // Data wormhole always reads as Copy — the original source file is never touched, only
        // a .lnk is materialised. Cursor reflects this so the user can't confuse it with a
        // real file move.
        // Explorer's standard drag semantics:
        //   - Ctrl  → force Copy
        //   - Shift → force Move
        //   - no modifier → Move if source and dest are on the same volume, Copy otherwise
        // The cursor previews the action of the first dragged path; per-file decisions in
        // DropOntoPortal apply the same rule per item, so a mixed-volume batch behaves
        // correctly even if the cursor only shows one of the two effects.
        var ctrl  = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
        var shift = (e.KeyStates & DragDropKeyStates.ShiftKey)   == DragDropKeyStates.ShiftKey;
        if (ctrl)  return DragDropEffects.Copy;
        if (shift) return DragDropEffects.Move;

        var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (paths is null || paths.Length == 0) return DragDropEffects.Copy;
        return IsSameVolume(paths[0], _record.Portal?.SourcePath)
            ? DragDropEffects.Move
            : DragDropEffects.Copy;
    }

    /// <summary>True if <paramref name="source"/> and <paramref name="destFolder"/> share the
    /// same path root — drive letter for local paths, UNC server+share for network paths. Used
    /// to decide the default Move-vs-Copy behaviour on a Portal drop (Explorer semantics —
    /// same-volume drag defaults to Move, cross-volume defaults to Copy).</summary>
    private static bool IsSameVolume(string source, string? destFolder)
    {
        if (string.IsNullOrWhiteSpace(destFolder)) return false;
        try
        {
            var srcRoot = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(source));
            var dstRoot = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(destFolder));
            return !string.IsNullOrEmpty(srcRoot)
                && string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Defensive — GetFullPath throws on bad paths (illegal chars, too-long). Fall back
            // to Copy on uncertainty so we never silently move a file we couldn't reason about.
            return false;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_record.IsLocked) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (paths is null || paths.Length == 0) return;
        e.Handled = true;
        DropOntoPortal(paths, e);
    }

    private void DropOntoPortal(string[] paths, DragEventArgs e)
    {
        if (_record.Portal is null || string.IsNullOrWhiteSpace(_record.Portal.SourcePath)) return;
        var dest = _record.Portal.SourcePath;
        if (!Directory.Exists(dest))
        {
            MessageBox.Show(OwnerForDialogs(),"The Portal source folder isn't currently available.",
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Per-file Move-vs-Copy decision matches the cursor preview rule in ResolveDropEffect:
        // Ctrl forces Copy, Shift forces Move, otherwise same-volume drags Move and cross-volume
        // drags Copy. Evaluated per item so a mixed-volume batch (e.g. user drags 2 files from
        // C:\ and 1 from D:\ at the same time) gets the right behaviour for each.
        var ctrl  = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
        var shift = (e.KeyStates & DragDropKeyStates.ShiftKey)   == DragDropKeyStates.ShiftKey;
        bool ShouldMove(string source) =>
            !ctrl && (shift || IsSameVolume(source, dest));

        var errors = new List<string>();
        foreach (var source in paths)
        {
            try
            {
                // Skip a self-drop (dragging an item from the same source folder back in) —
                // the operation would either be a same-path Move (NOP) or a Copy that would
                // create "name (2)" pointlessly.
                var srcDir = Directory.Exists(source)
                    ? Path.GetDirectoryName(source.TrimEnd(Path.DirectorySeparatorChar))
                    : Path.GetDirectoryName(source);
                if (string.Equals(srcDir, dest, StringComparison.OrdinalIgnoreCase)) continue;

                // Block any drop that would put a folder inside itself or one of its descendants
                // (recursion-into-self). Catches: dropping the wormhole's own source folder onto
                // the wormhole (would create C:\Foo\Foo), or dropping any parent folder onto a
                // Portal whose source is a descendant (would infinite-loop Directory.Move /
                // CopyDirectoryRecursive). Only relevant when source is a directory — file drops
                // can't recurse.
                if (IsDestinationInsideSource(source, dest))
                {
                    errors.Add($"{Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar))}: can't drop a folder into itself or one of its subfolders.");
                    continue;
                }

                var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var targetPath = Path.Combine(dest, name);
                targetPath = UniqueTargetPath(targetPath);

                var move = ShouldMove(source);
                if (Directory.Exists(source))
                {
                    if (move) Directory.Move(source, targetPath);
                    else CopyDirectoryRecursive(source, targetPath);
                }
                else
                {
                    if (move) File.Move(source, targetPath, overwrite: false);
                    else File.Copy(source, targetPath, overwrite: false);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
            }
        }
        // No manual refresh — the FolderWatcher will pick the changes up after its 300 ms tick
        // and the manager calls RefreshPortalItems(). Only show errors here.
        if (errors.Count > 0)
            MessageBox.Show(OwnerForDialogs(),"Some items couldn't be moved/copied:\n\n" + string.Join("\n", errors),
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>True if <paramref name="dest"/> is the same path as <paramref name="source"/>
    /// or a descendant directory of it. Used to block recursive moves / copies that would
    /// produce <c>C:\Foo\Foo</c> or trigger an infinite copy loop when the user drops a folder
    /// onto one of its own subtrees. Source-is-file → always false (files can't recurse).</summary>
    private static bool IsDestinationInsideSource(string source, string dest)
    {
        if (!Directory.Exists(source)) return false;
        try
        {
            var sourceFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destFull   = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(destFull, sourceFull, StringComparison.OrdinalIgnoreCase)
                || destFull.StartsWith(sourceFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Defensive: GetFullPath can throw on illegal characters / too-long paths. Treat as
            // "not same" (= allow the drop) — File.Move / Directory.Move will surface their own
            // exception to the per-item error list if it really is a bad path.
            return false;
        }
    }

    private static string UniqueTargetPath(string candidate)
    {
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var dir = Path.GetDirectoryName(candidate)!;
        var stem = Path.GetFileNameWithoutExtension(candidate);
        var ext = Path.GetExtension(candidate);
        for (var n = 2; n < 1000; n++)
        {
            var next = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(next) && !Directory.Exists(next)) return next;
        }
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    private static void CopyDirectoryRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
        foreach (var sub in Directory.EnumerateDirectories(source))
            CopyDirectoryRecursive(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    // -----------------------------------------------------------------------------------------
    // Sort by — hamburger menu + persistence
    // -----------------------------------------------------------------------------------------

    private string GetCurrentSortMode() => _record.Portal?.SortMode ?? "Name";

    private void SetSortMode(string mode)
    {
        if (string.IsNullOrEmpty(mode) || _record.Portal is null) return;
        _record.Portal.SortMode = mode;
        _onPersist();
        RefreshPortalItems();
    }

    /// <summary>Order Portal entries: folders first (alphabetical), then files by chosen sort
    /// mode. Folders-first mirrors Stardock and Explorer conventions — the user expects to see
    /// directories grouped at the top regardless of the file-side sort.</summary>
    private static IEnumerable<string> SortPortalEntries(IEnumerable<string> entries, string sortMode)
    {
        var materialised = entries as IList<string> ?? entries.ToList();
        var folders = materialised.Where(Directory.Exists)
            .OrderBy(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                     StringComparer.CurrentCultureIgnoreCase);
        var files = materialised.Where(p => !Directory.Exists(p));
        files = sortMode switch
        {
            "Modified" => files.OrderByDescending(p => SafeLastWriteTime(p)),
            "Type"     => files.OrderBy(p => Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)
                                .ThenBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase),
            _          => files.OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase),
        };
        return folders.Concat(files);
    }

    private static DateTime SafeLastWriteTime(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>True if the directory exists and has zero entries (files OR subdirectories).
    /// EnumerateFileSystemEntries is cheaper than GetFiles+GetDirectories — it streams the first
    /// entry and we early-out. Any IO failure is treated as "not empty" so the delete-folder
    /// prompt errs on the side of preserving the folder.</summary>
    private static bool IsEmptyDirectory(string path)
    {
        try { return !Directory.EnumerateFileSystemEntries(path).Any(); }
        catch { return false; }
    }

    private void UpdateEmptyStateVisibility()
    {
        EmptyStateHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not WormholeItemViewModel vm) return;
        OpenItem(vm);
    }

    // -----------------------------------------------------------------------------------------
    // Per-item context menu — always the Windows shell native menu (same one Explorer shows).
    // The custom AresToys curated menu (Open / Open file location / Copy path / Move to /
    // Rename / Delete) was removed in favour of the native one: Explorer's menu already covers
    // every entry we had AND every third-party verb the user has installed (Open with…, Send
    // to, 7-Zip, Git GUI, etc.), and an "Open file location" entry inside a window that *is*
    // showing the folder content is redundant.
    // -----------------------------------------------------------------------------------------

    private void OnItemRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not WormholeItemViewModel vm) return;
        var screen = PointToScreen(e.GetPosition(this));
        AresToys.App.Services.Shell.ShellContextMenu.Show(vm.AbsolutePath, this, screen);
        e.Handled = true;
    }

    /// <summary>Launch the item via shell verb — the default verb (Open) for the file type.
    /// Wired to the double-click handler on each tile; never called from the right-click flow
    /// any more (which goes straight to the shell context menu and lets the user pick Open
    /// themselves).</summary>
    private void OpenItem(WormholeItemViewModel vm)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = vm.AbsolutePath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(OwnerForDialogs(),"Couldn't open the item:\n" + ex.Message,
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Roll / lock state
    // -----------------------------------------------------------------------------------------

    private void ToggleRoll()
    {
        _record.IsRolled = !_record.IsRolled;
        ApplyRollState();
        _onPersist();
    }

    private void ApplyRollState()
    {
        if (_record.IsRolled)
        {
            ContentArea.Visibility = Visibility.Collapsed;
            // Window.MinHeight (80 in the XAML) silently clamps the rolled Height we set
            // below — without dropping MinHeight first, the rolled wormhole stayed 80 px tall
            // and showed ~46 px of body backdrop strip under the header. Drop it to 0 while
            // rolled, restore on unroll.
            //
            // Target collapsed size: 32 (header) + 1+1 borders + ~14 strip ≈ 48 px. The thin
            // strip is intentional — user-requested "circa il 50% dell'header" — to keep the
            // wormhole feeling like an object that can be unrolled, not just the title bar.
            MinHeight = 0;
            Height = 48;
            ResizeMode = ResizeMode.NoResize;
            ChevronGlyph.Text = ChevronDownGlyph;
        }
        else
        {
            ContentArea.Visibility = Visibility.Visible;
            // Order matters: set Height FIRST while MinHeight is still 0 (from the previous
            // rolled state). Setting MinHeight=80 first would leave a pending "clamp Height up
            // to 80" pass that interleaves badly with our explicit Height=UnrolledHeight
            // assignment — the user reported the wormhole staying ~80 px tall instead of
            // restoring to the saved height. Once Height matches UnrolledHeight (>= 80) we can
            // safely raise MinHeight back to 80 without re-clamping.
            Height = _record.Geometry.UnrolledHeight;
            MinHeight = 80;
            ResizeMode = _record.IsLocked ? ResizeMode.NoResize : ResizeMode.CanResize;
            ChevronGlyph.Text = ChevronUpGlyph;
        }
    }

    private void ApplyLockState()
    {
        LockGlyph.Text = _record.IsLocked ? LockClosedGlyph : LockOpenGlyph;
        if (!_record.IsRolled)
            ResizeMode = _record.IsLocked ? ResizeMode.NoResize : ResizeMode.CanResize;
    }
}
